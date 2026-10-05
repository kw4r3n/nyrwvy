namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core

[<RequireQualifiedAccess>]
type AuthoringSessionError =
    | SnapshotFileNotFound of string
    | SnapshotFileAlreadyExists of string
    | LockUnavailable of string
    | IoFailure of string
    | SnapshotCodecFailure of SnapshotCodecError
    | WorkspaceValidationFailure of ValidationIssue list
    | ProposalNotFound of string
    | SessionClosed
    | ExternalModification
    | SessionInvalidated
    | WorkflowSimulationFailure of AgentWorkflowError
    | StructuralSimulationFailure of AgentStructuralWorkflowError
    | TypeDeclarationSimulationFailure of AgentTypeDeclarationWorkflowError
    | EffectDeclarationSimulationFailure of AgentEffectDeclarationWorkflowError
    | CapabilityDeclarationSimulationFailure of AgentCapabilityDeclarationWorkflowError
    | CommitRejected of TransactionDiagnostic list
    | SemanticProjectionFailure of TypedSemanticIrError
    | ExecutionLoweringFailure of ExecutionIrError
    | BackendFailure of CSharpBackendError
    | DependencyFailure of string

type AuthoringSessionStatus =
    { SnapshotId: SnapshotId
      SnapshotFingerprint: SnapshotFingerprint
      SemanticFingerprint: SemanticFingerprint }

type AuthoringDeclarations =
    { Functions: SemanticFunction list
      Types: SemanticType list
      Effects: SemanticEffect list
      Capabilities: SemanticCapability list }

[<RequireQualifiedAccess>]
type AuthoringProposalReview =
    | Operations of AgentChangeReview
    | ReplaceBody of AgentExpressionStructuralChangeReview
    | ReplaceTarget of AgentExpressionStructuralWorkflow.Targeted.Review
    | CreateFunction of AgentExpressionStructuralWorkflow.FunctionCreation.Review
    | ParameterTypeMigration of AgentParameterTypeMigrationReview
    | ResultTypeMigration of AgentResultTypeMigrationReview
    | ProductFieldTypeMigration of AgentProductFieldTypeMigrationReview
    | CreateType of AgentTypeDeclarationReview
    | CreateTypeGroup of AgentTypeDeclarationGroupReview
    | FieldlessUnionVariantEvolution of AgentFieldlessUnionVariantEvolutionReview
    | CreateEffect of AgentEffectDeclarationReview
    | CreateCapability of AgentCapabilityDeclarationReview

type AuthoringProposal =
    { ProposalId: string
      Review: AuthoringProposalReview
      /// Proposed bodies of the functions a migration or union-variant evolution rewrites, in
      /// review order; empty for other reviews. Rendered only by a full review.
      RewrittenFunctions: SemanticFunction list }

/// What a human Change Review page of a pending proposal reads. `ProposedSnapshot` is absent
/// for an operations proposal, whose review already carries its Change Review projection.
type AuthoringPendingProposalView =
    { TransactionId: TransactionId
      Proposal: AuthoringProposal
      BaseSnapshotId: SnapshotId
      BaseIsCommitted: bool
      ProposedSnapshot: Snapshot option
      /// Functions the proposal creates or rewrites, in review order.
      FunctionIds: FunctionId list
      /// Types the proposal creates or rewrites, in review order.
      TypeIds: TypeId list }

type AuthoringCommitReceipt =
    { TransactionId: TransactionId
      PublishedSnapshotId: SnapshotId
      Readiness: TransactionReadiness }

type AuthoringRejectionReceipt =
    { TransactionId: TransactionId
      RetainedSnapshotId: SnapshotId }

[<RequireQualifiedAccess>]
type private PendingProposal =
    | Operations of AgentChangeProposal
    | ReplaceBody of AgentExpressionStructuralChangeProposal
    | ReplaceTarget of AgentExpressionStructuralWorkflow.Targeted.Proposal
    | CreateFunction of AgentExpressionStructuralWorkflow.FunctionCreation.Proposal
    | ParameterTypeMigration of AgentParameterTypeMigrationProposal
    | ResultTypeMigration of AgentResultTypeMigrationProposal
    | ProductFieldTypeMigration of AgentProductFieldTypeMigrationProposal
    | CreateType of AgentTypeDeclarationProposal
    | CreateTypeGroup of AgentTypeDeclarationGroupProposal
    | FieldlessUnionVariantEvolution of AgentFieldlessUnionVariantEvolutionProposal
    | CreateEffect of AgentEffectDeclarationProposal
    | CreateCapability of AgentCapabilityDeclarationProposal

module private PendingProposal =
    let proposedSnapshot (pending: PendingProposal): Snapshot option =
        match pending with
        | PendingProposal.Operations _ -> None
        | PendingProposal.ReplaceBody proposal -> Some(AgentExpressionStructuralChangeProposal.proposedSnapshot proposal)
        | PendingProposal.ReplaceTarget proposal -> Some(AgentExpressionStructuralWorkflow.Targeted.Proposal.proposedSnapshot proposal)
        | PendingProposal.CreateFunction proposal -> Some(AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.proposedSnapshot proposal)
        | PendingProposal.ParameterTypeMigration proposal -> Some(AgentParameterTypeMigrationProposal.proposedSnapshot proposal)
        | PendingProposal.ResultTypeMigration proposal -> Some(AgentResultTypeMigrationProposal.proposedSnapshot proposal)
        | PendingProposal.ProductFieldTypeMigration proposal -> Some(AgentProductFieldTypeMigrationProposal.proposedSnapshot proposal)
        | PendingProposal.CreateType proposal -> Some(AgentTypeDeclarationProposal.proposedSnapshot proposal)
        | PendingProposal.CreateTypeGroup proposal -> Some(AgentTypeDeclarationGroupProposal.proposedSnapshot proposal)
        | PendingProposal.FieldlessUnionVariantEvolution proposal -> Some(AgentFieldlessUnionVariantEvolutionProposal.proposedSnapshot proposal)
        | PendingProposal.CreateEffect proposal -> Some(AgentEffectDeclarationProposal.proposedSnapshot proposal)
        | PendingProposal.CreateCapability proposal -> Some(AgentCapabilityDeclarationProposal.proposedSnapshot proposal)

