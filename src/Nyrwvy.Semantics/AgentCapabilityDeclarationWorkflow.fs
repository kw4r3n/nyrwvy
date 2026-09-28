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
    let private diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let private validationDiagnostics (issues: ValidationIssue list) =
        issues
        |> List.map (fun issue -> diagnostic ("Validation." + issue.Code) issue.Message)

    let private validateTypeRef (snapshot: Snapshot) (context: string) (typeRef: TypeRef) =
        let typeIds = snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList
        let capabilityIds = snapshot.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList

        let rec validate current =
            match current with
            | TypeRef.Primitive _ -> Ok()
            | TypeRef.TypeParameter _ ->
                Error(diagnostic "CapabilityDeclarationTypeParameterNotAllowed" $"{context} cannot use a function type parameter; declared types are monomorphic.")
            | TypeRef.Named typeId when Set.contains typeId typeIds -> Ok()
            | TypeRef.Named typeId ->
                Error(
                    diagnostic
                        "CapabilityDeclarationUnknownNamedType"
                        $"{context} references unknown type '{StableId.value typeId}'."
                )
            | TypeRef.Capability capabilityId when Set.contains capabilityId capabilityIds -> Ok()
            | TypeRef.Capability capabilityId ->
                Error(
                    diagnostic
                        "CapabilityDeclarationUnknownCapabilityType"
                        $"{context} references unknown capability '{StableId.value capabilityId}'."
                )
            | TypeRef.Function(arguments, result) ->
                (arguments @ [ result ]) |> List.fold (fun state arg -> state |> Result.bind (fun () -> validate arg)) (Ok())
            | TypeRef.BuiltinGeneric(kind, arguments) ->
                let expectedArity =
                    match kind with
                    | BuiltinGenericType.TaskScope -> 0
                    | BuiltinGenericType.Option
                    | BuiltinGenericType.List
                    | BuiltinGenericType.Task -> 1
                    | BuiltinGenericType.Result -> 2

                if List.length arguments <> expectedArity then
                    Error(
                        diagnostic
                            "CapabilityDeclarationBuiltinGenericArityMismatch"
                            $"{context} uses {kind} with {arguments.Length} type arguments; exactly {expectedArity} required."
                    )
                else
                    let rec loop = function
                        | [] -> Ok()
                        | argument :: rest ->
                            match validate argument with
                            | Error error -> Error error
                            | Ok() -> loop rest
                    loop arguments

        validate typeRef

    let private validateParameters
        (snapshot: Snapshot)
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
                        snapshot
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
                    match validateParameters snapshot index operation.Parameters with
                    | Error error -> Error error
                    | Ok() ->
                        match
                            validateTypeRef
                                snapshot
                                $"Capability operation at index {index} result"
                                operation.ResultType
                        with
                        | Error error -> Error error
                        | Ok() ->
                            match operation.FailureType with
                            | Some failureType ->
                                match
                                    validateTypeRef
                                        snapshot
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
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: rest ->
            Error(
                AgentCapabilityDeclarationWorkflowError.SimulationRejected(
                    validationDiagnostics (issue :: rest)
                )
            )
        | [] ->
            match validatePreflight baseSnapshot request with
            | Error error ->
                Error(AgentCapabilityDeclarationWorkflowError.SimulationRejected [ error ])
            | Ok() ->
                let createdCapability = materialize request
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = StableIdAllocator.newSnapshotId ()
                        ParentId = Some baseSnapshot.Id
                        Capabilities = baseSnapshot.Capabilities @ [ createdCapability ] }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: rest ->
                    Error(
                        AgentCapabilityDeclarationWorkflowError.SimulationRejected(
                            validationDiagnostics (issue :: rest)
                        )
                    )
                | [] ->
                    match StructuralTransactionInternals.classifyReadiness proposedSnapshot with
                    | Error diagnostics ->
                        Error(AgentCapabilityDeclarationWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        match StructuralTransactionInternals.computeReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error(AgentCapabilityDeclarationWorkflowError.FingerprintFailed error)
                        | Ok receipt ->
                            let review =
                                { RequestedChange = request
                                  Receipt = receipt
                                  Readiness = readiness
                                  CreatedCapability = createdCapability }

                            Ok(
                                AgentCapabilityDeclarationProposal
                                    { TransactionId = transactionId
                                      ProposedSnapshot = proposedSnapshot
                                      Review = review }
                            )

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
