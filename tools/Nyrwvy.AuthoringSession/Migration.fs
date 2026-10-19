namespace Nyrwvy.Tools.AuthoringSession

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core

/// The fingerprints of one graph before and after its migration, as `status` reports them: for
/// the local snapshot the resolved graph (imports included), for a dependency its own graph.
type MigrationFingerprints =
    { SnapshotFingerprint: string
      SemanticFingerprint: string }

/// The migration of one pinned dependency retained in the sidecar.
type MigratedDependencyReport =
    { SnapshotId: string
      /// Original-source provenance from the manifest.
      Path: string
      /// `embedded` or `legacySource` (a manifest without retained content); after the migration
      /// every dependency is `embedded`.
      StorageBefore: string
      SourceSchemaVersion: int
      TargetSchemaVersion: int
      Before: MigrationFingerprints
      After: MigrationFingerprints
      Rewrites: (string * int) list
      DeclarationsMarkedExported: int
      FunctionTypeParameterListsDeclared: int
      /// Whether semantic fingerprint equality was an asserted invariant (no retired node rewritten).
      SemanticFingerprintExpectedEqual: bool }

/// The evidence `migrate` records for one workspace (docs/specs/schema-evolution-policy.md,
/// migration fingerprints and evidence).
type MigrationReport =
    { Path: string
      /// False when the snapshot and its sidecar were already in the current form; nothing was
      /// written then.
      Changed: bool
      SourceSchemaVersion: int
      SourceSchema: string
      TargetSchemaVersion: int
      TargetSchema: string
      Before: MigrationFingerprints
      After: MigrationFingerprints
      Rewrites: (string * int) list
      DeclarationsMarkedExported: int
      FunctionTypeParameterListsDeclared: int
      /// Whether semantic fingerprint equality was an asserted invariant for the whole resolved
      /// graph: no retired node was rewritten in the local snapshot or in any dependency.
      SemanticFingerprintExpectedEqual: bool
      Dependencies: MigratedDependencyReport list
      Tool: string
      ToolVersion: string }

