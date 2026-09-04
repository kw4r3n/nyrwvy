namespace NyamNyam.Core.Tests

open NyamNyam.Core
open Xunit

module RecipeBoundsTests =
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let private createFixture () =
        let resultNodeId = StableId.createForTesting<GraphNodeTag> "node-recipe-bounds-result"
        let target: SemanticFunction =
            { Id = StableId.createForTesting<FunctionTag> "function-recipe-bounds-target"
              DisplayName = "target"
              Parameters = []
              ResultType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ float64Type ])
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = resultNodeId; Kind = SemanticNodeKind.ListEmpty float64Type } ]
                  ResultNodeId = resultNodeId } }
        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-recipe-bounds"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ target ]
              Obligations = [] }
        let workspace =
            match AgentWorkspace.create snapshot with
            | Ok workspace -> workspace
            | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues
        snapshot, workspace, target

    let rec private deepFloat depth =
        if depth <= 1 then
            PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0)
        else
            PureExpressionRecipe.Float64Add(
                PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0),
                deepFloat (depth - 1)
            )

    let private listRecipe items = PureExpressionRecipe.ListConstruct(float64Type, items)

    let private simulate snapshot workspace target replacement =
        AgentPureExpressionStructuralWorkflow.simulate
            workspace
            { TargetFunctionId = target.Id
              ExpectedBaseSnapshotId = snapshot.Id
              ExpectedCurrentResultNodeId = target.Body.ResultNodeId
              Replacement = replacement }

    let private assertRejectedCode expected = function
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``pure recipe traversal enforces depth and node budgets at their boundaries`` () =
        let snapshot, workspace, target = createFixture ()
        let literal () = PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0)

        match simulate snapshot workspace target (listRecipe [ deepFloat 31 ]) with
        | Ok _ -> ()
        | error -> failwithf "Depth 32 should pass: %A" error

        simulate snapshot workspace target (listRecipe [ deepFloat 32 ])
        |> assertRejectedCode "StructuralRecipeDepthLimitExceeded"

        match simulate snapshot workspace target (listRecipe (List.init 63 (fun _ -> literal ()))) with
        | Ok _ -> ()
        | error -> failwithf "Node count 64 should pass: %A" error

        simulate snapshot workspace target (listRecipe (List.init 64 (fun _ -> literal ())))
        |> assertRejectedCode "StructuralRecipeNodeLimitExceeded"
