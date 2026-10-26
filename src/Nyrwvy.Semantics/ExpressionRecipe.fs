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
    /// The target node's expression as it was in the base snapshot; accepted at most once, and only
    /// in the replacement of a targeted replacement.
    | OriginalTarget
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
    | StringEncodeUtf8 of ExpressionRecipe
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
    | StringLastIndexOfBefore of value: ExpressionRecipe * substring: ExpressionRecipe * ``end``: ExpressionRecipe
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
    | Int64BitAnd of ExpressionRecipe * ExpressionRecipe
    | Int64BitOr of ExpressionRecipe * ExpressionRecipe
    | Int64BitXor of ExpressionRecipe * ExpressionRecipe
    | Int64ShiftLeft of value: ExpressionRecipe * count: ExpressionRecipe
    | Int64ShiftRight of value: ExpressionRecipe * count: ExpressionRecipe
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
    | Float64Min of ExpressionRecipe * ExpressionRecipe
    | Float64Max of ExpressionRecipe * ExpressionRecipe
    | Float64Subtract of ExpressionRecipe * ExpressionRecipe
    | Float64Multiply of ExpressionRecipe * ExpressionRecipe
    | Float64Divide of ExpressionRecipe * ExpressionRecipe
    | Float64FloorModulo of ExpressionRecipe * ExpressionRecipe
    | Float64Equal of ExpressionRecipe * ExpressionRecipe
    | Float64LessThan of ExpressionRecipe * ExpressionRecipe
    | Float64IsNaN of ExpressionRecipe
    | Float64IsFinite of ExpressionRecipe
    | Float64IsInfinite of ExpressionRecipe
    | Int64ToFloat64 of ExpressionRecipe
    | Int64WrappingAbs of ExpressionRecipe
    | Int64BitNot of ExpressionRecipe
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
    | BytesEmpty
    | BytesFill of length: ExpressionRecipe * value: ExpressionRecipe
    | BytesLength of ExpressionRecipe
    | BytesGet of bytes: ExpressionRecipe * index: ExpressionRecipe
    | BytesSet of bytes: ExpressionRecipe * index: ExpressionRecipe * value: ExpressionRecipe
    | BytesAppend of bytes: ExpressionRecipe * value: ExpressionRecipe
    | BytesConcat of ExpressionRecipe * ExpressionRecipe
    | BytesSlice of bytes: ExpressionRecipe * start: ExpressionRecipe * ``end``: ExpressionRecipe
    | BytesFromLatin1 of ExpressionRecipe
    | BytesToLatin1 of ExpressionRecipe
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
    | ListContains of list: ExpressionRecipe * item: ExpressionRecipe
    | MapGet of map: ExpressionRecipe * key: ExpressionRecipe
    | MapGetOrDefault of map: ExpressionRecipe * key: ExpressionRecipe * defaultValue: ExpressionRecipe
    | ListSlice of list: ExpressionRecipe * start: ExpressionRecipe * ``end``: ExpressionRecipe
    | ListSetAt of list: ExpressionRecipe * index: ExpressionRecipe * item: ExpressionRecipe
    | ListInsertAt of list: ExpressionRecipe * index: ExpressionRecipe * item: ExpressionRecipe
    | ListRemoveAt of list: ExpressionRecipe * index: ExpressionRecipe
    | MapInsert of map: ExpressionRecipe * key: ExpressionRecipe * ``end``: ExpressionRecipe
    | ListConcat of left: ExpressionRecipe * right: ExpressionRecipe
    | SetUnion of left: ExpressionRecipe * right: ExpressionRecipe
    | SetIntersection of left: ExpressionRecipe * right: ExpressionRecipe
    | SetDifference of left: ExpressionRecipe * right: ExpressionRecipe
    | SetIsSubset of left: ExpressionRecipe * right: ExpressionRecipe
    | MapUnion of left: ExpressionRecipe * right: ExpressionRecipe
    /// `context` binds callback parameters by identity; the unbound ones receive iteration values.
    | ListFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    /// The seed followed by every intermediate ListFold state, as F# List.scan.
    | ListScan of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | SetFold of source: ExpressionRecipe * seed: ExpressionRecipe * folderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
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
    | ListZipWith of left: ExpressionRecipe * right: ExpressionRecipe * combinerFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListFlatMap of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapFromList of source: ExpressionRecipe * keyOfFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListFilter of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapFilter of source: ExpressionRecipe * keepFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | SetFilter of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | MapMapValues of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | SetMap of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListSortBy of source: ExpressionRecipe * lessThanFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListMinBy of source: ExpressionRecipe * lessThanFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListMaxBy of source: ExpressionRecipe * lessThanFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListAny of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListFindIndex of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ListAll of source: ExpressionRecipe * predicateFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    /// `typeArguments` instantiate a generic Product's type parameters; empty means "derive them
    /// from the field values" (always empty for a monomorphic Product).
    | ProductConstruct of typeId: TypeId * typeArguments: TypeRef list * fields: (FieldId * ExpressionRecipe) list
    | FieldAccess of source: ExpressionRecipe * fieldId: FieldId
    | ProductUpdate of typeId: TypeId * source: ExpressionRecipe * fields: (FieldId * ExpressionRecipe) list
    /// `typeArguments` as for ProductConstruct; a variant whose fields do not mention every type
    /// parameter (a fieldless `None`-like variant) needs them given.
    | UnionConstruct of variantId: VariantId * typeArguments: TypeRef list * fields: (FieldId * ExpressionRecipe) list
    | OptionNone of itemType: TypeRef
    | OptionSome of ExpressionRecipe
    | OptionGetOrDefault of option: ExpressionRecipe * defaultValue: ExpressionRecipe
    | ResultGetOrDefault of result: ExpressionRecipe * defaultValue: ExpressionRecipe
    | OptionOkOr of option: ExpressionRecipe * error: ExpressionRecipe
    | ResultOkValue of ExpressionRecipe
    | ResultErrValue of ExpressionRecipe
    | OptionIsSome of ExpressionRecipe
    | ResultIsOk of ExpressionRecipe
    | OptionMap of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | OptionAndThen of source: ExpressionRecipe * binderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ResultMap of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ResultMapErr of source: ExpressionRecipe * mapperFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
    | ResultAndThen of source: ExpressionRecipe * binderFunctionId: FunctionId * context: (ParameterId * ExpressionRecipe) list
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
        | ExpressionRecipe.OriginalTarget
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
        | ExpressionRecipe.StringEncodeUtf8 value -> ExpressionRecipe.StringEncodeUtf8(go value)
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
        | ExpressionRecipe.StringLastIndexOfBefore(value, set, stop) -> ExpressionRecipe.StringLastIndexOfBefore(go value, go set, go stop)
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
        | ExpressionRecipe.Int64BitAnd(left, right) -> ExpressionRecipe.Int64BitAnd(go left, go right)
        | ExpressionRecipe.Int64BitOr(left, right) -> ExpressionRecipe.Int64BitOr(go left, go right)
        | ExpressionRecipe.Int64BitXor(left, right) -> ExpressionRecipe.Int64BitXor(go left, go right)
        | ExpressionRecipe.Int64ShiftLeft(left, right) -> ExpressionRecipe.Int64ShiftLeft(go left, go right)
        | ExpressionRecipe.Int64ShiftRight(left, right) -> ExpressionRecipe.Int64ShiftRight(go left, go right)
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
        | ExpressionRecipe.Float64Min(left, right) -> ExpressionRecipe.Float64Min(go left, go right)
        | ExpressionRecipe.Float64Max(left, right) -> ExpressionRecipe.Float64Max(go left, go right)
        | ExpressionRecipe.Float64Subtract(left, right) -> ExpressionRecipe.Float64Subtract(go left, go right)
        | ExpressionRecipe.Float64Multiply(left, right) -> ExpressionRecipe.Float64Multiply(go left, go right)
        | ExpressionRecipe.Float64Divide(left, right) -> ExpressionRecipe.Float64Divide(go left, go right)
        | ExpressionRecipe.Float64FloorModulo(left, right) -> ExpressionRecipe.Float64FloorModulo(go left, go right)
        | ExpressionRecipe.Float64Equal(left, right) -> ExpressionRecipe.Float64Equal(go left, go right)
        | ExpressionRecipe.Float64LessThan(left, right) -> ExpressionRecipe.Float64LessThan(go left, go right)
        | ExpressionRecipe.Float64IsNaN value -> ExpressionRecipe.Float64IsNaN(go value)
        | ExpressionRecipe.Float64IsFinite value -> ExpressionRecipe.Float64IsFinite(go value)
        | ExpressionRecipe.Float64IsInfinite value -> ExpressionRecipe.Float64IsInfinite(go value)
        | ExpressionRecipe.Int64ToFloat64 value -> ExpressionRecipe.Int64ToFloat64(go value)
        | ExpressionRecipe.Int64WrappingAbs value -> ExpressionRecipe.Int64WrappingAbs(go value)
        | ExpressionRecipe.Int64BitNot value -> ExpressionRecipe.Int64BitNot(go value)
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
        | ExpressionRecipe.BytesEmpty -> recipe
        | ExpressionRecipe.BytesFill(length, value) -> ExpressionRecipe.BytesFill(go length, go value)
        | ExpressionRecipe.BytesLength value -> ExpressionRecipe.BytesLength(go value)
        | ExpressionRecipe.BytesGet(bytes, index) -> ExpressionRecipe.BytesGet(go bytes, go index)
        | ExpressionRecipe.BytesSet(bytes, index, value) -> ExpressionRecipe.BytesSet(go bytes, go index, go value)
        | ExpressionRecipe.BytesAppend(bytes, value) -> ExpressionRecipe.BytesAppend(go bytes, go value)
        | ExpressionRecipe.BytesConcat(left, right) -> ExpressionRecipe.BytesConcat(go left, go right)
        | ExpressionRecipe.BytesSlice(bytes, start, stop) -> ExpressionRecipe.BytesSlice(go bytes, go start, go stop)
        | ExpressionRecipe.BytesFromLatin1 value -> ExpressionRecipe.BytesFromLatin1(go value)
        | ExpressionRecipe.BytesToLatin1 value -> ExpressionRecipe.BytesToLatin1(go value)
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
        | ExpressionRecipe.ListContains(list, item) -> ExpressionRecipe.ListContains(go list, go item)
        | ExpressionRecipe.MapGet(map, key) -> ExpressionRecipe.MapGet(go map, go key)
        | ExpressionRecipe.MapGetOrDefault(map, key, defaultValue) -> ExpressionRecipe.MapGetOrDefault(go map, go key, go defaultValue)
        | ExpressionRecipe.ListSlice(list, start, stop) -> ExpressionRecipe.ListSlice(go list, go start, go stop)
        | ExpressionRecipe.ListSetAt(list, index, item) -> ExpressionRecipe.ListSetAt(go list, go index, go item)
        | ExpressionRecipe.ListInsertAt(list, index, item) -> ExpressionRecipe.ListInsertAt(go list, go index, go item)
        | ExpressionRecipe.ListRemoveAt(list, index) -> ExpressionRecipe.ListRemoveAt(go list, go index)
        | ExpressionRecipe.MapInsert(map, key, value) -> ExpressionRecipe.MapInsert(go map, go key, go value)
        | ExpressionRecipe.ListConcat(left, right) -> ExpressionRecipe.ListConcat(go left, go right)
        | ExpressionRecipe.SetUnion(left, right) -> ExpressionRecipe.SetUnion(go left, go right)
        | ExpressionRecipe.SetIntersection(left, right) -> ExpressionRecipe.SetIntersection(go left, go right)
        | ExpressionRecipe.SetDifference(left, right) -> ExpressionRecipe.SetDifference(go left, go right)
        | ExpressionRecipe.SetIsSubset(left, right) -> ExpressionRecipe.SetIsSubset(go left, go right)
        | ExpressionRecipe.MapUnion(left, right) -> ExpressionRecipe.MapUnion(go left, go right)
        | ExpressionRecipe.ListFold(source, seed, folder, context) -> ExpressionRecipe.ListFold(go source, go seed, folder, pairs context)
        | ExpressionRecipe.ListScan(source, seed, folder, context) -> ExpressionRecipe.ListScan(go source, go seed, folder, pairs context)
        | ExpressionRecipe.MapFold(source, seed, folder, context) -> ExpressionRecipe.MapFold(go source, go seed, folder, pairs context)
        | ExpressionRecipe.SetFold(source, seed, folder, context) -> ExpressionRecipe.SetFold(go source, go seed, folder, pairs context)
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
        | ExpressionRecipe.ListZipWith(left, right, callback, context) -> ExpressionRecipe.ListZipWith(go left, go right, callback, pairs context)
        | ExpressionRecipe.ListFlatMap(source, callback, context) -> ExpressionRecipe.ListFlatMap(go source, callback, pairs context)
        | ExpressionRecipe.MapFromList(source, callback, context) -> ExpressionRecipe.MapFromList(go source, callback, pairs context)
        | ExpressionRecipe.ListFilter(source, callback, context) -> ExpressionRecipe.ListFilter(go source, callback, pairs context)
        | ExpressionRecipe.MapFilter(source, callback, context) -> ExpressionRecipe.MapFilter(go source, callback, pairs context)
        | ExpressionRecipe.SetFilter(source, callback, context) -> ExpressionRecipe.SetFilter(go source, callback, pairs context)
        | ExpressionRecipe.ListSortBy(source, callback, context) -> ExpressionRecipe.ListSortBy(go source, callback, pairs context)
        | ExpressionRecipe.MapMapValues(source, callback, context) -> ExpressionRecipe.MapMapValues(go source, callback, pairs context)
        | ExpressionRecipe.SetMap(source, callback, context) -> ExpressionRecipe.SetMap(go source, callback, pairs context)
        | ExpressionRecipe.OptionMap(source, callback, context) -> ExpressionRecipe.OptionMap(go source, callback, pairs context)
        | ExpressionRecipe.OptionAndThen(source, callback, context) -> ExpressionRecipe.OptionAndThen(go source, callback, pairs context)
        | ExpressionRecipe.ResultMap(source, callback, context) -> ExpressionRecipe.ResultMap(go source, callback, pairs context)
        | ExpressionRecipe.ResultMapErr(source, callback, context) -> ExpressionRecipe.ResultMapErr(go source, callback, pairs context)
        | ExpressionRecipe.ResultAndThen(source, callback, context) -> ExpressionRecipe.ResultAndThen(go source, callback, pairs context)
        | ExpressionRecipe.ListMinBy(source, callback, context) -> ExpressionRecipe.ListMinBy(go source, callback, pairs context)
        | ExpressionRecipe.ListMaxBy(source, callback, context) -> ExpressionRecipe.ListMaxBy(go source, callback, pairs context)
        | ExpressionRecipe.ListAny(source, callback, context) -> ExpressionRecipe.ListAny(go source, callback, pairs context)
        | ExpressionRecipe.ListFindIndex(source, callback, context) -> ExpressionRecipe.ListFindIndex(go source, callback, pairs context)
        | ExpressionRecipe.ListAll(source, callback, context) -> ExpressionRecipe.ListAll(go source, callback, pairs context)
        | ExpressionRecipe.ProductConstruct(typeId, typeArguments, fields) -> ExpressionRecipe.ProductConstruct(typeId, List.map mapType typeArguments, pairs fields)
        | ExpressionRecipe.FieldAccess(source, fieldId) -> ExpressionRecipe.FieldAccess(go source, fieldId)
        | ExpressionRecipe.ProductUpdate(typeId, source, fields) -> ExpressionRecipe.ProductUpdate(typeId, go source, pairs fields)
        | ExpressionRecipe.UnionConstruct(variantId, typeArguments, fields) -> ExpressionRecipe.UnionConstruct(variantId, List.map mapType typeArguments, pairs fields)
        | ExpressionRecipe.OptionNone itemType -> ExpressionRecipe.OptionNone(mapType itemType)
        | ExpressionRecipe.OptionSome value -> ExpressionRecipe.OptionSome(go value)
        | ExpressionRecipe.OptionGetOrDefault(option, defaultValue) -> ExpressionRecipe.OptionGetOrDefault(go option, go defaultValue)
        | ExpressionRecipe.ResultGetOrDefault(result, defaultValue) -> ExpressionRecipe.ResultGetOrDefault(go result, go defaultValue)
        | ExpressionRecipe.OptionOkOr(option, error) -> ExpressionRecipe.OptionOkOr(go option, go error)
        | ExpressionRecipe.ResultOkValue value -> ExpressionRecipe.ResultOkValue(go value)
        | ExpressionRecipe.ResultErrValue value -> ExpressionRecipe.ResultErrValue(go value)
        | ExpressionRecipe.OptionIsSome value -> ExpressionRecipe.OptionIsSome(go value)
        | ExpressionRecipe.ResultIsOk value -> ExpressionRecipe.ResultIsOk(go value)
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
