namespace Nyrwvy.Core

/// The same dependency-order rule is used by semantic abort admission and IR lowering.
/// Traversal stays local to a graph: enclosing Bind/branch edges own cross-scope order.
[<RequireQualifiedAccess>]
module internal GraphOrder =
    let tryUnorderedPair (dependencies: Map<GraphNodeId, GraphNodeId list>) (observable: GraphNodeId list) =
        let dependsOn nodeId targetId =
            let visited = System.Collections.Generic.HashSet<GraphNodeId>()
            let rec visit currentId =
                if currentId = targetId then true
                elif not (visited.Add currentId) then false
                else Map.tryFind currentId dependencies |> Option.exists (List.exists visit)
            visit nodeId
        let rec pairs = function
            | [] -> None
            | first :: rest ->
                match rest |> List.tryFind (fun second -> not (dependsOn first second) && not (dependsOn second first)) with
                | Some second -> Some(first, second)
                | None -> pairs rest
        observable |> List.sortBy StableId.value |> pairs

    /// Legacy effect-only graphs retain their existing lowering-time readiness check.
    /// A new local exit must also pass ordering before it can be published or reopened.
    let tryAbortOrderConflict (functions: Map<FunctionId, SemanticFunction>) (fn: SemanticFunction) =
        let all = ValueGraph.collectNodes fn.Body
        if not (all |> List.exists (fun node -> match node.Kind with SemanticNodeKind.Abort _ -> true | _ -> false)) then None
        else
            let functionHasEffects id = Map.tryFind id functions |> Option.exists (fun target -> not (List.isEmpty target.RequiredEffects))
            // Bodies and TaskStart workers belong to kinds that are observable on their own above.
            // Pure list callbacks are not checked here, as before: validation requires them pure.
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
