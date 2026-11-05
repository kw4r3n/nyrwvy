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
      FunctionEffects: FunctionEffectSummary list
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
    /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot
    /// Functions of the proposed snapshot, for rendering rewritten bodies in a full review.
    let proposedFunctions proposal = (state proposal).ProposedSnapshot.Functions

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

    /// Functions that construct the owning Product, read the field or update it, at any nesting depth.
    /// Exact construction/read/update owners used by both migration validation and repair advice.
    let affectedFunctions (functions: SemanticFunction list) (typeId: TypeId) (fieldId: FieldId) =
        functions
        |> List.filter (fun fn ->
            ValueGraph.collectNodes fn.Body
            |> List.exists (fun node ->
                match node.Kind with
                | SemanticNodeKind.ProductConstruct(constructed, _, _) -> constructed = typeId
                | SemanticNodeKind.FieldAccess(_, accessed) -> accessed = fieldId
                | SemanticNodeKind.ProductUpdate(_, _, bindings) ->
                    bindings |> List.exists (fun binding -> binding.TargetFieldId = fieldId)
                | _ -> false))

    type private Prepared =
        { OwnerType: SemanticType
          UpdatedType: SemanticType
          Field: SemanticField
          Affected: SemanticFunction list
          Bodies: Map<FunctionId, ValueGraph> }

    let private prepare (request: AgentProductFieldTypeMigrationRequest) (baseSnapshot: Snapshot) =
        let owner =
            baseSnapshot.Types
            |> List.tryPick (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product fields ->
                    fields |> List.tryFind (fun field -> field.Id = request.FieldId) |> Option.map (fun field -> item, fields, field)
                | SemanticTypeKind.Union _ -> None)
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            reject "FieldTypeMigrationExpectedBaseMismatch" "The migration request targets a different base snapshot."
        else
            // The new type is checked like a declared field type, so an unknown or ill-formed type
            // is reported before any rewrite is bound against it.
            // A field of a generic Product may use the Product's own type parameters.
            let newFieldTypeCheck =
                validateDeclaredTypeRefWith
                    "FieldTypeMigration"
                    (baseSnapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList)
                    (baseSnapshot.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList)
                    (owner |> Option.map (fun (ownerType, _, _) -> ownerType.TypeParameters |> List.map fst |> Set.ofList) |> Option.defaultValue Set.empty)
                    "The new field type"
                    request.NewFieldType
            match owner, newFieldTypeCheck with
            | None, _ -> reject "FieldTypeMigrationFieldNotFound" "The field is not a field of an existing Product."
            | Some(_, _, field), _ when field.Type = request.NewFieldType ->
                reject "FieldTypeMigrationTypeUnchanged" "The requested field type is already the current type."
            | Some _, Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Some(ownerType, fields, field), Ok() ->
                let affected = affectedFunctions baseSnapshot.Functions ownerType.Id field.Id
                let expected = affected |> List.map (fun fn -> fn.Id) |> Set.ofList
                let requested = request.FunctionRewrites |> List.map (fun rewrite -> rewrite.FunctionId)
                let render values =
                    let ids = values |> Set.toList
                    let shown = ids |> List.truncate 5 |> List.map (fun id ->
                        match baseSnapshot.Functions |> List.tryFind (fun fn -> fn.Id = id) with
                        | None -> StableId.value id
                        | Some fn ->
                            let mutable shown = min 128 fn.DisplayName.Length
                            if shown > 0 && shown < fn.DisplayName.Length && System.Char.IsHighSurrogate fn.DisplayName[shown - 1] then shown <- shown - 1
                            let name = fn.DisplayName.Substring(0, shown) + (if shown < fn.DisplayName.Length then "…" else "")
                            $"'{name}' ({StableId.value id})") |> String.concat ", "
                    if ids.Length > 5 then shown + $"; +{ids.Length - 5} more (inspect declarations)" else shown
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
                        let candidateIndex = ExpressionRecipeSupport.buildSnapshotIndex candidate
                        let materialized =
                            affected
                            |> List.map (fun fn ->
                                AgentExpressionStructuralWorkflow.materializeReplacementBody candidate candidateIndex fn (Map.find fn.Id rewrites)
                                |> Result.map (fun body -> fn.Id, body)
                                |> Result.mapError (fun error -> fn, error))
                        match materialized |> List.tryPick (function Error failure -> Some failure | Ok _ -> None) with
                        | Some(fn, error) ->
                            reject "FieldTypeMigrationRewriteInvalid" $"Function '{StableId.value fn.Id}' rewrite is invalid: {error.Code}: {error.Message}"
                        | None ->
                            Ok
                                { OwnerType = ownerType
                                  UpdatedType = updatedType
                                  Field = field
                                  Affected = affected
                                  Bodies = materialized |> List.choose (function Ok item -> Some item | Error _ -> None) |> Map.ofList }

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentProductFieldTypeMigrationRequest)
        : Result<AgentProductFieldTypeMigrationProposal, AgentStructuralWorkflowError> =
        simulateStructural
            workspace
            (prepare request)
            (fun prepared snapshot ->
                { snapshot with
                    Types = snapshot.Types |> List.map (fun item -> if item.Id = prepared.OwnerType.Id then prepared.UpdatedType else item)
                    Functions =
                        snapshot.Functions
                        |> List.map (fun fn ->
                            match Map.tryFind fn.Id prepared.Bodies with
                            | Some body -> { fn with Body = body }
                            | None -> fn) })
        |> Result.map (fun candidate ->
            let prepared = candidate.Prepared
            let proposedSnapshot = candidate.ProposedSnapshot
            let proposed functionId = proposedSnapshot.Functions |> List.find (fun fn -> fn.Id = functionId)
            AgentProductFieldTypeMigrationProposal
                { TransactionId = candidate.TransactionId
                  ProposedSnapshot = proposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = candidate.Receipt
                      Readiness = candidate.Readiness
                      FunctionEffects = EffectRequirements.summarize proposedSnapshot (prepared.Affected |> List.map (fun fn -> fn.Id) |> Set.ofList)
                      ResultingChange =
                        { FieldChange =
                            { TypeId = prepared.OwnerType.Id
                              FieldId = prepared.Field.Id
                              OldType = prepared.Field.Type
                              NewType = request.NewFieldType }
                          BodyRewrites =
                            prepared.Affected
                            |> List.map (fun fn ->
                                SignatureMigrationCore.bodyDiff candidate.BaseSnapshot proposedSnapshot fn (proposed fn.Id)) } } })

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
