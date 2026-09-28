namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type RecipePatternSlot =
    | Field of FieldId
    | Value
    | Error

type RecipePatternReference =
    { ScopeDepth: int
      Slot: RecipePatternSlot }

/// Ephemeral typed construction intent for bounded cross-domain expressions.
/// Child expressions are nested recipes; production graph-node and pattern-binding
/// IDs remain system-owned. Effects must already be declared by the target function.
[<RequireQualifiedAccess>]
type ExpressionRecipe =
    | Parameter of ParameterId
    | ParameterSlot of int
    | PatternReference of RecipePatternReference
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of ExpressionRecipe
    | Int64ToString of ExpressionRecipe
    | StringParseInt64 of ExpressionRecipe
    | StringLength of ExpressionRecipe
    | StringSlice of value: ExpressionRecipe * start: ExpressionRecipe * ``end``: ExpressionRecipe
    | StringContains of ExpressionRecipe * ExpressionRecipe
    | StringEqual of ExpressionRecipe * ExpressionRecipe
    | StringLessThan of ExpressionRecipe * ExpressionRecipe
    | ValueEqual of ExpressionRecipe * ExpressionRecipe
    | StringConcat of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingAdd of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingSubtract of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingMultiply of ExpressionRecipe * ExpressionRecipe
    | Int64FloorDivide of ExpressionRecipe * ExpressionRecipe
    | Int64FloorModulo of ExpressionRecipe * ExpressionRecipe
    | Int64Equal of ExpressionRecipe * ExpressionRecipe
    | Int64LessThan of ExpressionRecipe * ExpressionRecipe
    | Int64LessThanOrEqual of ExpressionRecipe * ExpressionRecipe
    | Int64GreaterThan of ExpressionRecipe * ExpressionRecipe
    | Int64GreaterThanOrEqual of ExpressionRecipe * ExpressionRecipe
    | Float64Add of ExpressionRecipe * ExpressionRecipe
    | Float64Subtract of ExpressionRecipe * ExpressionRecipe
    | Float64Multiply of ExpressionRecipe * ExpressionRecipe
    | Float64Divide of ExpressionRecipe * ExpressionRecipe
    | Float64Equal of ExpressionRecipe * ExpressionRecipe
    | Float64LessThan of ExpressionRecipe * ExpressionRecipe
    | Float64IsNaN of ExpressionRecipe
    | Float64IsFinite of ExpressionRecipe
    | Float64IsInfinite of ExpressionRecipe
    | Int64ToFloat64 of ExpressionRecipe
    | ListEmpty of itemType: TypeRef
    | ListConstruct of itemType: TypeRef * items: ExpressionRecipe list
    | ListLength of ExpressionRecipe
    | ListAppendItem of list: ExpressionRecipe * item: ExpressionRecipe
    | ListItemAt of list: ExpressionRecipe * index: ExpressionRecipe
    | ListConcat of left: ExpressionRecipe * right: ExpressionRecipe
    /// `context` binds callback parameters by identity; the unbound ones receive iteration values.
    | ListFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ParallelAll of source: ExpressionRecipe * workerFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | CancellationRequested
    /// The body's one unbound parameter receives the new `TaskScope`.
    | TaskScope of bodyFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | TaskStart of scope: ExpressionRecipe * workerFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list * item: ExpressionRecipe
    /// `limit` is an optional `Option<Int64>` of milliseconds; absent means no limit.
    | TaskWait of tasks: ExpressionRecipe * mode: TaskWaitMode * limit: ExpressionRecipe option
    | TaskCancel of task: ExpressionRecipe
    | UseResource of acquire: ExpressionRecipe * releaseCapability: ExpressionRecipe * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListEffectFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListMap of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListFilter of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListSortBy of source: ExpressionRecipe * lessThanFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListAny of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListAll of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ProductConstruct of typeId: TypeId * fields: (FieldId * ExpressionRecipe) list
    | FieldAccess of source: ExpressionRecipe * fieldId: FieldId
    | UnionConstruct of variantId: VariantId * fields: (FieldId * ExpressionRecipe) list
    | OptionNone of itemType: TypeRef
    | OptionSome of ExpressionRecipe
    | ResultOk of errorType: TypeRef * value: ExpressionRecipe
    | Abort of error: ExpressionRecipe
    | ResultErr of okType: TypeRef * error: ExpressionRecipe
    /// Construct a stateless capability using named pure functions, one per operation.
    | CapabilityConstruct of CapabilityId * implementations: (CapabilityOperationId * FunctionId) list
    | FunctionReference of FunctionId
    | FunctionInvoke of functionValue: ExpressionRecipe * arguments: ExpressionRecipe list
    | DirectCall of targetFunctionId: FunctionId * arguments: (ParameterId * ExpressionRecipe) list
    /// A direct call to the owning function, with arguments keyed by zero-based parameter slot.
    /// It materializes to an ordinary DirectCall once the owner's identities are known, so a
    /// recursive function can be created in one request.
    | SelfCall of arguments: (int * ExpressionRecipe) list
    | CapabilityInvoke of capabilityValue: ExpressionRecipe * operationId: CapabilityOperationId * arguments: (CapabilityParameterId * ExpressionRecipe) list
    | BoolNot of ExpressionRecipe
    /// The right operand becomes a nested value graph evaluated only when left is true.
    | BoolAnd of left: ExpressionRecipe * right: ExpressionRecipe
    /// The right operand becomes a nested value graph evaluated only when left is false.
    | BoolOr of left: ExpressionRecipe * right: ExpressionRecipe
    | Conditional of condition: ExpressionRecipe * whenTrue: ExpressionRecipe * whenFalse: ExpressionRecipe
    | MatchUnion of input: ExpressionRecipe * arms: (VariantId * ExpressionRecipe) list
    | Bind of value: ExpressionRecipe * body: ExpressionRecipe
    | MatchOption of input: ExpressionRecipe * someBody: ExpressionRecipe * noneBody: ExpressionRecipe
    | MatchResult of input: ExpressionRecipe * okBody: ExpressionRecipe * errBody: ExpressionRecipe

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

/// Structural rewriting of the type references embedded in an expression recipe.
[<RequireQualifiedAccess>]
module ExpressionRecipeTypes =
    let rec map (mapType: TypeRef -> TypeRef) (recipe: ExpressionRecipe): ExpressionRecipe =
        let go = map mapType
        let pairs values = values |> List.map (fun (id, value) -> id, go value)
        match recipe with
        | ExpressionRecipe.Parameter _
        | ExpressionRecipe.ParameterSlot _
        | ExpressionRecipe.PatternReference _
        | ExpressionRecipe.StringLiteral _
        | ExpressionRecipe.BoolLiteral _
        | ExpressionRecipe.UnitLiteral
        | ExpressionRecipe.Int64Literal _
        | ExpressionRecipe.Float64Literal _ -> recipe
        | ExpressionRecipe.StringIsEmpty value -> ExpressionRecipe.StringIsEmpty(go value)
        | ExpressionRecipe.Int64ToString value -> ExpressionRecipe.Int64ToString(go value)
        | ExpressionRecipe.StringParseInt64 value -> ExpressionRecipe.StringParseInt64(go value)
        | ExpressionRecipe.StringLength value -> ExpressionRecipe.StringLength(go value)
        | ExpressionRecipe.StringSlice(value, start, stop) -> ExpressionRecipe.StringSlice(go value, go start, go stop)
        | ExpressionRecipe.StringContains(left, right) -> ExpressionRecipe.StringContains(go left, go right)
        | ExpressionRecipe.StringEqual(left, right) -> ExpressionRecipe.StringEqual(go left, go right)
        | ExpressionRecipe.StringLessThan(left, right) -> ExpressionRecipe.StringLessThan(go left, go right)
        | ExpressionRecipe.ValueEqual(left, right) -> ExpressionRecipe.ValueEqual(go left, go right)
        | ExpressionRecipe.StringConcat(left, right) -> ExpressionRecipe.StringConcat(go left, go right)
        | ExpressionRecipe.Int64WrappingAdd(left, right) -> ExpressionRecipe.Int64WrappingAdd(go left, go right)
        | ExpressionRecipe.Int64WrappingSubtract(left, right) -> ExpressionRecipe.Int64WrappingSubtract(go left, go right)
        | ExpressionRecipe.Int64WrappingMultiply(left, right) -> ExpressionRecipe.Int64WrappingMultiply(go left, go right)
        | ExpressionRecipe.Int64FloorDivide(left, right) -> ExpressionRecipe.Int64FloorDivide(go left, go right)
        | ExpressionRecipe.Int64FloorModulo(left, right) -> ExpressionRecipe.Int64FloorModulo(go left, go right)
        | ExpressionRecipe.Int64Equal(left, right) -> ExpressionRecipe.Int64Equal(go left, go right)
        | ExpressionRecipe.Int64LessThan(left, right) -> ExpressionRecipe.Int64LessThan(go left, go right)
        | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> ExpressionRecipe.Int64LessThanOrEqual(go left, go right)
        | ExpressionRecipe.Int64GreaterThan(left, right) -> ExpressionRecipe.Int64GreaterThan(go left, go right)
        | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> ExpressionRecipe.Int64GreaterThanOrEqual(go left, go right)
        | ExpressionRecipe.Float64Add(left, right) -> ExpressionRecipe.Float64Add(go left, go right)
        | ExpressionRecipe.Float64Subtract(left, right) -> ExpressionRecipe.Float64Subtract(go left, go right)
        | ExpressionRecipe.Float64Multiply(left, right) -> ExpressionRecipe.Float64Multiply(go left, go right)
        | ExpressionRecipe.Float64Divide(left, right) -> ExpressionRecipe.Float64Divide(go left, go right)
        | ExpressionRecipe.Float64Equal(left, right) -> ExpressionRecipe.Float64Equal(go left, go right)
        | ExpressionRecipe.Float64LessThan(left, right) -> ExpressionRecipe.Float64LessThan(go left, go right)
        | ExpressionRecipe.Float64IsNaN value -> ExpressionRecipe.Float64IsNaN(go value)
        | ExpressionRecipe.Float64IsFinite value -> ExpressionRecipe.Float64IsFinite(go value)
        | ExpressionRecipe.Float64IsInfinite value -> ExpressionRecipe.Float64IsInfinite(go value)
        | ExpressionRecipe.Int64ToFloat64 value -> ExpressionRecipe.Int64ToFloat64(go value)
        | ExpressionRecipe.ListEmpty itemType -> ExpressionRecipe.ListEmpty(mapType itemType)
        | ExpressionRecipe.ListConstruct(itemType, items) -> ExpressionRecipe.ListConstruct(mapType itemType, List.map go items)
        | ExpressionRecipe.ListLength value -> ExpressionRecipe.ListLength(go value)
        | ExpressionRecipe.ListAppendItem(list, item) -> ExpressionRecipe.ListAppendItem(go list, go item)
        | ExpressionRecipe.ListItemAt(list, index) -> ExpressionRecipe.ListItemAt(go list, go index)
        | ExpressionRecipe.ListConcat(left, right) -> ExpressionRecipe.ListConcat(go left, go right)
        | ExpressionRecipe.ListFold(source, seed, folder, context) -> ExpressionRecipe.ListFold(go source, go seed, folder, pairs context)
        | ExpressionRecipe.UseResource(acquire, capability, operation, body, context) ->
            ExpressionRecipe.UseResource(go acquire, go capability, operation, body, pairs context)
        | ExpressionRecipe.ParallelAll(source, worker, context) -> ExpressionRecipe.ParallelAll(go source, worker, pairs context)
        | ExpressionRecipe.CancellationRequested -> recipe
        | ExpressionRecipe.TaskScope(body, context) -> ExpressionRecipe.TaskScope(body, pairs context)
        | ExpressionRecipe.TaskStart(scope, worker, context, item) -> ExpressionRecipe.TaskStart(go scope, worker, pairs context, go item)
        | ExpressionRecipe.TaskWait(tasks, mode, limit) -> ExpressionRecipe.TaskWait(go tasks, mode, Option.map go limit)
        | ExpressionRecipe.TaskCancel task -> ExpressionRecipe.TaskCancel(go task)
        | ExpressionRecipe.ListEffectFold(source, seed, folder, context) -> ExpressionRecipe.ListEffectFold(go source, go seed, folder, pairs context)
        | ExpressionRecipe.ListMap(source, callback, context) -> ExpressionRecipe.ListMap(go source, callback, pairs context)
        | ExpressionRecipe.ListFilter(source, callback, context) -> ExpressionRecipe.ListFilter(go source, callback, pairs context)
        | ExpressionRecipe.ListSortBy(source, callback, context) -> ExpressionRecipe.ListSortBy(go source, callback, pairs context)
        | ExpressionRecipe.ListAny(source, callback, context) -> ExpressionRecipe.ListAny(go source, callback, pairs context)
        | ExpressionRecipe.ListAll(source, callback, context) -> ExpressionRecipe.ListAll(go source, callback, pairs context)
        | ExpressionRecipe.ProductConstruct(typeId, fields) -> ExpressionRecipe.ProductConstruct(typeId, pairs fields)
        | ExpressionRecipe.FieldAccess(source, fieldId) -> ExpressionRecipe.FieldAccess(go source, fieldId)
        | ExpressionRecipe.UnionConstruct(variantId, fields) -> ExpressionRecipe.UnionConstruct(variantId, pairs fields)
        | ExpressionRecipe.OptionNone itemType -> ExpressionRecipe.OptionNone(mapType itemType)
        | ExpressionRecipe.OptionSome value -> ExpressionRecipe.OptionSome(go value)
        | ExpressionRecipe.ResultOk(errorType, value) -> ExpressionRecipe.ResultOk(mapType errorType, go value)
        | ExpressionRecipe.Abort error -> ExpressionRecipe.Abort(go error)
        | ExpressionRecipe.ResultErr(okType, error) -> ExpressionRecipe.ResultErr(mapType okType, go error)
        | ExpressionRecipe.CapabilityConstruct _
        | ExpressionRecipe.FunctionReference _ -> recipe
        | ExpressionRecipe.FunctionInvoke(value, arguments) -> ExpressionRecipe.FunctionInvoke(go value, List.map go arguments)
        | ExpressionRecipe.DirectCall(functionId, arguments) -> ExpressionRecipe.DirectCall(functionId, pairs arguments)
        | ExpressionRecipe.SelfCall arguments -> ExpressionRecipe.SelfCall(pairs arguments)
        | ExpressionRecipe.CapabilityInvoke(capability, operation, arguments) ->
            ExpressionRecipe.CapabilityInvoke(go capability, operation, arguments |> List.map (fun (id, value) -> id, go value))
        | ExpressionRecipe.BoolNot operand -> ExpressionRecipe.BoolNot(go operand)
        | ExpressionRecipe.BoolAnd(left, right) -> ExpressionRecipe.BoolAnd(go left, go right)
        | ExpressionRecipe.BoolOr(left, right) -> ExpressionRecipe.BoolOr(go left, go right)
        | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) -> ExpressionRecipe.Conditional(go condition, go whenTrue, go whenFalse)
        | ExpressionRecipe.MatchUnion(input, arms) -> ExpressionRecipe.MatchUnion(go input, pairs arms)
        | ExpressionRecipe.Bind(value, body) -> ExpressionRecipe.Bind(go value, go body)
        | ExpressionRecipe.MatchOption(input, someBody, noneBody) -> ExpressionRecipe.MatchOption(go input, go someBody, go noneBody)
        | ExpressionRecipe.MatchResult(input, okBody, errBody) -> ExpressionRecipe.MatchResult(go input, go okBody, go errBody)

    /// Type references embedded directly in a recipe, including nested recipes.
    let collect (recipe: ExpressionRecipe): TypeRef list =
        let collected = System.Collections.Generic.List<TypeRef>()
        map (fun typeRef -> collected.Add typeRef; typeRef) recipe |> ignore
        List.ofSeq collected

