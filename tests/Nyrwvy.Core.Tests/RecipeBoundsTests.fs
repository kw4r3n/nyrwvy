namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module RecipeBoundsTests =
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let private createFixture () =
        let resultNodeId = StableId.createForTesting<GraphNodeTag> "node-recipe-bounds-result"
        let target: SemanticFunction =
            { Exported = true; Id = StableId.createForTesting<FunctionTag> "function-recipe-bounds-target"
              TypeParameters = []
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
            ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0)
        else
            ExpressionRecipe.Float64Add(
                ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0),
                deepFloat (depth - 1)
            )

    let private listRecipe items = ExpressionRecipe.ListConstruct(float64Type, items)

    let private simulate
        (snapshot: Snapshot)
        (workspace: AgentWorkspace)
        (target: SemanticFunction)
        (replacement: ExpressionRecipe)
        =
        AgentExpressionStructuralWorkflow.simulate
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
        let literal () = ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0)

        match simulate snapshot workspace target (listRecipe [ deepFloat 31 ]) with
        | Ok _ -> ()
        | error -> failwithf "Depth 32 should pass: %A" error

        simulate snapshot workspace target (listRecipe [ deepFloat 32 ])
        |> assertRejectedCode "StructuralRecipeDepthLimitExceeded"

        match simulate snapshot workspace target (listRecipe (List.init 255 (fun _ -> literal ()))) with
        | Ok _ -> ()
        | error -> failwithf "Node count 256 should pass: %A" error

        simulate snapshot workspace target (listRecipe (List.init 256 (fun _ -> literal ())))
        |> assertRejectedCode "StructuralRecipeNodeLimitExceeded"

    [<Fact>]
    let ``node-limit diagnostics report the count and the largest extractable subtrees`` () =
        let snapshot, workspace, target = createFixture ()
        let literal () = ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0)
        let wide count = ExpressionRecipe.ListConstruct(float64Type, List.init count (fun _ -> literal ()))
        let small = ExpressionRecipe.ListConcat(wide 3, wide 2)
        // 1 + 201 + (1 + 61 + 8) = 272 nodes. Both root children fit within 256; their own children are
        // nested in an already listed subtree, so they are not repeated.
        let recipe = ExpressionRecipe.ListConcat(wide 200, ExpressionRecipe.ListConcat(wide 60, small))
        match simulate snapshot workspace target recipe with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("StructuralRecipeNodeLimitExceeded", diagnostic.Code)
            Assert.Contains("contains 272 nodes, more than 256", diagnostic.Message)
            Assert.Contains("/0 ListConstruct (201 nodes); /1 ListConcat (70 nodes)", diagnostic.Message)
            Assert.DoesNotContain("/1/0", diagnostic.Message)
        | other -> failwithf "Expected a node-limit rejection, got %A" other

    [<Fact>]
    let ``targeted replacement enforces depth and node budgets at their boundaries`` () =
        let float64List = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ float64Type ])
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-recipe-bounds-targeted-" + name)
        let leftId, rightId, resultId = nodeId "left", nodeId "right", nodeId "result"
        let target: SemanticFunction =
            { Exported = true; Id = StableId.createForTesting<FunctionTag> "function-recipe-bounds-targeted"
              TypeParameters = []
              DisplayName = "targeted"
              Parameters = []
              ResultType = float64List
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = leftId; Kind = SemanticNodeKind.ListEmpty float64Type }
                      { Id = rightId; Kind = SemanticNodeKind.ListEmpty float64Type }
                      { Id = resultId; Kind = SemanticNodeKind.ListConcat(leftId, rightId) } ]
                  ResultNodeId = resultId } }
        let snapshot = { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-recipe-bounds-targeted") with Functions = [ target ] }
        let workspace =
            match AgentWorkspace.create snapshot with
            | Ok workspace -> workspace
            | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues
        let literal () = ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0)
        let rec deep depth = if depth <= 1 then literal () else ExpressionRecipe.Float64Add(literal (), deep (depth - 1))
        let list items = ExpressionRecipe.ListConstruct(float64Type, items)
        // The bounds apply to the replacement recipe of one non-result node, not to the rest of the body.
        let replace replacement =
            AgentExpressionStructuralWorkflow.Targeted.simulate
                workspace
                { TargetFunctionId = target.Id
                  ExpectedBaseSnapshotId = snapshot.Id
                  TargetNodeId = leftId
                  Replacement = replacement }
        let accepted label = function
            | Ok _ -> ()
            | error -> failwithf "%s should pass: %A" label error

        replace (list [ deep 31 ]) |> accepted "Depth 32"
        replace (list [ deep 32 ]) |> assertRejectedCode "StructuralRecipeDepthLimitExceeded"
        replace (list (List.init 255 (fun _ -> literal ()))) |> accepted "Node count 256"
        replace (list (List.init 256 (fun _ -> literal ()))) |> assertRejectedCode "StructuralRecipeNodeLimitExceeded"
