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
    | Int64ToHexString of ExpressionRecipe
    | StringParseInt64 of ExpressionRecipe
    | StringParseInt64Hex of ExpressionRecipe
    | Float64ToString of ExpressionRecipe
    | StringParseFloat64 of ExpressionRecipe
    | StringCodePoints of ExpressionRecipe
    | StringToLower of ExpressionRecipe
    | StringToUpper of ExpressionRecipe
    | StringDecodeUtf8Lossy of ExpressionRecipe
    | Float64RoundToInt64 of ExpressionRecipe
    | Float64FloorToInt64 of ExpressionRecipe
    | Float64Sqrt of ExpressionRecipe
    | Float64Abs of ExpressionRecipe
    | Float64Negate of ExpressionRecipe
    | Float64Floor of ExpressionRecipe
    | Float64Ceiling of ExpressionRecipe
    | Float64Truncate of ExpressionRecipe
    | StringUtf8ByteCount of ExpressionRecipe
    | StringFromCodePoint of ExpressionRecipe
    | StringToCodePoint of ExpressionRecipe
    | StringLength of ExpressionRecipe
    | StringSlice of value: ExpressionRecipe * start: ExpressionRecipe * ``end``: ExpressionRecipe
    | StringContains of ExpressionRecipe * ExpressionRecipe
    | StringIndexOf of ExpressionRecipe * ExpressionRecipe * ExpressionRecipe
    | StringSpan of value: ExpressionRecipe * set: ExpressionRecipe * from: ExpressionRecipe
    | StringSpanBack of value: ExpressionRecipe * set: ExpressionRecipe * ``end``: ExpressionRecipe
    | StringLastIndexOf of ExpressionRecipe * ExpressionRecipe
    | StringStartsWith of ExpressionRecipe * ExpressionRecipe
    | StringEndsWith of ExpressionRecipe * ExpressionRecipe
    | StringSplit of ExpressionRecipe * ExpressionRecipe
    | StringSplitLines of value: ExpressionRecipe * breaks: ExpressionRecipe
    | StringReplace of value: ExpressionRecipe * pattern: ExpressionRecipe * replacement: ExpressionRecipe
    | StringTrim of ExpressionRecipe
    | StringTrimStart of ExpressionRecipe
    | StringTrimEnd of ExpressionRecipe
    | StringJoin of list: ExpressionRecipe * separator: ExpressionRecipe
    | StringEqual of ExpressionRecipe * ExpressionRecipe
    | StringLessThan of ExpressionRecipe * ExpressionRecipe
    | ValueEqual of ExpressionRecipe * ExpressionRecipe
    | StringConcat of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingAdd of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingSubtract of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingMultiply of ExpressionRecipe * ExpressionRecipe
    | Int64FloorDivide of ExpressionRecipe * ExpressionRecipe
    | Int64FloorModulo of ExpressionRecipe * ExpressionRecipe
    | Int64Min of ExpressionRecipe * ExpressionRecipe
    | Int64Max of ExpressionRecipe * ExpressionRecipe
    | Int64CheckedAdd of ExpressionRecipe * ExpressionRecipe
    | Int64CheckedSubtract of ExpressionRecipe * ExpressionRecipe
    | Int64CheckedMultiply of ExpressionRecipe * ExpressionRecipe
    | ListRange of ExpressionRecipe * ExpressionRecipe
    | StringRepeat of ExpressionRecipe * ExpressionRecipe
    | Float64Round of ExpressionRecipe * ExpressionRecipe
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
    | Int64WrappingAbs of ExpressionRecipe
    | Bits32FromInt64 of ExpressionRecipe
    | Bits32ToInt64 of ExpressionRecipe
    | Bits32Not of ExpressionRecipe
    | Bits32Xor of ExpressionRecipe * ExpressionRecipe
    | Bits32And of ExpressionRecipe * ExpressionRecipe
    | Bits32Or of ExpressionRecipe * ExpressionRecipe
    | Bits32Add of ExpressionRecipe * ExpressionRecipe
    | Bits32RotateRight of value: ExpressionRecipe * amount: ExpressionRecipe
    | Bits32ShiftRight of value: ExpressionRecipe * amount: ExpressionRecipe
    | Bits32ShiftLeft of value: ExpressionRecipe * amount: ExpressionRecipe
    | ListEmpty of itemType: TypeRef
    | SetEmpty of itemType: TypeRef
    | MapEmpty of keyType: TypeRef * valueType: TypeRef
    | ListConstruct of itemType: TypeRef * items: ExpressionRecipe list
    | ListLength of ExpressionRecipe
    | MapValues of ExpressionRecipe
    | MapKeys of ExpressionRecipe
    | SetToList of ExpressionRecipe
    | MapSize of ExpressionRecipe
    | SetFromList of ExpressionRecipe
    | SetSize of ExpressionRecipe
    | ListReverse of ExpressionRecipe
    | ListLast of ExpressionRecipe
    | ListDropLast of ExpressionRecipe
    | ListAppendItem of list: ExpressionRecipe * item: ExpressionRecipe
    | ListItemAt of list: ExpressionRecipe * index: ExpressionRecipe
    | MapRemove of map: ExpressionRecipe * key: ExpressionRecipe
    | SetInsert of map: ExpressionRecipe * key: ExpressionRecipe
    | SetRemove of map: ExpressionRecipe * key: ExpressionRecipe
    | MapContainsKey of map: ExpressionRecipe * key: ExpressionRecipe
    | SetContains of map: ExpressionRecipe * key: ExpressionRecipe
    | ListIndexOf of list: ExpressionRecipe * item: ExpressionRecipe
    | ListLastIndexOf of list: ExpressionRecipe * item: ExpressionRecipe
    | MapGet of map: ExpressionRecipe * key: ExpressionRecipe
    | MapGetOrDefault of map: ExpressionRecipe * key: ExpressionRecipe * defaultValue: ExpressionRecipe
    | ListSlice of list: ExpressionRecipe * start: ExpressionRecipe * ``end``: ExpressionRecipe
    | MapInsert of map: ExpressionRecipe * key: ExpressionRecipe * ``end``: ExpressionRecipe
    | ListConcat of left: ExpressionRecipe * right: ExpressionRecipe
    | SetUnion of left: ExpressionRecipe * right: ExpressionRecipe
    | SetIntersection of left: ExpressionRecipe * right: ExpressionRecipe
    | SetDifference of left: ExpressionRecipe * right: ExpressionRecipe
    /// `context` binds callback parameters by identity; the unbound ones receive iteration values.
    | ListFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ParallelAll of source: ExpressionRecipe * workerFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | CancellationRequested
    /// The body's one unbound Unit parameter is supplied while ambient cancellation is masked.
    | ExceptionCatch of bodyFunctionId: FunctionId * bodyContext: (ParameterId * ExpressionRecipe) list * handlerFunctionId: FunctionId * handlerContext: (ParameterId * ExpressionRecipe) list
    | ExceptionIsCancellation of exceptionValue: ExpressionRecipe
    | ExceptionMessage of exceptionValue: ExpressionRecipe
    | ExceptionRethrow of exceptionValue: ExpressionRecipe * resultType: TypeRef
    | CancellationShield of bodyFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    /// The body's one unbound parameter receives the new `TaskScope`.
    | TaskScope of bodyFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | TaskStart of scope: ExpressionRecipe * workerFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list * item: ExpressionRecipe
    /// `limit` is an optional `Option<Int64>` of milliseconds; absent means no limit.
    | TaskWait of tasks: ExpressionRecipe * mode: TaskWaitMode * limit: ExpressionRecipe option
    | TaskCancel of task: ExpressionRecipe
    | OneShotCreate of scope: ExpressionRecipe * valueType: TypeRef
    | OneShotComplete of cell: ExpressionRecipe * value: ExpressionRecipe
    | OneShotWait of cell: ExpressionRecipe * limit: ExpressionRecipe option
    | UseResource of acquire: ExpressionRecipe * releaseCapability: ExpressionRecipe * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListEffectFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListMap of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapFromList of source: ExpressionRecipe * keyOfFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListFilter of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapFilter of source: ExpressionRecipe * keepFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapMapValues of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListSortBy of source: ExpressionRecipe * lessThanFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListMinBy of source: ExpressionRecipe * lessThanFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListAny of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListFindIndex of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListAll of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ProductConstruct of typeId: TypeId * fields: (FieldId * ExpressionRecipe) list
    | FieldAccess of source: ExpressionRecipe * fieldId: FieldId
    | ProductUpdate of typeId: TypeId * source: ExpressionRecipe * fields: (FieldId * ExpressionRecipe) list
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
    /// Each arm names one or more variants. At most one arm may name none: it is the `otherwise` arm,
    /// and materialization expands it to an explicit list of the variants no other arm names.
    | MatchUnion of input: ExpressionRecipe * arms: (VariantId list * ExpressionRecipe) list
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
    /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot

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
        | ExpressionRecipe.Int64ToHexString value -> ExpressionRecipe.Int64ToHexString(go value)
        | ExpressionRecipe.StringParseInt64 value -> ExpressionRecipe.StringParseInt64(go value)
        | ExpressionRecipe.StringParseInt64Hex value -> ExpressionRecipe.StringParseInt64Hex(go value)
        | ExpressionRecipe.Float64ToString value -> ExpressionRecipe.Float64ToString(go value)
        | ExpressionRecipe.StringParseFloat64 value -> ExpressionRecipe.StringParseFloat64(go value)
        | ExpressionRecipe.StringCodePoints value -> ExpressionRecipe.StringCodePoints(go value)
        | ExpressionRecipe.StringToLower value -> ExpressionRecipe.StringToLower(go value)
        | ExpressionRecipe.StringToUpper value -> ExpressionRecipe.StringToUpper(go value)
        | ExpressionRecipe.StringDecodeUtf8Lossy value -> ExpressionRecipe.StringDecodeUtf8Lossy(go value)
        | ExpressionRecipe.Float64RoundToInt64 value -> ExpressionRecipe.Float64RoundToInt64(go value)
        | ExpressionRecipe.Float64FloorToInt64 value -> ExpressionRecipe.Float64FloorToInt64(go value)
        | ExpressionRecipe.Float64Sqrt value -> ExpressionRecipe.Float64Sqrt(go value)
        | ExpressionRecipe.Float64Abs value -> ExpressionRecipe.Float64Abs(go value)
        | ExpressionRecipe.Float64Negate value -> ExpressionRecipe.Float64Negate(go value)
        | ExpressionRecipe.Float64Floor value -> ExpressionRecipe.Float64Floor(go value)
        | ExpressionRecipe.Float64Ceiling value -> ExpressionRecipe.Float64Ceiling(go value)
        | ExpressionRecipe.Float64Truncate value -> ExpressionRecipe.Float64Truncate(go value)
        | ExpressionRecipe.StringUtf8ByteCount value -> ExpressionRecipe.StringUtf8ByteCount(go value)
        | ExpressionRecipe.StringFromCodePoint value -> ExpressionRecipe.StringFromCodePoint(go value)
        | ExpressionRecipe.StringToCodePoint value -> ExpressionRecipe.StringToCodePoint(go value)
        | ExpressionRecipe.StringLength value -> ExpressionRecipe.StringLength(go value)
        | ExpressionRecipe.StringSlice(value, start, stop) -> ExpressionRecipe.StringSlice(go value, go start, go stop)
        | ExpressionRecipe.StringContains(left, right) -> ExpressionRecipe.StringContains(go left, go right)
        | ExpressionRecipe.StringIndexOf(value, substring, from) -> ExpressionRecipe.StringIndexOf(go value, go substring, go from)
        | ExpressionRecipe.StringSpan(value, set, from) -> ExpressionRecipe.StringSpan(go value, go set, go from)
        | ExpressionRecipe.StringSpanBack(value, set, stop) -> ExpressionRecipe.StringSpanBack(go value, go set, go stop)
        | ExpressionRecipe.StringLastIndexOf(left, right) -> ExpressionRecipe.StringLastIndexOf(go left, go right)
        | ExpressionRecipe.StringStartsWith(left, right) -> ExpressionRecipe.StringStartsWith(go left, go right)
        | ExpressionRecipe.StringEndsWith(left, right) -> ExpressionRecipe.StringEndsWith(go left, go right)
        | ExpressionRecipe.StringSplit(left, right) -> ExpressionRecipe.StringSplit(go left, go right)
        | ExpressionRecipe.StringSplitLines(value, breaks) -> ExpressionRecipe.StringSplitLines(go value, go breaks)
        | ExpressionRecipe.StringReplace(value, pattern, replacement) -> ExpressionRecipe.StringReplace(go value, go pattern, go replacement)
        | ExpressionRecipe.StringTrim value -> ExpressionRecipe.StringTrim(go value)
        | ExpressionRecipe.StringTrimStart value -> ExpressionRecipe.StringTrimStart(go value)
        | ExpressionRecipe.StringTrimEnd value -> ExpressionRecipe.StringTrimEnd(go value)
        | ExpressionRecipe.StringJoin(list, separator) -> ExpressionRecipe.StringJoin(go list, go separator)
        | ExpressionRecipe.StringEqual(left, right) -> ExpressionRecipe.StringEqual(go left, go right)
        | ExpressionRecipe.StringLessThan(left, right) -> ExpressionRecipe.StringLessThan(go left, go right)
        | ExpressionRecipe.ValueEqual(left, right) -> ExpressionRecipe.ValueEqual(go left, go right)
        | ExpressionRecipe.StringConcat(left, right) -> ExpressionRecipe.StringConcat(go left, go right)
        | ExpressionRecipe.Int64WrappingAdd(left, right) -> ExpressionRecipe.Int64WrappingAdd(go left, go right)
        | ExpressionRecipe.Int64WrappingSubtract(left, right) -> ExpressionRecipe.Int64WrappingSubtract(go left, go right)
        | ExpressionRecipe.Int64WrappingMultiply(left, right) -> ExpressionRecipe.Int64WrappingMultiply(go left, go right)
        | ExpressionRecipe.Int64FloorDivide(left, right) -> ExpressionRecipe.Int64FloorDivide(go left, go right)
        | ExpressionRecipe.Int64FloorModulo(left, right) -> ExpressionRecipe.Int64FloorModulo(go left, go right)
        | ExpressionRecipe.Int64Min(left, right) -> ExpressionRecipe.Int64Min(go left, go right)
        | ExpressionRecipe.Int64Max(left, right) -> ExpressionRecipe.Int64Max(go left, go right)
        | ExpressionRecipe.Int64CheckedAdd(left, right) -> ExpressionRecipe.Int64CheckedAdd(go left, go right)
        | ExpressionRecipe.Int64CheckedSubtract(left, right) -> ExpressionRecipe.Int64CheckedSubtract(go left, go right)
        | ExpressionRecipe.Int64CheckedMultiply(left, right) -> ExpressionRecipe.Int64CheckedMultiply(go left, go right)
        | ExpressionRecipe.ListRange(left, right) -> ExpressionRecipe.ListRange(go left, go right)
        | ExpressionRecipe.StringRepeat(left, right) -> ExpressionRecipe.StringRepeat(go left, go right)
        | ExpressionRecipe.Float64Round(left, right) -> ExpressionRecipe.Float64Round(go left, go right)
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
        | ExpressionRecipe.Int64WrappingAbs value -> ExpressionRecipe.Int64WrappingAbs(go value)
        | ExpressionRecipe.Bits32FromInt64 value -> ExpressionRecipe.Bits32FromInt64(go value)
        | ExpressionRecipe.Bits32ToInt64 value -> ExpressionRecipe.Bits32ToInt64(go value)
        | ExpressionRecipe.Bits32Not value -> ExpressionRecipe.Bits32Not(go value)
        | ExpressionRecipe.Bits32Xor(left, right) -> ExpressionRecipe.Bits32Xor(go left, go right)
        | ExpressionRecipe.Bits32And(left, right) -> ExpressionRecipe.Bits32And(go left, go right)
        | ExpressionRecipe.Bits32Or(left, right) -> ExpressionRecipe.Bits32Or(go left, go right)
        | ExpressionRecipe.Bits32Add(left, right) -> ExpressionRecipe.Bits32Add(go left, go right)
        | ExpressionRecipe.Bits32RotateRight(left, right) -> ExpressionRecipe.Bits32RotateRight(go left, go right)
        | ExpressionRecipe.Bits32ShiftRight(left, right) -> ExpressionRecipe.Bits32ShiftRight(go left, go right)
        | ExpressionRecipe.Bits32ShiftLeft(left, right) -> ExpressionRecipe.Bits32ShiftLeft(go left, go right)
        | ExpressionRecipe.ListEmpty itemType -> ExpressionRecipe.ListEmpty(mapType itemType)
        | ExpressionRecipe.SetEmpty itemType -> ExpressionRecipe.SetEmpty(mapType itemType)
        | ExpressionRecipe.MapEmpty(keyType, valueType) -> ExpressionRecipe.MapEmpty(mapType keyType, mapType valueType)
        | ExpressionRecipe.ListConstruct(itemType, items) -> ExpressionRecipe.ListConstruct(mapType itemType, List.map go items)
        | ExpressionRecipe.ListLength value -> ExpressionRecipe.ListLength(go value)
        | ExpressionRecipe.MapValues value -> ExpressionRecipe.MapValues(go value)
        | ExpressionRecipe.MapKeys value -> ExpressionRecipe.MapKeys(go value)
        | ExpressionRecipe.SetToList value -> ExpressionRecipe.SetToList(go value)
        | ExpressionRecipe.MapSize value -> ExpressionRecipe.MapSize(go value)
        | ExpressionRecipe.SetFromList value -> ExpressionRecipe.SetFromList(go value)
        | ExpressionRecipe.SetSize value -> ExpressionRecipe.SetSize(go value)
        | ExpressionRecipe.ListReverse value -> ExpressionRecipe.ListReverse(go value)
        | ExpressionRecipe.ListLast value -> ExpressionRecipe.ListLast(go value)
        | ExpressionRecipe.ListDropLast value -> ExpressionRecipe.ListDropLast(go value)
        | ExpressionRecipe.ListAppendItem(list, item) -> ExpressionRecipe.ListAppendItem(go list, go item)
        | ExpressionRecipe.ListItemAt(list, index) -> ExpressionRecipe.ListItemAt(go list, go index)
        | ExpressionRecipe.MapRemove(map, key) -> ExpressionRecipe.MapRemove(go map, go key)
        | ExpressionRecipe.SetInsert(map, key) -> ExpressionRecipe.SetInsert(go map, go key)
        | ExpressionRecipe.SetRemove(map, key) -> ExpressionRecipe.SetRemove(go map, go key)
        | ExpressionRecipe.MapContainsKey(map, key) -> ExpressionRecipe.MapContainsKey(go map, go key)
        | ExpressionRecipe.SetContains(map, key) -> ExpressionRecipe.SetContains(go map, go key)
        | ExpressionRecipe.ListIndexOf(list, item) -> ExpressionRecipe.ListIndexOf(go list, go item)
        | ExpressionRecipe.ListLastIndexOf(list, item) -> ExpressionRecipe.ListLastIndexOf(go list, go item)
        | ExpressionRecipe.MapGet(map, key) -> ExpressionRecipe.MapGet(go map, go key)
        | ExpressionRecipe.MapGetOrDefault(map, key, defaultValue) -> ExpressionRecipe.MapGetOrDefault(go map, go key, go defaultValue)
        | ExpressionRecipe.ListSlice(list, start, stop) -> ExpressionRecipe.ListSlice(go list, go start, go stop)
        | ExpressionRecipe.MapInsert(map, key, value) -> ExpressionRecipe.MapInsert(go map, go key, go value)
        | ExpressionRecipe.ListConcat(left, right) -> ExpressionRecipe.ListConcat(go left, go right)
        | ExpressionRecipe.SetUnion(left, right) -> ExpressionRecipe.SetUnion(go left, go right)
        | ExpressionRecipe.SetIntersection(left, right) -> ExpressionRecipe.SetIntersection(go left, go right)
        | ExpressionRecipe.SetDifference(left, right) -> ExpressionRecipe.SetDifference(go left, go right)
        | ExpressionRecipe.ListFold(source, seed, folder, context) -> ExpressionRecipe.ListFold(go source, go seed, folder, pairs context)
        | ExpressionRecipe.MapFold(source, seed, folder, context) -> ExpressionRecipe.MapFold(go source, go seed, folder, pairs context)
        | ExpressionRecipe.UseResource(acquire, capability, operation, body, context) ->
            ExpressionRecipe.UseResource(go acquire, go capability, operation, body, pairs context)
        | ExpressionRecipe.ParallelAll(source, worker, context) -> ExpressionRecipe.ParallelAll(go source, worker, pairs context)
        | ExpressionRecipe.CancellationRequested -> recipe
        | ExpressionRecipe.ExceptionCatch(body, bodyContext, handler, handlerContext) -> ExpressionRecipe.ExceptionCatch(body, pairs bodyContext, handler, pairs handlerContext)
        | ExpressionRecipe.ExceptionIsCancellation value -> ExpressionRecipe.ExceptionIsCancellation(go value)
        | ExpressionRecipe.ExceptionMessage value -> ExpressionRecipe.ExceptionMessage(go value)
        | ExpressionRecipe.ExceptionRethrow(value, resultType) -> ExpressionRecipe.ExceptionRethrow(go value, mapType resultType)
        | ExpressionRecipe.CancellationShield(body, context) -> ExpressionRecipe.CancellationShield(body, pairs context)
        | ExpressionRecipe.TaskScope(body, context) -> ExpressionRecipe.TaskScope(body, pairs context)
        | ExpressionRecipe.TaskStart(scope, worker, context, item) -> ExpressionRecipe.TaskStart(go scope, worker, pairs context, go item)
        | ExpressionRecipe.OneShotCreate(scope, valueType) -> ExpressionRecipe.OneShotCreate(go scope, mapType valueType)
        | ExpressionRecipe.OneShotComplete(cell, value) -> ExpressionRecipe.OneShotComplete(go cell, go value)
        | ExpressionRecipe.OneShotWait(cell, limit) -> ExpressionRecipe.OneShotWait(go cell, Option.map go limit)
        | ExpressionRecipe.TaskWait(tasks, mode, limit) -> ExpressionRecipe.TaskWait(go tasks, mode, Option.map go limit)
        | ExpressionRecipe.TaskCancel task -> ExpressionRecipe.TaskCancel(go task)
        | ExpressionRecipe.ListEffectFold(source, seed, folder, context) -> ExpressionRecipe.ListEffectFold(go source, go seed, folder, pairs context)
        | ExpressionRecipe.ListMap(source, callback, context) -> ExpressionRecipe.ListMap(go source, callback, pairs context)
        | ExpressionRecipe.MapFromList(source, callback, context) -> ExpressionRecipe.MapFromList(go source, callback, pairs context)
        | ExpressionRecipe.ListFilter(source, callback, context) -> ExpressionRecipe.ListFilter(go source, callback, pairs context)
        | ExpressionRecipe.MapFilter(source, callback, context) -> ExpressionRecipe.MapFilter(go source, callback, pairs context)
        | ExpressionRecipe.ListSortBy(source, callback, context) -> ExpressionRecipe.ListSortBy(go source, callback, pairs context)
        | ExpressionRecipe.MapMapValues(source, callback, context) -> ExpressionRecipe.MapMapValues(go source, callback, pairs context)
        | ExpressionRecipe.ListMinBy(source, callback, context) -> ExpressionRecipe.ListMinBy(go source, callback, pairs context)
        | ExpressionRecipe.ListAny(source, callback, context) -> ExpressionRecipe.ListAny(go source, callback, pairs context)
        | ExpressionRecipe.ListFindIndex(source, callback, context) -> ExpressionRecipe.ListFindIndex(go source, callback, pairs context)
        | ExpressionRecipe.ListAll(source, callback, context) -> ExpressionRecipe.ListAll(go source, callback, pairs context)
        | ExpressionRecipe.ProductConstruct(typeId, fields) -> ExpressionRecipe.ProductConstruct(typeId, pairs fields)
        | ExpressionRecipe.FieldAccess(source, fieldId) -> ExpressionRecipe.FieldAccess(go source, fieldId)
        | ExpressionRecipe.ProductUpdate(typeId, source, fields) -> ExpressionRecipe.ProductUpdate(typeId, go source, pairs fields)
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

    /// What opened a lexical frame; used only to describe visible frames in scope diagnostics.
    [<RequireQualifiedAccess>]
    type private ScopeFrameKind =
        | UnionArm of VariantId list
        | BindBody
        | OptionSome
        | OptionNone
        | ResultOk
        | ResultErr

    type private ScopeFrame<'Binding> =
        { Kind: ScopeFrameKind
          Slots: (RecipePatternSlot * 'Binding) list }

    type private PatternTypeFrame = ScopeFrame<TypeRef>
    type private PatternBindingFrame = ScopeFrame<PatternBinding>

    type private ParameterSlotContext =
        { Type: TypeRef
          ParameterId: ParameterId option
          DisplayName: string }

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
            |> List.map (fun parameter ->
                { Type = parameter.Type
                  ParameterId = Some parameter.Id
                  DisplayName = parameter.DisplayName })
          Description = $"target function {StableId.describe target.DisplayName target.Id}" }

    /// The owner's parameters in a diagnostic, by slot, display name and (when allocated) ID.
    let private describeOwnerParameters (owner: ExpressionOwnerContext) =
        match owner.SelfParameters with
        | [] -> $"{owner.Description} has no parameters."
        | parameters ->
            let entries =
                parameters
                |> List.mapi (fun slot parameter ->
                    match parameter.ParameterId with
                    | Some parameterId -> $"{slot} = {StableId.describe parameter.DisplayName parameterId}"
                    | None -> $"{slot} = '{parameter.DisplayName}'")
                |> String.concat ", "
            $"Parameters of {owner.Description}: {entries}."

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
    let private bits32Type = TypeRef.Primitive PrimitiveType.Bits32
    let private listType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private optionType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
    let private resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])

    /// The first key that occurs more than once in `items`, if any.
    let private tryFindDuplicate keyOf items =
        items |> List.countBy keyOf |> List.tryFind (fun (_, count) -> count > 1) |> Option.map fst

    /// A recipe MatchUnion's arms resolved against the union's variants, in declaration order of each
    /// arm's first variant: the variants each arm covers (an `otherwise` arm expanded), its body, and
    /// whether its body sees the variant's fields (only an explicit arm naming exactly one variant).
    let private resolveUnionArms (variants: SemanticVariant list) (arms: (VariantId list * ExpressionRecipe) list) =
        let explicitArms, otherwiseArms = arms |> List.partition (fun (variantIds, _) -> not (List.isEmpty variantIds))
        let named = explicitArms |> List.collect fst
        let declared = variants |> List.map (fun variant -> variant.Id) |> Set.ofList
        let remaining = variants |> List.filter (fun variant -> not (List.contains variant.Id named))
        match tryFindDuplicate id named, otherwiseArms with
        | Some variantId, _ ->
            Error(diagnostic "PureExpressionMatchArmDuplicate" $"MatchUnion contains variant '{StableId.value variantId}' more than once.")
        | None, _ when not (Set.isSubset (Set.ofList named) declared) ->
            Error(diagnostic "PureExpressionMatchArmMismatch" "MatchUnion must contain every declared variant exactly once and no other variants.")
        | None, _ :: _ :: _ ->
            Error(diagnostic "PureExpressionMatchOtherwiseDuplicate" "MatchUnion has more than one otherwise arm.")
        | None, [ _ ] when List.isEmpty remaining ->
            Error(diagnostic "PureExpressionMatchOtherwiseUnused" "MatchUnion has an otherwise arm, but every variant is already named by another arm.")
        | None, [] when not (List.isEmpty remaining) ->
            Error(diagnostic "PureExpressionMatchArmMismatch" "MatchUnion must contain every declared variant exactly once and no other variants.")
        | None, otherwiseArm ->
            let variantIndex variantId = variants |> List.findIndex (fun variant -> variant.Id = variantId)
            let covered variantIds = variants |> List.filter (fun variant -> List.contains variant.Id variantIds)
            let explicitResolved =
                explicitArms
                |> List.map (fun (variantIds, body) -> covered variantIds, body, (List.length variantIds = 1))
            let otherwiseResolved = otherwiseArm |> List.map (fun (_, body) -> remaining, body, false)
            Ok(
                explicitResolved @ otherwiseResolved
                |> List.sortBy (fun (armVariants: SemanticVariant list, _, _) -> variantIndex armVariants.Head.Id)
            )

    let private mapOf keyType valueType = TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ])

    let private setOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ])

    let private setItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]) -> Some itemType
        | _ -> None

    let private mapEntryTypes typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) -> Some(keyType, valueType)
        | _ -> None

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
        | ExpressionRecipe.SetEmpty _
        | ExpressionRecipe.MapEmpty _
        | ExpressionRecipe.OptionNone _ -> Seq.empty
        | ExpressionRecipe.StringIsEmpty child
        | ExpressionRecipe.Int64ToString child
        | ExpressionRecipe.Int64ToHexString child
        | ExpressionRecipe.StringParseInt64 child
        | ExpressionRecipe.StringParseInt64Hex child
        | ExpressionRecipe.Float64ToString child
        | ExpressionRecipe.StringParseFloat64 child
        | ExpressionRecipe.StringCodePoints child
        | ExpressionRecipe.StringToLower child
        | ExpressionRecipe.StringToUpper child
        | ExpressionRecipe.StringTrim child
        | ExpressionRecipe.StringTrimStart child
        | ExpressionRecipe.StringTrimEnd child
        | ExpressionRecipe.StringDecodeUtf8Lossy child
        | ExpressionRecipe.Float64RoundToInt64 child
        | ExpressionRecipe.Float64FloorToInt64 child
        | ExpressionRecipe.Float64Sqrt child
        | ExpressionRecipe.Float64Abs child
        | ExpressionRecipe.Float64Negate child
        | ExpressionRecipe.Float64Floor child
        | ExpressionRecipe.Float64Ceiling child
        | ExpressionRecipe.Float64Truncate child
        | ExpressionRecipe.StringUtf8ByteCount child
        | ExpressionRecipe.StringFromCodePoint child
        | ExpressionRecipe.StringToCodePoint child
        | ExpressionRecipe.StringLength child
        | ExpressionRecipe.BoolNot child
        | ExpressionRecipe.Float64IsNaN child
        | ExpressionRecipe.Float64IsFinite child
        | ExpressionRecipe.Float64IsInfinite child
        | ExpressionRecipe.Int64ToFloat64 child
        | ExpressionRecipe.Int64WrappingAbs child
        | ExpressionRecipe.Bits32FromInt64 child
        | ExpressionRecipe.Bits32ToInt64 child
        | ExpressionRecipe.Bits32Not child
        | ExpressionRecipe.ListLength child
        | ExpressionRecipe.MapValues child
        | ExpressionRecipe.MapKeys child
        | ExpressionRecipe.SetToList child
        | ExpressionRecipe.MapSize child
        | ExpressionRecipe.SetFromList child
        | ExpressionRecipe.SetSize child
        | ExpressionRecipe.ListReverse child
        | ExpressionRecipe.ListLast child
        | ExpressionRecipe.ListDropLast child
        | ExpressionRecipe.FieldAccess(child, _)
        | ExpressionRecipe.OptionSome child
        | ExpressionRecipe.ResultOk(_, child)
        | ExpressionRecipe.Abort child
        | ExpressionRecipe.ResultErr(_, child) -> Seq.singleton child
        | ExpressionRecipe.StringSlice(value, start, stop) -> seq { value; start; stop }
        | ExpressionRecipe.StringIndexOf(value, substring, from) -> seq { value; substring; from }
        | ExpressionRecipe.StringReplace(value, pattern, replacement) -> seq { value; pattern; replacement }
        | ExpressionRecipe.StringSpan(value, set, bound)
        | ExpressionRecipe.StringSpanBack(value, set, bound) -> seq { value; set; bound }
        | ExpressionRecipe.ListSlice(list, start, stop) -> seq { list; start; stop }
        | ExpressionRecipe.MapInsert(map, key, value) -> seq { map; key; value }
        | ExpressionRecipe.MapGetOrDefault(map, key, defaultValue) -> seq { map; key; defaultValue }
        | ExpressionRecipe.StringContains(left, right)
        | ExpressionRecipe.StringLastIndexOf(left, right)
        | ExpressionRecipe.StringStartsWith(left, right)
        | ExpressionRecipe.StringEndsWith(left, right)
        | ExpressionRecipe.StringSplit(left, right)
        | ExpressionRecipe.StringSplitLines(left, right)
        | ExpressionRecipe.StringJoin(left, right)
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
        | ExpressionRecipe.Int64Min(left, right)
        | ExpressionRecipe.Int64Max(left, right)
        | ExpressionRecipe.Int64CheckedAdd(left, right)
        | ExpressionRecipe.Int64CheckedSubtract(left, right)
        | ExpressionRecipe.Int64CheckedMultiply(left, right)
        | ExpressionRecipe.ListRange(left, right)
        | ExpressionRecipe.StringRepeat(left, right)
        | ExpressionRecipe.Float64Round(left, right)
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
        | ExpressionRecipe.Bits32Xor(left, right)
        | ExpressionRecipe.Bits32And(left, right)
        | ExpressionRecipe.Bits32Or(left, right)
        | ExpressionRecipe.Bits32Add(left, right)
        | ExpressionRecipe.Bits32RotateRight(left, right)
        | ExpressionRecipe.Bits32ShiftRight(left, right)
        | ExpressionRecipe.Bits32ShiftLeft(left, right)
        | ExpressionRecipe.ListAppendItem(left, right)
        | ExpressionRecipe.ListItemAt(left, right)
        | ExpressionRecipe.MapRemove(left, right)
        | ExpressionRecipe.SetInsert(left, right)
        | ExpressionRecipe.SetRemove(left, right)
        | ExpressionRecipe.MapContainsKey(left, right)
        | ExpressionRecipe.SetContains(left, right)
        | ExpressionRecipe.ListIndexOf(left, right)
        | ExpressionRecipe.ListLastIndexOf(left, right)
        | ExpressionRecipe.MapGet(left, right)
        | ExpressionRecipe.ListConcat(left, right)
        | ExpressionRecipe.SetUnion(left, right)
        | ExpressionRecipe.SetIntersection(left, right)
        | ExpressionRecipe.SetDifference(left, right) -> seq { yield left; yield right }
        | ExpressionRecipe.ListFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.MapFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.ListEffectFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.UseResource(acquire, releaseCapability, _, _, context) ->
            seq { yield acquire; yield releaseCapability; yield! context |> Seq.map snd }
        | ExpressionRecipe.ParallelAll(source, _, context) -> seq { yield source; yield! context |> Seq.map snd }
        | ExpressionRecipe.CancellationRequested -> Seq.empty
        | ExpressionRecipe.ExceptionCatch(_, bodyContext, _, handlerContext) -> (bodyContext @ handlerContext) |> Seq.map snd
        | ExpressionRecipe.ExceptionIsCancellation value
        | ExpressionRecipe.ExceptionMessage value
        | ExpressionRecipe.ExceptionRethrow(value, _) -> Seq.singleton value
        | ExpressionRecipe.CancellationShield(_, context) -> context |> Seq.map snd
        | ExpressionRecipe.TaskScope(_, context) -> context |> Seq.map snd
        | ExpressionRecipe.TaskStart(scope, _, context, item) -> seq { yield scope; yield item; yield! context |> Seq.map snd }
        | ExpressionRecipe.OneShotCreate(scope, _) -> Seq.singleton scope
        | ExpressionRecipe.OneShotComplete(cell, value) -> seq { yield cell; yield value }
        | ExpressionRecipe.OneShotWait(cell, limit) -> seq { yield cell; yield! Option.toList limit }
        | ExpressionRecipe.TaskWait(tasks, _, limit) -> seq { yield tasks; yield! Option.toList limit }
        | ExpressionRecipe.TaskCancel task -> Seq.singleton task
        | ExpressionRecipe.ListMap(source, _, context)
        | ExpressionRecipe.MapFromList(source, _, context)
        | ExpressionRecipe.ListFilter(source, _, context)
        | ExpressionRecipe.MapFilter(source, _, context)
        | ExpressionRecipe.ListSortBy(source, _, context)
        | ExpressionRecipe.MapMapValues(source, _, context)
        | ExpressionRecipe.ListMinBy(source, _, context)
        | ExpressionRecipe.ListAny(source, _, context)
        | ExpressionRecipe.ListFindIndex(source, _, context)
        | ExpressionRecipe.ListAll(source, _, context) -> seq { yield source; yield! context |> Seq.map snd }
        | ExpressionRecipe.ListConstruct(_, items) -> items |> Seq.ofList
        | ExpressionRecipe.ProductConstruct(_, fields)
        | ExpressionRecipe.UnionConstruct(_, fields) -> fields |> Seq.map snd
        | ExpressionRecipe.ProductUpdate(_, source, fields) -> seq { yield source; yield! fields |> Seq.map snd }
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

    /// The wire property that holds a recipe field. Named union fields already use the wire name;
    /// an unnamed single operand is `value`, and unnamed operand pairs are `left`/`right`
    /// (`value`/`substring` for StringContains).
    let private recipeFieldName caseName (fields: System.Reflection.PropertyInfo[]) index =
        let name = fields[index].Name
        if not (name.StartsWith "Item") then name
        elif fields.Length = 1 then "value"
        else
            match caseName, index with
            | "StringContains", 0 -> "value"
            | "StringContains", _ -> "substring"
            | ("StringIndexOf" | "StringLastIndexOf" | "StringStartsWith" | "StringEndsWith" | "StringSplit"), 0 -> "value"
            | ("StringIndexOf" | "StringLastIndexOf"), 1 -> "substring"
            | "StringIndexOf", _ -> "from"
            | "StringStartsWith", _ -> "prefix"
            | "StringEndsWith", _ -> "suffix"
            | "StringSplit", _ -> "separator"
            | _, 0 -> "left"
            | _ -> "right"

    /// The recipe kind in wire spelling, e.g. `optionSome`.
    let private recipeKindName (recipe: ExpressionRecipe) =
        let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(recipe, typeof<ExpressionRecipe>)
        string (System.Char.ToLowerInvariant case.Name[0]) + case.Name.Substring 1

    /// The path of `target` (by reference) inside `root`, in the submitted request's property
    /// names: `body.whenFalse.value`, `body.arguments[1].value`, `replacement.arms[0].body`.
    /// Generic over the recipe cases, so a new case needs no change here.
    let private recipePath (rootLabel: string) (root: ExpressionRecipe) (target: ExpressionRecipe) =
        let rec search path (node: ExpressionRecipe) =
            if obj.ReferenceEquals(node, target) then Some path
            else
                let case, values = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(node, typeof<ExpressionRecipe>)
                let fields = case.GetFields()
                values
                |> Seq.indexed
                |> Seq.tryPick (fun (index, value) ->
                    let name = recipeFieldName case.Name fields index
                    match value with
                    | :? ExpressionRecipe as child -> search $"{path}.{name}" child
                    | :? (ExpressionRecipe option) as child -> child |> Option.bind (search $"{path}.{name}")
                    | :? string -> None
                    | :? System.Collections.IEnumerable as items ->
                        items
                        |> Seq.cast<obj>
                        |> Seq.indexed
                        |> Seq.tryPick (fun (itemIndex, item) ->
                            match item with
                            | :? ExpressionRecipe as child -> search $"{path}.{name}[{itemIndex}]" child
                            | null -> None
                            | _ when Microsoft.FSharp.Reflection.FSharpType.IsTuple(item.GetType()) ->
                                let property = if name = "arms" then "body" else "value"
                                Microsoft.FSharp.Reflection.FSharpValue.GetTupleFields item
                                |> Array.tryPick (function
                                    | :? ExpressionRecipe as child -> search $"{path}.{name}[{itemIndex}].{property}" child
                                    | _ -> None)
                            | _ -> None)
                    | _ -> None)
        search rootLabel root

    /// Appends the recipe path and kind of the node that produced `error`, when known.
    let private locate rootLabel root (failure: (TransactionDiagnostic * ExpressionRecipe) option) (error: TransactionDiagnostic) =
        match rootLabel, failure with
        | Some label, Some(known, node) when known = error ->
            match recipePath label root node with
            | Some path -> { error with Message = $"{error.Message} At {path} ({recipeKindName node})." }
            | None -> error
        | _ -> error

    /// Snapshot lookups built once per simulate or preflight and shared by inference and materialization.
    type private SnapshotIndex =
        { Snapshot: Snapshot
          FunctionsById: Map<FunctionId, SemanticFunction>
          TypesById: Map<TypeId, SemanticType>
          CapabilityOperationsById: Map<CapabilityOperationId, CapabilityId * SemanticCapabilityOperation>
          VariantsById: Map<VariantId, TypeId * SemanticVariant>
          ProductFieldsById: Map<FieldId, TypeId * SemanticField>
          TypeIds: Set<TypeId>
          CapabilityIds: Set<CapabilityId> }

    let private buildSnapshotIndex (snapshot: Snapshot) =
        { Snapshot = snapshot
          FunctionsById = snapshot.Functions |> List.map (fun functionEntity -> functionEntity.Id, functionEntity) |> Map.ofList
          TypesById = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id, typeEntity) |> Map.ofList
          CapabilityOperationsById =
            snapshot.Capabilities
            |> List.collect (fun capability ->
                capability.Operations |> List.map (fun operation -> operation.Id, (capability.Id, operation)))
            |> Map.ofList
          VariantsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product _ -> []
                | SemanticTypeKind.Union variants -> variants |> List.map (fun variant -> variant.Id, (typeEntity.Id, variant)))
            |> Map.ofList
          ProductFieldsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.Id, (typeEntity.Id, field))
                | SemanticTypeKind.Union _ -> [])
            |> Map.ofList
          TypeIds = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id) |> Set.ofList
          CapabilityIds = snapshot.Capabilities |> List.map (fun capability -> capability.Id) |> Set.ofList }

    let private validateTypeRef (index: SnapshotIndex) typeRef =
        let invalid message = diagnostic "PureExpressionInvalidTypeRef" message

        let rec validate typeRef =
            match typeRef with
            | TypeRef.Primitive _
            // Scope (a parameter type must declare it) is checked on the resulting snapshot.
            | TypeRef.TypeParameter _ -> Ok()
            | TypeRef.Named typeId when Set.contains typeId index.TypeIds -> Ok()
            | TypeRef.Named typeId -> Error(invalid $"Type reference names unknown type '{StableId.value typeId}'.")
            | TypeRef.Capability capabilityId when Set.contains capabilityId index.CapabilityIds -> Ok()
            | TypeRef.Capability capabilityId ->
                Error(invalid $"Type reference names unknown capability '{StableId.value capabilityId}'.")
            | TypeRef.Function(arguments, result) ->
                (arguments @ [ result ]) |> List.fold (fun state arg -> state |> Result.bind (fun () -> validate arg)) (Ok())
            | TypeRef.BuiltinGeneric(kind, arguments) ->
                let expectedArity =
                    match kind with
                    | BuiltinGenericType.Exception -> 0
                    | BuiltinGenericType.TaskScope -> 0
                    | BuiltinGenericType.Option
                    | BuiltinGenericType.List
                    | BuiltinGenericType.Set
                    | BuiltinGenericType.OneShot
                    | BuiltinGenericType.Task -> 1
                    | BuiltinGenericType.Result
                    | BuiltinGenericType.Map -> 2

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

    /// A declared field by ID with a description of its owning product or variant.
    let private tryFindFieldWithOwner (snapshot: Snapshot) fieldId =
        snapshot.Types
        |> List.tryPick (fun typeEntity ->
            match typeEntity.Kind with
            | SemanticTypeKind.Product fields ->
                fields
                |> List.tryFind (fun field -> field.Id = fieldId)
                |> Option.map (fun field -> field, $"product '{typeEntity.DisplayName}'")
            | SemanticTypeKind.Union variants ->
                variants
                |> List.tryPick (fun variant ->
                    variant.Fields
                    |> List.tryFind (fun field -> field.Id = fieldId)
                    |> Option.map (fun field -> field, $"variant '{variant.DisplayName}' of '{typeEntity.DisplayName}'")))

    /// A pattern slot in a diagnostic: a field by display name and ID with its owning variant/type
    /// (the ID alone when no declaration has it), or `Value`/`Error`.
    let private describePatternSlot (snapshot: Snapshot) slot =
        match slot with
        | RecipePatternSlot.Value -> "Value"
        | RecipePatternSlot.Error -> "Error"
        | RecipePatternSlot.Field fieldId ->
            tryFindFieldWithOwner snapshot fieldId
            |> Option.map (fun (field, owner) -> $"field {StableId.describe field.DisplayName fieldId} of {owner}")
            |> Option.defaultValue $"field '{StableId.value fieldId}' (not a declared field)"

    let private shortSlotName (snapshot: Snapshot) slot =
        match slot with
        | RecipePatternSlot.Value -> "Value"
        | RecipePatternSlot.Error -> "Error"
        | RecipePatternSlot.Field fieldId ->
            tryFindFieldWithOwner snapshot fieldId
            |> Option.map (fun (field, _) -> $"'{field.DisplayName}'")
            |> Option.defaultValue $"'{StableId.value fieldId}'"

    /// One visible frame in a diagnostic, e.g. `arm 'Stop' of MatchUnion on 'Action' (binds 'member')`.
    let private describeScopeFrame (snapshot: Snapshot) (frame: ScopeFrame<'Binding>) =
        let opener =
            match frame.Kind with
            | ScopeFrameKind.UnionArm variantIds ->
                snapshot.Types
                |> List.tryPick (fun typeEntity ->
                    match typeEntity.Kind with
                    | SemanticTypeKind.Union variants when variants |> List.exists (fun variant -> List.contains variant.Id variantIds) ->
                        let names =
                            variants
                            |> List.filter (fun variant -> List.contains variant.Id variantIds)
                            |> List.map (fun variant -> variant.DisplayName)
                            |> String.concat "|"
                        Some $"arm '{names}' of MatchUnion on '{typeEntity.DisplayName}'"
                    | SemanticTypeKind.Union _
                    | SemanticTypeKind.Product _ -> None)
                |> Option.defaultValue ("arm '" + (variantIds |> List.map StableId.value |> String.concat "|") + "' of MatchUnion")
            | ScopeFrameKind.BindBody -> "Bind body"
            | ScopeFrameKind.OptionSome -> "Some arm of MatchOption"
            | ScopeFrameKind.OptionNone -> "None arm of MatchOption"
            | ScopeFrameKind.ResultOk -> "Ok arm of MatchResult"
            | ScopeFrameKind.ResultErr -> "Err arm of MatchResult"
        let bindings =
            match frame.Slots with
            | [] -> "no bindings"
            | slots -> "binds " + (slots |> List.map (fst >> shortSlotName snapshot) |> String.concat ", ")
        $"{opener} ({bindings})"

    /// The frames visible at a pattern reference, innermost first, and the depths where the slot is bound.
    let private describeVisibleFrames (snapshot: Snapshot) (frames: ScopeFrame<'Binding> list) slot =
        let listing =
            match frames with
            | [] -> "No frames are visible here; only a Bind body or a match arm opens one."
            | _ ->
                let entries =
                    frames
                    |> List.mapi (fun depth frame -> $"depth {depth} = {describeScopeFrame snapshot frame}")
                    |> String.concat "; "
                $"Visible frames, innermost first: {entries}."
        let foundAt =
            frames
            |> List.indexed
            |> List.choose (fun (depth, frame) ->
                if frame.Slots |> List.exists (fun (candidate, _) -> candidate = slot) then Some depth else None)
        match foundAt with
        | [] -> listing
        | [ depth ] -> $"The slot is found at depth {depth}. {listing}"
        | depths ->
            let text = depths |> List.map string |> String.concat ", "
            $"The slot is found at depths {text}; choose the frame that binds the intended value. Empty frames still count toward scope depth. {listing}"

    let private resolvePatternType (snapshot: Snapshot) (frames: PatternTypeFrame list) (reference: RecipePatternReference) =
        let slotText () = describePatternSlot snapshot reference.Slot
        if reference.ScopeDepth < 0 then
            Error(
                diagnostic
                    "PureExpressionPatternScopeDepthInvalid"
                    $"Pattern-reference scope depth must be non-negative (slot {slotText ()}). {describeVisibleFrames snapshot frames reference.Slot}"
            )
        else
            match List.tryItem reference.ScopeDepth frames with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionPatternScopeNotFound"
                        $"Pattern-reference scope depth {reference.ScopeDepth} for slot {slotText ()} is not visible here. {describeVisibleFrames snapshot frames reference.Slot}"
                )
            | Some frame ->
                match frame.Slots |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
                | Some(_, typeRef) -> Ok typeRef
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionPatternSlotNotFound"
                            $"Pattern-reference slot {slotText ()} does not exist in scope depth {reference.ScopeDepth}. {describeVisibleFrames snapshot frames reference.Slot}"
                    )

    /// Successful inference results keyed by recipe identity and scope, so materialization, which
    /// recovers the input type of every match and bind, infers each subtree once. Valid only
    /// for one snapshot index and owner.
    type private InferenceCache() =
        let find (table: System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * 'T) list>) frames recipe =
            match table.TryGetValue recipe with
            | true, entries -> entries |> List.tryPick (fun (known, value) -> if known = frames then Some value else None)
            | false, _ -> None
        let add (table: System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * 'T) list>) frames recipe value =
            let entries =
                match table.TryGetValue recipe with
                | true, entries -> entries
                | false, _ -> []
            table[recipe] <- (frames, value) :: entries
        let values = System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * TypeRef) list>(HashIdentity.Reference)
        let completions =
            System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * GraphCompletion) list>(HashIdentity.Reference)
        member _.TryValue(frames, recipe) = find values frames recipe
        member _.AddValue(frames, recipe, value) = add values frames recipe value
        member _.TryCompletion(frames, recipe) = find completions frames recipe
        member _.AddCompletion(frames, recipe, value) = add completions frames recipe value

    /// The `OperationSignatures` entry of each `ExpressionRecipe` case, indexed by union tag.
    let private fixedSignatureEntries =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<ExpressionRecipe>)
        |> Array.map (fun case -> OperationSignatures.tryFind (string (System.Char.ToLowerInvariant case.Name[0]) + case.Name.Substring 1))

    let private readRecipeTag =
        Microsoft.FSharp.Reflection.FSharpValue.PreComputeUnionTagReader(typeof<ExpressionRecipe>)

    let private fixedSignatureEntry (recipe: ExpressionRecipe) =
        match fixedSignatureEntries[readRecipeTag recipe] with
        | Some entry -> entry
        | None -> invalidOp $"Recipe kind '{recipeKindName recipe}' is inferred from OperationSignatures but has no entry there."

    let private inferTypeWithContext
        (index: SnapshotIndex)
        (owner: ExpressionOwnerContext)
        (cache: InferenceCache)
        (initialFrames: PatternTypeFrame list)
        (rootLabel: string option)
        recipe
        =
        let snapshot = index.Snapshot
        let functionsById = index.FunctionsById
        let typesById = index.TypesById
        let capabilityOperationsById = index.CapabilityOperationsById
        let variantsById = index.VariantsById
        let productFieldsById = index.ProductFieldsById
        let oneShotPayloadRequirements = lazy (OneShotPayloadRequirements.derive snapshot)

        let ty = DiagnosticText.typeRef snapshot
        let tyPair = DiagnosticText.typeRefPair snapshot
        let callbackName = DiagnosticText.functionName
        let callbackSignature = DiagnosticText.signature snapshot

        let typeMismatch role side expected actual =
            let expected, produced = tyPair expected actual
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires {expected}, but the recipe produces {produced}."

        let listMismatch role side actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires List<T>, but the recipe produces {ty actual}."

        let mapMismatch role side actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires Map<K, V>, but the recipe produces {ty actual}."

        let setItemNotEquatable itemType =
            diagnostic
                "SetItemTypeNotEquatable"
                $"Set item type is not equatable: {EquatableTypes.explain snapshot.Types itemType}"

        let mapKeyNotEquatable keyType =
            diagnostic
                "MapKeyTypeNotEquatable"
                $"Map key type is not equatable: {EquatableTypes.explain snapshot.Types keyType}"

        /// The first of `effects` that the owner does not declare.
        let missingEffect effects =
            effects |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects))

        let missingEffectError subject effectId =
            diagnostic
                "ExpressionCallMissingEffectRequirement"
                $"{subject} requires effect {effectLabel snapshot effectId} not declared by {owner.Description}."

        /// Types context bindings with `inferValue` and returns the callback's unbound parameters with
        /// the type arguments that the context values determine for a generic callback.
        let validateCallbackContext role (callback: SemanticFunction) (context: (ParameterId * ExpressionRecipe) list) inferValue =
            let parametersById = callback.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList
            match tryFindDuplicate fst context with
            | Some parameterId ->
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
                                    $"{role} context binds '{StableId.value parameterId}', which is not a parameter of callback {callbackName callback}: {callbackSignature callback}."
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
                                            "PureExpressionCallbackContextTypeMismatch"
                                            $"{role} context value for parameter {StableId.describe parameter.DisplayName parameterId} of callback {callbackName callback} has type {ty actual}, but the parameter expects {ty parameter.Type}."
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
                        "PureExpressionCallbackNotFound"
                        $"{role} callback function '{StableId.value callbackFunctionId}' does not exist."
                )
            | Some callback ->
                match validateCallbackContext role callback context inferValue with
                | Error error -> Error error
                | Ok(unbound, _) when List.length unbound <> 1 ->
                    Error(
                        diagnostic
                            "PureExpressionCallbackArityMismatch"
                            $"{role} callback function {callbackName callback} must have exactly one parameter not bound by context; it has type {callbackSignature callback} and context binds {List.length context}."
                    )
                | Ok(unbound, substitution) ->
                    match TypeParameters.matchType substitution unbound.Head.Type sourceItemType with
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackInputTypeMismatch"
                                $"{role} callback function {callbackName callback} input type does not match the source list item type {ty sourceItemType}; the callback has type {callbackSignature callback}."
                        )
                    | Some substitution when predicateResultRequired && TypeParameters.substitute substitution callback.ResultType <> boolType ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackResultTypeMismatch"
                                $"{role} predicate {callbackName callback} must return Bool; it has type {callbackSignature callback}."
                        )
                    | Some _ when not (PureFunctions.isPure snapshot callback.Id) ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackEffectsNotAllowed"
                                $"{role} callback function {callbackName callback} must be pure, including its transitive calls and ambient task or cancellation behavior."
                        )
                    | Some substitution -> Ok(callback, substitution)

        /// The ListSortBy and ListMinBy callback: exactly two parameters not bound by context, both of the item type
        /// (left, right), returning Bool, with no required effects.
        let validateLessThanCallback role callbackFunctionId context sourceItemType inferValue =
            match Map.tryFind callbackFunctionId functionsById with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionCallbackNotFound"
                        $"{role} callback function '{StableId.value callbackFunctionId}' does not exist."
                )
            | Some callback ->
                match validateCallbackContext role callback context inferValue with
                | Error error -> Error error
                | Ok(unbound, _) when List.length unbound <> 2 ->
                    Error(
                        diagnostic
                            "PureExpressionCallbackArityMismatch"
                            $"{role} callback function {callbackName callback} must have exactly two parameters not bound by context (left, right); it has type {callbackSignature callback} and context binds {List.length context}."
                    )
                | Ok(unbound, substitution) ->
                    match
                        TypeParameters.matchType substitution unbound.Head.Type sourceItemType
                        |> Option.bind (fun current -> TypeParameters.matchType current unbound.Tail.Head.Type sourceItemType)
                    with
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackInputTypeMismatch"
                                $"{role} callback function {callbackName callback} input types must both match the source list item type {ty sourceItemType}; the callback has type {callbackSignature callback}."
                        )
                    | Some substitution when TypeParameters.substitute substitution callback.ResultType <> boolType ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackResultTypeMismatch"
                                $"{role} less-than callback {callbackName callback} must return Bool; it has type {callbackSignature callback}."
                        )
                    | Some _ when not (PureFunctions.isPure snapshot callback.Id) ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackEffectsNotAllowed"
                                $"{role} callback function {callbackName callback} must be pure, including its transitive calls and ambient task or cancellation behavior."
                        )
                    | Some substitution -> Ok(callback, substitution)

        // The innermost node whose own check produced the propagating diagnostic, to locate it.
        let failure: (TransactionDiagnostic * ExpressionRecipe) option ref = ref None
        let track recipe result =
            match result with
            | Error error ->
                match failure.Value with
                | Some(known, _) when known = error -> ()
                | _ -> failure.Value <- Some(error, recipe)
            | Ok _ -> ()
            result

        let rec infer frames recipe =
            match cache.TryValue(frames, recipe) with
            | Some value -> Ok value
            | None ->
                let result = track recipe (inferNode frames recipe)
                result |> Result.iter (fun value -> cache.AddValue(frames, recipe, value))
                result
        and inferCompletion frames recipe =
            match cache.TryCompletion(frames, recipe) with
            | Some completion -> Ok completion
            | None ->
                let result = track recipe (inferCompletionNode frames recipe)
                result |> Result.iter (fun completion -> cache.AddCompletion(frames, recipe, completion))
                result
        and inferNode frames recipe =
            // A fixed-signature operation, checked from its `OperationSignatures` entry.
            let fixedSignature () =
                let entry = fixedSignatureEntry recipe
                let role = string (System.Char.ToUpperInvariant entry.Kind[0]) + entry.Kind.Substring 1
                let fail failure =
                    match failure with
                    | OperationSignatures.OperandFailure.TypeMismatch(side, expected, actual) -> typeMismatch role side expected actual
                    | OperationSignatures.OperandFailure.ShapeMismatch(side, expected, actual) ->
                        diagnostic
                            "PureExpressionOperandTypeMismatch"
                            $"{role} {side} operand requires {expected}, but the recipe produces {ty actual}."
                    | OperationSignatures.OperandFailure.NotEquatable(constraintEntry, itemType) ->
                        diagnostic constraintEntry.Code (constraintEntry.Message + EquatableTypes.explain snapshot.Types itemType)
                OperationSignatures.check
                    (infer frames)
                    (EquatableTypes.isEquatable snapshot.Types)
                    fail
                    entry
                    (recipeChildren recipe |> List.ofSeq)

            let validateFieldBindings role (expectedFields: SemanticField list) (bindings: (FieldId * ExpressionRecipe) list) =
                match tryFindDuplicate fst bindings with
                | Some fieldId ->
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
                                            (let expected, produced = tyPair field.Type actual in
                                             $"{role} field {StableId.describe field.DisplayName field.Id} requires {expected}, but the recipe produces {produced}.")
                                    )
                                | Ok _ -> validateFields rest
                        validateFields expectedFields

            let validateCallArguments (callee: SemanticFunction) (bindings: (ParameterId * ExpressionRecipe) list) =
                match tryFindDuplicate fst bindings with
                | Some parameterId ->
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
                                $"DirectCall targets parameter '{StableId.value parameterId}' not owned by callee {callbackName callee}: {callbackSignature callee}."
                        )
                    | None ->
                        let actualIds = bindings |> List.map fst |> Set.ofList
                        match callee.Parameters |> List.tryFind (fun parameter -> not (Set.contains parameter.Id actualIds)) with
                        | Some parameter ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentMissing"
                                    $"DirectCall is missing required parameter {StableId.describe parameter.DisplayName parameter.Id} for callee {callbackName callee}: {callbackSignature callee}."
                            )
                        | None ->
                            // A call to the owner itself is a self call: it takes the owner's exact
                            // parameter types, like SelfCall, so a generic owner never calls itself at
                            // other type arguments (there is no polymorphic recursion, and a tail self
                            // call runs as a jump that reuses the parameter locals).
                            let isSelfCall = owner.SelfFunctionId = Some callee.Id
                            // Any other generic callee's type arguments follow from matching each parameter type.
                            let rec validateParameters substitution = function
                                | [] -> Ok substitution
                                | (parameter: SemanticParameter) :: rest ->
                                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                                    match infer frames value with
                                    | Error error -> Error error
                                    | Ok actual when isSelfCall ->
                                        if actual = parameter.Type then validateParameters substitution rest
                                        else
                                            let expected, produced = tyPair parameter.Type actual
                                            Error(
                                                diagnostic
                                                    "PureExpressionCallArgumentTypeMismatch"
                                                    $"DirectCall parameter {StableId.describe parameter.DisplayName parameter.Id} of {owner.Description} requires {expected}, but the recipe produces {produced}. A function calls itself at its own parameter types; there is no polymorphic recursion."
                                            )
                                    | Ok actual ->
                                        match TypeParameters.matchType substitution parameter.Type actual with
                                        | Some extended -> validateParameters extended rest
                                        | None ->
                                            let expected, produced = tyPair (TypeParameters.substitute substitution parameter.Type) actual
                                            Error(
                                                diagnostic
                                                    "PureExpressionCallArgumentTypeMismatch"
                                                    $"DirectCall parameter {StableId.describe parameter.DisplayName parameter.Id} of callee {callbackName callee} requires {expected}, but the recipe produces {produced}."
                                            )
                            validateParameters Map.empty callee.Parameters

            let validateCapabilityArguments
                (operation: SemanticCapabilityOperation)
                (bindings: (CapabilityParameterId * ExpressionRecipe) list)
                =
                match tryFindDuplicate fst bindings with
                | Some parameterId ->
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
                                            (let expected, produced = tyPair parameter.Type actual in
                                             $"CapabilityInvoke parameter {StableId.describe parameter.DisplayName parameter.Id} of operation {StableId.describe operation.DisplayName operation.Id} requires {expected}, but the recipe produces {produced}.")
                                    )
                                | Ok _ -> validateParameters rest
                        validateParameters operation.Parameters

            match recipe with
            | ExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId owner.ParametersById with
                | Some parameter -> Ok parameter.Type
                | None ->
                    let foreign =
                        snapshot.Functions
                        |> List.tryPick (fun functionEntity ->
                            functionEntity.Parameters
                            |> List.tryFind (fun parameter -> parameter.Id = parameterId)
                            |> Option.map (fun parameter ->
                                $"Parameter {StableId.describe parameter.DisplayName parameterId} of function {StableId.describe functionEntity.DisplayName functionEntity.Id}"))
                        |> Option.defaultValue $"Parameter '{StableId.value parameterId}'"
                    Error(
                        diagnostic
                            "PureExpressionParameterOutOfScope"
                            $"{foreign} is not owned by {owner.Description}. {describeOwnerParameters owner}"
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
                            $"Parameter slot {index} is outside the requested parameter list. {describeOwnerParameters owner}"
                    )
                | Some slots ->
                    match List.tryItem index slots with
                    | Some slot -> Ok slot.Type
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionParameterSlotOutOfRange"
                                $"Parameter slot {index} is outside the requested parameter list. {describeOwnerParameters owner}"
                        )
            | ExpressionRecipe.PatternReference reference -> resolvePatternType snapshot frames reference
            | ExpressionRecipe.StringLiteral value when isNull value ->
                Error(diagnostic "PureExpressionNullStringLiteral" "StringLiteral requires a non-null value.")
            | ExpressionRecipe.StringLiteral _ -> Ok stringType
            | ExpressionRecipe.BoolLiteral _ -> Ok boolType
            | ExpressionRecipe.UnitLiteral -> Ok unitType
            | ExpressionRecipe.Int64Literal _ -> Ok int64Type
            | ExpressionRecipe.Float64Literal _ -> Ok float64Type
            | ExpressionRecipe.BoolNot _
            | ExpressionRecipe.StringIsEmpty _
            | ExpressionRecipe.StringContains _
            | ExpressionRecipe.StringEqual _
            | ExpressionRecipe.StringLessThan _
            | ExpressionRecipe.StringConcat _
            | ExpressionRecipe.StringLength _
            | ExpressionRecipe.StringSlice _
            | ExpressionRecipe.StringCodePoints _
            | ExpressionRecipe.StringIndexOf _
            | ExpressionRecipe.StringLastIndexOf _
            | ExpressionRecipe.StringSpan _
            | ExpressionRecipe.StringSpanBack _
            | ExpressionRecipe.StringStartsWith _
            | ExpressionRecipe.StringEndsWith _
            | ExpressionRecipe.StringSplit _
            | ExpressionRecipe.StringSplitLines _
            | ExpressionRecipe.StringReplace _
            | ExpressionRecipe.StringTrim _
            | ExpressionRecipe.StringTrimStart _
            | ExpressionRecipe.StringTrimEnd _
            | ExpressionRecipe.StringJoin _
            | ExpressionRecipe.StringToLower _
            | ExpressionRecipe.StringToUpper _
            | ExpressionRecipe.StringUtf8ByteCount _
            | ExpressionRecipe.StringDecodeUtf8Lossy _
            | ExpressionRecipe.StringFromCodePoint _
            | ExpressionRecipe.StringToCodePoint _
            | ExpressionRecipe.Int64ToString _
            | ExpressionRecipe.StringParseInt64 _
            | ExpressionRecipe.Int64ToHexString _
            | ExpressionRecipe.StringParseInt64Hex _
            | ExpressionRecipe.StringRepeat _
            | ExpressionRecipe.Float64ToString _
            | ExpressionRecipe.StringParseFloat64 _
            | ExpressionRecipe.Int64WrappingAdd _
            | ExpressionRecipe.Int64WrappingSubtract _
            | ExpressionRecipe.Int64WrappingMultiply _
            | ExpressionRecipe.Int64FloorDivide _
            | ExpressionRecipe.Int64FloorModulo _
            | ExpressionRecipe.Int64Min _
            | ExpressionRecipe.Int64Max _
            | ExpressionRecipe.Int64CheckedAdd _
            | ExpressionRecipe.Int64CheckedSubtract _
            | ExpressionRecipe.Int64CheckedMultiply _
            | ExpressionRecipe.Int64WrappingAbs _
            | ExpressionRecipe.Int64Equal _
            | ExpressionRecipe.Int64LessThan _
            | ExpressionRecipe.Int64LessThanOrEqual _
            | ExpressionRecipe.Int64GreaterThan _
            | ExpressionRecipe.Int64GreaterThanOrEqual _
            | ExpressionRecipe.Int64ToFloat64 _
            | ExpressionRecipe.Float64Add _
            | ExpressionRecipe.Float64Subtract _
            | ExpressionRecipe.Float64Multiply _
            | ExpressionRecipe.Float64Divide _
            | ExpressionRecipe.Float64Equal _
            | ExpressionRecipe.Float64LessThan _
            | ExpressionRecipe.Float64IsNaN _
            | ExpressionRecipe.Float64IsFinite _
            | ExpressionRecipe.Float64IsInfinite _
            | ExpressionRecipe.Float64Sqrt _
            | ExpressionRecipe.Float64Abs _
            | ExpressionRecipe.Float64Negate _
            | ExpressionRecipe.Float64Floor _
            | ExpressionRecipe.Float64Ceiling _
            | ExpressionRecipe.Float64Truncate _
            | ExpressionRecipe.Float64RoundToInt64 _
            | ExpressionRecipe.Float64Round _
            | ExpressionRecipe.Float64FloorToInt64 _
            | ExpressionRecipe.Bits32FromInt64 _
            | ExpressionRecipe.Bits32ToInt64 _
            | ExpressionRecipe.Bits32Xor _
            | ExpressionRecipe.Bits32And _
            | ExpressionRecipe.Bits32Or _
            | ExpressionRecipe.Bits32Not _
            | ExpressionRecipe.Bits32Add _
            | ExpressionRecipe.Bits32RotateRight _
            | ExpressionRecipe.Bits32ShiftRight _
            | ExpressionRecipe.Bits32ShiftLeft _
            | ExpressionRecipe.ListLength _
            | ExpressionRecipe.ListItemAt _
            | ExpressionRecipe.ListSlice _
            | ExpressionRecipe.ListRange _
            | ExpressionRecipe.ListReverse _
            | ExpressionRecipe.ListLast _
            | ExpressionRecipe.ListDropLast _
            | ExpressionRecipe.ListAppendItem _
            | ExpressionRecipe.ListConcat _
            | ExpressionRecipe.CancellationRequested
            | ExpressionRecipe.ListIndexOf _
            | ExpressionRecipe.ListLastIndexOf _
            | ExpressionRecipe.MapInsert _
            | ExpressionRecipe.MapGet _
            | ExpressionRecipe.MapGetOrDefault _
            | ExpressionRecipe.MapContainsKey _
            | ExpressionRecipe.MapRemove _
            | ExpressionRecipe.MapSize _
            | ExpressionRecipe.MapKeys _
            | ExpressionRecipe.MapValues _
            | ExpressionRecipe.SetFromList _
            | ExpressionRecipe.SetInsert _
            | ExpressionRecipe.SetContains _
            | ExpressionRecipe.SetRemove _
            | ExpressionRecipe.SetUnion _
            | ExpressionRecipe.SetIntersection _
            | ExpressionRecipe.SetDifference _
            | ExpressionRecipe.SetSize _
            | ExpressionRecipe.SetToList _
            | ExpressionRecipe.OptionSome _ -> fixedSignature ()
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
            | ExpressionRecipe.ListEmpty itemType ->
                match validateTypeRef index itemType with
                | Error error -> Error error
                | Ok() -> Ok(listType itemType)
            | ExpressionRecipe.SetEmpty itemType ->
                match validateTypeRef index itemType with
                | Error error -> Error error
                | Ok() when not (EquatableTypes.isEquatable snapshot.Types itemType) -> Error(setItemNotEquatable itemType)
                | Ok() -> Ok(setOf itemType)
            | ExpressionRecipe.MapEmpty(keyType, valueType) ->
                let mapType = mapOf keyType valueType
                match validateTypeRef index keyType |> Result.bind (fun () -> validateTypeRef index valueType) with
                | Error error -> Error error
                | Ok() when not (EquatableTypes.isEquatable snapshot.Types keyType) -> Error(mapKeyNotEquatable keyType)
                | Ok() -> Ok mapType
            | ExpressionRecipe.ListConstruct(itemType, items) ->
                match validateTypeRef index itemType with
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
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ListFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "ListFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"ListFold folder {callbackName folder} must have exactly two parameters not bound by context (accumulator, item); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                    let matched =
                                        TypeParameters.matchType substitution unbound.Head.Type seedType
                                        |> Option.bind (fun current -> TypeParameters.matchType current unbound.Tail.Head.Type itemType)
                                    match matched with
                                    | Some current when TypeParameters.substitute current folder.ResultType = seedType ->
                                        if PureFunctions.isPure snapshot folder.Id then Ok seedType
                                        else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"ListFold folder {callbackName folder} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                    | Some current when
                                        (match TypeParameters.substitute current folder.ResultType with
                                         | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ]) -> okType = seedType
                                         | _ -> false) ->
                                        // Same shape as ListEffectFold's Result mode: point there instead of a pass-through arm.
                                        Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListFold folder {callbackName folder} must return the accumulator type {ty seedType}; the folder has type {callbackSignature folder}. To stop at the first Err, use ListEffectFold with the same seed and folder: it accepts a pure folder returning Result<S, E> and yields Result<S, E>.")
                                    | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListFold folder {callbackName folder} must have type (accumulator, item) -> accumulator, here ({ty seedType}, {ty itemType}) -> {ty seedType}; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.MapFold(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match mapEntryTypes sourceType with
                    | None -> Error(mapMismatch "MapFold" "map" sourceType)
                    | Some(keyType, valueType) ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"MapFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "MapFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 3 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"MapFold folder {callbackName folder} must have exactly three parameters not bound by context (accumulator, key, value); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                    let matched =
                                        TypeParameters.matchType substitution unbound[0].Type seedType
                                        |> Option.bind (fun current -> TypeParameters.matchType current unbound[1].Type keyType)
                                        |> Option.bind (fun current -> TypeParameters.matchType current unbound[2].Type valueType)
                                    match matched with
                                    | Some current when TypeParameters.substitute current folder.ResultType = seedType ->
                                        if PureFunctions.isPure snapshot folder.Id then Ok seedType
                                        else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"MapFold folder {callbackName folder} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                    | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"MapFold folder {callbackName folder} must have type (accumulator, key, value) -> accumulator, here ({ty seedType}, {ty keyType}, {ty valueType}) -> {ty seedType}; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.MapFilter(source, keepFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match mapEntryTypes sourceType with
                    | None -> Error(mapMismatch "MapFilter" "map" sourceType)
                    | Some(keyType, valueType) ->
                        match Map.tryFind keepFunctionId functionsById with
                        | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"MapFilter callback '{StableId.value keepFunctionId}' does not exist.")
                        | Some keep ->
                            match validateCallbackContext "MapFilter" keep context (infer frames) with
                            | Error error -> Error error
                            | Ok(unbound, _) when unbound.Length <> 2 ->
                                Error(diagnostic "PureExpressionCallbackArityMismatch" $"MapFilter callback {callbackName keep} must have exactly two parameters not bound by context (key, value); it has type {callbackSignature keep} and context binds {List.length context}.")
                            | Ok(unbound, substitution) ->
                                let matched =
                                    TypeParameters.matchType substitution unbound[0].Type keyType
                                    |> Option.bind (fun current -> TypeParameters.matchType current unbound[1].Type valueType)
                                match matched with
                                | Some current when TypeParameters.substitute current keep.ResultType = boolType ->
                                    if PureFunctions.isPure snapshot keep.Id then Ok sourceType
                                    else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"MapFilter callback {callbackName keep} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"MapFilter callback {callbackName keep} must have type (key, value) -> Bool, here ({ty keyType}, {ty valueType}) -> Bool; the callback has type {callbackSignature keep}.")
            | ExpressionRecipe.MapMapValues(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match mapEntryTypes sourceType with
                    | None -> Error(mapMismatch "MapMapValues" "map" sourceType)
                    | Some(keyType, valueType) ->
                        match Map.tryFind mapperFunctionId functionsById with
                        | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"MapMapValues callback '{StableId.value mapperFunctionId}' does not exist.")
                        | Some mapper ->
                            match validateCallbackContext "MapMapValues" mapper context (infer frames) with
                            | Error error -> Error error
                            | Ok(unbound, _) when unbound.Length <> 2 ->
                                Error(diagnostic "PureExpressionCallbackArityMismatch" $"MapMapValues callback {callbackName mapper} must have exactly two parameters not bound by context (key, value); it has type {callbackSignature mapper} and context binds {List.length context}.")
                            | Ok(unbound, substitution) ->
                                let matched =
                                    TypeParameters.matchType substitution unbound[0].Type keyType
                                    |> Option.bind (fun current -> TypeParameters.matchType current unbound[1].Type valueType)
                                match matched with
                                | Some current ->
                                    if PureFunctions.isPure snapshot mapper.Id then Ok(mapOf keyType (TypeParameters.substitute current mapper.ResultType))
                                    else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"MapMapValues callback {callbackName mapper} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                | None -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"MapMapValues callback {callbackName mapper} must have type (key, value) -> U, here ({ty keyType}, {ty valueType}) -> U; the callback has type {callbackSignature mapper}.")
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
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ListEffectFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "ListEffectFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"ListEffectFold folder {callbackName folder} must have exactly two parameters not bound by context (accumulator, item); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                  match
                                      TypeParameters.matchType substitution unbound.Head.Type seedType
                                      |> Option.bind (fun current -> TypeParameters.matchType current unbound.Tail.Head.Type itemType)
                                  with
                                  | None -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListEffectFold folder {callbackName folder} must take (accumulator, item), here ({ty seedType}, {ty itemType}); the folder has type {callbackSignature folder}.")
                                  | Some current ->
                                    match missingEffect folder.RequiredEffects with
                                    | Some effectId -> Error(missingEffectError $"ListEffectFold folder {callbackName folder}" effectId)
                                    | None ->
                                        match TypeParameters.substitute current folder.ResultType with
                                        | resultType when resultType = seedType -> Ok seedType
                                        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ]) as resultType when okType = seedType -> Ok resultType
                                        | _ ->
                                            Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListEffectFold folder {callbackName folder} must return the accumulator type S or Result<S, E>, here {ty seedType} or Result<{ty seedType}, E>; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.UseResource(acquire, releaseCapability, releaseOperationId, bodyFunctionId, context) ->
                let fail code message = Error(diagnostic code message)
                match infer frames acquire with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ resourceType; errorType ])) ->
                    match Map.tryFind bodyFunctionId functionsById, Map.tryFind releaseOperationId capabilityOperationsById with
                    | None, _ -> fail "PureExpressionCallbackNotFound" $"UseResource body '{StableId.value bodyFunctionId}' does not exist."
                    | _, None -> fail "ExpressionCapabilityOperationNotFound" $"UseResource references unknown release operation '{StableId.value releaseOperationId}'."
                    | Some body, Some(ownerCapabilityId, operation) ->
                        match validateCallbackContext "UseResource" body context (infer frames), infer frames releaseCapability with
                        | Error error, _
                        | _, Error error -> Error error
                        | Ok(unbound, substitution), Ok receiverType ->
                          match (match unbound with [ resource ] -> TypeParameters.matchType substitution resource.Type resourceType | _ -> None) with
                          | None ->
                            fail "UseResourceBodyMismatch" $"UseResource body {callbackName body} must have exactly one parameter not bound by context, of the acquired resource type {ty resourceType}; the body has type {callbackSignature body}."
                          | Some _ when receiverType <> TypeRef.Capability ownerCapabilityId ->
                            fail "UseResourceReleaseReceiverMismatch" "UseResource release capability value does not own the release operation."
                          | Some bodySubstitution ->
                            let bodyResult = TypeParameters.substitute bodySubstitution body.ResultType
                            let releaseReturnsResult = operation.ResultType = resultType unitType errorType
                            match bodyResult, missingEffect (body.RequiredEffects @ [ operation.EffectId ]) with
                            | _, Some effectId -> Error(missingEffectError "UseResource" effectId)
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; bodyError ]), None when bodyError <> errorType ->
                                fail "UseResourceResultMismatch" $"UseResource body {callbackName body} must return Result<T, E> with the acquisition error type E = {ty errorType}; it returns {ty bodyResult}."
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]), None ->
                                match operation.Parameters with
                                | [ parameter ] when parameter.Type = resourceType && (operation.ResultType = unitType || releaseReturnsResult) ->
                                    Ok bodyResult
                                | _ ->
                                    fail "UseResourceReleaseSignatureMismatch" "The release operation must take exactly the resource and return Unit or Result<Unit, E>."
                            | _ -> fail "UseResourceResultMismatch" $"UseResource body {callbackName body} must return Result<T, E> with the acquisition error type E = {ty errorType}; it returns {ty bodyResult}."
                | Ok actual -> fail "UseResourceAcquireTypeMismatch" $"UseResource acquisition must produce Result<R, E>, not {ty actual}."
            | ExpressionRecipe.ExceptionCatch(bodyId, bodyContext, handlerId, handlerContext) ->
                let callback role id context inputType =
                    match Map.tryFind id functionsById with
                    | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ExceptionCatch {role} function does not exist.")
                    | Some callback ->
                        validateCallbackContext "ExceptionCatch" callback context (infer frames)
                        |> Result.bind (fun (unbound, substitution) ->
                            match (match unbound with [ input ] -> TypeParameters.matchType substitution input.Type inputType | _ -> None), missingEffect callback.RequiredEffects with
                            | None, _ -> Error(diagnostic "ExceptionCatchCallbackMismatch" $"ExceptionCatch {role} must have one unbound input of type {ty inputType}.")
                            | _, Some effectId -> Error(missingEffectError "ExceptionCatch" effectId)
                            | Some current, None -> Ok(TypeParameters.substitute current callback.ResultType))
                match callback "body" bodyId bodyContext (TypeRef.Primitive PrimitiveType.Unit), callback "handler" handlerId handlerContext (TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) with
                | Ok bodyResult, Ok handlerResult when bodyResult = handlerResult ->
                    match bodyResult with
                    | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) -> Ok bodyResult
                    | _ -> Error(diagnostic "ExceptionCatchResultMismatch" "ExceptionCatch callbacks must return the same Result<T, E>.")
                | Ok _, Ok _ -> Error(diagnostic "ExceptionCatchResultMismatch" "ExceptionCatch callbacks must return the same Result<T, E>.")
                | Error error, _ | _, Error error -> Error error
            | ExpressionRecipe.ExceptionIsCancellation value ->
                match infer frames value with
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) -> Ok boolType
                | Ok _ -> Error(diagnostic "ExceptionValueTypeMismatch" "ExceptionIsCancellation requires Exception.")
                | Error error -> Error error
            | ExpressionRecipe.ExceptionMessage value ->
                match infer frames value with
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) -> Ok stringType
                | Ok _ -> Error(diagnostic "ExceptionValueTypeMismatch" "ExceptionMessage requires Exception.")
                | Error error -> Error error
            | ExpressionRecipe.ExceptionRethrow(value, resultType) ->
                match validateTypeRef index resultType, infer frames value with
                | Ok (), Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) -> Ok resultType
                | Ok (), Ok _ -> Error(diagnostic "ExceptionValueTypeMismatch" "ExceptionRethrow requires Exception.")
                | Error error, _ | _, Error error -> Error error
            | ExpressionRecipe.CancellationShield(bodyFunctionId, context) ->
                match Map.tryFind bodyFunctionId functionsById with
                | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"CancellationShield body '{StableId.value bodyFunctionId}' does not exist.")
                | Some body ->
                    match validateCallbackContext "CancellationShield" body context (infer frames) with
                    | Error error -> Error error
                    | Ok(unbound, substitution) ->
                        let scopeType = TypeRef.Primitive PrimitiveType.Unit
                        match (match unbound with [ scope ] -> TypeParameters.matchType substitution scope.Type scopeType | _ -> None),
                              missingEffect body.RequiredEffects with
                        | None, _ -> Error(diagnostic "CancellationShieldBodyMismatch" $"CancellationShield body {callbackName body} must have exactly one parameter not bound by context, of type Unit; the body has type {callbackSignature body}.")
                        | _, Some effectId ->
                            Error(missingEffectError $"CancellationShield body {StableId.describe body.DisplayName body.Id}" effectId)
                        | Some current, None ->
                            match TypeParameters.substitute current body.ResultType with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result -> Ok result
                            | other -> Error(diagnostic "CancellationShieldResultMismatch" $"CancellationShield body {callbackName body} must return Result<T, E>, not {ty other}.")
            | ExpressionRecipe.TaskScope(bodyFunctionId, context) ->
                match Map.tryFind bodyFunctionId functionsById with
                | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"TaskScope body '{StableId.value bodyFunctionId}' does not exist.")
                | Some body ->
                    match validateCallbackContext "TaskScope" body context (infer frames) with
                    | Error error -> Error error
                    | Ok(unbound, substitution) ->
                        let scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
                        match (match unbound with [ scope ] -> TypeParameters.matchType substitution scope.Type scopeType | _ -> None),
                              missingEffect body.RequiredEffects with
                        | None, _ -> Error(diagnostic "TaskScopeBodyMismatch" $"TaskScope body {callbackName body} must have exactly one parameter not bound by context, of type TaskScope; the body has type {callbackSignature body}.")
                        | _, Some effectId ->
                            Error(missingEffectError $"TaskScope body {StableId.describe body.DisplayName body.Id}" effectId)
                        | Some current, None ->
                            match TypeParameters.substitute current body.ResultType with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result -> Ok result
                            | other -> Error(diagnostic "TaskScopeResultMismatch" $"TaskScope body {callbackName body} must return Result<T, E>, not {ty other}.")
            | ExpressionRecipe.TaskStart(scope, workerFunctionId, context, item) ->
                match infer frames scope, infer frames item with
                | Error error, _
                | _, Error error -> Error error
                | Ok scopeType, _ when scopeType <> TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) ->
                    Error(diagnostic "TaskStartScopeTypeMismatch" $"TaskStart scope must be a TaskScope, not {ty scopeType}.")
                | Ok _, Ok itemType ->
                    match Map.tryFind workerFunctionId functionsById with
                    | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"TaskStart worker '{StableId.value workerFunctionId}' does not exist.")
                    | Some worker ->
                        match validateCallbackContext "TaskStart" worker context (infer frames) with
                        | Error error -> Error error
                        | Ok(unbound, substitution) ->
                            let matched = match unbound with [ parameter ] -> TypeParameters.matchType substitution parameter.Type itemType | _ -> None
                            match matched, missingEffect worker.RequiredEffects with
                            | None, _ ->
                                Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"TaskStart worker {callbackName worker} must have exactly one parameter not bound by context, of the item type {ty itemType}; the worker has type {callbackSignature worker}.")
                            | _, Some effectId ->
                                Error(missingEffectError $"TaskStart worker {StableId.describe worker.DisplayName worker.Id}" effectId)
                            | Some current, None ->
                                match TypeParameters.substitute current worker.ResultType with
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result ->
                                    Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ result ]))
                                | other -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"TaskStart worker {callbackName worker} must return Result<U, E>, not {ty other}.")
            | ExpressionRecipe.TaskWait(tasks, _, limit) ->
                let limitType =
                    match limit |> Option.map (infer frames) with
                    | None -> Ok()
                    | Some(Error error) -> Error error
                    | Some(Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))) -> Ok()
                    | Some(Ok actual) -> Error(diagnostic "TaskWaitLimitTypeMismatch" $"TaskWait limit requires Option<Int64>, not {ty actual}.")
                match infer frames tasks, limitType with
                | Error error, _
                | _, Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ result ]) ])), Ok() ->
                    Ok(listType (optionType result))
                | Ok actual, Ok() -> Error(diagnostic "TaskWaitInputTypeMismatch" $"TaskWait requires List<Task<T>>, not {ty actual}.")
            | ExpressionRecipe.OneShotCreate(scope, valueType) ->
                match infer frames scope, validateTypeRef index valueType with
                | Error error, _ | _, Error error -> Error error
                | Ok actual, _ when actual <> TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> Error(diagnostic "OneShotScopeTypeMismatch" "OneShotCreate requires TaskScope.")
                | _, _ when TaskTypes.mentionsScope valueType -> Error(diagnostic "OneShotScopePayloadUnsupported" "A OneShot payload cannot contain TaskScope.")
                | _ -> Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ valueType ]))
            | ExpressionRecipe.OneShotComplete(cell, value) ->
                match infer frames cell, infer frames value with
                | Error error, _ | _, Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ expected ])), Ok actual when expected = actual -> Ok boolType
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ _ ])), _ -> Error(diagnostic "OneShotValueTypeMismatch" "OneShotComplete payload must match the cell's exact type.")
                | _ -> Error(diagnostic "OneShotCellTypeMismatch" "OneShotComplete requires OneShot<T>.")
            | ExpressionRecipe.OneShotWait(cell, limit) ->
                let limitType = match limit |> Option.map (infer frames) with
                                | None | Some(Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))) -> Ok()
                                | Some(Error error) -> Error error
                                | _ -> Error(diagnostic "OneShotLimitTypeMismatch" "OneShotWait requires Option<Int64> limit.")
                match infer frames cell, limitType with
                | Error error, _ | _, Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ valueType ])), Ok() -> Ok(optionType valueType)
                | _ -> Error(diagnostic "OneShotCellTypeMismatch" "OneShotWait requires OneShot<T>.")
            | ExpressionRecipe.TaskCancel task ->
                match infer frames task with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ _ ])) -> Ok unitType
                | Ok actual -> Error(diagnostic "TaskCancelInputTypeMismatch" $"TaskCancel requires Task<T>, not {ty actual}.")
            | ExpressionRecipe.ParallelAll(source, workerFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType, Map.tryFind workerFunctionId functionsById with
                    | None, _ -> Error(listMismatch "ParallelAll" "source" sourceType)
                    | _, None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ParallelAll worker '{StableId.value workerFunctionId}' does not exist.")
                    | Some itemType, Some worker ->
                        match validateCallbackContext "ParallelAll" worker context (infer frames) with
                        | Error error -> Error error
                        | Ok(unbound, substitution) ->
                            let matched = match unbound with [ item ] -> TypeParameters.matchType substitution item.Type itemType | _ -> None
                            match matched, missingEffect worker.RequiredEffects with
                            | None, _ ->
                                Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ParallelAll worker {callbackName worker} must have exactly one parameter not bound by context, of the item type {ty itemType}; the worker has type {callbackSignature worker}.")
                            | _, Some effectId ->
                                Error(missingEffectError $"ParallelAll worker {StableId.describe worker.DisplayName worker.Id}" effectId)
                            | Some current, None ->
                                match TypeParameters.substitute current worker.ResultType with
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                    Ok(resultType (listType okType) errorType)
                                | other -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ParallelAll worker {callbackName worker} must return Result<U, E>, not {ty other}.")
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
            | ExpressionRecipe.MapFromList(source, keyOfFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "MapFromList" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "MapFromList" keyOfFunctionId context sourceItemType false (infer frames) with
                        | Error error -> Error error
                        | Ok(keyOf, substitution) ->
                            let keyType = TypeParameters.substitute substitution keyOf.ResultType
                            if EquatableTypes.isEquatable snapshot.Types keyType then Ok(mapOf keyType sourceItemType)
                            else Error(mapKeyNotEquatable keyType)
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
            | ExpressionRecipe.ListMinBy(source, lessThanFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListMinBy" "source" sourceType)
                    | Some sourceItemType ->
                        match validateLessThanCallback "ListMinBy" lessThanFunctionId context sourceItemType (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok(optionType sourceItemType)
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
            | ExpressionRecipe.ListFindIndex(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFindIndex" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListFindIndex" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok(optionType int64Type)
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
                        Error(diagnostic "PureExpressionFieldAccessTypeMismatch" $"Field access to {StableId.describe field.DisplayName field.Id} requires {DiagnosticText.namedType snapshot ownerTypeId}, but the source recipe produces {DiagnosticText.typeRefWithId snapshot actual}.")
            | ExpressionRecipe.ProductUpdate(typeId, source, fields) ->
                match Map.tryFind typeId typesById with
                | None ->
                    Error(diagnostic "PureExpressionProductTypeNotFound" $"Product update references unknown type '{StableId.value typeId}'.")
                | Some { Kind = SemanticTypeKind.Product declaredFields } ->
                    let declaredIds = declaredFields |> List.map (fun field -> field.Id) |> Set.ofList
                    match fields |> List.tryFind (fun (fieldId, _) -> not (Set.contains fieldId declaredIds)) with
                    | _ when List.isEmpty fields ->
                        Error(diagnostic "PureExpressionEmptyProductUpdate" $"Product update of {DiagnosticText.namedType snapshot typeId} must replace at least one field.")
                    | Some(fieldId, _) ->
                        Error(diagnostic "PureExpressionFieldBindingMismatch" $"Product update of {DiagnosticText.namedType snapshot typeId} binds field '{StableId.value fieldId}', which that type does not declare.")
                    | None ->
                        match infer frames source with
                        | Error error -> Error error
                        | Ok(TypeRef.Named sourceTypeId) when sourceTypeId = typeId ->
                            let boundIds = fields |> List.map fst |> Set.ofList
                            let updatedFields = declaredFields |> List.filter (fun field -> Set.contains field.Id boundIds)
                            validateFieldBindings "ProductUpdate" updatedFields fields |> Result.map (fun () -> TypeRef.Named typeId)
                        | Ok actual ->
                            Error(diagnostic "PureExpressionProductUpdateSourceMismatch" $"Product update of {DiagnosticText.namedType snapshot typeId} requires a source of that type, but the source recipe produces {DiagnosticText.typeRefWithId snapshot actual}.")
                | Some _ ->
                    Error(diagnostic "PureExpressionProductTargetMismatch" $"Product update target '{StableId.value typeId}' is not a product type.")
            | ExpressionRecipe.UnionConstruct(variantId, fields) ->
                match Map.tryFind variantId variantsById with
                | None ->
                    Error(diagnostic "PureExpressionUnionVariantNotFound" $"Union construction references unknown variant '{StableId.value variantId}'.")
                | Some(ownerTypeId, variant) ->
                    validateFieldBindings "UnionConstruct" variant.Fields fields |> Result.map (fun () -> TypeRef.Named ownerTypeId)
            | ExpressionRecipe.OptionNone itemType ->
                match validateTypeRef index itemType with
                | Error error -> Error error
                | Ok() -> Ok(optionType itemType)
            | ExpressionRecipe.ResultOk(errorType, value) ->
                match validateTypeRef index errorType with
                | Error error -> Error error
                | Ok() -> infer frames value |> Result.map (fun okType -> resultType okType errorType)
            | ExpressionRecipe.ResultErr(okType, error) ->
                match validateTypeRef index okType with
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
                    match missingEffect callee.RequiredEffects with
                    | Some effectId -> Error(missingEffectError $"DirectCall callee {callbackName callee}" effectId)
                    | None ->
                        validateCallArguments callee arguments |> Result.bind (fun substitution ->
                            if OneShotPayloadRequirements.violates oneShotPayloadRequirements.Value callee.Id substitution then
                                Error(diagnostic "OneShotScopePayloadUnsupported" "This generic call would place TaskScope inside a OneShot payload, directly or through another generic call.")
                            else Ok(TypeParameters.substitute substitution callee.ResultType))
            | ExpressionRecipe.SelfCall arguments ->
                // The owner's RequiredEffects trivially cover its own, so only the arguments are checked.
                let slotCount = List.length owner.SelfParameters
                match tryFindDuplicate fst arguments with
                | Some slot ->
                    Error(diagnostic "PureExpressionDuplicateCallArgument" $"SelfCall binds parameter slot {slot} more than once.")
                | None ->
                    match arguments |> List.tryFind (fun (slot, _) -> slot < 0 || slot >= slotCount) with
                    | Some(slot, _) ->
                        Error(
                            diagnostic
                                "PureExpressionCallArgumentTargetNotFound"
                                $"SelfCall targets parameter slot {slot}, but {owner.Description} has {slotCount} parameter(s). {describeOwnerParameters owner}"
                        )
                    | None ->
                        let bound = arguments |> List.map fst |> Set.ofList
                        match List.init slotCount id |> List.tryFind (fun slot -> not (Set.contains slot bound)) with
                        | Some slot ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentMissing"
                                    $"SelfCall is missing required parameter slot {slot} ('{owner.SelfParameters[slot].DisplayName}') of {owner.Description}."
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
                                                (let expected, produced = tyPair parameter.Type actual in
                                                 $"SelfCall parameter slot {slot} requires {expected}, but the recipe produces {produced}. A function calls itself at its own parameter types; there is no polymorphic recursion.")
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
                                $"CapabilityInvoke operation {StableId.describe operation.DisplayName operationId} requires {ty (TypeRef.Capability ownerCapabilityId)}, but the receiver produces {ty actual}."
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

        and inferCompletionNode frames recipe =
            let merge role left right =
                match GraphCompletion.merge [ Some left; Some right ] with
                | Some result -> Ok result
                | None ->
                    let leftText, rightText = DiagnosticText.completionPair snapshot left right
                    Error(diagnostic "PureExpressionBranchTypeMismatch" $"{role} normally completing branches must produce one common type; found {leftText} and {rightText}.")
            match recipe with
            | ExpressionRecipe.Abort payload ->
                match owner.ResultType with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; errorType ]) ->
                    infer frames payload |> Result.bind (fun actual ->
                        if actual = errorType then Ok GraphCompletion.Aborts
                        else
                            let expected, produced = tyPair errorType actual
                            Error(diagnostic "AbortPayloadTypeMismatch" $"Abort payload has type {produced}, but {owner.Description} requires {expected}."))
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
                        match resolveUnionArms variants arms with
                        | Error error -> Error error
                        | Ok resolved ->
                            let rec inferArms expected = function
                                | [] ->
                                    match expected with
                                    | Some resultType -> Ok resultType
                                    | None -> Error(diagnostic "PureExpressionMatchHasNoArms" "MatchUnion requires at least one declared variant to derive a result type.")
                                | ((armVariants: SemanticVariant list), body, bindsFields) :: rest ->
                                    let frame: PatternTypeFrame =
                                        { Kind = ScopeFrameKind.UnionArm(armVariants |> List.map (fun variant -> variant.Id))
                                          Slots =
                                            if bindsFields then
                                                armVariants.Head.Fields |> List.map (fun field -> RecipePatternSlot.Field field.Id, field.Type)
                                            else [] }
                                    match inferCompletion (frame :: frames) body with
                                    | Error error -> Error error
                                    | Ok actual ->
                                        match expected with
                                        | None -> inferArms (Some actual) rest
                                        | Some expectedType -> merge "MatchUnion" expectedType actual |> Result.bind (fun merged -> inferArms (Some merged) rest)
                            inferArms None resolved
                    | Some _
                    | None ->
                        Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
            | ExpressionRecipe.Bind(input, body) ->
                match infer frames input with
                | Error error -> Error error
                | Ok inputType -> inferCompletion ({ Kind = ScopeFrameKind.BindBody; Slots = [ RecipePatternSlot.Value, inputType ] } :: frames) body
            | ExpressionRecipe.MatchOption(input, someBody, noneBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) ->
                    let someFrame: PatternTypeFrame = { Kind = ScopeFrameKind.OptionSome; Slots = [ RecipePatternSlot.Value, itemType ] }
                    match inferCompletion (someFrame :: frames) someBody with
                    | Error error -> Error error
                    | Ok someType ->
                        match inferCompletion ({ Kind = ScopeFrameKind.OptionNone; Slots = [] } :: frames) noneBody with
                        | Error error -> Error error
                        | Ok noneType -> merge "MatchOption" someType noneType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchOption input must produce Option<T>.")
            | ExpressionRecipe.MatchResult(input, okBody, errBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                    let okFrame: PatternTypeFrame = { Kind = ScopeFrameKind.ResultOk; Slots = [ RecipePatternSlot.Value, okType ] }
                    let errFrame: PatternTypeFrame = { Kind = ScopeFrameKind.ResultErr; Slots = [ RecipePatternSlot.Error, errorType ] }
                    match inferCompletion (okFrame :: frames) okBody with
                    | Error error -> Error error
                    | Ok okResultType ->
                        match inferCompletion (errFrame :: frames) errBody with
                        | Error error -> Error error
                        | Ok errResultType -> merge "MatchResult" okResultType errResultType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchResult input must produce Result<T,E>.")

            | _ -> infer frames recipe |> Result.map GraphCompletion.Normal

        match inferCompletion initialFrames recipe with
        | Error error -> Error(locate rootLabel recipe failure.Value error)
        | Ok completion -> Ok completion

    let private inferTypeWithFrames rootLabel index target initialFrames recipe =
        inferTypeWithContext index (ownerForExistingFunction target) (InferenceCache()) initialFrames rootLabel recipe

    let private inferType rootLabel index target recipe = inferTypeWithFrames rootLabel index target [] recipe

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

    let private bindingTypeFrames (frames: PatternBindingFrame list): PatternTypeFrame list =
        frames |> List.map (fun frame -> { Kind = frame.Kind; Slots = frame.Slots |> List.map (fun (slot, binding) -> slot, binding.Type) })

    let private resolvePatternBinding (frames: PatternBindingFrame list) (reference: RecipePatternReference) =
        match List.tryItem reference.ScopeDepth frames with
        | Some frame ->
            match frame.Slots |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
            | Some(_, binding) -> binding
            | None -> failwith "Pattern recipe passed preflight with an unavailable slot."
        | None -> failwith "Pattern recipe passed preflight with an unavailable scope."

    /// Materialization is deterministic in shape/order with no caller-controlled IDs or CSE.
    let rec private materializeWithContext
        (index: SnapshotIndex)
        (owner: ExpressionOwnerContext)
        (cache: InferenceCache)
        (frames: PatternBindingFrame list)
        recipe
        =
        let snapshot = index.Snapshot
        let materializeRecipe child = materializeWithContext index owner cache frames child

        let materializeGraph childFrames body =
            let nodes, resultNodeId = materializeWithContext index owner cache childFrames body
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
            match inferTypeWithContext index owner cache (bindingTypeFrames frames) None child with
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
        | ExpressionRecipe.Int64ToHexString value -> unary SemanticNodeKind.Int64ToHexString value
        | ExpressionRecipe.StringParseInt64 value -> unary SemanticNodeKind.StringParseInt64 value
        | ExpressionRecipe.StringParseInt64Hex value -> unary SemanticNodeKind.StringParseInt64Hex value
        | ExpressionRecipe.Float64ToString value -> unary SemanticNodeKind.Float64ToString value
        | ExpressionRecipe.StringParseFloat64 value -> unary SemanticNodeKind.StringParseFloat64 value
        | ExpressionRecipe.StringCodePoints value -> unary SemanticNodeKind.StringCodePoints value
        | ExpressionRecipe.StringToLower value -> unary SemanticNodeKind.StringToLower value
        | ExpressionRecipe.StringToUpper value -> unary SemanticNodeKind.StringToUpper value
        | ExpressionRecipe.StringTrim value -> unary SemanticNodeKind.StringTrim value
        | ExpressionRecipe.StringTrimStart value -> unary SemanticNodeKind.StringTrimStart value
        | ExpressionRecipe.StringTrimEnd value -> unary SemanticNodeKind.StringTrimEnd value
        | ExpressionRecipe.StringDecodeUtf8Lossy value -> unary SemanticNodeKind.StringDecodeUtf8Lossy value
        | ExpressionRecipe.Float64RoundToInt64 value -> unary SemanticNodeKind.Float64RoundToInt64 value
        | ExpressionRecipe.Float64FloorToInt64 value -> unary SemanticNodeKind.Float64FloorToInt64 value
        | ExpressionRecipe.Float64Sqrt value -> unary SemanticNodeKind.Float64Sqrt value
        | ExpressionRecipe.Float64Abs value -> unary SemanticNodeKind.Float64Abs value
        | ExpressionRecipe.Float64Negate value -> unary SemanticNodeKind.Float64Negate value
        | ExpressionRecipe.Float64Floor value -> unary SemanticNodeKind.Float64Floor value
        | ExpressionRecipe.Float64Ceiling value -> unary SemanticNodeKind.Float64Ceiling value
        | ExpressionRecipe.Float64Truncate value -> unary SemanticNodeKind.Float64Truncate value
        | ExpressionRecipe.StringUtf8ByteCount value -> unary SemanticNodeKind.StringUtf8ByteCount value
        | ExpressionRecipe.StringFromCodePoint value -> unary SemanticNodeKind.StringFromCodePoint value
        | ExpressionRecipe.StringToCodePoint value -> unary SemanticNodeKind.StringToCodePoint value
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
        | ExpressionRecipe.StringIndexOf(value, substring, from) ->
            let valueNodes, valueResult = materializeRecipe value
            let substringNodes, substringResult = materializeRecipe substring
            let fromNodes, fromResult = materializeRecipe from
            let nodeId = StableIdAllocator.newGraphNodeId ()
            valueNodes @ substringNodes @ fromNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.StringIndexOf(valueResult, substringResult, fromResult) } ],
            nodeId
        | ExpressionRecipe.StringSpan(value, set, bound)
        | ExpressionRecipe.StringSpanBack(value, set, bound) ->
            let valueNodes, valueResult = materializeRecipe value
            let setNodes, setResult = materializeRecipe set
            let boundNodes, boundResult = materializeRecipe bound
            let nodeId = StableIdAllocator.newGraphNodeId ()
            let kind =
                match recipe with
                | ExpressionRecipe.StringSpan _ -> SemanticNodeKind.StringSpan(valueResult, setResult, boundResult)
                | _ -> SemanticNodeKind.StringSpanBack(valueResult, setResult, boundResult)
            valueNodes @ setNodes @ boundNodes @ [ { Id = nodeId; Kind = kind } ], nodeId
        | ExpressionRecipe.StringLastIndexOf(left, right) -> binary SemanticNodeKind.StringLastIndexOf left right
        | ExpressionRecipe.StringStartsWith(left, right) -> binary SemanticNodeKind.StringStartsWith left right
        | ExpressionRecipe.StringEndsWith(left, right) -> binary SemanticNodeKind.StringEndsWith left right
        | ExpressionRecipe.StringSplit(left, right) -> binary SemanticNodeKind.StringSplit left right
        | ExpressionRecipe.StringSplitLines(value, breaks) -> binary SemanticNodeKind.StringSplitLines value breaks
        | ExpressionRecipe.StringReplace(value, pattern, replacement) ->
            let valueNodes, valueResult = materializeRecipe value
            let patternNodes, patternResult = materializeRecipe pattern
            let replacementNodes, replacementResult = materializeRecipe replacement
            let nodeId = StableIdAllocator.newGraphNodeId ()
            valueNodes @ patternNodes @ replacementNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.StringReplace(valueResult, patternResult, replacementResult) } ],
            nodeId
        | ExpressionRecipe.StringJoin(list, separator) -> binary SemanticNodeKind.StringJoin list separator
        | ExpressionRecipe.StringEqual(left, right) -> binary SemanticNodeKind.StringEqual left right
        | ExpressionRecipe.StringLessThan(left, right) -> binary SemanticNodeKind.StringLessThan left right
        | ExpressionRecipe.ValueEqual(left, right) -> binary SemanticNodeKind.ValueEqual left right
        | ExpressionRecipe.StringConcat(left, right) -> binary SemanticNodeKind.StringConcat left right
        | ExpressionRecipe.Int64WrappingAdd(left, right) -> binary SemanticNodeKind.Int64WrappingAdd left right
        | ExpressionRecipe.Int64WrappingSubtract(left, right) -> binary SemanticNodeKind.Int64WrappingSubtract left right
        | ExpressionRecipe.Int64WrappingMultiply(left, right) -> binary SemanticNodeKind.Int64WrappingMultiply left right
        | ExpressionRecipe.Int64FloorDivide(left, right) -> binary SemanticNodeKind.Int64FloorDivide left right
        | ExpressionRecipe.Int64FloorModulo(left, right) -> binary SemanticNodeKind.Int64FloorModulo left right
        | ExpressionRecipe.Int64Min(left, right) -> binary SemanticNodeKind.Int64Min left right
        | ExpressionRecipe.Int64Max(left, right) -> binary SemanticNodeKind.Int64Max left right
        | ExpressionRecipe.Int64CheckedAdd(left, right) -> binary SemanticNodeKind.Int64CheckedAdd left right
        | ExpressionRecipe.Int64CheckedSubtract(left, right) -> binary SemanticNodeKind.Int64CheckedSubtract left right
        | ExpressionRecipe.Int64CheckedMultiply(left, right) -> binary SemanticNodeKind.Int64CheckedMultiply left right
        | ExpressionRecipe.ListRange(left, right) -> binary SemanticNodeKind.ListRange left right
        | ExpressionRecipe.StringRepeat(left, right) -> binary SemanticNodeKind.StringRepeat left right
        | ExpressionRecipe.Float64Round(left, right) -> binary SemanticNodeKind.Float64Round left right
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
        | ExpressionRecipe.Int64WrappingAbs value -> unary SemanticNodeKind.Int64WrappingAbs value
        | ExpressionRecipe.Bits32FromInt64 value -> unary SemanticNodeKind.Bits32FromInt64 value
        | ExpressionRecipe.Bits32ToInt64 value -> unary SemanticNodeKind.Bits32ToInt64 value
        | ExpressionRecipe.Bits32Not value -> unary SemanticNodeKind.Bits32Not value
        | ExpressionRecipe.Bits32Xor(left, right) -> binary SemanticNodeKind.Bits32Xor left right
        | ExpressionRecipe.Bits32And(left, right) -> binary SemanticNodeKind.Bits32And left right
        | ExpressionRecipe.Bits32Or(left, right) -> binary SemanticNodeKind.Bits32Or left right
        | ExpressionRecipe.Bits32Add(left, right) -> binary SemanticNodeKind.Bits32Add left right
        | ExpressionRecipe.Bits32RotateRight(left, right) -> binary SemanticNodeKind.Bits32RotateRight left right
        | ExpressionRecipe.Bits32ShiftRight(left, right) -> binary SemanticNodeKind.Bits32ShiftRight left right
        | ExpressionRecipe.Bits32ShiftLeft(left, right) -> binary SemanticNodeKind.Bits32ShiftLeft left right
        | ExpressionRecipe.ListEmpty itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ListEmpty itemType } ], nodeId
        | ExpressionRecipe.SetEmpty itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.SetEmpty itemType } ], nodeId
        | ExpressionRecipe.MapEmpty(keyType, valueType) ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.MapEmpty(keyType, valueType) } ], nodeId
        | ExpressionRecipe.ListConstruct(itemType, items) ->
            let itemNodes, itemResults = many items
            let nodeId = StableIdAllocator.newGraphNodeId ()
            itemNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListConstruct(itemType, itemResults) } ], nodeId
        | ExpressionRecipe.ListLength list -> unary SemanticNodeKind.ListLength list
        | ExpressionRecipe.MapValues map -> unary SemanticNodeKind.MapValues map
        | ExpressionRecipe.MapKeys map -> unary SemanticNodeKind.MapKeys map
        | ExpressionRecipe.SetToList map -> unary SemanticNodeKind.SetToList map
        | ExpressionRecipe.MapSize map -> unary SemanticNodeKind.MapSize map
        | ExpressionRecipe.SetFromList list -> unary SemanticNodeKind.SetFromList list
        | ExpressionRecipe.SetSize map -> unary SemanticNodeKind.SetSize map
        | ExpressionRecipe.ListReverse list -> unary SemanticNodeKind.ListReverse list
        | ExpressionRecipe.ListLast list -> unary SemanticNodeKind.ListLast list
        | ExpressionRecipe.ListDropLast list -> unary SemanticNodeKind.ListDropLast list
        | ExpressionRecipe.ListAppendItem(list, item) -> binary SemanticNodeKind.ListAppendItem list item
        | ExpressionRecipe.ListItemAt(list, index) -> binary SemanticNodeKind.ListItemAt list index
        | ExpressionRecipe.MapRemove(map, key) -> binary SemanticNodeKind.MapRemove map key
        | ExpressionRecipe.SetInsert(map, key) -> binary SemanticNodeKind.SetInsert map key
        | ExpressionRecipe.SetRemove(map, key) -> binary SemanticNodeKind.SetRemove map key
        | ExpressionRecipe.MapContainsKey(map, key) -> binary SemanticNodeKind.MapContainsKey map key
        | ExpressionRecipe.SetContains(map, key) -> binary SemanticNodeKind.SetContains map key
        | ExpressionRecipe.ListIndexOf(list, item) -> binary SemanticNodeKind.ListIndexOf list item
        | ExpressionRecipe.ListLastIndexOf(list, item) -> binary SemanticNodeKind.ListLastIndexOf list item
        | ExpressionRecipe.MapGet(map, key) -> binary SemanticNodeKind.MapGet map key
        | ExpressionRecipe.MapGetOrDefault(map, key, defaultValue) ->
            let mapNodes, mapResult = materializeRecipe map
            let keyNodes, keyResult = materializeRecipe key
            let defaultNodes, defaultResult = materializeRecipe defaultValue
            let nodeId = StableIdAllocator.newGraphNodeId ()
            mapNodes @ keyNodes @ defaultNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.MapGetOrDefault(mapResult, keyResult, defaultResult) } ],
            nodeId
        | ExpressionRecipe.ListSlice(list, start, stop) ->
            let listNodes, listResult = materializeRecipe list
            let startNodes, startResult = materializeRecipe start
            let stopNodes, stopResult = materializeRecipe stop
            let nodeId = StableIdAllocator.newGraphNodeId ()
            listNodes @ startNodes @ stopNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.ListSlice(listResult, startResult, stopResult) } ],
            nodeId
        | ExpressionRecipe.MapInsert(map, key, value) ->
            let listNodes, listResult = materializeRecipe map
            let startNodes, startResult = materializeRecipe key
            let stopNodes, stopResult = materializeRecipe value
            let nodeId = StableIdAllocator.newGraphNodeId ()
            listNodes @ startNodes @ stopNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.MapInsert(listResult, startResult, stopResult) } ],
            nodeId
        | ExpressionRecipe.ListConcat(left, right) -> binary SemanticNodeKind.ListConcat left right
        | ExpressionRecipe.SetUnion(left, right) -> binary SemanticNodeKind.SetUnion left right
        | ExpressionRecipe.SetIntersection(left, right) -> binary SemanticNodeKind.SetIntersection left right
        | ExpressionRecipe.SetDifference(left, right) -> binary SemanticNodeKind.SetDifference left right
        | ExpressionRecipe.ListFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.MapFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MapFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
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
        | ExpressionRecipe.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
            let bodyNodes, bodyBindings = materializeContext body bodyContext
            let handlerNodes, handlerBindings = materializeContext handler handlerContext
            let nodeId = StableIdAllocator.newGraphNodeId ()
            bodyNodes @ handlerNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ExceptionCatch(body, bodyBindings, handler, handlerBindings) } ], nodeId
        | ExpressionRecipe.ExceptionIsCancellation value -> unary SemanticNodeKind.ExceptionIsCancellation value
        | ExpressionRecipe.ExceptionMessage value -> unary SemanticNodeKind.ExceptionMessage value
        | ExpressionRecipe.ExceptionRethrow(value, resultType) -> unary (fun value -> SemanticNodeKind.ExceptionRethrow(value, resultType)) value
        | ExpressionRecipe.CancellationShield(bodyFunctionId, context) ->
            let contextNodes, contextBindings = materializeContext bodyFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.CancellationShield(bodyFunctionId, contextBindings) } ], nodeId
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
        | ExpressionRecipe.OneShotCreate(scope, valueType) -> unary (fun scope -> SemanticNodeKind.OneShotCreate(scope, valueType)) scope
        | ExpressionRecipe.OneShotComplete(cell, value) -> binary SemanticNodeKind.OneShotComplete cell value
        | ExpressionRecipe.OneShotWait(cell, limit) ->
            let cellNodes, cellId = materializeRecipe cell
            let limitNodes, limitId = match limit with Some item -> let nodes, id = materializeRecipe item in nodes, Some id | None -> [], None
            let id = StableIdAllocator.newGraphNodeId ()
            cellNodes @ limitNodes @ [ { Id = id; Kind = SemanticNodeKind.OneShotWait(cellId, limitId) } ], id
        | ExpressionRecipe.TaskCancel task -> unary SemanticNodeKind.TaskCancel task
        | ExpressionRecipe.ListMap(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.ListMap source mapperFunctionId context
        | ExpressionRecipe.MapFromList(source, keyOfFunctionId, context) -> listCallback SemanticNodeKind.MapFromList source keyOfFunctionId context
        | ExpressionRecipe.ListFilter(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListFilter source predicateFunctionId context
        | ExpressionRecipe.MapFilter(source, keepFunctionId, context) -> listCallback SemanticNodeKind.MapFilter source keepFunctionId context
        | ExpressionRecipe.MapMapValues(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.MapMapValues source mapperFunctionId context
        | ExpressionRecipe.ListSortBy(source, lessThanFunctionId, context) -> listCallback SemanticNodeKind.ListSortBy source lessThanFunctionId context
        | ExpressionRecipe.ListMinBy(source, lessThanFunctionId, context) -> listCallback SemanticNodeKind.ListMinBy source lessThanFunctionId context
        | ExpressionRecipe.ListAny(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListAny source predicateFunctionId context
        | ExpressionRecipe.ListFindIndex(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListFindIndex source predicateFunctionId context
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
        | ExpressionRecipe.ProductUpdate(typeId, source, fields) ->
            let updatedFields =
                match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                | { Kind = SemanticTypeKind.Product declaredFields } ->
                    declaredFields |> List.filter (fun field -> fields |> List.exists (fun (fieldId, _) -> fieldId = field.Id))
                | _ -> failwith "Product update recipe passed preflight with a non-product type."
            let sourceNodes, sourceResult = materializeRecipe source
            let fieldNodes, fieldBindings = materializeFieldBindings updatedFields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ProductUpdate(typeId, sourceResult, fieldBindings) } ], nodeId
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

            let resolved =
                match resolveUnionArms variants arms with
                | Ok resolved -> resolved
                | Error _ -> failwith "MatchUnion recipe passed preflight with arms that do not resolve."

            let materializedArms: UnionMatchArm list =
                resolved
                |> List.map (fun (armVariants, bodyRecipe, bindsFields) ->
                    // A one-variant arm binds every field, even an `otherwise` arm whose body cannot see them.
                    let fieldBindings =
                        match armVariants with
                        | [ variant ] ->
                            variant.Fields
                            |> List.map (fun field ->
                                { SourceFieldId = field.Id
                                  Binding =
                                    { Id = StableIdAllocator.newPatternBindingId ()
                                      DisplayName = None
                                      Type = field.Type } })
                        | _ -> []
                    let frame: PatternBindingFrame =
                        { Kind = ScopeFrameKind.UnionArm(armVariants |> List.map (fun variant -> variant.Id))
                          Slots =
                            if bindsFields then
                                fieldBindings |> List.map (fun item -> RecipePatternSlot.Field item.SourceFieldId, item.Binding)
                            else [] }
                    { VariantIds = armVariants |> List.map (fun variant -> variant.Id)
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
                  Body = materializeGraph ({ Kind = ScopeFrameKind.BindBody; Slots = [ RecipePatternSlot.Value, binding ] } :: frames) body }
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
                  Body = materializeGraph ({ Kind = ScopeFrameKind.OptionSome; Slots = [ RecipePatternSlot.Value, someBinding ] } :: frames) someBody }
            let noneGraph = materializeGraph ({ Kind = ScopeFrameKind.OptionNone; Slots = [] } :: frames) noneBody
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
                  Body = materializeGraph ({ Kind = ScopeFrameKind.ResultOk; Slots = [ RecipePatternSlot.Value, okBinding ] } :: frames) okBody }
            let errArm: ErrorMatchArm =
                { ErrorBinding = errBinding
                  Body = materializeGraph ({ Kind = ScopeFrameKind.ResultErr; Slots = [ RecipePatternSlot.Error, errBinding ] } :: frames) errBody }
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchResult(inputResult, okArm, errArm) } ], nodeId

    let private materialize index target frames recipe =
        materializeWithContext index (ownerForExistingFunction target) (InferenceCache()) frames recipe

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

        /// The scope frame a nested graph opens for recipe pattern references, if any: branches and
        /// Bool operands open none, arms open one frame per binding slot.
        let private frameOfScope (scope: NestedScope): PatternBindingFrame option =
            let slots =
                scope.Bindings
                |> List.map (fun binding ->
                    match binding with
                    | NestedBinding.Field(fieldId, pattern) -> RecipePatternSlot.Field fieldId, pattern
                    | NestedBinding.Value pattern -> RecipePatternSlot.Value, pattern
                    | NestedBinding.Error pattern -> RecipePatternSlot.Error, pattern)

            let frame kind: PatternBindingFrame option = Some { Kind = kind; Slots = slots }

            match scope.Role with
            | NestedGraphRole.WhenTrue
            | NestedGraphRole.WhenFalse
            | NestedGraphRole.BoolAndRight
            | NestedGraphRole.BoolOrRight -> None
            | NestedGraphRole.UnionArm variantIds -> frame (ScopeFrameKind.UnionArm variantIds)
            | NestedGraphRole.BindBody -> frame ScopeFrameKind.BindBody
            | NestedGraphRole.OptionSome -> frame ScopeFrameKind.OptionSome
            | NestedGraphRole.OptionNone -> frame ScopeFrameKind.OptionNone
            | NestedGraphRole.ResultOk -> frame ScopeFrameKind.ResultOk
            | NestedGraphRole.ResultErr -> frame ScopeFrameKind.ResultErr

        let rec private tryFindTarget
            (frames: PatternBindingFrame list)
            targetNodeId
            (graph: ValueGraph)
            =
            match graph.Nodes |> List.tryFind (fun node -> node.Id = targetNodeId) with
            | Some node -> Some(node, frames)
            | None ->
                graph.Nodes
                |> List.tryPick (fun node ->
                    SemanticNodeShape.nestedScopes node
                    |> List.tryPick (fun scope ->
                        let innerFrames =
                            match frameOfScope scope with
                            | Some frame -> frame :: frames
                            | None -> frames
                        tryFindTarget innerFrames targetNodeId scope.Graph))

        let rec private graphResultUses owner (graph: ValueGraph) =
            (graph.ResultNodeId, owner)
            :: (graph.Nodes
                |> List.collect (fun node ->
                    SemanticNodeShape.nestedGraphs node
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

        /// Nodes that use the target node as a value, as full validation sees value uses. An
        /// abrupt expression cannot stand where any of them reads it.
        let private valueConsumers targetNodeId (target: SemanticFunction) =
            ValueGraph.collectNodes target.Body
            |> List.filter (fun node ->
                SemanticNodeShape.directValueReferences node |> List.contains targetNodeId)

        /// Whether the target node is the result of the function body or of a nested graph.
        let private isGraphResult targetNodeId (target: SemanticFunction) =
            graphResultUses None target.Body |> List.exists (fun (resultNodeId, _) -> resultNodeId = targetNodeId)

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
                        |> List.map (SemanticNodeShape.mapNestedGraphs (replaceTarget targetNodeId replacementNodes)) }

        let rec private pruneNodes removedNodeIds (graph: ValueGraph) =
            { graph with
                Nodes =
                    graph.Nodes
                    |> List.choose (fun node ->
                        if Set.contains node.Id removedNodeIds then
                            None
                        else
                            Some(SemanticNodeShape.mapNestedGraphs (pruneNodes removedNodeIds) node)) }

        let private materializeReplacement
            index
            target
            frames
            targetNodeId
            recipe
            =
            let nodes, generatedRootId = materialize index target frames recipe
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
            /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
            let proposedSnapshot proposal = (state proposal).ProposedSnapshot

        [<RequireQualifiedAccess>]
        type CommitResult =
            | Rejected of TransactionDiagnostic list
            | Committed of AgentWorkspace * PublishedTransaction

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

        let private simulateWith
            (requireUniqueName: bool)
            (workspace: AgentWorkspace)
            (request: Request)
            : Result<Proposal, AgentStructuralWorkflowError> =
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
