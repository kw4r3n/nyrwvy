namespace NyamNyam.Core

open Microsoft.FSharp.Reflection

/// Closed M2 recipe for the first bounded structural-editing slice.
/// Existing semantic objects are referenced by stable ID; new graph-node IDs are
/// allocated only by AgentStructuralWorkflow.
[<RequireQualifiedAccess>]
type Float64ExpressionRecipe =
    | Parameter of ParameterId
    | Literal of Float64Bits
    | Add of Float64ExpressionRecipe * Float64ExpressionRecipe

type AgentReplaceFunctionBodyWithFloat64Recipe =
    { TargetFunctionId: FunctionId
      ExpectedBaseSnapshotId: SnapshotId
      ExpectedCurrentResultNodeId: GraphNodeId
      Replacement: Float64ExpressionRecipe }

type StructuralNodeSummary =
    { NodeId: GraphNodeId
      Role: string
      Type: TypeRef option
      Dependencies: GraphNodeId list
      ParameterId: ParameterId option
      FunctionId: FunctionId option
      Float64BitsHex: string option }

type FunctionBodyReplacementDiff =
    { OwnerFunctionId: FunctionId
      OldResultNodeId: GraphNodeId
      NewResultNodeId: GraphNodeId
      RemovedNodes: StructuralNodeSummary list
      AddedNodes: StructuralNodeSummary list }

type AgentStructuralChangeReview =
    { RequestedChange: AgentReplaceFunctionBodyWithFloat64Recipe
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: FunctionBodyReplacementDiff
      BaseFocus: FunctionFocusView
      ProposedFocus: FunctionFocusView }

type PublishedStructuralTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: FunctionBodyReplacementDiff
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type AgentStructuralRejectionReceipt =
    { TransactionId: TransactionId
      RetainedSnapshotId: SnapshotId }

type private StructuralProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentStructuralChangeReview }

type AgentStructuralChangeProposal = private AgentStructuralChangeProposal of StructuralProposalState

[<RequireQualifiedAccess>]
type AgentStructuralWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | ReviewProjectionFailed of SemanticQueryError
    | FingerprintFailed of SnapshotCodecError

[<RequireQualifiedAccess>]
type AgentStructuralCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * PublishedStructuralTransaction

