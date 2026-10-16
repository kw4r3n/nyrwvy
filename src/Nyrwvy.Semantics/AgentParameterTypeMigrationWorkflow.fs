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
      FunctionEffects: FunctionEffectSummary list
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
    /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot
    /// Functions of the proposed snapshot, for rendering rewritten bodies in a full review.
    let proposedFunctions proposal = (state proposal).ProposedSnapshot.Functions

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
      FunctionEffects: FunctionEffectSummary list
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
    /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot
    /// Functions of the proposed snapshot, for rendering rewritten bodies in a full review.
    let proposedFunctions proposal = (state proposal).ProposedSnapshot.Functions

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
          TemporaryToOriginalParameters: Map<ParameterId, ParameterId>
          /// Creation mints fresh type parameters for the temporary copy; this maps them back to
          /// the original target's own type parameters.
          TemporaryToOriginalTypeParameters: Map<TypeParameterId, TypeRef> }

    /// A caller's temporary copy: the created function and its fresh type parameters mapped back.
    type TemporaryCaller =
        { Function: SemanticFunction
          TemporaryToOriginalTypeParameters: Map<TypeParameterId, TypeRef> }

    /// Which function a recipe is rewritten for, deciding how its calls to the target are rewritten.
    [<RequireQualifiedAccess>]
    type TemporaryOwner =
        /// The migrated target: a call to itself by FunctionId becomes a SelfCall, so it is typed
        /// against the new signature and materializes as a call to the temporary copy.
        | Target
        /// A caller: calls to the original target are redirected to the temporary target, and a
        /// SelfCall becomes an ordinary call to the unchanged original caller.
        | Caller of callerId: FunctionId * temporaryTargetId: FunctionId * parameterMap: Map<ParameterId, ParameterId>

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

    let unsupportedRewriteUseCode prefix = $"{prefix}UnsupportedRewriteUse"

    let rewriteForTemporaryFunction
        (prefix: string)
        (ownerParameters: SemanticParameter list)
        (originalTargetId: FunctionId)
        (owner: TemporaryOwner)
        recipe
        =
        let ownerSlots =
            ownerParameters
            |> List.mapi (fun index parameter -> parameter.Id, index)
            |> Map.ofList

        let selfCallSlots arguments =
            let rec loop collected = function
                | [] -> Ok(List.rev collected)
                | (parameterId, value) :: rest ->
                    match Map.tryFind parameterId ownerSlots with
                    | Some slot -> loop ((slot, value) :: collected) rest
                    | None ->
                        Error(
                            diagnostic
                                $"{prefix}TargetSelfCallArgumentNotFound"
                                $"The target's call to itself binds parameter '{StableId.value parameterId}', which the target does not own."
                        )
            loop [] arguments

        // A callback or function-value use of the target would be bound against its old signature,
        // since only direct calls are redirected to the migrated copy.
        let nonCallTargetUse functionId =
            if functionId = originalTargetId then
                let user =
                    match owner with
                    | TemporaryOwner.Target -> "The target replacement"
                    | TemporaryOwner.Caller(callerId, _, _) -> $"Caller '{StableId.value callerId}' rewrite"
                Error(
                    diagnostic
                        (unsupportedRewriteUseCode prefix)
                        $"{user} uses the target function '{StableId.value originalTargetId}' as a callback or function value; this bounded migration rewrites only direct calls to the target."
                )
            else
                Ok()

        let rec rewrite recipe =
            let referencing functionId rewritten = nonCallTargetUse functionId |> Result.bind (fun () -> rewritten)
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
                nonCallTargetUse functionId
                |> Result.bind (fun () -> rewrite source)
                |> Result.bind (fun source -> rewritePairs context |> Result.map (fun context -> make(source, functionId, context)))
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
            | ExpressionRecipe.CapabilityConstruct(_, implementations) ->
                let rec check = function
                    | [] -> Ok recipe
                    | (_, functionId) :: rest -> referencing functionId (check rest)
                check implementations
            | ExpressionRecipe.FunctionReference functionId -> referencing functionId (Ok recipe)
            | ExpressionRecipe.FunctionInvoke(value, arguments) ->
                rewrite value |> Result.bind (fun target ->
                    rewriteAll arguments |> Result.map (fun values -> ExpressionRecipe.FunctionInvoke(target, values)))
            | ExpressionRecipe.DirectCall(functionId, arguments) ->
                rewritePairs arguments
                |> Result.bind (fun mappedArguments ->
                    match owner with
                    | TemporaryOwner.Caller(_, temporaryTargetId, parameterMap) when functionId = originalTargetId ->
                        let remappedArguments =
                            mappedArguments
                            |> List.map (fun (parameterId, value) ->
                                (Map.tryFind parameterId parameterMap |> Option.defaultValue parameterId), value)
                        Ok(ExpressionRecipe.DirectCall(temporaryTargetId, remappedArguments))
                    | TemporaryOwner.Target when functionId = originalTargetId ->
                        // The target's call to itself must be typed against the new signature, not
                        // the original function still in the base snapshot.
                        selfCallSlots mappedArguments |> Result.map ExpressionRecipe.SelfCall
                    | _ -> Ok(ExpressionRecipe.DirectCall(functionId, mappedArguments)))
            | ExpressionRecipe.SelfCall arguments ->
                rewritePairs arguments
                |> Result.map (fun mappedArguments ->
                    match owner with
                    | TemporaryOwner.Caller(callerId, _, _)
                        when mappedArguments |> List.forall (fun (slot, _) -> slot >= 0 && slot < List.length ownerParameters) ->
                        // A caller's temporary copy is discarded after remapping, so its self call
                        // becomes an ordinary call to the unchanged original caller.
                        ExpressionRecipe.DirectCall(
                            callerId,
                            mappedArguments |> List.map (fun (slot, value) -> (List.item slot ownerParameters).Id, value))
                    | _ ->
                        // The migrated target's temporary copy is remapped back to the target, and
                        // an out-of-range caller slot is left for creation to diagnose.
                        ExpressionRecipe.SelfCall mappedArguments)
            | ExpressionRecipe.PatternReference _
            | ExpressionRecipe.StringLiteral _
            | ExpressionRecipe.BoolLiteral _
            | ExpressionRecipe.UnitLiteral
            | ExpressionRecipe.Int64Literal _
            | ExpressionRecipe.Float64Literal _
            | ExpressionRecipe.ListEmpty _
            | ExpressionRecipe.SetEmpty _
            | ExpressionRecipe.MapEmpty _
            | ExpressionRecipe.OptionNone _ -> Ok recipe
            | ExpressionRecipe.StringIsEmpty value -> one ExpressionRecipe.StringIsEmpty value
            | ExpressionRecipe.Int64ToString value -> one ExpressionRecipe.Int64ToString value
            | ExpressionRecipe.Int64ToHexString value -> one ExpressionRecipe.Int64ToHexString value
            | ExpressionRecipe.StringParseInt64 value -> one ExpressionRecipe.StringParseInt64 value
            | ExpressionRecipe.StringParseInt64Hex value -> one ExpressionRecipe.StringParseInt64Hex value
            | ExpressionRecipe.Float64ToString value -> one ExpressionRecipe.Float64ToString value
            | ExpressionRecipe.StringParseFloat64 value -> one ExpressionRecipe.StringParseFloat64 value
            | ExpressionRecipe.StringCodePoints value -> one ExpressionRecipe.StringCodePoints value
            | ExpressionRecipe.StringToLower value -> one ExpressionRecipe.StringToLower value
            | ExpressionRecipe.StringToUpper value -> one ExpressionRecipe.StringToUpper value
            | ExpressionRecipe.StringDecodeUtf8Lossy value -> one ExpressionRecipe.StringDecodeUtf8Lossy value
            | ExpressionRecipe.Float64RoundToInt64 value -> one ExpressionRecipe.Float64RoundToInt64 value
            | ExpressionRecipe.Float64FloorToInt64 value -> one ExpressionRecipe.Float64FloorToInt64 value
            | ExpressionRecipe.Float64Sqrt value -> one ExpressionRecipe.Float64Sqrt value
            | ExpressionRecipe.Float64Abs value -> one ExpressionRecipe.Float64Abs value
            | ExpressionRecipe.Float64Negate value -> one ExpressionRecipe.Float64Negate value
            | ExpressionRecipe.Float64Floor value -> one ExpressionRecipe.Float64Floor value
            | ExpressionRecipe.Float64Ceiling value -> one ExpressionRecipe.Float64Ceiling value
            | ExpressionRecipe.Float64Truncate value -> one ExpressionRecipe.Float64Truncate value
            | ExpressionRecipe.StringUtf8ByteCount value -> one ExpressionRecipe.StringUtf8ByteCount value
            | ExpressionRecipe.StringFromCodePoint value -> one ExpressionRecipe.StringFromCodePoint value
            | ExpressionRecipe.StringToCodePoint value -> one ExpressionRecipe.StringToCodePoint value
            | ExpressionRecipe.StringLength value -> one ExpressionRecipe.StringLength value
            | ExpressionRecipe.StringSlice(value, start, stop) -> three ExpressionRecipe.StringSlice value start stop
            | ExpressionRecipe.Float64IsNaN value -> one ExpressionRecipe.Float64IsNaN value
            | ExpressionRecipe.Float64IsFinite value -> one ExpressionRecipe.Float64IsFinite value
            | ExpressionRecipe.Float64IsInfinite value -> one ExpressionRecipe.Float64IsInfinite value
            | ExpressionRecipe.Int64ToFloat64 value -> one ExpressionRecipe.Int64ToFloat64 value
            | ExpressionRecipe.Int64WrappingAbs value -> one ExpressionRecipe.Int64WrappingAbs value
            | ExpressionRecipe.Int64BitNot value -> one ExpressionRecipe.Int64BitNot value
            | ExpressionRecipe.Bits32FromInt64 value -> one ExpressionRecipe.Bits32FromInt64 value
            | ExpressionRecipe.Bits32ToInt64 value -> one ExpressionRecipe.Bits32ToInt64 value
            | ExpressionRecipe.Bits32Not value -> one ExpressionRecipe.Bits32Not value
            | ExpressionRecipe.ListLength value -> one ExpressionRecipe.ListLength value
            | ExpressionRecipe.MapValues value -> one ExpressionRecipe.MapValues value
            | ExpressionRecipe.MapKeys value -> one ExpressionRecipe.MapKeys value
            | ExpressionRecipe.SetToList value -> one ExpressionRecipe.SetToList value
            | ExpressionRecipe.MapSize value -> one ExpressionRecipe.MapSize value
            | ExpressionRecipe.SetFromList value -> one ExpressionRecipe.SetFromList value
            | ExpressionRecipe.SetSize value -> one ExpressionRecipe.SetSize value
            | ExpressionRecipe.ListReverse value -> one ExpressionRecipe.ListReverse value
            | ExpressionRecipe.ListLast value -> one ExpressionRecipe.ListLast value
            | ExpressionRecipe.ListDropLast value -> one ExpressionRecipe.ListDropLast value
            | ExpressionRecipe.OptionSome value -> one ExpressionRecipe.OptionSome value
            | ExpressionRecipe.StringContains(left, right) -> two ExpressionRecipe.StringContains left right
            | ExpressionRecipe.StringIndexOf(value, substring, from) -> three ExpressionRecipe.StringIndexOf value substring from
            | ExpressionRecipe.StringSpan(value, set, from) -> three ExpressionRecipe.StringSpan value set from
            | ExpressionRecipe.StringSpanBack(value, set, stop) -> three ExpressionRecipe.StringSpanBack value set stop
            | ExpressionRecipe.StringLastIndexOf(left, right) -> two ExpressionRecipe.StringLastIndexOf left right
            | ExpressionRecipe.StringStartsWith(left, right) -> two ExpressionRecipe.StringStartsWith left right
            | ExpressionRecipe.StringEndsWith(left, right) -> two ExpressionRecipe.StringEndsWith left right
            | ExpressionRecipe.StringSplit(left, right) -> two ExpressionRecipe.StringSplit left right
            | ExpressionRecipe.StringSplitLines(value, breaks) -> two ExpressionRecipe.StringSplitLines value breaks
            | ExpressionRecipe.StringReplace(value, pattern, replacement) -> three ExpressionRecipe.StringReplace value pattern replacement
            | ExpressionRecipe.StringTrim value -> one ExpressionRecipe.StringTrim value
            | ExpressionRecipe.StringTrimStart value -> one ExpressionRecipe.StringTrimStart value
            | ExpressionRecipe.StringTrimEnd value -> one ExpressionRecipe.StringTrimEnd value
            | ExpressionRecipe.StringJoin(list, separator) -> two ExpressionRecipe.StringJoin list separator
            | ExpressionRecipe.StringEqual(left, right) -> two ExpressionRecipe.StringEqual left right
            | ExpressionRecipe.StringLessThan(left, right) -> two ExpressionRecipe.StringLessThan left right
            | ExpressionRecipe.ValueEqual(left, right) -> two ExpressionRecipe.ValueEqual left right
            | ExpressionRecipe.StringConcat(left, right) -> two ExpressionRecipe.StringConcat left right
            | ExpressionRecipe.Int64WrappingAdd(left, right) -> two ExpressionRecipe.Int64WrappingAdd left right
            | ExpressionRecipe.Int64WrappingSubtract(left, right) -> two ExpressionRecipe.Int64WrappingSubtract left right
            | ExpressionRecipe.Int64WrappingMultiply(left, right) -> two ExpressionRecipe.Int64WrappingMultiply left right
            | ExpressionRecipe.Int64FloorDivide(left, right) -> two ExpressionRecipe.Int64FloorDivide left right
            | ExpressionRecipe.Int64FloorModulo(left, right) -> two ExpressionRecipe.Int64FloorModulo left right
            | ExpressionRecipe.Int64Min(left, right) -> two ExpressionRecipe.Int64Min left right
            | ExpressionRecipe.Int64Max(left, right) -> two ExpressionRecipe.Int64Max left right
            | ExpressionRecipe.Int64BitAnd(left, right) -> two ExpressionRecipe.Int64BitAnd left right
            | ExpressionRecipe.Int64BitOr(left, right) -> two ExpressionRecipe.Int64BitOr left right
            | ExpressionRecipe.Int64BitXor(left, right) -> two ExpressionRecipe.Int64BitXor left right
            | ExpressionRecipe.Int64ShiftLeft(left, right) -> two ExpressionRecipe.Int64ShiftLeft left right
            | ExpressionRecipe.Int64ShiftRight(left, right) -> two ExpressionRecipe.Int64ShiftRight left right
            | ExpressionRecipe.Int64CheckedAdd(left, right) -> two ExpressionRecipe.Int64CheckedAdd left right
            | ExpressionRecipe.Int64CheckedSubtract(left, right) -> two ExpressionRecipe.Int64CheckedSubtract left right
            | ExpressionRecipe.Int64CheckedMultiply(left, right) -> two ExpressionRecipe.Int64CheckedMultiply left right
            | ExpressionRecipe.ListRange(left, right) -> two ExpressionRecipe.ListRange left right
            | ExpressionRecipe.StringRepeat(left, right) -> two ExpressionRecipe.StringRepeat left right
            | ExpressionRecipe.Float64Round(left, right) -> two ExpressionRecipe.Float64Round left right
            | ExpressionRecipe.Bits32Xor(left, right) -> two ExpressionRecipe.Bits32Xor left right
            | ExpressionRecipe.Bits32And(left, right) -> two ExpressionRecipe.Bits32And left right
            | ExpressionRecipe.Bits32Or(left, right) -> two ExpressionRecipe.Bits32Or left right
            | ExpressionRecipe.Bits32Add(left, right) -> two ExpressionRecipe.Bits32Add left right
            | ExpressionRecipe.Bits32RotateRight(left, right) -> two ExpressionRecipe.Bits32RotateRight left right
            | ExpressionRecipe.Bits32ShiftRight(left, right) -> two ExpressionRecipe.Bits32ShiftRight left right
            | ExpressionRecipe.Bits32ShiftLeft(left, right) -> two ExpressionRecipe.Bits32ShiftLeft left right
            | ExpressionRecipe.Int64Equal(left, right) -> two ExpressionRecipe.Int64Equal left right
            | ExpressionRecipe.Int64LessThan(left, right) -> two ExpressionRecipe.Int64LessThan left right
            | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> two ExpressionRecipe.Int64LessThanOrEqual left right
            | ExpressionRecipe.Int64GreaterThan(left, right) -> two ExpressionRecipe.Int64GreaterThan left right
            | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> two ExpressionRecipe.Int64GreaterThanOrEqual left right
            | ExpressionRecipe.Float64Add(left, right) -> two ExpressionRecipe.Float64Add left right
            | ExpressionRecipe.Float64Min(left, right) -> two ExpressionRecipe.Float64Min left right
            | ExpressionRecipe.Float64Max(left, right) -> two ExpressionRecipe.Float64Max left right
            | ExpressionRecipe.Float64Subtract(left, right) -> two ExpressionRecipe.Float64Subtract left right
            | ExpressionRecipe.Float64Multiply(left, right) -> two ExpressionRecipe.Float64Multiply left right
            | ExpressionRecipe.Float64Divide(left, right) -> two ExpressionRecipe.Float64Divide left right
            | ExpressionRecipe.Float64Equal(left, right) -> two ExpressionRecipe.Float64Equal left right
            | ExpressionRecipe.Float64LessThan(left, right) -> two ExpressionRecipe.Float64LessThan left right
            | ExpressionRecipe.ListAppendItem(list, item) -> two ExpressionRecipe.ListAppendItem list item
            | ExpressionRecipe.ListItemAt(list, index) -> two ExpressionRecipe.ListItemAt list index
            | ExpressionRecipe.MapRemove(map, key) -> two ExpressionRecipe.MapRemove map key
            | ExpressionRecipe.SetInsert(map, key) -> two ExpressionRecipe.SetInsert map key
            | ExpressionRecipe.SetRemove(map, key) -> two ExpressionRecipe.SetRemove map key
            | ExpressionRecipe.MapContainsKey(map, key) -> two ExpressionRecipe.MapContainsKey map key
            | ExpressionRecipe.SetContains(map, key) -> two ExpressionRecipe.SetContains map key
            | ExpressionRecipe.ListIndexOf(map, key) -> two ExpressionRecipe.ListIndexOf map key
            | ExpressionRecipe.ListLastIndexOf(map, key) -> two ExpressionRecipe.ListLastIndexOf map key
            | ExpressionRecipe.MapGet(map, key) -> two ExpressionRecipe.MapGet map key
            | ExpressionRecipe.ListSlice(list, start, stop) -> three ExpressionRecipe.ListSlice list start stop
            | ExpressionRecipe.ListSetAt(list, index, item) -> three ExpressionRecipe.ListSetAt list index item
            | ExpressionRecipe.ListInsertAt(list, index, item) -> three ExpressionRecipe.ListInsertAt list index item
            | ExpressionRecipe.ListRemoveAt(list, index) -> two ExpressionRecipe.ListRemoveAt list index
            | ExpressionRecipe.MapInsert(map, key, value) -> three ExpressionRecipe.MapInsert map key value
            | ExpressionRecipe.MapGetOrDefault(map, key, defaultValue) -> three ExpressionRecipe.MapGetOrDefault map key defaultValue
            | ExpressionRecipe.OptionGetOrDefault(option, defaultValue) -> two ExpressionRecipe.OptionGetOrDefault option defaultValue
            | ExpressionRecipe.ListConcat(left, right) -> two ExpressionRecipe.ListConcat left right
            | ExpressionRecipe.SetUnion(left, right) -> two ExpressionRecipe.SetUnion left right
            | ExpressionRecipe.SetIntersection(left, right) -> two ExpressionRecipe.SetIntersection left right
            | ExpressionRecipe.SetDifference(left, right) -> two ExpressionRecipe.SetDifference left right
            | ExpressionRecipe.SetIsSubset(left, right) -> two ExpressionRecipe.SetIsSubset left right
            | ExpressionRecipe.MapUnion(left, right) -> two ExpressionRecipe.MapUnion left right
            | ExpressionRecipe.Bind(value, body) -> two ExpressionRecipe.Bind value body
            | ExpressionRecipe.ListConstruct(itemType, items) ->
                rewriteAll items |> Result.map (fun items -> ExpressionRecipe.ListConstruct(itemType, items))
            | ExpressionRecipe.ListFold(source, seed, folder, context) ->
                nonCallTargetUse folder
                |> Result.bind (fun () -> two (fun (source, seed) -> source, seed) source seed)
                |> Result.bind (fun (source, seed) ->
                    rewritePairs context |> Result.map (fun context -> ExpressionRecipe.ListFold(source, seed, folder, context)))
            | ExpressionRecipe.MapFold(source, seed, folder, context) ->
                nonCallTargetUse folder
                |> Result.bind (fun () -> two (fun (source, seed) -> source, seed) source seed)
                |> Result.bind (fun (source, seed) ->
                    rewritePairs context |> Result.map (fun context -> ExpressionRecipe.MapFold(source, seed, folder, context)))
            | ExpressionRecipe.ListEffectFold(source, seed, folder, context) ->
                nonCallTargetUse folder
                |> Result.bind (fun () -> two (fun (source, seed) -> source, seed) source seed)
                |> Result.bind (fun (source, seed) ->
                    rewritePairs context |> Result.map (fun context -> ExpressionRecipe.ListEffectFold(source, seed, folder, context)))
            | ExpressionRecipe.UseResource(acquire, releaseCapability, operationId, bodyFunctionId, context) ->
                nonCallTargetUse bodyFunctionId
                |> Result.bind (fun () -> two (fun (acquire, releaseCapability) -> acquire, releaseCapability) acquire releaseCapability)
                |> Result.bind (fun (acquire, releaseCapability) ->
                    rewritePairs context
                    |> Result.map (fun context -> ExpressionRecipe.UseResource(acquire, releaseCapability, operationId, bodyFunctionId, context)))
            | ExpressionRecipe.ParallelAll(source, functionId, context) -> callback ExpressionRecipe.ParallelAll source functionId context
            | ExpressionRecipe.CancellationRequested -> Ok recipe
            | ExpressionRecipe.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
                nonCallTargetUse body
                |> Result.bind (fun () -> nonCallTargetUse handler)
                |> Result.bind (fun () -> rewritePairs bodyContext)
                |> Result.bind (fun bodyContext -> rewritePairs handlerContext |> Result.map (fun handlerContext -> ExpressionRecipe.ExceptionCatch(body, bodyContext, handler, handlerContext)))
            | ExpressionRecipe.ExceptionIsCancellation value -> rewrite value |> Result.map ExpressionRecipe.ExceptionIsCancellation
            | ExpressionRecipe.ExceptionMessage value -> rewrite value |> Result.map ExpressionRecipe.ExceptionMessage
            | ExpressionRecipe.ExceptionRethrow(value, resultType) -> rewrite value |> Result.map (fun value -> ExpressionRecipe.ExceptionRethrow(value, resultType))
            | ExpressionRecipe.CancellationShield(bodyFunctionId, context) ->
                nonCallTargetUse bodyFunctionId
                |> Result.bind (fun () -> rewritePairs context |> Result.map (fun context -> ExpressionRecipe.CancellationShield(bodyFunctionId, context)))
            | ExpressionRecipe.TaskScope(bodyFunctionId, context) ->
                nonCallTargetUse bodyFunctionId
                |> Result.bind (fun () -> rewritePairs context |> Result.map (fun context -> ExpressionRecipe.TaskScope(bodyFunctionId, context)))
            | ExpressionRecipe.TaskStart(scope, workerFunctionId, context, item) ->
                nonCallTargetUse workerFunctionId
                |> Result.bind (fun () -> two (fun (scope, item) -> scope, item) scope item)
                |> Result.bind (fun (scope, item) ->
                    rewritePairs context |> Result.map (fun context -> ExpressionRecipe.TaskStart(scope, workerFunctionId, context, item)))
            | ExpressionRecipe.TaskWait(tasks, mode, None) -> one (fun tasks -> ExpressionRecipe.TaskWait(tasks, mode, None)) tasks
            | ExpressionRecipe.TaskWait(tasks, mode, Some limit) ->
                two (fun (tasks, limit) -> ExpressionRecipe.TaskWait(tasks, mode, Some limit)) tasks limit
            | ExpressionRecipe.OneShotCreate(scope, valueType) -> one (fun scope -> ExpressionRecipe.OneShotCreate(scope, valueType)) scope
            | ExpressionRecipe.OneShotComplete(cell, value) -> two ExpressionRecipe.OneShotComplete cell value
            | ExpressionRecipe.OneShotWait(cell, None) -> one (fun cell -> ExpressionRecipe.OneShotWait(cell, None)) cell
            | ExpressionRecipe.OneShotWait(cell, Some limit) -> two (fun (cell, limit) -> ExpressionRecipe.OneShotWait(cell, Some limit)) cell limit
            | ExpressionRecipe.TaskCancel task -> one ExpressionRecipe.TaskCancel task
            | ExpressionRecipe.ListMap(source, functionId, context) -> callback ExpressionRecipe.ListMap source functionId context
            | ExpressionRecipe.ListFlatMap(source, functionId, context) -> callback ExpressionRecipe.ListFlatMap source functionId context
            | ExpressionRecipe.ListZipWith(left, right, combiner, context) ->
                nonCallTargetUse combiner
                |> Result.bind (fun () -> two (fun (left, right) -> left, right) left right)
                |> Result.bind (fun (left, right) ->
                    rewritePairs context |> Result.map (fun context -> ExpressionRecipe.ListZipWith(left, right, combiner, context)))
            | ExpressionRecipe.MapFromList(source, functionId, context) -> callback ExpressionRecipe.MapFromList source functionId context
            | ExpressionRecipe.ListFilter(source, functionId, context) -> callback ExpressionRecipe.ListFilter source functionId context
            | ExpressionRecipe.MapFilter(source, functionId, context) -> callback ExpressionRecipe.MapFilter source functionId context
            | ExpressionRecipe.ListSortBy(source, functionId, context) -> callback ExpressionRecipe.ListSortBy source functionId context
            | ExpressionRecipe.MapMapValues(source, functionId, context) -> callback ExpressionRecipe.MapMapValues source functionId context
            | ExpressionRecipe.OptionMap(source, functionId, context) -> callback ExpressionRecipe.OptionMap source functionId context
            | ExpressionRecipe.OptionAndThen(source, functionId, context) -> callback ExpressionRecipe.OptionAndThen source functionId context
            | ExpressionRecipe.ResultMap(source, functionId, context) -> callback ExpressionRecipe.ResultMap source functionId context
            | ExpressionRecipe.ResultMapErr(source, functionId, context) -> callback ExpressionRecipe.ResultMapErr source functionId context
            | ExpressionRecipe.ResultAndThen(source, functionId, context) -> callback ExpressionRecipe.ResultAndThen source functionId context
            | ExpressionRecipe.ListMinBy(source, functionId, context) -> callback ExpressionRecipe.ListMinBy source functionId context
            | ExpressionRecipe.ListAny(source, functionId, context) -> callback ExpressionRecipe.ListAny source functionId context
            | ExpressionRecipe.ListFindIndex(source, functionId, context) -> callback ExpressionRecipe.ListFindIndex source functionId context
            | ExpressionRecipe.ListAll(source, functionId, context) -> callback ExpressionRecipe.ListAll source functionId context
            | ExpressionRecipe.ProductConstruct(typeId, fields) ->
                rewritePairs fields |> Result.map (fun fields -> ExpressionRecipe.ProductConstruct(typeId, fields))
            | ExpressionRecipe.FieldAccess(source, fieldId) -> one (fun source -> ExpressionRecipe.FieldAccess(source, fieldId)) source
            | ExpressionRecipe.ProductUpdate(typeId, source, fields) ->
                match rewrite source with
                | Error error -> Error error
                | Ok source -> rewritePairs fields |> Result.map (fun fields -> ExpressionRecipe.ProductUpdate(typeId, source, fields))
            | ExpressionRecipe.UnionConstruct(variantId, fields) ->
                rewritePairs fields |> Result.map (fun fields -> ExpressionRecipe.UnionConstruct(variantId, fields))
            | ExpressionRecipe.ResultOk(errorType, value) -> one (fun value -> ExpressionRecipe.ResultOk(errorType, value)) value
            | ExpressionRecipe.Abort error -> one ExpressionRecipe.Abort error
            | ExpressionRecipe.ResultErr(okType, error) -> one (fun error -> ExpressionRecipe.ResultErr(okType, error)) error
            | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
                rewrite capabilityValue
                |> Result.bind (fun capabilityValue ->
                    rewritePairs arguments
                    |> Result.map (fun arguments -> ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments)))
            | ExpressionRecipe.BoolNot operand -> one ExpressionRecipe.BoolNot operand
            | ExpressionRecipe.BoolAnd(left, right) -> two ExpressionRecipe.BoolAnd left right
            | ExpressionRecipe.BoolOr(left, right) -> two ExpressionRecipe.BoolOr left right
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

    /// Creation mints fresh type parameters for a temporary copy; matching the created signature
    /// against the requested one maps every fresh type parameter back to the original owner's.
    let temporaryTypeParameters
        (request: AgentExpressionStructuralWorkflow.FunctionCreation.Request)
        (created: SemanticFunction)
        : Map<TypeParameterId, TypeRef> =
        let requested = request.ResultType :: (request.Parameters |> List.map (fun parameter -> parameter.Type))
        let materialized = created.ResultType :: (created.Parameters |> List.map (fun parameter -> parameter.Type))
        List.zip materialized requested
        |> List.fold
            (fun substitution (fresh, original) ->
                substitution |> Option.bind (fun current -> TypeParameters.matchType current fresh original))
            (Some Map.empty)
        |> Option.defaultValue Map.empty

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
                prepared.Target.Id
                TemporaryOwner.Target
                targetRecipe
        with
        | Error error when error.Code = unsupportedRewriteUseCode prefix ->
            Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
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
                AgentExpressionStructuralWorkflow.FunctionCreation.simulateReplacement
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
                          TemporaryToOriginalParameters = temporaryToOriginal
                          TemporaryToOriginalTypeParameters = temporaryTypeParameters creationRequest created }

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
                targetFunctionId
                (TemporaryOwner.Caller(
                    caller.Id,
                    temporaryTarget.Function.Id,
                    temporaryTarget.OriginalToTemporaryParameters
                ))
                replacement
        with
        | Error error when error.Code = unsupportedRewriteUseCode prefix ->
            Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
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
                AgentExpressionStructuralWorkflow.FunctionCreation.simulateReplacement
                    temporaryTarget.Workspace
                    creationRequest
            with
            | Error error ->
                wrapStructuralError
                    $"{prefix}CallerRewriteInvalid"
                    $"Caller '{StableId.value caller.Id}' rewrite is invalid"
                    error
            | Ok proposal ->
                let created =
                    proposal
                    |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
                    |> fun review -> review.CreatedFunction
                Ok
                    { Function = created
                      TemporaryToOriginalTypeParameters = temporaryTypeParameters creationRequest created }

    /// Maps a temporary copy's body back onto its original owner: parameter identities, the
    /// owner's type parameters, and calls to the temporary target.
    let rec remapGraph
        (ownerParameterMap: Map<ParameterId, ParameterId>)
        (ownerTypeParameters: Map<TypeParameterId, TypeRef>)
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

        let mapType = TypeParameters.substitute ownerTypeParameters
        let mapBinding (binding: PatternBinding) = { binding with Type = mapType binding.Type }
        let remap = remapGraph ownerParameterMap ownerTypeParameters temporaryTarget targetFunctionId

        let mapNode (node: SemanticNode) =
            let node = SemanticNodeShape.mapNested mapBinding remap node
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
                | SemanticNodeKind.ListEmpty itemType -> SemanticNodeKind.ListEmpty(mapType itemType)
                | SemanticNodeKind.SetEmpty itemType -> SemanticNodeKind.SetEmpty(mapType itemType)
                | SemanticNodeKind.MapEmpty(keyType, valueType) -> SemanticNodeKind.MapEmpty(mapType keyType, mapType valueType)
                | SemanticNodeKind.ListConstruct(itemType, items) -> SemanticNodeKind.ListConstruct(mapType itemType, items)
                | SemanticNodeKind.OptionNone itemType -> SemanticNodeKind.OptionNone(mapType itemType)
                | SemanticNodeKind.ResultOk(errorType, valueNodeId) -> SemanticNodeKind.ResultOk(mapType errorType, valueNodeId)
                | SemanticNodeKind.ResultErr(okType, errorNodeId) -> SemanticNodeKind.ResultErr(mapType okType, errorNodeId)
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
                    | Ok allCallerSites ->
                        // The target body is replaced as a whole, so its own uses of itself are
                        // neither callers to rewrite nor affected call sites.
                        let callerSites = allCallerSites |> List.filter (fun site -> site.CallerFunctionId <> target.Id)
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
          Prepared: Preflight
          BodyRewrites: FunctionBodyReplacementDiff list }

    /// Materializes the migrated target and every caller through temporary functions, then maps
    /// them back onto the original identities: the migrated target and the migrated callers.
    let private migrate
        (prefix: string)
        (workspace: AgentWorkspace)
        (prepared: Preflight)
        (parameterTypes: Map<ParameterId, TypeRef>)
        (resultType: TypeRef)
        targetReplacement
        : Result<SemanticFunction * Map<FunctionId, SemanticFunction>, AgentStructuralWorkflowError> =
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
                        Body =
                            remapGraph
                                (ownerMap temporaryTarget.Function prepared.Target)
                                temporaryTarget.TemporaryToOriginalTypeParameters
                                temporaryTarget
                                prepared.Target.Id
                                temporaryTarget.Function.Body }
                let migratedCallers =
                    temporaryCallers
                    |> List.map (fun ((original: SemanticFunction), (temporary: TemporaryCaller)) ->
                        original.Id,
                        { original with
                            Body =
                                remapGraph
                                    (ownerMap temporary.Function original)
                                    temporary.TemporaryToOriginalTypeParameters
                                    temporaryTarget
                                    prepared.Target.Id
                                    temporary.Function.Body })
                    |> Map.ofList
                Ok(migratedTarget, migratedCallers)

    /// The signature-migration simulate pipeline: preflight against the base, migrate the target
    /// and its callers, then validate and review the proposed snapshot as a whole.
    let simulate
        (prefix: string)
        (workspace: AgentWorkspace)
        (expectedBaseSnapshotId: SnapshotId)
        (targetFunctionId: FunctionId)
        (callerRewrites: AgentParameterTypeCallerRewrite list)
        (checkTarget: SemanticFunction -> Result<unit, AgentStructuralWorkflowError>)
        (parameterTypes: Map<ParameterId, TypeRef>)
        (resultType: SemanticFunction -> TypeRef)
        targetReplacement
        : Result<Simulated, AgentStructuralWorkflowError> =
        simulateStructural
            workspace
            (fun baseSnapshot ->
                preflight prefix baseSnapshot expectedBaseSnapshotId targetFunctionId callerRewrites checkTarget
                |> Result.bind (fun prepared ->
                    migrate prefix workspace prepared parameterTypes (resultType prepared.Target) targetReplacement
                    |> Result.map (fun (migratedTarget, migratedCallers) -> prepared, migratedTarget, migratedCallers)))
            (fun (_, migratedTarget, migratedCallers) snapshot ->
                { snapshot with
                    Functions =
                        snapshot.Functions
                        |> List.map (fun functionEntity ->
                            if functionEntity.Id = migratedTarget.Id then migratedTarget
                            else Map.tryFind functionEntity.Id migratedCallers |> Option.defaultValue functionEntity) })
        |> Result.map (fun candidate ->
            let prepared, _, _ = candidate.Prepared
            let proposed functionId = candidate.ProposedSnapshot.Functions |> List.find (fun fn -> fn.Id = functionId)
            { TransactionId = candidate.TransactionId
              ProposedSnapshot = candidate.ProposedSnapshot
              Receipt = candidate.Receipt
              Readiness = candidate.Readiness
              Prepared = prepared
              BodyRewrites =
                prepared.Target :: prepared.Callers
                |> List.map (fun original -> bodyDiff candidate.BaseSnapshot candidate.ProposedSnapshot original (proposed original.Id)) })

