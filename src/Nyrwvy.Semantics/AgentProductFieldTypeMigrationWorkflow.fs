namespace Nyrwvy.Core

type AgentFunctionBodyRewrite =
    { FunctionId: FunctionId
      Replacement: ExpressionRecipe }

type AgentProductFieldTypeMigrationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      FieldId: FieldId
      NewFieldType: TypeRef
      /// One complete replacement body for every function that constructs the owning Product
      /// or reads the field. Recipes keep referring to the field by its existing FieldId.
      FunctionRewrites: AgentFunctionBodyRewrite list }

type AgentProductFieldTypeChange =
    { TypeId: TypeId
      FieldId: FieldId
      OldType: TypeRef
      NewType: TypeRef }

type AgentProductFieldTypeMigrationDiff =
    { FieldChange: AgentProductFieldTypeChange
      BodyRewrites: FunctionBodyReplacementDiff list }

type AgentProductFieldTypeMigrationReview =
    { RequestedChange: AgentProductFieldTypeMigrationRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: AgentProductFieldTypeMigrationDiff }

type AgentProductFieldTypeMigrationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: AgentProductFieldTypeMigrationDiff
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private ProductFieldTypeMigrationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentProductFieldTypeMigrationReview }

type AgentProductFieldTypeMigrationProposal =
    private
    | AgentProductFieldTypeMigrationProposal of ProductFieldTypeMigrationProposalState

[<RequireQualifiedAccess>]
module AgentProductFieldTypeMigrationProposal =
    let private state (AgentProductFieldTypeMigrationProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
type AgentProductFieldTypeMigrationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentProductFieldTypeMigrationPublishedTransaction

/// Changes the type of an existing Product field together with every construction and read of it,
/// as one reviewed publication. The type, field and function identities are kept.
[<RequireQualifiedAccess>]
module AgentProductFieldTypeMigrationWorkflow =
    open StructuralTransactionInternals

    let private reject code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    /// Functions that construct the owning Product or read the field, at any nesting depth.
    let private affectedFunctions (snapshot: Snapshot) (typeId: TypeId) (fieldId: FieldId) =
        snapshot.Functions
        |> List.filter (fun fn ->
            ValueGraph.collectNodes fn.Body
            |> List.exists (fun node ->
                match node.Kind with
                | SemanticNodeKind.ProductConstruct(constructed, _) -> constructed = typeId
                | SemanticNodeKind.FieldAccess(_, accessed) -> accessed = fieldId
                | _ -> false))

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentProductFieldTypeMigrationRequest)
        : Result<AgentProductFieldTypeMigrationProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        let owner =
            baseSnapshot.Types
            |> List.tryPick (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product fields ->
                    fields |> List.tryFind (fun field -> field.Id = request.FieldId) |> Option.map (fun field -> item, fields, field)
                | SemanticTypeKind.Union _ -> None)
        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> reject "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] when request.ExpectedBaseSnapshotId <> baseSnapshot.Id ->
            reject "FieldTypeMigrationExpectedBaseMismatch" "The migration request targets a different base snapshot."
        | [] ->
            match owner with
            | None -> reject "FieldTypeMigrationFieldNotFound" "The field is not a field of an existing Product."
            | Some(_, _, field) when field.Type = request.NewFieldType ->
                reject "FieldTypeMigrationTypeUnchanged" "The requested field type is already the current type."
            | Some(ownerType, fields, field) ->
                let affected = affectedFunctions baseSnapshot ownerType.Id field.Id
                let expected = affected |> List.map (fun fn -> fn.Id) |> Set.ofList
                let requested = request.FunctionRewrites |> List.map (fun rewrite -> rewrite.FunctionId)
                let render values = values |> Set.toList |> List.map StableId.value |> String.concat ", "
                if List.distinct requested <> requested then
                    reject "FieldTypeMigrationDuplicateRewrite" "Each affected function may have only one replacement body."
                elif Set.ofList requested <> expected then
                    reject
                        "FieldTypeMigrationRewriteMismatch"
                        $"Rewrites must cover exactly the functions that construct the Product or read the field. Missing: [{render (Set.difference expected (Set.ofList requested))}]. Unrelated: [{render (Set.difference (Set.ofList requested) expected)}]."
                else
                    match affected |> List.tryPick (fun fn -> bodyObligationError fn baseSnapshot |> Option.map (fun error -> fn, error)) with
                    | Some(fn, error) ->
                        reject "FieldTypeMigrationBodyReferencedByObligation" $"Function '{StableId.value fn.Id}': {error.Code}: {error.Message}"
                    | None ->
                        let updatedType =
                            { ownerType with
                                Kind =
                                    SemanticTypeKind.Product(
                                        fields |> List.map (fun item -> if item.Id = field.Id then { item with Type = request.NewFieldType } else item)) }
                        // Every affected body is bound against the retyped declaration before the
                        // candidate as a whole is validated; untouched functions stay unchanged.
                        let candidate =
                            { baseSnapshot with
                                Types = baseSnapshot.Types |> List.map (fun item -> if item.Id = ownerType.Id then updatedType else item) }
                        let rewrites = request.FunctionRewrites |> List.map (fun rewrite -> rewrite.FunctionId, rewrite.Replacement) |> Map.ofList
                        let materialized =
                            affected
                            |> List.map (fun fn ->
                                AgentExpressionStructuralWorkflow.materializeReplacementBody candidate fn (Map.find fn.Id rewrites)
                                |> Result.map (fun body -> fn.Id, body)
                                |> Result.mapError (fun error -> fn, error))
                        match materialized |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                        | Some(fn, error) ->
                            reject "FieldTypeMigrationRewriteInvalid" $"Function '{StableId.value fn.Id}' rewrite is invalid: {error.Code}: {error.Message}"
                        | None ->
                            let bodies = materialized |> List.choose (function Ok item -> Some item | Error _ -> None) |> Map.ofList
                            let proposedSnapshot =
                                { candidate with
                                    Id = StableIdAllocator.newSnapshotId ()
                                    ParentId = Some baseSnapshot.Id
                                    Functions =
                                        candidate.Functions
                                        |> List.map (fun fn ->
                                            match Map.tryFind fn.Id bodies with
                                            | Some body -> { fn with Body = body }
                                            | None -> fn) }
                            match SnapshotValidator.validate proposedSnapshot with
                            | issue :: _ -> reject "FieldTypeMigrationInvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                            | [] ->
                                match classifyReadiness proposedSnapshot with
                                | Error diagnostics -> Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                                | Ok readiness ->
                                    let transactionId = StableIdAllocator.newTransactionId ()
                                    match createReceipt transactionId baseSnapshot proposedSnapshot with
                                    | Error error -> Error error
                                    | Ok receipt ->
                                        let proposed functionId = proposedSnapshot.Functions |> List.find (fun fn -> fn.Id = functionId)
                                        Ok(
                                            AgentProductFieldTypeMigrationProposal
                                                { TransactionId = transactionId
                                                  ProposedSnapshot = proposedSnapshot
                                                  Review =
                                                    { RequestedChange = request
                                                      Receipt = receipt
                                                      Readiness = readiness
                                                      ResultingChange =
                                                        { FieldChange =
                                                            { TypeId = ownerType.Id
                                                              FieldId = field.Id
                                                              OldType = field.Type
                                                              NewType = request.NewFieldType }
                                                          BodyRewrites =
                                                            affected
                                                            |> List.map (fun fn ->
                                                                SignatureMigrationCore.bodyDiff baseSnapshot proposedSnapshot fn (proposed fn.Id)) } } }
                                        )

    let commitWithPolicy workspace (AgentProductFieldTypeMigrationProposal proposal) policy =
        match publishCandidateWithPolicy workspace proposal.ProposedSnapshot proposal.Review.Readiness proposal.Review.Receipt policy with
        | Error diagnostics -> AgentProductFieldTypeMigrationCommitResult.Rejected diagnostics
        | Ok nextWorkspace ->
            AgentProductFieldTypeMigrationCommitResult.Committed(
                nextWorkspace,
                { TransactionId = proposal.TransactionId
                  BaseSnapshotId = proposal.Review.Receipt.BaseSnapshotId
                  PublishedSnapshotId = proposal.ProposedSnapshot.Id
                  Diff = proposal.Review.ResultingChange
                  Readiness = proposal.Review.Readiness
                  Receipt = proposal.Review.Receipt })

    let commit workspace proposal = commitWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations
