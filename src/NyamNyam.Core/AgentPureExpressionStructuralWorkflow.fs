namespace NyamNyam.Core

/// Ephemeral typed construction intent for the first cross-domain pure-expression slice.
/// Child expressions are nested recipes; production graph-node IDs remain system-owned.
[<RequireQualifiedAccess>]
type PureExpressionRecipe =
    | Parameter of ParameterId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of PureExpressionRecipe
    | StringContains of PureExpressionRecipe * PureExpressionRecipe
    | Int64WrappingAdd of PureExpressionRecipe * PureExpressionRecipe
    | Int64WrappingSubtract of PureExpressionRecipe * PureExpressionRecipe
    | Int64WrappingMultiply of PureExpressionRecipe * PureExpressionRecipe
    | Int64Equal of PureExpressionRecipe * PureExpressionRecipe
    | Int64LessThan of PureExpressionRecipe * PureExpressionRecipe
    | Int64LessThanOrEqual of PureExpressionRecipe * PureExpressionRecipe
    | Int64GreaterThan of PureExpressionRecipe * PureExpressionRecipe
    | Int64GreaterThanOrEqual of PureExpressionRecipe * PureExpressionRecipe
    | Float64Add of PureExpressionRecipe * PureExpressionRecipe
    | Float64Subtract of PureExpressionRecipe * PureExpressionRecipe
    | Float64Multiply of PureExpressionRecipe * PureExpressionRecipe
    | Float64Divide of PureExpressionRecipe * PureExpressionRecipe
    | Float64Equal of PureExpressionRecipe * PureExpressionRecipe
    | Float64LessThan of PureExpressionRecipe * PureExpressionRecipe
    | Float64IsNaN of PureExpressionRecipe
    | Float64IsFinite of PureExpressionRecipe
    | Float64IsInfinite of PureExpressionRecipe

type AgentReplaceFunctionBodyWithPureExpressionRecipe =
    { TargetFunctionId: FunctionId
      ExpectedBaseSnapshotId: SnapshotId
      ExpectedCurrentResultNodeId: GraphNodeId
      Replacement: PureExpressionRecipe }

type AgentPureExpressionStructuralChangeReview =
    { RequestedChange: AgentReplaceFunctionBodyWithPureExpressionRecipe
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: FunctionBodyReplacementDiff
      BaseFocus: FunctionFocusView
      ProposedFocus: FunctionFocusView }

type private PureExpressionStructuralProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentPureExpressionStructuralChangeReview }

type AgentPureExpressionStructuralChangeProposal =
    private
    | AgentPureExpressionStructuralChangeProposal of PureExpressionStructuralProposalState