module private ProposalReview =
    /// Functions and types a proposal rewrites, from its resulting change as well as its
    /// intent: signature/variant edits can rewrite other callers or match owners.
    let touched (review: AuthoringProposalReview): FunctionId list * TypeId list =
        match review with
        | AuthoringProposalReview.Operations item ->
            item.ResultingChange.AffectedFunctionIds @ (item.ResultingChange.Diff.FunctionsRemoved |> List.map (fun removed -> removed.FunctionId)),
            item.ResultingChange.AffectedTypeIds @ (item.ResultingChange.Diff.TypesRemoved |> List.map (fun removed -> removed.TypeId))
        | AuthoringProposalReview.ReplaceBody item -> [ item.RequestedChange.TargetFunctionId ], []
        | AuthoringProposalReview.ReplaceTarget item -> [ item.RequestedChange.TargetFunctionId ], []
        | AuthoringProposalReview.ParameterTypeMigration item ->
            item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), []
        | AuthoringProposalReview.ResultTypeMigration item ->
            item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), []
        | AuthoringProposalReview.ProductFieldTypeMigration item ->
            item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), [ item.ResultingChange.FieldChange.TypeId ]
        | AuthoringProposalReview.FieldlessUnionVariantEvolution item ->
            item.RepairedFunctionIds, [ item.UpdatedType.Id ]
        | AuthoringProposalReview.CreateFunction _
        | AuthoringProposalReview.CreateType _
        | AuthoringProposalReview.CreateTypeGroup _
        | AuthoringProposalReview.CreateEffect _
        | AuthoringProposalReview.CreateCapability _ -> [], []

/// Test seam for storage failures that cannot be produced reliably through the file system.
module internal SessionStorageFaults =
    /// When it returns true for a full path, the next replacement of that path fails.
    let mutable failReplacement: string -> bool = fun _ -> false

module private SessionStorage =
    let fullPath (path: string): string = Path.GetFullPath(path)
    let lockPath (path: string): string = path + ".lock"

    let acquireLock (path: string): Result<FileStream, AuthoringSessionError> =
        try
            Ok(new FileStream(lockPath path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        with
        | :? IOException -> Error(AuthoringSessionError.LockUnavailable path)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    /// Releases the lock and removes its file, so a clean close or a failed open leaves nothing
    /// behind. The file is unlinked before the handle closes where the OS allows it (POSIX), so a
    /// concurrent opener either fails against the still-held lock or creates a fresh file; where it
    /// does not (Windows), the delete is retried after the close and fails harmlessly if another
    /// session has already taken the lock. Session commits verify the persisted source digest, so
    /// the remaining POSIX window (an opener between its open and lock calls) cannot corrupt a snapshot.
    let releaseLock (path: string) (lockHandle: FileStream) =
        let tryDelete () =
            try File.Delete(lockPath path); true
            with
            | :? IOException
            | :? UnauthorizedAccessException -> false
        let deleted = tryDelete ()
        lockHandle.Dispose()
        if not deleted then tryDelete () |> ignore

    let readText (path: string): Result<string, AuthoringSessionError> =
        try Ok(File.ReadAllText(path, Encoding.UTF8))
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    // SnapshotCodec establishes validity; pinning additionally requires completeness.
    let ensureCompleteDependency (imported: Snapshot): Result<Snapshot, AuthoringSessionError> =
        if imported.Obligations |> List.exists (fun obligation -> obligation.Status = ObligationStatus.Open) then
            Error(AuthoringSessionError.DependencyFailure "The selected dependency has open obligations; complete it before pinning.")
        else Ok imported

    let decodeCompleteDependency (source: string): Result<Snapshot, AuthoringSessionError> =
        SnapshotCodec.deserialize source
        |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
        |> Result.bind ensureCompleteDependency

    let readCompleteDependency path = readText path |> Result.bind decodeCompleteDependency

    let private writeTemporary (path: string) (content: string): Result<string, AuthoringSessionError> =
        let directory = Path.GetDirectoryName(path)
        let tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp")
        try
            File.WriteAllText(tempPath, content, UTF8Encoding(false))
            Ok tempPath
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    // Also used for a manifest whose associated snapshot could not be created.
    // Cleanup must not mask the original failure or prevent lock disposal.
    let cleanupTemporary (tempPath: string) =
        try if File.Exists tempPath then File.Delete tempPath
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

    let private createAtomicallyCore (path: string) (content: string): Result<unit, AuthoringSessionError> =
        match writeTemporary path content with
        | Error error -> Error error
        | Ok tempPath ->
            try
                File.Move(tempPath, path, false)
                Ok()
            with
            | :? IOException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)
            | :? UnauthorizedAccessException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)

    let private replaceAtomicallyCore (path: string) (content: string): Result<unit, AuthoringSessionError> =
        if SessionStorageFaults.failReplacement path then Error(AuthoringSessionError.IoFailure $"Injected replacement failure for '{path}'.") else
        match writeTemporary path content with
        | Error error -> Error error
        | Ok tempPath ->
            try
                File.Move(tempPath, path, true)
                Ok()
            with
            | :? IOException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)
            | :? UnauthorizedAccessException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)

    let createAtomically path content =
        SnapshotComputationScope.Measure("persistence.create", fun () -> createAtomicallyCore path content)

    let replaceAtomically path content =
        SnapshotComputationScope.Measure("persistence.replace", fun () -> replaceAtomicallyCore path content)

    let ensureParentDirectory (path: string): Result<unit, AuthoringSessionError> =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            Ok()
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

/// One imported declaration that an upgrade adds, removes or changes.
type AuthoringDependencyChange =
    { Kind: string
      Id: string }

/// The reviewed impact of replacing one pinned dependency with a new version.
type AuthoringDependencyUpgradePreview =
    { ReplacedSnapshotId: string
      ReplacedFingerprint: string
      NewSnapshotId: string
      /// Confirms the exact reviewed version when the upgrade is applied.
      NewFingerprint: string
      Added: AuthoringDependencyChange list
      Removed: AuthoringDependencyChange list
      Changed: AuthoringDependencyChange list
      /// Validation issues of the local graph against the upgraded imports; empty when compatible.
      Issues: ValidationIssue list }

type internal PinnedDependencySelection =
    { Imported: Snapshot
      Manifest: string
      SourcePath: string
      Fingerprint: string
      IsEmbedded: bool
      /// The individually pinned dependencies of a combined selection; empty for one dependency,
      /// whose `Imported` graph is then the dependency itself.
      Members: PinnedDependencySelection list }

