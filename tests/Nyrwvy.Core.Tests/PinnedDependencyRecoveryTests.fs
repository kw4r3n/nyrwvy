namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module PinnedDependencyRecoveryTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private withDirectory run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-pin-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try run root
        finally Directory.Delete(root, true)

    let private save path snapshot = File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
    let private sourceSnapshot () = (TestGraph.create()).Snapshot
    let private manifestPath path = path + ".dependency.json"
    let private status (session: AuthoringSession) = session.Status() |> unwrap
    let private selection (session: AuthoringSession) = JsonNode.Parse(session.DependencySelection.Value)

    let private legacy root =
        let imported = sourceSnapshot ()
        let source = Path.Combine(root, "source.json")
        let local = Path.Combine(root, "local.json")
        save source imported
        File.WriteAllText(local, SnapshotCodec.serializeResolved imported (Snapshot.empty (StableIdAllocator.newSnapshotId ())) |> unwrap)
        File.WriteAllText(manifestPath local, JsonSerializer.Serialize(
            {| path = source; snapshotId = StableId.value imported.Id
               fingerprint = SnapshotFingerprint.compute imported |> unwrap |> SnapshotFingerprint.value |}))
        imported, source, local

    let private expectFailure = function
        | Error _ -> ()
        | Ok(session: AuthoringSession) -> session.Close(); failwith "Invalid pin accepted"

    [<Fact>]
    let ``embedded dependency survives source deletion and relocation without changing identity`` () =
        withDirectory (fun root ->
            let source = Path.Combine(root, "source.json")
            let local = Path.Combine(root, "local.json")
            save source (sourceSnapshot ())
            use session = AuthoringSession.createNewWithDependency local (Some source) |> unwrap
            let before = status session
            let provenance = selection session
            Assert.Equal("embedded", provenance["storage"].GetValue<string>())
            Assert.Null(provenance["snapshotJson"])
            session.Close()
            let relocated = Path.Combine(root, "clone", "renamed.json")
            Directory.CreateDirectory(Path.GetDirectoryName relocated) |> ignore
            File.Copy(local, relocated)
            File.Copy(manifestPath local, manifestPath relocated)
            File.Delete source
            File.Delete local
            File.Delete(manifestPath local)
            use reopened = AuthoringSession.openExisting relocated |> unwrap
            Assert.Equal(before, status reopened)
            Assert.Equal(2, reopened.Declarations().Functions.Length)
            reopened.ProjectExecution() |> unwrap |> ignore
            // Pin is idempotent for already retained content, even with no source.
            reopened.Close()
            let manifest = File.ReadAllText(manifestPath relocated)
            use pinned = AuthoringSession.pinExistingDependency relocated None |> unwrap
            Assert.Equal(before, status pinned)
            Assert.Equal(manifest, File.ReadAllText(manifestPath relocated)))

    [<Fact>]
    let ``imported declaration deletion is rejected before a proposal is registered`` () =
        withDirectory (fun root ->
            let effectId = StableIdAllocator.newEffectId ()
            let capabilityId = StableIdAllocator.newCapabilityId ()
            let typeId = StableIdAllocator.newTypeId ()
            let functionId = StableIdAllocator.newFunctionId ()
            let nodeId = StableIdAllocator.newGraphNodeId ()
            let imported =
                { Snapshot.empty (StableIdAllocator.newSnapshotId ()) with
                    Types = [ { Id = typeId; DisplayName = "Unused"; Kind = SemanticTypeKind.Product [] } ]
                    Effects = [ { Id = effectId; DisplayName = "Notify" } ]
                    Capabilities =
                        [ { Id = capabilityId; DisplayName = "Notifier"
                            Operations =
                                [ { Id = StableIdAllocator.newCapabilityOperationId (); DisplayName = "notify"
                                    Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Unit
                                    FailureType = None; EffectId = effectId } ] } ]
                    Functions =
                        [ { Id = functionId; DisplayName = "constant"; Parameters = []
                            ResultType = TypeRef.Primitive PrimitiveType.Int64; RequiredEffects = []
                            Body = { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.Int64Literal 7L } ]; ResultNodeId = nodeId } } ] }
            let source = Path.Combine(root, "source.json")
            let local = Path.Combine(root, "local.json")
            save source imported
            use session = AuthoringSession.createNewWithDependency local (Some source) |> unwrap
            let before = File.ReadAllText(local)
            for operations in
                [ [ AgentOperationIntent.DeleteFunction functionId ]
                  [ AgentOperationIntent.DeleteType typeId ]
                  [ AgentOperationIntent.DeleteCapability capabilityId ]
                  [ AgentOperationIntent.DeleteCapabilityOperation imported.Capabilities.Head.Operations.Head.Id ]
                  [ AgentOperationIntent.DeleteCapability capabilityId; AgentOperationIntent.DeleteEffect effectId ] ] do
                match session.SimulateOperations operations with
                | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                    Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationModified")
                | other -> failwithf "Imported deletion proposal accepted: %A" other
            Assert.Equal(before, File.ReadAllText(local)))

    [<Fact>]
    let ``legacy pin recovery accepts only the exact original content and leaves local graph bytes unchanged`` () =
        withDirectory (fun root ->
            let imported, source, local = legacy root
            use original = AuthoringSession.openExisting local |> unwrap
            let before = status original
            Assert.Equal("legacySource", (selection original).["storage"].GetValue<string>())
            AuthoringSession.pinExistingDependency local None |> expectFailure // single writer
            original.Close()
            let localBytes = File.ReadAllText local
            let legacyBytes = File.ReadAllText(manifestPath local)
            let archived = Path.Combine(root, "archived-original.json")
            File.Copy(source, archived)
            // Same SnapshotId but changed content is still a different pin.
            save source { imported with Functions = [] }
            AuthoringSession.openExisting local |> expectFailure
            AuthoringSession.pinExistingDependency local (Some source) |> expectFailure
            Assert.Equal(legacyBytes, File.ReadAllText(manifestPath local))
            Assert.Equal(localBytes, File.ReadAllText local)
            use recovered = AuthoringSession.pinExistingDependency local (Some archived) |> unwrap
            Assert.Equal(before, status recovered)
            Assert.Equal("embedded", (selection recovered).["storage"].GetValue<string>())
            Assert.Equal(localBytes, File.ReadAllText local)
            recovered.Close()
            File.Delete archived
            File.Delete source
            use reopened = AuthoringSession.openExisting local |> unwrap
            Assert.Equal(before, status reopened))

    [<Fact>]
    let ``ordinary open never silently migrates a legacy manifest`` () =
        withDirectory (fun root ->
            let _, _, local = legacy root
            let before = File.ReadAllText(manifestPath local)
            (AuthoringSession.openExisting local |> unwrap).Close()
            Assert.Equal(before, File.ReadAllText(manifestPath local))
            (AuthoringSession.pinExistingDependency local None |> unwrap).Close()
            Assert.NotEqual<string>(before, File.ReadAllText(manifestPath local)))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``embedded corruption cannot bypass the fingerprint or local digest`` alsoReplaceFingerprint =
        withDirectory (fun root ->
            let imported = sourceSnapshot ()
            let source, local = Path.Combine(root, "source.json"), Path.Combine(root, "local.json")
            save source imported
            (AuthoringSession.createNewWithDependency local (Some source) |> unwrap).Close()
            let altered = { imported with Functions = [] }
            let manifest = JsonNode.Parse(File.ReadAllText(manifestPath local))
            manifest["snapshotJson"] <- JsonValue.Create(SnapshotCodec.serialize altered |> unwrap)
            if alsoReplaceFingerprint then
                manifest["fingerprint"] <- JsonValue.Create(SnapshotFingerprint.compute altered |> unwrap |> SnapshotFingerprint.value)
            File.WriteAllText(manifestPath local, manifest.ToJsonString())
            // The live original source is still valid: no silent fallback to it.
            AuthoringSession.openExisting local |> expectFailure
            let beforePin = File.ReadAllText(manifestPath local)
            AuthoringSession.pinExistingDependency local None |> expectFailure
            Assert.Equal(beforePin, File.ReadAllText(manifestPath local)))

    [<Fact>]
    let ``manifest changes invalidate pending publication even for embedded dependencies`` () =
        withDirectory (fun root ->
            let source, local = Path.Combine(root, "source.json"), Path.Combine(root, "local.json")
            save source (sourceSnapshot ())
            use session = AuthoringSession.createNewWithDependency local (Some source) |> unwrap
            let before = File.ReadAllText local
            let proposal = session.SimulateCreateFunction
                                { ExpectedBaseSnapshotId = (status session).SnapshotId
                                  DisplayName = "local"; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Int64
                                  RequiredEffects = []; Body = ExpressionRecipe.Int64Literal 1L } |> unwrap
            File.AppendAllText(manifestPath local, " ")
            match session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Manifest modification was ignored: %A" other
            Assert.Equal(before, File.ReadAllText local)
            match session.Reject proposal.ProposalId with
            | Error AuthoringSessionError.SessionInvalidated -> ()
            | other -> failwithf "Changed selection did not invalidate the session: %A" other)

    [<Fact>]
    let ``pin cannot attach a dependency to a standalone workspace and a nested graph is flattened`` () =
        withDirectory (fun root ->
            let source, local = Path.Combine(root, "source.json"), Path.Combine(root, "local.json")
            save source (sourceSnapshot ())
            (AuthoringSession.createNew local |> unwrap).Close()
            let before = File.ReadAllText local
            AuthoringSession.pinExistingDependency local (Some source) |> expectFailure
            Assert.Equal(before, File.ReadAllText local)
            Assert.False(File.Exists(manifestPath local))
            File.Delete local
            (AuthoringSession.createNewWithDependency local (Some source) |> unwrap).Close()
            // Selecting a workspace that has its own dependency pins its complete, flattened graph.
            use nested = AuthoringSession.createNewWithDependency (Path.Combine(root, "nested.json")) (Some local) |> unwrap
            Assert.Equal(sourceSnapshot().Functions.Length, nested.Declarations().Functions.Length))

    [<Fact>]
    let ``failed legacy pin replacement preserves both files and releases the lock`` () =
        // Unix directory permissions exercise the real same-directory temporary
        // write failure. Root bypasses mode bits; Windows needs a different ACL fixture.
        if not (OperatingSystem.IsWindows()) && Environment.UserName <> "root" then
            withDirectory (fun root ->
                let _, _, local = legacy root
                (AuthoringSession.openExisting local |> unwrap).Close() // pre-create lock
                let localBefore, manifestBefore = File.ReadAllText local, File.ReadAllText(manifestPath local)
                let mode = File.GetUnixFileMode root
                try
                    File.SetUnixFileMode(root, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)
                    match AuthoringSession.pinExistingDependency local None with
                    | Error(AuthoringSessionError.IoFailure _) -> ()
                    | other -> failwithf "Expected pin publication failure: %A" other
                finally File.SetUnixFileMode(root, mode)
                Assert.Equal(localBefore, File.ReadAllText local)
                Assert.Equal(manifestBefore, File.ReadAllText(manifestPath local))
                use retry = AuthoringSession.pinExistingDependency local None |> unwrap
                Assert.Equal("embedded", (selection retry).["storage"].GetValue<string>()))
