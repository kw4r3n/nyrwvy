namespace Nyrwvy.Core

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

type AgentResultTypeMigrationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      TargetFunctionId: FunctionId
      NewResultType: TypeRef
      /// The complete new target body, which must have the new result type.
      TargetReplacement: ExpressionRecipe
      /// One complete replacement body for every function that directly calls the target.
      CallerRewrites: AgentParameterTypeCallerRewrite list }

type AgentResultTypeChange =
    { FunctionId: FunctionId
      OldType: TypeRef
      NewType: TypeRef }

type AgentResultTypeMigrationDiff =
    { ResultChange: AgentResultTypeChange
      AffectedCallSites: QueryCallSite list
      BodyRewrites: FunctionBodyReplacementDiff list }

type AgentResultTypeMigrationReview =
    { RequestedChange: AgentResultTypeMigrationRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: AgentResultTypeMigrationDiff }

type AgentResultTypeMigrationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: AgentResultTypeMigrationDiff
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private ResultTypeMigrationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentResultTypeMigrationReview }

type AgentResultTypeMigrationProposal =
    private
    | AgentResultTypeMigrationProposal of ResultTypeMigrationProposalState

[<RequireQualifiedAccess>]
module AgentResultTypeMigrationProposal =
    let private state (AgentResultTypeMigrationProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
type AgentResultTypeMigrationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentResultTypeMigrationPublishedTransaction

/// Shared machinery for reviewed signature migrations: a target function's parameter or result
/// types change together with its body and every direct caller in one publication.
[<RequireQualifiedAccess>]
module internal SignatureMigrationCore =
    open StructuralTransactionInternals

    type Preflight =
        { Target: SemanticFunction
          CallerSites: QueryCallSite list
          Callers: SemanticFunction list
          RewritesByCaller: Map<FunctionId, ExpressionRecipe> }

    type TemporaryTarget =
        { Workspace: AgentWorkspace
          Function: SemanticFunction
          OriginalToTemporaryParameters: Map<ParameterId, ParameterId>
          TemporaryToOriginalParameters: Map<ParameterId, ParameterId> }

    let reject code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    let wrapStructuralError code context = function
        | AgentStructuralWorkflowError.SimulationRejected (inner :: _) ->
            reject code $"{context}: {inner.Code}: {inner.Message}"
        | error -> Error error

    let functionById (snapshot: Snapshot) functionId =
        snapshot.Functions
        |> List.tryFind (fun functionEntity -> functionEntity.Id = functionId)

    let nodeById (functionEntity: SemanticFunction) nodeId =
        ValueGraph.collectNodes functionEntity.Body
        |> List.tryFind (fun node -> node.Id = nodeId)

    let rewriteForTemporaryFunction
        (prefix: string)
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
            let rewriteAll values =
                values |> List.map (fun value -> (), value) |> rewritePairs |> Result.map (List.map snd)
            let one make value = rewrite value |> Result.map make
            let two make left right =
                rewrite left |> Result.bind (fun left -> rewrite right |> Result.map (fun right -> make(left, right)))
            let callback make source functionId context =
                rewrite source |> Result.bind (fun source -> rewritePairs context |> Result.map (fun context -> make(source, functionId, context)))
            let three make first second third =
                rewrite first |> Result.bind (fun first -> two (fun (second, third) -> make(first, second, third)) second third)

            match recipe with
            | ExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId ownerSlots with
                | Some index -> Ok(ExpressionRecipe.ParameterSlot index)
                | None -> Ok recipe
            | ExpressionRecipe.ParameterSlot _ ->
                Error(
                    diagnostic
                        $"{prefix}ParameterSlotUnavailable"
                        "Migration recipes must reference existing parameters by stable ID."
                )
            | ExpressionRecipe.DirectCall(functionId, arguments) ->
                rewritePairs arguments
                |> Result.map (fun mappedArguments ->
                    match targetMapping with
                    | Some(originalTargetId, temporaryTargetId, parameterMap) when functionId = originalTargetId ->
                        let remappedArguments =
                            mappedArguments
                            |> List.map (fun (parameterId, value) ->
                                (Map.tryFind parameterId parameterMap |> Option.defaultValue parameterId), value)
                        ExpressionRecipe.DirectCall(temporaryTargetId, remappedArguments)
                    | _ -> ExpressionRecipe.DirectCall(functionId, mappedArguments))
            | ExpressionRecipe.PatternReference _
            | ExpressionRecipe.StringLiteral _
            | ExpressionRecipe.BoolLiteral _
            | ExpressionRecipe.UnitLiteral
            | ExpressionRecipe.Int64Literal _
            | ExpressionRecipe.Float64Literal _
            | ExpressionRecipe.ListEmpty _
            | ExpressionRecipe.OptionNone _ -> Ok recipe
            | ExpressionRecipe.StringIsEmpty value -> one ExpressionRecipe.StringIsEmpty value
            | ExpressionRecipe.Int64ToString value -> one ExpressionRecipe.Int64ToString value
            | ExpressionRecipe.StringParseInt64 value -> one ExpressionRecipe.StringParseInt64 value
            | ExpressionRecipe.Float64IsNaN value -> one ExpressionRecipe.Float64IsNaN value
            | ExpressionRecipe.Float64IsFinite value -> one ExpressionRecipe.Float64IsFinite value
            | ExpressionRecipe.Float64IsInfinite value -> one ExpressionRecipe.Float64IsInfinite value
            | ExpressionRecipe.ListLength value -> one ExpressionRecipe.ListLength value
            | ExpressionRecipe.OptionSome value -> one ExpressionRecipe.OptionSome value
            | ExpressionRecipe.StringContains(left, right) -> two ExpressionRecipe.StringContains left right
            | ExpressionRecipe.StringEqual(left, right) -> two ExpressionRecipe.StringEqual left right
            | ExpressionRecipe.StringConcat(left, right) -> two ExpressionRecipe.StringConcat left right
            | ExpressionRecipe.Int64WrappingAdd(left, right) -> two ExpressionRecipe.Int64WrappingAdd left right
            | ExpressionRecipe.Int64WrappingSubtract(left, right) -> two ExpressionRecipe.Int64WrappingSubtract left right
            | ExpressionRecipe.Int64WrappingMultiply(left, right) -> two ExpressionRecipe.Int64WrappingMultiply left right
            | ExpressionRecipe.Int64Equal(left, right) -> two ExpressionRecipe.Int64Equal left right
            | ExpressionRecipe.Int64LessThan(left, right) -> two ExpressionRecipe.Int64LessThan left right
            | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> two ExpressionRecipe.Int64LessThanOrEqual left right
            | ExpressionRecipe.Int64GreaterThan(left, right) -> two ExpressionRecipe.Int64GreaterThan left right
            | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> two ExpressionRecipe.Int64GreaterThanOrEqual left right
            | ExpressionRecipe.Float64Add(left, right) -> two ExpressionRecipe.Float64Add left right
            | ExpressionRecipe.Float64Subtract(left, right) -> two ExpressionRecipe.Float64Subtract left right
            | ExpressionRecipe.Float64Multiply(left, right) -> two ExpressionRecipe.Float64Multiply left right
            | ExpressionRecipe.Float64Divide(left, right) -> two ExpressionRecipe.Float64Divide left right
            | ExpressionRecipe.Float64Equal(left, right) -> two ExpressionRecipe.Float64Equal left right
            | ExpressionRecipe.Float64LessThan(left, right) -> two ExpressionRecipe.Float64LessThan left right
            | ExpressionRecipe.ListAppendItem(list, item) -> two ExpressionRecipe.ListAppendItem list item
            | ExpressionRecipe.ListConcat(left, right) -> two ExpressionRecipe.ListConcat left right
            | ExpressionRecipe.Bind(value, body) -> two ExpressionRecipe.Bind value body
            | ExpressionRecipe.ListConstruct(itemType, items) ->
                rewriteAll items |> Result.map (fun items -> ExpressionRecipe.ListConstruct(itemType, items))
            | ExpressionRecipe.ListFold(source, seed, folder, context) ->
                two (fun (source, seed) -> source, seed) source seed
                |> Result.bind (fun (source, seed) ->
                    rewritePairs context |> Result.map (fun context -> ExpressionRecipe.ListFold(source, seed, folder, context)))
            | ExpressionRecipe.ListEffectFold(source, seed, folder, context) ->
                two (fun (source, seed) -> source, seed) source seed
                |> Result.bind (fun (source, seed) ->
                    rewritePairs context |> Result.map (fun context -> ExpressionRecipe.ListEffectFold(source, seed, folder, context)))
            | ExpressionRecipe.UseResource(acquire, releaseCapability, operationId, bodyFunctionId, context) ->
                two (fun (acquire, releaseCapability) -> acquire, releaseCapability) acquire releaseCapability
                |> Result.bind (fun (acquire, releaseCapability) ->
                    rewritePairs context
                    |> Result.map (fun context -> ExpressionRecipe.UseResource(acquire, releaseCapability, operationId, bodyFunctionId, context)))
            | ExpressionRecipe.ParallelAll(source, functionId, context) -> callback ExpressionRecipe.ParallelAll source functionId context
            | ExpressionRecipe.CancellationRequested -> Ok recipe
            | ExpressionRecipe.ListMap(source, functionId, context) -> callback ExpressionRecipe.ListMap source functionId context
            | ExpressionRecipe.ListFilter(source, functionId, context) -> callback ExpressionRecipe.ListFilter source functionId context
            | ExpressionRecipe.ListAny(source, functionId, context) -> callback ExpressionRecipe.ListAny source functionId context
            | ExpressionRecipe.ListAll(source, functionId, context) -> callback ExpressionRecipe.ListAll source functionId context
            | ExpressionRecipe.ProductConstruct(typeId, fields) ->
                rewritePairs fields |> Result.map (fun fields -> ExpressionRecipe.ProductConstruct(typeId, fields))
            | ExpressionRecipe.FieldAccess(source, fieldId) -> one (fun source -> ExpressionRecipe.FieldAccess(source, fieldId)) source
            | ExpressionRecipe.UnionConstruct(variantId, fields) ->
                rewritePairs fields |> Result.map (fun fields -> ExpressionRecipe.UnionConstruct(variantId, fields))
            | ExpressionRecipe.ResultOk(errorType, value) -> one (fun value -> ExpressionRecipe.ResultOk(errorType, value)) value
            | ExpressionRecipe.ResultErr(okType, error) -> one (fun error -> ExpressionRecipe.ResultErr(okType, error)) error
            | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
                rewrite capabilityValue
                |> Result.bind (fun capabilityValue ->
                    rewritePairs arguments
                    |> Result.map (fun arguments -> ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments)))
            | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) -> three ExpressionRecipe.Conditional condition whenTrue whenFalse
            | ExpressionRecipe.MatchUnion(input, arms) ->
                rewrite input |> Result.bind (fun input -> rewritePairs arms |> Result.map (fun arms -> ExpressionRecipe.MatchUnion(input, arms)))
            | ExpressionRecipe.MatchOption(input, someBody, noneBody) -> three ExpressionRecipe.MatchOption input someBody noneBody
            | ExpressionRecipe.MatchResult(input, okBody, errBody) -> three ExpressionRecipe.MatchResult input okBody errBody

        rewrite recipe

    let functionCreationRequest
        (workspace: AgentWorkspace)
        (functionEntity: SemanticFunction)
        (parameterTypes: Map<ParameterId, TypeRef>)
        (resultType: TypeRef)
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
          ResultType = resultType
          RequiredEffects = functionEntity.RequiredEffects
          Body = body }

    let materializeTemporaryTarget
        (prefix: string)
        workspace
        (prepared: Preflight)
        (parameterTypes: Map<ParameterId, TypeRef>)
        (resultType: TypeRef)
        targetRecipe
        =
        match
            rewriteForTemporaryFunction
                prefix
                prepared.Target.Parameters
                None
                targetRecipe
        with
        | Error error ->
            reject
                $"{prefix}TargetRewriteInvalid"
                $"{error.Code}: {error.Message}"
        | Ok temporaryRecipe ->
            let creationRequest =
                functionCreationRequest
                    workspace
                    prepared.Target
                    parameterTypes
                    resultType
                    temporaryRecipe

            match
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate
                    workspace
                    creationRequest
            with
            | Error error ->
                wrapStructuralError
                    $"{prefix}TargetRewriteInvalid"
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

    let materializeTemporaryCaller
        (prefix: string)
        (temporaryTarget: TemporaryTarget)
        targetFunctionId
        (caller: SemanticFunction)
        replacement
        =
        match
            rewriteForTemporaryFunction
                prefix
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
                $"{prefix}CallerRewriteInvalid"
                $"Caller '{StableId.value caller.Id}' rewrite is invalid: {error.Code}: {error.Message}"
        | Ok temporaryRecipe ->
            let creationRequest =
                functionCreationRequest
                    temporaryTarget.Workspace
                    caller
                    Map.empty
                    caller.ResultType
                    temporaryRecipe

            match
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate
                    temporaryTarget.Workspace
                    creationRequest
            with
            | Error error ->
                wrapStructuralError
                    $"{prefix}CallerRewriteInvalid"
                    $"Caller '{StableId.value caller.Id}' rewrite is invalid"
                    error
            | Ok proposal ->
                proposal
                |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
                |> fun review -> Ok review.CreatedFunction

    let rec remapGraph
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

        let remap = remapGraph ownerParameterMap temporaryTarget targetFunctionId

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
                | SemanticNodeKind.Bind(inputNodeId, body) ->
                    SemanticNodeKind.Bind(inputNodeId, { body with Body = remap body.Body })
                | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                    SemanticNodeKind.MatchOption(inputNodeId, { someArm with Body = remap someArm.Body }, remap noneBody)
                | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                    SemanticNodeKind.MatchResult(inputNodeId, { okArm with Body = remap okArm.Body }, { errArm with Body = remap errArm.Body })
                | other -> other
            { node with Kind = kind }

        { graph with Nodes = graph.Nodes |> List.map mapNode }

    let validateCallSiteKind
        (prefix: string)
        (snapshot: Snapshot)
        (site: QueryCallSite)
        =
        match functionById snapshot site.CallerFunctionId with
        | None ->
            reject
                $"{prefix}CallerNotFound"
                $"Caller '{StableId.value site.CallerFunctionId}' does not exist."
        | Some caller ->
            match nodeById caller site.CallNodeId with
            | Some { Kind = SemanticNodeKind.DirectCall _ } -> Ok()
            | Some _ ->
                reject
                    $"{prefix}UnsupportedCallerUse"
                    $"Caller use '{StableId.value site.CallNodeId}' is not a DirectCall; this bounded migration does not rewrite callback references."
            | None ->
                reject
                    $"{prefix}CallerUseNotFound"
                    $"Caller use '{StableId.value site.CallNodeId}' no longer exists."

    let validateCallSites prefix snapshot sites =
        let rec loop = function
            | [] -> Ok()
            | head :: tail ->
                match validateCallSiteKind prefix snapshot head with
                | Error error -> Error error
                | Ok() -> loop tail
        loop sites

    /// Checks the base, the target and the complete caller set shared by signature migrations.
    /// `checkTarget` validates the migration-specific part of the signature before callers.
    let preflight
        (prefix: string)
        (baseSnapshot: Snapshot)
        (expectedBaseSnapshotId: SnapshotId)
        (targetFunctionId: FunctionId)
        (callerRewrites: AgentParameterTypeCallerRewrite list)
        (checkTarget: SemanticFunction -> Result<unit, AgentStructuralWorkflowError>)
        =
        if expectedBaseSnapshotId <> baseSnapshot.Id then
            reject $"{prefix}ExpectedBaseMismatch" "The migration request targets a different base snapshot."
        else
            match functionById baseSnapshot targetFunctionId with
            | None -> reject $"{prefix}TargetNotFound" "The target function does not exist."
            | Some target ->
                match checkTarget target with
                | Error error -> Error error
                | Ok() ->
                    match SemanticQuery.getCallers baseSnapshot target.Id with
                    | Error error -> Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                    | Ok callerSites ->
                        match validateCallSites prefix baseSnapshot callerSites with
                        | Error error -> Error error
                        | Ok() ->
                            let callerIds =
                                callerSites
                                |> List.map (fun site -> site.CallerFunctionId)
                                |> List.distinct
                                |> List.sortBy StableId.value
                            let duplicate =
                                callerRewrites
                                |> List.countBy (fun rewrite -> rewrite.CallerFunctionId)
                                |> List.tryFind (fun (_, count) -> count > 1)
                            let expectedSet = Set.ofList callerIds
                            let requestedSet = callerRewrites |> List.map (fun rewrite -> rewrite.CallerFunctionId) |> Set.ofList
                            let render values = values |> Set.toList |> List.map StableId.value |> String.concat ", "
                            if callerIds |> List.contains target.Id then
                                reject
                                    $"{prefix}RecursiveTargetUnsupported"
                                    "This bounded migration does not support a target that directly calls itself."
                            else
                                match duplicate with
                                | Some(callerId, _) ->
                                    reject
                                        $"{prefix}DuplicateCallerRewrite"
                                        $"Caller '{StableId.value callerId}' has more than one replacement body."
                                | None when expectedSet <> requestedSet ->
                                    reject
                                        $"{prefix}CallerRewriteMismatch"
                                        $"Caller rewrites must match the complete affected caller set. Missing: [{render (Set.difference expectedSet requestedSet)}]. Unrelated: [{render (Set.difference requestedSet expectedSet)}]."
                                | None ->
                                    match bodyObligationError target baseSnapshot with
                                    | Some error ->
                                        reject $"{prefix}TargetBodyReferencedByObligation" $"{error.Code}: {error.Message}"
                                    | None ->
                                        let callers =
                                            callerIds |> List.map (fun callerId -> baseSnapshot.Functions |> List.find (fun fn -> fn.Id = callerId))
                                        match
                                            callers
                                            |> List.tryPick (fun caller ->
                                                bodyObligationError caller baseSnapshot |> Option.map (fun error -> caller, error))
                                        with
                                        | Some(caller, error) ->
                                            reject
                                                $"{prefix}CallerBodyReferencedByObligation"
                                                $"Caller '{StableId.value caller.Id}': {error.Code}: {error.Message}"
                                        | None ->
                                            Ok
                                                { Target = target
                                                  CallerSites = callerSites
                                                  Callers = callers
                                                  RewritesByCaller =
                                                    callerRewrites
                                                    |> List.map (fun rewrite -> rewrite.CallerFunctionId, rewrite.Replacement)
                                                    |> Map.ofList }

    let bodyDiff
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

    type Simulated =
        { TransactionId: TransactionId
          ProposedSnapshot: Snapshot
          Receipt: TransactionPreviewReceipt
          Readiness: TransactionReadiness
          BodyRewrites: FunctionBodyReplacementDiff list }

    /// Materializes the migrated target and every caller through temporary functions, then maps
    /// them back onto the original identities and validates the proposed snapshot as a whole.
    let simulate
        (prefix: string)
        (workspace: AgentWorkspace)
        (baseSnapshot: Snapshot)
        (prepared: Preflight)
        (parameterTypes: Map<ParameterId, TypeRef>)
        (resultType: TypeRef)
        targetReplacement
        : Result<Simulated, AgentStructuralWorkflowError> =
        match materializeTemporaryTarget prefix workspace prepared parameterTypes resultType targetReplacement with
        | Error error -> Error error
        | Ok temporaryTarget ->
            let rec materializeCallers collected = function
                | [] -> Ok(List.rev collected)
                | caller :: rest ->
                    match materializeTemporaryCaller prefix temporaryTarget prepared.Target.Id caller (Map.find caller.Id prepared.RewritesByCaller) with
                    | Error error -> Error error
                    | Ok temporaryCaller -> materializeCallers ((caller, temporaryCaller) :: collected) rest
            match materializeCallers [] prepared.Callers with
            | Error error -> Error error
            | Ok temporaryCallers ->
                let ownerMap (temporary: SemanticFunction) (original: SemanticFunction) =
                    List.map2 (fun (temp: SemanticParameter) (originalParameter: SemanticParameter) -> temp.Id, originalParameter.Id)
                        temporary.Parameters original.Parameters
                    |> Map.ofList
                let migratedTarget =
                    { prepared.Target with
                        Parameters =
                            prepared.Target.Parameters
                            |> List.map (fun (parameter: SemanticParameter) ->
                                match Map.tryFind parameter.Id parameterTypes with
                                | Some newType -> { parameter with Type = newType }
                                | None -> parameter)
                        ResultType = resultType
                        Body = remapGraph (ownerMap temporaryTarget.Function prepared.Target) temporaryTarget prepared.Target.Id temporaryTarget.Function.Body }
                let migratedCallers =
                    temporaryCallers
                    |> List.map (fun ((original: SemanticFunction), (temporary: SemanticFunction)) ->
                        original.Id, { original with Body = remapGraph (ownerMap temporary original) temporaryTarget prepared.Target.Id temporary.Body })
                    |> Map.ofList
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = StableIdAllocator.newSnapshotId ()
                        ParentId = Some baseSnapshot.Id
                        Functions =
                            baseSnapshot.Functions
                            |> List.map (fun functionEntity ->
                                if functionEntity.Id = migratedTarget.Id then migratedTarget
                                else Map.tryFind functionEntity.Id migratedCallers |> Option.defaultValue functionEntity) }
                match SnapshotValidator.validate proposedSnapshot with
                | issue :: _ -> reject $"{prefix}InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [] ->
                    match classifyReadiness proposedSnapshot with
                    | Error diagnostics -> Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        let transactionId = StableIdAllocator.newTransactionId ()
                        match createReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error error
                        | Ok receipt ->
                            let proposed functionId = proposedSnapshot.Functions |> List.find (fun fn -> fn.Id = functionId)
                            let rewrites =
                                prepared.Target :: prepared.Callers
                                |> List.map (fun original -> bodyDiff baseSnapshot proposedSnapshot original (proposed original.Id))
                            Ok
                                { TransactionId = transactionId
                                  ProposedSnapshot = proposedSnapshot
                                  Receipt = receipt
                                  Readiness = readiness
                                  BodyRewrites = rewrites }

[<RequireQualifiedAccess>]
module AgentParameterTypeMigrationWorkflow =
    open StructuralTransactionInternals

    let private prefix = "ParameterTypeMigration"

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentParameterTypeMigrationRequest)
        : Result<AgentParameterTypeMigrationProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        let checkParameter (target: SemanticFunction) =
            match target.Parameters |> List.tryFind (fun parameter -> parameter.Id = request.TargetParameterId) with
            | None -> SignatureMigrationCore.reject $"{prefix}ParameterNotFound" "The target parameter is not owned by the target function."
            | Some parameter when parameter.Type = request.NewParameterType ->
                SignatureMigrationCore.reject $"{prefix}TypeUnchanged" "The requested parameter type is already the current type."
            | Some _ -> Ok()
        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> SignatureMigrationCore.reject "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            SignatureMigrationCore.preflight
                prefix baseSnapshot request.ExpectedBaseSnapshotId request.TargetFunctionId request.CallerRewrites checkParameter
            |> Result.bind (fun prepared ->
                let parameter = prepared.Target.Parameters |> List.find (fun item -> item.Id = request.TargetParameterId)
                SignatureMigrationCore.simulate
                    prefix workspace baseSnapshot prepared
                    (Map.ofList [ parameter.Id, request.NewParameterType ]) prepared.Target.ResultType request.TargetReplacement
                |> Result.map (fun simulated ->
                    AgentParameterTypeMigrationProposal
                        { TransactionId = simulated.TransactionId
                          ProposedSnapshot = simulated.ProposedSnapshot
                          Review =
                            { RequestedChange = request
                              Receipt = simulated.Receipt
                              Readiness = simulated.Readiness
                              ResultingChange =
                                { ParameterChange =
                                    { FunctionId = prepared.Target.Id
                                      ParameterId = parameter.Id
                                      OldType = parameter.Type
                                      NewType = request.NewParameterType }
                                  AffectedCallSites = prepared.CallerSites
                                  BodyRewrites = simulated.BodyRewrites } } }))

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

    let rejectProposal
        (workspace: AgentWorkspace)
        (AgentParameterTypeMigrationProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId =
            AgentWorkspace.currentSnapshotId workspace }


/// Changes a function's result type together with its body and every direct caller, as one
/// reviewed publication. Identities of the target, its parameters and untouched nodes are kept.
[<RequireQualifiedAccess>]
module AgentResultTypeMigrationWorkflow =
    open StructuralTransactionInternals

    let private prefix = "ResultTypeMigration"

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentResultTypeMigrationRequest)
        : Result<AgentResultTypeMigrationProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        let checkResult (target: SemanticFunction) =
            if target.ResultType = request.NewResultType then
                SignatureMigrationCore.reject $"{prefix}TypeUnchanged" "The requested result type is already the current type."
            else Ok()
        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> SignatureMigrationCore.reject "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            SignatureMigrationCore.preflight
                prefix baseSnapshot request.ExpectedBaseSnapshotId request.TargetFunctionId request.CallerRewrites checkResult
            |> Result.bind (fun prepared ->
                SignatureMigrationCore.simulate
                    prefix workspace baseSnapshot prepared Map.empty request.NewResultType request.TargetReplacement
                |> Result.map (fun simulated ->
                    AgentResultTypeMigrationProposal
                        { TransactionId = simulated.TransactionId
                          ProposedSnapshot = simulated.ProposedSnapshot
                          Review =
                            { RequestedChange = request
                              Receipt = simulated.Receipt
                              Readiness = simulated.Readiness
                              ResultingChange =
                                { ResultChange =
                                    { FunctionId = prepared.Target.Id
                                      OldType = prepared.Target.ResultType
                                      NewType = request.NewResultType }
                                  AffectedCallSites = prepared.CallerSites
                                  BodyRewrites = simulated.BodyRewrites } } }))

    let commitWithPolicy workspace (AgentResultTypeMigrationProposal proposal) policy =
        match
            publishReviewedWithPolicy
                workspace
                proposal.TransactionId
                proposal.ProposedSnapshot
                (List.head proposal.Review.ResultingChange.BodyRewrites)
                proposal.Review.Readiness
                proposal.Review.Receipt
                policy
        with
        | AgentStructuralCommitResult.Rejected diagnostics -> AgentResultTypeMigrationCommitResult.Rejected diagnostics
        | AgentStructuralCommitResult.Committed(nextWorkspace, _) ->
            AgentResultTypeMigrationCommitResult.Committed(
                nextWorkspace,
                { TransactionId = proposal.TransactionId
                  BaseSnapshotId = proposal.Review.Receipt.BaseSnapshotId
                  PublishedSnapshotId = proposal.ProposedSnapshot.Id
                  Diff = proposal.Review.ResultingChange
                  Readiness = proposal.Review.Readiness
                  Receipt = proposal.Review.Receipt })

    let commit workspace proposal = commitWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations
