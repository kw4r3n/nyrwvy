namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// The startup modes beside `open` (create, pin and the three upgrade forms) on both hosts: each
/// case starts from the same files, and the exit code, stderr, the replies to `status` and
/// `declarations` and every file left in the directory must be the same bytes.
module ZigHostStartupModesParityTests =
    open ZigHostParityTests

    let private sid<'tag> text =
        let bytes = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes("startup-modes-" + text))
        StableId.createForTesting<'tag> (Guid(bytes[0..15]).ToString("D"))
    let private library version exported =
        { Snapshot.empty (sid<SnapshotTag> version) with
            Types = [ { Id = sid<TypeTag> "type"; DisplayName = version; TypeParameters = []; Exported = exported; Kind = SemanticTypeKind.Product [] } ] }
    let private old = library "old" true
    let private updated = library "new" true
    let private hidden = library "hidden" false
    let private other = Snapshot.empty (sid<SnapshotTag> "other")
    /// A local type whose field refers to the dependency's type.
    let private app =
        { Snapshot.empty (sid<SnapshotTag> "app") with
            Types = [ { Id = sid<TypeTag> "holder"; DisplayName = "Holder"; TypeParameters = []; Exported = true
                        Kind = SemanticTypeKind.Product [ { Id = sid<FieldTag> "holder-field"; DisplayName = "value"; Type = TypeRef.Named(old.Types.Head.Id, []) } ] } ] }
    let private fingerprint snapshot = SnapshotFingerprint.compute snapshot |> unwrap |> SnapshotFingerprint.value
    let private encode (path: string) snapshot =
        let json = SnapshotCodec.serialize snapshot |> unwrap
        File.WriteAllText(path, if path.EndsWith ".nyrec" then RecordCodec.ofJson json else json)
    let private quoted (value: string) = JsonSerializer.Serialize value

    /// Library sources beside the workspace, which is `app.json` resolved against `old`.
    let private libraries (dir: string) =
        encode (Path.Combine(dir, "old.json")) old
        encode (Path.Combine(dir, "new.json")) updated
        encode (Path.Combine(dir, "hidden.json")) hidden
        encode (Path.Combine(dir, "other.nyrec")) other
    let private resolved (sidecarPath: string) embedded (dir: string) =
        libraries dir
        let path = Path.Combine(dir, "app.json")
        File.WriteAllText(path, SnapshotCodec.serializeResolved old app |> unwrap)
        let content = if embedded then Some("snapshotJson", SnapshotCodec.serialize old |> unwrap) else None
        File.WriteAllText(path + ".dependency.json", (memberOf old content sidecarPath).ToJsonString())
    let private embedded dir = resolved (Path.Combine(dir, "old.json")) true dir
    let private standalone (dir: string) =
        libraries dir
        File.WriteAllText(Path.Combine(dir, "app.json"), (SnapshotCodec.serialize (Snapshot.empty (sid<SnapshotTag> "app")) |> unwrap).Replace("\"schemaVersion\":24", "\"schemaVersion\":21"))

    let private files (dir: string) =
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        |> Array.map (fun file -> Path.GetRelativePath(dir, file), File.ReadAllText file)
        |> Array.sortWith (fun (x, _) (y, _) -> String.CompareOrdinal(x, y))
        |> Array.toList

    /// Each host on a fresh copy of the same directory (paths are part of manifests, review
    /// fingerprints and messages); returns the F# exit code and stderr after comparing.
    let private compare (dir: string) (setup: string -> unit) (arguments: string -> string list) =
        let requests = [ """{"id":"s","op":"status"}"""; """{"id":"d","op":"declarations"}""" ]
        let runHost zig =
            for entry in Directory.GetFileSystemEntries dir do
                if Directory.Exists entry then Directory.Delete(entry, true) else File.Delete entry
            setup dir
            let arguments = arguments dir
            let code, errors, answers = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "startup-modes" ] zig arguments requests
            arguments, code, errors, answers |> List.map comparable, files dir
        let expected, actual = runHost false, runHost true
        let arguments, code, errors, answers, written = expected
        let _, zigCode, zigErrors, zigAnswers, zigWritten = actual
        let context = $"""{String.Join(" ", arguments)}{"\n"}F#: {code} {errors}{"\n"}Zig: {zigCode} {zigErrors}"""
        Assert.True((code = zigCode), context)
        Assert.True((errors = zigErrors), context)
        Assert.Equal<string list>(answers, zigAnswers)
        Assert.Equal<(string * string) list>(written, zigWritten)
        Assert.DoesNotContain(written, fun (name, _) -> name.EndsWith ".previous" || name.EndsWith ".tmp")
        code, errors

    let private succeeds (code, errors: string) =
        Assert.True((code = 0), errors)
        Assert.Equal(seedWarning, errors)
    let private fails (expected: string) (code, errors: string) =
        Assert.Equal(2, code)
        Assert.StartsWith(seedWarning + "\n" + expected, errors.ReplaceLineEndings "\n")

    /// The review fingerprint the F# host previews for `request` on the files `setup` writes.
    let private review (dir: string) (setup: string -> unit) (request: string) =
        setup dir
        let code, errors, answers = run false [ "open"; "--snapshot"; Path.Combine(dir, "app.json"); "--read-only" ] [ request ]
        Assert.True((code = 0), errors)
        use doc = parseDocument answers.Head
        doc.RootElement.GetProperty("result").GetProperty("reviewFingerprint").GetString()

    [<Fact; Trait("Suite", "Extended")>]
    let ``Create writes the same snapshot sidecar and backends on both hosts`` () =
        withDirectory (fun dir ->
            let create name dependencies = fun dir -> [ "create"; "--snapshot"; Path.Combine(dir, name) ] @ (dependencies |> List.collect (fun d -> [ "--dependency"; Path.Combine(dir, d) ]))
            succeeds (compare dir libraries (create "app.json" []))
            succeeds (compare dir libraries (create "app.nyrec" []))
            succeeds (compare dir libraries (create "app.nyrec" [ "old.json"; "other.nyrec" ]))
            succeeds (compare dir libraries (create "sub/app.json" [ "old.json" ]))
            fails "SnapshotFileAlreadyExists: " (compare dir libraries (create "old.json" []))
            fails "SnapshotFileAlreadyExists: " (compare dir (fun dir -> libraries dir; File.WriteAllText(Path.Combine(dir, "app.json.dependency.json"), "{}")) (create "app.json" []))
            fails "DependencyFailure: The selected dependency is unavailable." (compare dir libraries (create "app.json" [ "missing.json" ]))
            fails "DependencyFailure: Each dependency may be selected only once." (compare dir libraries (fun dir -> create "app.json" [ "old.json" ] dir @ [ "--dependency"; Path.Combine(dir, ".", "old.json") ]))
            fails "Usage: " (compare dir libraries (fun dir -> create "app.json" [] dir @ [ "--dependency" ])))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Pin re-embeds a legacy or relocated selection and refuses other content on both hosts`` () =
        withDirectory (fun dir ->
            let pin source = fun dir -> [ "pin"; "--snapshot"; Path.Combine(dir, "app.json") ] @ (source |> Option.map (fun name -> [ "--dependency"; Path.Combine(dir, name) ]) |> Option.defaultValue [])
            let moved dir = resolved (Path.Combine(dir, "moved", "old.json")) false dir
            let legacy dir = resolved (Path.Combine(dir, "old.json")) false dir
            succeeds (compare dir moved (pin (Some "old.json")))
            succeeds (compare dir legacy (pin None))
            succeeds (compare dir embedded (pin None))
            succeeds (compare dir embedded (pin (Some "old.json")))
            fails "DependencyFailure: The selected dependency is unavailable." (compare dir moved (pin None))
            fails "DependencyFailure: The selected dependency version changed." (compare dir moved (pin (Some "new.json")))
            fails "DependencyFailure: This workspace has no dependency selection to pin." (compare dir standalone (pin None))
            let set dir =
                embedded dir
                let manifest = Nodes.JsonObject()
                let members = Nodes.JsonArray()
                members.Add(memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) (Path.Combine(dir, "old.json")))
                members.Add(memberOf other (Some("snapshotJson", SnapshotCodec.serialize other |> unwrap)) (Path.Combine(dir, "other.nyrec")))
                manifest["dependencies"] <- members
                File.WriteAllText(Path.Combine(dir, "app.json"), SnapshotCodec.serializeResolved (DependencyGraph.combine [ old; other ] |> unwrap) app |> unwrap)
                File.WriteAllText(Path.Combine(dir, "app.json.dependency.json"), manifest.ToJsonString())
            succeeds (compare dir set (pin None))
            fails "DependencyFailure: Pin with a source applies only to a single legacy dependency." (compare dir set (pin (Some "old.json"))))
