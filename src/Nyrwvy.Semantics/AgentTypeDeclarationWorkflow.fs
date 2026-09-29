namespace Nyrwvy.Core

open System

type AgentTypeFieldIntent =
    { DisplayName: string
      Type: TypeRef }

type AgentTypeVariantIntent =
    { DisplayName: string
      Fields: AgentTypeFieldIntent list }

[<RequireQualifiedAccess>]
type AgentTypeDeclarationKindIntent =
    | Product of AgentTypeFieldIntent list
    | Union of AgentTypeVariantIntent list

type AgentCreateTypeDeclarationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      DisplayName: string
      Kind: AgentTypeDeclarationKindIntent }

type AgentTypeDeclarationReview =
    { RequestedChange: AgentCreateTypeDeclarationRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      CreatedType: SemanticType }

type AgentTypeDeclarationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      CreatedTypeId: TypeId
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private AgentTypeDeclarationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentTypeDeclarationReview }

type AgentTypeDeclarationProposal =
    private
    | AgentTypeDeclarationProposal of AgentTypeDeclarationProposalState

[<RequireQualifiedAccess>]
type AgentTypeDeclarationWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | FingerprintFailed of SnapshotCodecError

[<RequireQualifiedAccess>]
type AgentTypeDeclarationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentTypeDeclarationPublishedTransaction

