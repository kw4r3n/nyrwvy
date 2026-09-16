namespace NyamNyam.Core.Tests

open NyamNyam.Core
open Xunit

module AgentParameterTypeMigrationTests =
    type private Fixture =
        { Snapshot: Snapshot
          RequestType: SemanticType
          EmailField: SemanticField
          Classify: SemanticFunction
          ClassifyInput: SemanticParameter
          Process: SemanticFunction
          ProcessInput: SemanticParameter
          Unrelated: SemanticFunction }

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool

    let private snapshotId name =
        StableId.createForTesting<SnapshotTag> ("snapshot-m5-migration-" + name)

    let private typeId name =
        StableId.createForTesting<TypeTag> ("type-m5-migration-" + name)

    let private fieldId name =
        StableId.createForTesting<FieldTag> ("field-m5-migration-" + name)

    let private functionId name =
        StableId.createForTesting<FunctionTag> ("function-m5-migration-" + name)

    let private parameterId name =
        StableId.createForTesting<ParameterTag> ("parameter-m5-migration-" + name)

    let private nodeId name =
        StableId.createForTesting<GraphNodeTag> ("node-m5-migration-" + name)

    let private createFixture () =
        let emailField: SemanticField =
            { Id = fieldId "request-email"
              DisplayName = "email"
              Type = stringType }

        let requestType: SemanticType =
            { Id = typeId "request"
              DisplayName = "ValidationRequest"
              Kind = SemanticTypeKind.Product [ emailField ] }

        let classifyInput: SemanticParameter =
            { Id = parameterId "classify-input"
              DisplayName = "input"
              Type = stringType }

        let classifyInputNode = nodeId "classify-input"
        let atNode = nodeId "classify-at"
        let classifyResult = nodeId "classify-result"

        let classify: SemanticFunction =
            { Id = functionId "classify"
              DisplayName = "classify"
              Parameters = [ classifyInput ]
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = classifyInputNode
                        Kind = SemanticNodeKind.ParameterReference classifyInput.Id }
                      { Id = atNode
                        Kind = SemanticNodeKind.StringLiteral "@" }
                      { Id = classifyResult
                        Kind =
                            SemanticNodeKind.StringContains(
                                classifyInputNode,
                                atNode
                            ) } ]
                  ResultNodeId = classifyResult } }

        let processInput: SemanticParameter =
            { Id = parameterId "process-input"
              DisplayName = "input"
              Type = stringType }

        let processInputNode = nodeId "process-input"
        let processCall = nodeId "process-call"

        let processFunction: SemanticFunction =
            { Id = functionId "process"
              DisplayName = "process"
              Parameters = [ processInput ]
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = processInputNode
                        Kind = SemanticNodeKind.ParameterReference processInput.Id }
                      { Id = processCall
                        Kind =
                            SemanticNodeKind.DirectCall(
                                classify.Id,
                                [ { TargetParameterId = classifyInput.Id
                                    ValueNodeId = processInputNode } ]
                            ) } ]
                  ResultNodeId = processCall } }

        let unrelatedResult = nodeId "unrelated-result"
        let unrelated: SemanticFunction =
            { Id = functionId "unrelated"
              DisplayName = "unrelated"
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = unrelatedResult
                        Kind = SemanticNodeKind.BoolLiteral true } ]
                  ResultNodeId = unrelatedResult } }

        let snapshot =
            { Snapshot.empty (snapshotId "initial") with
                Types = [ requestType ]
                Functions = [ classify; processFunction; unrelated ] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        { Snapshot = snapshot
          RequestType = requestType
          EmailField = emailField
          Classify = classify
          ClassifyInput = classifyInput
          Process = processFunction
          ProcessInput = processInput
          Unrelated = unrelated }

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private targetReplacement fixture =
        ExpressionRecipe.StringContains(
            ExpressionRecipe.FieldAccess(
                ExpressionRecipe.Parameter fixture.ClassifyInput.Id,
                fixture.EmailField.Id
            ),
            ExpressionRecipe.StringLiteral "@"
        )

    let private callerReplacement fixture =
        ExpressionRecipe.DirectCall(
            fixture.Classify.Id,
            [ fixture.ClassifyInput.Id,
              ExpressionRecipe.ProductConstruct(
                  fixture.RequestType.Id,
                  [ fixture.EmailField.Id,
                    ExpressionRecipe.Parameter fixture.ProcessInput.Id ]
              ) ]
        )

    let private request fixture callerRewrites: AgentParameterTypeMigrationRequest =
        { ExpectedBaseSnapshotId = fixture.Snapshot.Id
          TargetFunctionId = fixture.Classify.Id
          TargetParameterId = fixture.ClassifyInput.Id
          NewParameterType = TypeRef.Named fixture.RequestType.Id
          TargetReplacement = targetReplacement fixture
          CallerRewrites = callerRewrites }

    let private validRequest fixture =
        request
            fixture
            [ { CallerFunctionId = fixture.Process.Id
                Replacement = callerReplacement fixture } ]

    let private simulate ws migrationRequest =
        match AgentParameterTypeMigrationWorkflow.simulate ws migrationRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected migration simulation failure: %A" error

    let private assertRejectedCode expected = function
        | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
            when diagnostics |> List.exists (fun diagnostic -> diagnostic.Code = expected) ->
            ()
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``parameter type migration preserves identities and atomically rewrites target and caller`` () =
        let fixture = createFixture ()
        let ws = workspace fixture.Snapshot
        let proposal = validRequest fixture |> simulate ws
        let review = AgentParameterTypeMigrationProposal.review proposal

        Assert.Equal(fixture.Classify.Id, review.ResultingChange.ParameterChange.FunctionId)
        Assert.Equal(fixture.ClassifyInput.Id, review.ResultingChange.ParameterChange.ParameterId)
        Assert.Equal(stringType, review.ResultingChange.ParameterChange.OldType)
        Assert.Equal(TypeRef.Named fixture.RequestType.Id, review.ResultingChange.ParameterChange.NewType)
        Assert.Single(review.ResultingChange.AffectedCallSites) |> ignore
        Assert.Equal(2, review.ResultingChange.BodyRewrites.Length)
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)

        match AgentParameterTypeMigrationWorkflow.commit ws proposal with
        | AgentParameterTypeMigrationCommitResult.Rejected diagnostics ->
            failwithf "Migration commit failed: %A" diagnostics
        | AgentParameterTypeMigrationCommitResult.Committed(next, publication) ->
            let published = AgentWorkspace.snapshotForStructuralEditing next
            Assert.Empty(SnapshotValidator.validate published)
            Assert.Equal(review.ResultingChange, publication.Diff)
            Assert.Equal(fixture.Snapshot.Functions.Length, published.Functions.Length)

            let classify =
                published.Functions
                |> List.find (fun fn -> fn.Id = fixture.Classify.Id)
            Assert.Equal(fixture.ClassifyInput.Id, classify.Parameters.Head.Id)
            Assert.Equal(TypeRef.Named fixture.RequestType.Id, classify.Parameters.Head.Type)

            let processFunction =
                published.Functions
                |> List.find (fun fn -> fn.Id = fixture.Process.Id)
            Assert.Equal(fixture.ProcessInput.Id, processFunction.Parameters.Head.Id)
            Assert.Equal(stringType, processFunction.Parameters.Head.Type)

            let call =
                ValueGraph.collectNodes processFunction.Body
                |> List.find (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.DirectCall(functionId, _) ->
                        functionId = fixture.Classify.Id
                    | _ -> false)
            match call.Kind with
            | SemanticNodeKind.DirectCall(_, [ argument ]) ->
                Assert.Equal(fixture.ClassifyInput.Id, argument.TargetParameterId)
                let argumentNode =
                    ValueGraph.collectNodes processFunction.Body
                    |> List.find (fun node -> node.Id = argument.ValueNodeId)
                match argumentNode.Kind with
                | SemanticNodeKind.ProductConstruct(typeId, _) ->
                    Assert.Equal(fixture.RequestType.Id, typeId)
                | other -> failwithf "Expected request construction, got %A" other
            | other -> failwithf "Unexpected migrated call: %A" other

            let unrelated =
                published.Functions
                |> List.find (fun fn -> fn.Id = fixture.Unrelated.Id)
            Assert.Equal(fixture.Unrelated, unrelated)

    [<Fact>]
    let ``migration rejects missing and incompatible caller rewrites before proposal acceptance`` () =
        let fixture = createFixture ()
        let ws = workspace fixture.Snapshot

        request fixture []
        |> AgentParameterTypeMigrationWorkflow.simulate ws
        |> assertRejectedCode "ParameterTypeMigrationCallerRewriteMismatch"

        request
            fixture
            [ { CallerFunctionId = fixture.Process.Id
                Replacement =
                    ExpressionRecipe.DirectCall(
                        fixture.Classify.Id,
                        [ fixture.ClassifyInput.Id,
                          ExpressionRecipe.Parameter fixture.ProcessInput.Id ]
                    ) } ]
        |> AgentParameterTypeMigrationWorkflow.simulate ws
        |> assertRejectedCode "ParameterTypeMigrationCallerRewriteInvalid"

    [<Fact>]
    let ``reviewed migration becomes stale after another publication`` () =
        let fixture = createFixture ()
        let ws = workspace fixture.Snapshot
        let proposal = validRequest fixture |> simulate ws

        let advanced =
            match
                AgentWorkflow.simulate
                    ws
                    [ AgentOperationIntent.RenameDisplayName(
                        AgentRenameTarget.Function fixture.Unrelated.Id,
                        "unrelated_renamed"
                      ) ]
            with
            | Error error -> failwithf "Unexpected rename simulation failure: %A" error
            | Ok renameProposal ->
                match AgentWorkflow.commit ws renameProposal with
                | AgentCommitResult.Rejected diagnostics ->
                    failwithf "Unexpected rename commit failure: %A" diagnostics
                | AgentCommitResult.Committed(next, _) -> next

        match AgentParameterTypeMigrationWorkflow.commit advanced proposal with
        | AgentParameterTypeMigrationCommitResult.Committed _ ->
            failwith "Expected the migration proposal to be stale."
        | AgentParameterTypeMigrationCommitResult.Rejected diagnostics ->
            Assert.Contains(
                diagnostics,
                fun diagnostic -> diagnostic.Code = "StaleBaseSnapshot"
            )
