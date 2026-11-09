namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core
open Xunit

module ZigHostStartupCommandParityTests =
    open ZigHostParityTests

    let private quoted (text: string) = JsonSerializer.Serialize text
    let private sid<'tag> name =
        let bytes = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes("startup-" + name))
        StableId.createForTesting<'tag> (Guid(bytes[0..15]).ToString("D"))
    let private library version =
        { Snapshot.empty (sid<SnapshotTag> version) with
            Types = [ { Id = sid<TypeTag> "type"; DisplayName = version; TypeParameters = []; Exported = true; Kind = SemanticTypeKind.Product [] } ] }
    let private save (path: string) snapshot =
        let json = SnapshotCodec.serialize snapshot |> unwrap
        File.WriteAllText(path, if Path.GetExtension(path) = ".nyrec" then RecordCodec.ofJson json else json)
    let private status = """{"id":"status","op":"status"}"""
    let private allocate = """{"id":"allocate","op":"simulateCreateType","displayName":"AfterStartup","typeKind":{"kind":"product","fields":[]}}"""
    let private requests = [ status; """{"id":"dependency","op":"dependency"}"""; """{"id":"declarations","op":"declarations"}"""; allocate ]
    let private result reply =
        use document = parseDocument reply
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("result").Clone()
    let private bytes path = if File.Exists path then Some(Convert.ToBase64String(File.ReadAllBytes path)) else None
    let private files path = [ ""; ".dependency.json"; ".dependency.json.previous"; ".backends.json"; ".previous"; ".backends.json.previous" ] |> List.map (fun suffix -> bytes (path + suffix))
    let private clear path =
        for suffix in [ ""; ".dependency.json"; ".dependency.json.previous"; ".backends.json"; ".previous"; ".backends.json.previous" ] do
            if File.Exists(path + suffix) then File.Delete(path + suffix)
    let private compare path prepare arguments input expectedCode =
        clear path
        prepare ()
        let before = files path
        let code, errors, answers = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "startup-parity" ] arguments input
        Assert.True((code = expectedCode), $"exit={code}, stderr={errors}")
        if expectedCode = 0 then
            Assert.Equal(seedWarning, errors)
            Assert.Equal(input.Length, answers.Length)
            answers |> List.iter (result >> ignore)
        else Assert.Equal<string option list>(before, files path)
        (code, comparable errors, answers |> List.map comparable), files path

    [<Fact; Trait("Suite", "Extended")>]
    let ``Native create matches snapshots sidecars dependency selection and allocator consumption`` () =
        withDirectory (fun dir ->
            for extension in [ ".json"; ".nyrec" ] do
                let source, second = Path.Combine(dir, "library" + extension), Path.Combine(dir, "second" + extension)
                save source (library "library")
                save second (Snapshot.empty (sid<SnapshotTag> "second"))
                let path = Path.Combine(dir, "nested", "app" + extension)
                for dependencies in [ []; [ source ]; [ source; second ] ] do
                    let args = [ "create"; "--snapshot"; path ] @ (dependencies |> List.collect (fun dependency -> [ "--dependency"; dependency ]))
                    let (_, _, answers), persisted = compare path ignore args requests 0
                    let snapshotId = (result answers[0]).GetProperty("snapshotId").GetString()
                    Assert.Equal(StableIdAllocator.seededPayload "startup-parity" 1L, snapshotId)
                    Assert.Equal(Some(Convert.ToBase64String(Text.Encoding.UTF8.GetBytes "{\"targets\": [\"csharp\"]}\n")), persisted[3])
                    Assert.Equal(0, (result answers[0]).GetProperty("pendingProposalCount").GetInt32())
                    Assert.Equal(4L, StableIdAllocator.seededCount "startup-parity" answers[3])
                let preset = "{\"targets\": [\"zig\"]}\n"
                compare path (fun () -> Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore; File.WriteAllText(path + ".backends.json", preset)) [ "create"; "--snapshot"; path ] requests 0 |> ignore
                Assert.Equal(preset, File.ReadAllText(path + ".backends.json"))
                for existing in [ ""; ".dependency.json" ] do
                    compare path (fun () -> File.WriteAllText(path + existing, "existing")) [ "create"; "--snapshot"; path; "--dependency"; source ] [] 2 |> ignore
                compare path ignore [ "create"; "--snapshot"; path; "--dependency"; source; "--dependency"; source ] [] 2 |> ignore
                compare path ignore [ "create"; "--snapshot"; path; "--dependency"; Path.Combine(dir, "missing.json") ] [] 2 |> ignore
                let restartSource = Path.Combine(dir, "restart-library" + extension)
                let observed = StableId.createForTesting<SnapshotTag> (StableIdAllocator.seededPayload "startup-parity" 90L)
                save restartSource { (library "restart") with Id = observed }
                let (_, _, restarted), _ = compare path ignore [ "create"; "--snapshot"; path; "--dependency"; restartSource ] requests 0
                Assert.Equal(StableIdAllocator.seededPayload "startup-parity" 91L, (result restarted.Head).GetProperty("snapshotId").GetString())
                Assert.Equal(94L, StableIdAllocator.seededCount "startup-parity" restarted[3]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Native pin retains exact graph and recovers legacy pins only from exact content`` () =
        withDirectory (fun dir ->
            for extension in [ ".json"; ".nyrec" ] do
                let dependency = library "original"
                let path, source, copy = Path.Combine(dir, "app" + extension), Path.Combine(dir, "original" + extension), Path.Combine(dir, "copy" + extension)
                save source dependency
                save copy dependency
                let local = Snapshot.empty (sid<SnapshotTag> "app")
                let localJson = SnapshotCodec.serializeResolved dependency local |> unwrap
                let sourceText = if extension = ".nyrec" then RecordCodec.ofJson localJson else localJson
                let setup embedded missing =
                    File.WriteAllText(path, sourceText)
                    let manifest = memberOf dependency (if embedded then Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap) else None) (if missing then Path.Combine(dir, "lost.json") else source)
                    File.WriteAllText(path + ".dependency.json", manifest.ToJsonString())
                for embedded, missing, overridePath in [ false, false, None; false, true, Some copy; true, false, None; true, true, Some copy ] do
                    let args = [ "pin"; "--snapshot"; path ] @ (overridePath |> Option.toList |> List.collect (fun value -> [ "--dependency"; value ]))
                    compare path (fun () -> setup embedded missing) args requests 0 |> ignore
                    Assert.Equal(sourceText, File.ReadAllText path)
                    use manifest = JsonDocument.Parse(File.ReadAllText(path + ".dependency.json"))
                    let expectedKey = if extension = ".nyrec" && (not embedded || overridePath.IsSome) then "snapshotRecords" else "snapshotJson"
                    Assert.True(fst (manifest.RootElement.TryGetProperty expectedKey))
                let wrong = Path.Combine(dir, "wrong" + extension)
                save wrong (library "wrong")
                for overridePath in [ None; Some wrong; Some(Path.Combine(dir, "missing.json")) ] do
                    let args = [ "pin"; "--snapshot"; path ] @ (overridePath |> Option.toList |> List.collect (fun value -> [ "--dependency"; value ]))
                    compare path (fun () -> setup false true) args [] 2 |> ignore
                compare path (fun () -> save path local) [ "pin"; "--snapshot"; path ] [] 2 |> ignore
                for white in [ "\u00A0"; "\u2003" ] do
                    for field in [ "path"; "fingerprint"; "snapshotId" ] do
                        let malformed () =
                            setup true false
                            let manifest = JsonNode.Parse(File.ReadAllText(path + ".dependency.json"))
                            manifest[field] <- JsonValue.Create white
                            File.WriteAllText(path + ".dependency.json", manifest.ToJsonString())
                        let (_, error, _), _ = compare path malformed [ "pin"; "--snapshot"; path; "--dependency"; copy ] [] 2
                        Assert.Contains("Dependency manifest fields must be non-blank strings.", error)
                    let blankContent () =
                        setup true false
                        let manifest = JsonNode.Parse(File.ReadAllText(path + ".dependency.json"))
                        manifest["snapshotJson"] <- JsonValue.Create white
                        File.WriteAllText(path + ".dependency.json", manifest.ToJsonString())
                    let (_, error, _), _ = compare path blankContent [ "pin"; "--snapshot"; path ] [] 2
                    Assert.Contains("The embedded dependency is missing.", error))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Native startup preserves pin error ordering and refuses symbolic link writes`` () =
        withDirectory (fun dir ->
            let old, updated = library "old", library "new"
            let path, source = Path.Combine(dir, "app.json"), Path.Combine(dir, "new.json")
            save source updated
            let local = Snapshot.empty (sid<SnapshotTag> "app")
            let setup () =
                File.WriteAllText(path, SnapshotCodec.serializeResolved old local |> unwrap)
                File.WriteAllText(path + ".dependency.json", "{}")
                File.WriteAllText(path + ".dependency.json.previous", (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "old.json").ToJsonString())
            for args in [ [ "pin"; "--snapshot"; path ]; [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; "stale" ] ] do
                compare path setup args [] 2 |> ignore
            for primitive in [ "\"invalid\""; "null"; "42"; "[]"; "true" ] do
                let malformed () =
                    File.WriteAllText(path, SnapshotCodec.serializeResolved old local |> unwrap)
                    File.WriteAllText(path + ".dependency.json", primitive)
                let (_, error, _), _ = compare path malformed [ "pin"; "--snapshot"; path ] [] 2
                Assert.Contains("DependencyFailure: Invalid dependency manifest or source path.", error)
            let valid = (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "old.json").ToJsonString()
            let duplicate = valid.TrimEnd('}') + ",\"path\":\"duplicate\"}"
            let emptyEmbedded = JsonNode.Parse valid
            emptyEmbedded["snapshotJson"] <- JsonValue.Create ""
            for first, expected in [ emptyEmbedded.ToJsonString(), "The embedded dependency is missing."; valid, "Invalid dependency manifest." ] do
                let malformed () =
                    File.WriteAllText(path, SnapshotCodec.serializeResolved old local |> unwrap)
                    File.WriteAllText(path + ".dependency.json", "{\"dependencies\":[" + first + "," + duplicate + "]}")
                let (_, error, _), _ = compare path malformed [ "pin"; "--snapshot"; path ] [] 2
                Assert.Contains(expected, error)
            if not (OperatingSystem.IsWindows()) then
                let real = Path.Combine(dir, "real.json")
                save real local
                for mode in [ "pin"; "upgrade" ] do
                    let args = [ mode; "--snapshot"; path ] @ (if mode = "upgrade" then [ "--dependency"; source; "--expect"; "f" ] else [])
                    compare path (fun () -> File.CreateSymbolicLink(path, real) |> ignore) args [] 2 |> ignore
                    Assert.Equal(SnapshotCodec.serialize local |> unwrap, File.ReadAllText real))


module ZigHostStartupCliParityTests =
    open ZigHostParityTests

    let private quote (text: string) = JsonSerializer.Serialize text
    let private library name = Snapshot.empty (StableId.createForTesting<SnapshotTag> name)
    let private encoded record snapshot =
        let json = SnapshotCodec.serialize snapshot |> unwrap
        if record then RecordCodec.ofJson json else json
    let private requests =
        [ """{"id":"status","op":"status"}"""
          """{"id":"dependency","op":"dependency"}"""
          """{"id":"create","op":"simulateCreateType","displayName":"Next","typeKind":{"kind":"product","fields":[]}}""" ]

    /// `path` relative to the working directory as a fixed 11 ".." climbs to the root plus the rooted path, so the
    /// argument does not depend on where the checkout or the temporary directory lives (".." above the root stays at the root).
    /// shortcut: assumes a working directory at most 11 deep; deeper ones get as many ".." as they need.
    let private relativeFromCwd (path: string) =
        let depth = Environment.CurrentDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length
        String.replicate (max 11 depth) (".." + string Path.DirectorySeparatorChar) + path.Substring((Path.GetPathRoot path).Length)

    let private compareStartup directory setup arguments =
        setup ()
        let code, errors, replies = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "startup-parity" ] arguments requests
        let files =
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            |> Array.sort
            |> Array.map (fun file -> Path.GetRelativePath(directory, file), Convert.ToHexString(File.ReadAllBytes file))
        code, errors, replies |> List.map comparable, files

    [<Fact; Trait("Suite", "Extended")>]
    let ``Startup create preserves sources manifests backend declarations allocator and no overwrite`` () =
        withDirectory (fun dir ->
            for record in [ false; true ] do
                let ext = if record then ".nyrec" else ".json"
                let path = Path.Combine(dir, "nested", "app" + ext)
                let first = Path.Combine(dir, "first" + ext)
                let second = Path.Combine(dir, "second" + ext)
                let reset () =
                    for file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories) do File.Delete file
                    let dependency = { library "018bcfe5-6801-7000-8000-000000000001" with ParentId = Some(StableId.createForTesting<SnapshotTag> "018bcfe5-6801-7000-8000-000000000000") }
                    File.WriteAllText(first, encoded record dependency)
                    File.WriteAllText(second, encoded record (library "018bcfe5-6801-7000-8000-000000000002"))
                let relative = relativeFromCwd first
                for dependencies in [ []; [ "--dependency"; first ]; [ "--dependency"; relative ]; [ "--dependency"; first; "--dependency"; second ]; [ "--dependency"; first; "--dependency"; relative ] ] do
                    let code, _, replies, _ = compareStartup dir reset ([ "create"; "--snapshot"; path ] @ dependencies)
                    Assert.Equal((if dependencies.Length = 4 && Path.GetFullPath(dependencies[1]) = Path.GetFullPath(dependencies[3]) then 2 else 0), code)
                    if code = 0 then Assert.Equal(requests.Length, replies.Length)
                let existing () = reset (); Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore; File.WriteAllText(path, "original"); File.WriteAllText(path + ".backends.json", "keep")
                let code, _, _, _ = compareStartup dir existing [ "create"; "--snapshot"; path ]
                Assert.Equal(2, code)
                Assert.Equal("original", File.ReadAllText path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Startup pin recovers exact relocated legacy selections without rewriting snapshots or backend files`` () =
        withDirectory (fun dir ->
            for record in [ false; true ] do
                let ext = if record then ".nyrec" else ".json"
                let path = Path.Combine(dir, "app" + ext)
                let original = Path.Combine(dir, "original" + ext)
                let relocated = Path.Combine(dir, "relocated-" + StableIdAllocator.seededPayload "startup-parity" 9000L + ext)
                let imported = library "018bcfe5-6801-7000-8000-000000000001"
                let local = library "018bcfe5-6801-7000-8000-000000000002"
                let source = SnapshotCodec.serializeResolved imported local |> unwrap
                let reset () =
                    for file in Directory.GetFiles dir do File.Delete file
                    File.WriteAllText(path, if record then RecordCodec.ofJson source else source)
                    File.WriteAllText(path + ".dependency.json", (memberOf imported None original).ToJsonString())
                    File.WriteAllText(path + ".backends.json", "sentinel")
                    File.WriteAllText(relocated, encoded record imported)
                for selectedSource in [ relocated; relativeFromCwd relocated ] do
                    let code, _, replies, _ = compareStartup dir reset [ "pin"; "--snapshot"; path; "--dependency"; selectedSource ]
                    Assert.Equal(0, code)
                    Assert.Equal(requests.Length, replies.Length)
                Assert.Equal((if record then RecordCodec.ofJson source else source), File.ReadAllText path)
                let invalid () = reset (); File.WriteAllText(relocated, encoded record (library "018bcfe5-6801-7000-8000-000000000003"))
                let code, _, _, _ = compareStartup dir invalid [ "pin"; "--snapshot"; path; "--dependency"; relocated ]
                Assert.Equal(2, code)
                let noSelection () = reset (); File.WriteAllText(path, encoded record local); File.Delete(path + ".dependency.json")
                let code, _, _, _ = compareStartup dir noSelection [ "pin"; "--snapshot"; path ]
                Assert.Equal(2, code))
