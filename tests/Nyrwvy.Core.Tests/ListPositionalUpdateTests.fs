namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// ListSetAt(list, index, item), ListInsertAt(list, index, item) and ListRemoveAt(list, index)
/// (post-M2 order 62): positional updates that return a new List<T> and never fail. Set and remove
/// return the list unchanged for an index outside [0, length); insert clamps the index to [0, length].
module ListPositionalUpdateTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("list-positional-" + name)
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private strings = """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"string"}]}"""
    let private ints = """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
    let private text = """{"kind":"primitive","primitive":"string"}"""
    let private int64 = """{"kind":"primitive","primitive":"int64"}"""
    let private p name = """{"kind":"parameter","name":""" + "\"" + name + "\"}"
    let private lit (value: int) = """{"kind":"int64Literal","value":""" + "\"" + string value + "\"}"
    let private setAt list index item = """{"kind":"listSetAt","list":""" + list + ""","index":""" + index + ""","item":""" + item + "}"
    let private insertAt list index item = """{"kind":"listInsertAt","list":""" + list + ""","index":""" + index + ""","item":""" + item + "}"
    let private removeAt list index = """{"kind":"listRemoveAt","list":""" + list + ""","index":""" + index + "}"
    let private concat left right = """{"kind":"listConcat","left":""" + left + ""","right":""" + right + "}"

    let private create name parameters resultType body =
        """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":""" + "\"" + name + "\""
        + ""","parameters":[""" + (parameters |> List.map (fun (n, t) -> """{"displayName":""" + "\"" + n + "\"" + ""","type":""" + t + "}") |> String.concat ",")
        + """],"resultType":""" + resultType + ""","requiredEffects":[],"body":""" + body + "}"

    /// Authors the operations in a fresh session and passes `call` and the snapshot path on. Each
    /// `*Keep` function returns the updated list followed by the original, so a run also shows that
    /// the operation left its input unchanged.
    let private withProgram name (body: (string -> JsonDocument) -> string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            // A snapshot written directly declares its backend as `create` would.
            File.WriteAllText(path + ".backends.json", """{"targets": ["csharp"]}""")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let call (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            let commit (line: string) =
                use proposal = call line
                Assert.True(proposal.RootElement.GetProperty("ok").GetBoolean(), proposal.RootElement.ToString())
                let proposalId = proposal.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use committed = call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposalId + "\"}")
                Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            let three = [ "items", strings; "index", int64; "item", text ]
            commit (create "setKeep" three strings (concat (setAt (p "items") (p "index") (p "item")) (p "items")))
            commit (create "insertKeep" three strings (concat (insertAt (p "items") (p "index") (p "item")) (p "items")))
            commit (create "removeKeep" [ "items", strings; "index", int64 ] strings (concat (removeAt (p "items") (p "index")) (p "items")))
            // Two updates of the same list: the second must not see the first's writes.
            commit (create "setTwice" [ "items", strings; "index", int64 ] strings
                        (concat (setAt (p "items") (p "index") """{"kind":"stringLiteral","value":"x"}""")
                                (setAt (p "items") (p "index") """{"kind":"stringLiteral","value":"y"}""")))
            commit (create "insertTwice" [ "items", strings; "index", int64 ] strings
                        (concat (insertAt (p "items") (p "index") """{"kind":"stringLiteral","value":"x"}""")
                                (insertAt (p "items") (p "index") """{"kind":"stringLiteral","value":"y"}""")))
            // The same updates on a slice that starts past position 0 of the shared vector.
            let shifted = """{"kind":"listSlice","list":{"kind":"parameter","name":"items"},"start":""" + lit 2 + ""","end":""" + lit 1000 + "}"
            commit (create "shiftedUpdates" [ "items", strings; "index", int64 ] strings
                        (concat (concat (setAt shifted (p "index") """{"kind":"stringLiteral","value":"s"}""")
                                        (insertAt shifted (p "index") """{"kind":"stringLiteral","value":"i"}"""))
                                (concat (removeAt shifted (p "index")) shifted)))
            commit (create "setStep" [ "acc", ints; "i", int64 ] ints
                        (setAt (p "acc") """{"kind":"int64FloorModulo","left":{"kind":"parameter","name":"i"},"right":{"kind":"int64Literal","value":"100"}}""" (p "i")))
            commit (create "churn" [ "n", int64 ] ints
                        ("""{"kind":"listFold","source":{"kind":"listRange","start":""" + lit 0 + ""","end":""" + p "n"
                         + """},"seed":{"kind":"listRange","start":""" + lit 0 + ""","end":""" + lit 100 + """},"function":"setStep"}"""))
            body call path
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private rejection (call: string -> JsonDocument) line =
        use response = call line
        let root = response.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.ToString())
        root.ToString()

    [<Fact>]
    let ``ListSetAt, ListInsertAt and ListRemoveAt type check, persist and lower through every layer`` () =
        withProgram "layers" (fun call path ->
            let mismatch = "PureExpressionOperandTypeMismatch"
            let three = [ "items", strings; "index", int64; "item", text ]
            // The item must have the list's item type; the index must be Int64; the list must be a list.
            Assert.Contains(mismatch, rejection call (create "badSetItem" three strings (setAt (p "items") (p "index") (lit 1))))
            Assert.Contains(mismatch, rejection call (create "badInsertItem" three strings (insertAt (p "items") (p "index") (lit 1))))
            Assert.Contains(mismatch, rejection call (create "badSetIndex" three strings (setAt (p "items") (p "item") (p "item"))))
            Assert.Contains(mismatch, rejection call (create "badInsertIndex" three strings (insertAt (p "items") (p "item") (p "item"))))
            Assert.Contains(mismatch, rejection call (create "badRemoveIndex" three strings (removeAt (p "items") (p "item"))))
            Assert.Contains(mismatch, rejection call (create "badRemoveList" three strings (removeAt (p "item") (p "index"))))
            // The result is the same List<T>, not an Option.
            Assert.NotEqual<string>("", rejection call (create "badResult" three ints (setAt (p "items") (p "index") (p "item"))))
            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serializeCanonical snapshot |> unwrap "serialize"
            use document = JsonDocument.Parse json
            Assert.Equal(17, document.RootElement.GetProperty("schemaVersion").GetInt32())
            for kind in [ "listSetAt"; "listInsertAt"; "listRemoveAt" ] do
                Assert.Contains("\"" + kind + "\"", json)
            let restored = SnapshotCodec.deserialize json |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            let execution = typed |> ExecutionIr.lower |> unwrap "lower"
            let typedJson = TypedSemanticIrSerializer.serialize typed
            let executionJson = ExecutionIrSerializer.serialize execution
            for kind in [ "listSetAt"; "listInsertAt"; "listRemoveAt" ] do
                Assert.Contains("\"kind\":\"" + kind + "\"", typedJson)
                Assert.Contains("\"kind\":\"" + kind + "\"", executionJson)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            Assert.Contains(".SetAt(", source)
            Assert.Contains(".InsertAt(", source)
            Assert.Contains(".RemoveAt(", source))

    [<Fact>]
    let ``persisted positional updates report list, index and item type mismatches`` () =
        let textType = TypeRef.Primitive PrimitiveType.String
        let listOfText = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ textType ])
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-list-positional-" + name)
        let codes listKind indexKind itemKind (make: GraphNodeId -> GraphNodeId -> GraphNodeId -> SemanticNodeKind) =
            let body: ValueGraph =
                { Nodes =
                    [ { Id = nodeId "list"; Kind = listKind }
                      { Id = nodeId "index"; Kind = indexKind }
                      { Id = nodeId "item"; Kind = itemKind }
                      { Id = nodeId "op"; Kind = make (nodeId "list") (nodeId "index") (nodeId "item") } ]
                  ResultNodeId = nodeId "op" }
            let fn: SemanticFunction =
                { MustNotWait = false; Exported = true; Id = StableId.createForTesting<FunctionTag> "function-list-positional"
                  TypeParameters = []
                  DisplayName = "update"; Parameters = []; ResultType = listOfText; RequiredEffects = []; Body = body }
            SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-list-positional") with Functions = [ fn ] }
            |> List.map (fun issue -> issue.Code)
        let emptyText = SemanticNodeKind.ListEmpty textType
        let zero = SemanticNodeKind.Int64Literal 0L
        let word = SemanticNodeKind.StringLiteral "x"
        for name, make in
            [ "ListSetAt", (fun l i x -> SemanticNodeKind.ListSetAt(l, i, x))
              "ListInsertAt", (fun l i x -> SemanticNodeKind.ListInsertAt(l, i, x)) ] do
            Assert.Empty(codes emptyText zero word make)
            Assert.Contains(name + "ItemTypeMismatch", codes emptyText zero zero make)
            Assert.Contains(name + "IndexTypeMismatch", codes emptyText word word make)
            Assert.Contains(name + "ListTypeMismatch", codes word zero word make)
        let remove = fun l i (_: GraphNodeId) -> SemanticNodeKind.ListRemoveAt(l, i)
        Assert.Empty(codes emptyText zero word remove)
        Assert.Contains("ListRemoveAtIndexTypeMismatch", codes emptyText word word remove)
        Assert.Contains("ListRemoveAtListTypeMismatch", codes word zero word remove)

    let private list (items: string list) =
        """{"kind":"list","itemKind":"string","items":[""" + (items |> List.map (fun item -> "\"" + item + "\"") |> String.concat ",") + "]}"

    let private run (call: string -> JsonDocument) fn arguments expected =
        use result = call ("""{"id":"run","op":"runPureByName","function":""" + "\"" + fn + "\"" + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
        let root = result.RootElement
        Assert.True(root.GetProperty("ok").GetBoolean() && root.GetProperty("result").GetProperty("matches").GetBoolean(), fn + ": " + root.ToString())

    let private i64 (value: int) = """{"kind":"int64","value":""" + "\"" + string value + "\"}"
    let private str (value: string) = """{"kind":"string","value":""" + "\"" + value + "\"}"

    /// The reference semantics the runs are checked against.
    let private expectSet (items: string list) index item =
        if index >= 0 && index < items.Length then List.updateAt index item items else items
    let private expectInsert (items: string list) index item = List.insertAt (Math.Clamp(index, 0, items.Length)) item items
    let private expectRemove (items: string list) index =
        if index >= 0 && index < items.Length then List.removeAt index items else items

    [<Fact; Trait("Suite", "Extended")>]
    let ``positional updates follow their index rules and leave the input unchanged under runPure`` () =
        withProgram "run" (fun call _ ->
            // Lists that fit in the tail and lists whose items span trie leaves and the tail.
            let long = [ for i in 0..69 -> "v" + string i ]
            for items in [ []; [ "a" ]; [ "a"; "b"; "c" ]; long ] do
                let n = items.Length
                for index in List.distinct [ -1; 0; n - 1; n; n + 5; 5; 33 ] do
                    run call "setKeep" [ list items; i64 index; str "z" ] (list (expectSet items index "z" @ items))
                    run call "insertKeep" [ list items; i64 index; str "z" ] (list (expectInsert items index "z" @ items))
                    run call "removeKeep" [ list items; i64 index ] (list (expectRemove items index @ items))
                    run call "setTwice" [ list items; i64 index ] (list (expectSet items index "x" @ expectSet items index "y"))
                    run call "insertTwice" [ list items; i64 index ] (list (expectInsert items index "x" @ expectInsert items index "y"))
            for index in [ -1; 0; 1; 30; 31; 40; 67; 68; 73 ] do
                let rest = List.skip 2 long
                run call "shiftedUpdates" [ list long; i64 index ]
                    (list (expectSet rest index "s" @ expectInsert rest index "i" @ expectRemove rest index @ rest)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``a fold applying ListSetAt 50,000 times runs within the run limit`` () =
        withProgram "fold" (fun call _ ->
            let expected = """{"kind":"list","itemKind":"int64","items":[""" + ([ for p in 0..99 -> string (49900 + p) ] |> String.concat ",") + "]}"
            run call "churn" [ i64 50000 ] expected)