[<RequireQualifiedAccess>]
module AgentTypeDeclarationProposal =
    let private state (AgentTypeDeclarationProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentTypeDeclarationWorkflow =
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
                Error(diagnostic "TypeDeclarationTypeParameterNotAllowed" $"{context} cannot use a function type parameter; declared types are monomorphic.")
            | TypeRef.Named typeId when Set.contains typeId typeIds -> Ok()
            | TypeRef.Named typeId ->
                Error(
                    diagnostic
                        "TypeDeclarationUnknownNamedType"
                        $"{context} references unknown type '{StableId.value typeId}'."
                )
            | TypeRef.Capability capabilityId when Set.contains capabilityId capabilityIds -> Ok()
            | TypeRef.Capability capabilityId ->
                Error(
                    diagnostic
                        "TypeDeclarationUnknownCapabilityType"
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
                            "TypeDeclarationBuiltinGenericArityMismatch"
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

    let private validateFields (snapshot: Snapshot) context (fields: AgentTypeFieldIntent list) =
        let rec loop index (remaining: AgentTypeFieldIntent list) =
            match remaining with
            | [] -> Ok()
            | field :: _ when String.IsNullOrWhiteSpace field.DisplayName ->
                Error(
                    diagnostic
                        "TypeDeclarationFieldDisplayNameBlank"
                        $"{context} field at index {index} must have a nonblank display name."
                )
            | field :: rest ->
                match validateTypeRef snapshot $"{context} field at index {index}" field.Type with
                | Error error -> Error error
                | Ok() -> loop (index + 1) rest
        loop 0 fields

    let private validatePreflight (snapshot: Snapshot) (request: AgentCreateTypeDeclarationRequest) =
        if request.ExpectedBaseSnapshotId <> snapshot.Id then
            Error(
                diagnostic
                    "TypeDeclarationExpectedBaseMismatch"
                    "The type-declaration request targets a different base snapshot."
            )
        elif String.IsNullOrWhiteSpace request.DisplayName then
            Error(
                diagnostic
                    "TypeDeclarationDisplayNameBlank"
                    "Type display name must not be blank."
            )
        elif snapshot.Types |> List.exists (fun item -> item.DisplayName = request.DisplayName) then
            Error(
                diagnostic
                    "TypeDeclarationDuplicateDisplayName"
                    $"A type named '{request.DisplayName}' already exists; type display names must be unique."
            )
        else
            match request.Kind with
            | AgentTypeDeclarationKindIntent.Product fields ->
                validateFields snapshot "Product" fields
            | AgentTypeDeclarationKindIntent.Union variants ->
                let rec loop index (remaining: AgentTypeVariantIntent list) =
                    match remaining with
                    | [] -> Ok()
                    | variant :: _ when String.IsNullOrWhiteSpace variant.DisplayName ->
                        Error(
                            diagnostic
                                "TypeDeclarationVariantDisplayNameBlank"
                                $"Union variant at index {index} must have a nonblank display name."
                        )
                    | variant :: rest ->
                        match validateFields snapshot $"Union variant at index {index}" variant.Fields with
                        | Error error -> Error error
                        | Ok() -> loop (index + 1) rest
                loop 0 variants

    let private materializeField (intent: AgentTypeFieldIntent): SemanticField =
        { Id = StableIdAllocator.newFieldId ()
          DisplayName = intent.DisplayName
          Type = intent.Type }

    let private materialize (request: AgentCreateTypeDeclarationRequest): SemanticType =
        let kind =
            match request.Kind with
            | AgentTypeDeclarationKindIntent.Product fields ->
                SemanticTypeKind.Product(fields |> List.map materializeField)
            | AgentTypeDeclarationKindIntent.Union variants ->
                SemanticTypeKind.Union(
                    variants
                    |> List.map (fun (variant: AgentTypeVariantIntent) ->
                        { Id = StableIdAllocator.newVariantId ()
                          DisplayName = variant.DisplayName
                          Fields = variant.Fields |> List.map materializeField })
                )

        { Id = StableIdAllocator.newTypeId ()
          DisplayName = request.DisplayName
          Kind = kind }

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentCreateTypeDeclarationRequest)
        : Result<AgentTypeDeclarationProposal, AgentTypeDeclarationWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: rest ->
            Error(
                AgentTypeDeclarationWorkflowError.SimulationRejected(
                    validationDiagnostics (issue :: rest)
                )
            )
        | [] ->
            match validatePreflight baseSnapshot request with
            | Error error ->
                Error(AgentTypeDeclarationWorkflowError.SimulationRejected [ error ])
            | Ok() ->
                let createdType = materialize request
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = StableIdAllocator.newSnapshotId ()
                        ParentId = Some baseSnapshot.Id
                        Types = baseSnapshot.Types @ [ createdType ] }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: rest ->
                    Error(
                        AgentTypeDeclarationWorkflowError.SimulationRejected(
                            validationDiagnostics (issue :: rest)
                        )
                    )
                | [] ->
                    match StructuralTransactionInternals.classifyReadiness proposedSnapshot with
                    | Error diagnostics ->
                        Error(AgentTypeDeclarationWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        match StructuralTransactionInternals.computeReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error(AgentTypeDeclarationWorkflowError.FingerprintFailed error)
                        | Ok receipt ->
                            let review =
                                { RequestedChange = request
                                  Receipt = receipt
                                  Readiness = readiness
                                  CreatedType = createdType }

                            Ok(
                                AgentTypeDeclarationProposal
                                    { TransactionId = transactionId
                                      ProposedSnapshot = proposedSnapshot
                                      Review = review }
                            )

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentTypeDeclarationProposal proposal)
        (policy: CommitPolicy)
        : AgentTypeDeclarationCommitResult =
        let receipt = proposal.Review.Receipt

        if receipt.TransactionId <> proposal.TransactionId
           || receipt.ProposedSnapshotId <> proposal.ProposedSnapshot.Id then
            AgentTypeDeclarationCommitResult.Rejected
                [ diagnostic
                    "PreviewReceiptRequestMismatch"
                    "The reviewed receipt does not identify this type-declaration proposal." ]
        else
            match
                StructuralTransactionInternals.publishCandidateWithMessages
                    (StructuralTransactionInternals.declarationPublicationMessages "type")
                    workspace
                    proposal.ProposedSnapshot
                    proposal.Review.Readiness
                    receipt
                    policy
            with
            | Error diagnostics -> AgentTypeDeclarationCommitResult.Rejected diagnostics
            | Ok nextWorkspace ->
                AgentTypeDeclarationCommitResult.Committed(
                    nextWorkspace,
                    { TransactionId = proposal.TransactionId
                      BaseSnapshotId = receipt.BaseSnapshotId
                      PublishedSnapshotId = proposal.ProposedSnapshot.Id
                      CreatedTypeId = proposal.Review.CreatedType.Id
                      Readiness = proposal.Review.Readiness
                      Receipt = receipt }
                )

    let commit workspace proposal =
        publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

    let commitWithPolicy workspace proposal policy =
        publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentTypeDeclarationProposal proposal)
        : AgentWorkspace * TransactionRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
