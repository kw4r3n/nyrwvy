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

/// One member of a type group. `Reference` is a request-local TypeId that fields anywhere in the
/// group may name (`TypeRef.Named`) to refer to this member before it exists; simulation allocates
/// the real TypeId and rewrites every reference. References are never persisted.
type AgentTypeDeclarationGroupMember =
    { Reference: TypeId
      DisplayName: string
      Kind: AgentTypeDeclarationKindIntent }

/// Creates one or more types together, so they may refer to themselves and to each other.
type AgentCreateTypeDeclarationGroupRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      Declarations: AgentTypeDeclarationGroupMember list }

type AgentTypeDeclarationReview =
    { RequestedChange: AgentCreateTypeDeclarationRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      CreatedType: SemanticType }

type AgentTypeDeclarationGroupReview =
    { RequestedChange: AgentCreateTypeDeclarationGroupRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      CreatedTypes: SemanticType list }

type AgentTypeDeclarationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      CreatedTypeId: TypeId
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type AgentTypeDeclarationGroupPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      CreatedTypeIds: TypeId list
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private AgentTypeDeclarationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentTypeDeclarationReview }

type AgentTypeDeclarationProposal =
    private
    | AgentTypeDeclarationProposal of AgentTypeDeclarationProposalState

type private AgentTypeDeclarationGroupProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentTypeDeclarationGroupReview }

type AgentTypeDeclarationGroupProposal =
    private
    | AgentTypeDeclarationGroupProposal of AgentTypeDeclarationGroupProposalState

[<RequireQualifiedAccess>]
type AgentTypeDeclarationWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | FingerprintFailed of SnapshotCodecError

[<RequireQualifiedAccess>]
type AgentTypeDeclarationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentTypeDeclarationPublishedTransaction

[<RequireQualifiedAccess>]
type AgentTypeDeclarationGroupCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentTypeDeclarationGroupPublishedTransaction

