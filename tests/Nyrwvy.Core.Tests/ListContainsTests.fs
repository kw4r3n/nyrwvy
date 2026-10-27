namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// ListContains(list, item) (post-M2 order 66): Python `item in list` over an equatable item type,
/// compared with ValueEqual and stopping at the first equal item. Never fails.
module ListContainsTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("list-contains-" + name)
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private text = """{"kind":"primitive","primitive":"string"}"""
    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private boolType = """{"kind":"primitive","primitive":"bool"}"""
    let private float64Type = """{"kind":"primitive","primitive":"float64"}"""
    let private listOf item = """{"kind":"builtinGeneric","generic":"list","arguments":[""" + item + "]}"
    let private setOf item = """{"kind":"builtinGeneric","generic":"set","arguments":[""" + item + "]}"
    let private parameter name = """{"kind":"parameter","name":""" + "\"" + name + "\"}"
    let private contains list item = """{"kind":"listContains","list":""" + list + ""","item":""" + item + "}"

    let private create name parameters resultType body =
        """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":""" + "\"" + name + "\""
        + ""","parameters":[""" + (parameters |> List.map (fun (n, t) -> """{"displayName":""" + "\"" + n + "\"" + ""","type":""" + t + "}") |> String.concat ",")
        + """],"resultType":""" + resultType + ""","requiredEffects":[],"body":""" + body + "}"

    /// Authors `has` over List<String> and `hasSet` over List<Set<Int64>> in a fresh session.
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
            commit (create "has" [ "items", listOf text; "item", text ] boolType (contains (parameter "items") (parameter "item")))
            // Sets are equatable by content, so a list of sets can be searched.
            commit (create "hasSet" [ "items", listOf (setOf int64Type); "item", setOf int64Type ] boolType (contains (parameter "items") (parameter "item")))
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
    let ``ListContains type checks, persists and lowers through every layer`` () =
        withProgram "layers" (fun call path ->
            let reject name parameters resultType body expectedCode =
                Assert.Contains(expectedCode, rejection call (create name parameters resultType body))
            reject "floats" [ "items", listOf float64Type; "item", float64Type ] boolType (contains (parameter "items") (parameter "item")) "ListContainsItemTypeNotEquatable"
            reject "wrongItem" [ "items", listOf text; "item", int64Type ] boolType (contains (parameter "items") (parameter "item")) "PureExpressionOperandTypeMismatch"
            reject "notList" [ "items", setOf text; "item", text ] boolType (contains (parameter "items") (parameter "item")) "PureExpressionOperandTypeMismatch"

            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serializeCanonical snapshot |> unwrap "serialize"
            Assert.Contains("\"listContains\"", json)
            Assert.Contains("\"schemaVersion\":19", json)
            let restored = SnapshotCodec.deserialize json |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            Assert.Equal(json, SnapshotCodec.serializeCanonical restored |> unwrap "serialize again")
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            Assert.Contains("\"listContains\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains("\"listContains\"", ExecutionIrSerializer.serialize execution)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            Assert.Contains(".Contains(", source)
            match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":19", "\"schemaVersion\":18")) with
            | Ok _ -> failwith "A schema 18 snapshot with ListContains decoded."
            | Error error -> Assert.Contains("ListContains", sprintf "%A" error)

            // A persisted graph reports its own diagnostics.
            let has = snapshot.Functions |> List.find (fun fn -> fn.DisplayName = "has")
            let replace (parameterId: ParameterId) kind =
                { snapshot with
                    Functions =
                        snapshot.Functions
                        |> List.map (fun fn ->
                            if fn.Id <> has.Id then fn
                            else
                                { fn with
                                    Body =
                                        { fn.Body with
                                            Nodes =
                                                fn.Body.Nodes
                                                |> List.map (fun node ->
                                                    match node.Kind with
                                                    | SemanticNodeKind.ParameterReference reference when reference = parameterId -> { node with Kind = kind }
                                                    | _ -> node) } }) }
            let codes tampered = SnapshotValidator.validate tampered |> List.map (fun issue -> issue.Code)
            Assert.Contains("ListContainsListTypeMismatch", codes (replace has.Parameters.[0].Id (SemanticNodeKind.Int64Literal 1L)))
            Assert.Contains("ListContainsItemTypeMismatch", codes (replace has.Parameters.[1].Id (SemanticNodeKind.Int64Literal 1L))))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ListContains is Python item in list under runPure`` () =
        withProgram "run" (fun call _ ->
            let strings (values: string list) =
                """{"kind":"list","itemKind":"string","items":[""" + (values |> List.map JsonSerializer.Serialize |> String.concat ",") + "]}"
            let str (value: string) = """{"kind":"string","value":""" + JsonSerializer.Serialize value + "}"
            let bool (value: bool) = """{"kind":"bool","value":""" + (if value then "true" else "false") + "}"
            let intSet (values: int list) =
                """{"kind":"set","itemType":""" + int64Type + ""","items":[""" + (values |> List.map (fun v -> """{"kind":"int64","value":""" + "\"" + string v + "\"}") |> String.concat ",") + "]}"
            let sets (values: int list list) =
                """{"kind":"list","itemType":""" + setOf int64Type + ""","items":[""" + (values |> List.map intSet |> String.concat ",") + "]}"
            let run fn arguments expected =
                use result = call ("""{"id":"run","op":"runPureByName","function":""" + "\"" + fn + "\"" + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
                let root = result.RootElement
                Assert.True(root.GetProperty("ok").GetBoolean() && root.GetProperty("result").GetProperty("matches").GetBoolean(), fn + ": " + root.ToString())
            run "has" [ strings [ "a"; "b"; "c" ]; str "b" ] (bool true)
            run "has" [ strings [ "a"; "b"; "c" ]; str "a" ] (bool true)
            run "has" [ strings [ "a"; "b"; "c" ]; str "c" ] (bool true)
            run "has" [ strings [ "a"; "b"; "c" ]; str "d" ] (bool false)
            run "has" [ strings [ "a"; "b"; "c" ]; str "" ] (bool false)
            run "has" [ strings []; str "a" ] (bool false)
            run "has" [ strings [ ""; "b" ]; str "" ] (bool true)
            // Ordinal comparison: a surrogate pair is not its halves, and case matters.
            run "has" [ strings [ "A"; "\U0001F600" ]; str "a" ] (bool false)
            run "has" [ strings [ "A"; "\U0001F600" ]; str "\U0001F600" ] (bool true)
            let many = [ for index in 0 .. 3999 -> $"w{index}" ]
            run "has" [ strings many; str "w3999" ] (bool true)
            run "has" [ strings many; str "w4000" ] (bool false)
            // Sets compare by content regardless of insertion order.
            run "hasSet" [ sets [ [ 1; 2 ]; [ 3 ] ]; intSet [ 2; 1 ] ] (bool true)
            run "hasSet" [ sets [ [ 1; 2 ]; [ 3 ] ]; intSet [ 1 ] ] (bool false)
            run "hasSet" [ sets [ [] ]; intSet [] ] (bool true))
