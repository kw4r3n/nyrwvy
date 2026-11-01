namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Persistence in the record form (docs/decisions/snapshot-record-format.md, step B): the form
/// follows the file extension, `.nyrec` is written and read as records, a `.nyrec` dependency is
/// embedded under `snapshotRecords`, `migrate --to` converts a JSON workspace into a new `.nyrec`
/// one with unchanged fingerprints, and the viewer exports from record text.
[<Collection("Session storage faults")>]
module RecordPersistenceTests =
    // The record form accepts only full UUID IDs (ADR open point 4), never testing names.
    let private id<'tag> name = StableIdAllocator.derived<'tag> ("record-persistence-" + name)
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private withDirectory run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-record-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try run root
        finally Directory.Delete(root, true)

    let private assertLockReleased path =
        (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)).Dispose()

    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private status (session: AuthoringSession) = session.Status() |> unwrap "status"
    let private isRecordText (text: string) = text.StartsWith "snapshot snapshot:"
    let private sidecarKeys path =
        use document = JsonDocument.Parse(File.ReadAllText(path + ".dependency.json"))
        document.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toList

    /// A complete library with one exported function `name : Int64 -> Int64`.
    let private library name =
        let parameter = id<ParameterTag> (name + "-input")
        let node = id<GraphNodeTag> (name + "-node")
        { Snapshot.empty (id<SnapshotTag> name) with
            Functions =
                [ { MustNotWait = false; Exported = true; Id = id<FunctionTag> name; TypeParameters = []; DisplayName = name
                    Parameters = [ { Id = parameter; DisplayName = "input"; Type = int64Type } ]
                    ResultType = int64Type; RequiredEffects = []
                    Body = { Nodes = [ { Id = node; Kind = SemanticNodeKind.ParameterReference parameter } ]; ResultNodeId = node } } ] }

    let private commitLiteral (session: AuthoringSession) name =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (status session).SnapshotId; TypeParameters = None
              DisplayName = name; Parameters = []; ResultType = int64Type; RequiredEffects = []
              Body = ExpressionRecipe.Int64Literal 1L }
        let proposal = session.SimulateCreateFunction request |> unwrap "simulate"
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    /// A local function whose body calls `callee`, which lives in the dependency.
    let private commitCall (session: AuthoringSession) name (callee: string) =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (status session).SnapshotId; TypeParameters = None
              DisplayName = name; Parameters = [ { DisplayName = "x"; Type = int64Type } ]; ResultType = int64Type; RequiredEffects = []
              Body = ExpressionRecipe.DirectCall(id<FunctionTag> callee, [ id<ParameterTag> (callee + "-input"), ExpressionRecipe.ParameterSlot 0 ]) }
        let proposal = session.SimulateCreateFunction request |> unwrap "simulate call"
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit call" |> ignore

    [<Fact>]
    let ``a nyrec workspace is created, committed and reopened as records`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "workspace.nyrec")
            let fingerprint =
                use session = AuthoringSession.createNew path |> unwrap "create"
                Assert.True(isRecordText (File.ReadAllText path), "create wrote JSON to a .nyrec path")
                commitLiteral session "one"
                Assert.True(isRecordText (File.ReadAllText path), "commit wrote JSON to a .nyrec path")
                (status session).SnapshotFingerprint
            assertLockReleased path
            let bytes = File.ReadAllBytes path
            let reopened =
                use session = AuthoringSession.openExisting path |> unwrap "reopen"
                Assert.Equal<string list>([ "one" ], session.Declarations().Functions |> List.map (fun fn -> fn.DisplayName))
                (status session).SnapshotFingerprint
            Assert.Equal(fingerprint, reopened)
            Assert.Equal<byte[]>(bytes, File.ReadAllBytes path)
            assertLockReleased path
            // A record decode failure names its line and column.
            File.WriteAllText(path, File.ReadAllText(path).Replace("schema=", "schema=x"))
            match AuthoringSession.openExisting path with
            | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidShape message)) ->
                Assert.StartsWith("line 1, column ", message)
            | other -> failwithf "Corrupt record text was accepted: %A" other
            assertLockReleased path)

    [<Fact>]
    let ``a nyrec dependency is embedded as snapshotRecords and a JSON one as snapshotJson`` () =
        withDirectory (fun root ->
            let recordLibrary = Path.Combine(root, "records.nyrec")
            File.WriteAllText(recordLibrary, RecordCodec.serialize (library "rec") |> unwrap "serialize records")
            let jsonLibrary = Path.Combine(root, "json.json")
            File.WriteAllText(jsonLibrary, SnapshotCodec.serialize (library "jsn") |> unwrap "serialize json")
            let local = Path.Combine(root, "local.nyrec")
            let fingerprint =
                use session = AuthoringSession.createNewWithDependencies local [ recordLibrary ] |> unwrap "create"
                commitLiteral session "mine"
                commitCall session "caller" "rec"
                (status session).SnapshotFingerprint
            Assert.Equal<string list>([ "fingerprint"; "path"; "snapshotId"; "snapshotRecords" ], sidecarKeys local)
            let header = (File.ReadAllText local).Split('\n').[0]
            Assert.Contains(" dependency=", header)
            let reopened =
                use session = AuthoringSession.openExisting local |> unwrap "reopen"
                Assert.Equal<string list>([ "caller"; "mine"; "rec" ], session.Declarations().Functions |> List.map (fun fn -> fn.DisplayName) |> List.sort)
                (status session).SnapshotFingerprint
            Assert.Equal(fingerprint, reopened)
            // Without the sidecar the resolved record text is refused by name, not decoded.
            File.Delete(local + ".dependency.json")
            match AuthoringSession.openExisting local with
            | Error(AuthoringSessionError.DependencyFailure message) -> Assert.Contains(".dependency.json' is missing", message)
            | other -> failwithf "Resolved record text opened without its sidecar: %A" other
            let mixed = Path.Combine(root, "mixed.nyrec")
            (AuthoringSession.createNewWithDependencies mixed [ jsonLibrary ] |> unwrap "create mixed").Close()
            Assert.Equal<string list>([ "fingerprint"; "path"; "snapshotId"; "snapshotJson" ], sidecarKeys mixed)
            (AuthoringSession.openExisting mixed |> unwrap "reopen mixed").Close())

    [<Fact>]
    let ``failed temporary writes leave no partial files and can be retried`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "workspace.json")
            File.WriteAllText(path, "original")
            let content = String('x', 4096)
            try
                SessionStorageFaults.failTemporaryWrite <- (=) path
                for _ in 1 .. 2 do
                    match SessionStorage.replaceAtomically path content with
                    | Error(AuthoringSessionError.IoFailure _) -> ()
                    | other -> failwithf "Write failure not reported: %A" other
                    Assert.Equal("original", File.ReadAllText path)
                    Assert.Empty(Directory.GetFiles(root, ".*.tmp"))
            finally SessionStorageFaults.failTemporaryWrite <- fun _ -> false
            SessionStorage.replaceAtomically path content |> unwrap "retry"
            Assert.Equal(content, File.ReadAllText path)
            Assert.Empty(Directory.GetFiles(root, ".*.tmp")))

    [<Fact>]
    let ``a failed migrate --to sidecar creation leaves no target snapshot behind`` () =
        withDirectory (fun root ->
            let libraryPath = Path.Combine(root, "library.json")
            File.WriteAllText(libraryPath, SnapshotCodec.serialize (library "lib") |> unwrap "serialize library")
            let source = Path.Combine(root, "source.json")
            (AuthoringSession.createNewWithDependencies source [ libraryPath ] |> unwrap "create").Close()
            let target = Path.Combine(root, "target.nyrec")
            // A directory at the sidecar's name passes the pre-check (File.Exists is false) and fails the rename.
            Directory.CreateDirectory(target + ".dependency.json") |> ignore
            match AuthoringSessionMigration.migrateTo source None target with
            | Error(AuthoringSessionError.IoFailure _) -> ()
            | other -> failwithf "Sidecar creation failure not reported: %A" other
            Assert.False(File.Exists target, "An orphan target snapshot was left behind.")
            Assert.False(File.Exists(target + ".backends.json"))
            assertLockReleased source
            assertLockReleased target
            Directory.Delete(target + ".dependency.json")
            AuthoringSessionMigration.migrateTo source None target |> unwrap "retry" |> ignore
            (AuthoringSession.openExisting target |> unwrap "open target").Close())

    [<Fact>]
    let ``a failed migrate --to snapshot write removes newly created companion files`` () =
        withDirectory (fun root ->
            let libraryPath = Path.Combine(root, "library.json")
            File.WriteAllText(libraryPath, SnapshotCodec.serialize (library "lib") |> unwrap "serialize library")
            let source = Path.Combine(root, "source.json")
            (AuthoringSession.createNewWithDependencies source [ libraryPath ] |> unwrap "create").Close()
            let target = Path.Combine(root, "target.nyrec")
            Directory.CreateDirectory target |> ignore
            match AuthoringSessionMigration.migrateTo source None target with
            | Error(AuthoringSessionError.IoFailure _) -> ()
            | other -> failwithf "Snapshot creation failure not reported: %A" other
            Assert.False(File.Exists(target + ".dependency.json"))
            Assert.False(File.Exists(target + ".backends.json"))
            Assert.Empty(Directory.GetFiles(root, ".*.tmp"))
            assertLockReleased source
            assertLockReleased target
            Directory.Delete target
            AuthoringSessionMigration.migrateTo source None target |> unwrap "retry" |> ignore
            (AuthoringSession.openExisting target |> unwrap "open target").Close())

    [<Fact>]
    let ``migrate --to converts a JSON workspace into a nyrec one and leaves the source untouched`` () =
        withDirectory (fun root ->
            let libraryPath = Path.Combine(root, "library.json")
            File.WriteAllText(libraryPath, SnapshotCodec.serialize (library "lib") |> unwrap "serialize library")
            let source = Path.Combine(root, "source.json")
            let fingerprints =
                use session = AuthoringSession.createNewWithDependencies source [ libraryPath ] |> unwrap "create"
                commitLiteral session "mine"
                let status = status session
                status.SnapshotFingerprint, status.SemanticFingerprint
            let sourceBytes = File.ReadAllBytes source
            let sidecarBytes = File.ReadAllBytes(source + ".dependency.json")
            let backendSettings = "{\"targets\":[\"zig\",\"csharp\"]}\n"
            File.WriteAllText(source + ".backends.json", backendSettings)
            let target = Path.Combine(root, "converted", "target.nyrec")
            let report = AuthoringSessionMigration.migrateTo source None target |> unwrap "migrate --to"
            Assert.Equal(Some target, report.Target)
            Assert.True report.Changed
            Assert.Equal(report.Before, report.After)
            Assert.Equal(SnapshotFingerprint.value (fst fingerprints), report.After.SnapshotFingerprint)
            Assert.Equal(SemanticFingerprint.value (snd fingerprints), report.After.SemanticFingerprint)
            Assert.Contains("\"target\":", AuthoringSessionMigration.reportJson report)
            Assert.Equal<byte[]>(sourceBytes, File.ReadAllBytes source)
            Assert.Equal<byte[]>(sidecarBytes, File.ReadAllBytes(source + ".dependency.json"))
            Assert.Equal(backendSettings, File.ReadAllText(source + ".backends.json"))
            Assert.Equal(backendSettings, File.ReadAllText(target + ".backends.json"))
            Assert.Equal<PureRunBackend list>([ PureRunBackend.Zig; PureRunBackend.CSharp ], PureRunBackends.declared target |> unwrap "converted backends")
            Assert.True(isRecordText (File.ReadAllText target))
            Assert.Equal<string list>([ "fingerprint"; "path"; "snapshotId"; "snapshotJson" ], sidecarKeys target)
            assertLockReleased source
            assertLockReleased target
            let converted =
                use session = AuthoringSession.openExisting target |> unwrap "open target"
                Assert.Equal<string list>([ "lib"; "mine" ], session.Declarations().Functions |> List.map (fun fn -> fn.DisplayName) |> List.sort)
                status session
            Assert.Equal(fst fingerprints, converted.SnapshotFingerprint)
            Assert.Equal(snd fingerprints, converted.SemanticFingerprint)
            match AuthoringSessionMigration.migrateTo source None target with
            | Error(AuthoringSessionError.SnapshotFileAlreadyExists _) -> ()
            | other -> failwithf "An existing target was overwritten: %A" other
            // In place, a record workspace is already in the current form.
            let inPlace = AuthoringSessionMigration.migrate target None |> unwrap "migrate in place"
            Assert.False inPlace.Changed)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``migrate --to preserves preset target backends and permits a source without settings`` preset =
        withDirectory (fun root ->
            let source = Path.Combine(root, "source.json")
            (AuthoringSession.createNew source |> unwrap "create").Close()
            if not preset then File.Delete(source + ".backends.json")
            let target = Path.Combine(root, "target.nyrec")
            let settings = "{\"targets\":[\"zig\"]}\n"
            if preset then File.WriteAllText(target + ".backends.json", settings)
            AuthoringSessionMigration.migrateTo source None target |> unwrap "convert" |> ignore
            if preset then Assert.Equal(settings, File.ReadAllText(target + ".backends.json"))
            else Assert.False(File.Exists(target + ".backends.json")))

    [<Fact>]
    let ``migrate --to reports backend copy failures without publishing a target`` () =
        withDirectory (fun root ->
            let source = Path.Combine(root, "source.json")
            (AuthoringSession.createNew source |> unwrap "create").Close()
            let target = Path.Combine(root, "target.nyrec")
            Directory.CreateDirectory(target + ".backends.json") |> ignore
            match AuthoringSessionMigration.migrateTo source None target with
            | Error(AuthoringSessionError.IoFailure _) -> ()
            | other -> failwithf "Backend copy failure not reported: %A" other
            Assert.False(File.Exists target)
            assertLockReleased source
            assertLockReleased target
            Directory.Delete(target + ".backends.json")
            AuthoringSessionMigration.migrateTo source None target |> unwrap "retry" |> ignore
            Assert.Equal(File.ReadAllText(source + ".backends.json"), File.ReadAllText(target + ".backends.json")))

    [<Fact>]
    let ``the viewer exports the same focus from record and JSON text`` () =
        let snapshot = library "shown"
        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        let records = RecordCodec.serialize snapshot |> unwrap "records"
        let functionIdText = StableId.value snapshot.Functions.Head.Id
        let fromJson = ViewerExport.focusFromSource ViewerSourceEncoding.Json json functionIdText |> unwrap "from json"
        let fromRecords = ViewerExport.focusFromSource ViewerSourceEncoding.Record records functionIdText |> unwrap "from records"
        Assert.Equal(fromJson, fromRecords)
        match ViewerExport.focusFromSource ViewerSourceEncoding.Record json functionIdText with
        | Error(ViewerExportError.RecordDecodeFailed error) -> Assert.Equal(1, error.Line)
        | other -> failwithf "JSON text decoded as records: %A" other
