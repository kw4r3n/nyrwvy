namespace Nyrwvy.Core.Tests

open System
open Nyrwvy.Core
open Xunit

module ListFoldTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("fold-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ])
    let private stateId = id<TypeTag> "state"
    let private keyId = id<FieldTag> "key"
    let private foundId = id<FieldTag> "found"
    let private stateType = TypeRef.Named(stateId, [])

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private fnId name = id<FunctionTag> name
    let private paramId name = id<ParameterTag> name
    let private nd name kind : SemanticNode = { Id = id<GraphNodeTag> name; Kind = kind }
    let private pref name pid = nd name (SemanticNodeKind.ParameterReference pid)
    let private graph resultId nodes : ValueGraph = { Nodes = nodes; ResultNodeId = resultId }

    let private makeFn name parameters resultType body : SemanticFunction =
        { Id = fnId name
          DisplayName = name
          Exported = true
          MustNotWait = false
          TypeParameters = []
          Parameters = parameters |> List.map (fun (pid, displayName, itemType) -> { Id = pid; DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = []
          Body = body }

    let private buildFor prefix tree = GraphBuilder.build (fun path -> id<GraphNodeTag> (prefix + "-" + path)) tree

    let private fixture () =
        let state: SemanticType =
            { Exported = true; TypeParameters = []; Id = stateId
              DisplayName = "LookupState"
              Kind = SemanticTypeKind.Product
                [ { Id = keyId; DisplayName = "key"; Type = stringType }
                  { Id = foundId; DisplayName = "found"; Type = boolType } ] }
        // lookup_step(state, item): if state.found then state
        // else if item = state.key then { state with found = true } else state.
        let folder =
            let thenState = graph (id<GraphNodeTag> "ls-t-state") [ pref "ls-t-state" (paramId "ls-state") ]
            let thenFound =
                let g1 = pref "ls-g-state" (paramId "ls-state")
                let g2 = nd "ls-g-key" (SemanticNodeKind.FieldAccess(g1.Id, keyId))
                let g3 = nd "ls-g-true" (SemanticNodeKind.BoolLiteral true)
                let g4 =
                    nd "ls-g-pc"
                        (SemanticNodeKind.ProductConstruct(stateId, [],
                            [ { TargetFieldId = keyId; ValueNodeId = g2.Id }
                              { TargetFieldId = foundId; ValueNodeId = g3.Id } ]))
                graph g4.Id [ g1; g2; g3; g4 ]
            let elseState = graph (id<GraphNodeTag> "ls-h-state") [ pref "ls-h-state" (paramId "ls-state") ]
            let elseBody =
                let e1 = pref "ls-e-state" (paramId "ls-state")
                let e2 = pref "ls-e-item" (paramId "ls-item")
                let e3 = nd "ls-e-key" (SemanticNodeKind.FieldAccess(e1.Id, keyId))
                let e4 = nd "ls-e-eq" (SemanticNodeKind.StringEqual(e2.Id, e3.Id))
                let e5 = nd "ls-e-root" (SemanticNodeKind.Conditional(e4.Id, thenFound, elseState))
                graph e5.Id [ e1; e2; e3; e4; e5 ]
            let c1 = pref "ls-c-state" (paramId "ls-state")
            let c2 = nd "ls-c-found" (SemanticNodeKind.FieldAccess(c1.Id, foundId))
            let root = nd "ls-root" (SemanticNodeKind.Conditional(c2.Id, thenState, elseBody))
            let body = graph root.Id [ c1; c2; root ]
            makeFn "lookup_step"
                [ paramId "ls-state", "state", stateType; paramId "ls-item", "item", stringType ]
                stateType body
        // contains_key(items, key) = fold(items, { key = key, found = false }, lookup_step).found
        let search =
            let k1 = pref "ck-key" (paramId "ck-key")
            let k2 = nd "ck-false" (SemanticNodeKind.BoolLiteral false)
            let k3 =
                nd "ck-seed"
                    (SemanticNodeKind.ProductConstruct(stateId, [],
                        [ { TargetFieldId = keyId; ValueNodeId = k1.Id }
                          { TargetFieldId = foundId; ValueNodeId = k2.Id } ]))
            let k4 = pref "ck-items" (paramId "ck-items")
            let k5 = nd "ck-fold" (SemanticNodeKind.ListFold(k4.Id, k3.Id, folder.Id, []))
            let k6 = nd "ck-root" (SemanticNodeKind.FieldAccess(k5.Id, foundId))
            makeFn "contains_key"
                [ paramId "ck-items", "items", listType; paramId "ck-key", "key", stringType ]
                boolType (graph k6.Id [ k1; k2; k3; k4; k5; k6 ])
        let digitFolder =
            makeFn "append_digit"
                [ paramId "ad-acc", "accumulator", intType; paramId "ad-digit", "digit", intType ]
                intType
                (buildFor "ad"
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.Int64WrappingAdd(ids[0], ids[1]))
                        [ GraphBuilder.node (fun ids -> SemanticNodeKind.Int64WrappingMultiply(ids[0], ids[1]))
                            [ GraphBuilder.param (paramId "ad-acc"); GraphBuilder.leaf (SemanticNodeKind.Int64Literal 10L) ]
                          GraphBuilder.param (paramId "ad-digit") ]))
        let digits =
            makeFn "digits"
                [ paramId "dg-items", "items", TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ]) ]
                intType
                (buildFor "dg"
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.ListFold(ids[0], ids[1], digitFolder.Id, []))
                        [ GraphBuilder.param (paramId "dg-items"); GraphBuilder.leaf (SemanticNodeKind.Int64Literal 0L) ]))
        let snapshot =
            { Snapshot.empty (id<SnapshotTag> "initial") with
                Types = [ state ]
                Functions = [ folder; search; digitFolder; digits ] }
        snapshot, folder, search, digits

    [<Fact>]
    let ``fold with runtime context authors publishes persists and lowers with stable callback identity`` () =
        let snapshot, folder, search, _ = fixture ()
        Assert.Empty(SnapshotValidator.validate snapshot)
        let json = SnapshotCodec.serialize snapshot |> unwrap "serialize JSON"
        let restored = SnapshotCodec.deserialize json |> unwrap "restore JSON"
        let persisted = SnapshotCodec.serialize restored |> unwrap "reserialize"
        let reopened = SnapshotCodec.deserialize persisted |> unwrap "restore"
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute reopened)
        let focus = SemanticQuery.focusFunction reopened search.Id |> unwrap "focus"
        Assert.Contains(focus.Callees, fun site -> site.CalleeFunctionId = folder.Id)
        let foldNode =
            search.Body.Nodes
            |> List.find (fun node -> match node.Kind with | SemanticNodeKind.ListFold _ -> true | _ -> false)
        Assert.Equal(Some stateType,
            focus.NodeTypes |> List.find (fun fact -> fact.NodeId = foldNode.Id) |> fun fact -> fact.Type)
        let typed = TypedSemanticIr.project reopened |> unwrap "typed projection"
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"listFold\"", TypedSemanticIrSerializer.serialize typed)
        Assert.Contains("\"listFold\"", ExecutionIrSerializer.serialize execution)
        Assert.Contains("\"stringEqual\"", TypedSemanticIrSerializer.serialize typed)
        Assert.Contains("\"stringEqual\"", ExecutionIrSerializer.serialize execution)

    [<Fact>]
    let ``StringEqual rejects a mistyped operand before publication and during snapshot validation`` () =
        let sameWith tree =
            let fn =
                makeFn "same"
                    [ paramId "same-left", "left", stringType; paramId "same-right", "right", stringType ]
                    boolType (buildFor "same" tree)
            { Snapshot.empty (id<SnapshotTag> "equality-initial") with Functions = [ fn ] }
        let equalTree left =
            GraphBuilder.node (fun ids -> SemanticNodeKind.StringEqual(ids[0], ids[1]))
                [ left; GraphBuilder.param (paramId "same-right") ]
        let invalid = sameWith (equalTree (GraphBuilder.leaf (SemanticNodeKind.Int64Literal 1L)))
        Assert.Contains(SnapshotValidator.validate invalid, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
        let snapshot = sameWith (equalTree (GraphBuilder.param (paramId "same-left")))
        Assert.Empty(SnapshotValidator.validate snapshot)
        let corrupted =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun item ->
                    { item with
                        Body =
                            { item.Body with
                                Nodes = item.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference parameterId when parameterId = item.Parameters.Head.Id ->
                                        { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate corrupted, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

    [<Fact>]
    let ``fold rejects a folder with the wrong accumulator type before publication`` () =
        let snapshot, _, search, _ = fixture ()
        // The seed no longer has the folder's accumulator type.
        let wrongSeed =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun functionEntity ->
                    if functionEntity.Id <> search.Id then functionEntity
                    else
                        { functionEntity with
                            Body =
                                { functionEntity.Body with
                                    Nodes = functionEntity.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ProductConstruct _ -> { node with Kind = SemanticNodeKind.BoolLiteral false }
                                        | _ -> node) } }) }
        Assert.NotEmpty(SnapshotValidator.validate wrongSeed)
        let corrupted =
            { snapshot with
                Functions =
                    snapshot.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.Id <> search.Id then functionEntity
                        else
                            { functionEntity with
                                Body =
                                    { functionEntity.Body with
                                        Nodes =
                                            functionEntity.Body.Nodes
                                            |> List.map (fun node ->
                                                match node.Kind with
                                                | SemanticNodeKind.ListFold(source, seed, _, _) ->
                                                    { node with Kind = SemanticNodeKind.ListFold(source, seed, search.Id, []) }
                                                | _ -> node) } }) }
        let issues = SnapshotValidator.validate corrupted
        Assert.Contains(issues, fun issue -> issue.Code = "CallbackParameterTypeMismatch")
        Assert.Contains(issues, fun issue -> issue.Code = "CallbackResultTypeMismatch")

    [<Fact>]
    let ``fold checks the folder even when the source is not a list`` () =
        let snapshot, folder, _, _ = fixture ()
        // The folder checks used to be skipped entirely when the source had the wrong shape,
        // so an unknown or mistyped folder beside a non-list source reported only the source.
        let badWith folderId =
            let fn =
                makeFn "bad"
                    [ paramId "bad-items", "items", intType; paramId "bad-seed", "seed", intType ]
                    intType
                    (buildFor "bad"
                        (GraphBuilder.node (fun ids -> SemanticNodeKind.ListFold(ids[0], ids[1], folderId, []))
                            [ GraphBuilder.param (paramId "bad-items"); GraphBuilder.param (paramId "bad-seed") ]))
            SnapshotValidator.validate { snapshot with Functions = snapshot.Functions @ [ fn ] }
        let unknown = badWith (fnId "missing")
        Assert.Contains(unknown, fun issue -> issue.Code = "HigherOrderSourceTypeMismatch")
        Assert.Contains(unknown, fun issue -> issue.Code = "UnknownCallbackFunction")
        // lookup_step returns the state type, not the Int64 seed type used here.
        let mistyped = badWith folder.Id
        Assert.Contains(mistyped, fun issue -> issue.Code = "HigherOrderSourceTypeMismatch")
        Assert.Contains(mistyped, fun issue -> issue.Code = "CallbackResultTypeMismatch")
