namespace NyamNyam.Core.Tests

open NyamNyam.Core
open Xunit

module AgentPureExpressionDirectCallTests =
    type private Fixture =
        { Snapshot: Snapshot
          CallerId: FunctionId
          CalleeId: FunctionId
          EffectfulCalleeId: FunctionId
          CallEffectId: EffectId
          TextParameterId: ParameterId
          CountParameterId: ParameterId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-pure-call-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-pure-call-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-pure-call-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-pure-call-" + name)
    let private effectId name = StableId.createForTesting<EffectTag> ("effect-pure-call-" + name)

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64

    let private functionWithBody id name parameters resultType resultNodeId kind: SemanticFunction =
        { Id = id
          DisplayName = name
          Parameters = parameters
          ResultType = resultType
          RequiredEffects = []
          Body =
            { Nodes = [ { Id = resultNodeId; Kind = kind } ]
              ResultNodeId = resultNodeId } }

    let private createFixture (): Fixture =
        let textParameter: SemanticParameter =
            { Id = parameterId "text"
              DisplayName = "text"
              Type = stringType }
        let countParameter: SemanticParameter =
            { Id = parameterId "count"
              DisplayName = "count"
              Type = int64Type }

        let caller =
            functionWithBody
                (functionId "caller")
                "caller"
                []
                stringType
                (nodeId "caller-result")
                (SemanticNodeKind.StringLiteral "old")

        let callee =
            functionWithBody
                (functionId "callee")
                "callee"
                [ textParameter; countParameter ]
                stringType
                (nodeId "callee-result")
                (SemanticNodeKind.StringLiteral "result")

        let callEffect: SemanticEffect =
            { Id = effectId "required"
              DisplayName = "required" }

        let effectfulCallee =
            { functionWithBody
                (functionId "effectful")
                "effectful"
                []
                stringType
                (nodeId "effectful-result")
                (SemanticNodeKind.StringLiteral "effectful") with
                RequiredEffects = [ callEffect.Id ] }

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = [ callEffect ]
              Capabilities = []
              Functions = [ caller; callee; effectfulCallee ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        { Snapshot = snapshot
          CallerId = caller.Id
          CalleeId = callee.Id
          EffectfulCalleeId = effectfulCallee.Id
          CallEffectId = callEffect.Id
          TextParameterId = textParameter.Id
          CountParameterId = countParameter.Id }

    let private withCallerEffects effectIds (fixture: Fixture): Fixture =
        let snapshot =
            { fixture.Snapshot with
                Id = snapshotId "caller-effects"
                Functions =
                    fixture.Snapshot.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.Id = fixture.CallerId then
                            { functionEntity with RequiredEffects = effectIds }
                        else
                            functionEntity) }

        Assert.Empty(SnapshotValidator.validate snapshot)
        { fixture with Snapshot = snapshot }

    let private createWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request (fixture: Fixture) replacement: AgentReplaceFunctionBodyWithPureExpressionRecipe =
        let caller = fixture.Snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = fixture.CallerId)
        { TargetFunctionId = fixture.CallerId
          ExpectedBaseSnapshotId = fixture.Snapshot.Id
          ExpectedCurrentResultNodeId = caller.Body.ResultNodeId
          Replacement = replacement }

    let private assertRejectedCode expected = function
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``pure DirectCall materializes complete arguments in callee parameter order`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let recipe =
            PureExpressionRecipe.DirectCall(
                fixture.CalleeId,
                [ fixture.CountParameterId, PureExpressionRecipe.Int64Literal 2L
                  fixture.TextParameterId, PureExpressionRecipe.StringLiteral "hello" ]
            )

        let proposal =
            match AgentPureExpressionStructuralWorkflow.simulate workspace (request fixture recipe) with
            | Ok proposal -> proposal
            | Error error -> failwithf "Unexpected DirectCall simulation failure: %A" error

        let review = AgentPureExpressionStructuralChangeProposal.review proposal
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)

        let addedNodes = review.ResultingChange.AddedNodes
        Assert.Equal<string list>([ "StringLiteral"; "Int64Literal"; "DirectCall" ], addedNodes |> List.map (fun node -> node.Role))
        Assert.Equal(Some fixture.CalleeId, (List.last addedNodes).FunctionId)

    [<Fact>]
    let ``DirectCall accepts effects already declared by caller`` () =
        let fixture = createFixture ()
        let fixture = fixture |> withCallerEffects [ fixture.CallEffectId ]
        let workspace = createWorkspace fixture.Snapshot
        let recipe = PureExpressionRecipe.DirectCall(fixture.EffectfulCalleeId, [])

        let proposal =
            match AgentPureExpressionStructuralWorkflow.simulate workspace (request fixture recipe) with
            | Ok proposal -> proposal
            | Error error -> failwithf "Unexpected effectful DirectCall simulation failure: %A" error

        let review = AgentPureExpressionStructuralChangeProposal.review proposal
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        let callNode = review.ResultingChange.AddedNodes |> List.exactlyOne
        Assert.Equal("DirectCall", callNode.Role)
        Assert.Equal(Some fixture.EffectfulCalleeId, callNode.FunctionId)

    [<Fact>]
    let ``DirectCall rejects effects missing from caller declaration`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        request fixture (PureExpressionRecipe.DirectCall(fixture.EffectfulCalleeId, []))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "ExpressionCallMissingEffectRequirement"

    [<Fact>]
    let ``pure DirectCall preflight rejects incomplete and invalid calls`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let missingCallee = StableId.createForTesting<FunctionTag> "function-pure-call-missing"
        let foreignParameter = StableId.createForTesting<ParameterTag> "parameter-pure-call-foreign"

        let cases =
            [ PureExpressionRecipe.DirectCall(missingCallee, []), "PureExpressionCallTargetNotFound"
              PureExpressionRecipe.DirectCall(
                  fixture.CalleeId,
                  [ fixture.TextParameterId, PureExpressionRecipe.StringLiteral "a"
                    fixture.TextParameterId, PureExpressionRecipe.StringLiteral "b"
                    fixture.CountParameterId, PureExpressionRecipe.Int64Literal 1L ]
              ),
              "PureExpressionDuplicateCallArgument"
              PureExpressionRecipe.DirectCall(
                  fixture.CalleeId,
                  [ foreignParameter, PureExpressionRecipe.StringLiteral "a"
                    fixture.TextParameterId, PureExpressionRecipe.StringLiteral "b"
                    fixture.CountParameterId, PureExpressionRecipe.Int64Literal 1L ]
              ),
              "PureExpressionCallArgumentTargetNotFound"
              PureExpressionRecipe.DirectCall(
                  fixture.CalleeId,
                  [ fixture.TextParameterId, PureExpressionRecipe.StringLiteral "a" ]
              ),
              "PureExpressionCallArgumentMissing"
              PureExpressionRecipe.DirectCall(
                  fixture.CalleeId,
                  [ fixture.TextParameterId, PureExpressionRecipe.Int64Literal 1L
                    fixture.CountParameterId, PureExpressionRecipe.Int64Literal 2L ]
              ),
              "PureExpressionCallArgumentTypeMismatch" ]

        for recipe, expectedCode in cases do
            request fixture recipe
            |> AgentPureExpressionStructuralWorkflow.simulate workspace
            |> assertRejectedCode expectedCode
