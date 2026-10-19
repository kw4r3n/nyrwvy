namespace Nyrwvy.Core

type AgentReplaceFunctionBodyWithExpressionRecipe =
    { TargetFunctionId: FunctionId
      ExpectedBaseSnapshotId: SnapshotId
      ExpectedCurrentResultNodeId: GraphNodeId
      Replacement: ExpressionRecipe }

type AgentExpressionStructuralChangeReview =
    { RequestedChange: AgentReplaceFunctionBodyWithExpressionRecipe
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: FunctionBodyReplacementDiff
      BaseFocus: FunctionFocusView
      ProposedFocus: FunctionFocusView }

type private ExpressionStructuralProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentExpressionStructuralChangeReview }

type AgentExpressionStructuralChangeProposal =
    private
    | AgentExpressionStructuralChangeProposal of ExpressionStructuralProposalState

[<RequireQualifiedAccess>]
module AgentExpressionStructuralChangeProposal =
    let private state (AgentExpressionStructuralChangeProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id
    /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot

[<RequireQualifiedAccess>]
module AgentExpressionStructuralWorkflow =
    open StructuralTransactionInternals
    open ExpressionRecipeSupport
    open ExpressionRecipeInference
    open ExpressionRecipeMaterialization
    open TargetedReplacementGraph

    let private validatePreflight
        (request: AgentReplaceFunctionBodyWithExpressionRecipe)
        (index: SnapshotIndex)
        : Result<SemanticFunction, TransactionDiagnostic> =
        let baseSnapshot = index.Snapshot
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            Error(diagnostic "StructuralExpectedBaseMismatch" "The structural request targets a different base snapshot.")
        else
            match baseSnapshot.Functions |> List.tryFind (fun functionEntity -> functionEntity.Id = request.TargetFunctionId) with
            | None -> Error(diagnostic "StructuralTargetNotFound" "The target function does not exist.")
            | Some target when target.Body.ResultNodeId <> request.ExpectedCurrentResultNodeId ->
                Error(diagnostic "StructuralExpectedResultMismatch" "The target function result node changed after inspection.")
            | Some target ->
                match validateRecipeBounds recipeChildren request.Replacement with
                | Error error -> Error error
                | Ok() ->
                    match inferType (Some "replacement") index target request.Replacement with
                    | Error error -> Error error
                    | Ok(GraphCompletion.Normal replacementType) when replacementType <> target.ResultType ->
                        Error(
                            diagnostic
                                "PureExpressionResultTypeMismatch"
                                $"The replacement recipe produces {DiagnosticText.typeRef baseSnapshot replacementType}, but the target function returns {DiagnosticText.typeRef baseSnapshot target.ResultType}."
                        )
                    | Ok _ ->
                        match bodyObligationError target baseSnapshot with
                        | Some error -> Error error
                        | None -> Ok target

    /// Materializes a complete replacement body for an existing function against a candidate
    /// snapshot that a multi-function migration is still completing. The recipe is bound and
    /// type-checked against the candidate; the caller validates the final snapshot as a whole.
    let internal materializeReplacementBody (snapshot: Snapshot) (target: SemanticFunction) recipe
        : Result<ValueGraph, TransactionDiagnostic> =
        let index = buildSnapshotIndex snapshot
        match validateRecipeBounds recipeChildren recipe with
        | Error error -> Error error
        | Ok() ->
            match inferType None index target recipe with
            | Error error -> Error error
            | Ok(GraphCompletion.Normal replacementType) when replacementType <> target.ResultType ->
                Error(
                    diagnostic
                        "PureExpressionResultTypeMismatch"
                        $"The replacement recipe produces {DiagnosticText.typeRef snapshot replacementType}, but the target function returns {DiagnosticText.typeRef snapshot target.ResultType}."
                )
            | Ok _ ->
                let nodes, resultNodeId = materialize index target [] recipe
                Ok { Nodes = nodes; ResultNodeId = resultNodeId }

    /// Replaces one function's body in a proposed snapshot.
    let private withFunctionBody functionId body (snapshot: Snapshot) =
        { snapshot with
            Functions =
                snapshot.Functions
                |> List.map (fun functionEntity ->
                    if functionEntity.Id = functionId then
                        { functionEntity with Body = body }
                    else
                        functionEntity) }

    /// The review focus of one function before and after a simulated change.
    let private focusBaseAndProposed (candidate: SimulatedCandidate<'Prepared>) functionId =
        match
            SemanticQuery.focusFunction candidate.BaseSnapshot functionId,
            SemanticQuery.focusFunction candidate.ProposedSnapshot functionId
        with
        | Error error, _
        | _, Error error -> Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
        | Ok baseFocus, Ok proposedFocus -> Ok(baseFocus, proposedFocus)

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithExpressionRecipe)
        : Result<AgentExpressionStructuralChangeProposal, AgentStructuralWorkflowError> =
        simulateStructural
            workspace
            (fun baseSnapshot ->
                let index = buildSnapshotIndex baseSnapshot
                match validatePreflight request index with
                | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
                | Ok target ->
                    let newNodes, newResultId = materialize index target [] request.Replacement
                    Ok(target, { Nodes = newNodes; ResultNodeId = newResultId }))
            (fun (target, replacementBody) snapshot -> withFunctionBody target.Id replacementBody snapshot)
        |> Result.bind (fun candidate ->
            let target, replacementBody = candidate.Prepared
            let baseSnapshot = candidate.BaseSnapshot
            let proposedSnapshot = candidate.ProposedSnapshot
            match focusBaseAndProposed candidate target.Id with
            | Error error -> Error error
            | Ok(baseFocus, proposedFocus) ->
                let proposedTarget = proposedSnapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = target.Id)
                let diff =
                    { OwnerFunctionId = target.Id
                      OldResultNodeId = target.Body.ResultNodeId
                      NewResultNodeId = replacementBody.ResultNodeId
                      RemovedNodes =
                        ValueGraph.collectNodes target.Body
                        |> summarizeNodes baseSnapshot target
                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                      AddedNodes =
                        ValueGraph.collectNodes proposedTarget.Body
                        |> summarizeNodes proposedSnapshot proposedTarget }
                let review =
                    { RequestedChange = request
                      Receipt = candidate.Receipt
                      Readiness = candidate.Readiness
                      ResultingChange = diff
                      BaseFocus = baseFocus
                      ProposedFocus = proposedFocus }
                Ok(
                    AgentExpressionStructuralChangeProposal
                        { TransactionId = candidate.TransactionId
                          ProposedSnapshot = proposedSnapshot
                          Review = review }
                ))

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentExpressionStructuralChangeProposal proposal)
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

    let commit workspace proposal = publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations
    let commitWithPolicy workspace proposal policy = publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentExpressionStructuralChangeProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }

    [<RequireQualifiedAccess>]
    module Targeted =
        type Request =
            { TargetFunctionId: FunctionId
              ExpectedBaseSnapshotId: SnapshotId
              TargetNodeId: GraphNodeId
              Replacement: ExpressionRecipe }

        [<RequireQualifiedAccess>]
        type Use =
            | NodeDependency of consumerNodeId: GraphNodeId * role: ValueDependencyRole
            | GraphResult of ownerNodeId: GraphNodeId option

        type ReplacementDiff =
            { OwnerFunctionId: FunctionId
              TargetNodeId: GraphNodeId
              PreviousTarget: StructuralNodeSummary
              /// The node now at the target position (the replacement's root keeps the target
              /// node ID), so a compact review shows both the old and the new value.
              ProposedTarget: StructuralNodeSummary option
              RemovedNodes: StructuralNodeSummary list
              AddedNodes: StructuralNodeSummary list
              AddedPatternBindings: PatternBinding list
              RetainedSharedDependencies: StructuralNodeSummary list
              AffectedUses: Use list }

        type Review =
            { RequestedChange: Request
              Receipt: TransactionPreviewReceipt
              Readiness: TransactionReadiness
              ResultingChange: ReplacementDiff
              BaseFocus: FunctionFocusView
              ProposedFocus: FunctionFocusView }

        type PublishedTransaction =
            { TransactionId: TransactionId
              BaseSnapshotId: SnapshotId
              PublishedSnapshotId: SnapshotId
              Diff: ReplacementDiff
              Readiness: TransactionReadiness
              Receipt: TransactionPreviewReceipt }

        type private ProposalState =
            { TransactionId: TransactionId
              ProposedSnapshot: Snapshot
              Review: Review }

        type Proposal =
            private
            | Proposal of ProposalState

        [<RequireQualifiedAccess>]
        module Proposal =
            let private state (Proposal state) = state
            let review proposal = (state proposal).Review
            let transactionId proposal = (state proposal).TransactionId
            let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id
            /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
            let proposedSnapshot proposal = (state proposal).ProposedSnapshot

        [<RequireQualifiedAccess>]
        type CommitResult =
            | Rejected of TransactionDiagnostic list
            | Committed of AgentWorkspace * PublishedTransaction

        type private PruningPlan =
            { RemovedNodeIds: Set<GraphNodeId>
              RetainedSharedDependencyIds: Set<GraphNodeId>
              AffectedUses: Use list }

        type private Preflight =
            { Target: SemanticFunction
              TargetNode: SemanticNode
              Frames: PatternBindingFrame list
              Pruning: PruningPlan }

        let private affectedUses targetNodeId (target: SemanticFunction) =
            let nodeUses =
                ValueGraph.collectNodes target.Body
                |> List.collect (fun node ->
                    ValueDependency.ofNode node
                    |> List.choose (fun dependency ->
                        if dependency.NodeId = targetNodeId then
                            Some(Use.NodeDependency(node.Id, dependency.Role))
                        else
                            None))

            let resultUses =
                graphResultUses None target.Body
                |> List.choose (fun (resultNodeId, owner) ->
                    if resultNodeId = targetNodeId then
                        Some(Use.GraphResult owner)
                    else
                        None)

            nodeUses @ resultUses

        let private planPruning targetNodeId (targetNode: SemanticNode) (target: SemanticFunction) =
            let allNodes = ValueGraph.collectNodes target.Body
            let nodesById = allNodes |> List.map (fun node -> node.Id, node) |> Map.ofList

            let rec collectNode visited nodeId =
                if nodeId = targetNodeId || Set.contains nodeId visited then
                    visited
                else
                    match Map.tryFind nodeId nodesById with
                    | None -> visited
                    | Some node ->
                        let visited = Set.add nodeId visited
                        let visited =
                            SemanticNodeShape.directValueReferences node
                            |> List.fold collectNode visited
                        SemanticNodeShape.nestedGraphs node |> List.fold collectGraph visited

            and collectGraph visited (graph: ValueGraph) =
                graph.Nodes
                |> List.fold (fun state node -> collectNode state node.Id) visited

            let candidates =
                let direct =
                    SemanticNodeShape.directValueReferences targetNode
                    |> List.fold collectNode Set.empty
                SemanticNodeShape.nestedGraphs targetNode |> List.fold collectGraph direct

            let targetOwned =
                SemanticNodeShape.nestedGraphs targetNode
                |> List.collect ValueGraph.collectNodes
                |> List.map (fun node -> node.Id)
                |> Set.ofList

            let externalNodeSeeds =
                allNodes
                |> List.filter (fun node ->
                    node.Id <> targetNodeId && not (Set.contains node.Id candidates))
                |> List.collect SemanticNodeShape.directValueReferences
                |> List.filter (fun nodeId -> Set.contains nodeId candidates)
                |> Set.ofList

            let externalResultSeeds =
                graphResultUses None target.Body
                |> List.choose (fun (resultNodeId, owner) ->
                    if not (Set.contains resultNodeId candidates) then
                        None
                    else
                        match owner with
                        | None -> Some resultNodeId
                        | Some ownerNodeId
                            when ownerNodeId <> targetNodeId
                                 && not (Set.contains ownerNodeId candidates) ->
                            Some resultNodeId
                        | Some _ -> None)
                |> Set.ofList

            let seeds = Set.union externalNodeSeeds externalResultSeeds

            let rec protect retained nodeId =
                if not (Set.contains nodeId candidates) || Set.contains nodeId retained then
                    retained
                else
                    match Map.tryFind nodeId nodesById with
                    | None -> retained
                    | Some node ->
                        let retained = Set.add nodeId retained
                        let retained =
                            SemanticNodeShape.directValueReferences node
                            |> List.fold protect retained
                        SemanticNodeShape.nestedGraphs node
                        |> List.collect ValueGraph.collectNodes
                        |> List.map (fun nested -> nested.Id)
                        |> List.fold protect retained

            let retained = seeds |> Set.fold protect Set.empty
            let removed =
                Set.union targetOwned (Set.difference candidates retained)
            let retainedShared =
                Set.difference retained targetOwned

            { RemovedNodeIds = removed
              RetainedSharedDependencyIds = retainedShared
              AffectedUses = affectedUses targetNodeId target }

        let private validatePreflight
            (request: Request)
            (index: SnapshotIndex)
            : Result<Preflight, TransactionDiagnostic> =
            let baseSnapshot = index.Snapshot
            if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
                Error(
                    diagnostic
                        "StructuralExpectedBaseMismatch"
                        "The structural request targets a different base snapshot."
                )
            else
                match
                    baseSnapshot.Functions
                    |> List.tryFind (fun functionEntity -> functionEntity.Id = request.TargetFunctionId)
                with
                | None -> Error(diagnostic "StructuralTargetNotFound" "The target function does not exist.")
                | Some target ->
                    match tryFindTarget [] request.TargetNodeId target.Body with
                    | None ->
                        Error(
                            diagnostic
                                "TargetedExpressionTargetNotFound"
                                "The target node does not exist in the target function."
                        )
                    | Some(targetNode, frames) ->
                        match validateRecipeBounds recipeChildren request.Replacement with
                        | Error error -> Error error
                        | Ok() ->
                            let resolveType = SemanticFacts.createNodeCompletionResolver baseSnapshot target
                            match resolveType request.TargetNodeId with
                            | None ->
                                Error(
                                    diagnostic
                                        "TargetedExpressionTargetTypeUnavailable"
                                        "The target node type could not be derived from the validated base snapshot."
                                )
                            | Some targetType ->
                                match
                                    inferTypeWithFrames (Some "replacement")
                                        index
                                        target
                                        (bindingTypeFrames frames)
                                        request.Replacement
                                with
                                | Error error -> Error error
                                | Ok(GraphCompletion.Normal replacementType) when targetType <> GraphCompletion.Aborts && GraphCompletion.Normal replacementType <> targetType ->
                                    Error(
                                        diagnostic
                                            "TargetedExpressionTypeMismatch"
                                            $"The replacement recipe produces {DiagnosticText.typeRef baseSnapshot replacementType}, but the target node produces {DiagnosticText.completion baseSnapshot targetType}."
                                    )
                                | Ok GraphCompletion.Aborts when not (List.isEmpty (valueConsumers request.TargetNodeId target)) ->
                                    let consumer = valueConsumers request.TargetNodeId target |> List.head
                                    Error(
                                        diagnostic
                                            "TargetedExpressionAbruptValueUse"
                                            $"The replacement recipe always aborts, but node '{StableId.value consumer.Id}' uses the target node as a value; an abrupt replacement is valid only where the target is not used as a value, such as a branch or function result."
                                    )
                                | Ok GraphCompletion.Aborts when
                                    (match request.Replacement with
                                     | ExpressionRecipe.Abort _ -> true
                                     | _ -> false)
                                    && not (isGraphResult request.TargetNodeId target)
                                    ->
                                    Error(
                                        diagnostic
                                            "TargetedExpressionAbortNotGraphResult"
                                            "The replacement recipe is an abort, but the target node is not the result of its graph; an abort may replace only a branch or function result."
                                    )
                                | Ok _ ->
                                    let pruning = planPruning request.TargetNodeId targetNode target
                                    match
                                        baseSnapshot.Obligations
                                        |> List.tryFind (fun obligation ->
                                            obligation.CallNodeId = request.TargetNodeId
                                            || Set.contains obligation.CallNodeId pruning.RemovedNodeIds)
                                    with
                                    | Some obligation ->
                                        Error(
                                            diagnostic
                                                "TargetedExpressionReferencedByObligation"
                                                $"Obligation '{StableId.value obligation.Id}' targets the expression being replaced or a node that replacement would remove."
                                        )
                                    | None ->
                                        Ok
                                            { Target = target
                                              TargetNode = targetNode
                                              Frames = frames
                                              Pruning = pruning }

        let simulate
            (workspace: AgentWorkspace)
            (request: Request)
            : Result<Proposal, AgentStructuralWorkflowError> =
            simulateStructural
                workspace
                (fun baseSnapshot ->
                    let index = buildSnapshotIndex baseSnapshot
                    match validatePreflight request index with
                    | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
                    | Ok preflight ->
                        let replacementNodes =
                            materializeReplacement
                                index
                                preflight.Target
                                preflight.Frames
                                request.TargetNodeId
                                request.Replacement

                        let replacementBody =
                            preflight.Target.Body
                            |> replaceTarget request.TargetNodeId replacementNodes
                            |> pruneNodes preflight.Pruning.RemovedNodeIds

                        Ok(preflight, replacementBody))
                (fun (preflight, replacementBody) snapshot -> withFunctionBody preflight.Target.Id replacementBody snapshot)
            |> Result.bind (fun candidate ->
                let preflight, _ = candidate.Prepared
                let baseSnapshot = candidate.BaseSnapshot
                let proposedSnapshot = candidate.ProposedSnapshot
                let transactionId = candidate.TransactionId
                let receipt = candidate.Receipt
                let readiness = candidate.Readiness
                match focusBaseAndProposed candidate preflight.Target.Id with
                | Error error -> Error error
                | Ok(baseFocus, proposedFocus) ->
                    let proposedTarget =
                        proposedSnapshot.Functions
                        |> List.find (fun functionEntity ->
                            functionEntity.Id = preflight.Target.Id)

                    let baseNodes = ValueGraph.collectNodes preflight.Target.Body
                    let baseNodeIds =
                        baseNodes |> List.map (fun node -> node.Id) |> Set.ofList
                    let proposedNodes = ValueGraph.collectNodes proposedTarget.Body
                    let previousTarget =
                        summarizeNodes baseSnapshot preflight.Target [ preflight.TargetNode ]
                        |> List.exactlyOne
                    let proposedTargetSummary =
                        proposedNodes
                        |> List.filter (fun node -> node.Id = request.TargetNodeId)
                        |> summarizeNodes proposedSnapshot proposedTarget
                        |> List.tryHead
                    let removedNodes =
                        baseNodes
                        |> List.filter (fun node ->
                            Set.contains node.Id preflight.Pruning.RemovedNodeIds)
                        |> summarizeNodes baseSnapshot preflight.Target
                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                    let addedNodes =
                        proposedNodes
                        |> List.filter (fun node -> not (Set.contains node.Id baseNodeIds))
                        |> summarizeNodes proposedSnapshot proposedTarget
                    let baseBindingIds =
                        SemanticFacts.collectPatternBindings preflight.Target.Body
                        |> List.map (fun binding -> binding.Id)
                        |> Set.ofList
                    let addedBindings =
                        SemanticFacts.collectPatternBindings proposedTarget.Body
                        |> List.filter (fun binding ->
                            not (Set.contains binding.Id baseBindingIds))
                        |> List.sortBy (fun binding -> StableId.value binding.Id)
                    let retainedShared =
                        baseNodes
                        |> List.filter (fun node ->
                            Set.contains
                                node.Id
                                preflight.Pruning.RetainedSharedDependencyIds)
                        |> summarizeNodes baseSnapshot preflight.Target
                        |> List.sortBy (fun node -> StableId.value node.NodeId)

                    let diff =
                        { OwnerFunctionId = preflight.Target.Id
                          TargetNodeId = request.TargetNodeId
                          PreviousTarget = previousTarget
                          ProposedTarget = proposedTargetSummary
                          RemovedNodes = removedNodes
                          AddedNodes = addedNodes
                          AddedPatternBindings = addedBindings
                          RetainedSharedDependencies = retainedShared
                          AffectedUses = preflight.Pruning.AffectedUses }
                    let review =
                        { RequestedChange = request
                          Receipt = receipt
                          Readiness = readiness
                          ResultingChange = diff
                          BaseFocus = baseFocus
                          ProposedFocus = proposedFocus }
                    Ok(
                        Proposal
                            { TransactionId = transactionId
                              ProposedSnapshot = proposedSnapshot
                              Review = review }
                    ))

        let private publishWithPolicy
            (workspace: AgentWorkspace)
            (Proposal proposal)
            (policy: CommitPolicy)
            : CommitResult =
            let publicationDiff: FunctionBodyReplacementDiff =
                { OwnerFunctionId = proposal.Review.ResultingChange.OwnerFunctionId
                  OldResultNodeId = proposal.Review.BaseFocus.Function.Body.ResultNodeId
                  NewResultNodeId = proposal.Review.ProposedFocus.Function.Body.ResultNodeId
                  RemovedNodes = proposal.Review.ResultingChange.RemovedNodes
                  AddedNodes = proposal.Review.ResultingChange.AddedNodes }

            match
                publishReviewedWithPolicy
                    workspace
                    proposal.TransactionId
                    proposal.ProposedSnapshot
                    publicationDiff
                    proposal.Review.Readiness
                    proposal.Review.Receipt
                    policy
            with
            | AgentStructuralCommitResult.Rejected diagnostics -> CommitResult.Rejected diagnostics
            | AgentStructuralCommitResult.Committed(nextWorkspace, _) ->
                CommitResult.Committed(
                    nextWorkspace,
                    { TransactionId = proposal.TransactionId
                      BaseSnapshotId = proposal.Review.Receipt.BaseSnapshotId
                      PublishedSnapshotId = proposal.ProposedSnapshot.Id
                      Diff = proposal.Review.ResultingChange
                      Readiness = proposal.Review.Readiness
                      Receipt = proposal.Review.Receipt }
                )

        let commit workspace proposal =
            publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

        let commitWithPolicy workspace proposal policy =
            publishWithPolicy workspace proposal policy

        let reject
            (workspace: AgentWorkspace)
            (Proposal proposal)
            : AgentWorkspace * AgentStructuralRejectionReceipt =
            workspace,
            { TransactionId = proposal.TransactionId
              RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }

    [<RequireQualifiedAccess>]
    module FunctionCreation =
        type ParameterIntent =
            { DisplayName: string
              Type: TypeRef }

        type Request =
            { ExpectedBaseSnapshotId: SnapshotId
              DisplayName: string
              /// The function's type parameters in declared order, with request-local identities
              /// (wire: `"typeParameters": ["T"]`, each `{ "kind": "typeParameter", "name": "T" }` in a
              /// type naming one). `None` derives the list from the parameter types in
              /// first-appearance order, the rule that held before lists were declared.
              TypeParameters: (TypeParameterId * string) list option
              Parameters: ParameterIntent list
              ResultType: TypeRef
              RequiredEffects: EffectId list
              Body: ExpressionRecipe }

        /// The type parameter list a request's parameter types imply: first-appearance order, each
        /// request-local identity spelled as its name.
        let derivedTypeParameters (parameters: ParameterIntent list): (TypeParameterId * string) list =
            TypeParameters.derived (parameters |> List.map (fun parameter -> parameter.Type))
            |> List.map (fun id -> id, StableId.value id)

        /// The request's declared type parameters, or the derived list when it gives none.
        let declaredTypeParameters (request: Request) =
            request.TypeParameters |> Option.defaultWith (fun () -> derivedTypeParameters request.Parameters)

        type Review =
            { RequestedChange: Request
              Receipt: TransactionPreviewReceipt
              Readiness: TransactionReadiness
              CreatedFunction: SemanticFunction
              MaterializedBody: StructuralNodeSummary list
              ProposedFocus: FunctionFocusView }

        type PublishedTransaction =
            { TransactionId: TransactionId
              BaseSnapshotId: SnapshotId
              PublishedSnapshotId: SnapshotId
              CreatedFunctionId: FunctionId
              Readiness: TransactionReadiness
              Receipt: TransactionPreviewReceipt }

        type private Preflight =
            { CanonicalRequiredEffects: EffectId list
              TypeParameters: (TypeParameterId * string) list
              Owner: ExpressionOwnerContext }

        type private ProposalState =
            { TransactionId: TransactionId
              ProposedSnapshot: Snapshot
              Review: Review }

        type Proposal =
            private
            | Proposal of ProposalState

        [<RequireQualifiedAccess>]
        module Proposal =
            let private state (Proposal state) = state
            let review proposal = (state proposal).Review
            let transactionId proposal = (state proposal).TransactionId
            let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id
            /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
            let proposedSnapshot proposal = (state proposal).ProposedSnapshot

        [<RequireQualifiedAccess>]
        type CommitResult =
            | Rejected of TransactionDiagnostic list
            | Committed of AgentWorkspace * PublishedTransaction

        /// The declared list (explicit or derived) checked as the validator will check the function:
        /// nonblank distinct names, distinct identities, every type parameter the signature or body
        /// mentions declared, and every declared one mentioned by a parameter type, so a call can
        /// derive it by matching.
        let private validateTypeParameters (request: Request) =
            let declared = declaredTypeParameters request
            let names = declared |> Map.ofList
            let parameterTypes = request.Parameters |> List.map (fun parameter -> parameter.Type)
            let mentioned =
                TypeParameters.occurringIn (request.ResultType :: parameterTypes @ ExpressionRecipeTypes.collect request.Body)
            let introduced = TypeParameters.derived parameterTypes |> Set.ofList
            let spell id = TypeParameters.nameIn names id
            if declared |> List.exists (fun (_, name) -> System.String.IsNullOrWhiteSpace name) then
                Error(diagnostic "FunctionCreationTypeParameterNameBlank" "Type parameter names must not be blank.")
            else
                match declared |> List.countBy snd |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(name, _) -> Error(diagnostic "FunctionCreationDuplicateTypeParameter" $"Type parameter '{name}' is declared more than once.")
                | None ->
                    match declared |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                    | Some(id, _) -> Error(diagnostic "FunctionCreationDuplicateTypeParameter" $"Type parameter '{spell id}' is declared more than once.")
                    | None ->
                        match mentioned |> List.tryFind (fun id -> not (Map.containsKey id names)) with
                        | Some id ->
                            Error(
                                diagnostic
                                    "FunctionCreationTypeParameterNotDeclared"
                                    $"The function uses type parameter '{spell id}', which its typeParameters list does not declare."
                            )
                        | None ->
                            match declared |> List.tryFind (fun (id, _) -> not (Set.contains id introduced)) with
                            | Some(_, name) ->
                                Error(
                                    diagnostic
                                        "FunctionCreationTypeParameterNotInferable"
                                        $"Type parameter '{name}' is mentioned by no parameter type, so a call could not determine it; a type parameter must appear in at least one parameter type."
                                )
                            | None -> Ok declared

        let private validatePreflight (requireUniqueName: bool) (request: Request) (index: SnapshotIndex) =
            let baseSnapshot = index.Snapshot
            if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
                Error(
                    diagnostic
                        "StructuralExpectedBaseMismatch"
                        "The function-creation request targets a different base snapshot."
                )
            elif System.String.IsNullOrWhiteSpace request.DisplayName then
                Error(
                    diagnostic
                        "FunctionCreationDisplayNameBlank"
                        "Function display name must not be blank."
                )
            elif requireUniqueName && baseSnapshot.Functions |> List.exists (fun item -> item.DisplayName = request.DisplayName) then
                Error(
                    diagnostic
                        "FunctionCreationDuplicateDisplayName"
                        $"A function named '{request.DisplayName}' already exists; function display names must be unique."
                )
            else
                match
                    request.Parameters
                    |> List.mapi (fun index parameter -> index, parameter)
                    |> List.tryFind (fun (_, parameter) ->
                        System.String.IsNullOrWhiteSpace parameter.DisplayName)
                with
                | Some(index, _) ->
                    Error(
                        diagnostic
                            "FunctionCreationParameterDisplayNameBlank"
                            $"Parameter at index {index} must have a nonblank display name."
                    )
                | None ->
                    match validateTypeParameters request with
                    | Error error -> Error error
                    | Ok typeParameters ->
                    let typeRefs =
                        request.ResultType
                        :: (request.Parameters |> List.map (fun parameter -> parameter.Type))

                    let rec validateTypes = function
                        | [] -> Ok()
                        | typeRef :: rest ->
                            match validateTypeRef index typeRef with
                            | Error error -> Error error
                            | Ok() -> validateTypes rest

                    match validateTypes typeRefs with
                    | Error error -> Error error
                    | Ok() ->
                        match
                            request.RequiredEffects |> tryFindDuplicate id
                        with
                        | Some effectId ->
                            Error(
                                diagnostic
                                    "FunctionCreationDuplicateRequiredEffect"
                                    $"Required effect '{StableId.value effectId}' appears more than once."
                            )
                        | None ->
                            let existingEffects =
                                baseSnapshot.Effects
                                |> List.map (fun effect -> effect.Id)
                                |> Set.ofList

                            match
                                request.RequiredEffects
                                |> List.tryFind (fun effectId ->
                                    not (Set.contains effectId existingEffects))
                            with
                            | Some effectId ->
                                Error(
                                    diagnostic
                                        "FunctionCreationRequiredEffectNotFound"
                                        $"Required effect '{StableId.value effectId}' does not exist."
                                )
                            | None ->
                                let canonicalEffects =
                                    request.RequiredEffects
                                    |> List.sortBy StableId.value
                                let slots =
                                    request.Parameters
                                    |> List.map (fun parameter ->
                                        { Type = parameter.Type
                                          ParameterId = None
                                          DisplayName = parameter.DisplayName })
                                let owner =
                                    { ParametersById = Map.empty
                                      ParameterSlots = Some slots
                                      RequiredEffects = canonicalEffects
                                      ResultType = request.ResultType
                                      TypeParameters = typeParameters
                                      SelfFunctionId = None
                                      SelfParameters = slots
                                      Description = "the function being created" }

                                match validateRecipeBounds recipeChildren request.Body with
                                | Error error -> Error error
                                | Ok() ->
                                    match inferTypeWithContext index owner (InferenceCache()) [] (if requireUniqueName then Some "body" else None) request.Body with
                                    | Error error -> Error error
                                    | Ok(GraphCompletion.Normal bodyType) when bodyType <> request.ResultType ->
                                        Error(
                                            diagnostic
                                                "FunctionCreationResultTypeMismatch"
                                                $"The function body produces {DiagnosticText.typeRef baseSnapshot bodyType}, but the requested result type is {DiagnosticText.typeRef baseSnapshot request.ResultType}."
                                        )
                                    | Ok _ ->
                                        Ok
                                            { CanonicalRequiredEffects = canonicalEffects
                                              TypeParameters = typeParameters
                                              Owner = owner }

        /// A new function owns its type parameters: every declared type parameter receives a fresh
        /// system-owned identity, keeping its display name and declared position. A request that
        /// declares none gets the derived list first, so the list is explicit from here on.
        let private withFreshTypeParameters (request: Request): Request =
            let declared = declaredTypeParameters request
            if List.isEmpty declared then { request with TypeParameters = Some [] }
            else
                let fresh = declared |> List.map (fun (id, name) -> id, (StableIdAllocator.newTypeParameterId (), name))
                let substitution = fresh |> List.map (fun (id, (freshId, _)) -> id, TypeRef.TypeParameter freshId) |> Map.ofList
                let mapType = TypeParameters.substitute substitution
                { request with
                    TypeParameters = Some(fresh |> List.map snd)
                    Parameters = request.Parameters |> List.map (fun parameter -> { parameter with Type = mapType parameter.Type })
                    ResultType = mapType request.ResultType
                    Body = ExpressionRecipeTypes.map mapType request.Body }

        let private simulateWith
            (requireUniqueName: bool)
            (workspace: AgentWorkspace)
            (request: Request)
            : Result<Proposal, AgentStructuralWorkflowError> =
            // Undeclared or uninferable type parameters are reported on the request's own names.
            match validateTypeParameters request with
            | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Ok _ ->
            let request = withFreshTypeParameters request
            simulateStructural
                workspace
                (fun baseSnapshot ->
                    let index = buildSnapshotIndex baseSnapshot
                    match validatePreflight requireUniqueName request index with
                    | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
                    | Ok preflight ->
                        let functionId = StableIdAllocator.newFunctionId ()
                        let parameters: SemanticParameter list =
                            request.Parameters
                            |> List.map (fun parameter ->
                                { Id = StableIdAllocator.newParameterId ()
                                  DisplayName = parameter.DisplayName
                                  Type = parameter.Type })
                        let slots =
                            List.map2
                                (fun (parameter: SemanticParameter) (intent: ParameterIntent) ->
                                    { Type = intent.Type
                                      ParameterId = Some parameter.Id
                                      DisplayName = intent.DisplayName })
                                parameters
                                request.Parameters
                        let materializationOwner =
                            { preflight.Owner with
                                ParameterSlots = Some slots
                                SelfFunctionId = Some functionId
                                SelfParameters = slots }
                        let bodyNodes, bodyResultId =
                            materializeWithContext
                                index
                                materializationOwner
                                (InferenceCache())
                                []
                                request.Body
                        let createdFunction: SemanticFunction =
                            { Id = functionId
                              DisplayName = request.DisplayName
                              TypeParameters = preflight.TypeParameters
                              Parameters = parameters
                              ResultType = request.ResultType
                              RequiredEffects = preflight.CanonicalRequiredEffects
                              Body =
                                { Nodes = bodyNodes
                                  ResultNodeId = bodyResultId } }
                        Ok createdFunction)
                (fun createdFunction snapshot -> { snapshot with Functions = snapshot.Functions @ [ createdFunction ] })
            |> Result.bind (fun candidate ->
                let createdFunction = candidate.Prepared
                match SemanticQuery.focusFunction candidate.ProposedSnapshot createdFunction.Id with
                | Error error ->
                    Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                | Ok proposedFocus ->
                    let bodySummary =
                        ValueGraph.collectNodes createdFunction.Body
                        |> summarizeNodes candidate.ProposedSnapshot createdFunction
                    let review =
                        { RequestedChange = request
                          Receipt = candidate.Receipt
                          Readiness = candidate.Readiness
                          CreatedFunction = createdFunction
                          MaterializedBody = bodySummary
                          ProposedFocus = proposedFocus }
                    Ok(
                        Proposal
                            { TransactionId = candidate.TransactionId
                              ProposedSnapshot = candidate.ProposedSnapshot
                              Review = review }
                    ))

        /// Creates a new function; its display name must not already name a function,
        /// since by-name requests resolve exactly one declaration.
        let simulate workspace request = simulateWith true workspace request

        /// Recreates an existing function's signature under its current name, as signature
        /// migrations do on a temporary copy; the name is intentionally shared.
        let internal simulateReplacement workspace request = simulateWith false workspace request

        let private publishWithPolicy
            (workspace: AgentWorkspace)
            (Proposal proposal)
            (policy: CommitPolicy)
            : CommitResult =
            // The established structural publisher uses its body diff only as a
            // publication payload. Creation keeps its own review facts and discards
            // this transport adapter after stale/fingerprint/readiness validation.
            let transportDiff: FunctionBodyReplacementDiff =
                { OwnerFunctionId = proposal.Review.CreatedFunction.Id
                  OldResultNodeId = proposal.Review.CreatedFunction.Body.ResultNodeId
                  NewResultNodeId = proposal.Review.CreatedFunction.Body.ResultNodeId
                  RemovedNodes = []
                  AddedNodes = proposal.Review.MaterializedBody }

            match
                publishReviewedWithPolicy
                    workspace
                    proposal.TransactionId
                    proposal.ProposedSnapshot
                    transportDiff
                    proposal.Review.Readiness
                    proposal.Review.Receipt
                    policy
            with
            | AgentStructuralCommitResult.Rejected diagnostics ->
                CommitResult.Rejected diagnostics
            | AgentStructuralCommitResult.Committed(nextWorkspace, _) ->
                CommitResult.Committed(
                    nextWorkspace,
                    { TransactionId = proposal.TransactionId
                      BaseSnapshotId = proposal.Review.Receipt.BaseSnapshotId
                      PublishedSnapshotId = proposal.ProposedSnapshot.Id
                      CreatedFunctionId = proposal.Review.CreatedFunction.Id
                      Readiness = proposal.Review.Readiness
                      Receipt = proposal.Review.Receipt }
                )

        let commit workspace proposal =
            publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

        let commitWithPolicy workspace proposal policy =
            publishWithPolicy workspace proposal policy

        let reject
            (workspace: AgentWorkspace)
            (Proposal proposal)
            : AgentWorkspace * AgentStructuralRejectionReceipt =
            workspace,
            { TransactionId = proposal.TransactionId
              RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
