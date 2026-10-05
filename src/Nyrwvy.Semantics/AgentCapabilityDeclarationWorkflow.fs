namespace Nyrwvy.Core

open System

type AgentCapabilityParameterIntent =
    { DisplayName: string
      Type: TypeRef }

type AgentCapabilityOperationIntent =
    { DisplayName: string
      Parameters: AgentCapabilityParameterIntent list
      ResultType: TypeRef
      FailureType: TypeRef option
      EffectId: EffectId }

type AgentCreateCapabilityDeclarationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      DisplayName: string
      Operations: AgentCapabilityOperationIntent list }

type AgentCapabilityDeclarationReview =
    { RequestedChange: AgentCreateCapabilityDeclarationRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      CreatedCapability: SemanticCapability }

type AgentCapabilityDeclarationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      CreatedCapabilityId: CapabilityId
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private AgentCapabilityDeclarationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentCapabilityDeclarationReview }

type AgentCapabilityDeclarationProposal =
    private
    | AgentCapabilityDeclarationProposal of AgentCapabilityDeclarationProposalState

[<RequireQualifiedAccess>]
type AgentCapabilityDeclarationWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | FingerprintFailed of SnapshotCodecError

[<RequireQualifiedAccess>]
type AgentCapabilityDeclarationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentCapabilityDeclarationPublishedTransaction

