namespace NyamNyam.Core

type AgentParameterTypeCallerRewrite =
    { CallerFunctionId: FunctionId
      Replacement: ExpressionRecipe }

type AgentParameterTypeMigrationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      TargetFunctionId: FunctionId
      TargetParameterId: ParameterId
      NewParameterType: TypeRef
      TargetReplacement: ExpressionRecipe
      CallerRewrites: AgentParameterTypeCallerRewrite list }

type AgentParameterTypeChange =
    { FunctionId: FunctionId
      ParameterId: ParameterId
      OldType: TypeRef
      NewType: TypeRef }

type AgentParameterTypeMigrationDiff =
    { ParameterChange: AgentParameterTypeChange
      AffectedCallSites: QueryCallSite list
      BodyRewrites: FunctionBodyReplacementDiff list }

type AgentParameterTypeMigrationReview =
    { RequestedChange: AgentParameterTypeMigrationRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: AgentParameterTypeMigrationDiff }

type AgentParameterTypeMigrationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: AgentParameterTypeMigrationDiff
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private ParameterTypeMigrationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentParameterTypeMigrationReview }

type AgentParameterTypeMigrationProposal =
    private
    | AgentParameterTypeMigrationProposal of ParameterTypeMigrationProposalState

[<RequireQualifiedAccess>]
module AgentParameterTypeMigrationProposal =
    let private state (AgentParameterTypeMigrationProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
type AgentParameterTypeMigrationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentParameterTypeMigrationPublishedTransaction

[<RequireQualifiedAccess>]
module AgentParameterTypeMigrationWorkflow =
    open StructuralTransactionInternals

    type private Preflight =
        { Target: SemanticFunction
          Parameter: SemanticParameter
          CallerSites: QueryCallSite list
          Callers: SemanticFunction list
          RewritesByCaller: Map<FunctionId, ExpressionRecipe> }

    type private TemporaryTarget =
        { Workspace: AgentWorkspace
          Function: SemanticFunction
          OriginalToTemporaryParameters: Map<ParameterId, ParameterId>
          TemporaryToOriginalParameters: Map<ParameterId, ParameterId> }

    let private reject code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    let private wrapStructuralError code context = function
        | AgentStructuralWorkflowError.SimulationRejected (inner :: _) ->
            reject code $"{context}: {inner.Code}: {inner.Message}"
        | error -> Error error

    let private functionById (snapshot: Snapshot) functionId =
        snapshot.Functions
        |> List.tryFind (fun functionEntity -> functionEntity.Id = functionId)

    let private nodeById (functionEntity: SemanticFunction) nodeId =
        ValueGraph.collectNodes functionEntity.Body
        |> List.tryFind (fun node -> node.Id = nodeId)

    let private rewriteForTemporaryFunction
        (ownerParameters: SemanticParameter list)
        (targetMapping: (FunctionId * FunctionId * Map<ParameterId, ParameterId>) option)
        recipe
        =
        let ownerSlots =
            ownerParameters
            |> List.mapi (fun index parameter -> parameter.Id, index)
            |> Map.ofList

        let rec rewrite recipe =
            let rewritePairs values =
                let rec loop collected = function
                    | [] -> Ok(List.rev collected)
                    | (id, value) :: rest ->
                        match rewrite value with
                        | Error error -> Error error
                        | Ok mapped -> loop ((id, mapped) :: collected) rest
                loop [] values

            match recipe with
            | ExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId ownerSlots with
                | Some index -> Ok(ExpressionRecipe.ParameterSlot index)
                | None -> Ok recipe
            | ExpressionRecipe.ParameterSlot _ ->
                Error(
                    diagnostic
                        "ParameterTypeMigrationParameterSlotUnavailable"
                        "Migration recipes must reference existing parameters by stable ID."
                )
            | ExpressionRecipe.PatternReference _
            | ExpressionRecipe.StringLiteral _
            | ExpressionRecipe.BoolLiteral _
            | ExpressionRecipe.UnitLiteral -> Ok recipe
            | ExpressionRecipe.StringContains(value, substring) ->
                match rewrite value with
                | Error error -> Error error
                | Ok mappedValue ->
                    rewrite substring
                    |> Result.map (fun mappedSubstring ->
                        ExpressionRecipe.StringContains(mappedValue, mappedSubstring))
            | ExpressionRecipe.ProductConstruct(typeId, fields) ->
                rewritePairs fields
                |> Result.map (fun mapped ->
                    ExpressionRecipe.ProductConstruct(typeId, mapped))
            | ExpressionRecipe.FieldAccess(source, fieldId) ->
                rewrite source
                |> Result.map (fun mapped ->
                    ExpressionRecipe.FieldAccess(mapped, fieldId))
            | ExpressionRecipe.UnionConstruct(variantId, fields) ->
                rewritePairs fields
                |> Result.map (fun mapped ->
                    ExpressionRecipe.UnionConstruct(variantId, mapped))
            | ExpressionRecipe.DirectCall(functionId, arguments) ->
                rewritePairs arguments
                |> Result.map (fun mappedArguments ->
                    match targetMapping with
                    | Some(originalTargetId, temporaryTargetId, parameterMap)
                        when functionId = originalTargetId ->
                        let remappedArguments =
                            mappedArguments
                            |> List.map (fun (parameterId, value) ->
                                (Map.tryFind parameterId parameterMap
                                 |> Option.defaultValue parameterId),
                                value)
                        ExpressionRecipe.DirectCall(
                            temporaryTargetId,
                            remappedArguments
                        )
                    | _ ->
                        ExpressionRecipe.DirectCall(functionId, mappedArguments))
            | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
                match rewrite capabilityValue with
                | Error error -> Error error
                | Ok mappedCapability ->
                    rewritePairs arguments
                    |> Result.map (fun mappedArguments ->
                        ExpressionRecipe.CapabilityInvoke(
                            mappedCapability,
                            operationId,
                            mappedArguments
                        ))
            | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
                match rewrite condition with
                | Error error -> Error error
                | Ok mappedCondition ->
                    match rewrite whenTrue with
                    | Error error -> Error error
                    | Ok mappedTrue ->
                        rewrite whenFalse
                        |> Result.map (fun mappedFalse ->
                            ExpressionRecipe.Conditional(
                                mappedCondition,
                                mappedTrue,
                                mappedFalse
                            ))
            | ExpressionRecipe.MatchUnion(input, arms) ->
                match rewrite input with
                | Error error -> Error error
                | Ok mappedInput ->
                    rewritePairs arms
                    |> Result.map (fun mappedArms ->
                        ExpressionRecipe.MatchUnion(mappedInput, mappedArms))
            | unsupported ->
                Error(
                    diagnostic
                        "ParameterTypeMigrationRecipeUnsupported"
                        $"This bounded migration does not support recipe case '{unsupported.GetType().Name}' outside the selected M4 workload."
                )

        rewrite recipe

    let private functionCreationRequest
        (workspace: AgentWorkspace)
        (functionEntity: SemanticFunction)
        (parameterTypes: Map<ParameterId, TypeRef>)
        body
        : AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        let parameters: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent list =
            functionEntity.Parameters
            |> List.map (fun parameter ->
                { DisplayName = parameter.DisplayName
                  Type =
                    Map.tryFind parameter.Id parameterTypes
                    |> Option.defaultValue parameter.Type })

        { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId workspace
          DisplayName = functionEntity.DisplayName
          Parameters = parameters
          ResultType = functionEntity.ResultType
          RequiredEffects = functionEntity.RequiredEffects
          Body = body }

    let private materializeTemporaryTarget
        workspace
        (prepared: Preflight)
        newParameterType
        targetRecipe
        =
        match
            rewriteForTemporaryFunction
                prepared.Target.Parameters
                None
                targetRecipe
        with
        | Error error ->
            reject
                "ParameterTypeMigrationTargetRewriteInvalid"
                $"{error.Code}: {error.Message}"
        | Ok temporaryRecipe ->
            let creationRequest =
                functionCreationRequest
                    workspace
                    prepared.Target
                    (Map.ofList [ prepared.Parameter.Id, newParameterType ])
                    temporaryRecipe

            match
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate
                    workspace
                    creationRequest
            with
            | Error error ->
                wrapStructuralError
                    "ParameterTypeMigrationTargetRewriteInvalid"
                    "The migrated target body is invalid"
                    error
            | Ok proposal ->
                let created =
                    proposal
                    |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
                    |> fun review -> review.CreatedFunction

                match
                    AgentExpressionStructuralWorkflow.FunctionCreation.commitWithPolicy
                        workspace
                        proposal
                        CommitPolicy.AllowBlockingObligations
                with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics ->
                    Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(nextWorkspace, _) ->
                    let originalToTemporary =
                        List.map2
                            (fun (original: SemanticParameter) (temporary: SemanticParameter) ->
                                original.Id, temporary.Id)
                            prepared.Target.Parameters
                            created.Parameters
                        |> Map.ofList
                    let temporaryToOriginal =
                        originalToTemporary
                        |> Map.toList
                        |> List.map (fun (originalId, temporaryId) ->
                            temporaryId, originalId)
                        |> Map.ofList
                    Ok
                        { Workspace = nextWorkspace
                          Function = created
                          OriginalToTemporaryParameters = originalToTemporary
                          TemporaryToOriginalParameters = temporaryToOriginal }

    let private materializeTemporaryCaller
        (temporaryTarget: TemporaryTarget)
        targetFunctionId
        (caller: SemanticFunction)
        replacement
        =
        match
            rewriteForTemporaryFunction
                caller.Parameters
                (Some(
                    targetFunctionId,
                    temporaryTarget.Function.Id,
                    temporaryTarget.OriginalToTemporaryParameters
                ))
                replacement
        with
        | Error error ->
            reject
                "ParameterTypeMigrationCallerRewriteInvalid"
                $"Caller '{StableId.value caller.Id}' rewrite is invalid: {error.Code}: {error.Message}"
        | Ok temporaryRecipe ->
            let creationRequest =
                functionCreationRequest
                    temporaryTarget.Workspace
                    caller
                    Map.empty
                    temporaryRecipe

            match
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate
                    temporaryTarget.Workspace
                    creationRequest
            with
            | Error error ->
                wrapStructuralError
                    "ParameterTypeMigrationCallerRewriteInvalid"
                    $"Caller '{StableId.value caller.Id}' rewrite is invalid"
                    error
            | Ok proposal ->
                proposal
                |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
                |> fun review -> Ok review.CreatedFunction

    let rec private remapGraph
        (ownerParameterMap: Map<ParameterId, ParameterId>)
        (temporaryTarget: TemporaryTarget)
        targetFunctionId
        (graph: ValueGraph)
        =
        let mapOwnerParameter parameterId =
            Map.tryFind parameterId ownerParameterMap
            |> Option.defaultValue parameterId

        let mapTargetParameter parameterId =
            Map.tryFind
                parameterId
                temporaryTarget.TemporaryToOriginalParameters
            |> Option.defaultValue parameterId

        let mapNode (node: SemanticNode) =
            let kind =
                match node.Kind with
                | SemanticNodeKind.ParameterReference parameterId ->
                    SemanticNodeKind.ParameterReference(
                        mapOwnerParameter parameterId
                    )
                | SemanticNodeKind.DirectCall(functionId, arguments)
                    when functionId = temporaryTarget.Function.Id ->
                    SemanticNodeKind.DirectCall(
                        targetFunctionId,
                        arguments
                        |> List.map (fun argument ->
                            { argument with
                                TargetParameterId =
                                    mapTargetParameter
                                        argument.TargetParameterId })
                    )
                | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                    SemanticNodeKind.Conditional(
                        conditionNodeId,
                        remapGraph
                            ownerParameterMap
                            temporaryTarget
                            targetFunctionId
                            whenTrue,
                        remapGraph
                            ownerParameterMap
                            temporaryTarget
                            targetFunctionId
                            whenFalse
                    )
                | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                    SemanticNodeKind.MatchUnion(
                        inputNodeId,
                        arms
                        |> List.map (fun arm ->
                            { arm with
                                Body =
                                    remapGraph
                                        ownerParameterMap
                                        temporaryTarget
                                        targetFunctionId
                                        arm.Body })
                    )
                | other -> other
            { node with Kind = kind }

        { graph with Nodes = graph.Nodes |> List.map mapNode }

    let private validateCallSiteKind
        (snapshot: Snapshot)
        (site: QueryCallSite)
        =
        match functionById snapshot site.CallerFunctionId with
        | None ->
            reject
                "ParameterTypeMigrationCallerNotFound"
                $"Caller '{StableId.value site.CallerFunctionId}' does not exist."
        | Some caller ->
            match nodeById caller site.CallNodeId with
            | Some { Kind = SemanticNodeKind.DirectCall _ } -> Ok()
            | Some _ ->
                reject
                    "ParameterTypeMigrationUnsupportedCallerUse"
                    $"Caller use '{StableId.value site.CallNodeId}' is not a DirectCall; this bounded migration does not rewrite callback references."
            | None ->
                reject
                    "ParameterTypeMigrationCallerUseNotFound"
                    $"Caller use '{StableId.value site.CallNodeId}' no longer exists."

    let private validateCallSites snapshot sites =
        let rec loop = function
            | [] -> Ok()
            | head :: tail ->
                match validateCallSiteKind snapshot head with
                | Error error -> Error error
                | Ok() -> loop tail
        loop sites

    let private preflight
        (baseSnapshot: Snapshot)
        (request: AgentParameterTypeMigrationRequest)
        =
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            reject
                "ParameterTypeMigrationExpectedBaseMismatch"
                "The migration request targets a different base snapshot."
        else
            match functionById baseSnapshot request.TargetFunctionId with
            | None ->
                reject
                    "ParameterTypeMigrationTargetNotFound"
                    "The target function does not exist."
            | Some target ->
                match
                    target.Parameters
                    |> List.tryFind (fun parameter ->
                        parameter.Id = request.TargetParameterId)
                with
                | None ->
                    reject
                        "ParameterTypeMigrationParameterNotFound"
                        "The target parameter is not owned by the target function."
                | Some parameter when parameter.Type = request.NewParameterType ->
                    reject
                        "ParameterTypeMigrationTypeUnchanged"
                        "The requested parameter type is already the current type."
                | Some parameter ->
                    match SemanticQuery.getCallers baseSnapshot target.Id with
                    | Error error ->
                        Error(
                            AgentStructuralWorkflowError.ReviewProjectionFailed
                                error
                        )
                    | Ok callerSites ->
                        match validateCallSites baseSnapshot callerSites with
                        | Error error -> Error error
                        | Ok() ->
                            let callerIds =
                                callerSites
                                |> List.map (fun site -> site.CallerFunctionId)
                                |> List.distinct
                                |> List.sortBy StableId.value

                            if callerIds |> List.contains target.Id then
                                reject
                                    "ParameterTypeMigrationRecursiveTargetUnsupported"
                                    "This bounded migration does not support a target that directly calls itself."
                            else
                                match
                                    request.CallerRewrites
                                    |> List.countBy (fun rewrite ->
                                        rewrite.CallerFunctionId)
                                    |> List.tryFind (fun (_, count) ->
                                        count > 1)
                                with
                                | Some(callerId, _) ->
                                    reject
                                        "ParameterTypeMigrationDuplicateCallerRewrite"
                                        $"Caller '{StableId.value callerId}' has more than one replacement body."
                                | None ->
                                    let requestedCallerIds =
                                        request.CallerRewrites
                                        |> List.map (fun rewrite ->
                                            rewrite.CallerFunctionId)
                                    let expectedSet = Set.ofList callerIds
                                    let requestedSet =
                                        Set.ofList requestedCallerIds

                                    if expectedSet <> requestedSet then
                                        let render values =
                                            values
                                            |> Set.toList
                                            |> List.map StableId.value
                                            |> String.concat ", "
                                        reject
                                            "ParameterTypeMigrationCallerRewriteMismatch"
                                            $"Caller rewrites must match the complete affected caller set. Missing: [{render (Set.difference expectedSet requestedSet)}]. Unrelated: [{render (Set.difference requestedSet expectedSet)}]."
                                    else
                                        match
                                            bodyObligationError
                                                target
                                                baseSnapshot
                                        with
                                        | Some error ->
                                            reject
                                                "ParameterTypeMigrationTargetBodyReferencedByObligation"
                                                $"{error.Code}: {error.Message}"
                                        | None ->
                                            let callers =
                                                callerIds
                                                |> List.map (fun callerId ->
                                                    baseSnapshot.Functions
                                                    |> List.find (fun fn ->
                                                        fn.Id = callerId))
                                            match
                                                callers
                                                |> List.tryPick (fun caller ->
                                                    bodyObligationError
                                                        caller
                                                        baseSnapshot
                                                    |> Option.map (fun error ->
                                                        caller, error))
                                            with
                                            | Some(caller, error) ->
                                                reject
                                                    "ParameterTypeMigrationCallerBodyReferencedByObligation"
                                                    $"Caller '{StableId.value caller.Id}': {error.Code}: {error.Message}"
                                            | None ->
                                                Ok
                                                    { Target = target
                                                      Parameter = parameter
                                                      CallerSites = callerSites
                                                      Callers = callers
                                                      RewritesByCaller =
                                                        request.CallerRewrites
                                                        |> List.map (fun rewrite ->
                                                            rewrite.CallerFunctionId,
                                                            rewrite.Replacement)
                                                        |> Map.ofList }

    let private bodyDiff
        baseSnapshot
        proposedSnapshot
        (before: SemanticFunction)
        (after: SemanticFunction)
        =
        { OwnerFunctionId = before.Id
          OldResultNodeId = before.Body.ResultNodeId
          NewResultNodeId = after.Body.ResultNodeId
          RemovedNodes =
            ValueGraph.collectNodes before.Body
            |> summarizeNodes baseSnapshot before
            |> List.sortBy (fun node -> StableId.value node.NodeId)
          AddedNodes =
            ValueGraph.collectNodes after.Body
            |> summarizeNodes proposedSnapshot after
            |> List.sortBy (fun node -> StableId.value node.NodeId) }

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentParameterTypeMigrationRequest)
        : Result<AgentParameterTypeMigrationProposal, AgentStructuralWorkflowError> =
        let baseSnapshot =
            AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ ->
            reject "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            match preflight baseSnapshot request with
            | Error error -> Error error
            | Ok prepared ->
                match
                    materializeTemporaryTarget
                        workspace
                        prepared
                        request.NewParameterType
                        request.TargetReplacement
                with
                | Error error -> Error error
                | Ok temporaryTarget ->
                    let rec materializeCallers collected = function
                        | [] -> Ok(List.rev collected)
                        | caller :: rest ->
                            match
                                materializeTemporaryCaller
                                    temporaryTarget
                                    prepared.Target.Id
                                    caller
                                    (Map.find
                                        caller.Id
                                        prepared.RewritesByCaller)
                            with
                            | Error error -> Error error
                            | Ok temporaryCaller ->
                                materializeCallers
                                    ((caller, temporaryCaller) :: collected)
                                    rest

                    match materializeCallers [] prepared.Callers with
                    | Error error -> Error error
                    | Ok temporaryCallers ->
                        let targetOwnerParameterMap =
                            List.map2
                                (fun (temporary: SemanticParameter) (original: SemanticParameter) ->
                                    temporary.Id, original.Id)
                                temporaryTarget.Function.Parameters
                                prepared.Target.Parameters
                            |> Map.ofList
                        let migratedTarget =
                            { prepared.Target with
                                Parameters =
                                    prepared.Target.Parameters
                                    |> List.map (fun (parameter: SemanticParameter) ->
                                        if parameter.Id =
                                           prepared.Parameter.Id then
                                            { parameter with
                                                Type =
                                                    request.NewParameterType }
                                        else
                                            parameter)
                                Body =
                                    remapGraph
                                        targetOwnerParameterMap
                                        temporaryTarget
                                        prepared.Target.Id
                                        temporaryTarget.Function.Body }

                        let migratedCallers =
                            temporaryCallers
                            |> List.map (fun ((original: SemanticFunction), (temporary: SemanticFunction)) ->
                                let ownerParameterMap =
                                    List.map2
                                        (fun (temp: SemanticParameter) (originalParameter: SemanticParameter) ->
                                            temp.Id,
                                            originalParameter.Id)
                                        temporary.Parameters
                                        original.Parameters
                                    |> Map.ofList
                                original.Id,
                                { original with
                                    Body =
                                        remapGraph
                                            ownerParameterMap
                                            temporaryTarget
                                            prepared.Target.Id
                                            temporary.Body })
                            |> Map.ofList

                        let proposedSnapshot =
                            { baseSnapshot with
                                Id = StableIdAllocator.newSnapshotId ()
                                ParentId = Some baseSnapshot.Id
                                Functions =
                                    baseSnapshot.Functions
                                    |> List.map (fun functionEntity ->
                                        if functionEntity.Id =
                                           migratedTarget.Id then
                                            migratedTarget
                                        else
                                            Map.tryFind
                                                functionEntity.Id
                                                migratedCallers
                                            |> Option.defaultValue
                                                functionEntity) }

                        match SnapshotValidator.validate proposedSnapshot with
                        | issue :: _ ->
                            reject
                                "ParameterTypeMigrationInvalidProposedSnapshot"
                                $"{issue.Code}: {issue.Message}"
                        | [] ->
                            match classifyReadiness proposedSnapshot with
                            | Error diagnostics ->
                                Error(
                                    AgentStructuralWorkflowError.SimulationRejected
                                        diagnostics
                                )
                            | Ok readiness ->
                                let transactionId =
                                    StableIdAllocator.newTransactionId ()
                                match
                                    createReceipt
                                        transactionId
                                        baseSnapshot
                                        proposedSnapshot
                                with
                                | Error error -> Error error
                                | Ok receipt ->
                                    let proposedTarget =
                                        proposedSnapshot.Functions
                                        |> List.find (fun fn ->
                                            fn.Id = prepared.Target.Id)
                                    let targetDiff =
                                        bodyDiff
                                            baseSnapshot
                                            proposedSnapshot
                                            prepared.Target
                                            proposedTarget
                                    let callerDiffs =
                                        prepared.Callers
                                        |> List.map (fun caller ->
                                            let proposedCaller =
                                                proposedSnapshot.Functions
                                                |> List.find (fun fn ->
                                                    fn.Id = caller.Id)
                                            bodyDiff
                                                baseSnapshot
                                                proposedSnapshot
                                                caller
                                                proposedCaller)
                                    let diff =
                                        { ParameterChange =
                                            { FunctionId =
                                                prepared.Target.Id
                                              ParameterId =
                                                prepared.Parameter.Id
                                              OldType =
                                                prepared.Parameter.Type
                                              NewType =
                                                request.NewParameterType }
                                          AffectedCallSites =
                                            prepared.CallerSites
                                          BodyRewrites =
                                            targetDiff :: callerDiffs }
                                    Ok(
                                        AgentParameterTypeMigrationProposal
                                            { TransactionId = transactionId
                                              ProposedSnapshot =
                                                proposedSnapshot
                                              Review =
                                                { RequestedChange =
                                                    request
                                                  Receipt = receipt
                                                  Readiness = readiness
                                                  ResultingChange = diff } }
                                    )

    let private publishWithPolicy
        workspace
        (AgentParameterTypeMigrationProposal proposal)
        policy
        =
        match
            publishReviewedWithPolicy
                workspace
                proposal.TransactionId
                proposal.ProposedSnapshot
                (List.head
                    proposal.Review.ResultingChange.BodyRewrites)
                proposal.Review.Readiness
                proposal.Review.Receipt
                policy
        with
        | AgentStructuralCommitResult.Rejected diagnostics ->
            AgentParameterTypeMigrationCommitResult.Rejected diagnostics
        | AgentStructuralCommitResult.Committed(nextWorkspace, _) ->
            AgentParameterTypeMigrationCommitResult.Committed(
                nextWorkspace,
                { TransactionId = proposal.TransactionId
                  BaseSnapshotId =
                    proposal.Review.Receipt.BaseSnapshotId
                  PublishedSnapshotId = proposal.ProposedSnapshot.Id
                  Diff = proposal.Review.ResultingChange
                  Readiness = proposal.Review.Readiness
                  Receipt = proposal.Review.Receipt }
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
        (AgentParameterTypeMigrationProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId =
            AgentWorkspace.currentSnapshotId workspace }
