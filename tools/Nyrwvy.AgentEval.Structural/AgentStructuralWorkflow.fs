namespace Nyrwvy.Core

/// Typed Float64 expression recipe for bounded structural body replacement.
/// Existing semantic objects are referenced by stable ID; new graph-node IDs are
/// allocated only by AgentStructuralWorkflow.
[<RequireQualifiedAccess>]
type Float64ExpressionRecipe =
    | Parameter of ParameterId
    | Literal of Float64Bits
    | Add of Float64ExpressionRecipe * Float64ExpressionRecipe
    | Subtract of Float64ExpressionRecipe * Float64ExpressionRecipe
    | Multiply of Float64ExpressionRecipe * Float64ExpressionRecipe
    | Divide of Float64ExpressionRecipe * Float64ExpressionRecipe

type AgentReplaceFunctionBodyWithFloat64Recipe =
    { TargetFunctionId: FunctionId
      ExpectedBaseSnapshotId: SnapshotId
      ExpectedCurrentResultNodeId: GraphNodeId
      Replacement: Float64ExpressionRecipe }

type AgentStructuralChangeReview =
    { RequestedChange: AgentReplaceFunctionBodyWithFloat64Recipe
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: FunctionBodyReplacementDiff
      BaseFocus: FunctionFocusView
      ProposedFocus: FunctionFocusView }

type private StructuralProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentStructuralChangeReview }

type AgentStructuralChangeProposal = private AgentStructuralChangeProposal of StructuralProposalState

[<RequireQualifiedAccess>]
module AgentStructuralChangeProposal =
    let private state (AgentStructuralChangeProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentStructuralWorkflow =
    open StructuralTransactionInternals

    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let private recipeChildren recipe =
        match recipe with
        | Float64ExpressionRecipe.Add(left, right)
        | Float64ExpressionRecipe.Subtract(left, right)
        | Float64ExpressionRecipe.Multiply(left, right)
        | Float64ExpressionRecipe.Divide(left, right) -> seq { yield left; yield right }
        | Float64ExpressionRecipe.Parameter _
        | Float64ExpressionRecipe.Literal _ -> Seq.empty

    let rec private recipeParameters recipe =
        match recipe with
        | Float64ExpressionRecipe.Parameter parameterId -> [ parameterId ]
        | Float64ExpressionRecipe.Literal _ -> []
        | Float64ExpressionRecipe.Add(left, right)
        | Float64ExpressionRecipe.Subtract(left, right)
        | Float64ExpressionRecipe.Multiply(left, right)
        | Float64ExpressionRecipe.Divide(left, right) ->
            recipeParameters left @ recipeParameters right

    /// Materialization is deterministic in shape/order: left-to-right post-order,
    /// one fresh node for every recipe occurrence, with no CSE or local-node reuse.
    let rec private materialize recipe =
        let materializeBinary kind left right =
            let leftNodes, leftResult = materialize left
            let rightNodes, rightResult = materialize right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes
            @ rightNodes
            @ [ { Id = nodeId
                  Kind = kind (leftResult, rightResult) } ],
            nodeId

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
            materializeBinary SemanticNodeKind.Float64Add left right
        | Float64ExpressionRecipe.Subtract(left, right) ->
            materializeBinary SemanticNodeKind.Float64Subtract left right
        | Float64ExpressionRecipe.Multiply(left, right) ->
            materializeBinary SemanticNodeKind.Float64Multiply left right
        | Float64ExpressionRecipe.Divide(left, right) ->
            materializeBinary SemanticNodeKind.Float64Divide left right

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
                match validateRecipeBounds recipeChildren request.Replacement with
                | Error error -> Error error
                | Ok() ->
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
                        match bodyObligationError target baseSnapshot with
                        | Some error -> Error error
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
        publishReviewedWithPolicy
            workspace
            proposal.TransactionId
            proposal.ProposedSnapshot
            proposal.Review.ResultingChange
            proposal.Review.Readiness
            proposal.Review.Receipt
            policy

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
