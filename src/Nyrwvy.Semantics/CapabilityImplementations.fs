namespace Nyrwvy.Core

/// One contract shared by recipe validation and the full snapshot oracle.
[<RequireQualifiedAccess>]
module CapabilityImplementations =
    let validate (snapshot: Snapshot) capabilityId (implementations: (CapabilityOperationId * FunctionId) list) =
        match snapshot.Capabilities |> List.tryFind (fun capability -> capability.Id = capabilityId) with
        | None -> [ "UnknownCapabilityConstructTarget", "A capability construction must name an existing capability." ]
        | Some capability ->
            let operations = capability.Operations |> List.map (fun op -> op.Id, op) |> Map.ofList
            let functions = snapshot.Functions |> List.map (fun fn -> fn.Id, fn) |> Map.ofList
            [ if implementations |> List.countBy fst |> List.exists (fun (_, count) -> count > 1) then
                  yield "DuplicateCapabilityImplementation", "Each capability operation must have exactly one implementation."
              if (implementations |> List.map fst |> Set.ofList) <> (operations |> Map.keys |> Set.ofSeq) then
                  yield "CapabilityImplementationSetMismatch", "Implementations must cover exactly the operations of the constructed capability."
              for operationId, functionId in implementations do
                  match Map.tryFind operationId operations, Map.tryFind functionId functions with
                  | _, None ->
                      yield "UnknownCapabilityImplementationFunction", $"Implementation function '{StableId.value functionId}' does not exist."
                  | None, _ -> ()
                  | Some operation, Some fn ->
                      if not (List.isEmpty (TypeParameters.ofFunction fn)) then
                          yield "GenericCapabilityImplementationUnsupported", "Use a monomorphic wrapper for a generic implementation function."
                      if not (PureFunctions.isPure snapshot functionId) then
                          yield "ImpureCapabilityImplementation", $"Implementation '{StableId.value functionId}' must be transitively pure, including ambient cancellation behavior."
                      if (fn.Parameters |> List.map (fun p -> p.Type)) <> (operation.Parameters |> List.map (fun p -> p.Type))
                         || fn.ResultType <> operation.ResultType then
                          yield "CapabilityImplementationSignatureMismatch", $"Implementation '{StableId.value functionId}' must match operation '{StableId.value operationId}' parameter order, parameter types and result type exactly." ]
