namespace Nyrwvy.Core

/// The same dependency-order rule is used by semantic abort admission and IR lowering.
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
        observable |> List.sortBy StableId.value |> pairs

    /// Only functions with a local exit (Abort) are ordered here, so that such a function cannot be
    /// published or reopened unordered; graphs without one are ordered by the same rule at lowering
    /// (`ExecutionIrAnalysis.validateEffectOrder`).
    let tryAbortOrderConflict (functions: Map<FunctionId, SemanticFunction>) (fn: SemanticFunction) =
        let all = ValueGraph.collectNodes fn.Body
        if not (all |> List.exists (fun node -> match node.Kind with SemanticNodeKind.Abort _ -> true | _ -> false)) then None
        else
            let functionHasEffects id = Map.tryFind id functions |> Option.exists (fun target -> not (List.isEmpty target.RequiredEffects))
            // Bodies and TaskStart workers belong to kinds that are observable on their own above.
            // Pure callbacks carry no effects (validation requires them pure), so they never make a node observable.
            let carriesCalleeEffects role =
                match role with
                | FunctionReferenceRole.DirectCall
                | FunctionReferenceRole.EffectListCallback
                | FunctionReferenceRole.Worker
                | FunctionReferenceRole.Body -> true
                | FunctionReferenceRole.PureListCallback
                | FunctionReferenceRole.FunctionValue
                | FunctionReferenceRole.CapabilityImplementation _ -> false
            // Abort and every intrinsically effectful kind are observable on their own, except
            // ParallelAll: it orders nothing itself, so only an effectful worker makes it observable.
            let rec observable node =
                let throughReferences () =
                    SemanticNodeShape.functionReferences node |> List.exists (fun reference ->
                        carriesCalleeEffects reference.Role && functionHasEffects reference.FunctionId)
                    || SemanticNodeShape.nestedGraphs node |> List.exists (fun graph -> graph.Nodes |> List.exists observable)
                match node.Kind with
                | SemanticNodeKind.Abort _ -> true
                | SemanticNodeKind.ParallelAll _ -> throughReferences ()
                | _ when SemanticNodeShape.isIntrinsicallyEffectful node -> true
                | _ -> throughReferences ()
            let rec check graph =
                let edges = graph.Nodes |> List.map (fun node -> node.Id, SemanticNodeShape.dependencies node) |> Map.ofList
                match tryUnorderedPair edges (graph.Nodes |> List.filter observable |> List.map (fun node -> node.Id)) with
                | Some pair -> Some pair
                | None -> graph.Nodes |> List.collect SemanticNodeShape.nestedGraphs |> List.tryPick check
            check fn.Body
