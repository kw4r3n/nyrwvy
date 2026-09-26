namespace Nyrwvy.Core

/// Purity of named function references includes ambient behavior, not only nominal EffectIds.
[<RequireQualifiedAccess>]
module PureFunctions =
    let isPure (snapshot: Snapshot) functionId =
        let functions = snapshot.Functions |> List.map (fun fn -> fn.Id, fn) |> Map.ofList
        let rec visit seen pending =
            match pending with
            | [] -> true
            | id :: rest when Set.contains id seen -> visit seen rest
            | id :: rest ->
                match Map.tryFind id functions with
                | None -> false
                | Some fn when not (List.isEmpty fn.RequiredEffects) -> false
                | Some fn ->
                    let nodes = ValueGraph.collectNodes fn.Body
                    let effectful = nodes |> List.exists (fun node ->
                        match node.Kind with
                        | SemanticNodeKind.CapabilityInvoke _
                        | SemanticNodeKind.UseResource _
                        | SemanticNodeKind.ParallelAll _
                        | SemanticNodeKind.TaskScope _
                        | SemanticNodeKind.TaskStart _
                        | SemanticNodeKind.TaskWait _
                        | SemanticNodeKind.TaskCancel _
                        | SemanticNodeKind.CancellationRequested -> true
                        | _ -> false)
                    let targets = nodes |> List.collect (fun node ->
                        match node.Kind with
                        | SemanticNodeKind.CapabilityConstruct(_, implementations) -> List.map snd implementations
                        | SemanticNodeKind.FunctionReference target
                        | SemanticNodeKind.DirectCall(target, _)
                        | SemanticNodeKind.ListFold(_, _, target, _)
                        | SemanticNodeKind.ListEffectFold(_, _, target, _)
                        | SemanticNodeKind.ListMap(_, target, _)
                        | SemanticNodeKind.ListFilter(_, target, _)
                        | SemanticNodeKind.ListAny(_, target, _)
                        | SemanticNodeKind.ListAll(_, target, _) -> [ target ]
                        | _ -> [])
                    not effectful && visit (Set.add id seen) (targets @ rest)
        visit Set.empty [ functionId ]
