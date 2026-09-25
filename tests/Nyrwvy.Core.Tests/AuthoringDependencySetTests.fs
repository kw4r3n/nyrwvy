namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Workspaces that import several dependencies under the import collision policy (Q-002).
module AuthoringDependencySetTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("dependency-set-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private moneyId = id<TypeTag> "money"
    let private amountId = id<FieldTag> "amount"

    let private money name: SemanticType =
        { Id = moneyId; DisplayName = name; Kind = SemanticTypeKind.Product [ { Id = amountId; DisplayName = "amount"; Type = intType } ] }

    /// A function `name : Money -> Money` that returns its input.
    let private identity name: SemanticFunction =
        let parameter = id<ParameterTag> (name + "-input")
        let node = id<GraphNodeTag> (name + "-node")
        { Id = id<FunctionTag> name
          DisplayName = name
          Parameters = [ { Id = parameter; DisplayName = "input"; Type = TypeRef.Named moneyId } ]
          ResultType = TypeRef.Named moneyId
          RequiredEffects = []
          Body = { Nodes = [ { Id = node; Kind = SemanticNodeKind.ParameterReference parameter } ]; ResultNodeId = node } }

    let private library name (types: SemanticType list) functions =
        { Snapshot.empty (id<SnapshotTag> name) with Types = types; Functions = functions }

    /// A and B both contain the identical Money type, as if both depended on one shared library.
    let private libraryA = library "a" [ money "Money" ] [ identity "keep" ]
    let private libraryB = library "b" [ money "Money" ] [ identity "pass" ]
    /// C reuses Money's identity for a differently named declaration.
    let private libraryC = library "c" [ money "Cash" ] [ identity "hold" ]

    let private withLibraries run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-dependency-set-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let write name snapshot =
                let path = Path.Combine(root, name + ".json")
                File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap "serialize")
                path
            run root (write "a" libraryA) (write "b" libraryB) (write "c" libraryC)
        finally Directory.Delete(root, true)

    [<Fact>]
    let ``identical shared declarations combine once and other collisions are rejected`` () =
        let combined = DependencyGraph.combine [ libraryA; libraryB ] |> unwrap "combine"
        Assert.Equal<TypeId list>([ moneyId ], combined.Types |> List.map (fun item -> item.Id))
        Assert.Equal<string list>([ "keep"; "pass" ], combined.Functions |> List.map (fun fn -> fn.DisplayName))
        Assert.Equal(combined.Id, (DependencyGraph.combine [ libraryB; libraryA ] |> unwrap "reordered").Id)
        Assert.Equal(libraryA, DependencyGraph.combine [ libraryA ] |> unwrap "single")
        match DependencyGraph.combine [ libraryA; libraryC ] with
        | Error(DependencyCombinationError.ConflictingDeclaration(kind, collided)) ->
            Assert.Equal("type", kind)
            Assert.Equal(StableId.value moneyId, collided)
        | other -> failwithf "Colliding identity was merged: %A" other
        Assert.Equal(Error DependencyCombinationError.NoDependencies, DependencyGraph.combine [])

    [<Fact>]
    let ``a workspace imports several dependencies and keeps their exact content`` () =
        withLibraries (fun root a b c ->
            let path = Path.Combine(root, "local.json")
            let session = AuthoringSession.createNewWithDependencies path [ a; b ] |> unwrap "create"
            let functions = session.Declarations().Functions |> List.map (fun fn -> fn.DisplayName) |> Set.ofList
            Assert.Equal<Set<string>>(set [ "keep"; "pass" ], functions)
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                  DisplayName = "both"
                  Parameters = [ { DisplayName = "input"; Type = TypeRef.Named moneyId } ]
                  ResultType = TypeRef.Named moneyId
                  RequiredEffects = []
                  Body =
                    ExpressionRecipe.DirectCall(id<FunctionTag> "keep",
                        [ id<ParameterTag> "keep-input",
                          ExpressionRecipe.DirectCall(id<FunctionTag> "pass", [ id<ParameterTag> "pass-input", ExpressionRecipe.ParameterSlot 0 ]) ]) }
            let proposal = session.SimulateCreateFunction request |> unwrap "simulate"
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            use described = JsonDocument.Parse(session.DependencySelection |> Option.get)
            Assert.Equal(2, described.RootElement.GetProperty("dependencies").GetArrayLength())
            session.Close()
            // The local file stores only local declarations; both libraries stay embedded.
            let local = SnapshotCodec.deserialize (File.ReadAllText path)
            Assert.True(Result.isError local, "The local file must not be valid without its dependencies.")
            File.Delete a
            File.Delete b
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            Assert.Equal(3, reopened.Declarations().Functions.Length)
            reopened.Close()
            // Tampering with one embedded member is detected on open.
            let manifest = File.ReadAllText(path + ".dependency.json")
            File.WriteAllText(path + ".dependency.json", manifest.Replace("pass", "PASS"))
            match AuthoringSession.openExisting path with
            | Error(AuthoringSessionError.DependencyFailure _)
            | Error(AuthoringSessionError.SnapshotCodecFailure _) -> ()
            | other -> failwithf "Tampered dependency set opened: %A" other
            ignore c)

    [<Fact>]
    let ``colliding or repeated dependencies are rejected without creating files`` () =
        withLibraries (fun root a b c ->
            let path = Path.Combine(root, "local.json")
            let expectFailure result =
                match result with
                | Error(AuthoringSessionError.DependencyFailure message) -> message
                | Ok(session: AuthoringSession) -> session.Close(); failwith "Invalid dependency set accepted."
                | Error error -> failwithf "Unexpected error: %A" error
            let message = expectFailure (AuthoringSession.createNewWithDependencies path [ a; c ])
            Assert.Contains(StableId.value moneyId, message)
            Assert.False(File.Exists path)
            Assert.False(File.Exists(path + ".dependency.json"))
            expectFailure (AuthoringSession.createNewWithDependencies path [ a; a ]) |> ignore
            use session = AuthoringSession.createNewWithDependencies path [ b ] |> unwrap "single"
            use single = JsonDocument.Parse(session.DependencySelection |> Option.get)
            Assert.False(fst (single.RootElement.TryGetProperty "dependencies")))

    [<Fact>]
    let ``a dependency with its own dependencies is flattened and shared content imports once`` () =
        withLibraries (fun root a b _ ->
            let basePath = Path.Combine(root, "base.json")
            File.WriteAllText(basePath, SnapshotCodec.serialize (library "base" [ money "Money" ] []) |> unwrap "base")
            // `mid` is itself a workspace that imports `base` and adds `grow`.
            let midPath = Path.Combine(root, "mid.json")
            let mid = AuthoringSession.createNewWithDependencies midPath [ basePath ] |> unwrap "mid"
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = (mid.Status() |> unwrap "status").SnapshotId
                  DisplayName = "grow"
                  Parameters = [ { DisplayName = "input"; Type = TypeRef.Named moneyId } ]
                  ResultType = TypeRef.Named moneyId
                  RequiredEffects = []
                  Body =
                    ExpressionRecipe.ProductConstruct(moneyId,
                        [ amountId, ExpressionRecipe.Int64WrappingAdd(ExpressionRecipe.FieldAccess(ExpressionRecipe.ParameterSlot 0, amountId), ExpressionRecipe.Int64Literal 1L) ]) }
            let proposal = mid.SimulateCreateFunction request |> unwrap "grow"
            mid.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            mid.Close()
            let appPath = Path.Combine(root, "app.json")
            let app = AuthoringSession.createNewWithDependencies appPath [ midPath ] |> unwrap "app"
            Assert.Equal<string list>([ "grow" ], app.Declarations().Functions |> List.map (fun fn -> fn.DisplayName))
            Assert.Equal<TypeId list>([ moneyId ], app.Declarations().Types |> List.map (fun item -> item.Id))
            app.Close()
            // The flattened content is retained: removing both upstream files does not affect the app.
            File.Delete basePath
            File.Delete midPath
            File.Delete(midPath + ".dependency.json")
            use reopened = AuthoringSession.openExisting appPath |> unwrap "reopen"
            Assert.Equal(1, reopened.Declarations().Functions.Length)
            reopened.Close()
            // A diamond: `a` and `b` share Money with the flattened `mid`, which is imported once.
            let midAgain = Path.Combine(root, "mid2.json")
            File.WriteAllText(Path.Combine(root, "base.json"), SnapshotCodec.serialize (library "base" [ money "Money" ] []) |> unwrap "base again")
            (AuthoringSession.createNewWithDependencies midAgain [ Path.Combine(root, "base.json") ] |> unwrap "mid again").Close()
            use diamond = AuthoringSession.createNewWithDependencies (Path.Combine(root, "diamond.json")) [ midAgain; a; b ] |> unwrap "diamond"
            Assert.Equal<TypeId list>([ moneyId ], diamond.Declarations().Types |> List.map (fun item -> item.Id))
            Assert.Equal<Set<string>>(set [ "keep"; "pass" ], diamond.Declarations().Functions |> List.map (fun fn -> fn.DisplayName) |> Set.ofList))