[<RequireQualifiedAccess>]
module AgentPureExpressionStructuralChangeProposal =
    let private state (AgentPureExpressionStructuralChangeProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentPureExpressionStructuralWorkflow =
    open StructuralTransactionInternals

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let rec private recipeStats recipe =
        match recipe with
        | PureExpressionRecipe.Parameter _
        | PureExpressionRecipe.StringLiteral _
        | PureExpressionRecipe.BoolLiteral _
        | PureExpressionRecipe.UnitLiteral
        | PureExpressionRecipe.Int64Literal _
        | PureExpressionRecipe.Float64Literal _ -> 1, 1
        | PureExpressionRecipe.StringIsEmpty child
        | PureExpressionRecipe.Float64IsNaN child
        | PureExpressionRecipe.Float64IsFinite child
        | PureExpressionRecipe.Float64IsInfinite child ->
            let count, depth = recipeStats child
            1 + count, 1 + depth
        | PureExpressionRecipe.StringContains(left, right)
        | PureExpressionRecipe.Int64WrappingAdd(left, right)
        | PureExpressionRecipe.Int64WrappingSubtract(left, right)
        | PureExpressionRecipe.Int64WrappingMultiply(left, right)
        | PureExpressionRecipe.Int64Equal(left, right)
        | PureExpressionRecipe.Int64LessThan(left, right)
        | PureExpressionRecipe.Int64LessThanOrEqual(left, right)
        | PureExpressionRecipe.Int64GreaterThan(left, right)
        | PureExpressionRecipe.Int64GreaterThanOrEqual(left, right)
        | PureExpressionRecipe.Float64Add(left, right)
        | PureExpressionRecipe.Float64Subtract(left, right)
        | PureExpressionRecipe.Float64Multiply(left, right)
        | PureExpressionRecipe.Float64Divide(left, right)
        | PureExpressionRecipe.Float64Equal(left, right)
        | PureExpressionRecipe.Float64LessThan(left, right) ->
            let leftCount, leftDepth = recipeStats left
            let rightCount, rightDepth = recipeStats right
            1 + leftCount + rightCount, 1 + max leftDepth rightDepth

    let private inferType (target: SemanticFunction) recipe =
        let parametersById = target.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList

        let typeMismatch role side expected actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires {expected}, but the recipe produces {actual}."

        let rec infer recipe =
            let unary role expected result child =
                match infer child with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "value" expected actual)
                | Ok _ -> Ok result

            let binary role expected result left right =
                match infer left with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "left" expected actual)
                | Ok _ ->
                    match infer right with
                    | Error error -> Error error
                    | Ok actual when actual <> expected -> Error(typeMismatch role "right" expected actual)
                    | Ok _ -> Ok result

            match recipe with
            | PureExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId parametersById with
                | Some parameter -> Ok parameter.Type
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionParameterOutOfScope"
                            $"Parameter '{StableId.value parameterId}' is not owned by the target function."
                    )
            | PureExpressionRecipe.StringLiteral _ -> Ok stringType
            | PureExpressionRecipe.BoolLiteral _ -> Ok boolType
            | PureExpressionRecipe.UnitLiteral -> Ok unitType
            | PureExpressionRecipe.Int64Literal _ -> Ok int64Type
            | PureExpressionRecipe.Float64Literal _ -> Ok float64Type
            | PureExpressionRecipe.StringIsEmpty value ->
                unary "StringIsEmpty" stringType boolType value
            | PureExpressionRecipe.StringContains(value, substring) ->
                binary "StringContains" stringType boolType value substring
            | PureExpressionRecipe.Int64WrappingAdd(left, right) ->
                binary "Int64WrappingAdd" int64Type int64Type left right
            | PureExpressionRecipe.Int64WrappingSubtract(left, right) ->
                binary "Int64WrappingSubtract" int64Type int64Type left right
            | PureExpressionRecipe.Int64WrappingMultiply(left, right) ->
                binary "Int64WrappingMultiply" int64Type int64Type left right
            | PureExpressionRecipe.Int64Equal(left, right) ->
                binary "Int64Equal" int64Type boolType left right
            | PureExpressionRecipe.Int64LessThan(left, right) ->
                binary "Int64LessThan" int64Type boolType left right
            | PureExpressionRecipe.Int64LessThanOrEqual(left, right) ->
                binary "Int64LessThanOrEqual" int64Type boolType left right
            | PureExpressionRecipe.Int64GreaterThan(left, right) ->
                binary "Int64GreaterThan" int64Type boolType left right
            | PureExpressionRecipe.Int64GreaterThanOrEqual(left, right) ->
                binary "Int64GreaterThanOrEqual" int64Type boolType left right
            | PureExpressionRecipe.Float64Add(left, right) ->
                binary "Float64Add" float64Type float64Type left right
            | PureExpressionRecipe.Float64Subtract(left, right) ->
                binary "Float64Subtract" float64Type float64Type left right
            | PureExpressionRecipe.Float64Multiply(left, right) ->
                binary "Float64Multiply" float64Type float64Type left right
            | PureExpressionRecipe.Float64Divide(left, right) ->
                binary "Float64Divide" float64Type float64Type left right
            | PureExpressionRecipe.Float64Equal(left, right) ->
                binary "Float64Equal" float64Type boolType left right
            | PureExpressionRecipe.Float64LessThan(left, right) ->
                binary "Float64LessThan" float64Type boolType left right
            | PureExpressionRecipe.Float64IsNaN value ->
                unary "Float64IsNaN" float64Type boolType value
            | PureExpressionRecipe.Float64IsFinite value ->
                unary "Float64IsFinite" float64Type boolType value
            | PureExpressionRecipe.Float64IsInfinite value ->
                unary "Float64IsInfinite" float64Type boolType value

        infer recipe

    /// Validates target identity, bounds, scope and recipe types before production IDs are allocated.
    let private validatePreflight
        (request: AgentReplaceFunctionBodyWithPureExpressionRecipe)
        (baseSnapshot: Snapshot)
        : Result<SemanticFunction, TransactionDiagnostic> =
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            Error(
                diagnostic
                    "StructuralExpectedBaseMismatch"
                    "The structural request targets a different base snapshot."
            )
        else
            match baseSnapshot.Functions |> List.tryFind (fun functionEntity -> functionEntity.Id = request.TargetFunctionId) with
            | None ->
                Error(diagnostic "StructuralTargetNotFound" "The target function does not exist.")
            | Some target when target.Body.ResultNodeId <> request.ExpectedCurrentResultNodeId ->
                Error(
                    diagnostic
                        "StructuralExpectedResultMismatch"
                        "The target function result node changed after inspection."
                )
            | Some target ->
                let nodeCount, depth = recipeStats request.Replacement

                if depth > 32 then
                    Error(
                        diagnostic
                            "StructuralRecipeDepthLimitExceeded"
                            "The structural recipe is deeper than 32 nodes."
                    )
                elif nodeCount > 64 then
                    Error(
                        diagnostic
                            "StructuralRecipeNodeLimitExceeded"
                            "The structural recipe contains more than 64 nodes."
                    )
                else
                    match inferType target request.Replacement with
                    | Error error -> Error error
                    | Ok replacementType when replacementType <> target.ResultType ->
                        Error(
                            diagnostic
                                "PureExpressionResultTypeMismatch"
                                $"The replacement recipe produces {replacementType}, but the target function returns {target.ResultType}."
                        )
                    | Ok _ ->
                        match bodyObligationError target baseSnapshot with
                        | Some error -> Error error
                        | None -> Ok target

    /// Materialization is deterministic left-to-right post-order with no caller-controlled IDs or CSE.
    let rec private materialize recipe =
        let unary kind child =
            let childNodes, childResult = materialize child
            let nodeId = StableIdAllocator.newGraphNodeId ()
            childNodes @ [ { Id = nodeId; Kind = kind childResult } ], nodeId

        let binary kind left right =
            let leftNodes, leftResult = materialize left
            let rightNodes, rightResult = materialize right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes
            @ rightNodes
            @ [ { Id = nodeId
                  Kind = kind (leftResult, rightResult) } ],
            nodeId

        match recipe with
        | PureExpressionRecipe.Parameter parameterId ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ], nodeId
        | PureExpressionRecipe.StringLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral value } ], nodeId
        | PureExpressionRecipe.BoolLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.BoolLiteral value } ], nodeId
        | PureExpressionRecipe.UnitLiteral ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.UnitLiteral } ], nodeId
        | PureExpressionRecipe.Int64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Int64Literal value } ], nodeId
        | PureExpressionRecipe.Float64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Float64Literal value } ], nodeId
        | PureExpressionRecipe.StringIsEmpty value ->
            unary SemanticNodeKind.StringIsEmpty value
        | PureExpressionRecipe.StringContains(value, substring) ->
            binary SemanticNodeKind.StringContains value substring
        | PureExpressionRecipe.Int64WrappingAdd(left, right) ->
            binary SemanticNodeKind.Int64WrappingAdd left right
        | PureExpressionRecipe.Int64WrappingSubtract(left, right) ->
            binary SemanticNodeKind.Int64WrappingSubtract left right
        | PureExpressionRecipe.Int64WrappingMultiply(left, right) ->
            binary SemanticNodeKind.Int64WrappingMultiply left right
        | PureExpressionRecipe.Int64Equal(left, right) ->
            binary SemanticNodeKind.Int64Equal left right
        | PureExpressionRecipe.Int64LessThan(left, right) ->
            binary SemanticNodeKind.Int64LessThan left right
        | PureExpressionRecipe.Int64LessThanOrEqual(left, right) ->
            binary SemanticNodeKind.Int64LessThanOrEqual left right
        | PureExpressionRecipe.Int64GreaterThan(left, right) ->
            binary SemanticNodeKind.Int64GreaterThan left right
        | PureExpressionRecipe.Int64GreaterThanOrEqual(left, right) ->
            binary SemanticNodeKind.Int64GreaterThanOrEqual left right
        | PureExpressionRecipe.Float64Add(left, right) ->
            binary SemanticNodeKind.Float64Add left right
        | PureExpressionRecipe.Float64Subtract(left, right) ->
            binary SemanticNodeKind.Float64Subtract left right
        | PureExpressionRecipe.Float64Multiply(left, right) ->
            binary SemanticNodeKind.Float64Multiply left right
        | PureExpressionRecipe.Float64Divide(left, right) ->
            binary SemanticNodeKind.Float64Divide left right
        | PureExpressionRecipe.Float64Equal(left, right) ->
            binary SemanticNodeKind.Float64Equal left right
        | PureExpressionRecipe.Float64LessThan(left, right) ->
            binary SemanticNodeKind.Float64LessThan left right
        | PureExpressionRecipe.Float64IsNaN value ->
            unary SemanticNodeKind.Float64IsNaN value
        | PureExpressionRecipe.Float64IsFinite value ->
            unary SemanticNodeKind.Float64IsFinite value
        | PureExpressionRecipe.Float64IsInfinite value ->
            unary SemanticNodeKind.Float64IsInfinite value

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithPureExpressionRecipe)
        : Result<AgentPureExpressionStructuralChangeProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            match validatePreflight request baseSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Ok target ->
                let newNodes, newResultId = materialize request.Replacement
                let replacementBody =
                    { Nodes = newNodes
                      ResultNodeId = newResultId }
                let proposedSnapshotId = StableIdAllocator.newSnapshotId ()
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = proposedSnapshotId
                        ParentId = Some baseSnapshot.Id
                        Functions =
                            baseSnapshot.Functions
                            |> List.map (fun functionEntity ->
                                if functionEntity.Id = target.Id then
                                    { functionEntity with Body = replacementBody }
                                else
                                    functionEntity) }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: _ -> rejected "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [] ->
                    match classifyReadiness proposedSnapshot with
                    | Error diagnostics -> Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        match createReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error error
                        | Ok receipt ->
                            match
                                SemanticQuery.focusFunction baseSnapshot target.Id,
                                SemanticQuery.focusFunction proposedSnapshot target.Id
                            with
                            | Error error, _
                            | _, Error error -> Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                            | Ok baseFocus, Ok proposedFocus ->
                                let proposedTarget =
                                    proposedSnapshot.Functions
                                    |> List.find (fun functionEntity -> functionEntity.Id = target.Id)

                                let diff =
                                    { OwnerFunctionId = target.Id
                                      OldResultNodeId = target.Body.ResultNodeId
                                      NewResultNodeId = newResultId
                                      RemovedNodes =
                                        ValueGraph.collectNodes target.Body
                                        |> summarizeNodes baseSnapshot target
                                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                                      AddedNodes =
                                        summarizeNodes
                                            proposedSnapshot
                                            proposedTarget
                                            proposedTarget.Body.Nodes }

                                let review =
                                    { RequestedChange = request
                                      Receipt = receipt
                                      Readiness = readiness
                                      ResultingChange = diff
                                      BaseFocus = baseFocus
                                      ProposedFocus = proposedFocus }

                                Ok(
                                    AgentPureExpressionStructuralChangeProposal
                                        { TransactionId = transactionId
                                          ProposedSnapshot = proposedSnapshot
                                          Review = review }
                                )

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentPureExpressionStructuralChangeProposal proposal)
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
        (AgentPureExpressionStructuralChangeProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
