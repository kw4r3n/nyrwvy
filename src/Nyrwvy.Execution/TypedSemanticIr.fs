namespace Nyrwvy.Core

/// Typed Semantic IR is a derived, backend-neutral projection of one fully
/// validated and semantically complete snapshot. It intentionally excludes
/// display names, obligations, transaction history, storage shape, and viewer data.
type TypedSemanticField =
    { Id: FieldId
      Type: TypeRef }

type TypedSemanticVariant =
    { Id: VariantId
      Fields: TypedSemanticField list }

[<RequireQualifiedAccess>]
type TypedSemanticTypeKind =
    | Product of TypedSemanticField list
    | Union of TypedSemanticVariant list

type TypedSemanticType =
    { Id: TypeId
      Kind: TypedSemanticTypeKind }

type TypedSemanticCapabilityParameter =
    { Id: CapabilityParameterId
      Type: TypeRef }

type TypedSemanticCapabilityOperation =
    { Id: CapabilityOperationId
      Parameters: TypedSemanticCapabilityParameter list
      ResultType: TypeRef
      FailureType: TypeRef option
      EffectId: EffectId }

type TypedSemanticCapability =
    { Id: CapabilityId
      Operations: TypedSemanticCapabilityOperation list }

type TypedSemanticParameter =
    { Id: ParameterId
      Type: TypeRef }

type TypedSemanticArgument =
    { TargetParameterId: ParameterId
      ValueNodeId: GraphNodeId }

type TypedSemanticCapabilityArgument =
    { TargetCapabilityParameterId: CapabilityParameterId
      ValueNodeId: GraphNodeId }

type TypedSemanticFieldValue =
    { TargetFieldId: FieldId
      ValueNodeId: GraphNodeId }

type TypedSemanticPatternBinding =
    { Id: PatternBindingId
      Type: TypeRef }

type TypedSemanticPatternField =
    { SourceFieldId: FieldId
      Binding: TypedSemanticPatternBinding }

type TypedSemanticUnionArm =
    { VariantIds: VariantId list
      FieldBindings: TypedSemanticPatternField list
      Body: TypedSemanticGraph }

and TypedSemanticValueArm =
    { ValueBinding: TypedSemanticPatternBinding
      Body: TypedSemanticGraph }

and TypedSemanticErrorArm =
    { ErrorBinding: TypedSemanticPatternBinding
      Body: TypedSemanticGraph }

and [<RequireQualifiedAccess>] TypedSemanticNodeKind =
    | ParameterReference of ParameterId
    | PatternReference of PatternBindingId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of valueNodeId: GraphNodeId
    | Int64ToString of valueNodeId: GraphNodeId
    | Int64ToHexString of valueNodeId: GraphNodeId
    | StringParseInt64 of valueNodeId: GraphNodeId
    | StringParseInt64Hex of valueNodeId: GraphNodeId
    | Float64ToString of valueNodeId: GraphNodeId
    | StringParseFloat64 of valueNodeId: GraphNodeId
    | StringCodePoints of valueNodeId: GraphNodeId
    | StringToLower of valueNodeId: GraphNodeId
    | StringToUpper of valueNodeId: GraphNodeId
    | StringDecodeUtf8Lossy of valueNodeId: GraphNodeId
    | Float64RoundToInt64 of valueNodeId: GraphNodeId
    | Float64FloorToInt64 of valueNodeId: GraphNodeId
    | Float64Sqrt of valueNodeId: GraphNodeId
    | Float64Abs of valueNodeId: GraphNodeId
    | Float64Negate of valueNodeId: GraphNodeId
    | Float64Floor of valueNodeId: GraphNodeId
    | Float64Ceiling of valueNodeId: GraphNodeId
    | Float64Truncate of valueNodeId: GraphNodeId
    | StringUtf8ByteCount of valueNodeId: GraphNodeId
    | StringFromCodePoint of valueNodeId: GraphNodeId
    | StringToCodePoint of valueNodeId: GraphNodeId
    | StringLength of valueNodeId: GraphNodeId
    | StringSlice of valueNodeId: GraphNodeId * startNodeId: GraphNodeId * endNodeId: GraphNodeId
    | StringContains of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    | StringIndexOf of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId * fromNodeId: GraphNodeId
    | StringSpan of valueNodeId: GraphNodeId * setNodeId: GraphNodeId * fromNodeId: GraphNodeId
    | StringSpanBack of valueNodeId: GraphNodeId * setNodeId: GraphNodeId * endNodeId: GraphNodeId
    | StringLastIndexOf of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    | StringStartsWith of valueNodeId: GraphNodeId * prefixNodeId: GraphNodeId
    | StringEndsWith of valueNodeId: GraphNodeId * suffixNodeId: GraphNodeId
    | StringSplit of valueNodeId: GraphNodeId * separatorNodeId: GraphNodeId
    | StringSplitLines of valueNodeId: GraphNodeId * breaksNodeId: GraphNodeId
    | StringReplace of valueNodeId: GraphNodeId * patternNodeId: GraphNodeId * replacementNodeId: GraphNodeId
    | StringTrim of valueNodeId: GraphNodeId
    | StringTrimStart of valueNodeId: GraphNodeId
    | StringTrimEnd of valueNodeId: GraphNodeId
    | StringJoin of listNodeId: GraphNodeId * separatorNodeId: GraphNodeId
    | StringEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | StringLessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | ValueEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | StringConcat of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64FloorDivide of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64FloorModulo of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64Min of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64Max of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64BitAnd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64BitOr of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64BitXor of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64ShiftLeft of valueNodeId: GraphNodeId * countNodeId: GraphNodeId
    | Int64ShiftRight of valueNodeId: GraphNodeId * countNodeId: GraphNodeId
    | Int64CheckedAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64CheckedSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64CheckedMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | ListRange of startNodeId: GraphNodeId * endNodeId: GraphNodeId
    | StringRepeat of textNodeId: GraphNodeId * countNodeId: GraphNodeId
    | Float64Round of valueNodeId: GraphNodeId * digitsNodeId: GraphNodeId
    | Int64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Add of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Min of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Max of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Subtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Multiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Divide of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64IsNaN of valueNodeId: GraphNodeId
    | Float64IsFinite of valueNodeId: GraphNodeId
    | Float64IsInfinite of valueNodeId: GraphNodeId
    | Int64ToFloat64 of valueNodeId: GraphNodeId
    | Int64WrappingAbs of valueNodeId: GraphNodeId
    | Int64BitNot of valueNodeId: GraphNodeId
    | Bits32FromInt64 of valueNodeId: GraphNodeId
    | Bits32ToInt64 of valueNodeId: GraphNodeId
    | Bits32Not of valueNodeId: GraphNodeId
    | Bits32Xor of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Bits32And of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Bits32Or of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Bits32Add of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Bits32RotateRight of valueNodeId: GraphNodeId * amountNodeId: GraphNodeId
    | Bits32ShiftRight of valueNodeId: GraphNodeId * amountNodeId: GraphNodeId
    | Bits32ShiftLeft of valueNodeId: GraphNodeId * amountNodeId: GraphNodeId
    | ListEmpty
    | SetEmpty
    | MapEmpty
    | ListConstruct of itemNodeIds: GraphNodeId list
    | ListLength of listNodeId: GraphNodeId
    | MapValues of mapNodeId: GraphNodeId
    | MapKeys of mapNodeId: GraphNodeId
    | SetToList of setNodeId: GraphNodeId
    | MapSize of mapNodeId: GraphNodeId
    | SetFromList of sourceListNodeId: GraphNodeId
    | SetSize of setNodeId: GraphNodeId
    | ListReverse of listNodeId: GraphNodeId
    | ListLast of listNodeId: GraphNodeId
    | ListDropLast of listNodeId: GraphNodeId
    | ListAppendItem of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | ListItemAt of listNodeId: GraphNodeId * indexNodeId: GraphNodeId
    | MapRemove of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId
    | SetInsert of setNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | SetRemove of setNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | SetUnion of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    | SetIntersection of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    | SetDifference of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    | SetIsSubset of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    | MapUnion of leftMapNodeId: GraphNodeId * rightMapNodeId: GraphNodeId
    | MapContainsKey of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId
    | SetContains of setNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | ListIndexOf of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | ListLastIndexOf of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | MapGet of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId
    | MapGetOrDefault of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId * defaultNodeId: GraphNodeId
    | ListSlice of listNodeId: GraphNodeId * startNodeId: GraphNodeId * endNodeId: GraphNodeId
    | MapInsert of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId * valueNodeId: GraphNodeId
    | ListConcat of leftListNodeId: GraphNodeId * rightListNodeId: GraphNodeId
    | ListFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: TypedSemanticArgument list
    | MapFold of sourceMapNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: TypedSemanticArgument list
    | ParallelAll of sourceListNodeId: GraphNodeId * workerFunctionId: FunctionId * context: TypedSemanticArgument list
    | CancellationRequested
    | ExceptionCatch of bodyFunctionId: FunctionId * bodyContext: TypedSemanticArgument list * handlerFunctionId: FunctionId * handlerContext: TypedSemanticArgument list
    | ExceptionIsCancellation of exceptionValue: GraphNodeId
    | ExceptionMessage of exceptionValue: GraphNodeId
    | ExceptionRethrow of exceptionValue: GraphNodeId * resultType: TypeRef
    | CancellationShield of bodyFunctionId: FunctionId * context: TypedSemanticArgument list
    | TaskScope of bodyFunctionId: FunctionId * context: TypedSemanticArgument list
    | TaskStart of scopeNodeId: GraphNodeId * workerFunctionId: FunctionId * context: TypedSemanticArgument list * itemNodeId: GraphNodeId
    | TaskWait of tasksNodeId: GraphNodeId * mode: TaskWaitMode * limitNodeId: GraphNodeId option
    | TaskCancel of taskNodeId: GraphNodeId
    | OneShotCreate of scope: GraphNodeId * valueType: TypeRef
    | OneShotComplete of cell: GraphNodeId * value: GraphNodeId
    | OneShotWait of cell: GraphNodeId * limit: GraphNodeId option
    | UseResource of acquireNodeId: GraphNodeId * releaseCapabilityNodeId: GraphNodeId * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListEffectFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListMap of sourceListNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: TypedSemanticArgument list
    | MapFromList of sourceListNodeId: GraphNodeId * keyOfFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListFilter of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: TypedSemanticArgument list
    | MapFilter of sourceMapNodeId: GraphNodeId * keepFunctionId: FunctionId * context: TypedSemanticArgument list
    | MapMapValues of sourceMapNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListSortBy of sourceListNodeId: GraphNodeId * lessThanFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListMinBy of sourceListNodeId: GraphNodeId * lessThanFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListAny of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListFindIndex of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListAll of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: TypedSemanticArgument list
    /// Construct a stateless capability using named pure functions, one per operation.
    | CapabilityConstruct of CapabilityId * implementations: (CapabilityOperationId * FunctionId) list
    | FunctionReference of FunctionId
    | FunctionInvoke of GraphNodeId * GraphNodeId list
    | DirectCall of FunctionId * TypedSemanticArgument list
    | CapabilityInvoke of capabilityValueNodeId: GraphNodeId * CapabilityOperationId * TypedSemanticCapabilityArgument list
    | ProductConstruct of TypedSemanticFieldValue list
    | FieldAccess of sourceNodeId: GraphNodeId * FieldId
    /// The source product with the bound fields replaced, in field-ID order.
    | ProductUpdate of sourceNodeId: GraphNodeId * TypedSemanticFieldValue list
    | UnionConstruct of VariantId * TypedSemanticFieldValue list
    | OptionNone
    | OptionSome of valueNodeId: GraphNodeId
    | ResultOk of valueNodeId: GraphNodeId
    | Abort of errorNodeId: GraphNodeId
    | ResultErr of errorNodeId: GraphNodeId
    | BoolNot of operandNodeId: GraphNodeId
    | BoolAnd of leftNodeId: GraphNodeId * right: TypedSemanticGraph
    | BoolOr of leftNodeId: GraphNodeId * right: TypedSemanticGraph
    | Conditional of conditionNodeId: GraphNodeId * whenTrue: TypedSemanticGraph * whenFalse: TypedSemanticGraph
    | MatchUnion of inputNodeId: GraphNodeId * TypedSemanticUnionArm list
    | Bind of inputNodeId: GraphNodeId * body: TypedSemanticValueArm
    | MatchOption of inputNodeId: GraphNodeId * someArm: TypedSemanticValueArm * noneBody: TypedSemanticGraph
    | MatchResult of inputNodeId: GraphNodeId * okArm: TypedSemanticValueArm * errArm: TypedSemanticErrorArm