module private PinnedDependency =
    let embed sourcePath imported =
        match SnapshotCodec.serialize imported, SnapshotFingerprint.compute imported with
        | Ok source, Ok fingerprint ->
            let fingerprint = SnapshotFingerprint.value fingerprint
            let manifest = JsonSerializer.Serialize(
                {| path = sourcePath
                   snapshotId = StableId.value imported.Id
                   fingerprint = fingerprint
                   snapshotJson = source |})
            Ok { Imported = imported; Manifest = manifest; SourcePath = sourcePath
                 Fingerprint = fingerprint; IsEmbedded = true; Members = [] }
        | Error error, _
        | _, Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)

    let private describeMember selected =
        {| path = selected.SourcePath
           snapshotId = StableId.value selected.Imported.Id
           fingerprint = selected.Fingerprint
           storage = if selected.IsEmbedded then "embedded" else "legacySource" |}

    let describe selected =
        // Discovery must not repeat the potentially large stored snapshot.
        match selected.Members with
        | [] -> JsonSerializer.Serialize(describeMember selected)
        | members -> JsonSerializer.Serialize({| dependencies = members |> List.map describeMember |})

    let private combinationFailure = function
        | DependencyCombinationError.NoDependencies -> AuthoringSessionError.DependencyFailure "A dependency set needs at least one dependency."
        | DependencyCombinationError.ConflictingDeclaration(kind, id) ->
            AuthoringSessionError.DependencyFailure $"Dependencies declare the {kind} '{id}' with different content; colliding imported identities are never merged."
        | DependencyCombinationError.FingerprintFailure error -> AuthoringSessionError.SnapshotCodecFailure error

    /// Combines individually pinned dependencies under the import collision policy.
    let combine (members: PinnedDependencySelection list) =
        match members with
        | [ single ] -> Ok single
        | _ ->
            DependencyGraph.combine (members |> List.map (fun item -> item.Imported))
            |> Result.mapError combinationFailure
            |> Result.bind (fun combined ->
                match SnapshotFingerprint.compute combined with
                | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
                | Ok fingerprint ->
                    let manifest =
                        members
                        |> List.map (fun item -> JsonSerializer.Deserialize<JsonElement>(item.Manifest))
                        |> fun items -> JsonSerializer.Serialize({| dependencies = items |})
                    Ok { Imported = combined; Manifest = manifest; SourcePath = ""
                         Fingerprint = SnapshotFingerprint.value fingerprint; IsEmbedded = true; Members = members })

    let private loadMember (manifest: string) (root: JsonElement) sourceOverride: Result<PinnedDependencySelection, AuthoringSessionError> =
        let fields = root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toList
        let embedded = fields = [ "fingerprint"; "path"; "snapshotId"; "snapshotJson" ]
        if not embedded && fields <> [ "fingerprint"; "path"; "snapshotId" ] then
            Error(AuthoringSessionError.DependencyFailure "Invalid dependency manifest.")
        else
            let sourcePath = root.GetProperty("path").GetString()
            let fingerprint = root.GetProperty("fingerprint").GetString()
            let snapshotId = root.GetProperty("snapshotId").GetString()
            if String.IsNullOrWhiteSpace sourcePath || String.IsNullOrWhiteSpace fingerprint
               || String.IsNullOrWhiteSpace snapshotId then
                Error(AuthoringSessionError.DependencyFailure "Dependency manifest fields must be non-blank strings.")
            else
                let selectedPath = sourceOverride |> Option.map SessionStorage.fullPath |> Option.defaultValue sourcePath
                let imported =
                    match sourceOverride with
                    | None when embedded ->
                        let source = root.GetProperty("snapshotJson").GetString()
                        if String.IsNullOrWhiteSpace source then
                            Error(AuthoringSessionError.DependencyFailure "The embedded dependency is missing.")
                        else SessionStorage.decodeCompleteDependency source
                    | _ when not (Path.IsPathFullyQualified selectedPath) || not (File.Exists selectedPath) ->
                        Error(AuthoringSessionError.DependencyFailure "The selected dependency is unavailable. Use pin with an exact copy of the original version to recover a legacy selection.")
                    | _ -> SessionStorage.readCompleteDependency selectedPath
                imported |> Result.bind (fun imported ->
                    match SnapshotFingerprint.compute imported with
                    | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
                    | Ok actual when SnapshotFingerprint.value actual = fingerprint
                                     && StableId.value imported.Id = snapshotId ->
                        Ok { Imported = imported; Manifest = manifest; SourcePath = selectedPath
                             Fingerprint = fingerprint; IsEmbedded = embedded; Members = [] }
                    | Ok _ -> Error(AuthoringSessionError.DependencyFailure "The selected dependency version changed. Pin only accepts the exact previously selected content, not an upgrade."))

    let load path sourceOverride: Result<PinnedDependencySelection option, AuthoringSessionError> =
        // Read directly: File.Exists treats permission failures as absence.
        try
            let manifest = File.ReadAllText(path + ".dependency.json", Encoding.UTF8)
            use document = JsonDocument.Parse(manifest)
            let root = document.RootElement
            match root.TryGetProperty("dependencies") with
            | true, members when root.EnumerateObject() |> Seq.length = 1 ->
                // A dependency set stores every member's exact content; there is no legacy form.
                if Option.isSome sourceOverride then
                    Error(AuthoringSessionError.DependencyFailure "Pin with a source applies only to a single legacy dependency.")
                elif members.ValueKind <> JsonValueKind.Array || members.GetArrayLength() < 2 then
                    Error(AuthoringSessionError.DependencyFailure "A dependency set lists at least two dependencies.")
                else
                    members.EnumerateArray()
                    |> Seq.toList
                    |> List.fold (fun state item ->
                        state |> Result.bind (fun loaded ->
                            if item.ValueKind <> JsonValueKind.Object || not (fst (item.TryGetProperty "snapshotJson")) then
                                Error(AuthoringSessionError.DependencyFailure "Every member of a dependency set must embed its content.")
                            else loadMember (item.GetRawText()) item None |> Result.map (fun selected -> loaded @ [ selected ]))) (Ok [])
                    |> Result.bind combine
                    |> Result.map (fun combined -> Some { combined with Manifest = manifest })
            | _ -> loadMember manifest root sourceOverride |> Result.map Some
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Ok None
        | :? JsonException
        | :? InvalidOperationException
        | :? KeyNotFoundException
        | :? ArgumentException
        | :? NotSupportedException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency manifest or source path.")
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.DependencyFailure error.Message)
        | :? IOException as error -> Error(AuthoringSessionError.DependencyFailure error.Message)

    let persistPin path source selected =
        embed selected.SourcePath selected.Imported
        |> Result.bind (fun pinned ->
            // Only the manifest is replaced. The graph and its digest marker are
            // unchanged; a failed replacement leaves the legacy selection intact.
            match SessionStorage.readText path, SessionStorage.readText (path + ".dependency.json") with
            | Ok current, Ok manifest when current = source && manifest = selected.Manifest ->
                SessionStorage.replaceAtomically (path + ".dependency.json") pinned.Manifest
                |> Result.map (fun () -> pinned)
            | Error error, _
            | _, Error error -> Error error
            | _ -> Error AuthoringSessionError.ExternalModification)

    /// Pins one source for the workspace at `localPath`. A source that has its own pinned
    /// dependencies is flattened into its complete graph; its sidecar already retains exact,
    /// flattened content, so no further recursion is needed.
    let pinSource (localPath: string) (sourcePath: string) =
        try
            let sourcePath = SessionStorage.fullPath sourcePath
            if sourcePath = localPath || not (File.Exists sourcePath) then
                Error(AuthoringSessionError.DependencyFailure "The selected dependency is unavailable.")
            elif File.Exists(sourcePath + ".dependency.json") then
                load sourcePath None
                |> Result.bind (function
                    | None -> Error(AuthoringSessionError.DependencyFailure "The dependency's own dependency selection is unavailable.")
                    | Some nested ->
                        SessionStorage.readText sourcePath
                        |> Result.bind (fun text ->
                            SnapshotCodec.deserializeResolved nested.Imported text
                            |> Result.mapError AuthoringSessionError.SnapshotCodecFailure)
                        |> Result.map (SnapshotCodec.resolveDependency nested.Imported)
                        |> Result.bind SessionStorage.ensureCompleteDependency)
                |> Result.bind (embed sourcePath)
            else
                SessionStorage.readCompleteDependency sourcePath
                |> Result.bind (embed sourcePath)
        with
        | :? ArgumentException
        | :? NotSupportedException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency path.")

    type UpgradePlan =
        { Selection: PinnedDependencySelection
          Local: Snapshot
          Preview: AuthoringDependencyUpgradePreview }

    let private declarationChanges (before: Snapshot) (after: Snapshot) =
        let entries (snapshot: Snapshot) =
            (snapshot.Types |> List.map (fun item -> ("type", StableId.value item.Id), box item))
            @ (snapshot.Effects |> List.map (fun item -> ("effect", StableId.value item.Id), box item))
            @ (snapshot.Capabilities |> List.map (fun item -> ("capability", StableId.value item.Id), box item))
            @ (snapshot.Functions |> List.map (fun item -> ("function", StableId.value item.Id), box item))
            |> Map.ofList
        let oldEntries, newEntries = entries before, entries after
        let change ((kind, id), _) = { Kind = kind; Id = id }
        let added = newEntries |> Map.toList |> List.filter (fun (key, _) -> not (Map.containsKey key oldEntries)) |> List.map change
        let removed = oldEntries |> Map.toList |> List.filter (fun (key, _) -> not (Map.containsKey key newEntries)) |> List.map change
        let changed =
            newEntries |> Map.toList
            |> List.filter (fun (key, item) -> Map.tryFind key oldEntries |> Option.exists (fun old -> old <> item))
            |> List.map change
        added, removed, changed

    /// Plans replacing one pinned dependency (the only one, or the member whose SnapshotId is
    /// `replaces`) with the exact content of `sourcePath`, and validates the local graph against it.
    let planUpgrade (localPath: string) (localSource: string) (current: PinnedDependencySelection) (sourcePath: string) (replaces: string option) =
        let members = match current.Members with [] -> [ current ] | members -> members
        let target =
            match replaces, members with
            | None, [ single ] -> Ok single
            | None, _ -> Error(AuthoringSessionError.DependencyFailure "Name the dependency to replace by its SnapshotId when the workspace has several.")
            | Some snapshotId, _ ->
                members |> List.tryFind (fun item -> StableId.value item.Imported.Id = snapshotId)
                |> Option.map Ok
                |> Option.defaultValue (Error(AuthoringSessionError.DependencyFailure $"No selected dependency has SnapshotId '{snapshotId}'."))
        target
        |> Result.bind (fun target ->
            pinSource localPath sourcePath
            |> Result.bind (fun replacement ->
                if replacement.Fingerprint = target.Fingerprint then
                    Error(AuthoringSessionError.DependencyFailure "The source is the version already selected; use pin to retain or relocate it.")
                else
                    let upgraded = members |> List.map (fun item -> if item = target then replacement else item)
                    combine upgraded
                    |> Result.bind (fun selection ->
                        SnapshotCodec.deserializeResolved current.Imported localSource
                        |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
                        |> Result.map (fun local ->
                            let added, removed, changed = declarationChanges target.Imported replacement.Imported
                            { Selection = selection
                              Local = local
                              Preview =
                                { ReplacedSnapshotId = StableId.value target.Imported.Id
                                  ReplacedFingerprint = target.Fingerprint
                                  NewSnapshotId = StableId.value replacement.Imported.Id
                                  NewFingerprint = replacement.Fingerprint
                                  Added = added
                                  Removed = removed
                                  Changed = changed
                                  Issues = SnapshotValidator.validate (SnapshotCodec.resolveDependency selection.Imported local) } }))))

