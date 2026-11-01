namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

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

    let private compareStartup directory setup arguments =
        let capture zig =
            setup ()
            let code, errors, replies = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "startup-parity" ] zig arguments requests
            let files =
                Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                |> Array.sort
                |> Array.map (fun file -> Path.GetRelativePath(directory, file), Convert.ToHexString(File.ReadAllBytes file))
            code, errors, replies |> List.map comparable, files
        let expected = capture false
        let actual = capture true
        Assert.Equal(expected, actual)
        expected

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
                let relative = Path.GetRelativePath(Environment.CurrentDirectory, first)
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
                for selectedSource in [ relocated; Path.GetRelativePath(Environment.CurrentDirectory, relocated) ] do
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

    [<Fact; Trait("Suite", "Extended")>]
    let ``Startup upgrade verifies single batch and selection reviews and persists exact pairs`` () =
        withDirectory (fun dir ->
            for record in [ false; true ] do
                let ext = if record then ".nyrec" else ".json"
                let path = Path.Combine(dir, "app" + ext)
                let source = Path.Combine(dir, "new" + ext)
                let old = library "018bcfe5-6801-7000-8000-000000000001"
                let updated = library "018bcfe5-6801-7000-8000-000000000002"
                let local = library "018bcfe5-6801-7000-8000-000000000003"
                let reset selected () =
                    for file in Directory.GetFiles dir do File.Delete file
                    File.WriteAllText(source, encoded record updated)
                    let json = if selected then SnapshotCodec.serializeResolved old local |> unwrap else SnapshotCodec.serialize local |> unwrap
                    File.WriteAllText(path, if record then RecordCodec.ofJson json else json)
                    if selected then File.WriteAllText(path + ".dependency.json", (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) "old.json").ToJsonString())
                    File.WriteAllText(path + ".backends.json", "sentinel")
                let oldId = StableId.value old.Id
                let upgrades = $"""[{{"source":{quote source},"replaces":"{oldId}"}}]"""
                let selection selected = $"""{{"sources":[{quote source}],"replaces":[{if selected then quote oldId else ""}]}}"""
                for selected, flag, payload in [ true, "--upgrades", upgrades; true, "--selection", selection true; false, "--selection", selection false ] do
                    reset selected ()
                    let property = if flag = "--upgrades" then "upgrades" else "selection"
                    let preview = $"""{{"id":"p","op":"previewDependencyUpgrades","{property}":{payload}}}"""
                    let code, _, replies = run false [ "open"; "--snapshot"; path; "--read-only" ] [ preview ]
                    Assert.Equal(0, code)
                    use document = parseDocument replies.Head
                    Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), replies.Head)
                    let fingerprint = document.RootElement.GetProperty("result").GetProperty("reviewFingerprint").GetString()
                    for review in [ fingerprint; "wrong" ] do
                        let code, _, _, _ = compareStartup dir (reset selected) [ "upgrade"; "--snapshot"; path; flag; payload; "--review"; review ]
                        Assert.Equal((if review = fingerprint then 0 else 2), code)
                let fingerprint = SnapshotFingerprint.compute updated |> unwrap |> SnapshotFingerprint.value
                for selectedSource in [ source; Path.GetRelativePath(Environment.CurrentDirectory, source) ] do
                    let code, _, _, _ = compareStartup dir (reset true) [ "upgrade"; "--snapshot"; path; "--dependency"; selectedSource; "--expect"; fingerprint ]
                    Assert.Equal(0, code))
