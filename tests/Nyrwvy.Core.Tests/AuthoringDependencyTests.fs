namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module AuthoringDependencyTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private withDirectory run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-dependency-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try run root
        finally Directory.Delete(root, true)

    let private save path snapshot = File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)

    let private expectDependencyFailure = function
        | Error(AuthoringSessionError.DependencyFailure _) -> ()
        | Ok(session: AuthoringSession) -> session.Close(); failwith "Unexpected successful dependency selection"
        | Error error -> failwithf "Expected DependencyFailure, got %A" error

    // A released lock leaves no file behind, so a failed open or a clean close is invisible afterwards.
    let private assertLockReleased path = Assert.False(File.Exists(path + ".lock"))

    [<Theory>]
    [<InlineData("{\"path\":null,\"snapshotId\":\"x\",\"fingerprint\":\"x\"}")>]
    [<InlineData("{\"path\":\"\",\"snapshotId\":\"x\",\"fingerprint\":\"x\"}")>]
    [<InlineData("{\"path\":42,\"snapshotId\":\"x\",\"fingerprint\":\"x\"}")>]
    [<InlineData("{\"path\":\"/missing\",\"snapshotId\":null,\"fingerprint\":\"x\"}")>]
    [<InlineData("{\"path\":\"/missing\",\"snapshotId\":\"x\",\"fingerprint\":null}")>]
    [<InlineData("{\"path\":\"/missing\",\"path\":\"/other\",\"snapshotId\":\"x\",\"fingerprint\":\"x\"}")>]
    [<InlineData("[]")>]
    [<InlineData("{")>]
    let ``invalid dependency manifests return errors and release the workspace lock`` (manifest: string) =
        withDirectory (fun root ->
            let path = Path.Combine(root, "local.json")
            let manifestPath = path + ".dependency.json"
            (AuthoringSession.createNew path |> unwrap).Close()
            File.WriteAllText(manifestPath, manifest)
            AuthoringSession.openExisting path |> expectDependencyFailure
            assertLockReleased path
            File.Delete manifestPath
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Empty(reopened.Declarations().Functions))

    [<Fact>]
    let ``an unreadable manifest is not treated as an absent dependency`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "local.json")
            (AuthoringSession.createNew path |> unwrap).Close()
            // A directory reliably raises an access/I/O failure on both Windows
            // and Unix, without chmod tests silently passing under a root runner.
            Directory.CreateDirectory(path + ".dependency.json") |> ignore
            AuthoringSession.openExisting path |> expectDependencyFailure
            assertLockReleased path)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData("\u0000")>]
    let ``invalid dependency creation paths do not escape or retain locks`` dependencyPath =
        withDirectory (fun root ->
            let path = Path.Combine(root, "local.json")
            AuthoringSession.createNewWithDependency path (Some dependencyPath) |> expectDependencyFailure
            Assert.False(File.Exists path)
            Assert.False(File.Exists(path + ".dependency.json"))
            assertLockReleased path)

    [<Fact>]
    let ``failed dependent snapshot creation cleans the manifest and releases its lock`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "local.json")
            let dependencyPath = Path.Combine(root, "source.json")
            save dependencyPath (TestGraph.create()).Snapshot
            Directory.CreateDirectory path |> ignore // snapshot publication must fail
            match AuthoringSession.createNewWithDependency path (Some dependencyPath) with
            | Error(AuthoringSessionError.IoFailure _) -> ()
            | other -> failwithf "Expected failed snapshot creation, got %A" other
            Assert.False(File.Exists(path + ".dependency.json"))
            assertLockReleased path
            Directory.Delete path
            use retry = AuthoringSession.createNewWithDependency path (Some dependencyPath) |> unwrap
            Assert.Equal(2, retry.Declarations().Functions.Length))

    [<Fact>]
    let ``incomplete dependencies are rejected at create and reopen rather than blocking every commit`` () =
        withDirectory (fun root ->
            let fixture = TestGraph.create ()
            let workspace = AgentWorkspace.create fixture.Snapshot |> unwrap
            let proposal =
                AgentWorkflow.simulate workspace
                    [ AgentOperationIntent.AddFunctionParameter
                        { TargetFunctionId = fixture.ValidateFunctionId
                          NewParameterDisplayName = "missing"
                          NewParameterType = fixture.StringType
                          CallSitePlans = [ AgentCallPlan.LeaveMissing fixture.RunCallNodeId ] } ]
                |> unwrap
            let incomplete =
                match AgentWorkflow.commitWithPolicy workspace proposal CommitPolicy.AllowBlockingObligations with
                | AgentCommitResult.Committed(candidate, _) ->
                    AgentWorkspace.serializeCurrentSnapshot candidate |> unwrap |> SnapshotCodec.deserialize |> unwrap
                | other -> failwithf "Failed to create incomplete fixture: %A" other
            let sourcePath = Path.Combine(root, "source.json")
            let path = Path.Combine(root, "local.json")
            save sourcePath incomplete
            AuthoringSession.createNewWithDependency path (Some sourcePath) |> expectDependencyFailure
            Assert.False(File.Exists path)
            Assert.False(File.Exists(path + ".dependency.json"))
            assertLockReleased path

            // Reproduce a dependent file created by the old implementation. The
            // selected version matches; rejection is specifically completeness.
            let local = Snapshot.empty (StableIdAllocator.newSnapshotId ())
            File.WriteAllText(path, SnapshotCodec.serializeResolved incomplete local |> unwrap)
            File.WriteAllText(path + ".dependency.json", JsonSerializer.Serialize(
                {| path = sourcePath
                   snapshotId = StableId.value incomplete.Id
                   fingerprint = SnapshotFingerprint.compute incomplete |> unwrap |> SnapshotFingerprint.value |}))
            AuthoringSession.openExisting path |> expectDependencyFailure
            assertLockReleased path
            // Incompleteness is still legal in standalone authoring workspaces.
            use source = AuthoringSession.openExisting sourcePath |> unwrap
            Assert.Single(source.GetOpenObligations fixture.RunFunctionId |> unwrap) |> ignore)

    [<Theory>]
    [<InlineData("rename-function")>]
    [<InlineData("rename-type")>]
    [<InlineData("replace-body")>]
    [<InlineData("replace-target")>]
    [<InlineData("add-parameter")>]
    [<InlineData("migrate-parameter")>]
    [<InlineData("evolve-union")>]
    let ``imported edits are rejected before a proposal is exposed`` operation =
        withDirectory (fun root ->
            let fixture = TestGraph.create ()
            let unionId = StableId.createForTesting<TypeTag> "type-imported-union"
            let variantId = StableId.createForTesting<VariantTag> "variant-imported-case"
            let imported =
                { fixture.Snapshot with
                    Types = { Id = unionId; DisplayName = "Choice"
                              Kind = SemanticTypeKind.Union [ { Id = variantId; DisplayName = "First"; Fields = [] } ] }
                            :: fixture.Snapshot.Types }
            let sourcePath = Path.Combine(root, "source.json")
            let path = Path.Combine(root, "local.json")
            save sourcePath imported
            use session = AuthoringSession.createNewWithDependency path (Some sourcePath) |> unwrap
            let before = session.Status() |> unwrap
            let source = File.ReadAllText path
            let proposed =
                match operation with
                | "rename-function" ->
                    session.SimulateOperations [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function fixture.ValidateFunctionId, "renamed") ]
                | "rename-type" ->
                    session.SimulateOperations [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Type fixture.PolicyTypeId, "Renamed") ]
                | "replace-body" ->
                    session.SimulateReplaceBody
                        { TargetFunctionId = fixture.ValidateFunctionId
                          ExpectedBaseSnapshotId = before.SnapshotId
                          ExpectedCurrentResultNodeId = fixture.ValidateInputNodeId
                          Replacement = ExpressionRecipe.StringLiteral "changed" }
                | "replace-target" ->
                    session.SimulateReplaceTarget
                        { TargetFunctionId = fixture.ValidateFunctionId
                          ExpectedBaseSnapshotId = before.SnapshotId
                          TargetNodeId = fixture.ValidateInputNodeId
                          Replacement = ExpressionRecipe.StringLiteral "changed" }
                | "add-parameter" ->
                    session.SimulateOperations
                        [ AgentOperationIntent.AddFunctionParameter
                            { TargetFunctionId = fixture.ValidateFunctionId
                              NewParameterDisplayName = "extra"
                              NewParameterType = fixture.StringType
                              CallSitePlans = [ AgentCallPlan.LeaveMissing fixture.RunCallNodeId ] } ]
                | "migrate-parameter" ->
                    session.SimulateParameterTypeMigration
                        { ExpectedBaseSnapshotId = before.SnapshotId
                          TargetFunctionId = fixture.ValidateFunctionId
                          TargetParameterId = fixture.ValidateInputParameterId
                          NewParameterType = TypeRef.Named fixture.PolicyTypeId
                          TargetReplacement = ExpressionRecipe.StringLiteral "changed"
                          CallerRewrites =
                            [ { CallerFunctionId = fixture.RunFunctionId
                                Replacement = ExpressionRecipe.DirectCall(fixture.ValidateFunctionId,
                                    [ fixture.ValidateInputParameterId, ExpressionRecipe.Parameter fixture.RunPolicyParameterId ]) } ] }
                | "evolve-union" ->
                    session.SimulateAddUnionVariantLike(
                        { ExpectedBaseSnapshotId = before.SnapshotId
                          TargetTypeId = unionId
                          SourceVariantId = variantId
                          NewVariantDisplayName = "Second" },
                        [])
                | _ -> failwith "Unknown test operation"
            match proposed with
            | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationModified")
            | other -> failwithf "Imported edit reached review or failed unexpectedly: %A" other
            Assert.Equal(before, session.Status() |> unwrap)
            Assert.Equal(source, File.ReadAllText path)
            Assert.Equal(SnapshotCodec.serialize imported |> unwrap, File.ReadAllText sourcePath))