type AuthoringSession internal (path: string, initialWorkspace: AgentWorkspace, initialPersistedSource: string, lockHandle: FileStream, selection: PinnedDependencySelection option) =
    let dependency = selection |> Option.map (fun item -> item.Imported)
    let dependencyManifest = selection |> Option.map (fun item -> item.Manifest)
    let proposals = Dictionary<string, TransactionId * PendingProposal>(StringComparer.Ordinal)
    // The registered review and the snapshot it was simulated against, for review pages.
    let registered = Dictionary<string, AuthoringProposal * SnapshotId>(StringComparer.Ordinal)
    let mutable workspace = initialWorkspace
    let mutable persistedSource = initialPersistedSource
    let mutable invalidated = false
    let mutable closed = false

    let ensureUsable () =
        if closed then Error AuthoringSessionError.SessionClosed
        elif invalidated then Error AuthoringSessionError.SessionInvalidated
        else Ok()

    let importedFunctionIds =
        dependency |> Option.map (fun snapshot -> snapshot.Functions |> List.map (fun item -> item.Id) |> Set.ofList)
        |> Option.defaultValue Set.empty
    let importedTypeIds =
        dependency |> Option.map (fun snapshot -> snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList)
        |> Option.defaultValue Set.empty

    let registerWith (proposedFunctions: SemanticFunction list) transactionId pending review =
        let functions, types = ProposalReview.touched review
        let removesImportedDeclaration =
            match review, dependency with
            | AuthoringProposalReview.Operations item, Some imported ->
                (item.ResultingChange.Diff.EffectsRemoved |> List.exists (fun removed ->
                    imported.Effects |> List.exists (fun effect -> effect.Id = removed.EffectId)))
                || (item.ResultingChange.Diff.CapabilitiesRemoved |> List.exists (fun removed ->
                    imported.Capabilities |> List.exists (fun capability -> capability.Id = removed.CapabilityId)))
                || (item.ResultingChange.Diff.CapabilityOperationsRemoved |> List.exists (fun removed ->
                    imported.Capabilities |> List.exists (fun capability -> capability.Id = removed.OwnerCapabilityId)))
            | _ -> false
        if functions |> List.exists (fun id -> Set.contains id importedFunctionIds)
           || types |> List.exists (fun id -> Set.contains id importedTypeIds)
           || removesImportedDeclaration then
            Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot
                [ { Code = "ImportedDeclarationModified"; Message = "Dependency declarations are read-only; no proposal was registered." } ]))
        else
            let proposalId = StableId.value transactionId
            proposals.Add(proposalId, (transactionId, pending))
            let rewritten =
                functions |> List.distinct |> List.choose (fun id -> proposedFunctions |> List.tryFind (fun item -> item.Id = id))
            let proposal = { ProposalId = proposalId; Review = review; RewrittenFunctions = rewritten }
            registered.Add(proposalId, (proposal, AgentWorkspace.currentSnapshotId workspace))
            Ok proposal

    let register transactionId pending review = registerWith [] transactionId pending review

    let dependencyStillPinned () =
        match selection with
        | Some selected when not selected.IsEmbedded ->
            match SessionStorage.readCompleteDependency selected.SourcePath with
            | Error _ -> false
            | Ok current ->
                match SnapshotFingerprint.compute current with
                | Ok actual -> SnapshotFingerprint.value actual = selected.Fingerprint && current.Id = selected.Imported.Id
                | _ -> false
        // Embedded content was validated on open; the exact manifest is checked
        // again at publication. The original source is provenance, not authority.
        | _ -> true

    let checkPersistedSource () =
        let selectionChanged =
            dependencyManifest |> Option.exists (fun expected ->
                match SessionStorage.readText (path + ".dependency.json") with
                | Ok actual -> actual <> expected
                | Error _ -> true)
        if selectionChanged || not (dependencyStillPinned ()) then
            invalidated <- true
            Error(AuthoringSessionError.DependencyFailure "The dependency selection changed outside this session.")
        elif not (File.Exists path) then
            invalidated <- true
            Error AuthoringSessionError.ExternalModification
        else
            match SessionStorage.readText path with
            | Error error -> Error error
            | Ok currentSource when currentSource <> persistedSource ->
                invalidated <- true
                Error AuthoringSessionError.ExternalModification
            | Ok _ -> Ok()

    let persistCandidate candidate =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            let serialized =
                match dependency with
                | None -> AgentWorkspace.serializeCurrentSnapshot candidate
                | Some imported -> AgentWorkspace.serializeWithDependency imported candidate
            match serialized with
            | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
            | Ok candidateSource ->
                match checkPersistedSource () with
                | Error error -> Error error
                | Ok() ->
                    match SessionStorage.replaceAtomically path candidateSource with
                    | Error error -> Error error
                    | Ok() ->
                        workspace <- candidate
                        persistedSource <- candidateSource
                        Ok()

    let commitCandidate proposalId transactionId publishedSnapshotId readiness candidate =
        match persistCandidate candidate with
        | Error error -> Error error
        | Ok() ->
            proposals.Remove proposalId |> ignore
            registered.Remove proposalId |> ignore
            Ok { TransactionId = transactionId; PublishedSnapshotId = publishedSnapshotId; Readiness = readiness }

    member _.Path = path
    /// Whether `runPure`/`runPureByName` may compile and run host C# sources (`hostSources` and
    /// `hostCapability` bindings). Off by default; the session process enables it only for the
    /// `--allow-host-sources` startup flag, never per request, because host code runs with the
    /// user's permissions.
    member val HostSourcesAllowed = false with get, set
    member _.DependencySelection = selection |> Option.map PinnedDependency.describe

    /// Reviews replacing a pinned dependency with a new version without changing anything.
    member _.PreviewDependencyUpgrade(sourcePath: string, replaces: string option): Result<AuthoringDependencyUpgradePreview, AuthoringSessionError> =
        match selection with
        | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade.")
        | Some current ->
            PinnedDependency.planUpgrade path persistedSource current sourcePath replaces
            |> Result.map (fun plan -> plan.Preview)

    /// Read-only execution must not run a stale in-memory graph after the selected
    /// snapshot or its pinned dependency was changed outside the session.
    member _.VerifyPersistedSource(): Result<unit, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())

    /// The current snapshot's id, without computing the fingerprints that `Status` reports.
    member _.SnapshotId: SnapshotId = AgentWorkspace.currentSnapshotId workspace

    member _.Status(): Result<AuthoringSessionStatus, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match AgentWorkspace.currentSnapshotFingerprint workspace with
        | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
        | Ok snapshotFingerprint ->
            match AgentWorkspace.currentSemanticFingerprint workspace with
            | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
            | Ok semanticFingerprint ->
                Ok
                    { SnapshotId = AgentWorkspace.currentSnapshotId workspace
                      SnapshotFingerprint = snapshotFingerprint
                      SemanticFingerprint = semanticFingerprint }

    member _.Declarations(): AuthoringDeclarations =
        { Functions = AgentWorkspace.listFunctions workspace
          Types = AgentWorkspace.listTypes workspace
          Effects = AgentWorkspace.listEffects workspace
          Capabilities = AgentWorkspace.listCapabilities workspace }

    member _.FocusFunction(functionId) = AgentWorkspace.focusFunction workspace functionId
    member _.GetCallers(functionId) = AgentWorkspace.getCallers workspace functionId
    member _.GetCallees(functionId) = AgentWorkspace.getCallees workspace functionId
    member _.GetOpenObligations(functionId) = AgentWorkspace.getOpenObligations workspace functionId
    member _.GetDependencies(functionId, nodeId) = AgentWorkspace.getDependencies workspace functionId nodeId
    member _.GetUses(functionId, nodeId) = AgentWorkspace.getUses workspace functionId nodeId

    /// `expectedBaseSnapshotId` defaults to the current snapshot; a different one is rejected as stale.
    member _.SimulateOperations(operations: AgentOperationIntent list, ?expectedBaseSnapshotId: SnapshotId): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            let expectedBase = defaultArg expectedBaseSnapshotId (AgentWorkspace.currentSnapshotId workspace)
            match AgentWorkflow.simulateFromBase workspace expectedBase operations with
            | Error error -> Error(AuthoringSessionError.WorkflowSimulationFailure error)
            | Ok proposal ->
                register (AgentChangeProposal.transactionId proposal) (PendingProposal.Operations proposal) (AuthoringProposalReview.Operations(AgentChangeProposal.review proposal))

    member _.SimulateReplaceBody(request: AgentReplaceFunctionBodyWithExpressionRecipe): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralChangeProposal.transactionId proposal) (PendingProposal.ReplaceBody proposal) (AuthoringProposalReview.ReplaceBody(AgentExpressionStructuralChangeProposal.review proposal))

    member _.SimulateReplaceTarget(request: AgentExpressionStructuralWorkflow.Targeted.Request): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.Targeted.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralWorkflow.Targeted.Proposal.transactionId proposal) (PendingProposal.ReplaceTarget proposal) (AuthoringProposalReview.ReplaceTarget(AgentExpressionStructuralWorkflow.Targeted.Proposal.review proposal))

    member _.SimulateCreateFunction(request: AgentExpressionStructuralWorkflow.FunctionCreation.Request): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.transactionId proposal) (PendingProposal.CreateFunction proposal) (AuthoringProposalReview.CreateFunction(AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review proposal))

    member _.SimulateParameterTypeMigration(request: AgentParameterTypeMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentParameterTypeMigrationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith (AgentParameterTypeMigrationProposal.proposedFunctions proposal) (AgentParameterTypeMigrationProposal.transactionId proposal) (PendingProposal.ParameterTypeMigration proposal) (AuthoringProposalReview.ParameterTypeMigration(AgentParameterTypeMigrationProposal.review proposal))

    member _.SimulateResultTypeMigration(request: AgentResultTypeMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentResultTypeMigrationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith (AgentResultTypeMigrationProposal.proposedFunctions proposal) (AgentResultTypeMigrationProposal.transactionId proposal) (PendingProposal.ResultTypeMigration proposal) (AuthoringProposalReview.ResultTypeMigration(AgentResultTypeMigrationProposal.review proposal))

    member _.SimulateProductFieldTypeMigration(request: AgentProductFieldTypeMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentProductFieldTypeMigrationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith (AgentProductFieldTypeMigrationProposal.proposedFunctions proposal) (AgentProductFieldTypeMigrationProposal.transactionId proposal) (PendingProposal.ProductFieldTypeMigration proposal) (AuthoringProposalReview.ProductFieldTypeMigration(AgentProductFieldTypeMigrationProposal.review proposal))

    member _.SimulateCreateType(request: AgentCreateTypeDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentTypeDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.TypeDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentTypeDeclarationProposal.transactionId proposal) (PendingProposal.CreateType proposal) (AuthoringProposalReview.CreateType(AgentTypeDeclarationProposal.review proposal))

    member _.SimulateCreateTypeGroup(request: AgentCreateTypeDeclarationGroupRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentTypeDeclarationWorkflow.simulateGroup workspace request with
            | Error error -> Error(AuthoringSessionError.TypeDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentTypeDeclarationGroupProposal.transactionId proposal) (PendingProposal.CreateTypeGroup proposal) (AuthoringProposalReview.CreateTypeGroup(AgentTypeDeclarationGroupProposal.review proposal))

    member _.SimulateAddFieldlessUnionVariantLike(request: AgentAddFieldlessUnionVariantLikeRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentFieldlessUnionVariantEvolutionWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith
                    (AgentFieldlessUnionVariantEvolutionProposal.proposedFunctions proposal)
                    (AgentFieldlessUnionVariantEvolutionProposal.transactionId proposal)
                    (PendingProposal.FieldlessUnionVariantEvolution proposal)
                    (AuthoringProposalReview.FieldlessUnionVariantEvolution(
                        AgentFieldlessUnionVariantEvolutionProposal.review proposal
                    ))

    member _.SimulateAddUnionVariantLike(request: AgentAddFieldlessUnionVariantLikeRequest, additionalFields: AgentTypeFieldIntent list): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentFieldlessUnionVariantEvolutionWorkflow.simulateWithFields workspace request additionalFields with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith
                    (AgentFieldlessUnionVariantEvolutionProposal.proposedFunctions proposal)
                    (AgentFieldlessUnionVariantEvolutionProposal.transactionId proposal)
                    (PendingProposal.FieldlessUnionVariantEvolution proposal)
                    (AuthoringProposalReview.FieldlessUnionVariantEvolution(
                        AgentFieldlessUnionVariantEvolutionProposal.review proposal
                    ))

    member _.SimulateCreateEffect(request: AgentCreateEffectDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentEffectDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.EffectDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentEffectDeclarationProposal.transactionId proposal) (PendingProposal.CreateEffect proposal) (AuthoringProposalReview.CreateEffect(AgentEffectDeclarationProposal.review proposal))

    member _.SimulateCreateCapability(request: AgentCreateCapabilityDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentCapabilityDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.CapabilityDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentCapabilityDeclarationProposal.transactionId proposal) (PendingProposal.CreateCapability proposal) (AuthoringProposalReview.CreateCapability(AgentCapabilityDeclarationProposal.review proposal))

    member _.Commit(proposalId: string, policy: CommitPolicy): Result<AuthoringCommitReceipt, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match proposals.TryGetValue proposalId with
            | false, _ -> Error(AuthoringSessionError.ProposalNotFound proposalId)
            | true, (_, PendingProposal.Operations proposal) ->
                match AgentWorkflow.commitWithPolicy workspace proposal policy with
                | AgentCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ReplaceBody proposal) ->
                match AgentExpressionStructuralWorkflow.commitWithPolicy workspace proposal policy with
                | AgentStructuralCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentStructuralCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ReplaceTarget proposal) ->
                match AgentExpressionStructuralWorkflow.Targeted.commitWithPolicy workspace proposal policy with
                | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateFunction proposal) ->
                match AgentExpressionStructuralWorkflow.FunctionCreation.commitWithPolicy workspace proposal policy with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ParameterTypeMigration proposal) ->
                match AgentParameterTypeMigrationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentParameterTypeMigrationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentParameterTypeMigrationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ResultTypeMigration proposal) ->
                match AgentResultTypeMigrationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentResultTypeMigrationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentResultTypeMigrationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ProductFieldTypeMigration proposal) ->
                match AgentProductFieldTypeMigrationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentProductFieldTypeMigrationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentProductFieldTypeMigrationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateType proposal) ->
                match AgentTypeDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentTypeDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentTypeDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateTypeGroup proposal) ->
                match AgentTypeDeclarationWorkflow.commitGroupWithPolicy workspace proposal policy with
                | AgentTypeDeclarationGroupCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentTypeDeclarationGroupCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.FieldlessUnionVariantEvolution proposal) ->
                match AgentFieldlessUnionVariantEvolutionWorkflow.commitWithPolicy workspace proposal policy with
                | AgentFieldlessUnionVariantEvolutionCommitResult.Rejected diagnostics ->
                    Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentFieldlessUnionVariantEvolutionCommitResult.Committed(candidate, publication) ->
                    commitCandidate
                        proposalId
                        publication.TransactionId
                        publication.PublishedSnapshotId
                        publication.Readiness
                        candidate
            | true, (_, PendingProposal.CreateEffect proposal) ->
                match AgentEffectDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentEffectDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentEffectDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateCapability proposal) ->
                match AgentCapabilityDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentCapabilityDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentCapabilityDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate

    /// Read-only: the review of a pending proposal and the snapshots a human review page renders.
    member _.PendingProposalView(proposalId: string): Result<AuthoringPendingProposalView, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match proposals.TryGetValue proposalId, registered.TryGetValue proposalId with
            | (true, (transactionId, pending)), (true, (proposal, baseSnapshotId)) ->
                let touched, touchedTypes = ProposalReview.touched proposal.Review
                let signatureOwners =
                    match proposal.Review with
                    | AuthoringProposalReview.CreateFunction item -> [ item.CreatedFunction.Id ]
                    | AuthoringProposalReview.ParameterTypeMigration item -> [ item.ResultingChange.ParameterChange.FunctionId ]
                    | AuthoringProposalReview.ResultTypeMigration item -> [ item.ResultingChange.ResultChange.FunctionId ]
                    | _ -> []
                Ok { TransactionId = transactionId
                     Proposal = proposal
                     BaseSnapshotId = baseSnapshotId
                     BaseIsCommitted = (AgentWorkspace.currentSnapshotId workspace = baseSnapshotId)
                     ProposedSnapshot = PendingProposal.proposedSnapshot pending
                     FunctionIds = signatureOwners @ touched |> List.distinct
                     TypeIds =
                        match proposal.Review with
                        | AuthoringProposalReview.CreateType item -> [ item.CreatedType.Id ]
                        | AuthoringProposalReview.CreateTypeGroup item -> item.CreatedTypes |> List.map (fun typeEntity -> typeEntity.Id)
                        | _ -> touchedTypes
                        |> List.distinct }
            | _ -> Error(AuthoringSessionError.ProposalNotFound proposalId)

    member _.Reject(proposalId: string): Result<AuthoringRejectionReceipt, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match proposals.TryGetValue proposalId with
            | false, _ -> Error(AuthoringSessionError.ProposalNotFound proposalId)
            | true, (transactionId, _) ->
                // Rejection only forgets a handle; all workflows retain the current
                // workspace and return the same identity-only receipt.
                proposals.Remove proposalId |> ignore
                registered.Remove proposalId |> ignore
                Ok { TransactionId = transactionId; RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }

    member _.ProjectExecution(): Result<ExecutionProgram, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match AgentExecution.projectTypedSemanticIr workspace with
        | Error error -> Error(AuthoringSessionError.SemanticProjectionFailure error)
        | Ok typed -> ExecutionIr.lower typed |> Result.mapError AuthoringSessionError.ExecutionLoweringFailure

    member this.EmitCSharp(profile: CSharpBackendProfile): Result<CSharpArtifact, AuthoringSessionError> =
        this.ProjectExecution()
        |> Result.bind (fun execution -> CSharpBackend.emit execution profile |> Result.mapError AuthoringSessionError.BackendFailure)

    member _.Close() =
        if not closed then
            closed <- true
            SessionStorage.releaseLock path lockHandle

    interface IDisposable with
        member this.Dispose() = this.Close()

[<RequireQualifiedAccess>]
module AuthoringSession =
    let private workspaceFromSource (dependency: Snapshot option) (source: string): Result<AgentWorkspace, AuthoringSessionError> =
        let decoded =
            match dependency with
            | None -> SnapshotCodec.deserialize source
            | Some imported -> SnapshotCodec.deserializeResolved imported source
        match decoded with
        | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
        | Ok snapshot ->
            (match dependency with
             | None -> AgentWorkspace.create snapshot
             | Some imported -> AgentWorkspace.createResolved imported snapshot)
            |> Result.mapError AuthoringSessionError.WorkspaceValidationFailure

    let private openUsing (path: string) pin sourceOverride: Result<AuthoringSession, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        let path = SessionStorage.fullPath path
        if not (File.Exists path) then Error(AuthoringSessionError.SnapshotFileNotFound path)
        else
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                match SessionStorage.readText path with
                | Error error ->
                    SessionStorage.releaseLock path lockHandle
                    Error error
                | Ok source ->
                    let loaded = PinnedDependency.load path sourceOverride
                    match loaded |> Result.bind (fun selected ->
                        workspaceFromSource (selected |> Option.map (fun item -> item.Imported)) source
                        |> Result.bind (fun workspace ->
                            // Verify the local digest and complete combined graph
                            // before changing even storage-only dependency metadata.
                            let prepared =
                                match pin, selected with
                                | false, _ -> Ok selected
                                | true, None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency selection to pin.")
                                | true, Some selected when selected.IsEmbedded && Option.isNone sourceOverride -> Ok(Some selected)
                                | true, Some selected -> PinnedDependency.persistPin path source selected |> Result.map Some
                            prepared |> Result.map (fun selected -> selected, workspace))) with
                    | Error error ->
                        SessionStorage.releaseLock path lockHandle
                        Error error
                    | Ok(selected, workspace) ->
                        Ok(new AuthoringSession(path, workspace, source, lockHandle, selected))

    let openExisting path = openUsing path false None

    /// Explicitly replaces one pinned dependency with the reviewed version `expectedFingerprint`,
    /// only when the local graph is valid against it, then opens the upgraded workspace.
    /// The sidecar and the local snapshot's dependency binding are rewritten; local declarations
    /// and identities are unchanged. The sidecar is replaced first; if the snapshot replacement then
    /// fails, the previous sidecar content is restored so the unchanged workspace still opens.
    let upgradeDependency (path: string) (sourcePath: string) (replaces: string option) (expectedFingerprint: string): Result<AuthoringSession, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        let path = SessionStorage.fullPath path
        if not (File.Exists path) then Error(AuthoringSessionError.SnapshotFileNotFound path)
        else
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                let result =
                    SessionStorage.readText path
                    |> Result.bind (fun source ->
                        PinnedDependency.load path None
                        |> Result.bind (function
                            | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade.")
                            | Some current -> PinnedDependency.planUpgrade path source current sourcePath replaces)
                        |> Result.bind (fun plan ->
                            if plan.Preview.NewFingerprint <> expectedFingerprint then
                                Error(AuthoringSessionError.DependencyFailure "The source differs from the reviewed upgrade version.")
                            elif not (List.isEmpty plan.Preview.Issues) then
                                Error(AuthoringSessionError.WorkspaceValidationFailure plan.Preview.Issues)
                            else
                                SnapshotCodec.serializeResolved plan.Selection.Imported plan.Local
                                |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
                                |> Result.bind (fun upgradedSource ->
                                    // Validate the upgraded graph before touching storage.
                                    workspaceFromSource (Some plan.Selection.Imported) upgradedSource
                                    |> Result.bind (fun workspace ->
                                        let sidecar = path + ".dependency.json"
                                        SessionStorage.readText sidecar
                                        |> Result.bind (fun previousManifest ->
                                            SessionStorage.replaceAtomically sidecar plan.Selection.Manifest
                                            |> Result.bind (fun () ->
                                                match SessionStorage.replaceAtomically path upgradedSource with
                                                | Ok() -> Ok()
                                                | Error error ->
                                                    // Restore the previous selection so the unchanged graph still opens.
                                                    match SessionStorage.replaceAtomically sidecar previousManifest with
                                                    | Ok() -> Error error
                                                    | Error rollback ->
                                                        let text = function AuthoringSessionError.IoFailure message -> message | other -> $"%A{other}"
                                                        Error(AuthoringSessionError.IoFailure
                                                            $"{text error} Restoring the previous dependency selection also failed: {text rollback}")))
                                        |> Result.map (fun () -> upgradedSource, workspace, plan.Selection)))))
                match result with
                | Error error ->
                    SessionStorage.releaseLock path lockHandle
                    Error error
                | Ok(source, workspace, selected) -> Ok(new AuthoringSession(path, workspace, source, lockHandle, Some selected))

    /// Storage-only recovery: retain exactly the selected ID/fingerprint/digest.
    /// Supplying another semantic version never authorizes an upgrade.
    let pinExistingDependency path sourceOverride = openUsing path true sourceOverride

    /// Creates a workspace whose imported graph combines the exact content of each dependency.
    let createNewWithDependencies (path: string) (dependencyPaths: string list): Result<AuthoringSession, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        let path = SessionStorage.fullPath path
        match SessionStorage.ensureParentDirectory path with
        | Error error -> Error error
        | Ok() when File.Exists path || File.Exists(path + ".dependency.json") -> Error(AuthoringSessionError.SnapshotFileAlreadyExists path)
        | Ok() ->
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                if File.Exists path || File.Exists(path + ".dependency.json") then
                    SessionStorage.releaseLock path lockHandle
                    Error(AuthoringSessionError.SnapshotFileAlreadyExists path)
                else
                    let pinOne = PinnedDependency.pinSource path
                    let selection =
                        match dependencyPaths with
                        | [] -> Ok None
                        | paths ->
                            paths
                            |> List.fold (fun state sourcePath ->
                                state |> Result.bind (fun pinned -> pinOne sourcePath |> Result.map (fun item -> pinned @ [ item ]))) (Ok [])
                            |> Result.bind (fun pinned ->
                                if (pinned |> List.map (fun item -> item.SourcePath) |> List.distinct).Length <> pinned.Length then
                                    Error(AuthoringSessionError.DependencyFailure "Each dependency may be selected only once.")
                                else PinnedDependency.combine pinned)
                            |> Result.map Some
                    match selection with
                    | Error error ->
                        SessionStorage.releaseLock path lockHandle
                        Error error
                    | Ok selected ->
                        let snapshot = Snapshot.empty (StableIdAllocator.newSnapshotId ())
                        let dependency = selected |> Option.map (fun item -> item.Imported)
                        let workspaceResult =
                            match dependency with
                            | None -> AgentWorkspace.create snapshot
                            | Some imported -> AgentWorkspace.createResolved imported snapshot
                        match workspaceResult with
                        | Error issues ->
                            SessionStorage.releaseLock path lockHandle
                            Error(AuthoringSessionError.WorkspaceValidationFailure issues)
                        | Ok workspace ->
                            let serialized =
                                match dependency with
                                | None -> AgentWorkspace.serializeCurrentSnapshot workspace
                                | Some imported -> AgentWorkspace.serializeWithDependency imported workspace
                            match serialized with
                            | Error error ->
                                SessionStorage.releaseLock path lockHandle
                                Error(AuthoringSessionError.SnapshotCodecFailure error)
                            | Ok source ->
                                let manifestResult =
                                    match selected with
                                    | None -> Ok()
                                    | Some selected -> SessionStorage.createAtomically (path + ".dependency.json") selected.Manifest
                                match manifestResult with
                                | Error error -> SessionStorage.releaseLock path lockHandle; Error error
                                | Ok() ->
                                    match SessionStorage.createAtomically path source with
                                    | Error error ->
                                        if selected.IsSome then SessionStorage.cleanupTemporary (path + ".dependency.json")
                                        SessionStorage.releaseLock path lockHandle
                                        Error error
                                    | Ok() -> Ok(new AuthoringSession(path, workspace, source, lockHandle, selected))

    let createNewWithDependency (path: string) (dependencyPath: string option) =
        createNewWithDependencies path (Option.toList dependencyPath)

    let createNew (path: string): Result<AuthoringSession, AuthoringSessionError> =
        createNewWithDependency path None
