namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

module ZigHostDependencyUpgradeParityTests =
    open ZigHostParityTests

    let private sid<'tag> text =
        let bytes = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes("dependency-preview-" + text))
        StableId.createForTesting<'tag> (Guid(bytes[0..15]).ToString("D"))
    let private library version =
        { Snapshot.empty (sid<SnapshotTag> version) with
            Types = [ { Id = sid<TypeTag> "type"; DisplayName = version; TypeParameters = []; Exported = true; Kind = SemanticTypeKind.Product [] } ] }
    let private quoted (value: string) = JsonSerializer.Serialize value
    let private success reply =
        use doc = parseDocument reply
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)
        doc.RootElement.GetProperty("result").Clone()

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency previews preserve incompatible visibility generic signatures nested imports and source diagnostics`` () =
        withDirectory (fun dir ->
            let old =
                let original = library "old"
                let fields: SemanticField list =
                    [ { Id = sid<FieldTag> "first"; DisplayName = "first"; Type = TypeRef.Primitive PrimitiveType.Int64 }
                      { Id = sid<FieldTag> "last"; DisplayName = "last"; Type = TypeRef.Primitive PrimitiveType.Int64 } ]
                { original with Types = [ { original.Types.Head with Kind = SemanticTypeKind.Product fields } ] }
            let local =
                { Snapshot.empty (sid<SnapshotTag> "app") with
                    Types = [ { Id = sid<TypeTag> "holder"; DisplayName = "Holder"; TypeParameters = []; Exported = true
                                Kind = SemanticTypeKind.Product [ { Id = sid<FieldTag> "holder-field"; DisplayName = "value"; Type = TypeRef.Named(old.Types.Head.Id, []) } ] } ] }
            let path = Path.Combine(dir, "app.json")
            File.WriteAllText(path, SnapshotCodec.serializeResolved old local |> unwrap)
            File.WriteAllText(path + ".dependency.json", (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "old.json").ToJsonString())
            let save name snapshot =
                let target = Path.Combine(dir, name + ".json")
                File.WriteAllText(target, SnapshotCodec.serialize snapshot |> unwrap)
                target
            let hidden = save "hidden" { old with Id = sid<SnapshotTag> "hidden"; Types = [ { old.Types.Head with Exported = false } ] }
            let removed = save "removed" (Snapshot.empty (sid<SnapshotTag> "removed"))
            let generic = save "generic" { old with Id = sid<SnapshotTag> "generic"; Types = [ { old.Types.Head with TypeParameters = [ sid<TypeParameterTag> "T", "T" ] } ] }
            let invalid = Path.Combine(dir, "invalid.json")
            let invalidRoot = System.Text.Json.Nodes.JsonNode.Parse(SnapshotCodec.serializeResolved old local |> unwrap).AsObject()
            invalidRoot.Remove("dependency") |> ignore
            File.WriteAllText(invalid, invalidRoot.ToJsonString())
            let resolvedInvalid = Path.Combine(dir, "resolved-invalid.json")
            let invalidResolvedRoot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText path)
            invalidResolvedRoot.["types"].[0].["fields"].[0].["type"].["typeId"] <- System.Text.Json.Nodes.JsonValue.Create(StableId.value (sid<TypeTag> "missing"))
            File.WriteAllText(resolvedInvalid, invalidResolvedRoot.ToJsonString())
            File.Copy(path + ".dependency.json", resolvedInvalid + ".dependency.json")
            let broken = Path.Combine(dir, "broken.json")
            File.WriteAllText(broken, "{\"schemaVersion\":")
            let same = save "same" old
            let source = Path.Combine(dir, "nested.json")
            let nestedLocal = Snapshot.empty (sid<SnapshotTag> "nested")
            File.WriteAllText(source, SnapshotCodec.serializeResolved old nestedLocal |> unwrap)
            File.WriteAllText(source + ".dependency.json", (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "nested-provenance.json").ToJsonString())
            let reordered = save "reordered" { old with Id = sid<SnapshotTag> "reordered" }
            let root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText reordered)
            let fields = (root.["types"].[0].["fields"]).AsArray()
            let reversed = fields |> Seq.map (fun field -> field.DeepClone()) |> Seq.rev |> Array.ofSeq
            fields.Clear()
            for field in reversed do fields.Add field
            File.WriteAllText(reordered, root.ToJsonString())
            let preview source = $"""{{"id":"p","op":"previewDependencyUpgrade","source":{quoted source}}}"""
            let declarations = """{"id":"d","op":"declarations"}"""
            let requests =
                [ yield declarations
                  for source in [ hidden; removed; generic; invalid; broken; same; path; source; reordered; resolvedInvalid ] do
                      yield preview source
                      yield declarations
                  yield $"""{{"id":"duplicate","op":"previewDependencyUpgrades","upgrades":[{{"source":{quoted hidden},"source":{quoted removed},"replaces":"{StableId.value old.Id}"}}]}}"""
                  yield declarations
                  yield $"""{{"id":"duplicate","op":"previewDependencyUpgrades","selection":{{"sources":[{quoted hidden}],"sources":[{quoted removed}],"replaces":[]}}}}"""
                  yield declarations ]
            let runHost zig =
                let code, errors, answers = run zig [ "open"; "--snapshot"; path; "--read-only" ] requests
                Assert.True((code = 0), $"host zig={zig}, exit={code}, stderr={errors}")
                Assert.Equal("", errors)
                Assert.Equal(requests.Length, answers.Length)
                answers
            let expected, actual = runHost false, runHost true
            for request, (left, right) in List.zip requests (List.zip expected actual) do Assert.True((comparable left = comparable right), $"{request}\nF#: {left}\nZig: {right}")
            for index in [ 1; 3; 5 ] do Assert.False((success expected[index]).GetProperty("compatible").GetBoolean())
            success expected[15] |> ignore
            Assert.Equal(1, (success expected[17]).GetProperty("changed").GetArrayLength())
            for index in [ 7; 9; 11; 13; 19; 21; 23 ] do
                use doc = parseDocument expected[index]
                Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), expected[index]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency previews invalidate externally changed snapshots and pins before loading new sources`` () =
        withDirectory (fun dir ->
            let old = library "old"
            let path = Path.Combine(dir, "app.json")
            let saved = SnapshotCodec.serializeResolved old (Snapshot.empty (sid<SnapshotTag> "app")) |> unwrap
            let manifest = (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "provenance-only.json").ToJsonString()
            let request = $"""{{"id":"p","op":"previewDependencyUpgrade","source":{quoted (Path.Combine(dir, "missing.json"))}}}"""
            for sidecar in [ false; true ] do
                let transcript zig =
                    File.WriteAllText(path, saved)
                    File.WriteAllText(path + ".dependency.json", manifest)
                    use host = start zig [ "open"; "--snapshot"; path; "--read-only" ]
                    let send request =
                        host.StandardInput.WriteLine(request: string)
                        host.StandardInput.Flush()
                        let reading = host.StandardOutput.ReadLineAsync()
                        Assert.True(reading.Wait silence, "Host did not answer.")
                        Assert.NotNull reading.Result
                        comparable reading.Result
                    success (send """{"id":"s","op":"status"}""") |> ignore
                    File.AppendAllText((if sidecar then path + ".dependency.json" else path), "\n")
                    let rejected = send request
                    let invalidated = send request
                    let declarations = send """{"id":"d","op":"declarations"}"""
                    success declarations |> ignore
                    host.StandardInput.Close()
                    Assert.True(host.WaitForExit(int silence.TotalMilliseconds))
                    Assert.Equal(0, host.ExitCode)
                    Assert.Equal("", host.StandardError.ReadToEnd())
                    [ rejected; invalidated; declarations ]
                let expected, actual = transcript false, transcript true
                Assert.Equal<string list>(expected, actual)
                use first = parseDocument expected[0]
                Assert.Equal((if sidecar then "DependencyFailure" else "ExternalModification"), first.RootElement.GetProperty("error").GetProperty("code").GetString())
                use second = parseDocument expected[1]
                Assert.Equal("SessionInvalidated", second.RootElement.GetProperty("error").GetProperty("code").GetString()))
