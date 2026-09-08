namespace NyamNyam.Core

open Microsoft.FSharp.Reflection

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

[<RequireQualifiedAccess>]
type ValueDependencyRole =
    | Value
    | Error
    | Substring
    | LeftOperand
    | RightOperand
    | Source
    | Item
    | ListItem of int
    | CallArgument of ParameterId
    | CapabilityValue
    | CapabilityArgument of CapabilityParameterId
    | FieldValue of FieldId
    | BranchCondition
    | MatchInput
    | BindInput

type ValueDependency =
    { Role: ValueDependencyRole
      NodeId: GraphNodeId }

[<RequireQualifiedAccess>]
module ValueDependency =
    let private create role nodeId =
        { Role = role
          NodeId = nodeId }

    /// Canonical role-aware direct value dependencies for one semantic node.
    /// Nested branch result nodes remain represented by their child ValueGraphs.
    let ofNode (node: SemanticNode): ValueDependency list =
        match node.Kind with
        | SemanticNodeKind.StringIsEmpty sourceNodeId ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.StringContains(sourceNodeId, substringNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId ]
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListConcat(leftNodeId, rightNodeId) ->
            [ create ValueDependencyRole.LeftOperand leftNodeId
              create ValueDependencyRole.RightOperand rightNodeId ]
        | SemanticNodeKind.Float64IsNaN valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId ->
            [ create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.ListConstruct(_, itemNodeIds) ->
            itemNodeIds
            |> List.mapi (fun index nodeId -> create (ValueDependencyRole.ListItem index) nodeId)
        | SemanticNodeKind.ListLength sourceNodeId
        | SemanticNodeKind.ListMap(sourceNodeId, _)
        | SemanticNodeKind.ListFilter(sourceNodeId, _)
        | SemanticNodeKind.ListAny(sourceNodeId, _)
        | SemanticNodeKind.ListAll(sourceNodeId, _)
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.ListAppendItem(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.DirectCall(_, arguments) ->
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
            |> List.map (fun argument ->
                create (ValueDependencyRole.CallArgument argument.TargetParameterId) argument.ValueNodeId)
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            create ValueDependencyRole.CapabilityValue capabilityValueNodeId
            :: (arguments
                |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                |> List.map (fun argument ->
                    create
                        (ValueDependencyRole.CapabilityArgument argument.TargetCapabilityParameterId)
                        argument.ValueNodeId))
        | SemanticNodeKind.ProductConstruct(_, fields)
        | SemanticNodeKind.UnionConstruct(_, fields) ->
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.map (fun field ->
                create (ValueDependencyRole.FieldValue field.TargetFieldId) field.ValueNodeId)
        | SemanticNodeKind.OptionSome valueNodeId
        | SemanticNodeKind.ResultOk(_, valueNodeId) ->
            [ create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.ResultErr(_, errorNodeId) ->
            [ create ValueDependencyRole.Error errorNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) ->
            [ create ValueDependencyRole.BranchCondition conditionNodeId ]
        | SemanticNodeKind.Bind(inputNodeId, _) ->
            [ create ValueDependencyRole.BindInput inputNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _)
        | SemanticNodeKind.MatchOption(inputNodeId, _, _)
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) ->
            [ create ValueDependencyRole.MatchInput inputNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.OptionNone _ -> []

type StructuralNodeSummary =
    { NodeId: GraphNodeId
      Role: string
      Type: TypeRef option
      Dependencies: GraphNodeId list
      ValueDependencies: ValueDependency list
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

module internal StructuralTransactionInternals =
    let diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let rejected code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    let validateRecipeBounds
        (children: 'Recipe -> seq<'Recipe>)
        (recipe: 'Recipe)
        : Result<unit, TransactionDiagnostic> =
        let rec visit depth remaining current =
            if depth > 32 then
                Error(
                    diagnostic
                        "StructuralRecipeDepthLimitExceeded"
                        "The structural recipe is deeper than 32 nodes."
                )
            elif remaining <= 0 then
                Error(
                    diagnostic
                        "StructuralRecipeNodeLimitExceeded"
                        "The structural recipe contains more than 64 nodes."
                )
            else
                let mutable nextRemaining = remaining - 1
                let mutable failure: TransactionDiagnostic option = None
                use enumerator = (children current).GetEnumerator()

                while Option.isNone failure && enumerator.MoveNext() do
                    match visit (depth + 1) nextRemaining enumerator.Current with
                    | Ok remainingAfterChild -> nextRemaining <- remainingAfterChild
                    | Error error -> failure <- Some error

                match failure with
                | Some error -> Error error
                | None -> Ok nextRemaining

        visit 1 64 recipe |> Result.map ignore

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

    let summarizeNodes
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        (nodes: SemanticNode list)
        : StructuralNodeSummary list =
        let nodeTypes = SemanticFacts.nodeTypes snapshot functionEntity
        nodes
        |> List.map (fun node ->
            let parameterId, functionId, bitsHex = summaryExtra node
            let valueDependencies = ValueDependency.ofNode node
            { NodeId = node.Id
              Role = unionCaseName node.Kind
              Type = Map.tryFind node.Id nodeTypes |> Option.defaultValue None
              Dependencies = valueDependencies |> List.map (fun dependency -> dependency.NodeId)
              ValueDependencies = valueDependencies
              ParameterId = parameterId
              FunctionId = functionId
              Float64BitsHex = bitsHex })

    let classifyReadiness snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues ->
            Error(
                issues
                |> List.map (fun issue -> diagnostic ("Validation." + issue.Code) issue.Message)
            )
        | Ok SemanticReadiness.Complete -> Ok TransactionReadiness.SemanticallyReady
        | Ok(SemanticReadiness.Incomplete ids) -> Ok(TransactionReadiness.Incomplete ids)

    let createReceipt transactionId baseSnapshot proposedSnapshot =
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

    let bodyObligationError (target: SemanticFunction) (snapshot: Snapshot) =
        let oldBodyIds =
            ValueGraph.collectNodes target.Body
            |> List.map (fun node -> node.Id)
            |> Set.ofList

        snapshot.Obligations
        |> List.tryFind (fun obligation -> Set.contains obligation.CallNodeId oldBodyIds)
        |> Option.map (fun obligation ->
            diagnostic
                "StructuralBodyReferencedByObligation"
                $"Obligation '{StableId.value obligation.Id}' targets the body being replaced.")

    let publishReviewedWithPolicy
        (workspace: AgentWorkspace)
        (transactionId: TransactionId)
        (proposedSnapshot: Snapshot)
        (diff: FunctionBodyReplacementDiff)
        (readiness: TransactionReadiness)
        (receipt: TransactionPreviewReceipt)
        (policy: CommitPolicy)
        : AgentStructuralCommitResult =
        let currentSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        if currentSnapshot.Id <> receipt.BaseSnapshotId then
            AgentStructuralCommitResult.Rejected
                [ diagnostic
                      "StaleBaseSnapshot"
                      "The workspace advanced beyond the reviewed structural proposal." ]
        else
            match SnapshotFingerprint.compute currentSnapshot with
            | Error _ ->
                AgentStructuralCommitResult.Rejected
                    [ diagnostic
                          "FingerprintFailure"
                          "The base fingerprint could not be recomputed." ]
            | Ok baseFingerprint when baseFingerprint <> receipt.BaseSnapshotFingerprint ->
                AgentStructuralCommitResult.Rejected
                    [ diagnostic
                          "PreviewReceiptBaseMismatch"
                          "The current base content differs from the reviewed base." ]
            | Ok _ ->
                match SnapshotFingerprint.compute proposedSnapshot, SemanticFingerprint.compute proposedSnapshot with
                | Ok proposedFingerprint, Ok semanticFingerprint
                    when proposedFingerprint = receipt.ProposedSnapshotFingerprint
                         && semanticFingerprint = receipt.ProposedSemanticFingerprint ->
                    match readiness, policy with
                    | TransactionReadiness.Incomplete ids, CommitPolicy.RejectBlockingObligations ->
                        let rendered = ids |> List.map StableId.value |> String.concat ", "
                        AgentStructuralCommitResult.Rejected
                            [ diagnostic
                                  "BlockingObligations"
                                  $"The proposed snapshot has blocking obligations: {rendered}." ]
                    | _ ->
                        match AgentWorkspace.publishValidatedSnapshot workspace proposedSnapshot with
                        | Error diagnostics -> AgentStructuralCommitResult.Rejected diagnostics
                        | Ok nextWorkspace ->
                            AgentStructuralCommitResult.Committed(
                                nextWorkspace,
                                { TransactionId = transactionId
                                  BaseSnapshotId = receipt.BaseSnapshotId
                                  PublishedSnapshotId = proposedSnapshot.Id
                                  Diff = diff
                                  Readiness = readiness
                                  Receipt = receipt }
                            )
                | Ok _, Ok _ ->
                    AgentStructuralCommitResult.Rejected
                        [ diagnostic
                              "PreviewReceiptResultMismatch"
                              "The proposed state differs from the reviewed receipt." ]
                | Error _, _
                | _, Error _ ->
                    AgentStructuralCommitResult.Rejected
                        [ diagnostic
                              "FingerprintFailure"
                              "The proposed fingerprint could not be recomputed." ]

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
