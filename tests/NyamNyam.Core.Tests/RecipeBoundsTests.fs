namespace NyamNyam.Core.Tests

open NyamNyam.Core
open Xunit

module RecipeBoundsTests =
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-recipe-bounds-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-recipe-bounds-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-recipe-bounds-" + name)

    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private float64ListType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ float64Type ])

    let private createFunction id name resultType resultNodeId kind: SemanticFunction =
        { Id = id
          DisplayName = name
          Parameters = []
          ResultType = resultType
          RequiredEffects = []
          Body =
            { Nodes = [ { Id = resultNodeId; Kind = kind } ]
              ResultNodeId = resultNodeId } }

    let private createFixture () =
        let floatTarget =
            createFunction
                (functionId "float")
                "float_target"
                float64Type
                (nodeId "float-result")
                (SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 0.0))

        let listTarget =
            createFunction
                (functionId "list")
                "list_target"
                float64ListType
                (nodeId "list-result")
                (SemanticNodeKind.ListEmpty float64Type)

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ floatTarget; listTarget ]
              Obligations = [] }

        let workspace =
            match AgentWorkspace.create snapshot with
            | Ok workspace -> workspace
            | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

        snapshot, workspace, floatTarget, listTarget

    let private request snapshot target replacement: AgentReplaceFunctionBodyWithPureExpressionRecipe =
        { TargetFunctionId = target.Id
          ExpectedBaseSnapshotId = snapshot.Id
          ExpectedCurrentResultNodeId = target.Body.ResultNodeId
          Replacement = replacement }

    let private assertAccepted = function
        | Ok _ -> ()
        | Error error -> failwithf "Expected recipe to pass preflight, got %A" error

    let private assertRejectedCode expected = function
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    let rec private deepRecipe depth =
        if depth <= 1 then
            PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0)
        else
            PureExpressionRecipe.Float64Add(
                PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0),
                deepRecipe (depth - 1)
            )

    let private listRecipe itemCount =
        PureExpressionRecipe.ListConstruct(
            float64Type,
            List.init itemCount (fun _ -> PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.0))
        )

    [<Fact>]
    let ``pure recipe traversal enforces depth and node budgets at their boundaries`` () =
        let snapshot, workspace, floatTarget, listTarget = createFixture ()

        request snapshot floatTarget (deepRecipe 32)
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertAccepted

        request snapshot floatTarget (deepRecipe 33)
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralRecipeDepthLimitExceeded"

        request snapshot listTarget (listRecipe 63)
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertAccepted

        request snapshot listTarget (listRecipe 64)
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralRecipeNodeLimitExceeded"
