namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type EffectRequirementSource =
    | FunctionContract of FunctionId
    | CapabilityOperation of CapabilityOperationId

type EffectRequirementEvidence =
    { EffectId: EffectId
      NodeId: GraphNodeId
      Source: EffectRequirementSource }

/// Derived from local body nodes and referenced public contracts, never persisted or inferred
/// transitively through a callee's implementation. Missing references keep the report incomplete.
type BodyEffectRequirements =
    { EffectIds: EffectId list
      Evidence: EffectRequirementEvidence list
      UnresolvedFunctionIds: FunctionId list
      UnresolvedOperationIds: CapabilityOperationId list }

type FunctionEffectSummary =
    { FunctionId: FunctionId
      DeclaredEffectIds: EffectId list
      Body: BodyEffectRequirements }

[<RequireQualifiedAccess>]
module EffectRequirements =
    let derive (snapshot: Snapshot) (fn: SemanticFunction): BodyEffectRequirements =
        let functions = snapshot.Functions |> List.map (fun item -> item.Id, item) |> Map.ofList
        let operations = snapshot.Capabilities |> List.collect (fun cap -> cap.Operations) |> List.map (fun op -> op.Id, op) |> Map.ofList
        let sources =
            ValueGraph.collectNodes fn.Body |> List.collect (fun node ->
                let functionSource id = node.Id, EffectRequirementSource.FunctionContract id
                let operationSource id = node.Id, EffectRequirementSource.CapabilityOperation id
                match node.Kind with
                | SemanticNodeKind.DirectCall(id, _)
                | SemanticNodeKind.ListFold(_, _, id, _)
                | SemanticNodeKind.ListEffectFold(_, _, id, _)
                | SemanticNodeKind.ParallelAll(_, id, _)
                | SemanticNodeKind.TaskScope(id, _)
                | SemanticNodeKind.TaskStart(_, id, _, _)
                | SemanticNodeKind.ListMap(_, id, _)
                | SemanticNodeKind.ListFilter(_, id, _)
                | SemanticNodeKind.ListAny(_, id, _)
                | SemanticNodeKind.ListAll(_, id, _) -> [ functionSource id ]
                | SemanticNodeKind.UseResource(_, _, operationId, bodyId, _) -> [ functionSource bodyId; operationSource operationId ]
                | SemanticNodeKind.CapabilityInvoke(_, id, _) -> [ operationSource id ]
                | _ -> [])
        let requirements = function
            | EffectRequirementSource.FunctionContract id -> Map.tryFind id functions |> Option.map (fun target -> target.RequiredEffects)
            | EffectRequirementSource.CapabilityOperation id -> Map.tryFind id operations |> Option.map (fun op -> [ op.EffectId ])
        let evidence =
            sources
            |> List.collect (fun (node, source) ->
                requirements source |> Option.defaultValue [] |> List.map (fun effect -> { EffectId = effect; NodeId = node; Source = source }))
            |> List.distinct
            |> List.sort
        { EffectIds = evidence |> List.map (fun item -> item.EffectId) |> List.distinct |> List.sortBy StableId.value
          Evidence = evidence
          UnresolvedFunctionIds = sources |> List.choose (function
            | _, EffectRequirementSource.FunctionContract id when not (Map.containsKey id functions) -> Some id
            | _ -> None) |> List.distinct |> List.sortBy StableId.value
          UnresolvedOperationIds = sources |> List.choose (function
            | _, EffectRequirementSource.CapabilityOperation id when not (Map.containsKey id operations) -> Some id
            | _ -> None) |> List.distinct |> List.sortBy StableId.value }

    let summarize (snapshot: Snapshot) functionIds =
        snapshot.Functions
        |> List.filter (fun fn -> Set.contains fn.Id functionIds)
        |> List.sortBy (fun fn -> StableId.value fn.Id)
        |> List.map (fun fn ->
            { FunctionId = fn.Id
              DeclaredEffectIds = fn.RequiredEffects |> List.distinct |> List.sortBy StableId.value
              Body = derive snapshot fn })