[<RequireQualifiedAccess>]
module AgentCapabilityDeclarationProposal =
    let private state (AgentCapabilityDeclarationProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentCapabilityDeclarationWorkflow =
    open StructuralTransactionInternals

    let private validateParameters
        validateTypeRef
        operationIndex
        (parameters: AgentCapabilityParameterIntent list)
        =
        let rec loop index (remaining: AgentCapabilityParameterIntent list) =
            match remaining with
            | [] -> Ok()
            | parameter :: _ when String.IsNullOrWhiteSpace parameter.DisplayName ->
                Error(
                    diagnostic
                        "CapabilityDeclarationParameterDisplayNameBlank"
                        $"Operation at index {operationIndex} parameter at index {index} must have a nonblank display name."
                )
            | parameter :: rest ->
                match
                    validateTypeRef
                        $"Operation at index {operationIndex} parameter at index {index}"
                        parameter.Type
                with
                | Error error -> Error error
                | Ok() -> loop (index + 1) rest
        loop 0 parameters

    let private validatePreflight
        (snapshot: Snapshot)
        (request: AgentCreateCapabilityDeclarationRequest)
        =
        let effectIds = snapshot.Effects |> List.map (fun item -> item.Id) |> Set.ofList
        let validateTypeRef =
            StructuralTransactionInternals.validateDeclaredTypeRef
                "CapabilityDeclaration"
                (snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList)
                (snapshot.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList)

        if request.ExpectedBaseSnapshotId <> snapshot.Id then
            Error(
                diagnostic
                    "CapabilityDeclarationExpectedBaseMismatch"
                    "The capability-declaration request targets a different base snapshot."
            )
        elif String.IsNullOrWhiteSpace request.DisplayName then
            Error(
                diagnostic
                    "CapabilityDeclarationDisplayNameBlank"
                    "Capability display name must not be blank."
            )
        elif snapshot.Capabilities |> List.exists (fun item -> item.DisplayName = request.DisplayName) then
            Error(
                diagnostic
                    "CapabilityDeclarationDuplicateDisplayName"
                    $"A capability named '{request.DisplayName}' already exists; capability display names must be unique."
            )
        else
            let rec loop index (remaining: AgentCapabilityOperationIntent list) =
                match remaining with
                | [] -> Ok()
                | operation :: _ when String.IsNullOrWhiteSpace operation.DisplayName ->
                    Error(
                        diagnostic
                            "CapabilityDeclarationOperationDisplayNameBlank"
                            $"Capability operation at index {index} must have a nonblank display name."
                    )
                | operation :: _ when not (Set.contains operation.EffectId effectIds) ->
                    Error(
                        diagnostic
                            "CapabilityDeclarationUnknownEffect"
                            $"Capability operation at index {index} references unknown effect '{StableId.value operation.EffectId}'."
                    )
                | operation :: rest ->
                    match validateParameters validateTypeRef index operation.Parameters with
                    | Error error -> Error error
                    | Ok() ->
                        match
                            validateTypeRef
                                $"Capability operation at index {index} result"
                                operation.ResultType
                        with
                        | Error error -> Error error
                        | Ok() ->
                            match operation.FailureType with
                            | Some failureType ->
                                match
                                    validateTypeRef
                                        $"Capability operation at index {index} failure"
                                        failureType
                                with
                                | Error error -> Error error
                                | Ok() -> loop (index + 1) rest
                            | None -> loop (index + 1) rest
            loop 0 request.Operations

    let private materializeParameter
        (intent: AgentCapabilityParameterIntent)
        : SemanticCapabilityParameter =
        { Id = StableIdAllocator.newCapabilityParameterId ()
          DisplayName = intent.DisplayName
          Type = intent.Type }

    let private materializeOperation
        (intent: AgentCapabilityOperationIntent)
        : SemanticCapabilityOperation =
        { Id = StableIdAllocator.newCapabilityOperationId ()
          DisplayName = intent.DisplayName
          Parameters = intent.Parameters |> List.map materializeParameter
          ResultType = intent.ResultType
          FailureType = intent.FailureType
          EffectId = intent.EffectId }

    let private materialize
        (request: AgentCreateCapabilityDeclarationRequest)
        : SemanticCapability =
        { Id = StableIdAllocator.newCapabilityId ()
          DisplayName = request.DisplayName
          Operations = request.Operations |> List.map materializeOperation }

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentCreateCapabilityDeclarationRequest)
        : Result<AgentCapabilityDeclarationProposal, AgentCapabilityDeclarationWorkflowError> =
        simulateDeclaration
            workspace
            (fun snapshot -> validatePreflight snapshot request)
            (fun () -> materialize request)
            (fun created snapshot -> { snapshot with Capabilities = snapshot.Capabilities @ [ created ] })
        |> Result.map (fun candidate ->
            AgentCapabilityDeclarationProposal
                { TransactionId = candidate.TransactionId
                  ProposedSnapshot = candidate.ProposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = candidate.Receipt
                      Readiness = candidate.Readiness
                      CreatedCapability = candidate.Prepared } })
        |> Result.mapError (function
            | DeclarationSimulationFailure.Rejected diagnostics -> AgentCapabilityDeclarationWorkflowError.SimulationRejected diagnostics
            | DeclarationSimulationFailure.FingerprintFailed error -> AgentCapabilityDeclarationWorkflowError.FingerprintFailed error)

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentCapabilityDeclarationProposal proposal)
        (policy: CommitPolicy)
        : AgentCapabilityDeclarationCommitResult =
        let receipt = proposal.Review.Receipt

        if receipt.TransactionId <> proposal.TransactionId
           || receipt.ProposedSnapshotId <> proposal.ProposedSnapshot.Id then
            AgentCapabilityDeclarationCommitResult.Rejected
                [ diagnostic
                    "PreviewReceiptRequestMismatch"
                    "The reviewed receipt does not identify this capability-declaration proposal." ]
        else
            match
                StructuralTransactionInternals.publishCandidateWithMessages
                    (StructuralTransactionInternals.declarationPublicationMessages "capability")
                    workspace
                    proposal.ProposedSnapshot
                    proposal.Review.Readiness
                    receipt
                    policy
            with
            | Error diagnostics -> AgentCapabilityDeclarationCommitResult.Rejected diagnostics
            | Ok nextWorkspace ->
                AgentCapabilityDeclarationCommitResult.Committed(
                    nextWorkspace,
                    { TransactionId = proposal.TransactionId
                      BaseSnapshotId = receipt.BaseSnapshotId
                      PublishedSnapshotId = proposal.ProposedSnapshot.Id
                      CreatedCapabilityId = proposal.Review.CreatedCapability.Id
                      Readiness = proposal.Review.Readiness
                      Receipt = receipt }
                )

    let commit workspace proposal =
        publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

    let commitWithPolicy workspace proposal policy =
        publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentCapabilityDeclarationProposal proposal)
        : AgentWorkspace * TransactionRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