/// `Nyrwvy.AuthoringSession migrate --snapshot <path> [--dependency <exact legacy source copy>]`:
/// rewrites a workspace to the current snapshot schema under the workspace lock and reports the
/// evidence. The rewrite itself is `SnapshotMigration.migrate`; this module owns the files: the
/// local snapshot, its dependency sidecar (each retained member is migrated and re-embedded; a
/// legacy source-only manifest becomes retained content) and the local snapshot's binding to the
/// migrated dependency digest. The result is validated as a resolved graph before anything is
/// written, and the pair is replaced with the same rollback as a dependency upgrade.
[<RequireQualifiedAccess>]
module AuthoringSessionMigration =
    let toolName = "Nyrwvy.AuthoringSession migrate"

    let private toolVersion () =
        let assembly = typeof<MigrationReport>.Assembly
        let informational =
            assembly.GetCustomAttributes(typeof<Reflection.AssemblyInformationalVersionAttribute>, false)
            |> Seq.tryHead
            |> Option.map (fun attribute -> (attribute :?> Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)
        match informational with
        | Some version when not (String.IsNullOrWhiteSpace version) -> version
        | _ -> string (assembly.GetName().Version)

    let private codec (value: Result<'T, SnapshotCodecError>): Result<'T, AuthoringSessionError> =
        Result.mapError AuthoringSessionError.SnapshotCodecFailure value

    let private fingerprints (snapshot: Snapshot): Result<MigrationFingerprints, AuthoringSessionError> =
        match SnapshotFingerprint.compute snapshot, SemanticFingerprint.compute snapshot with
        | Ok snapshotFingerprint, Ok semanticFingerprint ->
            Ok { SnapshotFingerprint = SnapshotFingerprint.value snapshotFingerprint
                 SemanticFingerprint = SemanticFingerprint.value semanticFingerprint }
        | Error error, _
        | _, Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)

    /// Representation-only migration must prove continuity before replacing either file.
    let internal verifyContinuity expectedEqual (before: MigrationFingerprints) (after: MigrationFingerprints) =
        if expectedEqual && before.SemanticFingerprint <> after.SemanticFingerprint then
            Error(AuthoringSessionError.MigrationRejected "Representation-only migration changed the semantic fingerprint; nothing was written.")
        else Ok()

    let private rejected (subject: string) = function
        | SnapshotMigrationError.RetiredFailureType operations ->
            let listed =
                operations
                |> List.map (fun (capability, operation) -> $"'{capability}.{operation}'")
                |> String.concat ", "
            AuthoringSessionError.MigrationRejected
                $"{subject} sets the retired FailureType on capability operation(s) {listed}; it has no faithful rewrite. Declare the expected failure as the operation's Result result type and migrate again (docs/decisions/capability-failure-type-retired.md)."

    /// The schema version a retained member's document declares: from its embedded content, or
    /// from the exact source copy a legacy manifest was resolved against.
    let private memberDeclaredVersion (selected: PinnedDependencySelection): Result<SnapshotSchemaVersion, AuthoringSessionError> =
        let text =
            if selected.IsEmbedded then
                try
                    use document = JsonDocument.Parse selected.Manifest
                    Ok(document.RootElement.GetProperty("snapshotJson").GetString())
                with
                | :? JsonException
                | :? InvalidOperationException
                | :? Collections.Generic.KeyNotFoundException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency manifest.")
            else SessionStorage.readText selected.SourcePath
        text |> Result.bind (SnapshotCodec.declaredVersion >> codec)

    let private migrateMember (selected: PinnedDependencySelection): Result<PinnedDependencySelection * MigratedDependencyReport, AuthoringSessionError> =
        memberDeclaredVersion selected
        |> Result.bind (fun declared ->
            SnapshotMigration.migrate declared selected.Imported
            |> Result.mapError (rejected $"The dependency '{StableId.value selected.Imported.Id}' (retained from '{selected.SourcePath}')")
            |> Result.bind (fun outcome ->
                fingerprints selected.Imported
                |> Result.bind (fun before ->
                    fingerprints outcome.Snapshot
                    |> Result.bind (fun after ->
                        verifyContinuity (SnapshotMigration.representationOnly outcome) before after
                        |> Result.bind (fun () ->
                        PinnedDependency.embedWith SnapshotCodec.serializeCurrent selected.SourcePath outcome.Snapshot
                        |> Result.map (fun pinned ->
                            pinned,
                            { SnapshotId = StableId.value selected.Imported.Id
                              Path = selected.SourcePath
                              StorageBefore = if selected.IsEmbedded then "embedded" else "legacySource"
                              SourceSchemaVersion = declared.Json
                              TargetSchemaVersion = outcome.TargetSchema.Json
                              Before = before
                              After = after
                              Rewrites = outcome.Rewrites
                              DeclarationsMarkedExported = outcome.DeclarationsMarkedExported
                              FunctionTypeParameterListsDeclared = outcome.FunctionTypeParameterListsDeclared
                              SemanticFingerprintExpectedEqual = SnapshotMigration.representationOnly outcome }))))))

    let private migrateSelection (selected: PinnedDependencySelection): Result<PinnedDependencySelection * MigratedDependencyReport list, AuthoringSessionError> =
        let members = if List.isEmpty selected.Members then [ selected ] else selected.Members
        members
        |> List.fold (fun state item ->
            state |> Result.bind (fun migrated -> migrateMember item |> Result.map (fun result -> migrated @ [ result ]))) (Ok [])
        |> Result.bind (fun migrated ->
            PinnedDependency.combine (migrated |> List.map fst)
            |> Result.map (fun combined -> combined, migrated |> List.map snd))

    type private ResultBuilder() =
        member _.Bind(value: Result<'T, 'E>, continuation: 'T -> Result<'U, 'E>) = Result.bind continuation value
        member _.Return(value: 'T): Result<'T, 'E> = Ok value
        member _.ReturnFrom(value: Result<'T, 'E>) = value

    let private result = ResultBuilder()

    let private migrateUnlocked (path: string) (sourceOverride: string option): Result<MigrationReport, AuthoringSessionError> =
        result {
            let! source = SessionStorage.readText path
            let! loaded = PinnedDependency.load path sourceOverride
            let! selected =
                match loaded, sourceOverride with
                | None, Some _ ->
                    Error(AuthoringSessionError.MigrationRejected "This workspace has no dependency selection; --dependency names the exact source copy of a legacy pin only.")
                | None, None -> SessionStorage.requireSidecarForResolvedSource path source |> Result.map (fun () -> None)
                | Some _, _ -> Ok loaded
            let! declared = SnapshotCodec.declaredVersion source |> codec
            let! local =
                match selected with
                | None -> SnapshotCodec.deserialize source
                | Some selected -> SnapshotCodec.deserializeResolved selected.Imported source
                |> codec
            let! migratedSelection =
                match selected with
                | None -> Ok None
                | Some selected -> migrateSelection selected |> Result.map Some
            let! outcome = SnapshotMigration.migrate declared local |> Result.mapError (rejected "The snapshot")
            let resolvedBefore =
                match selected with
                | None -> local
                | Some selected -> SnapshotCodec.resolveDependency selected.Imported local
            let resolvedAfter =
                match migratedSelection with
                | None -> outcome.Snapshot
                | Some(combined, _) -> SnapshotCodec.resolveDependency combined.Imported outcome.Snapshot
            let! before = fingerprints resolvedBefore
            let! migratedSource =
                match migratedSelection with
                | None -> SnapshotCodec.serializeCurrent outcome.Snapshot
                | Some(combined, _) -> SnapshotCodec.serializeResolvedCurrent combined.Imported outcome.Snapshot
                |> Result.mapError (function
                    | SnapshotCodecError.InvalidSnapshot issues -> AuthoringSessionError.WorkspaceValidationFailure issues
                    | other -> AuthoringSessionError.SnapshotCodecFailure other)
            let! after = fingerprints resolvedAfter
            let dependencyReports = migratedSelection |> Option.map snd |> Option.defaultValue []
            let expectedEqual =
                SnapshotMigration.representationOnly outcome
                && (dependencyReports |> List.forall (fun item -> item.SemanticFingerprintExpectedEqual))
            do! verifyContinuity expectedEqual before after
            let manifestChanged =
                match selected, migratedSelection with
                | Some current, Some(combined, _) -> current.Manifest <> combined.Manifest
                | _ -> false
            let changed = migratedSource <> source || manifestChanged
            do!
                if not changed then Ok()
                else
                    match selected, migratedSelection with
                    | Some current, Some(combined, _) ->
                        SessionStorage.replaceDependencyPair path source current.Manifest migratedSource combined.Manifest
                    | _ ->
                        SessionStorage.readText path
                        |> Result.bind (fun current ->
                            if current <> source then Error AuthoringSessionError.ExternalModification
                            else SessionStorage.replaceAtomically path migratedSource)
            return
                { Path = path
                  Changed = changed
                  SourceSchemaVersion = declared.Json
                  SourceSchema = declared.Nyason
                  TargetSchemaVersion = outcome.TargetSchema.Json
                  TargetSchema = outcome.TargetSchema.Nyason
                  Before = before
                  After = after
                  Rewrites = outcome.Rewrites
                  DeclarationsMarkedExported = outcome.DeclarationsMarkedExported
                  FunctionTypeParameterListsDeclared = outcome.FunctionTypeParameterListsDeclared
                  SemanticFingerprintExpectedEqual = expectedEqual
                  Dependencies = dependencyReports
                  Tool = toolName
                  ToolVersion = toolVersion () }
        }

    /// Migrates the workspace at `path`. `sourceOverride` is the exact copy of a legacy pin's
    /// source when the manifest's path is unavailable, as for `pin`. Nothing is written when the
    /// workspace is already in the current form (`Changed = false`). Validation failures write
    /// nothing; a reported snapshot-write failure rolls the sidecar back as for an upgrade.
    let migrate (path: string) (sourceOverride: string option): Result<MigrationReport, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        let path = SessionStorage.fullPath path
        if not (File.Exists path) then Error(AuthoringSessionError.SnapshotFileNotFound path)
        else
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                try migrateUnlocked path sourceOverride
                finally SessionStorage.releaseLock path lockHandle

    let private fingerprintsJson (value: MigrationFingerprints) =
        let node = JsonObject()
        node["snapshotFingerprint"] <- JsonValue.Create value.SnapshotFingerprint
        node["semanticFingerprint"] <- JsonValue.Create value.SemanticFingerprint
        node

    let private rewritesJson (rewrites: (string * int) list) =
        let items = JsonArray()
        for kind, count in rewrites do
            let item = JsonObject()
            item["kind"] <- JsonValue.Create kind
            item["count"] <- JsonValue.Create count
            items.Add item
        items

    /// The report as JSON, the command's standard output: source and target schema, the
    /// fingerprints before and after, what was rewritten, and the same for every dependency.
    let reportJson (report: MigrationReport): string =
        let root = JsonObject()
        root["tool"] <- JsonValue.Create report.Tool
        root["toolVersion"] <- JsonValue.Create report.ToolVersion
        root["path"] <- JsonValue.Create report.Path
        root["changed"] <- JsonValue.Create report.Changed
        root["sourceSchemaVersion"] <- JsonValue.Create report.SourceSchemaVersion
        root["sourceSchema"] <- JsonValue.Create report.SourceSchema
        root["targetSchemaVersion"] <- JsonValue.Create report.TargetSchemaVersion
        root["targetSchema"] <- JsonValue.Create report.TargetSchema
        root["before"] <- fingerprintsJson report.Before
        root["after"] <- fingerprintsJson report.After
        root["semanticFingerprintExpectedEqual"] <- JsonValue.Create report.SemanticFingerprintExpectedEqual
        root["rewrites"] <- rewritesJson report.Rewrites
        root["declarationsMarkedExported"] <- JsonValue.Create report.DeclarationsMarkedExported
        root["functionTypeParameterListsDeclared"] <- JsonValue.Create report.FunctionTypeParameterListsDeclared
        let dependencies = JsonArray()
        for item in report.Dependencies do
            let node = JsonObject()
            node["snapshotId"] <- JsonValue.Create item.SnapshotId
            node["path"] <- JsonValue.Create item.Path
            node["storageBefore"] <- JsonValue.Create item.StorageBefore
            node["storageAfter"] <- JsonValue.Create "embedded"
            node["sourceSchemaVersion"] <- JsonValue.Create item.SourceSchemaVersion
            node["targetSchemaVersion"] <- JsonValue.Create item.TargetSchemaVersion
            node["before"] <- fingerprintsJson item.Before
            node["after"] <- fingerprintsJson item.After
            node["semanticFingerprintExpectedEqual"] <- JsonValue.Create item.SemanticFingerprintExpectedEqual
            node["rewrites"] <- rewritesJson item.Rewrites
            node["declarationsMarkedExported"] <- JsonValue.Create item.DeclarationsMarkedExported
            node["functionTypeParameterListsDeclared"] <- JsonValue.Create item.FunctionTypeParameterListsDeclared
            dependencies.Add node
        root["dependencies"] <- dependencies
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))
