namespace Nyrwvy.Core

/// Purity of named function references includes ambient behavior, not only nominal EffectIds.
[<RequireQualifiedAccess>]
module PureFunctions =
    let private functionsById =
        SnapshotComputationScope.Memoize("functionsById", fun (snapshot: Snapshot) ->
            snapshot.Functions |> List.map (fun fn -> fn.Id, fn) |> Map.ofList)

    let isPure (snapshot: Snapshot) functionId =
        let functions = functionsById snapshot
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
                    let effectful = nodes |> List.exists SemanticNodeShape.isIntrinsicallyEffectful
                    // Every named function, in any role, must itself be pure. The UseResource and TaskScope
                    // bodies and the ParallelAll and TaskStart workers belong to kinds that are effectful
                    // themselves, so they are listed for uniformity: `effectful` already decides those.
                    let targets =
                        nodes
                        |> List.collect (SemanticNodeShape.functionReferences >> List.map (fun reference -> reference.FunctionId))
                    not effectful && visit (Set.add id seen) (targets @ rest)
        visit Set.empty [ functionId ]
