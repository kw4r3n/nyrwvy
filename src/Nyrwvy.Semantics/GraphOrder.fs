namespace Nyrwvy.Core

/// How one graph node takes part in runtime effect ordering. Semantic admission and IR lowering
/// each supply a view of their own node type; the ordering rule itself (which functions are
/// observable, which nodes are observable, which pairs are unordered) lives only in `GraphOrder`.
type internal EffectOrderView<'Node> =
    { NodeId: 'Node -> GraphNodeId
      /// The nodes of the same graph this node must run after, including nested free references.
      Dependencies: 'Node -> GraphNodeId list
      /// The node lists of the graphs this node owns.
      NestedGraphs: 'Node -> 'Node list list
      /// Whether the node is observable on its own, and the named functions it runs.
      Descriptor: 'Node -> bool * FunctionId list
      /// A local exit (Abort) is observable inside its own function only: it never makes a caller observable.
      IsLocalExit: 'Node -> bool }

/// The same dependency-order rule is used by semantic admission and IR lowering.
/// Traversal stays local to a graph: enclosing Bind/branch edges own cross-scope order.
[<RequireQualifiedAccess>]
module internal GraphOrder =
    let tryUnorderedPair (dependencies: Map<GraphNodeId, GraphNodeId list>) (observable: GraphNodeId list) =
        // A worklist rather than recursion: a dependency chain can be as long as the graph.
        let dependsOn nodeId targetId =
            let visited = System.Collections.Generic.HashSet<GraphNodeId>()
            let pending = System.Collections.Generic.Stack<GraphNodeId>()
            pending.Push nodeId
            let mutable found = false
            while not found && pending.Count > 0 do
                let currentId = pending.Pop()
                if currentId = targetId then found <- true
                elif visited.Add currentId then
                    Map.tryFind currentId dependencies |> Option.iter (List.iter pending.Push)
            found
        let rec pairs = function
            | [] -> None
            | first :: rest ->
                match rest |> List.tryFind (fun second -> not (dependsOn first second) && not (dependsOn second first)) with
                | Some second -> Some(first, second)
                | None -> pairs rest
        // Fast path: the observable nodes are totally ordered exactly when, in a topological order
        // of the graph, each one depends on the one before it. That is one walk per node instead of
        // one per pair; the pairwise search below runs only to name the pair once one exists.
        let totallyOrdered () =
            let keys = dependencies |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            let remaining = System.Collections.Generic.Dictionary<GraphNodeId, int>()
            let dependents = System.Collections.Generic.Dictionary<GraphNodeId, ResizeArray<GraphNodeId>>()
            for KeyValue(nodeId, nodeDependencies) in dependencies do
                let inGraph = nodeDependencies |> List.distinct |> List.filter keys.Contains
                remaining[nodeId] <- inGraph.Length
                for dependency in inGraph do
                    match dependents.TryGetValue dependency with
                    | true, values -> values.Add nodeId
                    | false, _ -> dependents[dependency] <- ResizeArray [ nodeId ]
            let ready = System.Collections.Generic.Queue<GraphNodeId>(remaining |> Seq.filter (fun pair -> pair.Value = 0) |> Seq.map (fun pair -> pair.Key) |> Seq.sortBy StableId.value)
            let order = ResizeArray<GraphNodeId>()
            while ready.Count > 0 do
                let nodeId = ready.Dequeue()
                order.Add nodeId
                match dependents.TryGetValue nodeId with
                | true, values ->
                    for dependent in values do
                        remaining[dependent] <- remaining[dependent] - 1
                        if remaining[dependent] = 0 then ready.Enqueue dependent
                | false, _ -> ()
            let wanted = Set.ofList observable
            // A cycle, or an observable node outside the map, leaves the pairwise search to decide.
            if order.Count <> remaining.Count || not (Set.isSubset wanted keys) then false
            else
                order
                |> Seq.filter wanted.Contains
                |> Seq.pairwise
                |> Seq.forall (fun (earlier, later) -> dependsOn later earlier)
        if List.length (List.distinct observable) < 2 || totallyOrdered () then None
        else observable |> List.sortBy StableId.value |> pairs

    /// Derive transitive runtime observability with a reverse worklist. A recursive call cycle
    /// is observable exactly when it reaches a seed; pure cycles do not invent effects.
    let observableClosure (descriptors: Map<FunctionId, bool * FunctionId list>) =
        let callers = System.Collections.Generic.Dictionary<FunctionId, ResizeArray<FunctionId>>()
        let observable = System.Collections.Generic.HashSet<FunctionId>()
        let pending = System.Collections.Generic.Queue<FunctionId>()
        for KeyValue(id, (seed, targets)) in descriptors do
            if seed && observable.Add id then pending.Enqueue id
            for target in targets do
                match callers.TryGetValue target with
                | true, values -> values.Add id
                | false, _ -> callers.Add(target, ResizeArray [ id ])
        while pending.Count > 0 do
            match callers.TryGetValue(pending.Dequeue()) with
            | true, values ->
                for caller in values do
                    if observable.Add caller then pending.Enqueue caller
            | false, _ -> ()
        observable |> Set.ofSeq

    let rec private allNodes (view: EffectOrderView<'Node>) (nodes: 'Node list) =
        nodes @ (nodes |> List.collect (fun node -> view.NestedGraphs node |> List.collect (allNodes view)))

    /// The runtime-observable functions: a function is observable when it declares effects, when
    /// any of its nodes (at any depth) is observable on its own other than a local exit, or when it
    /// runs an observable function. `functions` lists each function's id, whether it declares
    /// effects, and its body nodes.
    let observableFunctions (view: EffectOrderView<'Node>) (functions: (FunctionId * bool * 'Node list) seq) : Set<FunctionId> =
        functions
        |> Seq.map (fun (id, declaresEffects, body) ->
            let descriptors = allNodes view body |> List.map (fun node ->
                let intrinsic, targets = view.Descriptor node
                (intrinsic && not (view.IsLocalExit node)), targets)
            id, ((declaresEffects || (descriptors |> List.exists fst)), (descriptors |> List.collect snd |> List.distinct)))
        |> Map.ofSeq
        |> observableClosure

    let rec private nodeObservable (view: EffectOrderView<'Node>) (observable: Set<FunctionId>) (node: 'Node) =
        let intrinsic, targets = view.Descriptor node
        intrinsic
        || (targets |> List.exists (fun id -> Set.contains id observable))
        || (view.NestedGraphs node |> List.exists (List.exists (nodeObservable view observable)))

    /// The first pair of observable nodes of one graph (or of a graph nested in it) with no
    /// dependency between them, given the observable functions.
    let tryUnorderedEffects (view: EffectOrderView<'Node>) (observable: Set<FunctionId>) (body: 'Node list) =
        let rec check (nodes: 'Node list) =
            let edges = nodes |> List.map (fun node -> view.NodeId node, view.Dependencies node) |> Map.ofList
            let effectful = nodes |> List.filter (nodeObservable view observable) |> List.map view.NodeId
            match tryUnorderedPair edges effectful with
            | Some pair -> Some pair
            // Nested scopes retain their own dependencies: enclosing Bind/branch edges own order.
            | None -> nodes |> List.collect view.NestedGraphs |> List.tryPick check
        check body

    /// Bodies and TaskStart workers belong to kinds that are observable on their own.
    /// Pure callbacks carry no effects (validation requires them pure), so they never make a node observable.
    let private carriesCalleeEffects role =
        match role with
        | FunctionReferenceRole.DirectCall
        | FunctionReferenceRole.EffectListCallback
        | FunctionReferenceRole.Worker
        | FunctionReferenceRole.Body -> true
        | FunctionReferenceRole.PureListCallback
        | FunctionReferenceRole.FunctionValue
        | FunctionReferenceRole.CapabilityImplementation _ -> false

    let private isAbort (node: SemanticNode) = match node.Kind with SemanticNodeKind.Abort _ -> true | _ -> false

    /// The semantic view of the ordering rule. Abort and every intrinsically effectful kind are
    /// observable on their own, except ParallelAll: it orders nothing itself, so only an
    /// observable worker makes it observable. Typed lowering mirrors this per kind
    /// (`ExecutionIrAnalysis.orderingDescriptor`; EffectClassificationConsistencyTests checks it).
    let semanticView: EffectOrderView<SemanticNode> =
        { NodeId = fun node -> node.Id
          Dependencies = SemanticNodeShape.dependencies
          NestedGraphs = fun node -> SemanticNodeShape.nestedGraphs node |> List.map (fun graph -> graph.Nodes)
          Descriptor = fun node ->
            let targets =
                SemanticNodeShape.functionReferences node
                |> List.filter (fun reference -> carriesCalleeEffects reference.Role)
                |> List.map (fun reference -> reference.FunctionId)
            match node.Kind with
            | SemanticNodeKind.Abort _ -> true, targets
            | SemanticNodeKind.ParallelAll _ -> false, targets
            | _ -> SemanticNodeShape.isIntrinsicallyEffectful node, targets
          IsLocalExit = isAbort }

    /// The runtime-observable functions of a snapshot, by the rule lowering enforces.
    let semanticObservableFunctions (functions: SemanticFunction list) =
        functions
        |> List.map (fun fn -> fn.Id, not (List.isEmpty fn.RequiredEffects), fn.Body.Nodes)
        |> observableFunctions semanticView

    /// The unordered observable pair lowering would reject in `fn`, if any.
    let tryEffectOrderConflict (observable: Set<FunctionId>) (fn: SemanticFunction) =
        tryUnorderedEffects semanticView observable fn.Body.Nodes
