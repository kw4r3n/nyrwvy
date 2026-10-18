namespace Nyrwvy.Core

/// Derived scope-free requirements for type parameters used inside OneShot payloads.
/// These are graph facts, not persisted constraints or a new generic type system.
[<RequireQualifiedAccess>]
module internal OneShotPayloadRequirements =
    let private instantiations =
        SnapshotComputationScope.Memoize("oneShotInstantiations", fun (snapshot: Snapshot) ->
            let functions = snapshot.Functions |> List.map (fun fn -> fn.Id, fn) |> Map.ofList
            let resolve = SemanticFacts.createNodeTypeResolverFactory snapshot
            snapshot.Functions |> List.collect (fun fn ->
                let nodeType = resolve fn
                let listItem source =
                    match nodeType source with
                    | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])) -> [ item ]
                    | _ -> []
                let optionItem source =
                    match nodeType source with
                    | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])) -> [ item ]
                    | _ -> []
                let resultCase pick source =
                    match nodeType source with
                    | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ ok; error ])) -> [ pick (ok, error) ]
                    | _ -> []
                let mapItems source =
                    match nodeType source with
                    | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ key; value ])) -> [ key; value ]
                    | _ -> []
                ValueGraph.collectNodes fn.Body |> List.collect (fun node ->
                    let iteration =
                        match node.Kind with
                        | SemanticNodeKind.CancellationShield _ -> [ TypeRef.Primitive PrimitiveType.Unit ]
                        | SemanticNodeKind.TaskScope _ -> [ TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) ]
                        | SemanticNodeKind.TaskStart(_, _, _, item) -> nodeType item |> Option.toList
                        | SemanticNodeKind.UseResource(acquire, _, _, _, _) ->
                            match nodeType acquire with
                            | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ resource; _ ])) -> [ resource ]
                            | _ -> []
                        | SemanticNodeKind.ListFold(source, seed, _, _)
                        | SemanticNodeKind.ListEffectFold(source, seed, _, _) -> (nodeType seed |> Option.toList) @ listItem source
                        | SemanticNodeKind.MapFold(source, seed, _, _) -> (nodeType seed |> Option.toList) @ mapItems source
                        | SemanticNodeKind.ListSortBy(source, _, _)
                        | SemanticNodeKind.ListMinBy(source, _, _) -> let items = listItem source in items @ items
                        | SemanticNodeKind.ListZipWith(left, right, _, _) -> listItem left @ listItem right
                        | SemanticNodeKind.ListFlatMap(source, _, _)
                        | SemanticNodeKind.ListMap(source, _, _)
                        | SemanticNodeKind.MapFromList(source, _, _)
                        | SemanticNodeKind.ListFilter(source, _, _)
                        | SemanticNodeKind.ListAny(source, _, _)
                        | SemanticNodeKind.ListAll(source, _, _)
                        | SemanticNodeKind.ListFindIndex(source, _, _)
                        | SemanticNodeKind.ParallelAll(source, _, _) -> listItem source
                        | SemanticNodeKind.MapFilter(source, _, _)
                        | SemanticNodeKind.MapMapValues(source, _, _) -> mapItems source
                        | SemanticNodeKind.OptionMap(source, _, _)
                        | SemanticNodeKind.OptionAndThen(source, _, _) -> optionItem source
                        | SemanticNodeKind.ResultMap(source, _, _)
                        | SemanticNodeKind.ResultAndThen(source, _, _) -> resultCase fst source
                        | SemanticNodeKind.ResultMapErr(source, _, _) -> resultCase snd source
                        | _ -> []
                    SemanticNodeShape.functionReferences node |> List.choose (fun reference ->
                        Map.tryFind reference.FunctionId functions |> Option.bind (fun callee ->
                            let contextPairs = reference.Arguments |> List.choose (fun argument ->
                                match callee.Parameters |> List.tryFind (fun p -> p.Id = argument.TargetParameterId), nodeType argument.ValueNodeId with
                                | Some parameter, Some actual -> Some(parameter.Type, actual)
                                | _ -> None)
                            let unbound = callee.Parameters |> List.filter (fun p -> not (reference.Arguments |> List.exists (fun a -> a.TargetParameterId = p.Id)))
                            let iterationPairs = List.zip (List.truncate iteration.Length unbound) (List.truncate unbound.Length iteration) |> List.map (fun (p, actual) -> p.Type, actual)
                            TypeParameters.matchAll (contextPairs @ iterationPairs)
                            |> Option.map (fun substitution -> fn.Id, node.Id, reference, substitution))))))

    let private requiredParameters typeRef =
        let rec visit current =
            match current with
            | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, arguments) ->
                arguments |> List.collect TypeParameters.occurring |> List.map fst |> Set.ofList
            | TypeRef.BuiltinGeneric(_, arguments)
            | TypeRef.Named(_, arguments) -> arguments |> List.map visit |> Set.unionMany
            | TypeRef.Function(arguments, result) -> (result :: arguments) |> List.map visit |> Set.unionMany
            | _ -> Set.empty
        visit typeRef

    let private deriveUncached (snapshot: Snapshot) =
        let nodesByFunction = snapshot.Functions |> List.map (fun fn -> fn, ValueGraph.collectNodes fn.Body)
        let mutable requirements =
            nodesByFunction |> List.map (fun (fn, nodes) ->
                let bodyTypes = nodes |> List.collect (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.OneShotCreate(_, payload) -> [ TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ payload ]) ]
                    | _ -> SemanticNodeShape.payloadTypeRefs node)
                let types = fn.ResultType :: (fn.Parameters |> List.map (fun p -> p.Type)) @ bodyTypes
                fn.Id, types |> List.map requiredParameters |> Set.unionMany) |> Map.ofList
        // A monotone fixed point over existing parameter IDs handles transitive and recursive
        // generic calls, including a callee that creates a cell but returns Unit.
        if requirements |> Map.exists (fun _ parameters -> not (Set.isEmpty parameters)) then
            let calls = instantiations snapshot
            let mutable changed = true
            while changed do
                changed <- false
                for owner, _, reference, substitution in calls do
                    let inherited =
                        Map.find reference.FunctionId requirements |> Seq.choose (fun id -> Map.tryFind id substitution)
                        |> Seq.collect TypeParameters.occurring |> Seq.map fst |> Set.ofSeq
                    let previous = Map.find owner requirements
                    let next = Set.union previous inherited
                    if next <> previous then
                        requirements <- Map.add owner next requirements
                        changed <- true
        requirements

    let derive = SnapshotComputationScope.Memoize("oneShotPayloadRequirements", deriveUncached)

    let violates (requirements: Map<FunctionId, Set<TypeParameterId>>) target substitution =
        Map.tryFind target requirements |> Option.defaultValue Set.empty
        |> Set.exists (fun id -> Map.tryFind id substitution |> Option.exists TaskTypes.mentionsScope)

    let invalidCallbackUses snapshot =
        let requirements = derive snapshot
        if requirements |> Map.forall (fun _ parameters -> Set.isEmpty parameters) then []
        else
            instantiations snapshot |> List.choose (fun (_, nodeId, reference, substitution) ->
                if reference.Role <> FunctionReferenceRole.DirectCall && violates requirements reference.FunctionId substitution then
                    Some nodeId
                else None)
