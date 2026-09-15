namespace NyamNyam.Tools.AuthoringSession

open System
open System.Collections.Generic
open System.IO
open System.Text
open NyamNyam.Core

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
    | CreateType of AgentTypeDeclarationReview
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
    | CreateType of AgentTypeDeclarationProposal
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

    let private writeTemporary (path: string) (content: string): Result<string, AuthoringSessionError> =
        let directory = Path.GetDirectoryName(path)
        let tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp")
        try
            File.WriteAllText(tempPath, content, UTF8Encoding(false))
            Ok tempPath
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    let private cleanupTemporary (tempPath: string) =
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

type AuthoringSession internal (path: string, initialWorkspace: AgentWorkspace, initialPersistedSource: string, lockHandle: FileStream) =
    let proposals = Dictionary<string, PendingProposal>(StringComparer.Ordinal)
    let mutable workspace = initialWorkspace
    let mutable persistedSource = initialPersistedSource
    let mutable invalidated = false
    let mutable closed = false

    let ensureUsable () =
        if closed then Error AuthoringSessionError.SessionClosed
        elif invalidated then Error AuthoringSessionError.SessionInvalidated
        else Ok()

    let register transactionId pending review =
        let proposalId = StableId.value transactionId
        proposals.Add(proposalId, pending)
        { ProposalId = proposalId; Review = review }

    let persistCandidate candidate =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentWorkspace.serializeCurrentSnapshot candidate with
            | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
            | Ok candidateSource ->
                if not (File.Exists path) then
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
                Ok(register (AgentChangeProposal.transactionId proposal) (PendingProposal.Operations proposal) (AuthoringProposalReview.Operations(AgentChangeProposal.review proposal)))

    member _.SimulateReplaceBody(request: AgentReplaceFunctionBodyWithExpressionRecipe): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                Ok(register (AgentExpressionStructuralChangeProposal.transactionId proposal) (PendingProposal.ReplaceBody proposal) (AuthoringProposalReview.ReplaceBody(AgentExpressionStructuralChangeProposal.review proposal)))

    member _.SimulateReplaceTarget(request: AgentExpressionStructuralWorkflow.Targeted.Request): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.Targeted.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                Ok(register (AgentExpressionStructuralWorkflow.Targeted.Proposal.transactionId proposal) (PendingProposal.ReplaceTarget proposal) (AuthoringProposalReview.ReplaceTarget(AgentExpressionStructuralWorkflow.Targeted.Proposal.review proposal)))

    member _.SimulateCreateFunction(request: AgentExpressionStructuralWorkflow.FunctionCreation.Request): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                Ok(register (AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.transactionId proposal) (PendingProposal.CreateFunction proposal) (AuthoringProposalReview.CreateFunction(AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review proposal)))

    member _.SimulateCreateType(request: AgentCreateTypeDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentTypeDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.TypeDeclarationSimulationFailure error)
            | Ok proposal ->
                Ok(register (AgentTypeDeclarationProposal.transactionId proposal) (PendingProposal.CreateType proposal) (AuthoringProposalReview.CreateType(AgentTypeDeclarationProposal.review proposal)))

    member _.SimulateCreateEffect(request: AgentCreateEffectDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentEffectDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.EffectDeclarationSimulationFailure error)
            | Ok proposal ->
                Ok(register (AgentEffectDeclarationProposal.transactionId proposal) (PendingProposal.CreateEffect proposal) (AuthoringProposalReview.CreateEffect(AgentEffectDeclarationProposal.review proposal)))

    member _.SimulateCreateCapability(request: AgentCreateCapabilityDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentCapabilityDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.CapabilityDeclarationSimulationFailure error)
            | Ok proposal ->
                Ok(register (AgentCapabilityDeclarationProposal.transactionId proposal) (PendingProposal.CreateCapability proposal) (AuthoringProposalReview.CreateCapability(AgentCapabilityDeclarationProposal.review proposal)))

    member _.Commit(proposalId: string, policy: CommitPolicy): Result<AuthoringCommitReceipt, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match proposals.TryGetValue proposalId with
            | false, _ -> Error(AuthoringSessionError.ProposalNotFound proposalId)
            | true, PendingProposal.Operations proposal ->
                match AgentWorkflow.commitWithPolicy workspace proposal policy with
                | AgentCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, PendingProposal.ReplaceBody proposal ->
                match AgentExpressionStructuralWorkflow.commitWithPolicy workspace proposal policy with
                | AgentStructuralCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentStructuralCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, PendingProposal.ReplaceTarget proposal ->
                match AgentExpressionStructuralWorkflow.Targeted.commitWithPolicy workspace proposal policy with
                | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, PendingProposal.CreateFunction proposal ->
                match AgentExpressionStructuralWorkflow.FunctionCreation.commitWithPolicy workspace proposal policy with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, PendingProposal.CreateType proposal ->
                match AgentTypeDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentTypeDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentTypeDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, PendingProposal.CreateEffect proposal ->
                match AgentEffectDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentEffectDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentEffectDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, PendingProposal.CreateCapability proposal ->
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
            | true, PendingProposal.Operations proposal ->
                let _, receipt = AgentWorkflow.reject workspace proposal
                proposals.Remove proposalId |> ignore
                Ok { TransactionId = receipt.TransactionId; RetainedSnapshotId = receipt.RetainedSnapshotId }
            | true, PendingProposal.ReplaceBody proposal ->
                let _, receipt = AgentExpressionStructuralWorkflow.reject workspace proposal
                proposals.Remove proposalId |> ignore
                Ok { TransactionId = receipt.TransactionId; RetainedSnapshotId = receipt.RetainedSnapshotId }
            | true, PendingProposal.ReplaceTarget proposal ->
                let _, receipt = AgentExpressionStructuralWorkflow.Targeted.reject workspace proposal
                proposals.Remove proposalId |> ignore
                Ok { TransactionId = receipt.TransactionId; RetainedSnapshotId = receipt.RetainedSnapshotId }
            | true, PendingProposal.CreateFunction proposal ->
                let _, receipt = AgentExpressionStructuralWorkflow.FunctionCreation.reject workspace proposal
                proposals.Remove proposalId |> ignore
                Ok { TransactionId = receipt.TransactionId; RetainedSnapshotId = receipt.RetainedSnapshotId }
            | true, PendingProposal.CreateType proposal ->
                let _, receipt = AgentTypeDeclarationWorkflow.reject workspace proposal
                proposals.Remove proposalId |> ignore
                Ok { TransactionId = receipt.TransactionId; RetainedSnapshotId = receipt.RetainedSnapshotId }
            | true, PendingProposal.CreateEffect proposal ->
                let _, receipt = AgentEffectDeclarationWorkflow.reject workspace proposal
                proposals.Remove proposalId |> ignore
                Ok { TransactionId = receipt.TransactionId; RetainedSnapshotId = receipt.RetainedSnapshotId }
            | true, PendingProposal.CreateCapability proposal ->
                let _, receipt = AgentCapabilityDeclarationWorkflow.reject workspace proposal
                proposals.Remove proposalId |> ignore
                Ok { TransactionId = receipt.TransactionId; RetainedSnapshotId = receipt.RetainedSnapshotId }

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
    let private workspaceFromSource (source: string): Result<AgentWorkspace, AuthoringSessionError> =
        match SnapshotCodec.deserialize source with
        | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
        | Ok snapshot -> AgentWorkspace.create snapshot |> Result.mapError AuthoringSessionError.WorkspaceValidationFailure

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
                    match workspaceFromSource source with
                    | Error error ->
                        lockHandle.Dispose()
                        Error error
                    | Ok workspace -> Ok(new AuthoringSession(path, workspace, source, lockHandle))

    let createNew (path: string): Result<AuthoringSession, AuthoringSessionError> =
        let path = SessionStorage.fullPath path
        match SessionStorage.ensureParentDirectory path with
        | Error error -> Error error
        | Ok() when File.Exists path -> Error(AuthoringSessionError.SnapshotFileAlreadyExists path)
        | Ok() ->
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                if File.Exists path then
                    lockHandle.Dispose()
                    Error(AuthoringSessionError.SnapshotFileAlreadyExists path)
                else
                    let snapshot = Snapshot.empty (StableIdAllocator.newSnapshotId ())
                    match AgentWorkspace.create snapshot with
                    | Error issues ->
                        lockHandle.Dispose()
                        Error(AuthoringSessionError.WorkspaceValidationFailure issues)
                    | Ok workspace ->
                        match AgentWorkspace.serializeCurrentSnapshot workspace with
                        | Error error ->
                            lockHandle.Dispose()
                            Error(AuthoringSessionError.SnapshotCodecFailure error)
                        | Ok source ->
                            match SessionStorage.createAtomically path source with
                            | Error error ->
                                lockHandle.Dispose()
                                Error error
                            | Ok() -> Ok(new AuthoringSession(path, workspace, source, lockHandle))
