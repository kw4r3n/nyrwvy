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

    /// A local workspace on library A whose own function `wrap` calls A's `keep`.
    let private localOnA root a =
        let path = Path.Combine(root, "local.json")
        let session = AuthoringSession.createNewWithDependencies path [ a ] |> unwrap "create"
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
              DisplayName = "wrap"
              Parameters = [ { DisplayName = "input"; Type = TypeRef.Named moneyId } ]
              ResultType = TypeRef.Named moneyId
              RequiredEffects = []
              Body = ExpressionRecipe.DirectCall(id<FunctionTag> "keep", [ id<ParameterTag> "keep-input", ExpressionRecipe.ParameterSlot 0 ]) }
        let proposal = session.SimulateCreateFunction request |> unwrap "wrap"
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
        let wrap = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "wrap")).Id
        session.Close()
        path, wrap

    [<Fact>]
    let ``a dependency upgrade is previewed and applied only for the reviewed compatible version`` () =
        withLibraries (fun root a b _ ->
            let path, wrap = localOnA root a
            let write name snapshot =
                let file = Path.Combine(root, name + ".json")
                File.WriteAllText(file, SnapshotCodec.serialize snapshot |> unwrap name)
                file
            let compatible = write "a-v2" (library "a-v2" [ money "Money" ] [ identity "keep"; identity "extra" ])
            let incompatible = write "a-v3" (library "a-v3" [ money "Money" ] [ identity "other" ])
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let preview = session.PreviewDependencyUpgrade(compatible, None) |> unwrap "preview"
            Assert.Empty(preview.Issues)
            Assert.Equal<AuthoringDependencyChange list>([ { Kind = "function"; Id = StableId.value (id<FunctionTag> "extra") } ], preview.Added)
            Assert.Empty(preview.Removed)
            let breaking = session.PreviewDependencyUpgrade(incompatible, None) |> unwrap "breaking preview"
            Assert.NotEmpty(breaking.Issues)
            Assert.Contains({ Kind = "function"; Id = StableId.value (id<FunctionTag> "keep") }, breaking.Removed)
            match session.PreviewDependencyUpgrade(a, None) with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Same-version upgrade was previewed: %A" other
            let wire = sprintf """{"id":"preview","op":"previewDependencyUpgrade","source":"%s"}""" (compatible.Replace("\\", "\\\\"))
            use response = AuthoringProtocol.executeLine session wire |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("result").GetProperty("compatible").GetBoolean(), response.RootElement.ToString())
            session.Close()
            let before = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
            match AuthoringSession.upgradeDependency path incompatible None breaking.NewFingerprint with
            | Error(AuthoringSessionError.WorkspaceValidationFailure _) -> ()
            | other -> failwithf "Incompatible upgrade applied: %A" other
            match AuthoringSession.upgradeDependency path compatible None breaking.NewFingerprint with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Unreviewed version applied: %A" other
            Assert.Equal(before, (File.ReadAllText path, File.ReadAllText(path + ".dependency.json")))
            let upgraded = AuthoringSession.upgradeDependency path compatible None preview.NewFingerprint |> unwrap "upgrade"
            Assert.Contains("extra", upgraded.Declarations().Functions |> List.map (fun fn -> fn.DisplayName))
            upgraded.Close()
            File.Delete compatible
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            Assert.Contains(wrap, reopened.Declarations().Functions |> List.map (fun fn -> fn.Id))
            Assert.Equal(3, reopened.Declarations().Functions.Length)
            ignore b)

    [<Fact>]
    let ``a failed graph write during an upgrade restores the previous dependency selection`` () =
        withLibraries (fun root a _ _ ->
            let path, wrap = localOnA root a
            let compatible = Path.Combine(root, "a-v2.json")
            File.WriteAllText(compatible, SnapshotCodec.serialize (library "a-v2" [ money "Money" ] [ identity "keep"; identity "extra" ]) |> unwrap "a-v2")
            let preview =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                session.PreviewDependencyUpgrade(compatible, None) |> unwrap "preview"
            let before = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
            let target = Path.GetFullPath path
            SessionStorageFaults.failReplacement <- fun candidate -> candidate = target
            try
                match AuthoringSession.upgradeDependency path compatible None preview.NewFingerprint with
                | Error(AuthoringSessionError.IoFailure _) -> ()
                | other -> failwithf "Upgrade did not report the failed graph write: %A" other
            finally SessionStorageFaults.failReplacement <- fun _ -> false
            Assert.Equal(before, (File.ReadAllText path, File.ReadAllText(path + ".dependency.json")))
            do
                use reopened = AuthoringSession.openExisting path |> unwrap "reopen after failed upgrade"
                Assert.Contains(wrap, reopened.Declarations().Functions |> List.map (fun fn -> fn.Id))
                Assert.DoesNotContain("extra", reopened.Declarations().Functions |> List.map (fun fn -> fn.DisplayName))
            use upgraded = AuthoringSession.upgradeDependency path compatible None preview.NewFingerprint |> unwrap "retry upgrade"
            Assert.Contains("extra", upgraded.Declarations().Functions |> List.map (fun fn -> fn.DisplayName)))

    [<Fact>]
    let ``upgrading one member of a dependency set requires naming it`` () =
        withLibraries (fun root a b _ ->
            let path = Path.Combine(root, "set.json")
            (AuthoringSession.createNewWithDependencies path [ a; b ] |> unwrap "create").Close()
            let next = Path.Combine(root, "b-v2.json")
            File.WriteAllText(next, SnapshotCodec.serialize (library "b-v2" [ money "Money" ] [ identity "pass"; identity "more" ]) |> unwrap "b-v2")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            match session.PreviewDependencyUpgrade(next, None) with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Ambiguous upgrade was previewed: %A" other
            let preview = session.PreviewDependencyUpgrade(next, Some(StableId.value libraryB.Id)) |> unwrap "preview"
            Assert.Equal(StableId.value libraryB.Id, preview.ReplacedSnapshotId)
            Assert.Empty(preview.Issues)
            session.Close()
            use upgraded = AuthoringSession.upgradeDependency path next (Some(StableId.value libraryB.Id)) preview.NewFingerprint |> unwrap "upgrade"
            Assert.Equal<Set<string>>(set [ "keep"; "pass"; "more" ], upgraded.Declarations().Functions |> List.map (fun fn -> fn.DisplayName) |> Set.ofList))

    [<Fact>]
    let ``the same declaration with reordered Product fields is a conflict, not a merge`` () =
        let currencyId = id<FieldTag> "currency"
        let withFields fields: SemanticType =
            { Id = moneyId; DisplayName = "Money"; Kind = SemanticTypeKind.Product fields }
        let amount: SemanticField = { Id = amountId; DisplayName = "amount"; Type = intType }
        let currency: SemanticField = { Id = currencyId; DisplayName = "currency"; Type = TypeRef.Primitive PrimitiveType.String }
        let first = library "ordered" [ withFields [ amount; currency ] ] []
        let second = library "reordered" [ withFields [ currency; amount ] ] []
        // Field order is meaningful, so only identical declarations merge.
        match DependencyGraph.combine [ first; second ] with
        | Error(DependencyCombinationError.ConflictingDeclaration(kind, collided)) ->
            Assert.Equal("type", kind)
            Assert.Equal(StableId.value moneyId, collided)
        | other -> failwithf "Reordered declaration was merged: %A" other
        Assert.True(DependencyGraph.combine [ first; library "same" [ withFields [ amount; currency ] ] [] ] |> Result.isOk)

    [<Fact>]
    let ``display names shared across dependencies are rejected by mutation and ambiguous by name`` () =
        // Two libraries each hold a different function displayed as "keep".
        let libraryD = library "d" [ money "Money" ] [ { identity "other-keep" with DisplayName = "keep" } ]
        withLibraries (fun root a _ _ ->
            let d = Path.Combine(root, "d.json")
            File.WriteAllText(d, SnapshotCodec.serialize libraryD |> unwrap "serialize")
            use session = AuthoringSession.createNewWithDependencies (Path.Combine(root, "local.json")) [ a; d ] |> unwrap "create"
            Assert.Equal(2, session.Declarations().Functions |> List.filter (fun fn -> fn.DisplayName = "keep") |> List.length)
            let protocolError request =
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.GetRawText())
                let error = response.RootElement.GetProperty("error")
                Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
                error.GetProperty("message").GetString()
            Assert.Equal("Ambiguous function 'keep'.", protocolError """{"id":"read","op":"readFunction","function":"keep"}""")
            Assert.Equal(
                "Ambiguous function 'keep'.",
                protocolError """{"id":"delete","op":"simulateOperationsByName","operations":[{"kind":"deleteFunction","function":"keep"}]}""")
            Assert.Contains(
                "already exists",
                protocolError
                    """{"id":"create","op":"simulateCreateFunctionByName","displayName":"keep","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"int64Literal","value":1}}""")
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = session.SnapshotId
                  DisplayName = "keep"
                  Parameters = []
                  ResultType = intType
                  RequiredEffects = []
                  Body = ExpressionRecipe.Int64Literal 1L }
            match session.SimulateCreateFunction request with
            | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _))) ->
                Assert.Equal("FunctionCreationDuplicateDisplayName", diagnostic.Code)
            | other -> failwithf "Duplicate display name was accepted: %A" other
            session.Close())

    let private writeVersion root name snapshot =
        let path = Path.Combine(root, name + ".json")
        File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap name)
        path

    let private changedMoney =
        { money "Money" with Kind = SemanticTypeKind.Product [ { Id = amountId; DisplayName = "amount"; Type = TypeRef.Primitive PrimitiveType.String } ] }

    let private replacement source (snapshot: Snapshot): AuthoringDependencyUpgrade =
        { Source = source; Replaces = StableId.value snapshot.Id }

    let private diamondWithLocal root a b =
        let path, _ = localOnA root a
        // Retain the authored local graph while selecting the original diamond imports.
        let local = SnapshotCodec.deserializeResolved libraryA (File.ReadAllText path) |> unwrap "local"
        let combined = DependencyGraph.combine [ libraryA; libraryB ] |> unwrap "diamond"
        let selectionPath = Path.Combine(root, "diamond.json")
        (AuthoringSession.createNewWithDependencies selectionPath [ a; b ] |> unwrap "create diamond").Close()
        File.WriteAllText(selectionPath, SnapshotCodec.serializeResolved combined local |> unwrap "bind diamond")
        selectionPath, local

    [<Fact>]
    let ``atomic upgrades change shared diamond schema while preserving all local identities and reopening without sources`` () =
        withLibraries (fun root a b _ ->
            let path, local = diamondWithLocal root a b
            let nextA = writeVersion root "a-next" (library "a-next" [ changedMoney ] [ identity "keep" ])
            let nextB = writeVersion root "b-next" (library "b-next" [ changedMoney ] [ identity "pass" ])
            let upgrades = [ replacement nextA libraryA; replacement nextB libraryB ]
            let preview =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                for source, target in [ nextA, libraryA; nextB, libraryB ] do
                    match session.PreviewDependencyUpgrade(source, Some(StableId.value target.Id)) with
                    | Error(AuthoringSessionError.DependencyFailure message) -> Assert.Contains("different content", message)
                    | other -> failwithf "Mixed old/new diamond accepted: %A" other
                let review = session.PreviewDependencyUpgrades upgrades |> unwrap "batch preview"
                Assert.Empty(review.Issues)
                Assert.Contains({ Kind = "type"; Id = StableId.value moneyId }, review.Changed)
                let request = JsonSerializer.Serialize({| id = "batch"; op = "previewDependencyUpgrades"; upgrades = upgrades |> List.map (fun item -> {| source = item.Source; replaces = item.Replaces |}) |})
                use wire = JsonDocument.Parse(AuthoringProtocol.executeLine session request)
                Assert.True(wire.RootElement.GetProperty("ok").GetBoolean(), wire.RootElement.ToString())
                Assert.Equal(review.ReviewFingerprint, wire.RootElement.GetProperty("result").GetProperty("reviewFingerprint").GetString())
                review
            use upgraded = AuthoringSession.upgradeDependencies path upgrades preview.ReviewFingerprint |> unwrap "batch apply"
            Assert.Contains(local.Functions.Head, upgraded.Declarations().Functions)
            upgraded.Close()
            let combined = DependencyGraph.combine [ library "a-next" [ changedMoney ] [ identity "keep" ]; library "b-next" [ changedMoney ] [ identity "pass" ] ] |> unwrap "new imports"
            let after = SnapshotCodec.deserializeResolved combined (File.ReadAllText path) |> unwrap "after"
            Assert.Equal(local, after)
            File.Delete nextA
            File.Delete nextB
            File.Delete a
            File.Delete b
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            Assert.Contains(local.Functions.Head, reopened.Declarations().Functions))

    [<Fact>]
    let ``batch review is bound to exact local bytes current manifest new content and replacement targets`` () =
        withLibraries (fun root a b _ ->
            let path, _ = diamondWithLocal root a b
            let nextA = writeVersion root "a-next" (library "a-next" [ money "Money" ] [ identity "keep"; identity "extra" ])
            let nextB = writeVersion root "b-next" (library "b-next" [ money "Money" ] [ identity "pass"; identity "more" ])
            let upgrades = [ replacement nextA libraryA; replacement nextB libraryB ]
            let preview =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                session.PreviewDependencyUpgrades upgrades |> unwrap "preview"
            let original = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
            let expectRefusal selected =
                let before = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
                match AuthoringSession.upgradeDependencies path selected preview.ReviewFingerprint with
                | Error(AuthoringSessionError.DependencyFailure _) -> ()
                | Ok session -> session.Close(); failwith "Stale or retargeted review applied."
                | other -> failwithf "Unexpected refusal: %A" other
                Assert.Equal(before, (File.ReadAllText path, File.ReadAllText(path + ".dependency.json")))
            File.WriteAllText(path, (fst original).Replace("wrap", "wrapped"))
            expectRefusal upgrades
            File.WriteAllText(path, fst original)
            File.WriteAllText(path + ".dependency.json", snd original + "\n")
            expectRefusal upgrades
            File.WriteAllText(path + ".dependency.json", snd original)
            let newSource = File.ReadAllText nextA
            File.WriteAllText(nextA, newSource.Replace("extra", "EXTRA"))
            expectRefusal upgrades
            File.WriteAllText(nextA, newSource)
            expectRefusal [ replacement nextB libraryA; replacement nextA libraryB ]
            use applied = AuthoringSession.upgradeDependencies path upgrades preview.ReviewFingerprint |> unwrap "original still applies"
            Assert.Contains("extra", applied.Declarations().Functions |> List.map (fun item -> item.DisplayName)))

    [<Fact>]
    let ``empty duplicate unknown unchanged conflicting or invalid local batch cannot alter storage`` () =
        withLibraries (fun root a b _ ->
            let path, _ = diamondWithLocal root a b
            let nextA = writeVersion root "a-next" (library "a-next" [ changedMoney ] [ identity "keep" ])
            let nextB = writeVersion root "b-next" (library "b-next" [ changedMoney ] [ identity "pass" ])
            let incompatible = writeVersion root "a-breaking" (library "a-breaking" [ changedMoney ] [ identity "other" ])
            let requests = [ replacement nextA libraryA; replacement nextB libraryB ]
            let before = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
            do
                use session = AuthoringSession.openExisting path |> unwrap "open"
                for selected in [ []; [ requests.Head; requests.Head ]; [ { requests.Head with Replaces = "unknown" } ]; [ replacement a libraryA ]; [ requests.Head ] ] do
                    Assert.True(session.PreviewDependencyUpgrades selected |> Result.isError)
                let breaking = session.PreviewDependencyUpgrades [ replacement incompatible libraryA; replacement nextB libraryB ] |> unwrap "incompatible preview"
                Assert.NotEmpty(breaking.Issues)
                // Existing ownership lock remains in force for batch startup.
                match AuthoringSession.upgradeDependencies path requests "anything" with
                | Error(AuthoringSessionError.LockUnavailable _) -> ()
                | other -> failwithf "Lock bypassed: %A" other
                session.Close()
                match AuthoringSession.upgradeDependencies path [ replacement incompatible libraryA; replacement nextB libraryB ] breaking.ReviewFingerprint with
                | Error(AuthoringSessionError.WorkspaceValidationFailure _) -> ()
                | other -> failwithf "Incompatible local references accepted: %A" other
            Assert.Equal(before, (File.ReadAllText path, File.ReadAllText(path + ".dependency.json"))))

    [<Fact>]
    let ``failed atomic batch graph write restores exact sidecar and can retry`` () =
        withLibraries (fun root a b _ ->
            let path, _ = diamondWithLocal root a b
            let nextA = writeVersion root "a-next" (library "a-next" [ changedMoney ] [ identity "keep" ])
            let nextB = writeVersion root "b-next" (library "b-next" [ changedMoney ] [ identity "pass" ])
            let upgrades = [ replacement nextA libraryA; replacement nextB libraryB ]
            let preview =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                session.PreviewDependencyUpgrades upgrades |> unwrap "preview"
            let before = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
            SessionStorageFaults.failReplacement <- fun candidate -> candidate = Path.GetFullPath path
            try
                match AuthoringSession.upgradeDependencies path upgrades preview.ReviewFingerprint with
                | Error(AuthoringSessionError.IoFailure _) -> ()
                | other -> failwithf "Failed graph write accepted: %A" other
            finally SessionStorageFaults.failReplacement <- fun _ -> false
            Assert.Equal(before, (File.ReadAllText path, File.ReadAllText(path + ".dependency.json")))
            (AuthoringSession.openExisting path |> unwrap "reopen after rollback").Close()
            (AuthoringSession.upgradeDependencies path upgrades preview.ReviewFingerprint |> unwrap "retry").Close())

    [<Fact>]
    let ``missing resolved sidecar names exact recovery path but malformed standalone JSON keeps codec diagnosis`` () =
        withLibraries (fun root a _ _ ->
            let path, _ = localOnA root a
            let sidecar = path + ".dependency.json"
            let manifest = File.ReadAllText sidecar
            File.Delete sidecar
            let expectMissing result =
                match result with
                | Error(AuthoringSessionError.DependencyFailure message) -> Assert.Contains(sidecar, message); Assert.Contains("Restore the exact sidecar", message)
                | Ok(session: AuthoringSession) -> session.Close(); failwith "Missing sidecar opened."
                | other -> failwithf "Misleading missing-sidecar diagnosis: %A" other
            expectMissing (AuthoringSession.openExisting path)
            expectMissing (AuthoringSession.pinExistingDependency path None)
            expectMissing (AuthoringSession.createNewWithDependencies (Path.Combine(root, "consumer.json")) [ path ])
            File.WriteAllText(sidecar, manifest)
            (AuthoringSession.openExisting path |> unwrap "restored exact sidecar").Close()
            let broken = Path.Combine(root, "malformed.json")
            File.WriteAllText(broken, "{\"dependency\":")
            match AuthoringSession.openExisting broken with
            | Error(AuthoringSessionError.SnapshotCodecFailure _) -> ()
            | other -> failwithf "Malformed JSON diagnostic changed: %A" other)

    [<Fact>]
    let ``reviewed selection can consolidate two changed diamond branches then split while preserving local graph`` () =
        withLibraries (fun root a b _ ->
            let path, local = diamondWithLocal root a b
            let nextA = library "a-next" [ changedMoney ] [ identity "keep" ]
            let nextB = library "b-next" [ changedMoney ] [ identity "pass" ]
            let aSource = writeVersion root "a-next" nextA
            let bSource = writeVersion root "b-next" nextB
            let consolidated = { (DependencyGraph.combine [ nextA; nextB ] |> unwrap "consolidated") with Id = id<SnapshotTag> "consolidated" }
            let consolidatedSource = writeVersion root "consolidated" consolidated
            let selection = { Sources = [ consolidatedSource ]; Replaces = [ StableId.value libraryA.Id; StableId.value libraryB.Id ] }
            let preview =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                let preview = session.PreviewDependencySelectionUpgrade selection |> unwrap "selection preview"
                Assert.Empty(preview.Issues)
                Assert.Equal(2, preview.Replaced.Length)
                Assert.Single(preview.Selected) |> ignore
                let wire = JsonSerializer.Serialize({| id = "selection"; op = "previewDependencyUpgrades"; selection = {| sources = selection.Sources; replaces = selection.Replaces |} |})
                use result = JsonDocument.Parse(AuthoringProtocol.executeLine session wire)
                Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.ToString())
                preview
            let applied = AuthoringSession.upgradeDependencySelection path selection preview.ReviewFingerprint |> unwrap "consolidate"
            Assert.Contains(local.Functions.Head, applied.Declarations().Functions)
            applied.Close()
            Assert.Equal(local, SnapshotCodec.deserializeResolved consolidated (File.ReadAllText path) |> unwrap "same local")
            let split = { Sources = [ aSource; bSource ]; Replaces = [ StableId.value consolidated.Id ] }
            let splitReview =
                use session = AuthoringSession.openExisting path |> unwrap "consolidated open"
                session.PreviewDependencySelectionUpgrade split |> unwrap "split preview"
            let splitApplied = AuthoringSession.upgradeDependencySelection path split splitReview.ReviewFingerprint |> unwrap "split"
            Assert.Contains(local.Functions.Head, splitApplied.Declarations().Functions)
            splitApplied.Close()
            File.Delete aSource
            File.Delete bSource
            File.Delete consolidatedSource
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen split without sources"
            Assert.Contains(local.Functions.Head, reopened.Declarations().Functions))

    [<Fact>]
    let ``selection upgrade retains unnamed pins and rejects no-op duplicate and unknown members`` () =
        withLibraries (fun root a b _ ->
            let path, _ = diamondWithLocal root a b
            let next = writeVersion root "a-next" (library "a-next" [ money "Money" ] [ identity "keep"; identity "extra" ])
            use session = AuthoringSession.openExisting path |> unwrap "open"
            let currentA = StableId.value libraryA.Id
            for selection in [ { Sources = []; Replaces = [ currentA ] }; { Sources = [ a ]; Replaces = [] }; { Sources = [ a ]; Replaces = [ currentA ] }; { Sources = [ next; next ]; Replaces = [ currentA ] }; { Sources = [ next ]; Replaces = [ currentA; currentA ] }; { Sources = [ next ]; Replaces = [ "unknown" ] } ] do
                Assert.True(session.PreviewDependencySelectionUpgrade selection |> Result.isError)
            let selection = { Sources = [ next ]; Replaces = [ currentA ] }
            let preview = session.PreviewDependencySelectionUpgrade selection |> unwrap "preview retained member"
            Assert.Empty(preview.Issues)
            session.Close()
            use applied = AuthoringSession.upgradeDependencySelection path selection preview.ReviewFingerprint |> unwrap "apply"
            use pins = JsonDocument.Parse(applied.DependencySelection |> Option.get)
            let members = pins.RootElement.GetProperty("dependencies").EnumerateArray() |> Seq.toList
            Assert.Contains(members, fun item -> item.GetProperty("snapshotId").GetString() = StableId.value libraryB.Id))

    [<Fact>]
    let ``N-to-M selection token refuses stale graph pins source and retarget without touching files`` () =
        withLibraries (fun root a b _ ->
            let path, _ = diamondWithLocal root a b
            let consolidated = { (DependencyGraph.combine [ library "a-next" [ changedMoney ] [ identity "keep" ]; library "b-next" [ changedMoney ] [ identity "pass" ] ] |> unwrap "consolidated") with Id = id<SnapshotTag> "consolidated-stale" }
            let source = writeVersion root "consolidated" consolidated
            let selection = { Sources = [ source ]; Replaces = [ StableId.value libraryA.Id; StableId.value libraryB.Id ] }
            let preview =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                session.PreviewDependencySelectionUpgrade selection |> unwrap "preview"
            let original = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
            let selectedSource = File.ReadAllText source
            let refuse request =
                let before = File.ReadAllText path, File.ReadAllText(path + ".dependency.json")
                match AuthoringSession.upgradeDependencySelection path request preview.ReviewFingerprint with
                | Error(AuthoringSessionError.DependencyFailure _) -> ()
                | Ok session -> session.Close(); failwith "Stale selection was applied."
                | other -> failwithf "Unexpected selection refusal: %A" other
                Assert.Equal(before, (File.ReadAllText path, File.ReadAllText(path + ".dependency.json")))
            File.WriteAllText(path, fst original + "\n")
            refuse selection
            File.WriteAllText(path, fst original)
            File.WriteAllText(path + ".dependency.json", snd original + "\n")
            refuse selection
            File.WriteAllText(path + ".dependency.json", snd original)
            File.WriteAllText(source, selectedSource.Replace("Money", "ChangedMoney"))
            refuse selection
            File.WriteAllText(source, selectedSource)
            // The only-one-target variant is also correctly refused: the other old branch
            // still collides with the new complete schema, rather than hiding that collision.
            refuse { selection with Replaces = [ selection.Replaces.Head ] }
            (AuthoringSession.upgradeDependencySelection path selection preview.ReviewFingerprint |> unwrap "unchanged original review remains usable").Close())

    [<Fact>]
    let ``selection source order shares one review and persists canonical source path order`` () =
        withLibraries (fun root a b _ ->
            let path, local = diamondWithLocal root a b
            // Deliberately name the two next sources in opposite lexical order.
            let aSource = writeVersion root "z-a-next" (library "a-next-order" [ changedMoney ] [ identity "keep" ])
            let bSource = writeVersion root "a-b-next" (library "b-next-order" [ changedMoney ] [ identity "pass" ])
            let canonicalSources = [ aSource; bSource ] |> List.sort
            let forward = { Sources = canonicalSources; Replaces = [ StableId.value libraryA.Id; StableId.value libraryB.Id ] }
            let reversed = { forward with Sources = List.rev canonicalSources }
            let preview =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                let normal = session.PreviewDependencySelectionUpgrade forward |> unwrap "forward preview"
                let reverse = session.PreviewDependencySelectionUpgrade reversed |> unwrap "reversed preview"
                Assert.Empty(normal.Issues)
                Assert.Empty(reverse.Issues)
                Assert.Equal(normal.ReviewFingerprint, reverse.ReviewFingerprint)
                Assert.Equal<AuthoringDependencyMember list>(normal.Selected, reverse.Selected)
                normal
            let applied = AuthoringSession.upgradeDependencySelection path reversed preview.ReviewFingerprint |> unwrap "reversed-source apply"
            Assert.Contains(local.Functions.Head, applied.Declarations().Functions)
            applied.Close()
            use manifest = JsonDocument.Parse(File.ReadAllText(path + ".dependency.json"))
            let savedPaths =
                manifest.RootElement.GetProperty("dependencies").EnumerateArray()
                |> Seq.map (fun item -> item.GetProperty("path").GetString())
                |> Seq.toList
            Assert.Equal<string list>(canonicalSources, savedPaths)
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen canonical selection"
            Assert.Contains(local.Functions.Head, reopened.Declarations().Functions))
