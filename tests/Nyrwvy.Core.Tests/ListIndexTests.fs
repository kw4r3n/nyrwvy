namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// ListIndexOf(list, item) and ListFindIndex(source, predicate) (post-M2 order 51): the zero-based
/// position of the first equal or matching item as Option<Int64>. ListLastIndexOf(list, item)
/// (order 53) gives the position of the last equal item.
module ListIndexTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("list-index-" + name)
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private strings = """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"string"}]}"""
    let private floats = """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"float64"}]}"""
    let private text = """{"kind":"primitive","primitive":"string"}"""
    let private optionIndex = """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}"""

    let private create name parameters resultType body =
        """{"id":"create","op":"simulateCreateFunctionByName","displayName":""" + "\"" + name + "\""
        + ""","parameters":[""" + (parameters |> List.map (fun (n, t) -> """{"displayName":""" + "\"" + n + "\"" + ""","type":""" + t + "}") |> String.concat ",")
        + """],"resultType":""" + resultType + ""","requiredEffects":[],"body":""" + body + "}"

    /// Authors `isB`, `findB`, `indexOf` and `lastIndexOf` in a fresh session and passes `call` and the snapshot path on.
    let private withProgram name (body: (string -> JsonDocument) -> string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let call (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            let commit (line: string) =
                use proposal = call line
                Assert.True(proposal.RootElement.GetProperty("ok").GetBoolean(), proposal.RootElement.ToString())
                let proposalId = proposal.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use committed = call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposalId + "\"}")
                Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            commit (create "isB" [ "name", text ] """{"kind":"primitive","primitive":"bool"}"""
                        """{"kind":"stringEqual","left":{"kind":"parameter","name":"name"},"right":{"kind":"stringLiteral","value":"b"}}""")
            commit (create "findB" [ "items", strings ] optionIndex
                        """{"kind":"listFindIndex","source":{"kind":"parameter","name":"items"},"function":"isB"}""")
            commit (create "indexOf" [ "items", strings; "item", text ] optionIndex
                        """{"kind":"listIndexOf","list":{"kind":"parameter","name":"items"},"item":{"kind":"parameter","name":"item"}}""")
            commit (create "lastIndexOf" [ "items", strings; "item", text ] optionIndex
                        """{"kind":"listLastIndexOf","list":{"kind":"parameter","name":"items"},"item":{"kind":"parameter","name":"item"}}""")
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
    let ``ListIndexOf, ListLastIndexOf and ListFindIndex type check, persist and lower through every layer`` () =
        withProgram "layers" (fun call path ->
            Assert.Contains("ListIndexOfItemTypeNotEquatable", rejection call (create "badFloat" [ "items", floats; "item", """{"kind":"primitive","primitive":"float64"}""" ] optionIndex """{"kind":"listIndexOf","list":{"kind":"parameter","name":"items"},"item":{"kind":"parameter","name":"item"}}"""))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejection call (create "badItem" [ "items", strings ] optionIndex """{"kind":"listIndexOf","list":{"kind":"parameter","name":"items"},"item":{"kind":"int64Literal","value":"1"}}"""))
            Assert.Contains("ListLastIndexOfItemTypeNotEquatable", rejection call (create "badLastFloat" [ "items", floats; "item", """{"kind":"primitive","primitive":"float64"}""" ] optionIndex """{"kind":"listLastIndexOf","list":{"kind":"parameter","name":"items"},"item":{"kind":"parameter","name":"item"}}"""))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejection call (create "badSource" [ "item", text ] optionIndex """{"kind":"listFindIndex","source":{"kind":"parameter","name":"item"},"function":"isB"}"""))
            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap "serialize"
            Assert.Contains("\"listIndexOf\"", json)
            Assert.Contains("\"listFindIndex\"", json)
            Assert.Contains("\"listLastIndexOf\"", json)
            let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
            Assert.Contains("list-index-of", nyason)
            Assert.Contains("list-find-index", nyason)
            Assert.Contains("list-last-index-of", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let execution = TypedSemanticIr.project restored |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            Assert.Contains(".IndexOf(", source)
            Assert.Contains(".FindIndex(", source)
            Assert.Contains(".LastIndexOf(", source))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ListIndexOf and ListFindIndex give the first match and ListLastIndexOf the last under runPure`` () =
        withProgram "run" (fun call _ ->
            let list (items: string list) =
                """{"kind":"list","itemKind":"string","items":[""" + (items |> List.map (fun item -> "\"" + item + "\"") |> String.concat ",") + "]}"
            let index (value: int option) =
                match value with
                | Some i -> """{"kind":"option","itemKind":"int64","hasValue":true,"value":""" + "\"" + string i + "\"}"
                | None -> """{"kind":"option","itemKind":"int64","hasValue":false}"""
            let run fn arguments expected =
                use result = call ("""{"id":"run","op":"runPureByName","function":""" + "\"" + fn + "\"" + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
                let root = result.RootElement
                Assert.True(root.GetProperty("ok").GetBoolean() && root.GetProperty("result").GetProperty("matches").GetBoolean(), fn + ": " + root.ToString())
            let long = [ for i in 0..69 -> if i = 3 || i = 40 then "b" else "a" ]
            for items, first, last in [ [], None, None; [ "a" ], None, None; [ "b" ], Some 0, Some 0; [ "a"; "b"; "b" ], Some 1, Some 2; [ "a"; "c"; "b" ], Some 2, Some 2; long, Some 3, Some 40 ] do
                run "findB" [ list items ] (index first)
                run "indexOf" [ list items; """{"kind":"string","value":"b"}""" ] (index first)
                run "lastIndexOf" [ list items; """{"kind":"string","value":"b"}""" ] (index last)
            run "indexOf" [ list [ "x"; ""; "" ]; """{"kind":"string","value":""}""" ] (index (Some 1))
            run "lastIndexOf" [ list [ "x"; ""; "" ]; """{"kind":"string","value":""}""" ] (index (Some 2)))
