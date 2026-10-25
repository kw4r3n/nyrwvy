namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `Nyrwvy.AuthoringSession migrate` (legacy cutover, step 1): retired nodes become their
/// replacements with every stable ID kept, legacy pins become retained content, pre-21 documents get
/// explicit visibility and declared type parameter lists, a retired FailureType is rejected, the
/// result is written at the current schema and validated, the fingerprints before and after are
/// recorded, and a second run changes nothing.
[<Collection("Session storage faults")>]
module SnapshotMigrationTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("migration-" + name)
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private withDirectory run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-migrate-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try run root
        finally Directory.Delete(root, true)

    let private assertLockReleased path =
        (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)).Dispose()

    let private save path snapshot = File.WriteAllText(path, SnapshotCodec.serializeCanonical snapshot |> unwrap "serialize")
    let private manifestPath path = path + ".dependency.json"
    let private status (session: AuthoringSession) = session.Status() |> unwrap "status"
    let private migrate path = AuthoringSessionMigration.migrate path None |> unwrap "migrate"
    let private declaredVersion path = SnapshotCodec.declaredVersion (File.ReadAllText path) |> unwrap "declared version"
    let private decode path = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "decode"
    /// The codec writes declarations and nodes sorted by ID; a snapshot read back has that order.
    let private canonical (snapshot: Snapshot) = SnapshotCodec.serialize snapshot |> unwrap "canonical" |> SnapshotCodec.deserialize |> unwrap "canonical"

    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private mapType = TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ stringType; int64Type ])

    /// A function of two parameters whose body is one node over them, named after the kind.
    let private binary name parameterType resultType (kind: GraphNodeId -> GraphNodeId -> SemanticNodeKind) =
        let left, right = id<ParameterTag> (name + "-a"), id<ParameterTag> (name + "-b")
        let leftNode, rightNode, resultNode = id<GraphNodeTag> (name + "-a"), id<GraphNodeTag> (name + "-b"), id<GraphNodeTag> (name + "-result")
        { Id = id<FunctionTag> name; DisplayName = name; Exported = true; TypeParameters = []
          Parameters = [ { Id = left; DisplayName = "a"; Type = parameterType }; { Id = right; DisplayName = "b"; Type = parameterType } ]
          ResultType = resultType; RequiredEffects = []
          Body =
            { Nodes =
                [ { Id = leftNode; Kind = SemanticNodeKind.ParameterReference left }
                  { Id = rightNode; Kind = SemanticNodeKind.ParameterReference right }
                  { Id = resultNode; Kind = kind leftNode rightNode } ]
              ResultNodeId = resultNode } }

    /// `countOf(m: Map<String, Int64>, k: String, d: Int64): Int64 = MapGetOrDefault(m, k, d)`.
    let private countOf () =
        let m, k, d = id<ParameterTag> "count-m", id<ParameterTag> "count-k", id<ParameterTag> "count-d"
        let mNode, kNode, dNode, resultNode = id<GraphNodeTag> "count-m", id<GraphNodeTag> "count-k", id<GraphNodeTag> "count-d", id<GraphNodeTag> "count-result"
        { Id = id<FunctionTag> "countOf"; DisplayName = "countOf"; Exported = true; TypeParameters = []
          Parameters =
            [ { Id = m; DisplayName = "m"; Type = mapType }
              { Id = k; DisplayName = "k"; Type = stringType }
              { Id = d; DisplayName = "d"; Type = int64Type } ]
          ResultType = int64Type; RequiredEffects = []
          Body =
            { Nodes =
                [ { Id = mNode; Kind = SemanticNodeKind.ParameterReference m }
                  { Id = kNode; Kind = SemanticNodeKind.ParameterReference k }
                  { Id = dNode; Kind = SemanticNodeKind.ParameterReference d }
                  { Id = resultNode; Kind = SemanticNodeKind.MapGetOrDefault(mNode, kNode, dNode) } ]
              ResultNodeId = resultNode } }

    /// One function per retired kind.
    let private retiredSnapshot () =
        { Snapshot.empty (id<SnapshotTag> "retired") with
            Functions =
                [ binary "sameText" stringType boolType (fun a b -> SemanticNodeKind.StringEqual(a, b))
                  binary "sameNumber" int64Type boolType (fun a b -> SemanticNodeKind.Int64Equal(a, b))
                  countOf ()
                  binary "greater" int64Type boolType (fun a b -> SemanticNodeKind.Int64GreaterThan(a, b))
                  binary "atMost" int64Type boolType (fun a b -> SemanticNodeKind.Int64LessThanOrEqual(a, b))
                  binary "atLeast" int64Type boolType (fun a b -> SemanticNodeKind.Int64GreaterThanOrEqual(a, b)) ] }

    let private bodyOf (snapshot: Snapshot) name =
        (snapshot.Functions |> List.find (fun fn -> fn.DisplayName = name)).Body

    let private kindOf (graph: ValueGraph) nodeId = (graph.Nodes |> List.find (fun node -> node.Id = nodeId)).Kind

    [<Fact>]
    let ``representation-only fingerprint drift is rejected before publication`` () =
        let before = { SnapshotFingerprint = "snapshot-before"; SemanticFingerprint = "semantic-before" }
        let after = { SnapshotFingerprint = "snapshot-after"; SemanticFingerprint = "semantic-after" }
        match AuthoringSessionMigration.verifyContinuity true before after with
        | Error(AuthoringSessionError.MigrationRejected message) -> Assert.Contains("semantic fingerprint", message)
        | other -> failwithf "Fingerprint drift accepted: %A" other
        AuthoringSessionMigration.verifyContinuity false before after |> unwrap "explicit graph rewrite"
        AuthoringSessionMigration.verifyContinuity true before before |> unwrap "continuity"

    [<Fact>]
    let ``migration refuses an owned workspace and releases its lock after decode failure`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "locked.json")
            save path (TestGraph.create()).Snapshot
            let original = File.ReadAllText path
            use session = AuthoringSession.openExisting path |> unwrap "open"
            match AuthoringSessionMigration.migrate path None with
            | Error(AuthoringSessionError.LockUnavailable _) -> ()
            | other -> failwithf "Owned workspace accepted: %A" other
            Assert.Equal(original, File.ReadAllText path)
            session.Close()
            File.WriteAllText(path, "{malformed")
            match AuthoringSessionMigration.migrate path None with
            | Error(AuthoringSessionError.SnapshotCodecFailure _) -> ()
            | other -> failwithf "Malformed snapshot accepted: %A" other
            Assert.Equal("{malformed", File.ReadAllText path)
            assertLockReleased path
            File.WriteAllText(path, original)
            migrate path |> ignore)

    [<Fact>]
    let ``a derived inner ID collision rejects migration without replacing existing IDs`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "collision.json")
            let fn = countOf ()
            let collision = SnapshotMigration.innerNodeId fn.Body.ResultNodeId
            let snapshot =
                { Snapshot.empty (id<SnapshotTag> "collision") with
                    Functions = [ { fn with Body = { fn.Body with Nodes = { Id = collision; Kind = SemanticNodeKind.Int64Literal 99L } :: fn.Body.Nodes } } ] }
            save path snapshot
            let original = File.ReadAllText path
            match AuthoringSessionMigration.migrate path None with
            | Error(AuthoringSessionError.WorkspaceValidationFailure _)
            | Error(AuthoringSessionError.SnapshotCodecFailure _) -> ()
            | other -> failwithf "ID collision accepted: %A" other
            Assert.Equal(original, File.ReadAllText path)
            assertLockReleased path)

    [<Fact>]
    let ``a failed dependent snapshot replacement restores the exact legacy sidecar`` () =
        withDirectory (fun root ->
            let source = Path.Combine(root, "source.json")
            let path = Path.Combine(root, "local.json")
            let imported = retiredSnapshot ()
            save source imported
            (AuthoringSession.createNewWithDependency path (Some source) |> unwrap "create").Close()
            let original = File.ReadAllText path, File.ReadAllText(manifestPath path)
            SessionStorageFaults.failReplacement <- fun candidate -> candidate = path
            try
                match AuthoringSessionMigration.migrate path None with
                | Error(AuthoringSessionError.IoFailure _) -> ()
                | other -> failwithf "Failed replacement accepted: %A" other
            finally SessionStorageFaults.failReplacement <- fun _ -> false
            Assert.Equal(original, (File.ReadAllText path, File.ReadAllText(manifestPath path)))
            assertLockReleased path
            Assert.Empty(Directory.GetFiles(root, ".*.tmp"))
            migrate path |> ignore)

    [<Fact>]
    let ``retired nodes in conditional branches migrate without losing nested identities`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "nested.json")
            let fn = binary "nested" int64Type boolType (fun a b -> SemanticNodeKind.Int64LessThanOrEqual(a, b))
            let resultNode = List.last fn.Body.Nodes
            let conditionId, outerId, falseId = id<GraphNodeTag> "condition", id<GraphNodeTag> "outer", id<GraphNodeTag> "false"
            let nested: ValueGraph = { Nodes = [ resultNode ]; ResultNodeId = resultNode.Id }
            let body: ValueGraph =
                { Nodes =
                    (fn.Body.Nodes |> List.filter (fun node -> node.Id <> resultNode.Id)) @
                    [ { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral true }
                      { Id = outerId; Kind = SemanticNodeKind.Conditional(conditionId, nested,
                          { Nodes = [ { Id = falseId; Kind = SemanticNodeKind.BoolLiteral false } ]; ResultNodeId = falseId }) } ]
                  ResultNodeId = outerId }
            save path { Snapshot.empty (id<SnapshotTag> "nested") with Functions = [ { fn with Body = body } ] }
            let report = migrate path
            Assert.Equal<(string * int) list>([ "int64LessThanOrEqual", 1 ], report.Rewrites)
            let after = decode path
            match kindOf after.Functions.Head.Body outerId with
            | SemanticNodeKind.Conditional(_, branch, _) ->
                Assert.Equal(resultNode.Id, branch.ResultNodeId)
                Assert.Equal(SemanticNodeKind.BoolNot(SnapshotMigration.innerNodeId resultNode.Id), kindOf branch resultNode.Id)
                Assert.Equal(2, branch.Nodes.Length)
            | other -> failwithf "Conditional lost: %A" other
            Assert.False((migrate path).Changed))

    [<Fact>]
    let ``multiple retained dependencies migrate together and reopen without their sources`` () =
        withDirectory (fun root ->
            let first = retiredSnapshot ()
            let second = (TestGraph.create()).Snapshot
            let firstPath, secondPath, path = Path.Combine(root, "first.json"), Path.Combine(root, "second.json"), Path.Combine(root, "local.json")
            save firstPath first
            save secondPath second
            (AuthoringSession.createNewWithDependencies path [ firstPath; secondPath ] |> unwrap "create").Close()
            File.Delete firstPath
            File.Delete secondPath
            let report = migrate path
            Assert.Equal(2, report.Dependencies.Length)
            Assert.False report.SemanticFingerprintExpectedEqual
            let unchanged = report.Dependencies |> List.find (fun item -> item.SnapshotId = StableId.value second.Id)
            Assert.True unchanged.SemanticFingerprintExpectedEqual
            Assert.Equal(unchanged.Before, unchanged.After)
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            Assert.Equal(report.After.SnapshotFingerprint, SnapshotFingerprint.value (status reopened).SnapshotFingerprint)
            reopened.Close()
            Assert.False((migrate path).Changed))

    [<Fact>]
    let ``each retired kind is rewritten to its replacement with every stable ID kept, at schema 21, and validates`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "retired.json")
            let before = retiredSnapshot ()
            save path before
            Assert.True((declaredVersion path).Json < SnapshotSchema.current.Json)
            let report = migrate path
            Assert.True report.Changed
            Assert.Equal<(string * int) list>(
                [ "stringEqual", 1; "int64Equal", 1; "mapGetOrDefault", 1; "int64GreaterThan", 1; "int64LessThanOrEqual", 1; "int64GreaterThanOrEqual", 1 ],
                report.Rewrites)
            Assert.False report.SemanticFingerprintExpectedEqual
            Assert.NotEqual<string>(report.Before.SemanticFingerprint, report.After.SemanticFingerprint)
            Assert.Equal(SnapshotSchema.current, declaredVersion path)
            let after = decode path
            Assert.Empty(SnapshotValidator.validate after)
            let text = File.ReadAllText path
            for kind, _ in SnapshotMigration.retiredKinds do
                Assert.DoesNotContain($"\"kind\":\"{kind}\"", text)
            // Every function, parameter and node ID of the source is still there, and only the inner
            // nodes of two-node replacements are new, each derived from the node it serves.
            let ids (snapshot: Snapshot) =
                snapshot.Functions |> List.collect (fun fn -> fn.Body.Nodes |> List.map (fun node -> StableId.value node.Id)) |> Set.ofList
            let beforeIds, afterIds = ids before, ids after
            Assert.True(Set.isSubset beforeIds afterIds)
            let expectedInner =
                [ "count-result"; "atMost-result"; "atLeast-result" ]
                |> List.map (fun name -> StableId.value (SnapshotMigration.innerNodeId (id<GraphNodeTag> name)))
                |> Set.ofList
            Assert.Equal<Set<string>>(expectedInner, Set.difference afterIds beforeIds)
            let before = canonical before
            Assert.Equal<FunctionId list>(before.Functions |> List.map (fun fn -> fn.Id), after.Functions |> List.map (fun fn -> fn.Id))
            Assert.Equal<SemanticParameter list>(before.Functions |> List.collect (fun fn -> fn.Parameters), after.Functions |> List.collect (fun fn -> fn.Parameters))
            // The shapes: the result node keeps its ID and yields the same value.
            let a, b = id<GraphNodeTag> "sameText-a", id<GraphNodeTag> "sameText-b"
            Assert.Equal(SemanticNodeKind.ValueEqual(a, b), kindOf (bodyOf after "sameText") (id<GraphNodeTag> "sameText-result"))
            let a, b = id<GraphNodeTag> "sameNumber-a", id<GraphNodeTag> "sameNumber-b"
            Assert.Equal(SemanticNodeKind.ValueEqual(a, b), kindOf (bodyOf after "sameNumber") (id<GraphNodeTag> "sameNumber-result"))
            let a, b = id<GraphNodeTag> "greater-a", id<GraphNodeTag> "greater-b"
            Assert.Equal(SemanticNodeKind.Int64LessThan(b, a), kindOf (bodyOf after "greater") (id<GraphNodeTag> "greater-result"))
            let count = bodyOf after "countOf"
            let inner = SnapshotMigration.innerNodeId (id<GraphNodeTag> "count-result")
            Assert.Equal(SemanticNodeKind.MapGet(id<GraphNodeTag> "count-m", id<GraphNodeTag> "count-k"), kindOf count inner)
            Assert.Equal(SemanticNodeKind.OptionGetOrDefault(inner, id<GraphNodeTag> "count-d"), kindOf count (id<GraphNodeTag> "count-result"))
            Assert.Equal(id<GraphNodeTag> "count-result", count.ResultNodeId)
            let atMost = bodyOf after "atMost"
            let inner = SnapshotMigration.innerNodeId (id<GraphNodeTag> "atMost-result")
            Assert.Equal(SemanticNodeKind.Int64LessThan(id<GraphNodeTag> "atMost-b", id<GraphNodeTag> "atMost-a"), kindOf atMost inner)
            Assert.Equal(SemanticNodeKind.BoolNot inner, kindOf atMost (id<GraphNodeTag> "atMost-result"))
            let atLeast = bodyOf after "atLeast"
            let inner = SnapshotMigration.innerNodeId (id<GraphNodeTag> "atLeast-result")
            Assert.Equal(SemanticNodeKind.Int64LessThan(id<GraphNodeTag> "atLeast-a", id<GraphNodeTag> "atLeast-b"), kindOf atLeast inner)
            Assert.Equal(SemanticNodeKind.BoolNot inner, kindOf atLeast (id<GraphNodeTag> "atLeast-result"))
            // The migrated document opens as an ordinary workspace and projects to execution.
            use session = AuthoringSession.openExisting path |> unwrap "open"
            Assert.Equal(report.After.SnapshotFingerprint, SnapshotFingerprint.value (status session).SnapshotFingerprint)
            session.ProjectExecution() |> unwrap "project" |> ignore)

    [<Fact>]
    let ``migration is idempotent and records the fingerprints the session reports before and after`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "planner.json")
            save path (TestGraph.create()).Snapshot
            let before =
                use session = AuthoringSession.openExisting path |> unwrap "open"
                status session
            let report = migrate path
            Assert.True report.Changed
            Assert.Equal(SnapshotSchema.baseline.Json, report.SourceSchemaVersion)
            Assert.Equal(SnapshotSchema.current.Json, report.TargetSchemaVersion)
            Assert.Equal(SnapshotSchema.current.Nyason, report.TargetSchema)
            Assert.Equal(SnapshotFingerprint.value before.SnapshotFingerprint, report.Before.SnapshotFingerprint)
            Assert.Equal(SemanticFingerprint.value before.SemanticFingerprint, report.Before.SemanticFingerprint)
            // Representation only: the same semantic state, so both fingerprints are unchanged.
            Assert.True report.SemanticFingerprintExpectedEqual
            Assert.Equal(report.Before, report.After)
            Assert.Empty report.Rewrites
            Assert.Equal(AuthoringSessionMigration.toolName, report.Tool)
            Assert.False(String.IsNullOrWhiteSpace report.ToolVersion)
            let after =
                use session = AuthoringSession.openExisting path |> unwrap "reopen"
                status session
            Assert.Equal(before, after)
            let migrated = File.ReadAllText path
            let again = migrate path
            Assert.False again.Changed
            Assert.Equal(SnapshotSchema.current.Json, again.SourceSchemaVersion)
            Assert.Equal(report.After, again.Before)
            Assert.Equal(migrated, File.ReadAllText path)
            // The report's JSON carries the evidence under stable names.
            let json = JsonNode.Parse(AuthoringSessionMigration.reportJson report)
            Assert.Equal(report.Before.SnapshotFingerprint, (json["before"]["snapshotFingerprint"]).GetValue<string>())
            Assert.Equal(report.After.SemanticFingerprint, (json["after"]["semanticFingerprint"]).GetValue<string>())
            Assert.Equal(SnapshotSchema.current.Json, json["targetSchemaVersion"].GetValue<int>())
            Assert.True(json["semanticFingerprintExpectedEqual"].GetValue<bool>()))

    [<Fact>]
    let ``every declaration of a pre-21 document is marked exported and a schema-21 private declaration is kept`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "legacy.json")
            save path (TestGraph.create()).Snapshot
            Assert.DoesNotContain("\"exported\"", File.ReadAllText path)
            let report = migrate path
            Assert.Equal(3, report.DeclarationsMarkedExported)
            let text = File.ReadAllText path
            Assert.Equal(3, (text.Split("\"exported\":true").Length - 1))
            let after = decode path
            Assert.True(after.Types |> List.forall (fun item -> item.Exported))
            Assert.True(after.Functions |> List.forall (fun item -> item.Exported))
            // A document already at schema 21 keeps the visibility it declares.
            let privatePath = Path.Combine(root, "private.json")
            let fixture = (TestGraph.create()).Snapshot
            let withPrivate = { fixture with Types = fixture.Types |> List.map (fun item -> { item with Exported = false }) }
            save privatePath withPrivate
            Assert.Equal(21, (declaredVersion privatePath).Json)
            let report = migrate privatePath
            // Only the header moves to the current schema; the declared visibility is kept.
            Assert.True report.Changed
            Assert.Equal(SnapshotSchema.current, declaredVersion privatePath)
            Assert.Equal(0, report.DeclarationsMarkedExported)
            Assert.Equal(canonical withPrivate, decode privatePath))

    [<Fact>]
    let ``a generic function of a pre-21 document gets its derived type parameter list declared`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "generic.json")
            // Schema 9 form: names on the occurrences, no list on the function.
            File.WriteAllText(path,
                """{"schemaVersion":9,"snapshotId":"migration-generic","types":[],"effects":[],"capabilities":[],"functions":[{"id":"migration-second","displayName":"second","parameters":[{"id":"migration-first","displayName":"first","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","typeParameterId":"migration-T","displayName":"Item"}]}},{"id":"migration-second-parameter","displayName":"second","type":{"kind":"typeParameter","typeParameterId":"migration-U","displayName":"Marker"}}],"resultType":{"kind":"typeParameter","typeParameterId":"migration-U","displayName":"Marker"},"requiredEffects":[],"body":{"nodes":[{"id":"migration-second-result","kind":"parameterReference","parameterId":"migration-second-parameter"}],"resultNodeId":"migration-second-result"}}],"obligations":[]}""")
            let before = decode path
            let report = migrate path
            Assert.Equal(1, report.FunctionTypeParameterListsDeclared)
            Assert.True report.SemanticFingerprintExpectedEqual
            Assert.Equal(report.Before, report.After)
            let text = File.ReadAllText path
            Assert.Contains("\"typeParameters\":[{\"typeParameterId\":\"migration-T\",\"displayName\":\"Item\"},{\"typeParameterId\":\"migration-U\",\"displayName\":\"Marker\"}]", text)
            Assert.Contains("{\"kind\":\"typeParameter\",\"typeParameterId\":\"migration-T\"}", text)
            Assert.DoesNotContain("\"kind\":\"typeParameter\",\"typeParameterId\":\"migration-U\",\"displayName\"", text)
            Assert.DoesNotContain("\"kind\":\"typeParameter\",\"typeParameterId\":\"migration-T\",\"displayName\"", text)
            let after = decode path
            Assert.Equal<(TypeParameterId * string) list>(before.Functions.Head.TypeParameters, after.Functions.Head.TypeParameters)
            Assert.Equal({ before with Functions = before.Functions |> List.map (fun fn -> { fn with Exported = true }) }, after))

    [<Fact>]
    let ``a snapshot that sets the retired FailureType is rejected and left unchanged`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "failure.json")
            let effectId = id<EffectTag> "notify"
            let snapshot =
                { Snapshot.empty (id<SnapshotTag> "failure") with
                    Effects = [ { Id = effectId; DisplayName = "Notify"; Exported = true } ]
                    Capabilities =
                        [ { Id = id<CapabilityTag> "notifier"; DisplayName = "Notifier"; Exported = true
                            Operations =
                                [ { Id = id<CapabilityOperationTag> "notify"; DisplayName = "notify"; Parameters = []
                                    ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = Some stringType; EffectId = effectId } ] } ] }
            save path snapshot
            let original = File.ReadAllText path
            match AuthoringSessionMigration.migrate path None with
            | Error(AuthoringSessionError.MigrationRejected message) ->
                Assert.Contains("FailureType", message)
                Assert.Contains("'Notifier.notify'", message)
                Assert.Contains("Result", message)
            | other -> failwithf "FailureType was not rejected: %A" other
            Assert.Equal(original, File.ReadAllText path)
            Assert.Equal("MigrationRejected", (WireEncode.startupMessage (AuthoringSessionError.MigrationRejected "x")).Split(':').[0]))

    [<Fact>]
    let ``a legacy pin becomes retained content and the dependent keeps its identity and fingerprints`` () =
        withDirectory (fun root ->
            let imported = (TestGraph.create()).Snapshot
            let source = Path.Combine(root, "source.json")
            let local = Path.Combine(root, "local.json")
            save source imported
            File.WriteAllText(local, SnapshotCodec.serializeResolvedCanonical imported (Snapshot.empty (id<SnapshotTag> "local")) |> unwrap "local")
            File.WriteAllText(manifestPath local, JsonSerializer.Serialize(
                {| path = source; snapshotId = StableId.value imported.Id
                   fingerprint = SnapshotFingerprint.compute imported |> unwrap "fingerprint" |> SnapshotFingerprint.value |}))
            let before =
                use session = AuthoringSession.openExisting local |> unwrap "open legacy"
                Assert.Equal("legacySource", JsonNode.Parse(session.DependencySelection.Value).["storage"].GetValue<string>())
                status session
            let report = migrate local
            Assert.True report.Changed
            Assert.True report.SemanticFingerprintExpectedEqual
            Assert.Equal(report.Before, report.After)
            let dependency = Assert.Single report.Dependencies
            Assert.Equal("legacySource", dependency.StorageBefore)
            Assert.Equal(StableId.value imported.Id, dependency.SnapshotId)
            Assert.Equal(source, dependency.Path)
            Assert.Equal(dependency.Before, dependency.After)
            Assert.Equal(SnapshotSchema.baseline.Json, dependency.SourceSchemaVersion)
            Assert.Equal(3, dependency.DeclarationsMarkedExported)
            // The sidecar now embeds the dependency at the current schema; the source is not needed.
            let manifest = JsonNode.Parse(File.ReadAllText(manifestPath local))
            Assert.Equal(dependency.Before.SnapshotFingerprint, manifest["fingerprint"].GetValue<string>())
            let retained = manifest["snapshotJson"].GetValue<string>()
            Assert.Equal(SnapshotSchema.current, SnapshotCodec.declaredVersion retained |> unwrap "retained version")
            Assert.Equal(canonical imported, SnapshotCodec.deserialize retained |> unwrap "retained")
            Assert.Equal(SnapshotSchema.current, declaredVersion local)
            File.Delete source
            let after =
                use session = AuthoringSession.openExisting local |> unwrap "reopen"
                Assert.Equal("embedded", JsonNode.Parse(session.DependencySelection.Value).["storage"].GetValue<string>())
                Assert.Equal(2, session.Declarations().Functions.Length)
                status session
            Assert.Equal(before, after)
            let again = migrate local
            Assert.False again.Changed)

    [<Fact>]
    let ``a legacy pin whose source moved is migrated from the exact copy named by --dependency`` () =
        withDirectory (fun root ->
            let imported = (TestGraph.create()).Snapshot
            let source = Path.Combine(root, "source.json")
            let local = Path.Combine(root, "local.json")
            save source imported
            File.WriteAllText(local, SnapshotCodec.serializeResolvedCanonical imported (Snapshot.empty (id<SnapshotTag> "moved")) |> unwrap "local")
            File.WriteAllText(manifestPath local, JsonSerializer.Serialize(
                {| path = source; snapshotId = StableId.value imported.Id
                   fingerprint = SnapshotFingerprint.compute imported |> unwrap "fingerprint" |> SnapshotFingerprint.value |}))
            let archived = Path.Combine(root, "archived.json")
            File.Move(source, archived)
            let localBytes, manifestBytes = File.ReadAllText local, File.ReadAllText(manifestPath local)
            match AuthoringSessionMigration.migrate local None with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Unavailable legacy source accepted: %A" other
            Assert.Equal(localBytes, File.ReadAllText local)
            Assert.Equal(manifestBytes, File.ReadAllText(manifestPath local))
            // Changed content under the same ID is not the pinned version.
            let changed = Path.Combine(root, "changed.json")
            save changed { imported with Functions = [ imported.Functions.Head ] }
            match AuthoringSessionMigration.migrate local (Some changed) with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Changed legacy source accepted: %A" other
            let report = AuthoringSessionMigration.migrate local (Some archived) |> unwrap "migrate with copy"
            Assert.Equal("legacySource", (Assert.Single report.Dependencies).StorageBefore)
            File.Delete archived
            use session = AuthoringSession.openExisting local |> unwrap "reopen"
            Assert.Equal("embedded", JsonNode.Parse(session.DependencySelection.Value).["storage"].GetValue<string>())
            // A standalone workspace has no pin for --dependency to name.
            let standalone = Path.Combine(root, "standalone.json")
            save standalone imported
            match AuthoringSessionMigration.migrate standalone (Some archived) with
            | Error(AuthoringSessionError.MigrationRejected _) -> ()
            | other -> failwithf "Standalone --dependency accepted: %A" other)

    [<Fact>]
    let ``retired nodes inside a retained dependency are rewritten and the dependent rebinds to the migrated content`` () =
        withDirectory (fun root ->
            let imported = retiredSnapshot ()
            let source = Path.Combine(root, "source.json")
            let local = Path.Combine(root, "local.json")
            save source imported
            let created = AuthoringSession.createNewWithDependency local (Some source) |> unwrap "create"
            let beforeStatus = status created
            created.Close()
            let report = migrate local
            Assert.True report.Changed
            Assert.False report.SemanticFingerprintExpectedEqual
            Assert.Empty report.Rewrites
            let dependency = Assert.Single report.Dependencies
            Assert.Equal("embedded", dependency.StorageBefore)
            Assert.Equal(6, dependency.Rewrites.Length)
            Assert.False dependency.SemanticFingerprintExpectedEqual
            Assert.NotEqual<string>(dependency.Before.SnapshotFingerprint, dependency.After.SnapshotFingerprint)
            Assert.Equal(SnapshotFingerprint.value beforeStatus.SnapshotFingerprint, report.Before.SnapshotFingerprint)
            // The manifest names the migrated content's fingerprint, the local snapshot binds to it, and
            // the migrated dependency is what the reopened session resolves against.
            let manifest = JsonNode.Parse(File.ReadAllText(manifestPath local))
            Assert.Equal(dependency.After.SnapshotFingerprint, manifest["fingerprint"].GetValue<string>())
            Assert.DoesNotContain("\"int64GreaterThan\"", manifest["snapshotJson"].GetValue<string>())
            use session = AuthoringSession.openExisting local |> unwrap "reopen"
            let after = status session
            Assert.Equal(report.After.SnapshotFingerprint, SnapshotFingerprint.value after.SnapshotFingerprint)
            Assert.Equal(report.After.SemanticFingerprint, SemanticFingerprint.value after.SemanticFingerprint)
            Assert.Equal(6, session.Declarations().Functions.Length)
            session.ProjectExecution() |> unwrap "project" |> ignore
            session.Close()
            // A dependency that sets the retired FailureType rejects the whole migration.
            let effectId = id<EffectTag> "dep-notify"
            let failing =
                { Snapshot.empty (id<SnapshotTag> "failing") with
                    Effects = [ { Id = effectId; DisplayName = "Notify"; Exported = true } ]
                    Capabilities =
                        [ { Id = id<CapabilityTag> "dep-notifier"; DisplayName = "Notifier"; Exported = true
                            Operations =
                                [ { Id = id<CapabilityOperationTag> "dep-notify"; DisplayName = "notify"; Parameters = []
                                    ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = Some stringType; EffectId = effectId } ] } ] }
            let failingSource = Path.Combine(root, "failing.json")
            let failingLocal = Path.Combine(root, "failing-local.json")
            save failingSource failing
            (AuthoringSession.createNewWithDependency failingLocal (Some failingSource) |> unwrap "create failing").Close()
            let bytes = File.ReadAllText failingLocal, File.ReadAllText(manifestPath failingLocal)
            match AuthoringSessionMigration.migrate failingLocal None with
            | Error(AuthoringSessionError.MigrationRejected message) -> Assert.Contains("dependency", message)
            | other -> failwithf "Dependency FailureType accepted: %A" other
            Assert.Equal(bytes, (File.ReadAllText failingLocal, File.ReadAllText(manifestPath failingLocal))))

    let private repositoryRoot () =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName
            elif isNull directory.Parent then failwith "Could not locate repository root."
            else find directory.Parent
        find (DirectoryInfo(Environment.CurrentDirectory))

    let private runTool (arguments: string list) =
        let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
        let toolPath = Path.Combine(repositoryRoot (), "tools", "Nyrwvy.AuthoringSession", "bin", configuration, "net10.0", "Nyrwvy.AuthoringSession.dll")
        if not (File.Exists toolPath) then failwithf "Authoring-session executable was not built: %s" toolPath
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add toolPath
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        child.StandardInput.Close()
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        child.ExitCode, stdout.Result, stderr.Result

    [<Fact>]
    let ``the migrate command prints the report and exits without starting a session; a rejection exits with status 2`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "cli.json")
            save path (TestGraph.create()).Snapshot
            let exitCode, stdout, stderr = runTool [ "migrate"; "--snapshot"; path ]
            Assert.True((exitCode = 0), stderr)
            let report = JsonNode.Parse stdout
            Assert.True(report["changed"].GetValue<bool>())
            Assert.Equal(9, report["sourceSchemaVersion"].GetValue<int>())
            Assert.Equal(SnapshotSchema.current.Json, report["targetSchemaVersion"].GetValue<int>())
            Assert.Equal((report["before"]["semanticFingerprint"]).GetValue<string>(), (report["after"]["semanticFingerprint"]).GetValue<string>())
            Assert.Equal(SnapshotSchema.current, declaredVersion path)
            assertLockReleased path
            let failing = Path.Combine(root, "failing.json")
            let effectId = id<EffectTag> "cli-notify"
            save failing
                { Snapshot.empty (id<SnapshotTag> "cli-failing") with
                    Effects = [ { Id = effectId; DisplayName = "Notify"; Exported = true } ]
                    Capabilities =
                        [ { Id = id<CapabilityTag> "cli-notifier"; DisplayName = "Notifier"; Exported = true
                            Operations =
                                [ { Id = id<CapabilityOperationTag> "cli-notify"; DisplayName = "notify"; Parameters = []
                                    ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = Some stringType; EffectId = effectId } ] } ] }
            let exitCode, stdout, stderr = runTool [ "migrate"; "--snapshot"; failing ]
            Assert.Equal(2, exitCode)
            Assert.Equal("", stdout)
            Assert.StartsWith("MigrationRejected: ", stderr)
            let exitCode, _, stderr = runTool [ "migrate"; "--snapshot" ]
            Assert.Equal(2, exitCode)
            Assert.Contains("migrate --snapshot", stderr))

    // runPure parity through the JSONL protocol, as the Map and Int64 tests do.
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private str, i64, boolean = primitive "string", primitive "int64", primitive "bool"
    let private wordCounts = $"""{{"kind":"builtinGeneric","generic":"map","arguments":[{str},{i64}]}}"""
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""

    let private define session name (parameters: (string * string) list) resultType body =
        let parameterList =
            parameters |> List.map (fun (parameterName, parameterType) -> $"""{{"displayName":"{parameterName}","type":{parameterType}}}""") |> String.concat ","
        publish session
            $"""{{"id":"{name}","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{parameterList}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private text (value: string) = $"""{{"kind":"string","value":{JsonSerializer.Serialize value}}}"""
    let private int64 (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private bool (value: bool) = $"""{{"kind":"bool","value":{(if value then "true" else "false")}}}"""
    let private counts (entries: (string * int64) list) =
        let items = entries |> List.map (fun (key, value) -> $"""{{"key":{text key},"value":{int64 value}}}""") |> String.concat ","
        $"""{{"kind":"map","keyType":{str},"valueType":{i64},"entries":[{items}]}}"""

    let private expectRun session name (arguments: string list) expected =
        let run = result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    /// The same runPure cases before and after the migration.
    let private cases session =
        expectRun session "sameText" [ text "a"; text "a" ] (bool true)
        expectRun session "sameText" [ text "a"; text "b" ] (bool false)
        expectRun session "sameNumber" [ int64 3L; int64 3L ] (bool true)
        expectRun session "sameNumber" [ int64 3L; int64 -3L ] (bool false)
        expectRun session "countOf" [ counts [ "a", 1L; "b", 2L ]; text "b"; int64 0L ] (int64 2L)
        expectRun session "countOf" [ counts [ "a", 1L ]; text "z"; int64 -1L ] (int64 -1L)
        for a, b in [ 1L, 2L; 2L, 2L; 3L, 2L; Int64.MinValue, Int64.MaxValue; Int64.MaxValue, Int64.MinValue ] do
            expectRun session "greater" [ int64 a; int64 b ] (bool (a > b))
            expectRun session "atMost" [ int64 a; int64 b ] (bool (a <= b))
            expectRun session "atLeast" [ int64 a; int64 b ] (bool (a >= b))

    [<Fact; Trait("Suite", "Extended")>]
    let ``functions authored with retired recipe kinds run the same under runPure after the migration`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "workspace.json")
            let session = AuthoringSession.createNew path |> unwrap "create"
            let a, b = parameter "a", parameter "b"
            define session "sameText" [ "a", str; "b", str ] boolean $"""{{"kind":"stringEqual","left":{a},"right":{b}}}"""
            define session "sameNumber" [ "a", i64; "b", i64 ] boolean $"""{{"kind":"int64Equal","left":{a},"right":{b}}}"""
            define session "countOf" [ "m", wordCounts; "k", str; "d", i64 ] i64 $"""{{"kind":"mapGetOrDefault","map":{parameter "m"},"key":{parameter "k"},"default":{parameter "d"}}}"""
            define session "greater" [ "a", i64; "b", i64 ] boolean $"""{{"kind":"int64GreaterThan","left":{a},"right":{b}}}"""
            define session "atMost" [ "a", i64; "b", i64 ] boolean $"""{{"kind":"int64LessThanOrEqual","left":{a},"right":{b}}}"""
            define session "atLeast" [ "a", i64; "b", i64 ] boolean $"""{{"kind":"int64GreaterThanOrEqual","left":{a},"right":{b}}}"""
            cases session
            session.Close()
            let text = File.ReadAllText path
            for kind, _ in SnapshotMigration.retiredKinds do
                Assert.Contains($"\"kind\":\"{kind}\"", text)
            let report = migrate path
            Assert.Equal(6, report.Rewrites.Length)
            let text = File.ReadAllText path
            for kind, _ in SnapshotMigration.retiredKinds do
                Assert.DoesNotContain($"\"kind\":\"{kind}\"", text)
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            cases reopened
            Assert.Equal(report.After.SnapshotFingerprint, SnapshotFingerprint.value (status reopened).SnapshotFingerprint))
