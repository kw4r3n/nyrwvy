namespace Nyrwvy.Core

/// Where a waiting path ends: a builtin that blocks, or a capability operation marked `MayWait`.
[<RequireQualifiedAccess>]
type WaitSource =
    | Builtin of kind: string
    | Operation of CapabilityOperationId

/// Why a function may wait: the functions it runs on the way (each with the node that runs it),
/// then the node that waits and what it is. Derived, never persisted.
type WaitPath =
    { Via: (GraphNodeId * FunctionId) list
      NodeId: GraphNodeId
      Source: WaitSource }

/// "May wait" (docs/decisions/wait-and-task-bound-attributes.md): a function may wait when its body
/// waits itself (TaskWait, OneShotWait, ParallelAll, TaskScope, or a capability operation marked
/// `MayWait`, including a UseResource release) or runs a function that may wait (direct calls,
/// callbacks, bodies, handlers and workers). Unlike effect requirements nothing declares it, so the
/// fact is transitive. Function values and capability implementations are not followed: validation
/// requires both to be pure, and a pure function cannot wait.
[<RequireQualifiedAccess>]
module Waiting =
    /// The builtin wait a node performs itself, by kind name; each blocks its task in the C# runtime.
    let builtinWait (node: SemanticNode): string option =
        match node.Kind with
        | SemanticNodeKind.TaskWait _ -> Some "TaskWait"
        | SemanticNodeKind.OneShotWait _ -> Some "OneShotWait"
        // Joins every worker.
        | SemanticNodeKind.ParallelAll _ -> Some "ParallelAll"
        // Closing the scope joins its unfinished tasks.
        | SemanticNodeKind.TaskScope _ -> Some "TaskScope"
        | _ -> None

    let private deriveUncached (snapshot: Snapshot): Map<FunctionId, WaitPath> =
        let waitingOperations =
            snapshot.Capabilities
            |> List.collect (fun capability -> capability.Operations)
            |> List.filter (fun operation -> operation.MayWait)
            |> List.map (fun operation -> operation.Id)
            |> Set.ofList
        let bodies = snapshot.Functions |> List.map (fun fn -> fn.Id, ValueGraph.collectNodes fn.Body)
        let direct (nodes: SemanticNode list) =
            nodes
            |> List.tryPick (fun node ->
                match builtinWait node with
                | Some kind -> Some(node.Id, WaitSource.Builtin kind)
                | None ->
                    SemanticNodeShape.invokedOperations node
                    |> List.tryFind (fun id -> Set.contains id waitingOperations)
                    |> Option.map (fun id -> node.Id, WaitSource.Operation id))
        let mutable found =
            bodies
            |> List.choose (fun (id, nodes) -> direct nodes |> Option.map (fun (nodeId, source) -> id, { Via = []; NodeId = nodeId; Source = source }))
            |> Map.ofList
        let invoked =
            bodies
            |> List.map (fun (id, nodes) ->
                id,
                nodes
                |> List.collect (fun node ->
                    SemanticNodeShape.functionReferences node
                    |> List.filter (fun reference -> FunctionReferenceRole.isInvoked reference.Role)
                    |> List.map (fun reference -> node.Id, reference.FunctionId)))
        // Each round adds the functions that run one found in an earlier round, so every path is finite.
        let mutable changed = true
        while changed do
            changed <- false
            for id, calls in invoked do
                if not (Map.containsKey id found) then
                    match calls |> List.tryFind (fun (_, callee) -> Map.containsKey callee found) with
                    | Some(nodeId, callee) ->
                        let inner = Map.find callee found
                        found <- Map.add id { inner with Via = (nodeId, callee) :: inner.Via } found
                        changed <- true
                    | None -> ()
        found

    /// Every function that may wait, with one path to what it waits on.
    let derive: Snapshot -> Map<FunctionId, WaitPath> = SnapshotComputationScope.Memoize("waiting", deriveUncached)

    let mayWait (snapshot: Snapshot) (functionId: FunctionId) = Map.containsKey functionId (derive snapshot)

    /// The path as text, for diagnostics: `helper (node 'n1') -> TaskWait (node 'n2')`.
    let describe (snapshot: Snapshot) (path: WaitPath): string =
        let functionName id =
            snapshot.Functions
            |> List.tryFind (fun fn -> fn.Id = id)
            |> Option.map (fun fn -> fn.DisplayName)
            |> Option.defaultValue $"'{StableId.value id}'"
        let source =
            match path.Source with
            | WaitSource.Builtin kind -> kind
            | WaitSource.Operation id ->
                snapshot.Capabilities
                |> List.tryPick (fun capability ->
                    capability.Operations
                    |> List.tryFind (fun operation -> operation.Id = id)
                    |> Option.map (fun operation -> $"waiting operation {capability.DisplayName}.{operation.DisplayName}"))
                |> Option.defaultValue $"waiting operation '{StableId.value id}'"
        (path.Via |> List.map (fun (nodeId, id) -> $"{functionName id} (node '{StableId.value nodeId}')"))
        @ [ $"{source} (node '{StableId.value path.NodeId}')" ]
        |> String.concat " -> "
