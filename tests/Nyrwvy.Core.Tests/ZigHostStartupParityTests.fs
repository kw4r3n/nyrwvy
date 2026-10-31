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
        let once zig =
            clear path
            prepare ()
            let before = files path
            let code, errors, answers = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "startup-parity" ] zig arguments input
            Assert.True((code = expectedCode), $"zig={zig}, exit={code}, stderr={errors}")
            if expectedCode = 0 then
                Assert.Equal(seedWarning, errors)
                Assert.Equal(input.Length, answers.Length)
                answers |> List.iter (result >> ignore)
            else Assert.Equal<string option list>(before, files path)
            (code, comparable errors, answers |> List.map comparable), files path
        let expected, actual = once false, once true
        Assert.True((expected = actual), $"args={arguments}\nF#: {expected}\nZig: {actual}")
        actual

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
    let ``Native startup upgrades apply reviewed single batch and selection plans without allocating proposal IDs`` () =
        withDirectory (fun dir ->
            for extension in [ ".json"; ".nyrec" ] do
                let old, updated = library "old", library "new"
                let path, source = Path.Combine(dir, "app" + extension), Path.Combine(dir, "new" + extension)
                save source updated
                let local = Snapshot.empty (sid<SnapshotTag> "app")
                let setup selected =
                    if selected then
                        let json = SnapshotCodec.serializeResolved old local |> unwrap
                        File.WriteAllText(path, if extension = ".nyrec" then RecordCodec.ofJson json else json)
                        File.WriteAllText(path + ".dependency.json", (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "old.json").ToJsonString())
                    else save path local
                let batch = $"""[{{"source":{quoted source},"replaces":"{StableId.value old.Id}"}}]"""
                let selection selected = $"""{{"sources":[{quoted source}],"replaces":{if selected then quoted (StableId.value old.Id) |> sprintf "[%s]" else "[]"}}}"""
                for mode, selected, payload in [ "single", true, ""; "batch", true, batch; "selection", true, selection true; "selection", false, selection false ] do
                    clear path
                    setup selected
                    let preview =
                        match mode with
                        | "single" -> $"""{{"id":"review","op":"previewDependencyUpgrade","source":{quoted source}}}"""
                        | "batch" -> $"""{{"id":"review","op":"previewDependencyUpgrades","upgrades":{payload}}}"""
                        | _ -> $"""{{"id":"review","op":"previewDependencyUpgrades","selection":{payload}}}"""
                    let code, errors, answers = run false [ "open"; "--snapshot"; path; "--read-only" ] [ preview ]
                    Assert.True((code = 0), errors)
                    let reviewed = (result answers.Head).GetProperty(if mode = "single" then "newFingerprint" else "reviewFingerprint").GetString()
                    let args =
                        [ "upgrade"; "--snapshot"; path ] @
                        match mode with
                        | "single" -> [ "--dependency"; source; "--expect"; reviewed ]
                        | "batch" -> [ "--upgrades"; payload; "--review"; reviewed ]
                        | _ -> [ "--selection"; payload; "--review"; reviewed ]
                    let (_, _, replies), _ = compare path (fun () -> setup selected) args requests 0
                    Assert.Equal(StableId.value local.Id, (result replies.Head).GetProperty("snapshotId").GetString())
                    Assert.Equal(3L, StableIdAllocator.seededCount "startup-parity" replies[3])
                    Assert.False(File.Exists(path + ".dependency.json.previous"))
                    compare path (fun () -> setup selected) (args |> List.map (fun value -> if value = reviewed then "stale" else value)) [] 2 |> ignore
                compare path (fun () -> setup false) [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; "unused" ] [] 2 |> ignore
                compare path (fun () -> setup true) [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; "unused"; "--replaces"; "unknown" ] [] 2 |> ignore
                let dependent =
                    { local with Types = [ { Id = sid<TypeTag> "holder"; DisplayName = "Holder"; TypeParameters = []; Exported = true
                                             Kind = SemanticTypeKind.Product [ { Id = sid<FieldTag> "holder-field"; DisplayName = "value"; Type = TypeRef.Named(old.Types.Head.Id, []) } ] } ] }
                let incompatible () =
                    let json = SnapshotCodec.serializeResolved old dependent |> unwrap
                    File.WriteAllText(path, if extension = ".nyrec" then RecordCodec.ofJson json else json)
                    File.WriteAllText(path + ".dependency.json", (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "old.json").ToJsonString())
                save source (Snapshot.empty (sid<SnapshotTag> "removed"))
                clear path
                incompatible ()
                let preview = $"""{{"id":"incompatible","op":"previewDependencyUpgrade","source":{quoted source}}}"""
                let code, errors, answers = run false [ "open"; "--snapshot"; path; "--read-only" ] [ preview ]
                Assert.True((code = 0), errors)
                let review = result answers.Head
                Assert.False(review.GetProperty("compatible").GetBoolean())
                let fingerprint = review.GetProperty("newFingerprint").GetString()
                let args fingerprint = [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; fingerprint ]
                let (_, validation, _), _ = compare path incompatible (args fingerprint) [] 2
                Assert.Contains("WorkspaceValidationFailure:", validation)
                let (_, stale, _), _ = compare path incompatible (args "stale") [] 2
                Assert.Contains("DependencyFailure: The source differs from the reviewed upgrade version.", stale))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Native startup rejects malformed forms with identical usage and holds the same workspace lock`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "app.json")
            let malformed =
                [ [ "pin"; "--snapshot"; path; "--dependency"; "a"; "--dependency"; "b" ]
                  [ "create"; "--snapshot"; path; "--read-only" ]
                  [ "upgrade"; "--snapshot"; path; "--expect"; "f"; "--dependency"; "a" ]
                  for payload in [ "[]"; "{}"; "[{\"source\":\"a\",\"replaces\":\"b\",\"extra\":1}]"; "[{\"source\":\"a\",\"source\":\"b\",\"replaces\":\"c\"}]"; "[{\"source\":null,\"replaces\":\"b\"}]" ] do
                      [ "upgrade"; "--snapshot"; path; "--upgrades"; payload; "--review"; "f" ]
                  for payload in [ "{}"; "[]"; "{\"sources\":[1],\"replaces\":[]}"; "{\"sources\":[],\"replaces\":[],\"extra\":0}" ] do
                      [ "upgrade"; "--snapshot"; path; "--selection"; payload; "--review"; "f" ] ]
            for args in malformed do compare path ignore args [] 2 |> ignore
            save path (Snapshot.empty (sid<SnapshotTag> "locked"))
            for holder in [ false; true ] do
                use session = start holder [ "open"; "--snapshot"; path ]
                let errors = session.StandardError.ReadToEndAsync()
                session.StandardInput.WriteLine status
                session.StandardOutput.ReadLine() |> result |> ignore
                for args in [ [ "pin"; "--snapshot"; path ]; [ "upgrade"; "--snapshot"; path; "--dependency"; "missing"; "--expect"; "f" ] ] do
                    let code, stderr, replies = run (not holder) args []
                    Assert.Equal(2, code)
                    Assert.Equal($"LockUnavailable: {path} is open in another authoring session. Close that session first.", stderr)
                    Assert.Empty replies
                session.StandardInput.Close()
                Assert.True(session.WaitForExit(10000))
                Assert.Equal("", errors.Result.Trim()))

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


    [<Fact; Trait("Suite", "Extended")>]
    let ``Native startup flattens resolved dependency sources and applies two member reviewed upgrades`` () =
        withDirectory (fun dir ->
            for extension in [ ".json"; ".nyrec" ] do
                let old, updated = library "nested-old", library "nested-new"
                let other, nextOther = Snapshot.empty (sid<SnapshotTag> "other-old"), Snapshot.empty (sid<SnapshotTag> "other-new")
                let path, nested, source, second = Path.Combine(dir, "app" + extension), Path.Combine(dir, "nested" + extension), Path.Combine(dir, "new" + extension), Path.Combine(dir, "other-new" + extension)
                let encode json = if extension = ".nyrec" then RecordCodec.ofJson json else json
                let embed dependency provenance = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) provenance
                let nestedLocal = Snapshot.empty (sid<SnapshotTag> "nested-local")
                File.WriteAllText(nested, SnapshotCodec.serializeResolved old nestedLocal |> unwrap |> encode)
                File.WriteAllText(nested + ".dependency.json", (embed old "original.json").ToJsonString())
                let (_, _, created), _ = compare path ignore [ "create"; "--snapshot"; path; "--dependency"; nested ] requests 0
                let selection = (result created[1]).GetProperty("selection")
                Assert.Equal(StableId.value nestedLocal.Id, selection.GetProperty("snapshotId").GetString())
                use retained = JsonDocument.Parse(File.ReadAllText(path + ".dependency.json"))
                let contentKey = if extension = ".nyrec" then "snapshotRecords" else "snapshotJson"
                let flattened = retained.RootElement.GetProperty(contentKey).GetString()
                let decoded = if extension = ".nyrec" then RecordCodec.deserialize flattened |> unwrap else SnapshotCodec.deserialize flattened |> unwrap
                Assert.Single(decoded.Types) |> ignore
                save source updated
                save second nextOther
                let combined = DependencyGraph.combine [ old; other ] |> unwrap
                let local = Snapshot.empty (sid<SnapshotTag> "two-member-local")
                let setup () =
                    File.WriteAllText(path, SnapshotCodec.serializeResolved combined local |> unwrap |> encode)
                    let members = JsonArray()
                    members.Add(embed old "original.json")
                    members.Add(embed other "other.json")
                    let manifest = JsonObject()
                    manifest["dependencies"] <- members
                    File.WriteAllText(path + ".dependency.json", manifest.ToJsonString())
                let batch = $"""[{{"source":{quoted source},"replaces":"{StableId.value old.Id}"}},{{"source":{quoted second},"replaces":"{StableId.value other.Id}"}}]"""
                let selected = $"""{{"sources":[{quoted source},{quoted second}],"replaces":["{StableId.value old.Id}","{StableId.value other.Id}"]}}"""
                for mode, payload in [ "upgrades", batch; "selection", selected ] do
                    clear path
                    setup ()
                    let preview = $"""{{"id":"review","op":"previewDependencyUpgrades","{mode}":{payload}}}"""
                    let code, errors, answers = run false [ "open"; "--snapshot"; path; "--read-only" ] [ preview ]
                    Assert.True((code = 0), errors)
                    let reviewed = (result answers.Head).GetProperty("reviewFingerprint").GetString()
                    let (_, _, replies), _ = compare path setup [ "upgrade"; "--snapshot"; path; "--" + mode; payload; "--review"; reviewed ] requests 0
                    let members = (result replies[1]).GetProperty("selection").GetProperty("dependencies")
                    Assert.Equal(2, members.GetArrayLength())
                    let actualIds = members.EnumerateArray() |> Seq.map (fun memberPin -> memberPin.GetProperty("snapshotId").GetString()) |> Set.ofSeq
                    Assert.Equal<Set<string>>(Set.ofList [ StableId.value updated.Id; StableId.value nextOther.Id ], actualIds))