[<RequireQualifiedAccess>]
module AgentTypeDeclarationGroupProposal =
    let private state (AgentTypeDeclarationGroupProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

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

    /// Validates declared type references against the snapshot's types plus `groupReferences`
    /// (the IDs a type group is declaring together).
    let private typeRefValidator (snapshot: Snapshot) (groupReferences: Set<TypeId>) =
        StructuralTransactionInternals.validateDeclaredTypeRef
            "TypeDeclaration"
            (snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList |> Set.union groupReferences)
            (snapshot.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList)

    let private validateFields validateTypeRef context (fields: AgentTypeFieldIntent list) =
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
                match validateTypeRef $"{context} field at index {index}" field.Type with
                | Error error -> Error error
                | Ok() -> loop (index + 1) rest
        loop 0 fields

    let private validateKind validateTypeRef (kind: AgentTypeDeclarationKindIntent) =
        match kind with
        | AgentTypeDeclarationKindIntent.Product fields ->
            validateFields validateTypeRef "Product" fields
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
                    match validateFields validateTypeRef $"Union variant at index {index}" variant.Fields with
                    | Error error -> Error error
                    | Ok() -> loop (index + 1) rest
            loop 0 variants

    let private validateDisplayName (snapshot: Snapshot) (displayName: string) =
        if String.IsNullOrWhiteSpace displayName then
            Error(
                diagnostic
                    "TypeDeclarationDisplayNameBlank"
                    "Type display name must not be blank."
            )
        elif snapshot.Types |> List.exists (fun item -> item.DisplayName = displayName) then
            Error(
                diagnostic
                    "TypeDeclarationDuplicateDisplayName"
                    $"A type named '{displayName}' already exists; type display names must be unique."
            )
        else
            Ok()

    let private validateBase (snapshot: Snapshot) expectedBaseSnapshotId =
        if expectedBaseSnapshotId <> snapshot.Id then
            Error(
                diagnostic
                    "TypeDeclarationExpectedBaseMismatch"
                    "The type-declaration request targets a different base snapshot."
            )
        else
            Ok()

    let private validatePreflight (snapshot: Snapshot) (request: AgentCreateTypeDeclarationRequest) =
        validateBase snapshot request.ExpectedBaseSnapshotId
        |> Result.bind (fun () -> validateDisplayName snapshot request.DisplayName)
        |> Result.bind (fun () -> validateKind (typeRefValidator snapshot Set.empty) request.Kind)

    /// A group member must have a finite value: otherwise it could never be constructed. Existing
    /// types, primitives, List/Option (empty) and function/capability/task values count as inhabited;
    /// a member is inhabited once every field of the Product, or of some Union variant, is.
    let private validateInhabited (request: AgentCreateTypeDeclarationGroupRequest) =
        let references = request.Declarations |> List.map (fun item -> item.Reference) |> Set.ofList

        let rec inhabited (known: Set<TypeId>) typeRef =
            match typeRef with
            | TypeRef.Named id -> not (Set.contains id references) || Set.contains id known
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) -> List.exists (inhabited known) arguments
            | _ -> true

        let memberInhabited known (item: AgentTypeDeclarationGroupMember) =
            let all (fields: AgentTypeFieldIntent list) = fields |> List.forall (fun field -> inhabited known field.Type)
            match item.Kind with
            | AgentTypeDeclarationKindIntent.Product fields -> all fields
            | AgentTypeDeclarationKindIntent.Union variants -> variants |> List.exists (fun variant -> all variant.Fields)

        let rec fixpoint known =
            let next =
                request.Declarations
                |> List.filter (memberInhabited known)
                |> List.map (fun item -> item.Reference)
                |> Set.ofList
            if next = known then known else fixpoint next

        let known = fixpoint Set.empty
        match request.Declarations |> List.filter (fun item -> not (Set.contains item.Reference known)) with
        | [] -> Ok()
        | uninhabited ->
            let names = uninhabited |> List.map (fun item -> $"'{item.DisplayName}'") |> String.concat ", "
            Error(
                diagnostic
                    "TypeDeclarationUninhabited"
                    $"Type {names} has no finite value: its Product fields, or some field of each of its Union variants, need a type of this group that has none. Break the cycle with a List, an Option or a Union variant that does not."
            )

    let private validateGroupPreflight (snapshot: Snapshot) (request: AgentCreateTypeDeclarationGroupRequest) =
        let existingIds = snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList
        let references = request.Declarations |> List.map (fun item -> item.Reference)
        let referenceSet = Set.ofList references
        let validateTypeRef = typeRefValidator snapshot referenceSet

        let rec members seen (remaining: AgentTypeDeclarationGroupMember list) =
            match remaining with
            | [] -> Ok()
            | item :: rest ->
                match validateDisplayName snapshot item.DisplayName with
                | Error error -> Error error
                | Ok() when Set.contains item.DisplayName seen ->
                    Error(
                        diagnostic
                            "TypeDeclarationDuplicateDisplayName"
                            $"The type group declares '{item.DisplayName}' more than once; type display names must be unique."
                    )
                | Ok() ->
                    match validateKind validateTypeRef item.Kind with
                    | Error error -> Error { error with Message = $"Type '{item.DisplayName}': {error.Message}" }
                    | Ok() -> members (Set.add item.DisplayName seen) rest

        match validateBase snapshot request.ExpectedBaseSnapshotId with
        | Error error -> Error error
        | Ok() when List.isEmpty request.Declarations ->
            Error(diagnostic "TypeDeclarationGroupEmpty" "A type group must declare at least one type.")
        | Ok() when
            Set.count referenceSet <> List.length references
            || not (Set.isEmpty (Set.intersect referenceSet existingIds))
            ->
            Error(
                diagnostic
                    "TypeDeclarationGroupReferenceInvalid"
                    "Type group references must be distinct request-local IDs that name no existing type."
            )
        | Ok() ->
            members Set.empty request.Declarations
            |> Result.bind (fun () -> validateInhabited request)

    let private materializeField (rewrite: TypeRef -> TypeRef) (intent: AgentTypeFieldIntent): SemanticField =
        { Id = StableIdAllocator.newFieldId ()
          DisplayName = intent.DisplayName
          Type = rewrite intent.Type }

    let private materializeKind rewrite (kind: AgentTypeDeclarationKindIntent) =
        match kind with
        | AgentTypeDeclarationKindIntent.Product fields ->
            SemanticTypeKind.Product(fields |> List.map (materializeField rewrite))
        | AgentTypeDeclarationKindIntent.Union variants ->
            SemanticTypeKind.Union(
                variants
                |> List.map (fun (variant: AgentTypeVariantIntent) ->
                    { Id = StableIdAllocator.newVariantId ()
                      DisplayName = variant.DisplayName
                      Fields = variant.Fields |> List.map (materializeField rewrite) })
            )

    let private materialize (request: AgentCreateTypeDeclarationRequest): SemanticType =
        { Id = StableIdAllocator.newTypeId ()
          DisplayName = request.DisplayName
          Kind = materializeKind id request.Kind }

    /// Allocates every member's TypeId first, then rewrites the request-local references to them.
    let private materializeGroup (request: AgentCreateTypeDeclarationGroupRequest): SemanticType list =
        let allocated =
            request.Declarations
            |> List.map (fun item -> item.Reference, StableIdAllocator.newTypeId ())
            |> Map.ofList

        let rec rewrite typeRef =
            match typeRef with
            | TypeRef.Named reference when Map.containsKey reference allocated -> TypeRef.Named allocated[reference]
            | TypeRef.Function(arguments, result) -> TypeRef.Function(List.map rewrite arguments, rewrite result)
            | TypeRef.BuiltinGeneric(kind, arguments) -> TypeRef.BuiltinGeneric(kind, List.map rewrite arguments)
            | other -> other

        request.Declarations
        |> List.map (fun item ->
            { Id = allocated[item.Reference]
              DisplayName = item.DisplayName
              Kind = materializeKind rewrite item.Kind })

    /// Validates the base, runs the declaration-specific preflight, appends the created types and
    /// reviews the candidate snapshot.
    let private simulateCandidate
        (workspace: AgentWorkspace)
        (preflight: Snapshot -> Result<unit, TransactionDiagnostic>)
        (create: unit -> SemanticType list)
        =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: rest ->
            Error(
                AgentTypeDeclarationWorkflowError.SimulationRejected(
                    validationDiagnostics (issue :: rest)
                )
            )
        | [] ->
            match preflight baseSnapshot with
            | Error error ->
                Error(AgentTypeDeclarationWorkflowError.SimulationRejected [ error ])
            | Ok() ->
                let createdTypes = create ()
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = StableIdAllocator.newSnapshotId ()
                        ParentId = Some baseSnapshot.Id
                        Types = baseSnapshot.Types @ createdTypes }

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
                        | Ok receipt -> Ok(transactionId, proposedSnapshot, readiness, receipt, createdTypes)

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentCreateTypeDeclarationRequest)
        : Result<AgentTypeDeclarationProposal, AgentTypeDeclarationWorkflowError> =
        simulateCandidate workspace (fun snapshot -> validatePreflight snapshot request) (fun () -> [ materialize request ])
        |> Result.map (fun (transactionId, proposedSnapshot, readiness, receipt, createdTypes) ->
            AgentTypeDeclarationProposal
                { TransactionId = transactionId
                  ProposedSnapshot = proposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = receipt
                      Readiness = readiness
                      CreatedType = List.exactlyOne createdTypes } })

    /// Creates a group of types that may reference themselves and each other through their
    /// request-local references.
    let simulateGroup
        (workspace: AgentWorkspace)
        (request: AgentCreateTypeDeclarationGroupRequest)
        : Result<AgentTypeDeclarationGroupProposal, AgentTypeDeclarationWorkflowError> =
        simulateCandidate workspace (fun snapshot -> validateGroupPreflight snapshot request) (fun () -> materializeGroup request)
        |> Result.map (fun (transactionId, proposedSnapshot, readiness, receipt, createdTypes) ->
            AgentTypeDeclarationGroupProposal
                { TransactionId = transactionId
                  ProposedSnapshot = proposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = receipt
                      Readiness = readiness
                      CreatedTypes = createdTypes } })

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

    let private publishGroupWithPolicy
        (workspace: AgentWorkspace)
        (AgentTypeDeclarationGroupProposal proposal)
        (policy: CommitPolicy)
        : AgentTypeDeclarationGroupCommitResult =
        let receipt = proposal.Review.Receipt

        if receipt.TransactionId <> proposal.TransactionId
           || receipt.ProposedSnapshotId <> proposal.ProposedSnapshot.Id then
            AgentTypeDeclarationGroupCommitResult.Rejected
                [ diagnostic
                    "PreviewReceiptRequestMismatch"
                    "The reviewed receipt does not identify this type-group proposal." ]
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
            | Error diagnostics -> AgentTypeDeclarationGroupCommitResult.Rejected diagnostics
            | Ok nextWorkspace ->
                AgentTypeDeclarationGroupCommitResult.Committed(
                    nextWorkspace,
                    { TransactionId = proposal.TransactionId
                      BaseSnapshotId = receipt.BaseSnapshotId
                      PublishedSnapshotId = proposal.ProposedSnapshot.Id
                      CreatedTypeIds = proposal.Review.CreatedTypes |> List.map (fun item -> item.Id)
                      Readiness = proposal.Review.Readiness
                      Receipt = receipt }
                )

    let commitGroup workspace proposal =
        publishGroupWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

    let commitGroupWithPolicy workspace proposal policy =
        publishGroupWithPolicy workspace proposal policy

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

