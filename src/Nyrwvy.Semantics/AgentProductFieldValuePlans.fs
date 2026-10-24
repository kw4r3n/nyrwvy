namespace Nyrwvy.Core

/// The value an `addProductField` construction plan gives the new field: an existing node, or a
/// recipe evaluated in the lexical scope of the `productConstruct` node, as a targeted
/// replacement of that node would see it.
[<RequireQualifiedAccess>]
type AgentProductFieldValue =
    | Existing of GraphNodeId
    | Recipe of ExpressionRecipe

type AgentProductFieldValuePlan =
    { ConstructNodeId: GraphNodeId
      Value: AgentProductFieldValue }

/// Prepares the `addProductField` operation of `simulateOperations`: materializes each recipe
/// value in its construct's scope, so the operation itself (`Transaction.addProductField`) carries
/// only materialized nodes and can be re-simulated identically at commit.
[<RequireQualifiedAccess>]
module AgentProductFieldValuePlans =
    open StructuralTransactionInternals
    open ExpressionRecipeSupport
    open ExpressionRecipeInference
    open ExpressionRecipeMaterialization
    open TargetedReplacementGraph

    /// Binds and type-checks every recipe value against the current snapshot and materializes it
    /// with fresh node and pattern-binding IDs. The exhaustive-plan rule (one plan per construct,
    /// no stray plan) is applied when the operation is simulated.
    let prepare
        (workspace: AgentWorkspace)
        (intent: AgentAddProductFieldIntent)
        (plans: AgentProductFieldValuePlan list)
        : Result<AgentAddProductFieldIntent, TransactionDiagnostic> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        let index = buildSnapshotIndex baseSnapshot

        let preparePlan (plan: AgentProductFieldValuePlan): Result<ProductFieldConstructionPlan, TransactionDiagnostic> =
            match plan.Value with
            | AgentProductFieldValue.Existing valueNodeId ->
                Ok { ConstructNodeId = plan.ConstructNodeId; ValueNodeId = valueNodeId; AddedNodes = [] }
            | AgentProductFieldValue.Recipe recipe ->
                let located =
                    baseSnapshot.Functions
                    |> List.tryPick (fun fn -> tryFindTarget [] plan.ConstructNodeId fn.Body |> Option.map (fun (_, frames) -> fn, frames))
                match located with
                | None ->
                    Error(diagnostic "ProductFieldPlanTargetNotFound" $"Construct node '{StableId.value plan.ConstructNodeId}' does not exist in any function.")
                | Some(fn, frames) ->
                    let site = $"construct '{StableId.value plan.ConstructNodeId}' in function {StableId.describe fn.DisplayName fn.Id}"
                    match validateRecipeBounds recipeChildren recipe with
                    | Error error -> Error error
                    | Ok() ->
                        match inferTypeWithFrames (Some "value") index fn (bindingTypeFrames frames) recipe with
                        | Error error -> Error error
                        | Ok(GraphCompletion.Normal valueType) when valueType = intent.NewFieldType ->
                            let nodes, resultNodeId = materialize index fn frames recipe
                            Ok { ConstructNodeId = plan.ConstructNodeId; ValueNodeId = resultNodeId; AddedNodes = nodes }
                        | Ok completion ->
                            Error(
                                diagnostic
                                    "ProductFieldValueTypeMismatch"
                                    $"The value for {site} produces {DiagnosticText.completion baseSnapshot completion}, but the new field has type {DiagnosticText.typeRef baseSnapshot intent.NewFieldType}."
                            )

        let rec loop acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | plan :: rest ->
                match preparePlan plan with
                | Error error -> Error error
                | Ok prepared -> loop (prepared :: acc) rest
        loop [] plans |> Result.map (fun prepared -> { intent with ConstructionPlans = prepared })
