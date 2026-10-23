namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module CurrentPersistenceAndScalarTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private withDirectory run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-current-values-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try run root finally Directory.Delete(root, true)

    let private reply session line =
        use document = JsonDocument.Parse(AuthoringProtocol.executeLine session line)
        document.RootElement.Clone()

    let private request session line =
        let response = reply session line
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText())
        response.GetProperty("result")

    let private publish session line =
        let proposal = request session line
        request session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private container name arguments = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.concat "," arguments}]}}"""
    let private identity session name wireType =
        publish session $"""{{"id":"create","op":"simulateCreateFunctionByName","displayName":"{name}","exported":true,"parameters":[{{"displayName":"input","type":{wireType}}}],"resultType":{wireType},"requiredEffects":[],"body":{{"kind":"parameter","name":"input"}}}}"""

    [<Fact>]
    let ``ordinary JSON persistence uses the current schema while v6 fingerprints keep canonical bytes`` () =
        let legacy = """{"schemaVersion":9,"snapshotId":"current-writer-empty","types":[],"effects":[],"capabilities":[],"functions":[],"obligations":[]}"""
        let snapshot = SnapshotCodec.deserialize legacy |> unwrap
        Assert.Equal(legacy, SnapshotCodec.serializeCanonical snapshot |> unwrap)
        let hash domain text =
            Encoding.UTF8.GetBytes(domain + "\u0000" + text) |> SHA256.HashData |> Convert.ToHexString |> fun value -> "sha256:" + value.ToLowerInvariant()
        Assert.Equal(hash "nayamnyam.snapshot-fingerprint.v6" legacy, SnapshotFingerprint.compute snapshot |> unwrap |> SnapshotFingerprint.value)
        Assert.Equal(hash "nayamnyam.semantic-fingerprint.v6" (legacy.Replace("current-writer-empty", "semantic-fingerprint-root")), SemanticFingerprint.compute snapshot |> unwrap |> SemanticFingerprint.value)
        let before = SnapshotFingerprint.compute snapshot, SemanticFingerprint.compute snapshot
        let json = SnapshotCodec.serialize snapshot |> unwrap
        Assert.Equal(SnapshotSchema.current, SnapshotCodec.declaredVersion json |> unwrap)
        for restored in [ SnapshotCodec.deserialize json |> unwrap ] do
            Assert.Equal(before, (SnapshotFingerprint.compute restored, SemanticFingerprint.compute restored))

    [<Fact>]
    let ``a migrated all-exported workspace stays current after commit and a dependency keeps its old digest`` () =
        withDirectory (fun root ->
            let imported = (TestGraph.create()).Snapshot
            let source, local = Path.Combine(root, "source.json"), Path.Combine(root, "local.json")
            File.WriteAllText(source, SnapshotCodec.serializeCanonical imported |> unwrap)
            let original = File.ReadAllText source
            let empty = Snapshot.empty (StableId.createForTesting<SnapshotTag> "current-writer-dependent")
            File.WriteAllText(local, SnapshotCodec.serializeResolvedCanonical imported empty |> unwrap)
            File.WriteAllText(local + ".dependency.json", JsonSerializer.Serialize(
                {| path = source; snapshotId = StableId.value imported.Id
                   fingerprint = SnapshotFingerprint.compute imported |> unwrap |> SnapshotFingerprint.value |}))
            let migration = AuthoringSessionMigration.migrate local None |> unwrap
            Assert.Equal(migration.Before, migration.After)
            use session = AuthoringSession.openExisting local |> unwrap
            identity session "listIdentity" (container "list" [ primitive "string" ])
            Assert.Equal(SnapshotSchema.current, SnapshotCodec.declaredVersion (File.ReadAllText local) |> unwrap)
            let selected = JsonDocument.Parse(File.ReadAllText(local + ".dependency.json"))
            use selected = selected
            Assert.Equal(SnapshotSchema.current, SnapshotCodec.declaredVersion (selected.RootElement.GetProperty("snapshotJson").GetString()) |> unwrap)
            session.Close()
            File.Delete source
            use reopened = AuthoringSession.openExisting local |> unwrap
            Assert.Equal(3, reopened.Declarations().Functions.Length)
            Assert.Equal(SnapshotSchema.baseline, SnapshotCodec.declaredVersion original |> unwrap))

    [<Fact; Trait("Suite", "Extended")>]
    let ``typed scalar containers interoperate with legacy values in both input and expectation`` () =
        withDirectory (fun root ->
            use session = AuthoringSession.createNew (Path.Combine(root, "run.json")) |> unwrap
            let check name typed legacy =
                for argument, expected in [ typed, legacy; legacy, typed ] do
                    let result = request session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{argument}],"expected":{expected}}}"""
                    Assert.True(result.GetProperty("matches").GetBoolean(), result.GetRawText())
                    Assert.Equal(JsonValueKind.Null, result.GetProperty("mismatch").ValueKind)
            for kind, raw in [ "string", JsonSerializer.Serialize("雪\n"); "bool", "true"; "int64", "\"-9223372036854775808\"" ] do
                let itemType = primitive kind
                let value = $"""{{"kind":"{kind}","value":{raw}}}"""
                let listName, optionName, resultName = kind + "List", kind + "Option", kind + "Result"
                identity session listName (container "list" [ itemType ])
                identity session optionName (container "option" [ itemType ])
                identity session resultName (container "result" [ itemType; itemType ])
                for typedItems, legacyItems in [ value, raw; "", "" ] do
                    check listName $"""{{"kind":"list","itemType":{itemType},"items":[{typedItems}]}}""" $"""{{"kind":"list","itemKind":"{kind}","items":[{legacyItems}]}}"""
                check optionName $"""{{"kind":"option","itemType":{itemType},"hasValue":true,"value":{value}}}""" $"""{{"kind":"option","itemKind":"{kind}","hasValue":true,"value":{raw}}}"""
                check optionName $"""{{"kind":"option","itemType":{itemType},"hasValue":false}}""" $"""{{"kind":"option","itemKind":"{kind}","hasValue":false}}"""
                for isOk in [ "true"; "false" ] do
                    check resultName $"""{{"kind":"result","okType":{itemType},"errorType":{itemType},"isOk":{isOk},"value":{value}}}""" $"""{{"kind":"result","okKind":"{kind}","errorKind":"{kind}","isOk":{isOk},"value":{raw}}}"""
            let textType, boolType = primitive "string", primitive "bool"
            identity session "mixedResult" (container "result" [ textType; boolType ])
            check "mixedResult"
                $"""{{"kind":"result","okType":{textType},"errorType":{boolType},"isOk":true,"value":{{"kind":"string","value":"ok"}}}}"""
                """{"kind":"result","okKind":"string","errorKind":"bool","isOk":true,"value":"ok"}"""
            check "mixedResult"
                $"""{{"kind":"result","okType":{textType},"errorType":{boolType},"isOk":false,"value":{{"kind":"bool","value":false}}}}"""
                """{"kind":"result","okKind":"string","errorKind":"bool","isOk":false,"value":false}"""
            let optionInt = container "option" [ primitive "int64" ]
            identity session "nested" (container "list" [ optionInt ])
            let typed = $"""{{"kind":"list","itemType":{optionInt},"items":[{{"kind":"option","itemType":{primitive "int64"},"hasValue":true,"value":{{"kind":"int64","value":"2"}}}}]}}"""
            let legacy = $"""{{"kind":"list","itemType":{optionInt},"items":[{{"kind":"option","itemKind":"int64","hasValue":true,"value":"2"}}]}}"""
            check "nested" typed legacy)

    /// Fast-suite guard for the typed scalar acceptance path; the Extended case covers every kind and shape.
    [<Fact>]
    let ``a typed scalar List and Result run and match their legacy forms`` () =
        withDirectory (fun root ->
            use session = AuthoringSession.createNew (Path.Combine(root, "run.json")) |> unwrap
            let text = primitive "string"
            identity session "strings" (container "list" [ text ])
            identity session "textResult" (container "result" [ text; text ])
            let check name typed legacy =
                for argument, expected in [ typed, legacy; legacy, typed ] do
                    let result = request session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{argument}],"expected":{expected}}}"""
                    Assert.True(result.GetProperty("matches").GetBoolean(), result.GetRawText())
            check "strings"
                $"""{{"kind":"list","itemType":{text},"items":[{{"kind":"string","value":"a"}}]}}"""
                """{"kind":"list","itemKind":"string","items":["a"]}"""
            check "textResult"
                $"""{{"kind":"result","okType":{text},"errorType":{text},"isOk":false,"value":{{"kind":"string","value":"e"}}}}"""
                """{"kind":"result","okKind":"string","errorKind":"string","isOk":false,"value":"e"}""")

    [<Fact>]
    let ``typed scalar payload mismatches and mixed legacy fields fail before execution`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "invalid.json")
            use session = AuthoringSession.createNew path |> unwrap
            identity session "strings" (container "list" [ primitive "string" ])
            let before = File.ReadAllText path
            let invalid = $"""{{"kind":"list","itemType":{primitive "string"},"items":[{{"kind":"bool","value":true}}]}}"""
            let response = reply session $"""{{"id":"bad","op":"runPureByName","function":"strings","arguments":[{invalid}]}}"""
            Assert.False(response.GetProperty("ok").GetBoolean())
            Assert.Equal("RunContainerTypeMismatch", response.GetProperty("error").GetProperty("code").GetString())
            Assert.Contains("items[0]", response.GetRawText())
            let mixed = $"""{{"kind":"list","itemType":{primitive "string"},"itemKind":"string","items":[]}}"""
            let response = reply session $"""{{"id":"bad","op":"runPureByName","function":"strings","arguments":[{mixed}]}}"""
            Assert.False(response.GetProperty("ok").GetBoolean())
            Assert.Equal("ProtocolError", response.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal(before, File.ReadAllText path))
