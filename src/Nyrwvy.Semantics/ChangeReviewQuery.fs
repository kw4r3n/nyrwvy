namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type ChangeReviewReadiness =
    | Invalid of ValidationIssue list
    | Incomplete of ObligationId list
    | Complete

type ChangeReviewView =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      Diff: SemanticDiff
      Readiness: ChangeReviewReadiness
      AffectedFunctionIds: FunctionId list
      AffectedTypeIds: TypeId list
      FunctionFocus: FunctionFocusView list }

[<RequireQualifiedAccess>]
type ChangeReviewQueryError =
    | FocusQueryFailed of SemanticQueryError

[<RequireQualifiedAccess>]
module ChangeReviewQuery =
    let private functionOwnerByNode (snapshot: Snapshot): Map<GraphNodeId, FunctionId> =
        snapshot.Functions
        |> List.collect (fun functionEntity ->
            ValueGraph.collectNodes functionEntity.Body
            |> List.map (fun node -> node.Id, functionEntity.Id))
        |> Map.ofList

    let private affectedFunctionIds
        (simulation: TransactionSimulation)
        : FunctionId list =
        let ownersByNode = functionOwnerByNode simulation.ProposedSnapshot

        let renamed =
            simulation.Diff.Renames
            |> List.choose (function
                | FunctionRenamed(functionId, _, _)
                | ParameterRenamed(functionId, _, _, _) -> Some functionId
                | TypeRenamed _
                | FieldRenamed _
                | VariantRenamed _
                | EffectRenamed _
                | CapabilityRenamed _
                | CapabilityOperationRenamed _
                | CapabilityParameterRenamed _ -> None)

        let parameterOwners =
            (simulation.Diff.ParametersAdded |> List.map (fun item -> item.OwnerFunctionId))
            @ (simulation.Diff.ParametersRemoved |> List.map (fun item -> item.OwnerFunctionId))

        let effectOwners =
            simulation.Diff.FunctionRequiredEffectsChanged
            |> List.map (function
                | FunctionRequiredEffectChange.Added(functionId, _)
                | FunctionRequiredEffectChange.Removed(functionId, _) -> functionId)

        let changedCallOwners =
            (simulation.Diff.ArgumentsAttached
             |> List.map (fun item -> item.CallNodeId))
            @ (simulation.Diff.ObligationsCreated
               |> List.map (fun item -> item.CallNodeId))
            @ (simulation.Diff.ObligationsResolved
               |> List.map (fun item -> item.CallNodeId))
            @ (simulation.Diff.ParametersRemoved |> List.collect (fun item -> item.AffectedCallNodeIds))
            @ (simulation.Diff.UnionVariantsRemoved |> List.collect (fun item -> item.AffectedMatchNodeIds))
            |> List.choose (fun nodeId -> Map.tryFind nodeId ownersByNode)

        let changedProductOwners =
            (simulation.Diff.ProductFieldsBound |> List.map (fun item -> item.OwnerFunctionId))
            @ (simulation.Diff.ProductFieldsRemoved
               |> List.collect (fun item ->
                   item.AffectedConstructNodeIds |> List.choose (fun nodeId -> Map.tryFind nodeId ownersByNode)))

        let changedImplementationOwners =
            simulation.Diff.CapabilityImplementationsBound |> List.map (fun item -> item.OwnerFunctionId)

        let changedMatchOwners =
            simulation.Diff.UnionMatchArmsAdded |> List.map (fun item -> item.OwnerFunctionId)

        let present = simulation.ProposedSnapshot.Functions |> List.map (fun fn -> fn.Id) |> Set.ofList
        renamed @ parameterOwners @ effectOwners @ changedCallOwners @ changedProductOwners @ changedImplementationOwners @ changedMatchOwners
        |> List.distinct
        |> List.filter (fun id -> Set.contains id present)
        |> List.sortBy StableId.value

    let private affectedTypeIds (simulation: TransactionSimulation): TypeId list =
        (simulation.Diff.Renames
         |> List.choose (function
             | TypeRenamed(typeId, _, _)
             | FieldRenamed(typeId, _, _, _, _)
             | VariantRenamed(typeId, _, _, _) -> Some typeId
             | FunctionRenamed _
             | ParameterRenamed _
             | EffectRenamed _
             | CapabilityRenamed _
             | CapabilityOperationRenamed _
             | CapabilityParameterRenamed _ -> None))
        @ (simulation.Diff.ProductFieldsAdded |> List.map (fun item -> item.OwnerTypeId))
        @ (simulation.Diff.ProductFieldsRemoved |> List.map (fun item -> item.OwnerTypeId))
        @ (simulation.Diff.UnionVariantsRemoved |> List.map (fun item -> item.OwnerTypeId))
        @ (simulation.Diff.UnionVariantsAdded |> List.map (fun item -> item.OwnerTypeId))
        |> List.distinct
        |> List.filter (fun id -> simulation.ProposedSnapshot.Types |> List.exists (fun item -> item.Id = id))
        |> List.sortBy StableId.value

    let private readiness snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> ChangeReviewReadiness.Invalid issues
        | Ok SemanticReadiness.Complete -> ChangeReviewReadiness.Complete
        | Ok(SemanticReadiness.Incomplete obligationIds) ->
            ChangeReviewReadiness.Incomplete obligationIds

    let project
        (simulation: TransactionSimulation)
        : Result<ChangeReviewView, ChangeReviewQueryError> =
        let functionIds = affectedFunctionIds simulation

        let rec collectFocus acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | functionId :: rest ->
                match SemanticQuery.focusFunction simulation.ProposedSnapshot functionId with
                | Ok focus -> collectFocus (focus :: acc) rest
                | Error error -> Error(ChangeReviewQueryError.FocusQueryFailed error)

        collectFocus [] functionIds
        |> Result.map (fun focus ->
            { TransactionId = simulation.TransactionId
              BaseSnapshotId = simulation.BaseSnapshotId
              ProposedSnapshotId = simulation.ProposedSnapshot.Id
              Diff = simulation.Diff
              Readiness = readiness simulation.ProposedSnapshot
              AffectedFunctionIds = functionIds
              AffectedTypeIds = affectedTypeIds simulation
              FunctionFocus = focus })
