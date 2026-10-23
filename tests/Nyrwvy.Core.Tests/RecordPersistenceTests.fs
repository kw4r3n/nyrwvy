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
                [ { Exported = true; Id = id<FunctionTag> name; TypeParameters = []; DisplayName = name
                    Parameters = [ { Id = parameter; DisplayName = "input"; Type = int64Type } ]
                    ResultType = int64Type; RequiredEffects = []
                    Body = { Nodes = [ { Id = node; Kind = SemanticNodeKind.ParameterReference parameter } ]; ResultNodeId = node } } ] }

    let private commitLiteral (session: AuthoringSession) name =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = (status session).SnapshotId; TypeParameters = None
              DisplayName = name; Parameters = []; ResultType = int64Type; RequiredEffects = []
              Body = ExpressionRecipe.Int64Literal 1L }
        let proposal = session.SimulateCreateFunction request |> unwrap "simulate"
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

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
            Assert.False(File.Exists(path + ".lock"))
            let bytes = File.ReadAllBytes path
            let reopened =
                use session = AuthoringSession.openExisting path |> unwrap "reopen"
                Assert.Equal<string list>([ "one" ], session.Declarations().Functions |> List.map (fun fn -> fn.DisplayName))
                (status session).SnapshotFingerprint
            Assert.Equal(fingerprint, reopened)
            Assert.Equal<byte[]>(bytes, File.ReadAllBytes path)
            Assert.False(File.Exists(path + ".lock"))
            // A record decode failure names its line and column.
            File.WriteAllText(path, File.ReadAllText(path).Replace("schema=", "schema=x"))
            match AuthoringSession.openExisting path with
            | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidShape message)) ->
                Assert.StartsWith("line 1, column ", message)
            | other -> failwithf "Corrupt record text was accepted: %A" other
            Assert.False(File.Exists(path + ".lock")))

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
                (status session).SnapshotFingerprint
            Assert.Equal<string list>([ "fingerprint"; "path"; "snapshotId"; "snapshotRecords" ], sidecarKeys local)
            let header = (File.ReadAllText local).Split('\n').[0]
            Assert.Contains(" dependency=", header)
            let reopened =
                use session = AuthoringSession.openExisting local |> unwrap "reopen"
                Assert.Equal<string list>([ "mine"; "rec" ], session.Declarations().Functions |> List.map (fun fn -> fn.DisplayName) |> List.sort)
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
            Assert.True(isRecordText (File.ReadAllText target))
            Assert.Equal<string list>([ "fingerprint"; "path"; "snapshotId"; "snapshotJson" ], sidecarKeys target)
            Assert.False(File.Exists(source + ".lock"))
            Assert.False(File.Exists(target + ".lock"))
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
