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

[<RequireQualifiedAccess>]
module AgentFieldlessUnionVariantEvolutionWorkflow =
    open StructuralTransactionInternals

    let private validationDiagnostics (issues: ValidationIssue list) =
        issues
        |> List.map (fun issue ->
            diagnostic ("Validation." + issue.Code) issue.Message)

    let private cloneGraph (graph: ValueGraph): ValueGraph =
        let nodeIds =
            ValueGraph.collectNodes graph
            |> List.map (fun node -> node.Id)
            |> List.distinct

        let patternIds =
            SemanticFacts.collectPatternBindings graph
            |> List.map (fun binding -> binding.Id)
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
            { Id = mapNodeId node.Id
              Kind = mapKind node.Kind }

        and mapUnionArm (arm: UnionMatchArm): UnionMatchArm =
            { VariantId = arm.VariantId
              FieldBindings =
                arm.FieldBindings
                |> List.map (fun fieldBinding ->
                    { fieldBinding with
                        Binding = mapPatternBinding fieldBinding.Binding })
              Body = mapGraph arm.Body }

        and mapValueArm (arm: ValueMatchArm): ValueMatchArm =
            { ValueBinding = mapPatternBinding arm.ValueBinding
              Body = mapGraph arm.Body }

        and mapErrorArm (arm: ErrorMatchArm): ErrorMatchArm =
            { ErrorBinding = mapPatternBinding arm.ErrorBinding
              Body = mapGraph arm.Body }

        and mapKind kind =
            match kind with
            | SemanticNodeKind.ParameterReference parameterId ->
                SemanticNodeKind.ParameterReference parameterId
            | SemanticNodeKind.PatternReference bindingId ->
                SemanticNodeKind.PatternReference(mapPatternId bindingId)
            | SemanticNodeKind.StringLiteral value ->
                SemanticNodeKind.StringLiteral value
            | SemanticNodeKind.BoolLiteral value ->
                SemanticNodeKind.BoolLiteral value
            | SemanticNodeKind.UnitLiteral ->
                SemanticNodeKind.UnitLiteral
            | SemanticNodeKind.Int64Literal value ->
                SemanticNodeKind.Int64Literal value
            | SemanticNodeKind.Float64Literal value ->
                SemanticNodeKind.Float64Literal value
            | SemanticNodeKind.StringIsEmpty valueNodeId ->
                SemanticNodeKind.StringIsEmpty(mapNodeId valueNodeId)
            | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                SemanticNodeKind.StringContains(
                    mapNodeId valueNodeId,
                    mapNodeId substringNodeId
                )
            | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64WrappingAdd(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64WrappingSubtract(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64WrappingMultiply(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64Equal(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64LessThan(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64LessThanOrEqual(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64GreaterThan(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64GreaterThanOrEqual(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Add(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Subtract(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Multiply(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Divide(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Equal(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64LessThan(
                    mapNodeId leftNodeId,
                    mapNodeId rightNodeId
                )
            | SemanticNodeKind.Float64IsNaN valueNodeId ->
                SemanticNodeKind.Float64IsNaN(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64IsFinite valueNodeId ->
                SemanticNodeKind.Float64IsFinite(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64IsInfinite valueNodeId ->
                SemanticNodeKind.Float64IsInfinite(mapNodeId valueNodeId)
            | SemanticNodeKind.ListEmpty itemType ->
                SemanticNodeKind.ListEmpty itemType
            | SemanticNodeKind.ListConstruct(itemType, itemNodeIds) ->
                SemanticNodeKind.ListConstruct(
                    itemType,
                    itemNodeIds |> List.map mapNodeId
                )
            | SemanticNodeKind.ListLength listNodeId ->
                SemanticNodeKind.ListLength(mapNodeId listNodeId)
            | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
                SemanticNodeKind.ListAppendItem(
                    mapNodeId listNodeId,
                    mapNodeId itemNodeId
                )
            | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
                SemanticNodeKind.ListConcat(
                    mapNodeId leftListNodeId,
                    mapNodeId rightListNodeId
                )
            | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId) ->
                SemanticNodeKind.ListMap(
                    mapNodeId sourceListNodeId,
                    mapperFunctionId
                )
            | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId) ->
                SemanticNodeKind.ListFilter(
                    mapNodeId sourceListNodeId,
                    predicateFunctionId
                )
            | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId) ->
                SemanticNodeKind.ListAny(
                    mapNodeId sourceListNodeId,
                    predicateFunctionId
                )
            | SemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId) ->
                SemanticNodeKind.ListAll(
                    mapNodeId sourceListNodeId,
                    predicateFunctionId
                )
            | SemanticNodeKind.DirectCall(functionId, arguments) ->
                SemanticNodeKind.DirectCall(
                    functionId,
                    arguments
                    |> List.map (fun binding ->
                        { binding with ValueNodeId = mapNodeId binding.ValueNodeId })
                )
            | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, operationId, arguments) ->
                SemanticNodeKind.CapabilityInvoke(
                    mapNodeId capabilityValueNodeId,
                    operationId,
                    arguments
                    |> List.map (fun binding ->
                        { binding with ValueNodeId = mapNodeId binding.ValueNodeId })
                )
            | SemanticNodeKind.ProductConstruct(typeId, fields) ->
                SemanticNodeKind.ProductConstruct(
                    typeId,
                    fields
                    |> List.map (fun binding ->
                        { binding with ValueNodeId = mapNodeId binding.ValueNodeId })
                )
            | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
                SemanticNodeKind.FieldAccess(mapNodeId sourceNodeId, fieldId)
            | SemanticNodeKind.UnionConstruct(variantId, fields) ->
                SemanticNodeKind.UnionConstruct(
                    variantId,
                    fields
                    |> List.map (fun binding ->
                        { binding with ValueNodeId = mapNodeId binding.ValueNodeId })
                )
            | SemanticNodeKind.OptionNone itemType ->
                SemanticNodeKind.OptionNone itemType
            | SemanticNodeKind.OptionSome valueNodeId ->
                SemanticNodeKind.OptionSome(mapNodeId valueNodeId)
            | SemanticNodeKind.ResultOk(errorType, valueNodeId) ->
                SemanticNodeKind.ResultOk(errorType, mapNodeId valueNodeId)
            | SemanticNodeKind.ResultErr(okType, errorNodeId) ->
                SemanticNodeKind.ResultErr(okType, mapNodeId errorNodeId)
            | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                SemanticNodeKind.Conditional(
                    mapNodeId conditionNodeId,
                    mapGraph whenTrue,
                    mapGraph whenFalse
                )
            | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                SemanticNodeKind.MatchUnion(
                    mapNodeId inputNodeId,
                    arms |> List.map mapUnionArm
                )
            | SemanticNodeKind.Bind(inputNodeId, body) ->
                SemanticNodeKind.Bind(mapNodeId inputNodeId, mapValueArm body)
            | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                SemanticNodeKind.MatchOption(
                    mapNodeId inputNodeId,
                    mapValueArm someArm,
                    mapGraph noneBody
                )
            | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                SemanticNodeKind.MatchResult(
                    mapNodeId inputNodeId,
                    mapValueArm okArm,
                    mapErrorArm errArm
                )

        mapGraph graph

    let private repairMatches
        (sourceVariantId: VariantId)
        (addedVariantId: VariantId)
        (graph: ValueGraph)
        : ValueGraph * int =
        let rec repairGraph (current: ValueGraph): ValueGraph * int =
            let mutable repairedMatchCount = 0

            let repairNested nested =
                let repaired, count = repairGraph nested
                repairedMatchCount <- repairedMatchCount + count
                repaired

            let repairUnionArm (arm: UnionMatchArm) =
                { arm with Body = repairNested arm.Body }

            let repairValueArm (arm: ValueMatchArm) =
                { arm with Body = repairNested arm.Body }

            let repairErrorArm (arm: ErrorMatchArm) =
                { arm with Body = repairNested arm.Body }

            let repairKind kind =
                match kind with
                | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                    SemanticNodeKind.Conditional(
                        conditionNodeId,
                        repairNested whenTrue,
                        repairNested whenFalse
                    )
                | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                    let repairedArms = arms |> List.map repairUnionArm
                    match
                        repairedArms
                        |> List.tryFind (fun arm -> arm.VariantId = sourceVariantId)
                    with
                    | None ->
                        SemanticNodeKind.MatchUnion(inputNodeId, repairedArms)
                    | Some sourceArm ->
                        repairedMatchCount <- repairedMatchCount + 1
                        let addedArm: UnionMatchArm =
                            { VariantId = addedVariantId
                              FieldBindings = []
                              Body = cloneGraph sourceArm.Body }
                        SemanticNodeKind.MatchUnion(
                            inputNodeId,
                            repairedArms @ [ addedArm ]
                        )
                | SemanticNodeKind.Bind(inputNodeId, body) ->
                    SemanticNodeKind.Bind(inputNodeId, repairValueArm body)
                | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                    SemanticNodeKind.MatchOption(
                        inputNodeId,
                        repairValueArm someArm,
                        repairNested noneBody
                    )
                | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                    SemanticNodeKind.MatchResult(
                        inputNodeId,
                        repairValueArm okArm,
                        repairErrorArm errArm
                    )
                | other -> other

            let nodes =
                current.Nodes
                |> List.map (fun node ->
                    { node with Kind = repairKind node.Kind })

            { current with Nodes = nodes }, repairedMatchCount

        repairGraph graph

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentAddFieldlessUnionVariantLikeRequest)
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
                    match
                        variants
                        |> List.tryFind (fun variant ->
                            variant.Id = request.SourceVariantId)
                    with
                    | None ->
                        rejected
                            "UnionVariantEvolutionSourceVariantNotFound"
                            "The source variant does not belong to the target Union."
                    | Some sourceVariant when not (List.isEmpty sourceVariant.Fields) ->
                        rejected
                            "UnionVariantEvolutionSourceVariantHasFields"
                            "This bounded evolution operation supports only fieldless source variants."
                    | Some _ ->
                        let addedVariant: SemanticVariant =
                            { Id = StableIdAllocator.newVariantId ()
                              DisplayName = request.NewVariantDisplayName
                              Fields = [] }

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

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentFieldlessUnionVariantEvolutionProposal proposal)
        (policy: CommitPolicy)
        : AgentFieldlessUnionVariantEvolutionCommitResult =
        let currentSnapshot =
            AgentWorkspace.snapshotForStructuralEditing workspace
        let receipt = proposal.Review.Receipt

        if receipt.TransactionId <> proposal.TransactionId
           || receipt.ProposedSnapshotId <> proposal.ProposedSnapshot.Id then
            AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                [ diagnostic
                      "PreviewReceiptRequestMismatch"
                      "The reviewed receipt does not identify this Union-variant evolution proposal." ]
        elif currentSnapshot.Id <> receipt.BaseSnapshotId then
            AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                [ diagnostic
                      "StaleBaseSnapshot"
                      "The workspace advanced beyond the reviewed Union-variant evolution proposal." ]
        else
            match SnapshotFingerprint.compute currentSnapshot with
            | Error _ ->
                AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                    [ diagnostic
                          "FingerprintFailure"
                          "The base snapshot fingerprint could not be recomputed." ]
            | Ok baseFingerprint when baseFingerprint <> receipt.BaseSnapshotFingerprint ->
                AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                    [ diagnostic
                          "PreviewReceiptBaseMismatch"
                          "The current base content differs from the reviewed base." ]
            | Ok _ ->
                match
                    SnapshotFingerprint.compute proposal.ProposedSnapshot,
                    SemanticFingerprint.compute proposal.ProposedSnapshot
                with
                | Ok proposedFingerprint, Ok semanticFingerprint
                    when proposedFingerprint = receipt.ProposedSnapshotFingerprint
                         && semanticFingerprint = receipt.ProposedSemanticFingerprint ->
                    match proposal.Review.Readiness, policy with
                    | TransactionReadiness.Incomplete obligationIds,
                      CommitPolicy.RejectBlockingObligations ->
                        let rendered =
                            obligationIds
                            |> List.map StableId.value
                            |> String.concat ", "
                        AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                            [ diagnostic
                                  "BlockingObligations"
                                  $"The proposed snapshot has blocking obligations: {rendered}." ]
                    | _ ->
                        match
                            AgentWorkspace.publishValidatedSnapshot
                                workspace
                                proposal.ProposedSnapshot
                        with
                        | Error diagnostics ->
                            AgentFieldlessUnionVariantEvolutionCommitResult.Rejected diagnostics
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
                | Error _, _
                | _, Error _ ->
                    AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                        [ diagnostic
                              "FingerprintFailure"
                              "The proposed snapshot fingerprint could not be recomputed." ]
                | _ ->
                    AgentFieldlessUnionVariantEvolutionCommitResult.Rejected
                        [ diagnostic
                              "PreviewReceiptResultMismatch"
                              "The proposed Union-variant evolution differs from the reviewed result." ]

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
