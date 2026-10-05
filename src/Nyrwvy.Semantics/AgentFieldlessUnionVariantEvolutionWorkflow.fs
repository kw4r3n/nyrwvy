namespace Nyrwvy.Core

open System

type AgentAddFieldlessUnionVariantLikeRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      TargetTypeId: TypeId
      SourceVariantId: VariantId
      NewVariantDisplayName: string }

type AgentFieldlessUnionVariantEvolutionReview =
    { RequestedChange: AgentAddFieldlessUnionVariantLikeRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      UpdatedType: SemanticType
      AddedVariant: SemanticVariant
      RepairedFunctionIds: FunctionId list
      RepairedMatchCount: int }

type AgentFieldlessUnionVariantEvolutionPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      UpdatedTypeId: TypeId
      AddedVariantId: VariantId
      RepairedFunctionIds: FunctionId list
      RepairedMatchCount: int
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private AgentFieldlessUnionVariantEvolutionProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentFieldlessUnionVariantEvolutionReview }

type AgentFieldlessUnionVariantEvolutionProposal =
    private
    | AgentFieldlessUnionVariantEvolutionProposal of AgentFieldlessUnionVariantEvolutionProposalState

[<RequireQualifiedAccess>]
type AgentFieldlessUnionVariantEvolutionCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentFieldlessUnionVariantEvolutionPublishedTransaction

[<RequireQualifiedAccess>]
module AgentFieldlessUnionVariantEvolutionProposal =
    let private state (AgentFieldlessUnionVariantEvolutionProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id
    /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot
    /// Functions of the proposed snapshot, for rendering rewritten bodies in a full review.
    let proposedFunctions proposal = (state proposal).ProposedSnapshot.Functions

[<RequireQualifiedAccess>]
module AgentFieldlessUnionVariantEvolutionWorkflow =
    open StructuralTransactionInternals

    /// Clones a graph with fresh node and pattern-binding IDs. `outerBindings` are bindings owned
    /// by the enclosing arm (its field bindings); references to them are remapped as well, and the
    /// returned map gives their fresh IDs.
    let private cloneGraph (outerBindings: PatternBindingId list) (graph: ValueGraph): ValueGraph * Map<PatternBindingId, PatternBindingId> =
        let nodeIds =
            ValueGraph.collectNodes graph
            |> List.map (fun node -> node.Id)
            |> List.distinct

        let patternIds =
            outerBindings @ (SemanticFacts.collectPatternBindings graph |> List.map (fun binding -> binding.Id))
            |> List.distinct

        let nodeIdMap =
            nodeIds
            |> List.map (fun oldId -> oldId, StableIdAllocator.newGraphNodeId ())
            |> Map.ofList

        let patternIdMap =
            patternIds
            |> List.map (fun oldId -> oldId, StableIdAllocator.newPatternBindingId ())
            |> Map.ofList

        let mapNodeId id =
            Map.tryFind id nodeIdMap |> Option.defaultValue id

        let mapPatternId id =
            Map.tryFind id patternIdMap |> Option.defaultValue id

        let mapPatternBinding (binding: PatternBinding) =
            { binding with Id = mapPatternId binding.Id }

        let rec mapGraph (current: ValueGraph): ValueGraph =
            { Nodes = current.Nodes |> List.map mapNode
              ResultNodeId = mapNodeId current.ResultNodeId }

        and mapNode (node: SemanticNode): SemanticNode =
            let mapped =
                node
                |> SemanticNodeShape.mapValueReferences mapNodeId
                |> SemanticNodeShape.mapNested mapPatternBinding mapGraph

            let kind =
                match mapped.Kind with
                | SemanticNodeKind.PatternReference bindingId ->
                    SemanticNodeKind.PatternReference(mapPatternId bindingId)
                | other -> other

            { Id = mapNodeId node.Id; Kind = kind }

        mapGraph graph, patternIdMap

    let private repairMatches
        (sourceVariantId: VariantId)
        (addedVariantId: VariantId)
        (fieldMap: Map<FieldId, FieldId>)
        (extraFields: SemanticField list)
        (graph: ValueGraph)
        : ValueGraph * int =
        let rec repairGraph (current: ValueGraph): ValueGraph * int =
            let mutable repairedMatchCount = 0

            let repairNested nested =
                let repaired, count = repairGraph nested
                repairedMatchCount <- repairedMatchCount + count
                repaired

            let repairNode (node: SemanticNode) =
                let repaired = SemanticNodeShape.mapNestedGraphs repairNested node
                match repaired.Kind with
                | SemanticNodeKind.MatchUnion(inputNodeId, repairedArms) ->
                    match
                        repairedArms
                        |> List.tryFind (fun arm -> List.contains sourceVariantId arm.VariantIds)
                    with
                    | None -> repaired
                    | Some sourceArm when sourceArm.VariantIds.Length > 1 ->
                        // An arm naming several variants binds no fields, so the added variant joins it
                        // and shares its body instead of receiving a copy.
                        repairedMatchCount <- repairedMatchCount + 1
                        let joinedArms =
                            repairedArms
                            |> List.map (fun arm ->
                                if List.contains sourceVariantId arm.VariantIds then { arm with VariantIds = arm.VariantIds @ [ addedVariantId ] }
                                else arm)
                        { repaired with Kind = SemanticNodeKind.MatchUnion(inputNodeId, joinedArms) }
                    | Some sourceArm ->
                        repairedMatchCount <- repairedMatchCount + 1
                        let body, bindingMap =
                            cloneGraph (sourceArm.FieldBindings |> List.map (fun item -> item.Binding.Id)) sourceArm.Body
                        let addedArm: UnionMatchArm =
                            { VariantIds = [ addedVariantId ]
                              FieldBindings =
                                (sourceArm.FieldBindings
                                 |> List.map (fun item ->
                                     { SourceFieldId = Map.find item.SourceFieldId fieldMap
                                       Binding = { item.Binding with Id = Map.find item.Binding.Id bindingMap } }))
                                // Additional fields are bound but unused until the arm body is refined.
                                @ (extraFields
                                   |> List.map (fun field ->
                                       { SourceFieldId = field.Id
                                         Binding =
                                           { Id = StableIdAllocator.newPatternBindingId ()
                                             DisplayName = Some field.DisplayName
                                             Type = field.Type } }))
                              Body = body }
                        { repaired with Kind = SemanticNodeKind.MatchUnion(inputNodeId, repairedArms @ [ addedArm ]) }
                | _ -> repaired

            let nodes = current.Nodes |> List.map repairNode

            { current with Nodes = nodes }, repairedMatchCount

        repairGraph graph

    let private simulateLike
        (workspace: AgentWorkspace)
        (request: AgentAddFieldlessUnionVariantLikeRequest)
        (copySourceFields: bool)
        (additionalFields: AgentTypeFieldIntent list)
        : Result<AgentFieldlessUnionVariantEvolutionProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: rest ->
            Error(
                AgentStructuralWorkflowError.SimulationRejected(
                    validationDiagnostics (issue :: rest)
                )
            )
        | [] when request.ExpectedBaseSnapshotId <> baseSnapshot.Id ->
            rejected
                "UnionVariantEvolutionExpectedBaseMismatch"
                "The Union-variant evolution request targets a different base snapshot."
        | [] when String.IsNullOrWhiteSpace request.NewVariantDisplayName ->
            rejected
                "UnionVariantEvolutionDisplayNameBlank"
                "The new Union variant display name must not be blank."
        | [] ->
            match
                baseSnapshot.Types
                |> List.tryFind (fun item -> item.Id = request.TargetTypeId)
            with
            | None ->
                rejected
                    "UnionVariantEvolutionTypeNotFound"
                    "The target type does not exist."
            | Some targetType ->
                match targetType.Kind with
                | SemanticTypeKind.Product _ ->
                    rejected
                        "UnionVariantEvolutionTargetNotUnion"
                        "The target type must be a Union."
                | SemanticTypeKind.Union variants ->
                    let invalidFieldNames (sourceVariant: SemanticVariant) =
                        let names =
                            (sourceVariant.Fields |> List.map (fun field -> field.DisplayName))
                            @ (additionalFields |> List.map (fun field -> field.DisplayName))
                        List.distinct names <> names || names |> List.exists String.IsNullOrWhiteSpace
                    match
                        variants
                        |> List.tryFind (fun variant ->
                            variant.Id = request.SourceVariantId)
                    with
                    | None ->
                        rejected
                            "UnionVariantEvolutionSourceVariantNotFound"
                            "The source variant does not belong to the target Union."
                    | Some sourceVariant when not copySourceFields && not (List.isEmpty sourceVariant.Fields) ->
                        rejected
                            "UnionVariantEvolutionSourceVariantHasFields"
                            "This bounded evolution operation supports only fieldless source variants."
                    | Some sourceVariant when invalidFieldNames sourceVariant ->
                        rejected
                            "UnionVariantEvolutionFieldDisplayNameInvalid"
                            "New variant field display names must be non-blank and distinct, including copied source fields."
                    | Some sourceVariant when variants |> List.exists (fun variant -> variant.DisplayName = request.NewVariantDisplayName) ->
                        rejected
                            "UnionVariantEvolutionDuplicateVariantDisplayName"
                            "The Union already has a variant with this display name."
                    | Some sourceVariant ->
                        // Copied source fields keep names and types under fresh identities.
                        let copied =
                            sourceVariant.Fields |> List.map (fun (field: SemanticField) -> field, { field with Id = StableIdAllocator.newFieldId () })
                        let fieldMap = copied |> List.map (fun (source, added) -> source.Id, added.Id) |> Map.ofList
                        let added =
                            additionalFields |> List.map (fun field ->
                                ({ Id = StableIdAllocator.newFieldId (); DisplayName = field.DisplayName; Type = field.Type }: SemanticField))
                        let addedVariant: SemanticVariant =
                            { Id = StableIdAllocator.newVariantId ()
                              DisplayName = request.NewVariantDisplayName
                              Fields = (copied |> List.map snd) @ added }

                        let updatedType =
                            { targetType with
                                Kind =
                                    SemanticTypeKind.Union(
                                        variants @ [ addedVariant ]
                                    ) }

                        let repaired =
                            baseSnapshot.Functions
                            |> List.map (fun functionEntity ->
                                let body, repairedMatchCount =
                                    repairMatches
                                        request.SourceVariantId
                                        addedVariant.Id
                                        fieldMap
                                        added
                                        functionEntity.Body
                                functionEntity, body, repairedMatchCount)

                        let repairedFunctionIds =
                            repaired
                            |> List.choose (fun (functionEntity, _, count) ->
                                if count > 0 then Some functionEntity.Id else None)

                        let repairedMatchCount =
                            repaired
                            |> List.sumBy (fun (_, _, count) -> count)

                        let proposedFunctions =
                            repaired
                            |> List.map (fun (functionEntity, body, count) ->
                                if count > 0 then
                                    { functionEntity with Body = body }
                                else
                                    functionEntity)

                        let transactionId = StableIdAllocator.newTransactionId ()
                        let proposedSnapshot =
                            { baseSnapshot with
                                Id = StableIdAllocator.newSnapshotId ()
                                ParentId = Some baseSnapshot.Id
                                Types =
                                    baseSnapshot.Types
                                    |> List.map (fun item ->
                                        if item.Id = targetType.Id then updatedType else item)
                                Functions = proposedFunctions }

                        match SnapshotValidator.validate proposedSnapshot with
                        | issue :: rest ->
                            Error(
                                AgentStructuralWorkflowError.SimulationRejected(
                                    validationDiagnostics (issue :: rest)
                                )
                            )
                        | [] ->
                            match classifyReadiness proposedSnapshot with
                            | Error diagnostics ->
                                Error(
                                    AgentStructuralWorkflowError.SimulationRejected diagnostics
                                )
                            | Ok readiness ->
                                match
                                    createReceipt
                                        transactionId
                                        baseSnapshot
                                        proposedSnapshot
                                with
                                | Error error -> Error error
                                | Ok receipt ->
                                    let review =
                                        { RequestedChange = request
                                          Receipt = receipt
                                          Readiness = readiness
                                          UpdatedType = updatedType
                                          AddedVariant = addedVariant
                                          RepairedFunctionIds = repairedFunctionIds
                                          RepairedMatchCount = repairedMatchCount }

                                    Ok(
                                        AgentFieldlessUnionVariantEvolutionProposal
                                            { TransactionId = transactionId
                                              ProposedSnapshot = proposedSnapshot
                                              Review = review }
                                    )

    /// Adds a fieldless variant whose match arms copy those of a fieldless source variant.
    let simulate workspace (request: AgentAddFieldlessUnionVariantLikeRequest) =
        simulateLike workspace request false []

    /// Adds a variant that copies the source variant's fields (fresh identities, same names and
    /// types) plus `additionalFields`. Every match arm for the source is cloned for the new variant
    /// with its field bindings remapped, so no intermediate program is non-exhaustive; arm bodies
    /// can then be refined with targeted replacement.
    let simulateWithFields workspace (request: AgentAddFieldlessUnionVariantLikeRequest) (additionalFields: AgentTypeFieldIntent list) =
        simulateLike workspace request true additionalFields

    let private publicationMessages =
        { StaleBase = "The workspace advanced beyond the reviewed Union-variant evolution proposal."
          BaseFingerprintFailure = "The base snapshot fingerprint could not be recomputed."
          ProposedFingerprintFailure = "The proposed snapshot fingerprint could not be recomputed."
          ResultMismatch = "The proposed Union-variant evolution differs from the reviewed result." }

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentFieldlessUnionVariantEvolutionProposal proposal)
        (policy: CommitPolicy)
        : AgentFieldlessUnionVariantEvolutionCommitResult =
        let receipt = proposal.Review.Receipt

        if receipt.TransactionId <> proposal.TransactionId
           || receipt.ProposedSnapshotId <> proposal.ProposedSnapshot.Id then
            AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                [ diagnostic
                      "PreviewReceiptRequestMismatch"
                      "The reviewed receipt does not identify this Union-variant evolution proposal." ]
        else
            match
                publishCandidateWithMessages
                    publicationMessages
                    workspace
                    proposal.ProposedSnapshot
                    proposal.Review.Readiness
                    receipt
                    policy
            with
            | Error diagnostics -> AgentFieldlessUnionVariantEvolutionCommitResult.Rejected diagnostics
            | Ok nextWorkspace ->
                AgentFieldlessUnionVariantEvolutionCommitResult.Committed(
                    nextWorkspace,
                    { TransactionId = proposal.TransactionId
                      BaseSnapshotId = receipt.BaseSnapshotId
                      PublishedSnapshotId = proposal.ProposedSnapshot.Id
                      UpdatedTypeId = proposal.Review.UpdatedType.Id
                      AddedVariantId = proposal.Review.AddedVariant.Id
                      RepairedFunctionIds = proposal.Review.RepairedFunctionIds
                      RepairedMatchCount = proposal.Review.RepairedMatchCount
                      Readiness = proposal.Review.Readiness
                      Receipt = receipt }
                )

    let commit workspace proposal =
        publishWithPolicy
            workspace
            proposal
            CommitPolicy.RejectBlockingObligations

    let commitWithPolicy workspace proposal policy =
        publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentFieldlessUnionVariantEvolutionProposal proposal)
        : AgentWorkspace * TransactionRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