[<RequireQualifiedAccess>]
module AgentParameterTypeMigrationWorkflow =
    open StructuralTransactionInternals

    let private prefix = "ParameterTypeMigration"

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentParameterTypeMigrationRequest)
        : Result<AgentParameterTypeMigrationProposal, AgentStructuralWorkflowError> =
        let checkParameter (target: SemanticFunction) =
            match target.Parameters |> List.tryFind (fun parameter -> parameter.Id = request.TargetParameterId) with
            | None -> SignatureMigrationCore.reject $"{prefix}ParameterNotFound" "The target parameter is not owned by the target function."
            | Some parameter when parameter.Type = request.NewParameterType ->
                SignatureMigrationCore.reject $"{prefix}TypeUnchanged" "The requested parameter type is already the current type."
            | Some _ -> Ok()
        SignatureMigrationCore.simulate
            prefix workspace request.ExpectedBaseSnapshotId request.TargetFunctionId request.CallerRewrites checkParameter
            (Map.ofList [ request.TargetParameterId, request.NewParameterType ])
            (fun target -> target.ResultType)
            request.TargetReplacement
        |> Result.map (fun simulated ->
            let prepared = simulated.Prepared
            let parameter = prepared.Target.Parameters |> List.find (fun item -> item.Id = request.TargetParameterId)
            AgentParameterTypeMigrationProposal
                { TransactionId = simulated.TransactionId
                  ProposedSnapshot = simulated.ProposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = simulated.Receipt
                      Readiness = simulated.Readiness
                      FunctionEffects = EffectRequirements.summarize simulated.ProposedSnapshot (simulated.BodyRewrites |> List.map (fun diff -> diff.OwnerFunctionId) |> Set.ofList)
                      ResultingChange =
                        { ParameterChange =
                            { FunctionId = prepared.Target.Id
                              ParameterId = parameter.Id
                              OldType = parameter.Type
                              NewType = request.NewParameterType }
                          AffectedCallSites = prepared.CallerSites
                          BodyRewrites = simulated.BodyRewrites } } })

    let private publishWithPolicy
        workspace
        (AgentParameterTypeMigrationProposal proposal)
        policy
        =
        match publishCandidateWithPolicy workspace proposal.ProposedSnapshot proposal.Review.Readiness proposal.Review.Receipt policy with
        | Error diagnostics ->
            AgentParameterTypeMigrationCommitResult.Rejected diagnostics
        | Ok nextWorkspace ->
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
        let checkResult (target: SemanticFunction) =
            if target.ResultType = request.NewResultType then
                SignatureMigrationCore.reject $"{prefix}TypeUnchanged" "The requested result type is already the current type."
            else Ok()
        SignatureMigrationCore.simulate
            prefix workspace request.ExpectedBaseSnapshotId request.TargetFunctionId request.CallerRewrites checkResult
            Map.empty
            (fun _ -> request.NewResultType)
            request.TargetReplacement
        |> Result.map (fun simulated ->
            let prepared = simulated.Prepared
            AgentResultTypeMigrationProposal
                { TransactionId = simulated.TransactionId
                  ProposedSnapshot = simulated.ProposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = simulated.Receipt
                      Readiness = simulated.Readiness
                      FunctionEffects = EffectRequirements.summarize simulated.ProposedSnapshot (simulated.BodyRewrites |> List.map (fun diff -> diff.OwnerFunctionId) |> Set.ofList)
                      ResultingChange =
                        { ResultChange =
                            { FunctionId = prepared.Target.Id
                              OldType = prepared.Target.ResultType
                              NewType = request.NewResultType }
                          AffectedCallSites = prepared.CallerSites
                          BodyRewrites = simulated.BodyRewrites } } })

    let commitWithPolicy workspace (AgentResultTypeMigrationProposal proposal) policy =
        match publishCandidateWithPolicy workspace proposal.ProposedSnapshot proposal.Review.Readiness proposal.Review.Receipt policy with
        | Error diagnostics -> AgentResultTypeMigrationCommitResult.Rejected diagnostics
        | Ok nextWorkspace ->
            AgentResultTypeMigrationCommitResult.Committed(
                nextWorkspace,
                { TransactionId = proposal.TransactionId
                  BaseSnapshotId = proposal.Review.Receipt.BaseSnapshotId
                  PublishedSnapshotId = proposal.ProposedSnapshot.Id
                  Diff = proposal.Review.ResultingChange
                  Readiness = proposal.Review.Readiness
                  Receipt = proposal.Review.Receipt })

    let commit workspace proposal = commitWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations
