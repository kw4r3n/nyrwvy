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
    | CreateType of AgentTypeDeclarationReview
    | FieldlessUnionVariantEvolution of AgentFieldlessUnionVariantEvolutionReview
    | CreateEffect of AgentEffectDeclarationReview
    | CreateCapability of AgentCapabilityDeclarationReview

type AuthoringProposal =
    { ProposalId: string
      Review: AuthoringProposalReview }

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
    | CreateType of AgentTypeDeclarationProposal
    | FieldlessUnionVariantEvolution of AgentFieldlessUnionVariantEvolutionProposal
    | CreateEffect of AgentEffectDeclarationProposal
    | CreateCapability of AgentCapabilityDeclarationProposal

module private SessionStorage =
    let fullPath (path: string): string = Path.GetFullPath(path)
    let lockPath (path: string): string = path + ".lock"

    let acquireLock (path: string): Result<FileStream, AuthoringSessionError> =
        try
            Ok(new FileStream(lockPath path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        with
        | :? IOException -> Error(AuthoringSessionError.LockUnavailable path)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    let readText (path: string): Result<string, AuthoringSessionError> =
        try Ok(File.ReadAllText(path, Encoding.UTF8))
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    // SnapshotCodec establishes validity; pinning additionally requires completeness.
    let decodeCompleteDependency (source: string): Result<Snapshot, AuthoringSessionError> =
        SnapshotCodec.deserialize source
        |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
        |> Result.bind (fun imported ->
            if imported.Obligations |> List.exists (fun obligation -> obligation.Status = ObligationStatus.Open) then
                Error(AuthoringSessionError.DependencyFailure "The selected dependency has open obligations; complete it before pinning.")
            else Ok imported)

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

type internal PinnedDependencySelection =
    { Imported: Snapshot
      Manifest: string
      SourcePath: string
      Fingerprint: string
      IsEmbedded: bool }

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
                 Fingerprint = fingerprint; IsEmbedded = true }
        | Error error, _
        | _, Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)

    let describe selected =
        // Discovery must not repeat the potentially large stored snapshot.
        JsonSerializer.Serialize(
            {| path = selected.SourcePath
               snapshotId = StableId.value selected.Imported.Id
               fingerprint = selected.Fingerprint
               storage = if selected.IsEmbedded then "embedded" else "legacySource" |})

    let load path sourceOverride: Result<PinnedDependencySelection option, AuthoringSessionError> =
        // Read directly: File.Exists treats permission failures as absence.
        try
            let manifest = File.ReadAllText(path + ".dependency.json", Encoding.UTF8)
            use document = JsonDocument.Parse(manifest)
            let root = document.RootElement
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
                            Ok(Some { Imported = imported; Manifest = manifest; SourcePath = selectedPath
                                      Fingerprint = fingerprint; IsEmbedded = embedded })
                        | Ok _ -> Error(AuthoringSessionError.DependencyFailure "The selected dependency version changed. Pin only accepts the exact previously selected content, not an upgrade."))
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

type AuthoringSession internal (path: string, initialWorkspace: AgentWorkspace, initialPersistedSource: string, lockHandle: FileStream, selection: PinnedDependencySelection option) =
    let dependency = selection |> Option.map (fun item -> item.Imported)
    let dependencyManifest = selection |> Option.map (fun item -> item.Manifest)
    let proposals = Dictionary<string, TransactionId * PendingProposal>(StringComparer.Ordinal)
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

    let register transactionId pending review =
        // Check resulting changes too: signature/variant edits can rewrite other
        // callers or match owners, not just the intent's primary target.
        let functions, types =
            match review with
            | AuthoringProposalReview.Operations item ->
                item.ResultingChange.AffectedFunctionIds @ (item.ResultingChange.Diff.FunctionsRemoved |> List.map (fun removed -> removed.FunctionId)),
                item.ResultingChange.AffectedTypeIds @ (item.ResultingChange.Diff.TypesRemoved |> List.map (fun removed -> removed.TypeId))
            | AuthoringProposalReview.ReplaceBody item -> [ item.RequestedChange.TargetFunctionId ], []
            | AuthoringProposalReview.ReplaceTarget item -> [ item.RequestedChange.TargetFunctionId ], []
            | AuthoringProposalReview.ParameterTypeMigration item ->
                item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), []
            | AuthoringProposalReview.FieldlessUnionVariantEvolution item ->
                item.RepairedFunctionIds, [ item.UpdatedType.Id ]
            | AuthoringProposalReview.CreateFunction _
            | AuthoringProposalReview.CreateType _
            | AuthoringProposalReview.CreateEffect _
            | AuthoringProposalReview.CreateCapability _ -> [], []
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
            Ok { ProposalId = proposalId; Review = review }

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
            Ok { TransactionId = transactionId; PublishedSnapshotId = publishedSnapshotId; Readiness = readiness }

    member _.Path = path
    member _.DependencySelection = selection |> Option.map PinnedDependency.describe

    /// Read-only execution must not run a stale in-memory graph after the selected
    /// snapshot or its pinned dependency was changed outside the session.
    member _.VerifyPersistedSource(): Result<unit, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())

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

    member _.SimulateOperations(operations: AgentOperationIntent list): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentWorkflow.simulate workspace operations with
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
                register (AgentParameterTypeMigrationProposal.transactionId proposal) (PendingProposal.ParameterTypeMigration proposal) (AuthoringProposalReview.ParameterTypeMigration(AgentParameterTypeMigrationProposal.review proposal))

    member _.SimulateCreateType(request: AgentCreateTypeDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentTypeDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.TypeDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentTypeDeclarationProposal.transactionId proposal) (PendingProposal.CreateType proposal) (AuthoringProposalReview.CreateType(AgentTypeDeclarationProposal.review proposal))

    member _.SimulateAddFieldlessUnionVariantLike(request: AgentAddFieldlessUnionVariantLikeRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentFieldlessUnionVariantEvolutionWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register
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
            | true, (_, PendingProposal.CreateType proposal) ->
                match AgentTypeDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentTypeDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentTypeDeclarationCommitResult.Committed(candidate, publication) ->
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
            lockHandle.Dispose()

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
                    lockHandle.Dispose()
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
                        lockHandle.Dispose()
                        Error error
                    | Ok(selected, workspace) ->
                        Ok(new AuthoringSession(path, workspace, source, lockHandle, selected))

    let openExisting path = openUsing path false None

    /// Storage-only recovery: retain exactly the selected ID/fingerprint/digest.
    /// Supplying another semantic version never authorizes an upgrade.
    let pinExistingDependency path sourceOverride = openUsing path true sourceOverride

    let createNewWithDependency (path: string) (dependencyPath: string option): Result<AuthoringSession, AuthoringSessionError> =
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
                    lockHandle.Dispose()
                    Error(AuthoringSessionError.SnapshotFileAlreadyExists path)
                else
                    let selection =
                        match dependencyPath with
                        | None -> Ok None
                        | Some sourcePath ->
                            try
                                let sourcePath = SessionStorage.fullPath sourcePath
                                if sourcePath = path || not (File.Exists sourcePath) then
                                    Error(AuthoringSessionError.DependencyFailure "The selected dependency is unavailable.")
                                elif File.Exists(sourcePath + ".dependency.json") then
                                    Error(AuthoringSessionError.DependencyFailure "Nested dependencies are not supported.")
                                else
                                    match SessionStorage.readCompleteDependency sourcePath with
                                    | Error error -> Error error
                                    | Ok imported ->
                                        PinnedDependency.embed sourcePath imported |> Result.map Some
                            with
                            | :? ArgumentException
                            | :? NotSupportedException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency path.")
                    match selection with
                    | Error error ->
                        lockHandle.Dispose()
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
                            lockHandle.Dispose()
                            Error(AuthoringSessionError.WorkspaceValidationFailure issues)
                        | Ok workspace ->
                            let serialized =
                                match dependency with
                                | None -> AgentWorkspace.serializeCurrentSnapshot workspace
                                | Some imported -> AgentWorkspace.serializeWithDependency imported workspace
                            match serialized with
                            | Error error ->
                                lockHandle.Dispose()
                                Error(AuthoringSessionError.SnapshotCodecFailure error)
                            | Ok source ->
                                let manifestResult =
                                    match selected with
                                    | None -> Ok()
                                    | Some selected -> SessionStorage.createAtomically (path + ".dependency.json") selected.Manifest
                                match manifestResult with
                                | Error error -> lockHandle.Dispose(); Error error
                                | Ok() ->
                                    match SessionStorage.createAtomically path source with
                                    | Error error ->
                                        if selected.IsSome then SessionStorage.cleanupTemporary (path + ".dependency.json")
                                        lockHandle.Dispose()
                                        Error error
                                    | Ok() -> Ok(new AuthoringSession(path, workspace, source, lockHandle, selected))

    let createNew (path: string): Result<AuthoringSession, AuthoringSessionError> =
        createNewWithDependency path None