[<RequireQualifiedAccess>]
module AgentExpressionStructuralWorkflow =
    open StructuralTransactionInternals

    type private PatternTypeFrame = (RecipePatternSlot * TypeRef) list
    type private PatternBindingFrame = (RecipePatternSlot * PatternBinding) list

    type private ParameterSlotContext =
        { Type: TypeRef
          ParameterId: ParameterId option }

    type private ExpressionOwnerContext =
        { ParametersById: Map<ParameterId, SemanticParameter>
          ParameterSlots: ParameterSlotContext list option
          RequiredEffects: EffectId list
          ResultType: TypeRef
          /// The owner's own identity; None only while a new function is preflighted.
          SelfFunctionId: FunctionId option
          /// The owner's parameters in declaration order, addressed by SelfCall slots.
          SelfParameters: ParameterSlotContext list
          Description: string }

    let private ownerForExistingFunction (target: SemanticFunction) =
        { ParametersById =
            target.Parameters
            |> List.map (fun parameter -> parameter.Id, parameter)
            |> Map.ofList
          ParameterSlots = None
          RequiredEffects = target.RequiredEffects
          ResultType = target.ResultType
          SelfFunctionId = Some target.Id
          SelfParameters =
            target.Parameters
            |> List.map (fun parameter -> { Type = parameter.Type; ParameterId = Some parameter.Id })
          Description = $"target function {StableId.describe target.DisplayName target.Id}" }

    /// An effect in a diagnostic: display name and ID when declared, the ID alone otherwise.
    let private effectLabel (snapshot: Snapshot) effectId =
        match snapshot.Effects |> List.tryFind (fun effect -> effect.Id = effectId) with
        | Some effect -> StableId.describe effect.DisplayName effectId
        | None -> $"'{StableId.value effectId}'"

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private listType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private optionType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
    let private resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])

    let private listItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) -> Some itemType
        | _ -> None

    let private recipeChildren recipe =
        match recipe with
        | ExpressionRecipe.Parameter _
        | ExpressionRecipe.ParameterSlot _
        | ExpressionRecipe.PatternReference _
        | ExpressionRecipe.StringLiteral _
        | ExpressionRecipe.BoolLiteral _
        | ExpressionRecipe.UnitLiteral
        | ExpressionRecipe.Int64Literal _
        | ExpressionRecipe.Float64Literal _
        | ExpressionRecipe.ListEmpty _
        | ExpressionRecipe.OptionNone _ -> Seq.empty
        | ExpressionRecipe.StringIsEmpty child
        | ExpressionRecipe.Int64ToString child
        | ExpressionRecipe.StringParseInt64 child
        | ExpressionRecipe.StringLength child
        | ExpressionRecipe.BoolNot child
        | ExpressionRecipe.Float64IsNaN child
        | ExpressionRecipe.Float64IsFinite child
        | ExpressionRecipe.Float64IsInfinite child
        | ExpressionRecipe.Int64ToFloat64 child
        | ExpressionRecipe.ListLength child
        | ExpressionRecipe.FieldAccess(child, _)
        | ExpressionRecipe.OptionSome child
        | ExpressionRecipe.ResultOk(_, child)
        | ExpressionRecipe.Abort child
        | ExpressionRecipe.ResultErr(_, child) -> Seq.singleton child
        | ExpressionRecipe.StringSlice(value, start, stop) -> seq { value; start; stop }
        | ExpressionRecipe.StringContains(left, right)
        | ExpressionRecipe.StringEqual(left, right)
        | ExpressionRecipe.ValueEqual(left, right)
        | ExpressionRecipe.StringLessThan(left, right)
        | ExpressionRecipe.StringConcat(left, right)
        | ExpressionRecipe.Bind(left, right)
        | ExpressionRecipe.BoolAnd(left, right)
        | ExpressionRecipe.BoolOr(left, right)
        | ExpressionRecipe.Int64WrappingAdd(left, right)
        | ExpressionRecipe.Int64WrappingSubtract(left, right)
        | ExpressionRecipe.Int64WrappingMultiply(left, right)
        | ExpressionRecipe.Int64FloorDivide(left, right)
        | ExpressionRecipe.Int64FloorModulo(left, right)
        | ExpressionRecipe.Int64Equal(left, right)
        | ExpressionRecipe.Int64LessThan(left, right)
        | ExpressionRecipe.Int64LessThanOrEqual(left, right)
        | ExpressionRecipe.Int64GreaterThan(left, right)
        | ExpressionRecipe.Int64GreaterThanOrEqual(left, right)
        | ExpressionRecipe.Float64Add(left, right)
        | ExpressionRecipe.Float64Subtract(left, right)
        | ExpressionRecipe.Float64Multiply(left, right)
        | ExpressionRecipe.Float64Divide(left, right)
        | ExpressionRecipe.Float64Equal(left, right)
        | ExpressionRecipe.Float64LessThan(left, right)
        | ExpressionRecipe.ListAppendItem(left, right)
        | ExpressionRecipe.ListItemAt(left, right)
        | ExpressionRecipe.ListConcat(left, right) -> seq { yield left; yield right }
        | ExpressionRecipe.ListFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.ListEffectFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.UseResource(acquire, releaseCapability, _, _, context) ->
            seq { yield acquire; yield releaseCapability; yield! context |> Seq.map snd }
        | ExpressionRecipe.ParallelAll(source, _, context) -> seq { yield source; yield! context |> Seq.map snd }
        | ExpressionRecipe.CancellationRequested -> Seq.empty
        | ExpressionRecipe.TaskScope(_, context) -> context |> Seq.map snd
        | ExpressionRecipe.TaskStart(scope, _, context, item) -> seq { yield scope; yield item; yield! context |> Seq.map snd }
        | ExpressionRecipe.TaskWait(tasks, _, limit) -> seq { yield tasks; yield! Option.toList limit }
        | ExpressionRecipe.TaskCancel task -> Seq.singleton task
        | ExpressionRecipe.ListMap(source, _, context)
        | ExpressionRecipe.ListFilter(source, _, context)
        | ExpressionRecipe.ListSortBy(source, _, context)
        | ExpressionRecipe.ListAny(source, _, context)
        | ExpressionRecipe.ListAll(source, _, context) -> seq { yield source; yield! context |> Seq.map snd }
        | ExpressionRecipe.ListConstruct(_, items) -> items |> Seq.ofList
        | ExpressionRecipe.ProductConstruct(_, fields)
        | ExpressionRecipe.UnionConstruct(_, fields) -> fields |> Seq.map snd
        | ExpressionRecipe.CapabilityConstruct _
        | ExpressionRecipe.FunctionReference _ -> Seq.empty
        | ExpressionRecipe.FunctionInvoke(value, arguments) -> seq { yield value; yield! arguments }
        | ExpressionRecipe.DirectCall(_, arguments) -> arguments |> Seq.map snd
        | ExpressionRecipe.SelfCall arguments -> arguments |> Seq.map snd
        | ExpressionRecipe.CapabilityInvoke(capabilityValue, _, arguments) ->
            seq {
                yield capabilityValue
                yield! arguments |> Seq.map snd
            }
        | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
            seq { yield condition; yield whenTrue; yield whenFalse }
        | ExpressionRecipe.MatchUnion(input, arms) ->
            seq {
                yield input
                for _, body in arms do
                    yield body
            }
        | ExpressionRecipe.MatchOption(input, someBody, noneBody)
        | ExpressionRecipe.MatchResult(input, someBody, noneBody) ->
            seq { yield input; yield someBody; yield noneBody }

    let private validateTypeRef (snapshot: Snapshot) typeRef =
        let typeIds = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id) |> Set.ofList
        let capabilityIds = snapshot.Capabilities |> List.map (fun capability -> capability.Id) |> Set.ofList
        let invalid message = diagnostic "PureExpressionInvalidTypeRef" message

        let rec validate typeRef =
            match typeRef with
            | TypeRef.Primitive _
            // Scope (a parameter type must declare it) is checked on the resulting snapshot.
            | TypeRef.TypeParameter _ -> Ok()
            | TypeRef.Named typeId when Set.contains typeId typeIds -> Ok()
            | TypeRef.Named typeId -> Error(invalid $"Type reference names unknown type '{StableId.value typeId}'.")
            | TypeRef.Capability capabilityId when Set.contains capabilityId capabilityIds -> Ok()
            | TypeRef.Capability capabilityId ->
                Error(invalid $"Type reference names unknown capability '{StableId.value capabilityId}'.")
            | TypeRef.Function(arguments, result) ->
                (arguments @ [ result ]) |> List.fold (fun state arg -> state |> Result.bind (fun () -> validate arg)) (Ok())
            | TypeRef.BuiltinGeneric(kind, arguments) ->
                let expectedArity =
                    match kind with
                    | BuiltinGenericType.TaskScope -> 0
                    | BuiltinGenericType.Option
                    | BuiltinGenericType.List
                    | BuiltinGenericType.Task -> 1
                    | BuiltinGenericType.Result -> 2

                if List.length arguments <> expectedArity then
                    Error(invalid $"Builtin generic type '{kind}' requires {expectedArity} type argument(s).")
                else
                    let rec validateArguments = function
                        | [] -> Ok()
                        | argument :: rest ->
                            match validate argument with
                            | Error error -> Error error
                            | Ok() -> validateArguments rest
                    validateArguments arguments

        validate typeRef

    let private resolvePatternType (frames: PatternTypeFrame list) (reference: RecipePatternReference) =
        if reference.ScopeDepth < 0 then
            Error(
                diagnostic
                    "PureExpressionPatternScopeDepthInvalid"
                    "Pattern-reference scope depth must be non-negative."
            )
        else
            match List.tryItem reference.ScopeDepth frames with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionPatternScopeNotFound"
                        $"Pattern-reference scope depth {reference.ScopeDepth} is not visible here."
                )
            | Some frame ->
                match frame |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
                | Some(_, typeRef) -> Ok typeRef
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionPatternSlotNotFound"
                            $"Pattern-reference slot {reference.Slot} does not exist in scope depth {reference.ScopeDepth}."
                    )

    let private inferTypeWithContext
        (snapshot: Snapshot)
        (owner: ExpressionOwnerContext)
        (initialFrames: PatternTypeFrame list)
        recipe
        =
        let functionsById = snapshot.Functions |> List.map (fun functionEntity -> functionEntity.Id, functionEntity) |> Map.ofList
        let typesById = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id, typeEntity) |> Map.ofList
        let capabilityOperationsById =
            snapshot.Capabilities
            |> List.collect (fun capability ->
                capability.Operations |> List.map (fun operation -> operation.Id, (capability.Id, operation)))
            |> Map.ofList
        let variantsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product _ -> []
                | SemanticTypeKind.Union variants -> variants |> List.map (fun variant -> variant.Id, (typeEntity.Id, variant)))
            |> Map.ofList
        let productFieldsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.Id, (typeEntity.Id, field))
                | SemanticTypeKind.Union _ -> [])
            |> Map.ofList

        let typeMismatch role side expected actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires {expected}, but the recipe produces {actual}."

        let listMismatch role side actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires List<T>, but the recipe produces {actual}."

        /// Types context bindings with `inferValue` and returns the callback's unbound parameters with
        /// the type arguments that the context values determine for a generic callback.
        let validateCallbackContext role (callback: SemanticFunction) (context: (ParameterId * ExpressionRecipe) list) inferValue =
            let parametersById = callback.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList
            match context |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
            | Some(parameterId, _) ->
                Error(diagnostic "PureExpressionDuplicateCallArgument" $"{role} binds callback parameter '{StableId.value parameterId}' more than once.")
            | None ->
                context
                |> List.fold (fun state (parameterId, value) ->
                    state
                    |> Result.bind (fun substitution ->
                        match Map.tryFind parameterId parametersById with
                        | None ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentTargetNotFound"
                                    $"{role} context binds '{StableId.value parameterId}', which is not a parameter of callback '{StableId.value callback.Id}'."
                            )
                        | Some parameter ->
                            match inferValue value with
                            | Error error -> Error error
                            | Ok actual ->
                                match TypeParameters.matchType substitution parameter.Type actual with
                                | Some extended -> Ok extended
                                | None ->
                                    Error(
                                        diagnostic
                                            "PureExpressionListCallbackContextTypeMismatch"
                                            $"{role} context value for '{StableId.value parameterId}' has type {actual}, but the parameter expects {parameter.Type}."
                                    ))) (Ok Map.empty)
                |> Result.map (fun substitution ->
                    callback.Parameters |> List.filter (fun parameter -> not (context |> List.exists (fun (bound, _) -> bound = parameter.Id))),
                    substitution)

        /// Returns the callback and the type arguments that its context and item types determine.
        let validateListCallback role callbackFunctionId context sourceItemType predicateResultRequired inferValue =
            match Map.tryFind callbackFunctionId functionsById with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionListCallbackNotFound"
                        $"{role} callback function '{StableId.value callbackFunctionId}' does not exist."
                )
            | Some callback ->
                match validateCallbackContext role callback context inferValue with
                | Error error -> Error error
                | Ok(unbound, _) when List.length unbound <> 1 ->
                    Error(
                        diagnostic
                            "PureExpressionListCallbackArityMismatch"
                            $"{role} callback function '{StableId.value callback.Id}' must have exactly one parameter not bound by context."
                    )
                | Ok(unbound, substitution) ->
                    match TypeParameters.matchType substitution unbound.Head.Type sourceItemType with
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionListCallbackInputTypeMismatch"
                                $"{role} callback function '{StableId.value callback.Id}' input type does not match the source list item type."
                        )
                    | Some substitution when predicateResultRequired && TypeParameters.substitute substitution callback.ResultType <> boolType ->
                        Error(
                            diagnostic
                                "PureExpressionListCallbackResultTypeMismatch"
                                $"{role} predicate '{StableId.value callback.Id}' must return Bool."
                        )
                    | Some _ when not (List.isEmpty callback.RequiredEffects) ->
                        Error(
                            diagnostic
                                "PureExpressionListCallbackEffectsNotAllowed"
                                $"{role} callback function '{StableId.value callback.Id}' must be pure."
                        )
                    | Some substitution -> Ok(callback, substitution)

        /// ListSortBy's callback: exactly two parameters not bound by context, both of the item type
        /// (left, right), returning Bool, with no required effects.
        let validateLessThanCallback role callbackFunctionId context sourceItemType inferValue =
            match Map.tryFind callbackFunctionId functionsById with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionListCallbackNotFound"
                        $"{role} callback function '{StableId.value callbackFunctionId}' does not exist."
                )
            | Some callback ->
                match validateCallbackContext role callback context inferValue with
                | Error error -> Error error
                | Ok(unbound, _) when List.length unbound <> 2 ->
                    Error(
                        diagnostic
                            "PureExpressionListCallbackArityMismatch"
                            $"{role} callback function '{StableId.value callback.Id}' must have exactly two parameters not bound by context (left, right)."
                    )
                | Ok(unbound, substitution) ->
                    match
                        TypeParameters.matchType substitution unbound.Head.Type sourceItemType
                        |> Option.bind (fun current -> TypeParameters.matchType current unbound.Tail.Head.Type sourceItemType)
                    with
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionListCallbackInputTypeMismatch"
                                $"{role} callback function '{StableId.value callback.Id}' input types must both match the source list item type."
                        )
                    | Some substitution when TypeParameters.substitute substitution callback.ResultType <> boolType ->
                        Error(
                            diagnostic
                                "PureExpressionListCallbackResultTypeMismatch"
                                $"{role} less-than callback '{StableId.value callback.Id}' must return Bool."
                        )
                    | Some _ when not (List.isEmpty callback.RequiredEffects) ->
                        Error(
                            diagnostic
                                "PureExpressionListCallbackEffectsNotAllowed"
                                $"{role} callback function '{StableId.value callback.Id}' must be pure."
                        )
                    | Some substitution -> Ok(callback, substitution)

        let rec infer frames recipe =
            let unary role expected result child =
                match infer frames child with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "value" expected actual)
                | Ok _ -> Ok result

            let binary role expected result left right =
                match infer frames left with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "left" expected actual)
                | Ok _ ->
                    match infer frames right with
                    | Error error -> Error error
                    | Ok actual when actual <> expected -> Error(typeMismatch role "right" expected actual)
                    | Ok _ -> Ok result

            let validateFieldBindings role (expectedFields: SemanticField list) (bindings: (FieldId * ExpressionRecipe) list) =
                match bindings |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(fieldId, _) ->
                    Error(
                        diagnostic
                            "PureExpressionDuplicateFieldBinding"
                            $"{role} binds field '{StableId.value fieldId}' more than once."
                    )
                | None ->
                    let expectedIds = expectedFields |> List.map (fun field -> field.Id) |> Set.ofList
                    let actualIds = bindings |> List.map fst |> Set.ofList
                    if expectedIds <> actualIds then
                        Error(diagnostic "PureExpressionFieldBindingMismatch" $"{role} must bind exactly the fields declared by its target.")
                    else
                        let rec validateFields = function
                            | [] -> Ok()
                            | (field: SemanticField) :: rest ->
                                let _, value = bindings |> List.find (fun (fieldId, _) -> fieldId = field.Id)
                                match infer frames value with
                                | Error error -> Error error
                                | Ok actual when actual <> field.Type ->
                                    Error(
                                        diagnostic
                                            "PureExpressionFieldBindingTypeMismatch"
                                            $"{role} field {StableId.describe field.DisplayName field.Id} requires {field.Type}, but the recipe produces {actual}."
                                    )
                                | Ok _ -> validateFields rest
                        validateFields expectedFields

            let validateCallArguments (callee: SemanticFunction) (bindings: (ParameterId * ExpressionRecipe) list) =
                match bindings |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(parameterId, _) ->
                    Error(
                        diagnostic
                            "PureExpressionDuplicateCallArgument"
                            $"DirectCall binds parameter '{StableId.value parameterId}' more than once."
                    )
                | None ->
                    let calleeParametersById = callee.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList
                    match bindings |> List.tryFind (fun (parameterId, _) -> not (Map.containsKey parameterId calleeParametersById)) with
                    | Some(parameterId, _) ->
                        Error(
                            diagnostic
                                "PureExpressionCallArgumentTargetNotFound"
                                $"DirectCall targets parameter '{StableId.value parameterId}' not owned by callee '{StableId.value callee.Id}'."
                        )
                    | None ->
                        let actualIds = bindings |> List.map fst |> Set.ofList
                        match callee.Parameters |> List.tryFind (fun parameter -> not (Set.contains parameter.Id actualIds)) with
                        | Some parameter ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentMissing"
                                    $"DirectCall is missing required parameter '{StableId.value parameter.Id}' for callee '{StableId.value callee.Id}'."
                            )
                        | None ->
                            // A generic callee's type arguments follow from matching each parameter type.
                            let rec validateParameters substitution = function
                                | [] -> Ok substitution
                                | (parameter: SemanticParameter) :: rest ->
                                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                                    match infer frames value with
                                    | Error error -> Error error
                                    | Ok actual ->
                                        match TypeParameters.matchType substitution parameter.Type actual with
                                        | Some extended -> validateParameters extended rest
                                        | None ->
                                            Error(
                                                diagnostic
                                                    "PureExpressionCallArgumentTypeMismatch"
                                                    $"DirectCall parameter '{StableId.value parameter.Id}' requires {TypeParameters.substitute substitution parameter.Type}, but the recipe produces {actual}."
                                            )
                            validateParameters Map.empty callee.Parameters

            let validateCapabilityArguments
                (operation: SemanticCapabilityOperation)
                (bindings: (CapabilityParameterId * ExpressionRecipe) list)
                =
                match bindings |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(parameterId, _) ->
                    Error(
                        diagnostic
                            "ExpressionDuplicateCapabilityArgument"
                            $"CapabilityInvoke binds parameter '{StableId.value parameterId}' more than once."
                    )
                | None ->
                    let expectedIds = operation.Parameters |> List.map (fun parameter -> parameter.Id) |> Set.ofList
                    let actualIds = bindings |> List.map fst |> Set.ofList
                    if expectedIds <> actualIds then
                        Error(
                            diagnostic
                                "ExpressionCapabilityArgumentSetMismatch"
                                $"CapabilityInvoke must bind exactly the parameters declared by operation {StableId.describe operation.DisplayName operation.Id}."
                        )
                    else
                        let rec validateParameters = function
                            | [] -> Ok()
                            | (parameter: SemanticCapabilityParameter) :: rest ->
                                let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                                match infer frames value with
                                | Error error -> Error error
                                | Ok actual when actual <> parameter.Type ->
                                    Error(
                                        diagnostic
                                            "ExpressionCapabilityArgumentTypeMismatch"
                                            $"CapabilityInvoke parameter '{StableId.value parameter.Id}' requires {parameter.Type}, but the recipe produces {actual}."
                                    )
                                | Ok _ -> validateParameters rest
                        validateParameters operation.Parameters

            match recipe with
            | ExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId owner.ParametersById with
                | Some parameter -> Ok parameter.Type
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionParameterOutOfScope"
                            $"Parameter '{StableId.value parameterId}' is not owned by {owner.Description}."
                    )
            | ExpressionRecipe.ParameterSlot index ->
                match owner.ParameterSlots with
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionParameterSlotUnavailable"
                            "ParameterSlot is available only while creating a new function."
                    )
                | Some _ when index < 0 ->
                    Error(
                        diagnostic
                            "PureExpressionParameterSlotOutOfRange"
                            $"Parameter slot {index} is outside the requested parameter list."
                    )
                | Some slots ->
                    match List.tryItem index slots with
                    | Some slot -> Ok slot.Type
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionParameterSlotOutOfRange"
                                $"Parameter slot {index} is outside the requested parameter list."
                        )
            | ExpressionRecipe.PatternReference reference -> resolvePatternType frames reference
            | ExpressionRecipe.StringLiteral value when isNull value ->
                Error(diagnostic "PureExpressionNullStringLiteral" "StringLiteral requires a non-null value.")
            | ExpressionRecipe.StringLiteral _ -> Ok stringType
            | ExpressionRecipe.BoolLiteral _ -> Ok boolType
            | ExpressionRecipe.UnitLiteral -> Ok unitType
            | ExpressionRecipe.Int64Literal _ -> Ok int64Type
            | ExpressionRecipe.Float64Literal _ -> Ok float64Type
            | ExpressionRecipe.StringIsEmpty value -> unary "StringIsEmpty" stringType boolType value
            | ExpressionRecipe.Int64ToString value -> unary "Int64ToString" int64Type stringType value
            | ExpressionRecipe.StringParseInt64 value ->
                unary "StringParseInt64" stringType (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ int64Type ])) value
            | ExpressionRecipe.StringLength value -> unary "StringLength" stringType int64Type value
            | ExpressionRecipe.BoolNot operand -> unary "BoolNot" boolType boolType operand
            | ExpressionRecipe.BoolAnd(left, right)
            | ExpressionRecipe.BoolOr(left, right) ->
                let role =
                    match recipe with
                    | ExpressionRecipe.BoolAnd _ -> "BoolAnd"
                    | _ -> "BoolOr"
                match infer frames left with
                | Error error -> Error error
                | Ok actual when actual <> boolType -> Error(typeMismatch role "left" boolType actual)
                | Ok _ ->
                    // The right operand is a nested branch: it may abort, otherwise it must be Bool.
                    match inferCompletion frames right with
                    | Error error -> Error error
                    | Ok(GraphCompletion.Normal actual) when actual <> boolType -> Error(typeMismatch role "right" boolType actual)
                    | Ok _ -> Ok boolType
            | ExpressionRecipe.StringSlice(value, start, stop) ->
                match unary "StringSlice" stringType stringType value with
                | Error error -> Error error
                | Ok _ ->
                    match infer frames start with
                    | Error error -> Error error
                    | Ok actual when actual <> int64Type -> Error(typeMismatch "StringSlice" "start" int64Type actual)
                    | Ok _ ->
                        match infer frames stop with
                        | Error error -> Error error
                        | Ok actual when actual <> int64Type -> Error(typeMismatch "StringSlice" "end" int64Type actual)
                        | Ok _ -> Ok stringType
            | ExpressionRecipe.StringContains(value, substring) -> binary "StringContains" stringType boolType value substring
            | ExpressionRecipe.StringEqual(left, right) -> binary "StringEqual" stringType boolType left right
            | ExpressionRecipe.StringLessThan(left, right) -> binary "StringLessThan" stringType boolType left right
            | ExpressionRecipe.ValueEqual(left, right) ->
                match infer frames left with
                | Error error -> Error error
                | Ok leftType ->
                    match infer frames right with
                    | Error error -> Error error
                    | Ok rightType when rightType <> leftType -> Error(typeMismatch "ValueEqual" "right" leftType rightType)
                    | Ok _ when not (EquatableTypes.isEquatable snapshot.Types leftType) ->
                        Error(
                            diagnostic
                                "ValueEqualTypeNotEquatable"
                                $"ValueEqual compares values of a type that is not equatable: {EquatableTypes.explain snapshot.Types leftType}"
                        )
                    | Ok _ -> Ok boolType
            | ExpressionRecipe.StringConcat(left, right) -> binary "StringConcat" stringType stringType left right
            | ExpressionRecipe.Int64WrappingAdd(left, right) -> binary "Int64WrappingAdd" int64Type int64Type left right
            | ExpressionRecipe.Int64WrappingSubtract(left, right) -> binary "Int64WrappingSubtract" int64Type int64Type left right
            | ExpressionRecipe.Int64WrappingMultiply(left, right) -> binary "Int64WrappingMultiply" int64Type int64Type left right
            | ExpressionRecipe.Int64FloorDivide(left, right) -> binary "Int64FloorDivide" int64Type int64Type left right
            | ExpressionRecipe.Int64FloorModulo(left, right) -> binary "Int64FloorModulo" int64Type int64Type left right
            | ExpressionRecipe.Int64Equal(left, right) -> binary "Int64Equal" int64Type boolType left right
            | ExpressionRecipe.Int64LessThan(left, right) -> binary "Int64LessThan" int64Type boolType left right
            | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> binary "Int64LessThanOrEqual" int64Type boolType left right
            | ExpressionRecipe.Int64GreaterThan(left, right) -> binary "Int64GreaterThan" int64Type boolType left right
            | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> binary "Int64GreaterThanOrEqual" int64Type boolType left right
            | ExpressionRecipe.Float64Add(left, right) -> binary "Float64Add" float64Type float64Type left right
            | ExpressionRecipe.Float64Subtract(left, right) -> binary "Float64Subtract" float64Type float64Type left right
            | ExpressionRecipe.Float64Multiply(left, right) -> binary "Float64Multiply" float64Type float64Type left right
            | ExpressionRecipe.Float64Divide(left, right) -> binary "Float64Divide" float64Type float64Type left right
            | ExpressionRecipe.Float64Equal(left, right) -> binary "Float64Equal" float64Type boolType left right
            | ExpressionRecipe.Float64LessThan(left, right) -> binary "Float64LessThan" float64Type boolType left right
            | ExpressionRecipe.Float64IsNaN value -> unary "Float64IsNaN" float64Type boolType value
            | ExpressionRecipe.Float64IsFinite value -> unary "Float64IsFinite" float64Type boolType value
            | ExpressionRecipe.Float64IsInfinite value -> unary "Float64IsInfinite" float64Type boolType value
            | ExpressionRecipe.Int64ToFloat64 value -> unary "Int64ToFloat64" int64Type float64Type value
            | ExpressionRecipe.ListEmpty itemType ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() -> Ok(listType itemType)
            | ExpressionRecipe.ListConstruct(itemType, items) ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() ->
                    let rec validateItems = function
                        | [] -> Ok(listType itemType)
                        | item :: rest ->
                            match infer frames item with
                            | Error error -> Error error
                            | Ok actual when actual <> itemType -> Error(typeMismatch "ListConstruct" "item" itemType actual)
                            | Ok _ -> validateItems rest
                    validateItems items
            | ExpressionRecipe.ListLength list ->
                match infer frames list with
                | Error error -> Error error
                | Ok actual ->
                    match listItemType actual with
                    | Some _ -> Ok int64Type
                    | None -> Error(listMismatch "ListLength" "value" actual)
            | ExpressionRecipe.ListItemAt(list, index) ->
                match infer frames list with
                | Error error -> Error error
                | Ok listResultType ->
                    match listItemType listResultType with
                    | None -> Error(listMismatch "ListItemAt" "list" listResultType)
                    | Some itemType ->
                        match infer frames index with
                        | Error error -> Error error
                        | Ok actual when actual <> int64Type -> Error(typeMismatch "ListItemAt" "index" int64Type actual)
                        | Ok _ -> Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]))
            | ExpressionRecipe.ListAppendItem(list, item) ->
                match infer frames list with
                | Error error -> Error error
                | Ok listResultType ->
                    match listItemType listResultType with
                    | None -> Error(listMismatch "ListAppendItem" "list" listResultType)
                    | Some itemType ->
                        match infer frames item with
                        | Error error -> Error error
                        | Ok actual when actual <> itemType -> Error(typeMismatch "ListAppendItem" "item" itemType actual)
                        | Ok _ -> Ok listResultType
            | ExpressionRecipe.ListConcat(left, right) ->
                match infer frames left with
                | Error error -> Error error
                | Ok leftType ->
                    match listItemType leftType with
                    | None -> Error(listMismatch "ListConcat" "left" leftType)
                    | Some _ ->
                        match infer frames right with
                        | Error error -> Error error
                        | Ok rightType when rightType <> leftType -> Error(typeMismatch "ListConcat" "right" leftType rightType)
                        | Ok _ -> Ok leftType
            | ExpressionRecipe.ListFold(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFold" "source" sourceType)
                    | Some itemType ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionListCallbackNotFound" $"ListFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "ListFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionListCallbackArityMismatch" "ListFold folder must have exactly two parameters not bound by context (accumulator, item).")
                                | Ok(unbound, substitution) ->
                                    let matched =
                                        TypeParameters.matchType substitution unbound.Head.Type seedType
                                        |> Option.bind (fun current -> TypeParameters.matchType current unbound.Tail.Head.Type itemType)
                                    match matched with
                                    | Some current when TypeParameters.substitute current folder.ResultType = seedType ->
                                        if List.isEmpty folder.RequiredEffects then Ok seedType
                                        else Error(diagnostic "PureExpressionListCallbackEffectsNotAllowed" "ListFold folder must be pure.")
                                    | _ -> Error(diagnostic "PureExpressionListCallbackInputTypeMismatch" "ListFold folder must have type (accumulator, item) -> accumulator.")
            | ExpressionRecipe.ListEffectFold(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListEffectFold" "source" sourceType)
                    | Some itemType ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionListCallbackNotFound" $"ListEffectFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "ListEffectFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionListCallbackArityMismatch" "ListEffectFold folder must have exactly two parameters not bound by context (accumulator, item).")
                                | Ok(unbound, substitution) ->
                                  match
                                      TypeParameters.matchType substitution unbound.Head.Type seedType
                                      |> Option.bind (fun current -> TypeParameters.matchType current unbound.Tail.Head.Type itemType)
                                  with
                                  | None -> Error(diagnostic "PureExpressionListCallbackInputTypeMismatch" "ListEffectFold folder must take (accumulator, item).")
                                  | Some current ->
                                    match
                                        folder.RequiredEffects
                                        |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects))
                                    with
                                    | Some effectId ->
                                        Error(
                                            diagnostic
                                                "ExpressionCallMissingEffectRequirement"
                                                $"ListEffectFold folder {StableId.describe folder.DisplayName folder.Id} requires effect {effectLabel snapshot effectId} not declared by {owner.Description}."
                                        )
                                    | None ->
                                        match TypeParameters.substitute current folder.ResultType with
                                        | resultType when resultType = seedType -> Ok seedType
                                        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ]) as resultType when okType = seedType -> Ok resultType
                                        | _ ->
                                            Error(diagnostic "PureExpressionListCallbackInputTypeMismatch" "ListEffectFold folder must return the accumulator type S or Result<S, E>.")
            | ExpressionRecipe.UseResource(acquire, releaseCapability, releaseOperationId, bodyFunctionId, context) ->
                let fail code message = Error(diagnostic code message)
                match infer frames acquire with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ resourceType; errorType ])) ->
                    match Map.tryFind bodyFunctionId functionsById, Map.tryFind releaseOperationId capabilityOperationsById with
                    | None, _ -> fail "PureExpressionListCallbackNotFound" $"UseResource body '{StableId.value bodyFunctionId}' does not exist."
                    | _, None -> fail "ExpressionCapabilityOperationNotFound" $"UseResource references unknown release operation '{StableId.value releaseOperationId}'."
                    | Some body, Some(ownerCapabilityId, operation) ->
                        match validateCallbackContext "UseResource" body context (infer frames), infer frames releaseCapability with
                        | Error error, _
                        | _, Error error -> Error error
                        | Ok(unbound, substitution), Ok receiverType ->
                          match (match unbound with [ resource ] -> TypeParameters.matchType substitution resource.Type resourceType | _ -> None) with
                          | None ->
                            fail "UseResourceBodyMismatch" "UseResource body must have exactly one parameter not bound by context, of the acquired resource type."
                          | Some _ when receiverType <> TypeRef.Capability ownerCapabilityId ->
                            fail "UseResourceReleaseReceiverMismatch" "UseResource release capability value does not own the release operation."
                          | Some bodySubstitution ->
                            let bodyResult = TypeParameters.substitute bodySubstitution body.ResultType
                            let releaseReturnsResult = operation.ResultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ unitType; errorType ])
                            let missing =
                                body.RequiredEffects @ [ operation.EffectId ]
                                |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects))
                            match bodyResult, missing with
                            | _, Some effectId ->
                                fail "ExpressionCallMissingEffectRequirement" $"UseResource requires effect {effectLabel snapshot effectId} not declared by {owner.Description}."
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; bodyError ]), None when bodyError <> errorType ->
                                fail "UseResourceResultMismatch" "UseResource body must return Result<T, E> with the acquisition error type E."
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]), None ->
                                match operation.Parameters with
                                | [ parameter ] when parameter.Type = resourceType && (operation.ResultType = unitType || releaseReturnsResult) ->
                                    Ok bodyResult
                                | _ ->
                                    fail "UseResourceReleaseSignatureMismatch" "The release operation must take exactly the resource and return Unit or Result<Unit, E>."
                            | _ -> fail "UseResourceResultMismatch" "UseResource body must return Result<T, E> with the acquisition error type E."
                | Ok actual -> fail "UseResourceAcquireTypeMismatch" $"UseResource acquisition must produce Result<R, E>, not {actual}."
            | ExpressionRecipe.CancellationRequested -> Ok boolType
            | ExpressionRecipe.TaskScope(bodyFunctionId, context) ->
                match Map.tryFind bodyFunctionId functionsById with
                | None -> Error(diagnostic "PureExpressionListCallbackNotFound" $"TaskScope body '{StableId.value bodyFunctionId}' does not exist.")
                | Some body ->
                    match validateCallbackContext "TaskScope" body context (infer frames) with
                    | Error error -> Error error
                    | Ok(unbound, substitution) ->
                        let scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
                        match (match unbound with [ scope ] -> TypeParameters.matchType substitution scope.Type scopeType | _ -> None),
                              body.RequiredEffects |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects)) with
                        | None, _ -> Error(diagnostic "TaskScopeBodyMismatch" "TaskScope body must have exactly one parameter not bound by context, of type TaskScope.")
                        | _, Some effectId ->
                            Error(diagnostic "ExpressionCallMissingEffectRequirement" $"TaskScope body {StableId.describe body.DisplayName body.Id} requires effect {effectLabel snapshot effectId} not declared by {owner.Description}.")
                        | Some current, None ->
                            match TypeParameters.substitute current body.ResultType with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result -> Ok result
                            | _ -> Error(diagnostic "TaskScopeResultMismatch" "TaskScope body must return Result<T, E>.")
            | ExpressionRecipe.TaskStart(scope, workerFunctionId, context, item) ->
                match infer frames scope, infer frames item with
                | Error error, _
                | _, Error error -> Error error
                | Ok scopeType, _ when scopeType <> TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) ->
                    Error(diagnostic "TaskStartScopeTypeMismatch" $"TaskStart scope must be a TaskScope, not {scopeType}.")
                | Ok _, Ok itemType ->
                    match Map.tryFind workerFunctionId functionsById with
                    | None -> Error(diagnostic "PureExpressionListCallbackNotFound" $"TaskStart worker '{StableId.value workerFunctionId}' does not exist.")
                    | Some worker ->
                        match validateCallbackContext "TaskStart" worker context (infer frames) with
                        | Error error -> Error error
                        | Ok(unbound, substitution) ->
                            let matched = match unbound with [ parameter ] -> TypeParameters.matchType substitution parameter.Type itemType | _ -> None
                            match matched, worker.RequiredEffects |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects)) with
                            | None, _ ->
                                Error(diagnostic "PureExpressionListCallbackInputTypeMismatch" "TaskStart worker must have exactly one parameter not bound by context, of the item type.")
                            | _, Some effectId ->
                                Error(diagnostic "ExpressionCallMissingEffectRequirement" $"TaskStart worker {StableId.describe worker.DisplayName worker.Id} requires effect {effectLabel snapshot effectId} not declared by {owner.Description}.")
                            | Some current, None ->
                                match TypeParameters.substitute current worker.ResultType with
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result ->
                                    Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ result ]))
                                | _ -> Error(diagnostic "PureExpressionListCallbackInputTypeMismatch" "TaskStart worker must return Result<U, E>.")
            | ExpressionRecipe.TaskWait(tasks, _, limit) ->
                let limitType =
                    match limit |> Option.map (infer frames) with
                    | None -> Ok()
                    | Some(Error error) -> Error error
                    | Some(Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))) -> Ok()
                    | Some(Ok actual) -> Error(diagnostic "TaskWaitLimitTypeMismatch" $"TaskWait limit requires Option<Int64>, not {actual}.")
                match infer frames tasks, limitType with
                | Error error, _
                | _, Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ result ]) ])), Ok() ->
                    Ok(listType (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ result ])))
                | Ok actual, Ok() -> Error(diagnostic "TaskWaitInputTypeMismatch" $"TaskWait requires List<Task<T>>, not {actual}.")
            | ExpressionRecipe.TaskCancel task ->
                match infer frames task with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ _ ])) -> Ok unitType
                | Ok actual -> Error(diagnostic "TaskCancelInputTypeMismatch" $"TaskCancel requires Task<T>, not {actual}.")
            | ExpressionRecipe.ParallelAll(source, workerFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType, Map.tryFind workerFunctionId functionsById with
                    | None, _ -> Error(listMismatch "ParallelAll" "source" sourceType)
                    | _, None -> Error(diagnostic "PureExpressionListCallbackNotFound" $"ParallelAll worker '{StableId.value workerFunctionId}' does not exist.")
                    | Some itemType, Some worker ->
                        match validateCallbackContext "ParallelAll" worker context (infer frames) with
                        | Error error -> Error error
                        | Ok(unbound, substitution) ->
                            let matched = match unbound with [ item ] -> TypeParameters.matchType substitution item.Type itemType | _ -> None
                            match matched, worker.RequiredEffects |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects)) with
                            | None, _ ->
                                Error(diagnostic "PureExpressionListCallbackInputTypeMismatch" "ParallelAll worker must have exactly one parameter not bound by context, of the item type.")
                            | _, Some effectId ->
                                Error(diagnostic "ExpressionCallMissingEffectRequirement" $"ParallelAll worker {StableId.describe worker.DisplayName worker.Id} requires effect {effectLabel snapshot effectId} not declared by {owner.Description}.")
                            | Some current, None ->
                                match TypeParameters.substitute current worker.ResultType with
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                    Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ listType okType; errorType ]))
                                | _ -> Error(diagnostic "PureExpressionListCallbackInputTypeMismatch" "ParallelAll worker must return Result<U, E>.")
            | ExpressionRecipe.ListMap(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListMap" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListMap" mapperFunctionId context sourceItemType false (infer frames) with
                        | Error error -> Error error
                        | Ok(mapper, substitution) -> Ok(listType (TypeParameters.substitute substitution mapper.ResultType))
            | ExpressionRecipe.ListFilter(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFilter" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListFilter" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok sourceType
            | ExpressionRecipe.ListSortBy(source, lessThanFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListSortBy" "source" sourceType)
                    | Some sourceItemType ->
                        match validateLessThanCallback "ListSortBy" lessThanFunctionId context sourceItemType (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok sourceType
            | ExpressionRecipe.ListAny(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAny" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAny" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | ExpressionRecipe.ListAll(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAll" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAll" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | ExpressionRecipe.ProductConstruct(typeId, fields) ->
                match Map.tryFind typeId typesById with
                | None ->
                    Error(diagnostic "PureExpressionProductTypeNotFound" $"Product construction references unknown type '{StableId.value typeId}'.")
                | Some { Kind = SemanticTypeKind.Product expectedFields } ->
                    validateFieldBindings "ProductConstruct" expectedFields fields |> Result.map (fun () -> TypeRef.Named typeId)
                | Some _ ->
                    Error(diagnostic "PureExpressionProductTargetMismatch" $"Product construction target '{StableId.value typeId}' is not a product type.")
            | ExpressionRecipe.FieldAccess(source, fieldId) ->
                match Map.tryFind fieldId productFieldsById with
                | None ->
                    Error(diagnostic "PureExpressionProductFieldNotFound" $"Field access references field '{StableId.value fieldId}' that is not owned by a product type.")
                | Some(ownerTypeId, field) ->
                    match infer frames source with
                    | Error error -> Error error
                    | Ok(TypeRef.Named sourceTypeId) when sourceTypeId = ownerTypeId -> Ok field.Type
                    | Ok actual ->
                        Error(diagnostic "PureExpressionFieldAccessTypeMismatch" $"Field access requires {TypeRef.Named ownerTypeId}, but the source recipe produces {actual}.")
            | ExpressionRecipe.UnionConstruct(variantId, fields) ->
                match Map.tryFind variantId variantsById with
                | None ->
                    Error(diagnostic "PureExpressionUnionVariantNotFound" $"Union construction references unknown variant '{StableId.value variantId}'.")
                | Some(ownerTypeId, variant) ->
                    validateFieldBindings "UnionConstruct" variant.Fields fields |> Result.map (fun () -> TypeRef.Named ownerTypeId)
            | ExpressionRecipe.OptionNone itemType ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() -> Ok(optionType itemType)
            | ExpressionRecipe.OptionSome value -> infer frames value |> Result.map optionType
            | ExpressionRecipe.ResultOk(errorType, value) ->
                match validateTypeRef snapshot errorType with
                | Error error -> Error error
                | Ok() -> infer frames value |> Result.map (fun okType -> resultType okType errorType)
            | ExpressionRecipe.ResultErr(okType, error) ->
                match validateTypeRef snapshot okType with
                | Error error -> Error error
                | Ok() -> infer frames error |> Result.map (resultType okType)
            | ExpressionRecipe.CapabilityConstruct(capabilityId, implementations) ->
                match CapabilityImplementations.validate snapshot capabilityId implementations with
                | (code, message) :: _ -> Error(diagnostic code message)
                | [] -> Ok(TypeRef.Capability capabilityId)
            | ExpressionRecipe.FunctionReference target ->
                match Map.tryFind target functionsById with
                | None -> Error(diagnostic "UnknownFunctionReference" "The referenced function does not exist.")
                | Some fn when not (List.isEmpty (TypeParameters.ofFunction fn)) -> Error(diagnostic "GenericFunctionReferenceUnsupported" "Use a monomorphic wrapper for a generic function reference.")
                | Some _ when not (PureFunctions.isPure snapshot target) -> Error(diagnostic "ImpureFunctionReference" "Function references require transitive purity and no ambient cancellation reads.")
                | Some fn -> Ok(TypeRef.Function(List.map (fun (p: SemanticParameter) -> p.Type) fn.Parameters, fn.ResultType))
            | ExpressionRecipe.FunctionInvoke(value, arguments) ->
                match infer frames value with
                | Error error -> Error error
                | Ok(TypeRef.Function(parameters, result)) when parameters.Length = arguments.Length ->
                    List.zip parameters arguments
                    |> List.fold (fun state (expected, argument) ->
                        state |> Result.bind (fun () ->
                            infer frames argument |> Result.bind (fun actual ->
                                if actual = expected then Ok()
                                else Error(diagnostic "FunctionInvokeArgumentTypeMismatch" "Function invocation argument has the wrong type.")))) (Ok())
                    |> Result.map (fun () -> result)
                | Ok(TypeRef.Function _) -> Error(diagnostic "FunctionInvokeArityMismatch" "Function invocation arguments must match its positional parameter count.")
                | Ok _ -> Error(diagnostic "FunctionInvokeTargetTypeMismatch" "Function invocation requires a function value.")
            | ExpressionRecipe.DirectCall(targetFunctionId, arguments) ->
                match Map.tryFind targetFunctionId functionsById with
                | None ->
                    Error(diagnostic "PureExpressionCallTargetNotFound" $"DirectCall references unknown function '{StableId.value targetFunctionId}'.")
                | Some callee ->
                    match
                        callee.RequiredEffects
                        |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects))
                    with
                    | Some effectId ->
                        Error(
                            diagnostic
                                "ExpressionCallMissingEffectRequirement"
                                $"DirectCall callee '{StableId.value callee.Id}' requires effect '{StableId.value effectId}' not declared by {owner.Description}."
                        )
                    | None ->
                        validateCallArguments callee arguments |> Result.map (fun substitution -> TypeParameters.substitute substitution callee.ResultType)
            | ExpressionRecipe.SelfCall arguments ->
                // The owner's RequiredEffects trivially cover its own, so only the arguments are checked.
                let slotCount = List.length owner.SelfParameters
                match arguments |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(slot, _) ->
                    Error(diagnostic "PureExpressionDuplicateCallArgument" $"SelfCall binds parameter slot {slot} more than once.")
                | None ->
                    match arguments |> List.tryFind (fun (slot, _) -> slot < 0 || slot >= slotCount) with
                    | Some(slot, _) ->
                        Error(
                            diagnostic
                                "PureExpressionCallArgumentTargetNotFound"
                                $"SelfCall targets parameter slot {slot}, but {owner.Description} has {slotCount} parameter(s)."
                        )
                    | None ->
                        let bound = arguments |> List.map fst |> Set.ofList
                        match List.init slotCount id |> List.tryFind (fun slot -> not (Set.contains slot bound)) with
                        | Some slot ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentMissing"
                                    $"SelfCall is missing required parameter slot {slot} of {owner.Description}."
                            )
                        | None ->
                            let rec validateSlots = function
                                | [] -> Ok owner.ResultType
                                | (slot, (parameter: ParameterSlotContext)) :: rest ->
                                    let _, value = arguments |> List.find (fun (candidate, _) -> candidate = slot)
                                    match infer frames value with
                                    | Error error -> Error error
                                    | Ok actual when actual <> parameter.Type ->
                                        Error(
                                            diagnostic
                                                "PureExpressionCallArgumentTypeMismatch"
                                                $"SelfCall parameter slot {slot} requires {parameter.Type}, but the recipe produces {actual}."
                                        )
                                    | Ok _ -> validateSlots rest
                            validateSlots (List.indexed owner.SelfParameters)
            | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
                match Map.tryFind operationId capabilityOperationsById with
                | None ->
                    Error(
                        diagnostic
                            "ExpressionCapabilityOperationNotFound"
                            $"CapabilityInvoke references unknown operation '{StableId.value operationId}'."
                    )
                | Some(ownerCapabilityId, operation) ->
                    match infer frames capabilityValue with
                    | Error error -> Error error
                    | Ok actual when actual <> TypeRef.Capability ownerCapabilityId ->
                        Error(
                            diagnostic
                                "ExpressionCapabilityValueTypeMismatch"
                                $"CapabilityInvoke operation {StableId.describe operation.DisplayName operationId} requires {TypeRef.Capability ownerCapabilityId}, but the receiver produces {actual}."
                        )
                    | Ok _ when not (List.contains operation.EffectId owner.RequiredEffects) ->
                        Error(
                            diagnostic
                                "ExpressionCapabilityMissingEffectRequirement"
                                $"CapabilityInvoke operation {StableId.describe operation.DisplayName operationId} requires effect {effectLabel snapshot operation.EffectId} not declared by {owner.Description}."
                        )
                    | Ok _ ->
                        validateCapabilityArguments operation arguments |> Result.map (fun () -> operation.ResultType)
            | ExpressionRecipe.Abort _
            | ExpressionRecipe.Conditional _
            | ExpressionRecipe.MatchUnion _
            | ExpressionRecipe.Bind _
            | ExpressionRecipe.MatchOption _
            | ExpressionRecipe.MatchResult _ ->
                inferCompletion frames recipe
                |> Result.bind (function
                    | GraphCompletion.Normal value -> Ok value
                    | GraphCompletion.Aborts -> Error(diagnostic "AbortValueUse" $"An abrupt expression cannot be used as a value in {owner.Description}."))

        and inferCompletion frames recipe =
            let merge role left right =
                match GraphCompletion.merge [ Some left; Some right ] with
                | Some result -> Ok result
                | None -> Error(diagnostic "PureExpressionBranchTypeMismatch" $"{role} normally completing branches must produce one common type; found {left} and {right}.")
            match recipe with
            | ExpressionRecipe.Abort payload ->
                match owner.ResultType with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; errorType ]) ->
                    infer frames payload |> Result.bind (fun actual ->
                        if actual = errorType then Ok GraphCompletion.Aborts
                        else Error(diagnostic "AbortPayloadTypeMismatch" $"Abort payload has type {actual}, but {owner.Description} requires {errorType}."))
                | _ -> Error(diagnostic "AbortOwnerResultTypeMismatch" $"Abort requires {owner.Description} to return Result<T, E>.")
            | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
                match infer frames condition with
                | Error error -> Error error
                | Ok actual when actual <> boolType -> Error(typeMismatch "Conditional" "condition" boolType actual)
                | Ok _ ->
                    match inferCompletion frames whenTrue with
                    | Error error -> Error error
                    | Ok trueType ->
                        match inferCompletion frames whenFalse with
                        | Error error -> Error error
                        | Ok falseType -> merge "Conditional" trueType falseType
            | ExpressionRecipe.MatchUnion(input, arms) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.Named inputTypeId) ->
                    match Map.tryFind inputTypeId typesById with
                    | Some { Kind = SemanticTypeKind.Union variants } ->
                        match arms |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                        | Some(variantId, _) ->
                            Error(diagnostic "PureExpressionMatchArmDuplicate" $"MatchUnion contains variant '{StableId.value variantId}' more than once.")
                        | None ->
                            let expectedIds = variants |> List.map (fun variant -> variant.Id) |> Set.ofList
                            let actualIds = arms |> List.map fst |> Set.ofList
                            if expectedIds <> actualIds then
                                Error(diagnostic "PureExpressionMatchArmMismatch" "MatchUnion must contain every declared variant exactly once and no other variants.")
                            else
                                let rec inferArms expected = function
                                    | [] ->
                                        match expected with
                                        | Some resultType -> Ok resultType
                                        | None -> Error(diagnostic "PureExpressionMatchHasNoArms" "MatchUnion requires at least one declared variant to derive a result type.")
                                    | (variant: SemanticVariant) :: rest ->
                                        let _, body = arms |> List.find (fun (variantId, _) -> variantId = variant.Id)
                                        let frame: PatternTypeFrame =
                                            variant.Fields |> List.map (fun field -> RecipePatternSlot.Field field.Id, field.Type)
                                        match inferCompletion (frame :: frames) body with
                                        | Error error -> Error error
                                        | Ok actual ->
                                            match expected with
                                            | None -> inferArms (Some actual) rest
                                            | Some expectedType -> merge "MatchUnion" expectedType actual |> Result.bind (fun merged -> inferArms (Some merged) rest)
                                inferArms None variants
                    | Some _
                    | None ->
                        Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
            | ExpressionRecipe.Bind(input, body) ->
                match infer frames input with
                | Error error -> Error error
                | Ok inputType -> inferCompletion ([ RecipePatternSlot.Value, inputType ] :: frames) body
            | ExpressionRecipe.MatchOption(input, someBody, noneBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) ->
                    let someFrame: PatternTypeFrame = [ RecipePatternSlot.Value, itemType ]
                    match inferCompletion (someFrame :: frames) someBody with
                    | Error error -> Error error
                    | Ok someType ->
                        match inferCompletion ([] :: frames) noneBody with
                        | Error error -> Error error
                        | Ok noneType -> merge "MatchOption" someType noneType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchOption input must produce Option<T>.")
            | ExpressionRecipe.MatchResult(input, okBody, errBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                    let okFrame: PatternTypeFrame = [ RecipePatternSlot.Value, okType ]
                    let errFrame: PatternTypeFrame = [ RecipePatternSlot.Error, errorType ]
                    match inferCompletion (okFrame :: frames) okBody with
                    | Error error -> Error error
                    | Ok okResultType ->
                        match inferCompletion (errFrame :: frames) errBody with
                        | Error error -> Error error
                        | Ok errResultType -> merge "MatchResult" okResultType errResultType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchResult input must produce Result<T,E>.")

            | _ -> infer frames recipe |> Result.map GraphCompletion.Normal

        inferCompletion initialFrames recipe

    let private inferTypeWithFrames snapshot target initialFrames recipe =
        inferTypeWithContext snapshot (ownerForExistingFunction target) initialFrames recipe

    let private inferType snapshot target recipe = inferTypeWithFrames snapshot target [] recipe

    let private validatePreflight
        (request: AgentReplaceFunctionBodyWithExpressionRecipe)
        (baseSnapshot: Snapshot)
        : Result<SemanticFunction, TransactionDiagnostic> =
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
                    match inferType baseSnapshot target request.Replacement with
                    | Error error -> Error error
                    | Ok(GraphCompletion.Normal replacementType) when replacementType <> target.ResultType ->
                        Error(
                            diagnostic
                                "PureExpressionResultTypeMismatch"
                                $"The replacement recipe produces {replacementType}, but the target function returns {target.ResultType}."
                        )
                    | Ok _ ->
                        match bodyObligationError target baseSnapshot with
                        | Some error -> Error error
                        | None -> Ok target

    let private bindingTypeFrames (frames: PatternBindingFrame list): PatternTypeFrame list =
        frames |> List.map (List.map (fun (slot, binding) -> slot, binding.Type))

    let private resolvePatternBinding (frames: PatternBindingFrame list) (reference: RecipePatternReference) =
        match List.tryItem reference.ScopeDepth frames with
        | Some frame ->
            match frame |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
            | Some(_, binding) -> binding
            | None -> failwith "Pattern recipe passed preflight with an unavailable slot."
        | None -> failwith "Pattern recipe passed preflight with an unavailable scope."

    /// Materialization is deterministic in shape/order with no caller-controlled IDs or CSE.
    let rec private materializeWithContext
        (snapshot: Snapshot)
        (owner: ExpressionOwnerContext)
        (frames: PatternBindingFrame list)
        recipe
        =
        let materializeRecipe child = materializeWithContext snapshot owner frames child

        let materializeGraph childFrames body =
            let nodes, resultNodeId = materializeWithContext snapshot owner childFrames body
            { Nodes = nodes
              ResultNodeId = resultNodeId }

        let unary kind child =
            let childNodes, childResult = materializeRecipe child
            let nodeId = StableIdAllocator.newGraphNodeId ()
            childNodes @ [ { Id = nodeId; Kind = kind childResult } ], nodeId

        let binary kind left right =
            let leftNodes, leftResult = materializeRecipe left
            let rightNodes, rightResult = materializeRecipe right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ rightNodes @ [ { Id = nodeId; Kind = kind (leftResult, rightResult) } ], nodeId

        let rec many = function
            | [] -> [], []
            | head :: tail ->
                let headNodes, headResult = materializeRecipe head
                let tailNodes, tailResults = many tail
                headNodes @ tailNodes, headResult :: tailResults

        /// Materializes context values in the callback's parameter order, like call arguments.
        let materializeContext callbackFunctionId (context: (ParameterId * ExpressionRecipe) list) =
            let callback = snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = callbackFunctionId)
            callback.Parameters
            |> List.choose (fun parameter -> context |> List.tryFind (fun (parameterId, _) -> parameterId = parameter.Id))
            |> List.fold (fun (nodes, bindings) (parameterId, value) ->
                let valueNodes, valueResult = materializeRecipe value
                nodes @ valueNodes, bindings @ [ { TargetParameterId = parameterId; ValueNodeId = valueResult } ]) ([], [])

        let listCallback kind source callbackFunctionId context =
            let sourceNodes, sourceResult = materializeRecipe source
            let contextNodes, contextBindings = materializeContext callbackFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ contextNodes @ [ { Id = nodeId; Kind = kind (sourceResult, callbackFunctionId, contextBindings) } ], nodeId

        let materializeFieldBindings (expectedFields: SemanticField list) (bindings: (FieldId * ExpressionRecipe) list) =
            let rec loop = function
                | [] -> [], []
                | (field: SemanticField) :: rest ->
                    let _, value = bindings |> List.find (fun (fieldId, _) -> fieldId = field.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetFieldId = field.Id
                      ValueNodeId = valueResult } :: restBindings
            loop expectedFields

        let materializeCallArguments (callee: SemanticFunction) (bindings: (ParameterId * ExpressionRecipe) list) =
            let rec loop = function
                | [] -> [], []
                | (parameter: SemanticParameter) :: rest ->
                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetParameterId = parameter.Id
                      ValueNodeId = valueResult } :: restBindings
            loop callee.Parameters

        let materializeCapabilityArguments
            (operation: SemanticCapabilityOperation)
            (bindings: (CapabilityParameterId * ExpressionRecipe) list)
            =
            let rec loop = function
                | [] -> [], []
                | (parameter: SemanticCapabilityParameter) :: rest ->
                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetCapabilityParameterId = parameter.Id
                      ValueNodeId = valueResult } :: restBindings
            loop operation.Parameters

        let inferHere child =
            match inferTypeWithContext snapshot owner (bindingTypeFrames frames) child with
            | Ok(GraphCompletion.Normal typeRef) -> typeRef
            | _ -> failwith "Expression recipe passed preflight but failed materialization type recovery."

        match recipe with
        | ExpressionRecipe.Parameter parameterId ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ], nodeId
        | ExpressionRecipe.ParameterSlot index ->
            let parameterId =
                match owner.ParameterSlots |> Option.bind (List.tryItem index) with
                | Some { ParameterId = Some parameterId } -> parameterId
                | _ -> failwith "Parameter slot passed preflight without a materialized parameter ID."
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ], nodeId
        | ExpressionRecipe.PatternReference reference ->
            let binding = resolvePatternBinding frames reference
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.PatternReference binding.Id } ], nodeId
        | ExpressionRecipe.StringLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral value } ], nodeId
        | ExpressionRecipe.BoolLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.BoolLiteral value } ], nodeId
        | ExpressionRecipe.UnitLiteral ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.UnitLiteral } ], nodeId
        | ExpressionRecipe.Int64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Int64Literal value } ], nodeId
        | ExpressionRecipe.Float64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Float64Literal value } ], nodeId
        | ExpressionRecipe.StringIsEmpty value -> unary SemanticNodeKind.StringIsEmpty value
        | ExpressionRecipe.Int64ToString value -> unary SemanticNodeKind.Int64ToString value
        | ExpressionRecipe.StringParseInt64 value -> unary SemanticNodeKind.StringParseInt64 value
        | ExpressionRecipe.StringLength value -> unary SemanticNodeKind.StringLength value
        | ExpressionRecipe.BoolNot operand -> unary SemanticNodeKind.BoolNot operand
        | ExpressionRecipe.StringSlice(value, start, stop) ->
            let valueNodes, valueResult = materializeRecipe value
            let startNodes, startResult = materializeRecipe start
            let stopNodes, stopResult = materializeRecipe stop
            let nodeId = StableIdAllocator.newGraphNodeId ()
            valueNodes @ startNodes @ stopNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.StringSlice(valueResult, startResult, stopResult) } ],
            nodeId
        | ExpressionRecipe.StringContains(value, substring) -> binary SemanticNodeKind.StringContains value substring
        | ExpressionRecipe.StringEqual(left, right) -> binary SemanticNodeKind.StringEqual left right
        | ExpressionRecipe.StringLessThan(left, right) -> binary SemanticNodeKind.StringLessThan left right
        | ExpressionRecipe.ValueEqual(left, right) -> binary SemanticNodeKind.ValueEqual left right
        | ExpressionRecipe.StringConcat(left, right) -> binary SemanticNodeKind.StringConcat left right
        | ExpressionRecipe.Int64WrappingAdd(left, right) -> binary SemanticNodeKind.Int64WrappingAdd left right
        | ExpressionRecipe.Int64WrappingSubtract(left, right) -> binary SemanticNodeKind.Int64WrappingSubtract left right
        | ExpressionRecipe.Int64WrappingMultiply(left, right) -> binary SemanticNodeKind.Int64WrappingMultiply left right
        | ExpressionRecipe.Int64FloorDivide(left, right) -> binary SemanticNodeKind.Int64FloorDivide left right
        | ExpressionRecipe.Int64FloorModulo(left, right) -> binary SemanticNodeKind.Int64FloorModulo left right
        | ExpressionRecipe.Int64Equal(left, right) -> binary SemanticNodeKind.Int64Equal left right
        | ExpressionRecipe.Int64LessThan(left, right) -> binary SemanticNodeKind.Int64LessThan left right
        | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> binary SemanticNodeKind.Int64LessThanOrEqual left right
        | ExpressionRecipe.Int64GreaterThan(left, right) -> binary SemanticNodeKind.Int64GreaterThan left right
        | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> binary SemanticNodeKind.Int64GreaterThanOrEqual left right
        | ExpressionRecipe.Float64Add(left, right) -> binary SemanticNodeKind.Float64Add left right
        | ExpressionRecipe.Float64Subtract(left, right) -> binary SemanticNodeKind.Float64Subtract left right
        | ExpressionRecipe.Float64Multiply(left, right) -> binary SemanticNodeKind.Float64Multiply left right
        | ExpressionRecipe.Float64Divide(left, right) -> binary SemanticNodeKind.Float64Divide left right
        | ExpressionRecipe.Float64Equal(left, right) -> binary SemanticNodeKind.Float64Equal left right
        | ExpressionRecipe.Float64LessThan(left, right) -> binary SemanticNodeKind.Float64LessThan left right
        | ExpressionRecipe.Float64IsNaN value -> unary SemanticNodeKind.Float64IsNaN value
        | ExpressionRecipe.Float64IsFinite value -> unary SemanticNodeKind.Float64IsFinite value
        | ExpressionRecipe.Float64IsInfinite value -> unary SemanticNodeKind.Float64IsInfinite value
        | ExpressionRecipe.Int64ToFloat64 value -> unary SemanticNodeKind.Int64ToFloat64 value
        | ExpressionRecipe.ListEmpty itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ListEmpty itemType } ], nodeId
        | ExpressionRecipe.ListConstruct(itemType, items) ->
            let itemNodes, itemResults = many items
            let nodeId = StableIdAllocator.newGraphNodeId ()
            itemNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListConstruct(itemType, itemResults) } ], nodeId
        | ExpressionRecipe.ListLength list -> unary SemanticNodeKind.ListLength list
        | ExpressionRecipe.ListAppendItem(list, item) -> binary SemanticNodeKind.ListAppendItem list item
        | ExpressionRecipe.ListItemAt(list, index) -> binary SemanticNodeKind.ListItemAt list index
        | ExpressionRecipe.ListConcat(left, right) -> binary SemanticNodeKind.ListConcat left right
        | ExpressionRecipe.ListFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.ListEffectFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListEffectFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.UseResource(acquire, releaseCapability, releaseOperationId, bodyFunctionId, context) ->
            let acquireNodes, acquireResult = materializeRecipe acquire
            let capabilityNodes, capabilityResult = materializeRecipe releaseCapability
            let contextNodes, contextBindings = materializeContext bodyFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            acquireNodes @ capabilityNodes @ contextNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.UseResource(acquireResult, capabilityResult, releaseOperationId, bodyFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.ParallelAll(source, workerFunctionId, context) -> listCallback SemanticNodeKind.ParallelAll source workerFunctionId context
        | ExpressionRecipe.CancellationRequested ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.CancellationRequested } ], nodeId
        | ExpressionRecipe.TaskScope(bodyFunctionId, context) ->
            let contextNodes, contextBindings = materializeContext bodyFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.TaskScope(bodyFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.TaskStart(scope, workerFunctionId, context, item) ->
            let scopeNodes, scopeResult = materializeRecipe scope
            let itemNodes, itemResult = materializeRecipe item
            let contextNodes, contextBindings = materializeContext workerFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            scopeNodes @ itemNodes @ contextNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.TaskStart(scopeResult, workerFunctionId, contextBindings, itemResult) } ], nodeId
        | ExpressionRecipe.TaskWait(tasks, mode, limit) ->
            let tasksNodes, tasksResult = materializeRecipe tasks
            let limitNodes, limitResult =
                match limit with
                | Some limit -> materializeRecipe limit |> fun (nodes, result) -> nodes, Some result
                | None -> [], None
            let nodeId = StableIdAllocator.newGraphNodeId ()
            tasksNodes @ limitNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.TaskWait(tasksResult, mode, limitResult) } ], nodeId
        | ExpressionRecipe.TaskCancel task -> unary SemanticNodeKind.TaskCancel task
        | ExpressionRecipe.ListMap(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.ListMap source mapperFunctionId context
        | ExpressionRecipe.ListFilter(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListFilter source predicateFunctionId context
        | ExpressionRecipe.ListSortBy(source, lessThanFunctionId, context) -> listCallback SemanticNodeKind.ListSortBy source lessThanFunctionId context
        | ExpressionRecipe.ListAny(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListAny source predicateFunctionId context
        | ExpressionRecipe.ListAll(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListAll source predicateFunctionId context
        | ExpressionRecipe.ProductConstruct(typeId, fields) ->
            let expectedFields =
                match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                | { Kind = SemanticTypeKind.Product expectedFields } -> expectedFields
                | _ -> failwith "Product recipe passed preflight with a non-product type."
            let fieldNodes, fieldBindings = materializeFieldBindings expectedFields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ProductConstruct(typeId, fieldBindings) } ], nodeId
        | ExpressionRecipe.FieldAccess(source, fieldId) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.FieldAccess(sourceResult, fieldId) } ], nodeId
        | ExpressionRecipe.UnionConstruct(variantId, fields) ->
            let variant =
                snapshot.Types
                |> List.collect (fun typeEntity ->
                    match typeEntity.Kind with
                    | SemanticTypeKind.Product _ -> []
                    | SemanticTypeKind.Union variants -> variants)
                |> List.find (fun variant -> variant.Id = variantId)
            let fieldNodes, fieldBindings = materializeFieldBindings variant.Fields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.UnionConstruct(variantId, fieldBindings) } ], nodeId
        | ExpressionRecipe.OptionNone itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.OptionNone itemType } ], nodeId
        | ExpressionRecipe.OptionSome value -> unary SemanticNodeKind.OptionSome value
        | ExpressionRecipe.ResultOk(errorType, value) -> unary (fun valueNodeId -> SemanticNodeKind.ResultOk(errorType, valueNodeId)) value
        | ExpressionRecipe.Abort error -> unary SemanticNodeKind.Abort error
        | ExpressionRecipe.ResultErr(okType, error) -> unary (fun errorNodeId -> SemanticNodeKind.ResultErr(okType, errorNodeId)) error
        | ExpressionRecipe.CapabilityConstruct(capabilityId, implementations) ->
            let id = StableIdAllocator.newGraphNodeId ()
            [ { Id = id; Kind = SemanticNodeKind.CapabilityConstruct(capabilityId, List.sortBy fst implementations) } ], id
        | ExpressionRecipe.FunctionReference target ->
            let id = StableIdAllocator.newGraphNodeId ()
            [ { Id = id; Kind = SemanticNodeKind.FunctionReference target } ], id
        | ExpressionRecipe.FunctionInvoke(value, arguments) ->
            let nodes, values = many (value :: arguments)
            let id = StableIdAllocator.newGraphNodeId ()
            nodes @ [ { Id = id; Kind = SemanticNodeKind.FunctionInvoke(values.Head, values.Tail) } ], id
        | ExpressionRecipe.DirectCall(targetFunctionId, arguments) ->
            let callee = snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = targetFunctionId)
            let argumentNodes, argumentBindings = materializeCallArguments callee arguments
            let nodeId = StableIdAllocator.newGraphNodeId ()
            argumentNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.DirectCall(targetFunctionId, argumentBindings) } ], nodeId
        | ExpressionRecipe.SelfCall arguments ->
            let ownerFunctionId =
                match owner.SelfFunctionId with
                | Some functionId -> functionId
                | None -> failwith "SelfCall passed preflight without a materialized owner function ID."
            let rec loop slot = function
                | [] -> [], []
                | (parameter: ParameterSlotContext) :: rest ->
                    let parameterId =
                        match parameter.ParameterId with
                        | Some parameterId -> parameterId
                        | None -> failwith "SelfCall passed preflight without a materialized parameter ID."
                    let _, value = arguments |> List.find (fun (candidate, _) -> candidate = slot)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop (slot + 1) rest
                    valueNodes @ restNodes,
                    { TargetParameterId = parameterId
                      ValueNodeId = valueResult } :: restBindings
            let argumentNodes, argumentBindings = loop 0 owner.SelfParameters
            let nodeId = StableIdAllocator.newGraphNodeId ()
            argumentNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.DirectCall(ownerFunctionId, argumentBindings) } ], nodeId
        | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
            let capabilityNodes, capabilityResult = materializeRecipe capabilityValue
            let operation =
                snapshot.Capabilities
                |> List.collect (fun capability -> capability.Operations)
                |> List.find (fun operation -> operation.Id = operationId)
            let argumentNodes, argumentBindings = materializeCapabilityArguments operation arguments
            let nodeId = StableIdAllocator.newGraphNodeId ()
            capabilityNodes @ argumentNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.CapabilityInvoke(capabilityResult, operationId, argumentBindings) } ], nodeId
        | ExpressionRecipe.BoolAnd(left, right) ->
            let leftNodes, leftResult = materializeRecipe left
            let rightGraph = materializeGraph frames right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.BoolAnd(leftResult, rightGraph) } ], nodeId
        | ExpressionRecipe.BoolOr(left, right) ->
            let leftNodes, leftResult = materializeRecipe left
            let rightGraph = materializeGraph frames right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.BoolOr(leftResult, rightGraph) } ], nodeId
        | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
            let conditionNodes, conditionResult = materializeRecipe condition
            let trueGraph = materializeGraph frames whenTrue
            let falseGraph = materializeGraph frames whenFalse
            let nodeId = StableIdAllocator.newGraphNodeId ()
            conditionNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.Conditional(conditionResult, trueGraph, falseGraph) } ], nodeId
        | ExpressionRecipe.MatchUnion(input, arms) ->
            let inputNodes, inputResult = materializeRecipe input
            let inputType = inferHere input
            let variants =
                match inputType with
                | TypeRef.Named typeId ->
                    match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                    | { Kind = SemanticTypeKind.Union variants } -> variants
                    | _ -> failwith "MatchUnion recipe passed preflight with a non-union type."
                | _ -> failwith "MatchUnion recipe passed preflight with a non-named input type."

            let preparedArms =
                variants
                |> List.map (fun variant ->
                    let preparedFields =
                        variant.Fields
                        |> List.map (fun field ->
                            let binding: PatternBinding =
                                { Id = StableIdAllocator.newPatternBindingId ()
                                  DisplayName = None
                                  Type = field.Type }
                            { SourceFieldId = field.Id
                              Binding = binding },
                            (RecipePatternSlot.Field field.Id, binding))
                    variant,
                    (preparedFields |> List.map fst),
                    (preparedFields |> List.map snd))

            let materializedArms: UnionMatchArm list =
                preparedArms
                |> List.map (fun (variant, fieldBindings, frame) ->
                    let _, bodyRecipe = arms |> List.find (fun (variantId, _) -> variantId = variant.Id)
                    { VariantId = variant.Id
                      FieldBindings = fieldBindings
                      Body = materializeGraph (frame :: frames) bodyRecipe })

            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchUnion(inputResult, materializedArms) } ], nodeId
        | ExpressionRecipe.Bind(input, body) ->
            let inputNodes, inputResult = materializeRecipe input
            let binding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = inferHere input }
            let boundBody: ValueMatchArm =
                { ValueBinding = binding
                  Body = materializeGraph ([ RecipePatternSlot.Value, binding ] :: frames) body }
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.Bind(inputResult, boundBody) } ], nodeId
        | ExpressionRecipe.MatchOption(input, someBody, noneBody) ->
            let inputNodes, inputResult = materializeRecipe input
            let itemType =
                match inferHere input with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) -> itemType
                | _ -> failwith "MatchOption recipe passed preflight with a non-Option input."
            let someBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = itemType }
            let someArm: ValueMatchArm =
                { ValueBinding = someBinding
                  Body = materializeGraph ([ RecipePatternSlot.Value, someBinding ] :: frames) someBody }
            let noneGraph = materializeGraph ([] :: frames) noneBody
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchOption(inputResult, someArm, noneGraph) } ], nodeId
        | ExpressionRecipe.MatchResult(input, okBody, errBody) ->
            let inputNodes, inputResult = materializeRecipe input
            let okType, errorType =
                match inferHere input with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) -> okType, errorType
                | _ -> failwith "MatchResult recipe passed preflight with a non-Result input."
            let okBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = okType }
            let errBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = errorType }
            let okArm: ValueMatchArm =
                { ValueBinding = okBinding
                  Body = materializeGraph ([ RecipePatternSlot.Value, okBinding ] :: frames) okBody }
            let errArm: ErrorMatchArm =
                { ErrorBinding = errBinding
                  Body = materializeGraph ([ RecipePatternSlot.Error, errBinding ] :: frames) errBody }
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchResult(inputResult, okArm, errArm) } ], nodeId

    let private materialize snapshot target frames recipe =
        materializeWithContext snapshot (ownerForExistingFunction target) frames recipe

    /// Materializes a complete replacement body for an existing function against a candidate
    /// snapshot that a multi-function migration is still completing. The recipe is bound and
    /// type-checked against the candidate; the caller validates the final snapshot as a whole.
    let internal materializeReplacementBody (snapshot: Snapshot) (target: SemanticFunction) recipe
        : Result<ValueGraph, TransactionDiagnostic> =
        match validateRecipeBounds recipeChildren recipe with
        | Error error -> Error error
        | Ok() ->
            match inferType snapshot target recipe with
            | Error error -> Error error
            | Ok(GraphCompletion.Normal replacementType) when replacementType <> target.ResultType ->
                Error(
                    diagnostic
                        "PureExpressionResultTypeMismatch"
                        $"The replacement recipe produces {replacementType}, but the target function returns {target.ResultType}."
                )
            | Ok _ ->
                let nodes, resultNodeId = materialize snapshot target [] recipe
                Ok { Nodes = nodes; ResultNodeId = resultNodeId }

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithExpressionRecipe)
        : Result<AgentExpressionStructuralChangeProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            match validatePreflight request baseSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Ok target ->
                let newNodes, newResultId = materialize baseSnapshot target [] request.Replacement
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
                            match SemanticQuery.focusFunction baseSnapshot target.Id, SemanticQuery.focusFunction proposedSnapshot target.Id with
                            | Error error, _
                            | _, Error error -> Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                            | Ok baseFocus, Ok proposedFocus ->
                                let proposedTarget = proposedSnapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = target.Id)
                                let diff =
                                    { OwnerFunctionId = target.Id
                                      OldResultNodeId = target.Body.ResultNodeId
                                      NewResultNodeId = newResultId
                                      RemovedNodes =
                                        ValueGraph.collectNodes target.Body
                                        |> summarizeNodes baseSnapshot target
                                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                                      AddedNodes =
                                        ValueGraph.collectNodes proposedTarget.Body
                                        |> summarizeNodes proposedSnapshot proposedTarget }
                                let review =
                                    { RequestedChange = request
                                      Receipt = receipt
                                      Readiness = readiness
                                      ResultingChange = diff
                                      BaseFocus = baseFocus
                                      ProposedFocus = proposedFocus }
                                Ok(
                                    AgentExpressionStructuralChangeProposal
                                        { TransactionId = transactionId
                                          ProposedSnapshot = proposedSnapshot
                                          Review = review }
                                )

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

        let private childGraphs (node: SemanticNode) =
            match node.Kind with
            | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) -> [ whenTrue; whenFalse ]
            | SemanticNodeKind.BoolAnd(_, right)
            | SemanticNodeKind.BoolOr(_, right) -> [ right ]
            | SemanticNodeKind.MatchUnion(_, arms) -> arms |> List.map (fun arm -> arm.Body)
            | SemanticNodeKind.Bind(_, body) -> [ body.Body ]
            | SemanticNodeKind.MatchOption(_, someArm, noneBody) -> [ someArm.Body; noneBody ]
            | SemanticNodeKind.MatchResult(_, okArm, errArm) -> [ okArm.Body; errArm.Body ]
            | _ -> []

        let rec private tryFindTarget
            (frames: PatternBindingFrame list)
            targetNodeId
            (graph: ValueGraph)
            =
            match graph.Nodes |> List.tryFind (fun node -> node.Id = targetNodeId) with
            | Some node -> Some(node, frames)
            | None ->
                let choose first second =
                    match first with
                    | Some _ -> first
                    | None -> second ()

                graph.Nodes
                |> List.tryPick (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                        choose
                            (tryFindTarget frames targetNodeId whenTrue)
                            (fun () -> tryFindTarget frames targetNodeId whenFalse)
                    | SemanticNodeKind.BoolAnd(_, right)
                    | SemanticNodeKind.BoolOr(_, right) -> tryFindTarget frames targetNodeId right
                    | SemanticNodeKind.MatchUnion(_, arms) ->
                        arms
                        |> List.tryPick (fun arm ->
                            let frame: PatternBindingFrame =
                                arm.FieldBindings
                                |> List.map (fun field ->
                                    RecipePatternSlot.Field field.SourceFieldId, field.Binding)
                            tryFindTarget (frame :: frames) targetNodeId arm.Body)
                    | SemanticNodeKind.Bind(_, body) ->
                        let frame: PatternBindingFrame =
                            [ RecipePatternSlot.Value, body.ValueBinding ]
                        tryFindTarget (frame :: frames) targetNodeId body.Body
                    | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                        let someFrame: PatternBindingFrame =
                            [ RecipePatternSlot.Value, someArm.ValueBinding ]
                        choose
                            (tryFindTarget (someFrame :: frames) targetNodeId someArm.Body)
                            (fun () -> tryFindTarget ([] :: frames) targetNodeId noneBody)
                    | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                        let okFrame: PatternBindingFrame =
                            [ RecipePatternSlot.Value, okArm.ValueBinding ]
                        let errFrame: PatternBindingFrame =
                            [ RecipePatternSlot.Error, errArm.ErrorBinding ]
                        choose
                            (tryFindTarget (okFrame :: frames) targetNodeId okArm.Body)
                            (fun () -> tryFindTarget (errFrame :: frames) targetNodeId errArm.Body)
                    | _ -> None)

        let rec private graphResultUses owner (graph: ValueGraph) =
            (graph.ResultNodeId, owner)
            :: (graph.Nodes
                |> List.collect (fun node ->
                    childGraphs node
                    |> List.collect (graphResultUses (Some node.Id))))

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
                            SemanticFacts.directValueDependencies node
                            |> List.fold collectNode visited
                        childGraphs node |> List.fold collectGraph visited

            and collectGraph visited (graph: ValueGraph) =
                graph.Nodes
                |> List.fold (fun state node -> collectNode state node.Id) visited

            let candidates =
                let direct =
                    SemanticFacts.directValueDependencies targetNode
                    |> List.fold collectNode Set.empty
                childGraphs targetNode |> List.fold collectGraph direct

            let targetOwned =
                childGraphs targetNode
                |> List.collect ValueGraph.collectNodes
                |> List.map (fun node -> node.Id)
                |> Set.ofList

            let externalNodeSeeds =
                allNodes
                |> List.filter (fun node ->
                    node.Id <> targetNodeId && not (Set.contains node.Id candidates))
                |> List.collect SemanticFacts.directValueDependencies
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
                            SemanticFacts.directValueDependencies node
                            |> List.fold protect retained
                        childGraphs node
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
            (baseSnapshot: Snapshot)
            : Result<Preflight, TransactionDiagnostic> =
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
                                    inferTypeWithFrames
                                        baseSnapshot
                                        target
                                        (bindingTypeFrames frames)
                                        request.Replacement
                                with
                                | Error error -> Error error
                                | Ok(GraphCompletion.Normal replacementType) when targetType <> GraphCompletion.Aborts && GraphCompletion.Normal replacementType <> targetType ->
                                    Error(
                                        diagnostic
                                            "TargetedExpressionTypeMismatch"
                                            $"The replacement recipe produces {replacementType}, but the target node produces {targetType}."
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

        let private mapNodeGraphs (mapGraph: ValueGraph -> ValueGraph) (node: SemanticNode) =
            let kind =
                match node.Kind with
                | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                    SemanticNodeKind.Conditional(
                        conditionNodeId,
                        mapGraph whenTrue,
                        mapGraph whenFalse
                    )
                | SemanticNodeKind.BoolAnd(leftNodeId, right) -> SemanticNodeKind.BoolAnd(leftNodeId, mapGraph right)
                | SemanticNodeKind.BoolOr(leftNodeId, right) -> SemanticNodeKind.BoolOr(leftNodeId, mapGraph right)
                | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                    SemanticNodeKind.MatchUnion(
                        inputNodeId,
                        arms
                        |> List.map (fun arm -> { arm with Body = mapGraph arm.Body })
                    )
                | SemanticNodeKind.Bind(inputNodeId, body) ->
                    SemanticNodeKind.Bind(
                        inputNodeId,
                        { body with Body = mapGraph body.Body }
                    )
                | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                    SemanticNodeKind.MatchOption(
                        inputNodeId,
                        { someArm with Body = mapGraph someArm.Body },
                        mapGraph noneBody
                    )
                | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                    SemanticNodeKind.MatchResult(
                        inputNodeId,
                        { okArm with Body = mapGraph okArm.Body },
                        { errArm with Body = mapGraph errArm.Body }
                    )
                | _ -> node.Kind
            { node with Kind = kind }

        let rec private replaceTarget targetNodeId replacementNodes (graph: ValueGraph) =
            if graph.Nodes |> List.exists (fun node -> node.Id = targetNodeId) then
                { graph with
                    Nodes =
                        graph.Nodes
                        |> List.collect (fun node ->
                            if node.Id = targetNodeId then replacementNodes else [ node ]) }
            else
                { graph with
                    Nodes =
                        graph.Nodes
                        |> List.map (mapNodeGraphs (replaceTarget targetNodeId replacementNodes)) }

        let rec private pruneNodes removedNodeIds (graph: ValueGraph) =
            { graph with
                Nodes =
                    graph.Nodes
                    |> List.choose (fun node ->
                        if Set.contains node.Id removedNodeIds then
                            None
                        else
                            Some(mapNodeGraphs (pruneNodes removedNodeIds) node)) }

        let private materializeReplacement
            snapshot
            target
            frames
            targetNodeId
            recipe
            =
            let nodes, generatedRootId = materialize snapshot target frames recipe
            nodes
            |> List.map (fun node ->
                if node.Id = generatedRootId then
                    { node with Id = targetNodeId }
                else
                    node)

        let simulate
            (workspace: AgentWorkspace)
            (request: Request)
            : Result<Proposal, AgentStructuralWorkflowError> =
            let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

            match SnapshotValidator.validate baseSnapshot with
            | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
            | [] ->
                match validatePreflight request baseSnapshot with
                | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
                | Ok preflight ->
                    let replacementNodes =
                        materializeReplacement
                            baseSnapshot
                            preflight.Target
                            preflight.Frames
                            request.TargetNodeId
                            request.Replacement

                    let replacementBody =
                        preflight.Target.Body
                        |> replaceTarget request.TargetNodeId replacementNodes
                        |> pruneNodes preflight.Pruning.RemovedNodeIds

                    let proposedSnapshotId = StableIdAllocator.newSnapshotId ()
                    let transactionId = StableIdAllocator.newTransactionId ()
                    let proposedSnapshot =
                        { baseSnapshot with
                            Id = proposedSnapshotId
                            ParentId = Some baseSnapshot.Id
                            Functions =
                                baseSnapshot.Functions
                                |> List.map (fun functionEntity ->
                                    if functionEntity.Id = preflight.Target.Id then
                                        { functionEntity with Body = replacementBody }
                                    else
                                        functionEntity) }

                    match SnapshotValidator.validate proposedSnapshot with
                    | issue :: _ -> rejected "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                    | [] ->
                        match classifyReadiness proposedSnapshot with
                        | Error diagnostics ->
                            Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                        | Ok readiness ->
                            match createReceipt transactionId baseSnapshot proposedSnapshot with
                            | Error error -> Error error
                            | Ok receipt ->
                                match
                                    SemanticQuery.focusFunction baseSnapshot preflight.Target.Id,
                                    SemanticQuery.focusFunction proposedSnapshot preflight.Target.Id
                                with
                                | Error error, _
                                | _, Error error ->
                                    Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                                | Ok baseFocus, Ok proposedFocus ->
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
                                    )

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
              Parameters: ParameterIntent list
              ResultType: TypeRef
              RequiredEffects: EffectId list
              Body: ExpressionRecipe }

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

        [<RequireQualifiedAccess>]
        type CommitResult =
            | Rejected of TransactionDiagnostic list
            | Committed of AgentWorkspace * PublishedTransaction

        let private validatePreflight (request: Request) (baseSnapshot: Snapshot) =
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
                    let typeRefs =
                        request.ResultType
                        :: (request.Parameters |> List.map (fun parameter -> parameter.Type))

                    let rec validateTypes = function
                        | [] -> Ok()
                        | typeRef :: rest ->
                            match validateTypeRef baseSnapshot typeRef with
                            | Error error -> Error error
                            | Ok() -> validateTypes rest

                    match validateTypes typeRefs with
                    | Error error -> Error error
                    | Ok() ->
                        match
                            request.RequiredEffects
                            |> List.countBy id
                            |> List.tryFind (fun (_, count) -> count > 1)
                        with
                        | Some(effectId, _) ->
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
                                          ParameterId = None })
                                let owner =
                                    { ParametersById = Map.empty
                                      ParameterSlots = Some slots
                                      RequiredEffects = canonicalEffects
                                      ResultType = request.ResultType
                                      SelfFunctionId = None
                                      SelfParameters = slots
                                      Description = "the function being created" }

                                match validateRecipeBounds recipeChildren request.Body with
                                | Error error -> Error error
                                | Ok() ->
                                    match inferTypeWithContext baseSnapshot owner [] request.Body with
                                    | Error error -> Error error
                                    | Ok(GraphCompletion.Normal bodyType) when bodyType <> request.ResultType ->
                                        Error(
                                            diagnostic
                                                "FunctionCreationResultTypeMismatch"
                                                $"The function body produces {bodyType}, but the requested result type is {request.ResultType}."
                                        )
                                    | Ok _ ->
                                        Ok
                                            { CanonicalRequiredEffects = canonicalEffects
                                              Owner = owner }

        /// A new function owns its type parameters: every type parameter named in the request
        /// receives a fresh system-owned identity, keeping its display name.
        let private withFreshTypeParameters (request: Request): Request =
            let named =
                (request.ResultType :: (request.Parameters |> List.map (fun parameter -> parameter.Type)))
                @ ExpressionRecipeTypes.collect request.Body
                |> List.collect TypeParameters.occurring
                |> List.distinctBy fst
            if List.isEmpty named then request
            else
                let fresh =
                    named |> List.map (fun (id, name) -> id, TypeRef.TypeParameter(StableIdAllocator.newTypeParameterId (), name)) |> Map.ofList
                let mapType = TypeParameters.substitute fresh
                { request with
                    Parameters = request.Parameters |> List.map (fun parameter -> { parameter with Type = mapType parameter.Type })
                    ResultType = mapType request.ResultType
                    Body = ExpressionRecipeTypes.map mapType request.Body }

        let simulate
            (workspace: AgentWorkspace)
            (request: Request)
            : Result<Proposal, AgentStructuralWorkflowError> =
            let request = withFreshTypeParameters request
            let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

            match SnapshotValidator.validate baseSnapshot with
            | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
            | [] ->
                match validatePreflight request baseSnapshot with
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
                                  ParameterId = Some parameter.Id })
                            parameters
                            request.Parameters
                    let materializationOwner =
                        { preflight.Owner with
                            ParameterSlots = Some slots
                            SelfFunctionId = Some functionId
                            SelfParameters = slots }
                    let bodyNodes, bodyResultId =
                        materializeWithContext
                            baseSnapshot
                            materializationOwner
                            []
                            request.Body
                    let createdFunction: SemanticFunction =
                        { Id = functionId
                          DisplayName = request.DisplayName
                          Parameters = parameters
                          ResultType = request.ResultType
                          RequiredEffects = preflight.CanonicalRequiredEffects
                          Body =
                            { Nodes = bodyNodes
                              ResultNodeId = bodyResultId } }
                    let transactionId = StableIdAllocator.newTransactionId ()
                    let proposedSnapshot =
                        { baseSnapshot with
                            Id = StableIdAllocator.newSnapshotId ()
                            ParentId = Some baseSnapshot.Id
                            Functions = baseSnapshot.Functions @ [ createdFunction ] }

                    match SnapshotValidator.validate proposedSnapshot with
                    | issue :: _ -> rejected "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                    | [] ->
                        match classifyReadiness proposedSnapshot with
                        | Error diagnostics ->
                            Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                        | Ok readiness ->
                            match createReceipt transactionId baseSnapshot proposedSnapshot with
                            | Error error -> Error error
                            | Ok receipt ->
                                match SemanticQuery.focusFunction proposedSnapshot functionId with
                                | Error error ->
                                    Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                                | Ok proposedFocus ->
                                    let bodySummary =
                                        ValueGraph.collectNodes createdFunction.Body
                                        |> summarizeNodes proposedSnapshot createdFunction
                                    let review =
                                        { RequestedChange = request
                                          Receipt = receipt
                                          Readiness = readiness
                                          CreatedFunction = createdFunction
                                          MaterializedBody = bodySummary
                                          ProposedFocus = proposedFocus }
                                    Ok(
                                        Proposal
                                            { TransactionId = transactionId
                                              ProposedSnapshot = proposedSnapshot
                                              Review = review }
                                    )

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