[<RequireQualifiedAccess>]
module AgentStructuralChangeProposal =
    let private state (AgentStructuralChangeProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentStructuralWorkflow =
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let private diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let private rejected code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    let rec private recipeStats recipe =
        match recipe with
        | Float64ExpressionRecipe.Parameter _
        | Float64ExpressionRecipe.Literal _ -> 1, 1
        | Float64ExpressionRecipe.Add(left, right) ->
            let leftCount, leftDepth = recipeStats left
            let rightCount, rightDepth = recipeStats right
            1 + leftCount + rightCount, 1 + max leftDepth rightDepth

    let rec private recipeParameters recipe =
        match recipe with
        | Float64ExpressionRecipe.Parameter parameterId -> [ parameterId ]
        | Float64ExpressionRecipe.Literal _ -> []
        | Float64ExpressionRecipe.Add(left, right) ->
            recipeParameters left @ recipeParameters right

    /// Materialization is deterministic in shape/order: left-to-right post-order,
    /// one fresh node for every recipe occurrence, with no CSE or local-node reuse.
    let rec private materialize recipe =
        match recipe with
        | Float64ExpressionRecipe.Parameter parameterId ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId
                Kind = SemanticNodeKind.ParameterReference parameterId } ],
            nodeId
        | Float64ExpressionRecipe.Literal bits ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId
                Kind = SemanticNodeKind.Float64Literal bits } ],
            nodeId
        | Float64ExpressionRecipe.Add(left, right) ->
            let leftNodes, leftResult = materialize left
            let rightNodes, rightResult = materialize right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes
            @ rightNodes
            @ [ { Id = nodeId
                  Kind = SemanticNodeKind.Float64Add(leftResult, rightResult) } ],
            nodeId

    let private unionCaseName (kind: SemanticNodeKind) =
        let case, _ = FSharpValue.GetUnionFields(box kind, typeof<SemanticNodeKind>)
        case.Name

    let private summaryExtra (node: SemanticNode) =
        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId -> Some parameterId, None, None
        | SemanticNodeKind.Float64Literal bits -> None, None, Some(Float64Bits.toHex bits)
        | SemanticNodeKind.DirectCall(functionId, _)
        | SemanticNodeKind.ListMap(_, functionId)
        | SemanticNodeKind.ListFilter(_, functionId)
        | SemanticNodeKind.ListAny(_, functionId)
        | SemanticNodeKind.ListAll(_, functionId) -> None, Some functionId, None
        | _ -> None, None, None

    let private summarizeNodes
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        (nodes: SemanticNode list)
        : StructuralNodeSummary list =
        let nodeTypes = SemanticFacts.nodeTypes snapshot functionEntity
        nodes
        |> List.map (fun node ->
            let parameterId, functionId, bitsHex = summaryExtra node
            { NodeId = node.Id
              Role = unionCaseName node.Kind
              Type = Map.tryFind node.Id nodeTypes |> Option.defaultValue None
              Dependencies =
                SemanticFacts.directValueDependencies node
                |> List.distinct
                |> List.sortBy StableId.value
              ParameterId = parameterId
              FunctionId = functionId
              Float64BitsHex = bitsHex })

    let private classifyReadiness snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues ->
            Error(
                issues
                |> List.map (fun issue -> diagnostic ("Validation." + issue.Code) issue.Message)
            )
        | Ok SemanticReadiness.Complete -> Ok TransactionReadiness.SemanticallyReady
        | Ok(SemanticReadiness.Incomplete ids) -> Ok(TransactionReadiness.Incomplete ids)

    let private createReceipt transactionId baseSnapshot proposedSnapshot =
        match SnapshotFingerprint.compute baseSnapshot with
        | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
        | Ok baseFingerprint ->
            match SnapshotFingerprint.compute proposedSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
            | Ok proposedFingerprint ->
                match SemanticFingerprint.compute proposedSnapshot with
                | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
                | Ok semanticFingerprint ->
                    Ok
                        { TransactionId = transactionId
                          BaseSnapshotId = baseSnapshot.Id
                          BaseSnapshotFingerprint = baseFingerprint
                          ProposedSnapshotId = proposedSnapshot.Id
                          ProposedSnapshotFingerprint = proposedFingerprint
                          ProposedSemanticFingerprint = semanticFingerprint }

    /// Validate every agent-controlled precondition before allocating replacement
    /// graph identities. Depth is checked before node count so the two bounded
    /// failure classes remain observable for a deep recipe that also exceeds 64 nodes.
    let private validatePreflight
        (request: AgentReplaceFunctionBodyWithFloat64Recipe)
        (baseSnapshot: Snapshot)
        : Result<SemanticFunction, TransactionDiagnostic> =
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            Error(diagnostic "StructuralExpectedBaseMismatch" "The structural request targets a different base snapshot.")
        else
            match baseSnapshot.Functions |> List.tryFind (fun fn -> fn.Id = request.TargetFunctionId) with
            | None -> Error(diagnostic "StructuralTargetNotFound" "The target function does not exist.")
            | Some target when target.Body.ResultNodeId <> request.ExpectedCurrentResultNodeId ->
                Error(diagnostic "StructuralExpectedResultMismatch" "The target function result node changed after inspection.")
            | Some target when target.ResultType <> float64Type ->
                Error(diagnostic "StructuralTargetResultTypeMismatch" "The target function does not return Float64.")
            | Some target ->
                let nodeCount, depth = recipeStats request.Replacement
                if depth > 32 then
                    Error(diagnostic "StructuralRecipeDepthLimitExceeded" "The structural recipe is deeper than 32 nodes.")
                elif nodeCount > 64 then
                    Error(diagnostic "StructuralRecipeNodeLimitExceeded" "The structural recipe contains more than 64 nodes.")
                else
                    let parametersById = target.Parameters |> List.map (fun p -> p.Id, p) |> Map.ofList
                    let parameterError =
                        request.Replacement
                        |> recipeParameters
                        |> List.tryPick (fun parameterId ->
                            match Map.tryFind parameterId parametersById with
                            | None ->
                                Some(diagnostic "StructuralParameterOutOfScope" $"Parameter '{StableId.value parameterId}' is not owned by the target function.")
                            | Some parameter when parameter.Type <> float64Type ->
                                Some(diagnostic "StructuralParameterTypeMismatch" $"Parameter '{StableId.value parameterId}' is not Float64.")
                            | Some _ -> None)

                    match parameterError with
                    | Some error -> Error error
                    | None ->
                        let oldBodyIds =
                            ValueGraph.collectNodes target.Body
                            |> List.map (fun node -> node.Id)
                            |> Set.ofList
                        match baseSnapshot.Obligations |> List.tryFind (fun o -> Set.contains o.CallNodeId oldBodyIds) with
                        | Some obligation ->
                            Error(diagnostic "StructuralBodyReferencedByObligation" $"Obligation '{StableId.value obligation.Id}' targets the body being replaced.")
                        | None -> Ok target

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithFloat64Recipe)
        : Result<AgentStructuralChangeProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            match validatePreflight request baseSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Ok target ->
                let newNodes, newResultId = materialize request.Replacement
                let replacementBody = { Nodes = newNodes; ResultNodeId = newResultId }
                let proposedSnapshotId = StableIdAllocator.newSnapshotId ()
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = proposedSnapshotId
                        ParentId = Some baseSnapshot.Id
                        Functions =
                            baseSnapshot.Functions
                            |> List.map (fun fn -> if fn.Id = target.Id then { fn with Body = replacementBody } else fn) }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: _ -> rejected "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [] ->
                    match classifyReadiness proposedSnapshot with
                    | Error diagnostics -> Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        match createReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error error
                        | Ok receipt ->
                            match SemanticQuery.focusFunction baseSnapshot target.Id, SemanticQuery.focusFunction proposedSnapshot target.Id with
                            | Error error, _
                            | _, Error error -> Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                            | Ok baseFocus, Ok proposedFocus ->
                                let proposedTarget = proposedSnapshot.Functions |> List.find (fun fn -> fn.Id = target.Id)
                                let diff =
                                    { OwnerFunctionId = target.Id
                                      OldResultNodeId = target.Body.ResultNodeId
                                      NewResultNodeId = newResultId
                                      RemovedNodes =
                                        ValueGraph.collectNodes target.Body
                                        |> summarizeNodes baseSnapshot target
                                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                                      AddedNodes = summarizeNodes proposedSnapshot proposedTarget proposedTarget.Body.Nodes }
                                let review =
                                    { RequestedChange = request
                                      Receipt = receipt
                                      Readiness = readiness
                                      ResultingChange = diff
                                      BaseFocus = baseFocus
                                      ProposedFocus = proposedFocus }
                                Ok(AgentStructuralChangeProposal { TransactionId = transactionId; ProposedSnapshot = proposedSnapshot; Review = review })

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentStructuralChangeProposal proposal)
        (policy: CommitPolicy)
        : AgentStructuralCommitResult =
        let currentSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        let receipt = proposal.Review.Receipt
        if currentSnapshot.Id <> receipt.BaseSnapshotId then
            AgentStructuralCommitResult.Rejected [ diagnostic "StaleBaseSnapshot" "The workspace advanced beyond the reviewed structural proposal." ]
        else
            match SnapshotFingerprint.compute currentSnapshot with
            | Error _ -> AgentStructuralCommitResult.Rejected [ diagnostic "FingerprintFailure" "The base fingerprint could not be recomputed." ]
            | Ok baseFingerprint when baseFingerprint <> receipt.BaseSnapshotFingerprint ->
                AgentStructuralCommitResult.Rejected [ diagnostic "PreviewReceiptBaseMismatch" "The current base content differs from the reviewed base." ]
            | Ok _ ->
                match SnapshotFingerprint.compute proposal.ProposedSnapshot, SemanticFingerprint.compute proposal.ProposedSnapshot with
                | Ok proposedFingerprint, Ok semanticFingerprint
                    when proposedFingerprint = receipt.ProposedSnapshotFingerprint
                         && semanticFingerprint = receipt.ProposedSemanticFingerprint ->
                    match proposal.Review.Readiness, policy with
                    | TransactionReadiness.Incomplete ids, CommitPolicy.RejectBlockingObligations ->
                        let rendered = ids |> List.map StableId.value |> String.concat ", "
                        AgentStructuralCommitResult.Rejected [ diagnostic "BlockingObligations" $"The proposed snapshot has blocking obligations: {rendered}." ]
                    | _ ->
                        match AgentWorkspace.publishValidatedSnapshot workspace proposal.ProposedSnapshot with
                        | Error diagnostics -> AgentStructuralCommitResult.Rejected diagnostics
                        | Ok nextWorkspace ->
                            AgentStructuralCommitResult.Committed(
                                nextWorkspace,
                                { TransactionId = proposal.TransactionId
                                  BaseSnapshotId = receipt.BaseSnapshotId
                                  PublishedSnapshotId = proposal.ProposedSnapshot.Id
                                  Diff = proposal.Review.ResultingChange
                                  Readiness = proposal.Review.Readiness
                                  Receipt = receipt }
                            )
                | Ok _, Ok _ -> AgentStructuralCommitResult.Rejected [ diagnostic "PreviewReceiptResultMismatch" "The proposed state differs from the reviewed receipt." ]
                | Error _, _
                | _, Error _ -> AgentStructuralCommitResult.Rejected [ diagnostic "FingerprintFailure" "The proposed fingerprint could not be recomputed." ]

    let commit workspace proposal =
        publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

    let commitWithPolicy workspace proposal policy =
        publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentStructuralChangeProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