and TypedSemanticNode =
    { OriginNodeId: GraphNodeId
      Completion: GraphCompletion
      Kind: TypedSemanticNodeKind }
    member this.Type =
        match this.Completion with
        | GraphCompletion.Normal value -> value
        | GraphCompletion.Aborts -> invalidOp "An abrupt node has no value type."

and TypedSemanticGraph =
    { Nodes: TypedSemanticNode list
      ResultNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
module TypedSemanticNodeKind =
    /// The one exhaustive list of the graphs a node kind owns, in evaluation order (whenTrue before
    /// whenFalse, Some before None, Ok before Err, union arms in order). Every other kind owns none.
    /// Dependency, effect and effect-order analysis and the codec's feature scan walk nested graphs
    /// through it, so only a new structured kind needs an entry here.
    let subgraphs (kind: TypedSemanticNodeKind): TypedSemanticGraph list =
        match kind with
        | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) -> [ whenTrue; whenFalse ]
        | TypedSemanticNodeKind.BoolAnd(_, right)
        | TypedSemanticNodeKind.BoolOr(_, right) -> [ right ]
        | TypedSemanticNodeKind.MatchUnion(_, arms) -> arms |> List.map (fun arm -> arm.Body)
        | TypedSemanticNodeKind.Bind(_, body) -> [ body.Body ]
        | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) -> [ someArm.Body; noneBody ]
        | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) -> [ okArm.Body; errArm.Body ]
        | TypedSemanticNodeKind.ParameterReference _
        | TypedSemanticNodeKind.PatternReference _
        | TypedSemanticNodeKind.StringLiteral _
        | TypedSemanticNodeKind.BoolLiteral _
        | TypedSemanticNodeKind.UnitLiteral
        | TypedSemanticNodeKind.Int64Literal _
        | TypedSemanticNodeKind.Float64Literal _
        | TypedSemanticNodeKind.StringIsEmpty _
        | TypedSemanticNodeKind.Int64ToString _
        | TypedSemanticNodeKind.Int64ToHexString _
        | TypedSemanticNodeKind.StringParseInt64 _
        | TypedSemanticNodeKind.StringParseInt64Hex _
        | TypedSemanticNodeKind.Float64ToString _
        | TypedSemanticNodeKind.StringParseFloat64 _
        | TypedSemanticNodeKind.StringCodePoints _
        | TypedSemanticNodeKind.StringToLower _
        | TypedSemanticNodeKind.StringToUpper _
        | TypedSemanticNodeKind.StringDecodeUtf8Lossy _
        | TypedSemanticNodeKind.Float64RoundToInt64 _
        | TypedSemanticNodeKind.Float64FloorToInt64 _
        | TypedSemanticNodeKind.Float64Sqrt _
        | TypedSemanticNodeKind.Float64Abs _
        | TypedSemanticNodeKind.Float64Negate _
        | TypedSemanticNodeKind.Float64Floor _
        | TypedSemanticNodeKind.Float64Ceiling _
        | TypedSemanticNodeKind.Float64Truncate _
        | TypedSemanticNodeKind.StringUtf8ByteCount _
        | TypedSemanticNodeKind.StringFromCodePoint _
        | TypedSemanticNodeKind.StringToCodePoint _
        | TypedSemanticNodeKind.StringLength _
        | TypedSemanticNodeKind.StringSlice _
        | TypedSemanticNodeKind.StringContains _
        | TypedSemanticNodeKind.StringIndexOf _
        | TypedSemanticNodeKind.StringSpan _
        | TypedSemanticNodeKind.StringSpanBack _
        | TypedSemanticNodeKind.StringLastIndexOf _
        | TypedSemanticNodeKind.StringStartsWith _
        | TypedSemanticNodeKind.StringEndsWith _
        | TypedSemanticNodeKind.StringSplit _
        | TypedSemanticNodeKind.StringSplitLines _
        | TypedSemanticNodeKind.StringReplace _
        | TypedSemanticNodeKind.StringTrim _
        | TypedSemanticNodeKind.StringTrimStart _
        | TypedSemanticNodeKind.StringTrimEnd _
        | TypedSemanticNodeKind.StringJoin _
        | TypedSemanticNodeKind.StringEqual _
        | TypedSemanticNodeKind.StringLessThan _
        | TypedSemanticNodeKind.ValueEqual _
        | TypedSemanticNodeKind.StringConcat _
        | TypedSemanticNodeKind.Int64WrappingAdd _
        | TypedSemanticNodeKind.Int64WrappingSubtract _
        | TypedSemanticNodeKind.Int64WrappingMultiply _
        | TypedSemanticNodeKind.Int64FloorDivide _
        | TypedSemanticNodeKind.Int64FloorModulo _
        | TypedSemanticNodeKind.Int64Min _
        | TypedSemanticNodeKind.Int64Max _
        | TypedSemanticNodeKind.Int64BitAnd _
        | TypedSemanticNodeKind.Int64BitOr _
        | TypedSemanticNodeKind.Int64BitXor _
        | TypedSemanticNodeKind.Int64ShiftLeft _
        | TypedSemanticNodeKind.Int64ShiftRight _
        | TypedSemanticNodeKind.Int64CheckedAdd _
        | TypedSemanticNodeKind.Int64CheckedSubtract _
        | TypedSemanticNodeKind.Int64CheckedMultiply _
        | TypedSemanticNodeKind.ListRange _
        | TypedSemanticNodeKind.StringRepeat _
        | TypedSemanticNodeKind.Float64Round _
        | TypedSemanticNodeKind.Int64Equal _
        | TypedSemanticNodeKind.Int64LessThan _
        | TypedSemanticNodeKind.Int64LessThanOrEqual _
        | TypedSemanticNodeKind.Int64GreaterThan _
        | TypedSemanticNodeKind.Int64GreaterThanOrEqual _
        | TypedSemanticNodeKind.Float64Add _
        | TypedSemanticNodeKind.Float64Min _
        | TypedSemanticNodeKind.Float64Max _
        | TypedSemanticNodeKind.Float64Subtract _
        | TypedSemanticNodeKind.Float64Multiply _
        | TypedSemanticNodeKind.Float64Divide _
        | TypedSemanticNodeKind.Float64Equal _
        | TypedSemanticNodeKind.Float64LessThan _
        | TypedSemanticNodeKind.Float64IsNaN _
        | TypedSemanticNodeKind.Float64IsFinite _
        | TypedSemanticNodeKind.Float64IsInfinite _
        | TypedSemanticNodeKind.Int64ToFloat64 _
        | TypedSemanticNodeKind.Int64WrappingAbs _
        | TypedSemanticNodeKind.Int64BitNot _
        | TypedSemanticNodeKind.Bits32FromInt64 _
        | TypedSemanticNodeKind.Bits32ToInt64 _
        | TypedSemanticNodeKind.Bits32Not _
        | TypedSemanticNodeKind.Bits32Xor _
        | TypedSemanticNodeKind.Bits32And _
        | TypedSemanticNodeKind.Bits32Or _
        | TypedSemanticNodeKind.Bits32Add _
        | TypedSemanticNodeKind.Bits32RotateRight _
        | TypedSemanticNodeKind.Bits32ShiftRight _
        | TypedSemanticNodeKind.Bits32ShiftLeft _
        | TypedSemanticNodeKind.ListEmpty
        | TypedSemanticNodeKind.SetEmpty
        | TypedSemanticNodeKind.MapEmpty
        | TypedSemanticNodeKind.ListConstruct _
        | TypedSemanticNodeKind.ListLength _
        | TypedSemanticNodeKind.MapValues _
        | TypedSemanticNodeKind.MapKeys _
        | TypedSemanticNodeKind.SetToList _
        | TypedSemanticNodeKind.MapSize _
        | TypedSemanticNodeKind.SetFromList _
        | TypedSemanticNodeKind.SetSize _
        | TypedSemanticNodeKind.ListReverse _
        | TypedSemanticNodeKind.ListLast _
        | TypedSemanticNodeKind.ListDropLast _
        | TypedSemanticNodeKind.ListAppendItem _
        | TypedSemanticNodeKind.ListItemAt _
        | TypedSemanticNodeKind.MapRemove _
        | TypedSemanticNodeKind.SetInsert _
        | TypedSemanticNodeKind.SetRemove _
        | TypedSemanticNodeKind.SetUnion _
        | TypedSemanticNodeKind.SetIntersection _
        | TypedSemanticNodeKind.SetDifference _
        | TypedSemanticNodeKind.SetIsSubset _
        | TypedSemanticNodeKind.MapUnion _
        | TypedSemanticNodeKind.MapContainsKey _
        | TypedSemanticNodeKind.SetContains _
        | TypedSemanticNodeKind.ListIndexOf _
        | TypedSemanticNodeKind.ListLastIndexOf _
        | TypedSemanticNodeKind.MapGet _
        | TypedSemanticNodeKind.MapGetOrDefault _
        | TypedSemanticNodeKind.ListSlice _
        | TypedSemanticNodeKind.MapInsert _
        | TypedSemanticNodeKind.ListConcat _
        | TypedSemanticNodeKind.ListFold _
        | TypedSemanticNodeKind.MapFold _
        | TypedSemanticNodeKind.ParallelAll _
        | TypedSemanticNodeKind.CancellationRequested
        | TypedSemanticNodeKind.ExceptionCatch _
        | TypedSemanticNodeKind.ExceptionIsCancellation _
        | TypedSemanticNodeKind.ExceptionMessage _
        | TypedSemanticNodeKind.ExceptionRethrow _
        | TypedSemanticNodeKind.CancellationShield _
        | TypedSemanticNodeKind.TaskScope _
        | TypedSemanticNodeKind.TaskStart _
        | TypedSemanticNodeKind.TaskWait _
        | TypedSemanticNodeKind.OneShotCreate _
        | TypedSemanticNodeKind.OneShotComplete _
        | TypedSemanticNodeKind.OneShotWait _
        | TypedSemanticNodeKind.TaskCancel _
        | TypedSemanticNodeKind.UseResource _
        | TypedSemanticNodeKind.ListEffectFold _
        | TypedSemanticNodeKind.ListMap _
        | TypedSemanticNodeKind.MapFromList _
        | TypedSemanticNodeKind.ListFilter _
        | TypedSemanticNodeKind.MapFilter _
        | TypedSemanticNodeKind.ListSortBy _
        | TypedSemanticNodeKind.MapMapValues _
        | TypedSemanticNodeKind.ListMinBy _
        | TypedSemanticNodeKind.ListAny _
        | TypedSemanticNodeKind.ListFindIndex _
        | TypedSemanticNodeKind.ListAll _
        | TypedSemanticNodeKind.CapabilityConstruct _
        | TypedSemanticNodeKind.FunctionReference _
        | TypedSemanticNodeKind.FunctionInvoke _
        | TypedSemanticNodeKind.DirectCall _
        | TypedSemanticNodeKind.CapabilityInvoke _
        | TypedSemanticNodeKind.ProductConstruct _
        | TypedSemanticNodeKind.FieldAccess _
        | TypedSemanticNodeKind.ProductUpdate _
        | TypedSemanticNodeKind.UnionConstruct _
        | TypedSemanticNodeKind.OptionNone
        | TypedSemanticNodeKind.OptionSome _
        | TypedSemanticNodeKind.ResultOk _
        | TypedSemanticNodeKind.Abort _
        | TypedSemanticNodeKind.ResultErr _
        | TypedSemanticNodeKind.BoolNot _ -> []

    /// The one exhaustive list of the node IDs a kind holds outside its subgraphs: the operands
    /// dependency order and fingerprints read. Nested graphs add their free references separately.
    let valueReferences (kind: TypedSemanticNodeKind): GraphNodeId list =
        match kind with
        | TypedSemanticNodeKind.ParameterReference _
        | TypedSemanticNodeKind.PatternReference _
        | TypedSemanticNodeKind.StringLiteral _
        | TypedSemanticNodeKind.BoolLiteral _
        | TypedSemanticNodeKind.UnitLiteral
        | TypedSemanticNodeKind.Int64Literal _
        | TypedSemanticNodeKind.Float64Literal _
        | TypedSemanticNodeKind.ListEmpty
        | TypedSemanticNodeKind.SetEmpty
        | TypedSemanticNodeKind.MapEmpty
        | TypedSemanticNodeKind.OptionNone -> []
        | TypedSemanticNodeKind.Int64ToString valueNodeId
        | TypedSemanticNodeKind.Int64ToHexString valueNodeId
        | TypedSemanticNodeKind.StringParseInt64 valueNodeId
        | TypedSemanticNodeKind.StringParseInt64Hex valueNodeId
        | TypedSemanticNodeKind.Float64ToString valueNodeId
        | TypedSemanticNodeKind.StringParseFloat64 valueNodeId
        | TypedSemanticNodeKind.StringCodePoints valueNodeId
        | TypedSemanticNodeKind.StringToLower valueNodeId
        | TypedSemanticNodeKind.StringToUpper valueNodeId
        | TypedSemanticNodeKind.StringTrim valueNodeId
        | TypedSemanticNodeKind.StringTrimStart valueNodeId
        | TypedSemanticNodeKind.StringTrimEnd valueNodeId
        | TypedSemanticNodeKind.StringDecodeUtf8Lossy valueNodeId
        | TypedSemanticNodeKind.Float64RoundToInt64 valueNodeId
        | TypedSemanticNodeKind.Float64FloorToInt64 valueNodeId
        | TypedSemanticNodeKind.Float64Sqrt valueNodeId
        | TypedSemanticNodeKind.Float64Abs valueNodeId
        | TypedSemanticNodeKind.Float64Negate valueNodeId
        | TypedSemanticNodeKind.Float64Floor valueNodeId
        | TypedSemanticNodeKind.Float64Ceiling valueNodeId
        | TypedSemanticNodeKind.Float64Truncate valueNodeId
        | TypedSemanticNodeKind.StringUtf8ByteCount valueNodeId
        | TypedSemanticNodeKind.StringFromCodePoint valueNodeId
        | TypedSemanticNodeKind.StringToCodePoint valueNodeId
        | TypedSemanticNodeKind.StringLength valueNodeId
        | TypedSemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) -> [ valueNodeId; startNodeId; endNodeId ]
        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) -> [ valueNodeId; substringNodeId ]
        | TypedSemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) -> [ valueNodeId; substringNodeId; fromNodeId ]
        | TypedSemanticNodeKind.StringSpan(valueNodeId, setNodeId, boundNodeId)
        | TypedSemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, boundNodeId) -> [ valueNodeId; setNodeId; boundNodeId ]
        | TypedSemanticNodeKind.StringLastIndexOf(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringStartsWith(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringEndsWith(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringSplit(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringSplitLines(valueNodeId, otherNodeId) -> [ valueNodeId; otherNodeId ]
        | TypedSemanticNodeKind.StringReplace(valueNodeId, patternNodeId, replacementNodeId) ->
            [ valueNodeId; patternNodeId; replacementNodeId ]
        | TypedSemanticNodeKind.StringJoin(listNodeId, separatorNodeId) -> [ listNodeId; separatorNodeId ]
        | TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.StringLessThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.ValueEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64Min(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64Max(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64ShiftLeft(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64ShiftRight(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.ListRange(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.StringRepeat(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Round(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Min(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Max(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32And(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32Or(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32Add(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32RotateRight(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32ShiftRight(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32ShiftLeft(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.ListConcat(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.SetUnion(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.SetIntersection(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.SetDifference(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.SetIsSubset(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.MapUnion(leftNodeId, rightNodeId) ->
            [ leftNodeId; rightNodeId ]
        | TypedSemanticNodeKind.Float64IsNaN valueNodeId
        | TypedSemanticNodeKind.Float64IsFinite valueNodeId
        | TypedSemanticNodeKind.Int64ToFloat64 valueNodeId
        | TypedSemanticNodeKind.Int64WrappingAbs valueNodeId
        | TypedSemanticNodeKind.Int64BitNot valueNodeId
        | TypedSemanticNodeKind.Bits32FromInt64 valueNodeId
        | TypedSemanticNodeKind.Bits32ToInt64 valueNodeId
        | TypedSemanticNodeKind.Bits32Not valueNodeId
        | TypedSemanticNodeKind.Float64IsInfinite valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ListConstruct itemNodeIds -> itemNodeIds
        | TypedSemanticNodeKind.ListLength listNodeId -> [ listNodeId ]
        | TypedSemanticNodeKind.MapValues mapNodeId -> [ mapNodeId ]
        | TypedSemanticNodeKind.MapKeys mapNodeId -> [ mapNodeId ]
        | TypedSemanticNodeKind.SetToList mapNodeId -> [ mapNodeId ]
        | TypedSemanticNodeKind.MapSize mapNodeId -> [ mapNodeId ]
        | TypedSemanticNodeKind.SetFromList mapNodeId -> [ mapNodeId ]
        | TypedSemanticNodeKind.SetSize mapNodeId -> [ mapNodeId ]
        | TypedSemanticNodeKind.ListReverse listNodeId -> [ listNodeId ]
        | TypedSemanticNodeKind.ListLast listNodeId -> [ listNodeId ]
        | TypedSemanticNodeKind.ListDropLast listNodeId -> [ listNodeId ]
        | TypedSemanticNodeKind.ListMap(listNodeId, _, context)
        | TypedSemanticNodeKind.MapFromList(listNodeId, _, context)
        | TypedSemanticNodeKind.ListFilter(listNodeId, _, context)
        | TypedSemanticNodeKind.MapFilter(listNodeId, _, context)
        | TypedSemanticNodeKind.ListSortBy(listNodeId, _, context)
        | TypedSemanticNodeKind.MapMapValues(listNodeId, _, context)
        | TypedSemanticNodeKind.ListMinBy(listNodeId, _, context)
        | TypedSemanticNodeKind.ListAny(listNodeId, _, context)
        | TypedSemanticNodeKind.ListFindIndex(listNodeId, _, context)
        | TypedSemanticNodeKind.ListAll(listNodeId, _, context) -> listNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ListFold(listNodeId, seedNodeId, _, context) ->
            listNodeId :: seedNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.MapFold(listNodeId, seedNodeId, _, context) ->
            listNodeId :: seedNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ListEffectFold(listNodeId, seedNodeId, _, context) ->
            listNodeId :: seedNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, _, _, context) ->
            acquireNodeId :: releaseCapabilityNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ParallelAll(sourceListNodeId, _, context) ->
            sourceListNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.CancellationRequested -> []
        | TypedSemanticNodeKind.ExceptionCatch(_, bodyContext, _, handlerContext) -> (bodyContext @ handlerContext) |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.ExceptionIsCancellation value
        | TypedSemanticNodeKind.ExceptionMessage value
        | TypedSemanticNodeKind.ExceptionRethrow(value, _) -> [ value ]
        | TypedSemanticNodeKind.CancellationShield(_, context) -> context |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.TaskScope(_, context) -> context |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.TaskStart(scopeNodeId, _, context, itemNodeId) ->
            scopeNodeId :: itemNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.TaskWait(tasksNodeId, _, limitNodeId) -> tasksNodeId :: Option.toList limitNodeId
        | TypedSemanticNodeKind.OneShotCreate(scope, _) -> [ scope ]
        | TypedSemanticNodeKind.OneShotComplete(cell, value) -> [ cell; value ]
        | TypedSemanticNodeKind.OneShotWait(cell, limit) -> cell :: Option.toList limit
        | TypedSemanticNodeKind.TaskCancel taskNodeId -> [ taskNodeId ]
        | TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | TypedSemanticNodeKind.ListItemAt(listNodeId, indexNodeId) -> [ listNodeId; indexNodeId ]
        | TypedSemanticNodeKind.MapRemove(mapNodeId, keyNodeId) -> [ mapNodeId; keyNodeId ]
        | TypedSemanticNodeKind.SetInsert(mapNodeId, itemNodeId) -> [ mapNodeId; itemNodeId ]
        | TypedSemanticNodeKind.SetRemove(mapNodeId, itemNodeId) -> [ mapNodeId; itemNodeId ]
        | TypedSemanticNodeKind.MapContainsKey(mapNodeId, keyNodeId) -> [ mapNodeId; keyNodeId ]
        | TypedSemanticNodeKind.SetContains(mapNodeId, itemNodeId) -> [ mapNodeId; itemNodeId ]
        | TypedSemanticNodeKind.ListIndexOf(mapNodeId, itemNodeId) -> [ mapNodeId; itemNodeId ]
        | TypedSemanticNodeKind.ListLastIndexOf(mapNodeId, itemNodeId) -> [ mapNodeId; itemNodeId ]
        | TypedSemanticNodeKind.MapGet(mapNodeId, keyNodeId) -> [ mapNodeId; keyNodeId ]
        | TypedSemanticNodeKind.MapGetOrDefault(mapNodeId, keyNodeId, defaultNodeId) -> [ mapNodeId; keyNodeId; defaultNodeId ]
        | TypedSemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) -> [ listNodeId; startNodeId; endNodeId ]
        | TypedSemanticNodeKind.MapInsert(mapNodeId, keyNodeId, valueNodeId) -> [ mapNodeId; keyNodeId; valueNodeId ]
        | TypedSemanticNodeKind.CapabilityConstruct _
        | TypedSemanticNodeKind.FunctionReference _ -> []
        | TypedSemanticNodeKind.FunctionInvoke(value, arguments) -> value :: arguments
        | TypedSemanticNodeKind.DirectCall(_, arguments) -> arguments |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ProductConstruct fields
        | TypedSemanticNodeKind.UnionConstruct(_, fields) -> fields |> List.map (fun field -> field.ValueNodeId)
        | TypedSemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | TypedSemanticNodeKind.ProductUpdate(sourceNodeId, fields) ->
            sourceNodeId :: (fields |> List.map (fun field -> field.ValueNodeId))
        | TypedSemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ResultOk valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.Abort errorNodeId
        | TypedSemanticNodeKind.ResultErr errorNodeId -> [ errorNodeId ]
        | TypedSemanticNodeKind.BoolNot operandNodeId
        | TypedSemanticNodeKind.BoolAnd(operandNodeId, _)
        | TypedSemanticNodeKind.BoolOr(operandNodeId, _) -> [ operandNodeId ]
        | TypedSemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | TypedSemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.Bind(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]

type TypedSemanticFunction =
    { Id: FunctionId
      Parameters: TypedSemanticParameter list
      ResultType: TypeRef
      RequiredEffects: EffectId list
      Body: TypedSemanticGraph }

type TypedSemanticProgram =
    { SourceSnapshotId: SnapshotId
      Types: TypedSemanticType list
      Effects: EffectId list
      Capabilities: TypedSemanticCapability list
      Functions: TypedSemanticFunction list }

[<RequireQualifiedAccess>]
type TypedSemanticIrError =
    | InvalidSnapshot of ValidationIssue list
    | IncompleteSnapshot of ObligationId list
    | MissingNodeType of FunctionId * GraphNodeId

[<RequireQualifiedAccess>]
module internal ResultList =
    /// Maps each item in order, stopping at the first error.
    let traverse (mapper: 'a -> Result<'b, 'error>) (items: 'a list): Result<'b list, 'error> =
        let folder state item =
            match state with
            | Error error -> Error error
            | Ok values -> mapper item |> Result.map (fun value -> value :: values)

        items
        |> List.fold folder (Ok [])
        |> Result.map List.rev

[<RequireQualifiedAccess>]
module TypedSemanticIr =
    let private projectPatternBinding (binding: PatternBinding): TypedSemanticPatternBinding =
        { Id = binding.Id
          Type = binding.Type }

    let private projectFieldValue (binding: FieldValueBinding): TypedSemanticFieldValue =
        { TargetFieldId = binding.TargetFieldId
          ValueNodeId = binding.ValueNodeId }

    let private projectArgument (argument: ArgumentBinding): TypedSemanticArgument =
        { TargetParameterId = argument.TargetParameterId
          ValueNodeId = argument.ValueNodeId }

    let private projectContext (context: ArgumentBinding list) =
        context |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId) |> List.map projectArgument

    let private projectCapabilityArgument
        (argument: CapabilityArgumentBinding)
        : TypedSemanticCapabilityArgument =
        { TargetCapabilityParameterId = argument.TargetCapabilityParameterId
          ValueNodeId = argument.ValueNodeId }

    let rec private projectGraph
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        (resolveType: GraphNodeId -> GraphCompletion option)
        (graph: ValueGraph)
        : Result<TypedSemanticGraph, TypedSemanticIrError> =
        let projectPatternField (binding: PatternFieldBinding): TypedSemanticPatternField =
            { SourceFieldId = binding.SourceFieldId
              Binding = projectPatternBinding binding.Binding }

        let projectUnionArm (arm: UnionMatchArm): Result<TypedSemanticUnionArm, TypedSemanticIrError> =
            projectGraph snapshot functionEntity resolveType arm.Body
            |> Result.map (fun body ->
                { VariantIds = UnionMatchArm.canonicalVariantIds arm
                  FieldBindings =
                    arm.FieldBindings
                    |> List.sortBy (fun binding -> StableId.value binding.SourceFieldId)
                    |> List.map projectPatternField
                  Body = body })

        let projectValueArm (arm: ValueMatchArm): Result<TypedSemanticValueArm, TypedSemanticIrError> =
            projectGraph snapshot functionEntity resolveType arm.Body
            |> Result.map (fun body ->
                { ValueBinding = projectPatternBinding arm.ValueBinding
                  Body = body })

        let projectErrorArm (arm: ErrorMatchArm): Result<TypedSemanticErrorArm, TypedSemanticIrError> =
            projectGraph snapshot functionEntity resolveType arm.Body
            |> Result.map (fun body ->
                { ErrorBinding = projectPatternBinding arm.ErrorBinding
                  Body = body })

        let projectNode (node: SemanticNode): Result<TypedSemanticNode, TypedSemanticIrError> =
            match resolveType node.Id with
            | None -> Error(TypedSemanticIrError.MissingNodeType(functionEntity.Id, node.Id))
            | Some nodeType ->
                let projectedKind: Result<TypedSemanticNodeKind, TypedSemanticIrError> =
                    match node.Kind with
                    | SemanticNodeKind.ParameterReference parameterId ->
                        Ok(TypedSemanticNodeKind.ParameterReference parameterId)
                    | SemanticNodeKind.PatternReference bindingId ->
                        Ok(TypedSemanticNodeKind.PatternReference bindingId)
                    | SemanticNodeKind.StringLiteral value ->
                        Ok(TypedSemanticNodeKind.StringLiteral value)
                    | SemanticNodeKind.BoolLiteral value ->
                        Ok(TypedSemanticNodeKind.BoolLiteral value)
                    | SemanticNodeKind.UnitLiteral ->
                        Ok TypedSemanticNodeKind.UnitLiteral
                    | SemanticNodeKind.Int64Literal value ->
                        Ok(TypedSemanticNodeKind.Int64Literal value)
                    | SemanticNodeKind.Float64Literal bits ->
                        Ok(TypedSemanticNodeKind.Float64Literal bits)
                    | SemanticNodeKind.StringIsEmpty valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringIsEmpty valueNodeId)
                    | SemanticNodeKind.Int64ToString valueNodeId ->
                        Ok(TypedSemanticNodeKind.Int64ToString valueNodeId)
                    | SemanticNodeKind.Int64ToHexString valueNodeId ->
                        Ok(TypedSemanticNodeKind.Int64ToHexString valueNodeId)
                    | SemanticNodeKind.StringParseInt64 valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringParseInt64 valueNodeId)
                    | SemanticNodeKind.StringParseInt64Hex valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringParseInt64Hex valueNodeId)
                    | SemanticNodeKind.Float64ToString valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64ToString valueNodeId)
                    | SemanticNodeKind.StringParseFloat64 valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringParseFloat64 valueNodeId)
                    | SemanticNodeKind.StringCodePoints valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringCodePoints valueNodeId)
                    | SemanticNodeKind.StringToLower valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringToLower valueNodeId)
                    | SemanticNodeKind.StringToUpper valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringToUpper valueNodeId)
                    | SemanticNodeKind.StringDecodeUtf8Lossy valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringDecodeUtf8Lossy valueNodeId)
                    | SemanticNodeKind.Float64RoundToInt64 valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64RoundToInt64 valueNodeId)
                    | SemanticNodeKind.Float64FloorToInt64 valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64FloorToInt64 valueNodeId)
                    | SemanticNodeKind.Float64Sqrt valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64Sqrt valueNodeId)
                    | SemanticNodeKind.Float64Abs valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64Abs valueNodeId)
                    | SemanticNodeKind.Float64Negate valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64Negate valueNodeId)
                    | SemanticNodeKind.Float64Floor valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64Floor valueNodeId)
                    | SemanticNodeKind.Float64Ceiling valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64Ceiling valueNodeId)
                    | SemanticNodeKind.Float64Truncate valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64Truncate valueNodeId)
                    | SemanticNodeKind.StringUtf8ByteCount valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringUtf8ByteCount valueNodeId)
                    | SemanticNodeKind.StringFromCodePoint valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringFromCodePoint valueNodeId)
                    | SemanticNodeKind.StringToCodePoint valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringToCodePoint valueNodeId)
                    | SemanticNodeKind.StringLength valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringLength valueNodeId)
                    | SemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
                        Ok(TypedSemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId))
                    | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                        Ok(TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId))
                    | SemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) ->
                        Ok(TypedSemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId))
                    | SemanticNodeKind.StringSpan(valueNodeId, setNodeId, fromNodeId) ->
                        Ok(TypedSemanticNodeKind.StringSpan(valueNodeId, setNodeId, fromNodeId))
                    | SemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, endNodeId) ->
                        Ok(TypedSemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, endNodeId))
                    | SemanticNodeKind.StringLastIndexOf(valueNodeId, otherNodeId) ->
                        Ok(TypedSemanticNodeKind.StringLastIndexOf(valueNodeId, otherNodeId))
                    | SemanticNodeKind.StringStartsWith(valueNodeId, otherNodeId) ->
                        Ok(TypedSemanticNodeKind.StringStartsWith(valueNodeId, otherNodeId))
                    | SemanticNodeKind.StringEndsWith(valueNodeId, otherNodeId) ->
                        Ok(TypedSemanticNodeKind.StringEndsWith(valueNodeId, otherNodeId))
                    | SemanticNodeKind.StringSplit(valueNodeId, otherNodeId) ->
                        Ok(TypedSemanticNodeKind.StringSplit(valueNodeId, otherNodeId))
                    | SemanticNodeKind.StringSplitLines(valueNodeId, breaksNodeId) ->
                        Ok(TypedSemanticNodeKind.StringSplitLines(valueNodeId, breaksNodeId))
                    | SemanticNodeKind.StringReplace(valueNodeId, patternNodeId, replacementNodeId) ->
                        Ok(TypedSemanticNodeKind.StringReplace(valueNodeId, patternNodeId, replacementNodeId))
                    | SemanticNodeKind.StringTrim valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringTrim valueNodeId)
                    | SemanticNodeKind.StringTrimStart valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringTrimStart valueNodeId)
                    | SemanticNodeKind.StringTrimEnd valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringTrimEnd valueNodeId)
                    | SemanticNodeKind.StringJoin(listNodeId, separatorNodeId) ->
                        Ok(TypedSemanticNodeKind.StringJoin(listNodeId, separatorNodeId))
                    | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId))
                    | SemanticNodeKind.StringLessThan(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.StringLessThan(leftNodeId, rightNodeId))
                    | SemanticNodeKind.ValueEqual(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.ValueEqual(leftNodeId, rightNodeId))
                    | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64Min(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64Min(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64Max(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64Max(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64ShiftLeft(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64ShiftLeft(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64ShiftRight(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64ShiftRight(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId))
                    | SemanticNodeKind.ListRange(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.ListRange(leftNodeId, rightNodeId))
                    | SemanticNodeKind.StringRepeat(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.StringRepeat(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Round(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Round(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64Equal(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Add(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Min(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Min(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Max(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Max(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Divide(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Equal(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64IsNaN valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64IsNaN valueNodeId)
                    | SemanticNodeKind.Float64IsFinite valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64IsFinite valueNodeId)
                    | SemanticNodeKind.Float64IsInfinite valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64IsInfinite valueNodeId)
                    | SemanticNodeKind.Int64ToFloat64 valueNodeId ->
                        Ok(TypedSemanticNodeKind.Int64ToFloat64 valueNodeId)
                    | SemanticNodeKind.Int64WrappingAbs valueNodeId ->
                        Ok(TypedSemanticNodeKind.Int64WrappingAbs valueNodeId)
                    | SemanticNodeKind.Int64BitNot valueNodeId ->
                        Ok(TypedSemanticNodeKind.Int64BitNot valueNodeId)
                    | SemanticNodeKind.Bits32FromInt64 valueNodeId -> Ok(TypedSemanticNodeKind.Bits32FromInt64 valueNodeId)
                    | SemanticNodeKind.Bits32ToInt64 valueNodeId -> Ok(TypedSemanticNodeKind.Bits32ToInt64 valueNodeId)
                    | SemanticNodeKind.Bits32Not valueNodeId -> Ok(TypedSemanticNodeKind.Bits32Not valueNodeId)
                    | SemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId) -> Ok(TypedSemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Bits32And(leftNodeId, rightNodeId) -> Ok(TypedSemanticNodeKind.Bits32And(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Bits32Or(leftNodeId, rightNodeId) -> Ok(TypedSemanticNodeKind.Bits32Or(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Bits32Add(leftNodeId, rightNodeId) -> Ok(TypedSemanticNodeKind.Bits32Add(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Bits32RotateRight(leftNodeId, rightNodeId) -> Ok(TypedSemanticNodeKind.Bits32RotateRight(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Bits32ShiftRight(leftNodeId, rightNodeId) -> Ok(TypedSemanticNodeKind.Bits32ShiftRight(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Bits32ShiftLeft(leftNodeId, rightNodeId) -> Ok(TypedSemanticNodeKind.Bits32ShiftLeft(leftNodeId, rightNodeId))
                    | SemanticNodeKind.ListEmpty _ ->
                        Ok TypedSemanticNodeKind.ListEmpty
                    | SemanticNodeKind.SetEmpty _ ->
                        Ok TypedSemanticNodeKind.SetEmpty
                    | SemanticNodeKind.MapEmpty _ ->
                        Ok TypedSemanticNodeKind.MapEmpty
                    | SemanticNodeKind.ListConstruct(_, itemNodeIds) ->
                        Ok(TypedSemanticNodeKind.ListConstruct itemNodeIds)
                    | SemanticNodeKind.ListLength listNodeId ->
                        Ok(TypedSemanticNodeKind.ListLength listNodeId)
                    | SemanticNodeKind.MapValues mapNodeId ->
                        Ok(TypedSemanticNodeKind.MapValues mapNodeId)
                    | SemanticNodeKind.MapKeys mapNodeId ->
                        Ok(TypedSemanticNodeKind.MapKeys mapNodeId)
                    | SemanticNodeKind.SetToList mapNodeId ->
                        Ok(TypedSemanticNodeKind.SetToList mapNodeId)
                    | SemanticNodeKind.MapSize mapNodeId ->
                        Ok(TypedSemanticNodeKind.MapSize mapNodeId)
                    | SemanticNodeKind.SetFromList mapNodeId ->
                        Ok(TypedSemanticNodeKind.SetFromList mapNodeId)
                    | SemanticNodeKind.SetSize mapNodeId ->
                        Ok(TypedSemanticNodeKind.SetSize mapNodeId)
                    | SemanticNodeKind.ListReverse listNodeId ->
                        Ok(TypedSemanticNodeKind.ListReverse listNodeId)
                    | SemanticNodeKind.ListLast listNodeId ->
                        Ok(TypedSemanticNodeKind.ListLast listNodeId)
                    | SemanticNodeKind.ListDropLast listNodeId ->
                        Ok(TypedSemanticNodeKind.ListDropLast listNodeId)
                    | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId))
                    | SemanticNodeKind.ListItemAt(listNodeId, indexNodeId) ->
                        Ok(TypedSemanticNodeKind.ListItemAt(listNodeId, indexNodeId))
                    | SemanticNodeKind.MapRemove(mapNodeId, keyNodeId) ->
                        Ok(TypedSemanticNodeKind.MapRemove(mapNodeId, keyNodeId))
                    | SemanticNodeKind.SetInsert(mapNodeId, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.SetInsert(mapNodeId, itemNodeId))
                    | SemanticNodeKind.SetRemove(mapNodeId, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.SetRemove(mapNodeId, itemNodeId))
                    | SemanticNodeKind.SetUnion(leftSetNodeId, rightSetNodeId) ->
                        Ok(TypedSemanticNodeKind.SetUnion(leftSetNodeId, rightSetNodeId))
                    | SemanticNodeKind.SetIntersection(leftSetNodeId, rightSetNodeId) ->
                        Ok(TypedSemanticNodeKind.SetIntersection(leftSetNodeId, rightSetNodeId))
                    | SemanticNodeKind.SetDifference(leftSetNodeId, rightSetNodeId) ->
                        Ok(TypedSemanticNodeKind.SetDifference(leftSetNodeId, rightSetNodeId))
                    | SemanticNodeKind.SetIsSubset(leftSetNodeId, rightSetNodeId) ->
                        Ok(TypedSemanticNodeKind.SetIsSubset(leftSetNodeId, rightSetNodeId))
                    | SemanticNodeKind.MapUnion(leftMapNodeId, rightMapNodeId) ->
                        Ok(TypedSemanticNodeKind.MapUnion(leftMapNodeId, rightMapNodeId))
                    | SemanticNodeKind.MapContainsKey(mapNodeId, keyNodeId) ->
                        Ok(TypedSemanticNodeKind.MapContainsKey(mapNodeId, keyNodeId))
                    | SemanticNodeKind.SetContains(mapNodeId, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.SetContains(mapNodeId, itemNodeId))
                    | SemanticNodeKind.ListIndexOf(mapNodeId, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.ListIndexOf(mapNodeId, itemNodeId))
                    | SemanticNodeKind.ListLastIndexOf(mapNodeId, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.ListLastIndexOf(mapNodeId, itemNodeId))
                    | SemanticNodeKind.MapGet(mapNodeId, keyNodeId) ->
                        Ok(TypedSemanticNodeKind.MapGet(mapNodeId, keyNodeId))
                    | SemanticNodeKind.MapGetOrDefault(mapNodeId, keyNodeId, defaultNodeId) ->
                        Ok(TypedSemanticNodeKind.MapGetOrDefault(mapNodeId, keyNodeId, defaultNodeId))
                    | SemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) ->
                        Ok(TypedSemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId))
                    | SemanticNodeKind.MapInsert(mapNodeId, keyNodeId, valueNodeId) ->
                        Ok(TypedSemanticNodeKind.MapInsert(mapNodeId, keyNodeId, valueNodeId))
                    | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
                        Ok(TypedSemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId))
                    | SemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, projectContext context))
                    | SemanticNodeKind.MapFold(sourceMapNodeId, seedNodeId, folderFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.MapFold(sourceMapNodeId, seedNodeId, folderFunctionId, projectContext context))
                    | SemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, projectContext context))
                    | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, projectContext context))
                    | SemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, projectContext context))
                    | SemanticNodeKind.CancellationRequested -> Ok TypedSemanticNodeKind.CancellationRequested
                    | SemanticNodeKind.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
                        Ok(TypedSemanticNodeKind.ExceptionCatch(body, projectContext bodyContext, handler, projectContext handlerContext))
                    | SemanticNodeKind.ExceptionIsCancellation value -> Ok(TypedSemanticNodeKind.ExceptionIsCancellation value)
                    | SemanticNodeKind.ExceptionMessage value -> Ok(TypedSemanticNodeKind.ExceptionMessage value)
                    | SemanticNodeKind.ExceptionRethrow(value, resultType) -> Ok(TypedSemanticNodeKind.ExceptionRethrow(value, resultType))
                    | SemanticNodeKind.CancellationShield(bodyFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.CancellationShield(bodyFunctionId, projectContext context))
                    | SemanticNodeKind.TaskScope(bodyFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.TaskScope(bodyFunctionId, projectContext context))
                    | SemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, context, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, projectContext context, itemNodeId))
                    | SemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId) -> Ok(TypedSemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId))
                    | SemanticNodeKind.OneShotCreate(scope, valueType) -> Ok(TypedSemanticNodeKind.OneShotCreate(scope, valueType))
                    | SemanticNodeKind.OneShotComplete(cell, value) -> Ok(TypedSemanticNodeKind.OneShotComplete(cell, value))
                    | SemanticNodeKind.OneShotWait(cell, limit) -> Ok(TypedSemanticNodeKind.OneShotWait(cell, limit))
                    | SemanticNodeKind.TaskCancel taskNodeId -> Ok(TypedSemanticNodeKind.TaskCancel taskNodeId)
                    | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, projectContext context))
                    | SemanticNodeKind.MapFromList(sourceListNodeId, keyOfFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.MapFromList(sourceListNodeId, keyOfFunctionId, projectContext context))
                    | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, projectContext context))
                    | SemanticNodeKind.MapFilter(sourceMapNodeId, keepFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.MapFilter(sourceMapNodeId, keepFunctionId, projectContext context))
                    | SemanticNodeKind.ListSortBy(sourceListNodeId, lessThanFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListSortBy(sourceListNodeId, lessThanFunctionId, projectContext context))
                    | SemanticNodeKind.MapMapValues(sourceMapNodeId, mapperFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.MapMapValues(sourceMapNodeId, mapperFunctionId, projectContext context))
                    | SemanticNodeKind.ListMinBy(sourceListNodeId, lessThanFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListMinBy(sourceListNodeId, lessThanFunctionId, projectContext context))
                    | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, projectContext context))
                    | SemanticNodeKind.ListFindIndex(sourceListNodeId, predicateFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListFindIndex(sourceListNodeId, predicateFunctionId, projectContext context))
                    | SemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, projectContext context))
                    | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) -> Ok(TypedSemanticNodeKind.CapabilityConstruct(capabilityId, implementations))
                    | SemanticNodeKind.FunctionReference target -> Ok(TypedSemanticNodeKind.FunctionReference target)
                    | SemanticNodeKind.FunctionInvoke(value, arguments) -> Ok(TypedSemanticNodeKind.FunctionInvoke(value, arguments))
                    | SemanticNodeKind.DirectCall(functionId, arguments) ->
                        Ok(
                            TypedSemanticNodeKind.DirectCall(
                                functionId,
                                arguments
                                |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                                |> List.map projectArgument
                            )
                        )
                    | SemanticNodeKind.CapabilityInvoke(capabilityNodeId, operationId, arguments) ->
                        Ok(
                            TypedSemanticNodeKind.CapabilityInvoke(
                                capabilityNodeId,
                                operationId,
                                arguments
                                |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                                |> List.map projectCapabilityArgument
                            )
                        )
                    | SemanticNodeKind.ProductConstruct(_, fields) ->
                        Ok(
                            TypedSemanticNodeKind.ProductConstruct(
                                fields
                                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                                |> List.map projectFieldValue
                            )
                        )
                    | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
                        Ok(TypedSemanticNodeKind.FieldAccess(sourceNodeId, fieldId))
                    | SemanticNodeKind.ProductUpdate(_, sourceNodeId, fields) ->
                        Ok(
                            TypedSemanticNodeKind.ProductUpdate(
                                sourceNodeId,
                                fields
                                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                                |> List.map projectFieldValue
                            )
                        )
                    | SemanticNodeKind.UnionConstruct(variantId, fields) ->
                        Ok(
                            TypedSemanticNodeKind.UnionConstruct(
                                variantId,
                                fields
                                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                                |> List.map projectFieldValue
                            )
                        )
                    | SemanticNodeKind.OptionNone _ ->
                        Ok TypedSemanticNodeKind.OptionNone
                    | SemanticNodeKind.OptionSome valueNodeId ->
                        Ok(TypedSemanticNodeKind.OptionSome valueNodeId)
                    | SemanticNodeKind.ResultOk(_, valueNodeId) ->
                        Ok(TypedSemanticNodeKind.ResultOk valueNodeId)
                    | SemanticNodeKind.Abort errorNodeId -> Ok(TypedSemanticNodeKind.Abort errorNodeId)
                    | SemanticNodeKind.ResultErr(_, errorNodeId) ->
                        Ok(TypedSemanticNodeKind.ResultErr errorNodeId)
                    | SemanticNodeKind.BoolNot operandNodeId ->
                        Ok(TypedSemanticNodeKind.BoolNot operandNodeId)
                    | SemanticNodeKind.BoolAnd(leftNodeId, right) ->
                        projectGraph snapshot functionEntity resolveType right
                        |> Result.map (fun projectedRight -> TypedSemanticNodeKind.BoolAnd(leftNodeId, projectedRight))
                    | SemanticNodeKind.BoolOr(leftNodeId, right) ->
                        projectGraph snapshot functionEntity resolveType right
                        |> Result.map (fun projectedRight -> TypedSemanticNodeKind.BoolOr(leftNodeId, projectedRight))
                    | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                        match
                            projectGraph snapshot functionEntity resolveType whenTrue,
                            projectGraph snapshot functionEntity resolveType whenFalse
                        with
                        | Ok projectedTrue, Ok projectedFalse ->
                            Ok(TypedSemanticNodeKind.Conditional(conditionNodeId, projectedTrue, projectedFalse))
                        | Error error, _
                        | _, Error error -> Error error
                    | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                        ResultList.traverse projectUnionArm arms
                        |> Result.map (fun projectedArms ->
                            TypedSemanticNodeKind.MatchUnion(
                                inputNodeId,
                                projectedArms |> List.sortBy (fun arm -> StableId.value arm.VariantIds.Head)
                            ))
                    | SemanticNodeKind.Bind(inputNodeId, body) ->
                        projectValueArm body
                        |> Result.map (fun projected -> TypedSemanticNodeKind.Bind(inputNodeId, projected))
                    | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                        match
                            projectValueArm someArm,
                            projectGraph snapshot functionEntity resolveType noneBody
                        with
                        | Ok projectedSome, Ok projectedNone ->
                            Ok(TypedSemanticNodeKind.MatchOption(inputNodeId, projectedSome, projectedNone))
                        | Error error, _
                        | _, Error error -> Error error
                    | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                        match projectValueArm okArm, projectErrorArm errArm with
                        | Ok projectedOk, Ok projectedErr ->
                            Ok(TypedSemanticNodeKind.MatchResult(inputNodeId, projectedOk, projectedErr))
                        | Error error, _
                        | _, Error error -> Error error

                projectedKind
                |> Result.map (fun kind ->
                    { OriginNodeId = node.Id
                      Completion = nodeType
                      Kind = kind })

        graph.Nodes
        |> List.sortBy (fun node -> StableId.value node.Id)
        |> ResultList.traverse projectNode
        |> Result.map (fun nodes ->
            { Nodes = nodes
              ResultNodeId = graph.ResultNodeId })

    let private projectType (typeEntity: SemanticType): TypedSemanticType =
        let projectField (field: SemanticField): TypedSemanticField =
            { Id = field.Id
              Type = field.Type }

        let projectVariant (variant: SemanticVariant): TypedSemanticVariant =
            { Id = variant.Id
              Fields =
                variant.Fields
                |> List.sortBy (fun field -> StableId.value field.Id)
                |> List.map projectField }

        let kind =
            match typeEntity.Kind with
            | SemanticTypeKind.Product fields ->
                TypedSemanticTypeKind.Product(
                    fields
                    |> List.sortBy (fun field -> StableId.value field.Id)
                    |> List.map projectField
                )
            | SemanticTypeKind.Union variants ->
                TypedSemanticTypeKind.Union(
                    variants
                    |> List.sortBy (fun variant -> StableId.value variant.Id)
                    |> List.map projectVariant
                )

        { Id = typeEntity.Id
          Kind = kind }

    let private projectCapability (capability: SemanticCapability): TypedSemanticCapability =
        let projectParameter
            (parameter: SemanticCapabilityParameter)
            : TypedSemanticCapabilityParameter =
            { Id = parameter.Id
              Type = parameter.Type }

        let projectOperation
            (operation: SemanticCapabilityOperation)
            : TypedSemanticCapabilityOperation =
            { Id = operation.Id
              Parameters = operation.Parameters |> List.map projectParameter
              ResultType = operation.ResultType
              FailureType = operation.FailureType
              EffectId = operation.EffectId }

        { Id = capability.Id
          Operations =
            capability.Operations
            |> List.sortBy (fun operation -> StableId.value operation.Id)
            |> List.map projectOperation }

    let private projectFunction
        (snapshot: Snapshot)
        (createNodeTypeResolver: SemanticFunction -> GraphNodeId -> GraphCompletion option)
        (functionEntity: SemanticFunction)
        : Result<TypedSemanticFunction, TypedSemanticIrError> =
        let resolveType = createNodeTypeResolver functionEntity

        projectGraph snapshot functionEntity resolveType functionEntity.Body
        |> Result.map (fun body ->
            { Id = functionEntity.Id
              Parameters =
                functionEntity.Parameters
                |> List.map (fun parameter ->
                    { Id = parameter.Id
                      Type = parameter.Type })
              ResultType = functionEntity.ResultType
              RequiredEffects = functionEntity.RequiredEffects |> List.sortBy StableId.value
              Body = body })

    let project (snapshot: Snapshot): Result<TypedSemanticProgram, TypedSemanticIrError> =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> Error(TypedSemanticIrError.InvalidSnapshot issues)
        | Ok(SemanticReadiness.Incomplete obligationIds) ->
            Error(TypedSemanticIrError.IncompleteSnapshot obligationIds)
        | Ok SemanticReadiness.Complete ->
            let createNodeTypeResolver = SemanticFacts.createNodeCompletionResolverFactory snapshot
            snapshot.Functions
            |> List.sortBy (fun functionEntity -> StableId.value functionEntity.Id)
            |> ResultList.traverse (projectFunction snapshot createNodeTypeResolver)
            |> Result.map (fun functions ->
                { SourceSnapshotId = snapshot.Id
                  Types =
                    snapshot.Types
                    |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)
                    |> List.map projectType
                  Effects = snapshot.Effects |> List.map (fun effect -> effect.Id) |> List.sortBy StableId.value
                  Capabilities =
                    snapshot.Capabilities
                    |> List.sortBy (fun capability -> StableId.value capability.Id)
                    |> List.map projectCapability
                  Functions = functions })
