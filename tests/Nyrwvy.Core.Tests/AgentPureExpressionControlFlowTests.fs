namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module AgentPureExpressionControlFlowTests =
    type private Fixture =
        { Snapshot: Snapshot
          TargetId: FunctionId
          ResultNodeId: GraphNodeId
          OptionParameterId: ParameterId
          UnionParameterId: ParameterId
          FirstVariantId: VariantId
          SecondVariantId: VariantId
          FirstFieldId: FieldId }

    let private typeId name = StableId.createForTesting<TypeTag> ("type-pure-control-" + name)
    let private fieldId name = StableId.createForTesting<FieldTag> ("field-pure-control-" + name)
    let private variantId name = StableId.createForTesting<VariantTag> ("variant-pure-control-" + name)
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-pure-control-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-pure-control-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-pure-control-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-pure-control-" + name)

    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private optionType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])

    let private createFixture () =
        let choiceTypeId = typeId "choice"
        let firstFieldId = fieldId "first-value"
        let firstVariantId = variantId "first"
        let secondVariantId = variantId "second"
        let choiceType: SemanticType =
            { TypeParameters = []; Id = choiceTypeId
              DisplayName = "choice"
              Kind =
                SemanticTypeKind.Union
                    [ { Id = firstVariantId
                        DisplayName = "first"
                        Fields =
                            [ { Id = firstFieldId
                                DisplayName = "value"
                                Type = int64Type } ] }
                      { Id = secondVariantId
                        DisplayName = "second"
                        Fields = [] } ] }

        let optionParameter: SemanticParameter =
            { Id = parameterId "option"
              DisplayName = "option"
              Type = optionType int64Type }
        let unionParameter: SemanticParameter =
            { Id = parameterId "union"
              DisplayName = "union"
              Type = TypeRef.Named(choiceTypeId, []) }
        let targetId = functionId "target"
        let resultNodeId = nodeId "old-result"
        let target: SemanticFunction =
            { Id = targetId
              DisplayName = "target"
              Parameters = [ optionParameter; unionParameter ]
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = resultNodeId; Kind = SemanticNodeKind.Int64Literal 0L } ]
                  ResultNodeId = resultNodeId } }
        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = [ choiceType ]
              Effects = []
              Capabilities = []
              Functions = [ target ]
              Obligations = [] }
        Assert.Empty(SnapshotValidator.validate snapshot)
        { Snapshot = snapshot
          TargetId = targetId
          ResultNodeId = resultNodeId
          OptionParameterId = optionParameter.Id
          UnionParameterId = unionParameter.Id
          FirstVariantId = firstVariantId
          SecondVariantId = secondVariantId
          FirstFieldId = firstFieldId }

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Unexpected fixture failure: %A" issues

    let private request fixture replacement: AgentReplaceFunctionBodyWithExpressionRecipe =
        { TargetFunctionId = fixture.TargetId
          ExpectedBaseSnapshotId = fixture.Snapshot.Id
          ExpectedCurrentResultNodeId = fixture.ResultNodeId
          Replacement = replacement }

    let private simulate fixture replacement =
        match AgentExpressionStructuralWorkflow.simulate (workspace fixture.Snapshot) (request fixture replacement) with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected simulation failure: %A" error

    let private reviewRoles proposal =
        (AgentExpressionStructuralChangeProposal.review proposal).ResultingChange.AddedNodes
        |> List.map (fun node -> node.Role)

    let private pattern depth slot =
        ExpressionRecipe.PatternReference
            { ScopeDepth = depth
              Slot = slot }

    let private assertRejectedCode expected fixture replacement =
        match AgentExpressionStructuralWorkflow.simulate (workspace fixture.Snapshot) (request fixture replacement) with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``pure control-flow materializes all accepted parent shapes and recursively reviews branch nodes`` () =
        let fixture = createFixture ()

        let conditional =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.BoolLiteral true,
                ExpressionRecipe.Int64Literal 1L,
                ExpressionRecipe.Int64Literal 2L
            )
        Assert.Equal<string list>(
            [ "BoolLiteral"; "Conditional"; "Int64Literal"; "Int64Literal" ],
            conditional |> simulate fixture |> reviewRoles
        )

        let optionMatch =
            ExpressionRecipe.MatchOption(
                ExpressionRecipe.Parameter fixture.OptionParameterId,
                pattern 0 RecipePatternSlot.Value,
                ExpressionRecipe.Int64Literal 0L
            )
        let optionProposal = simulate fixture optionMatch
        Assert.Equal<string list>(
            [ "ParameterReference"; "MatchOption"; "PatternReference"; "Int64Literal" ],
            reviewRoles optionProposal
        )
        let optionPatternSummary =
            (AgentExpressionStructuralChangeProposal.review optionProposal).ResultingChange.AddedNodes
            |> List.find (fun node -> node.Role = "PatternReference")
        Assert.Equal(Some int64Type, optionPatternSummary.Type)

        let resultMatch =
            ExpressionRecipe.MatchResult(
                ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.Int64Literal 3L),
                pattern 0 RecipePatternSlot.Value,
                ExpressionRecipe.Int64Literal 0L
            )
        Assert.Equal<string list>(
            [ "Int64Literal"; "ResultOk"; "MatchResult"; "PatternReference"; "Int64Literal" ],
            resultMatch |> simulate fixture |> reviewRoles
        )

        let unionMatch =
            ExpressionRecipe.MatchUnion(
                ExpressionRecipe.Parameter fixture.UnionParameterId,
                [ [ fixture.SecondVariantId ], ExpressionRecipe.Int64Literal 2L
                  [ fixture.FirstVariantId ], pattern 0 (RecipePatternSlot.Field fixture.FirstFieldId) ]
            )
        Assert.Equal<string list>(
            [ "ParameterReference"; "MatchUnion"; "PatternReference"; "Int64Literal" ],
            unionMatch |> simulate fixture |> reviewRoles
        )

    [<Fact>]
    let ``nested match scope depth crosses the structurally empty Option None frame`` () =
        let fixture = createFixture ()
        let outerValue = pattern 1 RecipePatternSlot.Value
        let recipe =
            ExpressionRecipe.MatchOption(
                ExpressionRecipe.Parameter fixture.OptionParameterId,
                ExpressionRecipe.MatchOption(
                    ExpressionRecipe.OptionNone unitType,
                    outerValue,
                    outerValue
                ),
                ExpressionRecipe.Int64Literal 0L
            )

        let proposal = simulate fixture recipe
        let patternNodes =
            (AgentExpressionStructuralChangeProposal.review proposal).ResultingChange.AddedNodes
            |> List.filter (fun node -> node.Role = "PatternReference")
        Assert.Equal(2, List.length patternNodes)
        Assert.All(patternNodes, fun node -> Assert.Equal(Some int64Type, node.Type))

    [<Fact>]
    let ``pure control-flow preflight rejects invalid lexical references arm sets and branch types`` () =
        let fixture = createFixture ()

        pattern 0 RecipePatternSlot.Value
        |> assertRejectedCode "PureExpressionPatternScopeNotFound" fixture

        ExpressionRecipe.MatchOption(
            ExpressionRecipe.Parameter fixture.OptionParameterId,
            ExpressionRecipe.Int64Literal 1L,
            pattern 0 RecipePatternSlot.Value
        )
        |> assertRejectedCode "PureExpressionPatternSlotNotFound" fixture

        ExpressionRecipe.MatchUnion(
            ExpressionRecipe.Parameter fixture.UnionParameterId,
            [ [ fixture.FirstVariantId ], ExpressionRecipe.Int64Literal 1L
              [ fixture.FirstVariantId ], ExpressionRecipe.Int64Literal 2L ]
        )
        |> assertRejectedCode "PureExpressionMatchArmDuplicate" fixture

        ExpressionRecipe.MatchUnion(
            ExpressionRecipe.Parameter fixture.UnionParameterId,
            [ [ fixture.FirstVariantId ], ExpressionRecipe.Int64Literal 1L ]
        )
        |> assertRejectedCode "PureExpressionMatchArmMismatch" fixture

        ExpressionRecipe.Conditional(
            ExpressionRecipe.BoolLiteral true,
            ExpressionRecipe.Int64Literal 1L,
            ExpressionRecipe.BoolLiteral false
        )
        |> assertRejectedCode "PureExpressionBranchTypeMismatch" fixture
