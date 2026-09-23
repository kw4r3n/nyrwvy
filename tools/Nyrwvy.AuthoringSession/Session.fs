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
    let readCompleteDependency (path: string): Result<Snapshot, AuthoringSessionError> =
        readText path
        |> Result.bind (fun source ->
            SnapshotCodec.deserialize source
            |> Result.mapError AuthoringSessionError.SnapshotCodecFailure)
        |> Result.bind (fun imported ->
            if imported.Obligations |> List.exists (fun obligation -> obligation.Status = ObligationStatus.Open) then
                Error(AuthoringSessionError.DependencyFailure "The selected dependency has open obligations; complete it before pinning.")
            else Ok imported)

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

    let createAtomically (path: string) (content: string): Result<unit, AuthoringSessionError> =
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

    let replaceAtomically (path: string) (content: string): Result<unit, AuthoringSessionError> =
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

    let ensureParentDirectory (path: string): Result<unit, AuthoringSessionError> =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            Ok()
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

type AuthoringSession internal (path: string, initialWorkspace: AgentWorkspace, initialPersistedSource: string, lockHandle: FileStream, dependency: Snapshot option, dependencyManifest: string option) =
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
                item.ResultingChange.AffectedFunctionIds, item.ResultingChange.AffectedTypeIds
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
        if functions |> List.exists (fun id -> Set.contains id importedFunctionIds)
           || types |> List.exists (fun id -> Set.contains id importedTypeIds) then
            Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot
                [ { Code = "ImportedDeclarationModified"; Message = "Dependency declarations are read-only; no proposal was registered." } ]))
        else
            let proposalId = StableId.value transactionId
            proposals.Add(proposalId, (transactionId, pending))
            Ok { ProposalId = proposalId; Review = review }

    let dependencyStillPinned () =
        match dependency, dependencyManifest with
        | Some imported, Some manifest ->
            use document = JsonDocument.Parse(manifest)
            let sourcePath = document.RootElement.GetProperty("path").GetString()
            match SessionStorage.readText sourcePath with
            | Error _ -> false
            | Ok source ->
                match SnapshotCodec.deserialize source with
                | Error _ -> false
                | Ok current ->
                    match SnapshotFingerprint.compute current, SnapshotFingerprint.compute imported with
                    | Ok actual, Ok expected -> actual = expected && current.Id = imported.Id
                    | _ -> false
        | _ -> true

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
                    | Ok _ ->
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
    member _.DependencySelection = dependencyManifest

    member _.Status(): Result<AuthoringSessionStatus, AuthoringSessionError> =
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
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentWorkflow.simulate workspace operations with
            | Error error -> Error(AuthoringSessionError.WorkflowSimulationFailure error)
            | Ok proposal ->
                register (AgentChangeProposal.transactionId proposal) (PendingProposal.Operations proposal) (AuthoringProposalReview.Operations(AgentChangeProposal.review proposal))

    member _.SimulateReplaceBody(request: AgentReplaceFunctionBodyWithExpressionRecipe): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralChangeProposal.transactionId proposal) (PendingProposal.ReplaceBody proposal) (AuthoringProposalReview.ReplaceBody(AgentExpressionStructuralChangeProposal.review proposal))

    member _.SimulateReplaceTarget(request: AgentExpressionStructuralWorkflow.Targeted.Request): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.Targeted.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralWorkflow.Targeted.Proposal.transactionId proposal) (PendingProposal.ReplaceTarget proposal) (AuthoringProposalReview.ReplaceTarget(AgentExpressionStructuralWorkflow.Targeted.Proposal.review proposal))

    member _.SimulateCreateFunction(request: AgentExpressionStructuralWorkflow.FunctionCreation.Request): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.transactionId proposal) (PendingProposal.CreateFunction proposal) (AuthoringProposalReview.CreateFunction(AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review proposal))

    member _.SimulateParameterTypeMigration(request: AgentParameterTypeMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentParameterTypeMigrationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentParameterTypeMigrationProposal.transactionId proposal) (PendingProposal.ParameterTypeMigration proposal) (AuthoringProposalReview.ParameterTypeMigration(AgentParameterTypeMigrationProposal.review proposal))

    member _.SimulateCreateType(request: AgentCreateTypeDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentTypeDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.TypeDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentTypeDeclarationProposal.transactionId proposal) (PendingProposal.CreateType proposal) (AuthoringProposalReview.CreateType(AgentTypeDeclarationProposal.review proposal))

    member _.SimulateAddFieldlessUnionVariantLike(request: AgentAddFieldlessUnionVariantLikeRequest): Result<AuthoringProposal, AuthoringSessionError> =
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
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentEffectDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.EffectDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentEffectDeclarationProposal.transactionId proposal) (PendingProposal.CreateEffect proposal) (AuthoringProposalReview.CreateEffect(AgentEffectDeclarationProposal.review proposal))

    member _.SimulateCreateCapability(request: AgentCreateCapabilityDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentCapabilityDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.CapabilityDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentCapabilityDeclarationProposal.transactionId proposal) (PendingProposal.CreateCapability proposal) (AuthoringProposalReview.CreateCapability(AgentCapabilityDeclarationProposal.review proposal))

    member _.Commit(proposalId: string, policy: CommitPolicy): Result<AuthoringCommitReceipt, AuthoringSessionError> =
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

    let private loadDependency (path: string): Result<(Snapshot * string) option, AuthoringSessionError> =
        let manifestPath = path + ".dependency.json"
        // Read directly: File.Exists treats permission failures as absence.
        try
            let manifest = File.ReadAllText(manifestPath, Encoding.UTF8)
            use document = JsonDocument.Parse(manifest)
            let root = document.RootElement
            let fields = root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toList
            if List.sort fields <> [ "fingerprint"; "path"; "snapshotId" ] then
                Error(AuthoringSessionError.DependencyFailure "Invalid dependency manifest.")
            else
                let sourcePath = root.GetProperty("path").GetString()
                let fingerprint = root.GetProperty("fingerprint").GetString()
                let snapshotId = root.GetProperty("snapshotId").GetString()
                if String.IsNullOrWhiteSpace sourcePath || String.IsNullOrWhiteSpace fingerprint
                   || String.IsNullOrWhiteSpace snapshotId then
                    Error(AuthoringSessionError.DependencyFailure "Dependency manifest fields must be non-blank strings.")
                elif not (Path.IsPathFullyQualified sourcePath) || not (File.Exists sourcePath) then
                    Error(AuthoringSessionError.DependencyFailure "The selected dependency is unavailable.")
                else
                    match SessionStorage.readCompleteDependency sourcePath with
                    | Error error -> Error error
                    | Ok imported ->
                        match SnapshotFingerprint.compute imported with
                        | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
                        | Ok actual when SnapshotFingerprint.value actual = fingerprint
                                         && StableId.value imported.Id = snapshotId -> Ok(Some(imported, manifest))
                        | Ok _ -> Error(AuthoringSessionError.DependencyFailure "The selected dependency version changed.")
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Ok None
        | :? JsonException
        | :? InvalidOperationException
        | :? KeyNotFoundException
        | :? ArgumentException
        | :? NotSupportedException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency manifest.")
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.DependencyFailure error.Message)
        | :? IOException as error -> Error(AuthoringSessionError.DependencyFailure error.Message)

    let openExisting (path: string): Result<AuthoringSession, AuthoringSessionError> =
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
                    let loaded = loadDependency path
                    match loaded |> Result.bind (fun selected ->
                        workspaceFromSource (selected |> Option.map fst) source
                        |> Result.map (fun workspace -> selected, workspace)) with
                    | Error error ->
                        lockHandle.Dispose()
                        Error error
                    | Ok(selected, workspace) ->
                        Ok(new AuthoringSession(path, workspace, source, lockHandle,
                            selected |> Option.map fst, selected |> Option.map snd))

    let createNewWithDependency (path: string) (dependencyPath: string option): Result<AuthoringSession, AuthoringSessionError> =
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
                                        match SnapshotFingerprint.compute imported with
                                        | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
                                        | Ok fingerprint ->
                                            let manifest = JsonSerializer.Serialize(
                                                {| path = sourcePath
                                                   snapshotId = StableId.value imported.Id
                                                   fingerprint = SnapshotFingerprint.value fingerprint |})
                                            Ok(Some(imported, manifest))
                            with
                            | :? ArgumentException
                            | :? NotSupportedException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency path.")
                    match selection with
                    | Error error ->
                        lockHandle.Dispose()
                        Error error
                    | Ok selected ->
                        let snapshot = Snapshot.empty (StableIdAllocator.newSnapshotId ())
                        let dependency = selected |> Option.map fst
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
                                    | Some(_, manifest) -> SessionStorage.createAtomically (path + ".dependency.json") manifest
                                match manifestResult with
                                | Error error -> lockHandle.Dispose(); Error error
                                | Ok() ->
                                    match SessionStorage.createAtomically path source with
                                    | Error error ->
                                        if selected.IsSome then SessionStorage.cleanupTemporary (path + ".dependency.json")
                                        lockHandle.Dispose()
                                        Error error
                                    | Ok() -> Ok(new AuthoringSession(path, workspace, source, lockHandle,
                                        dependency, selected |> Option.map snd))

    let createNew (path: string): Result<AuthoringSession, AuthoringSessionError> =
        createNewWithDependency path None
