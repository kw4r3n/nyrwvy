namespace Nyrwvy.Core

[<Struct; StructuralEquality; StructuralComparison>]
type ExecutionValueId = internal ExecutionValueId of string

[<Struct; StructuralEquality; StructuralComparison>]
type ExecutionBlockId = internal ExecutionBlockId of string

[<RequireQualifiedAccess>]
module ExecutionValueId =
    let value (ExecutionValueId value) = value

[<RequireQualifiedAccess>]
module ExecutionBlockId =
    let value (ExecutionBlockId value) = value

[<RequireQualifiedAccess>]
type ExecutionOrigin =
    | FunctionParameter of ParameterId
    | SemanticNode of GraphNodeId
    | PatternBinding of matchNodeId: GraphNodeId * PatternBindingId

type ExecutionValue =
    { Id: ExecutionValueId
      Type: TypeRef
      Origin: ExecutionOrigin }

type ExecutionParameter =
    { SemanticParameterId: ParameterId
      Value: ExecutionValue }

type ExecutionField =
    { Id: FieldId
      Type: TypeRef }

type ExecutionVariant =
    { Id: VariantId
      Fields: ExecutionField list }

[<RequireQualifiedAccess>]
type ExecutionTypeKind =
    | Product of ExecutionField list
    | Union of ExecutionVariant list

type ExecutionType =
    { Id: TypeId
      Kind: ExecutionTypeKind }

type ExecutionCapabilityParameter =
    { Id: CapabilityParameterId
      Type: TypeRef }

type ExecutionCapabilityOperation =
    { Id: CapabilityOperationId
      Parameters: ExecutionCapabilityParameter list
      ResultType: TypeRef
      FailureType: TypeRef option
      EffectId: EffectId }

type ExecutionCapability =
    { Id: CapabilityId
      Operations: ExecutionCapabilityOperation list }

type ExecutionCallArgument =
    { TargetParameterId: ParameterId
      ValueId: ExecutionValueId }

type ExecutionCapabilityArgument =
    { TargetCapabilityParameterId: CapabilityParameterId
      ValueId: ExecutionValueId }

type ExecutionFieldValue =
    { TargetFieldId: FieldId
      ValueId: ExecutionValueId }

[<RequireQualifiedAccess>]
type ExecutionInstructionKind =
    | ReadParameter of ExecutionValueId
    | ReadPatternBinding of ExecutionValueId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of ExecutionValueId
    | Int64ToString of ExecutionValueId
    | Int64ToHexString of ExecutionValueId
    | StringParseInt64 of ExecutionValueId
    | StringParseInt64Hex of ExecutionValueId
    | Float64ToString of ExecutionValueId
    | StringParseFloat64 of ExecutionValueId
    | StringCodePoints of ExecutionValueId
    | StringToLower of ExecutionValueId
    | StringToUpper of ExecutionValueId
    | StringDecodeUtf8Lossy of ExecutionValueId
    | Float64RoundToInt64 of ExecutionValueId
    | Float64FloorToInt64 of ExecutionValueId
    | Float64Sqrt of ExecutionValueId
    | Float64Abs of ExecutionValueId
    | Float64Negate of ExecutionValueId
    | Float64Floor of ExecutionValueId
    | Float64Ceiling of ExecutionValueId
    | Float64Truncate of ExecutionValueId
    | StringUtf8ByteCount of ExecutionValueId
    | StringFromCodePoint of ExecutionValueId
    | StringToCodePoint of ExecutionValueId
    | StringLength of ExecutionValueId
    | BoolNot of ExecutionValueId
    | StringSlice of valueId: ExecutionValueId * startId: ExecutionValueId * endId: ExecutionValueId
    | StringContains of valueId: ExecutionValueId * substringId: ExecutionValueId
    | StringIndexOf of valueId: ExecutionValueId * substringId: ExecutionValueId * fromId: ExecutionValueId
    /// Code-point position of the first code point at or after `from` (clamped) not in `set`.
    | StringSpan of valueId: ExecutionValueId * setId: ExecutionValueId * fromId: ExecutionValueId
    /// Least code-point position p <= `end` (clamped) with every code point in [p, end) in `set`.
    | StringSpanBack of valueId: ExecutionValueId * setId: ExecutionValueId * endId: ExecutionValueId
    | StringLastIndexOf of valueId: ExecutionValueId * substringId: ExecutionValueId
    | StringStartsWith of valueId: ExecutionValueId * prefixId: ExecutionValueId
    | StringEndsWith of valueId: ExecutionValueId * suffixId: ExecutionValueId
    | StringSplit of valueId: ExecutionValueId * separatorId: ExecutionValueId
    /// Lines of `value` split at code points of `breaks` ("\r\n" is one break when both are members).
    | StringSplitLines of valueId: ExecutionValueId * breaksId: ExecutionValueId
    | StringJoin of listValueId: ExecutionValueId * separatorId: ExecutionValueId
    | StringEqual of leftId: ExecutionValueId * rightId: ExecutionValueId
    /// Ordinal code-point ordering of two Strings: left sorts strictly before right.
    | StringLessThan of leftId: ExecutionValueId * rightId: ExecutionValueId
    /// Structural equality of two values of one equatable type.
    | ValueEqual of leftId: ExecutionValueId * rightId: ExecutionValueId
    | StringConcat of leftId: ExecutionValueId * rightId: ExecutionValueId
    | Int64WrappingAdd of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64WrappingSubtract of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64WrappingMultiply of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64FloorDivide of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64FloorModulo of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64Min of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64Max of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64CheckedAdd of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64CheckedSubtract of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64CheckedMultiply of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | ListRange of startValueId: ExecutionValueId * endValueId: ExecutionValueId
    | StringRepeat of textValueId: ExecutionValueId * countValueId: ExecutionValueId
    | Float64Round of valueId: ExecutionValueId * digitsValueId: ExecutionValueId
    | Int64Equal of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64LessThan of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64LessThanOrEqual of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64GreaterThan of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64GreaterThanOrEqual of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Float64Add of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Float64Subtract of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Float64Multiply of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Float64Divide of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Float64Equal of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Float64LessThan of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Float64IsNaN of valueId: ExecutionValueId
    | Float64IsFinite of valueId: ExecutionValueId
    | Float64IsInfinite of valueId: ExecutionValueId
    | Int64ToFloat64 of valueId: ExecutionValueId
    | Int64WrappingAbs of valueId: ExecutionValueId
    | Bits32FromInt64 of valueId: ExecutionValueId
    | Bits32ToInt64 of valueId: ExecutionValueId
    | Bits32Not of valueId: ExecutionValueId
    | Bits32Xor of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Bits32And of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Bits32Or of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Bits32Add of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Bits32RotateRight of valueId: ExecutionValueId * amountValueId: ExecutionValueId
    | Bits32ShiftRight of valueId: ExecutionValueId * amountValueId: ExecutionValueId
    | Bits32ShiftLeft of valueId: ExecutionValueId * amountValueId: ExecutionValueId
    | ListEmpty
    | ListConstruct of ExecutionValueId list
    | ListLength of listValueId: ExecutionValueId
    | ListReverse of listValueId: ExecutionValueId
    | ListAppendItem of listValueId: ExecutionValueId * itemValueId: ExecutionValueId
    | ListItemAt of listValueId: ExecutionValueId * indexValueId: ExecutionValueId
    | ListSlice of listValueId: ExecutionValueId * startId: ExecutionValueId * endId: ExecutionValueId
    | ListConcat of leftListValueId: ExecutionValueId * rightListValueId: ExecutionValueId
    /// `context` binds callback parameters to values computed before iteration.
    | ListFold of sourceListValueId: ExecutionValueId * seedValueId: ExecutionValueId * folderFunctionId: FunctionId * context: ExecutionCallArgument list
    | ParallelAll of sourceListValueId: ExecutionValueId * workerFunctionId: FunctionId * context: ExecutionCallArgument list
    | CancellationRequested
    | TaskScope of bodyFunctionId: FunctionId * context: ExecutionCallArgument list
    | TaskStart of scopeValueId: ExecutionValueId * workerFunctionId: FunctionId * context: ExecutionCallArgument list * itemValueId: ExecutionValueId
    | TaskWait of tasksValueId: ExecutionValueId * mode: TaskWaitMode * limitValueId: ExecutionValueId option
    | TaskCancel of taskValueId: ExecutionValueId
    | UseResource of acquireValueId: ExecutionValueId * releaseCapabilityValueId: ExecutionValueId * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: ExecutionCallArgument list
    | ListEffectFold of sourceListValueId: ExecutionValueId * seedValueId: ExecutionValueId * folderFunctionId: FunctionId * context: ExecutionCallArgument list
    | ListMap of sourceListValueId: ExecutionValueId * mapperFunctionId: FunctionId * context: ExecutionCallArgument list
    | ListFilter of sourceListValueId: ExecutionValueId * predicateFunctionId: FunctionId * context: ExecutionCallArgument list
    /// Stable top-down merge sort under a pure less-than callback (see SemanticNodeKind.ListSortBy).
    | ListSortBy of sourceListValueId: ExecutionValueId * lessThanFunctionId: FunctionId * context: ExecutionCallArgument list
    | ListAny of sourceListValueId: ExecutionValueId * predicateFunctionId: FunctionId * context: ExecutionCallArgument list
    | ListAll of sourceListValueId: ExecutionValueId * predicateFunctionId: FunctionId * context: ExecutionCallArgument list
    /// Construct a stateless capability using named pure functions, one per operation.
    | CapabilityConstruct of CapabilityId * implementations: (CapabilityOperationId * FunctionId) list
    | FunctionReference of FunctionId
    | FunctionInvoke of ExecutionValueId * ExecutionValueId list
    | DirectCall of FunctionId * ExecutionCallArgument list
    | CapabilityInvoke of capabilityValueId: ExecutionValueId * CapabilityOperationId * ExecutionCapabilityArgument list
    | ProductConstruct of ExecutionFieldValue list
    | FieldAccess of sourceValueId: ExecutionValueId * FieldId
    /// A copy of the source product with the bound fields replaced; the result type is the source's.
    | ProductUpdate of sourceValueId: ExecutionValueId * ExecutionFieldValue list
    | UnionConstruct of VariantId * ExecutionFieldValue list
    | OptionNone
    | OptionSome of ExecutionValueId
    | ResultOk of ExecutionValueId
    | ResultErr of ExecutionValueId
    | ExtractUnionField of inputValueId: ExecutionValueId * VariantId * FieldId
    | ExtractOptionSome of inputValueId: ExecutionValueId
    | ExtractResultOk of inputValueId: ExecutionValueId
    | ExtractResultErr of inputValueId: ExecutionValueId

/// What an instruction needs from the program and the generated runtime besides its operand values.
[<RequireQualifiedAccess>]
type ExecutionInstructionUse =
    /// A function the instruction names, in the role the matching semantic node gives it.
    | Callback of FunctionReferenceRole * FunctionId
    /// A capability operation the instruction invokes, which needs a host binding.
    | CapabilityOperation of CapabilityOperationId
    /// A generated implementation class for the capability, one method per implemented operation
    /// (CapabilityConstruct; the same functions are also listed as `Callback`s).
    | CapabilityImplementationClass of CapabilityId * implementations: (CapabilityOperationId * FunctionId) list
    /// The ambient cancellation runtime (ParallelAll workers, CancellationRequested).
    | ConcurrencyRuntime
    /// The task runtime (TaskScope, TaskStart, TaskWait, TaskCancel).
    | TaskRuntime

[<RequireQualifiedAccess>]
module ExecutionInstructionKind =
    /// The one exhaustive list of what each instruction kind needs beyond its operand values. Call
    /// reachability, capability binding validation and the runtime the C# backend emits all derive
    /// from it, so a new kind is classified here once.
    let uses (kind: ExecutionInstructionKind): ExecutionInstructionUse list =
        let callback role functionId = ExecutionInstructionUse.Callback(role, functionId)
        match kind with
        | ExecutionInstructionKind.CapabilityConstruct(capabilityId, implementations) ->
            ExecutionInstructionUse.CapabilityImplementationClass(capabilityId, implementations)
            :: (implementations
                |> List.map (fun (operationId, functionId) ->
                    callback (FunctionReferenceRole.CapabilityImplementation operationId) functionId))
        | ExecutionInstructionKind.FunctionReference functionId -> [ callback FunctionReferenceRole.FunctionValue functionId ]
        | ExecutionInstructionKind.DirectCall(functionId, _) -> [ callback FunctionReferenceRole.DirectCall functionId ]
        | ExecutionInstructionKind.ListFold(_, _, functionId, _)
        | ExecutionInstructionKind.ListMap(_, functionId, _)
        | ExecutionInstructionKind.ListFilter(_, functionId, _)
        | ExecutionInstructionKind.ListSortBy(_, functionId, _)
        | ExecutionInstructionKind.ListAny(_, functionId, _)
        | ExecutionInstructionKind.ListAll(_, functionId, _) -> [ callback FunctionReferenceRole.PureListCallback functionId ]
        | ExecutionInstructionKind.ListEffectFold(_, _, functionId, _) -> [ callback FunctionReferenceRole.EffectListCallback functionId ]
        | ExecutionInstructionKind.ParallelAll(_, functionId, _) ->
            [ callback FunctionReferenceRole.Worker functionId; ExecutionInstructionUse.ConcurrencyRuntime ]
        | ExecutionInstructionKind.CancellationRequested -> [ ExecutionInstructionUse.ConcurrencyRuntime ]
        | ExecutionInstructionKind.TaskScope(functionId, _) ->
            [ callback FunctionReferenceRole.Body functionId; ExecutionInstructionUse.TaskRuntime ]
        | ExecutionInstructionKind.TaskStart(_, functionId, _, _) ->
            [ callback FunctionReferenceRole.Worker functionId; ExecutionInstructionUse.TaskRuntime ]
        | ExecutionInstructionKind.TaskWait _
        | ExecutionInstructionKind.TaskCancel _ -> [ ExecutionInstructionUse.TaskRuntime ]
        | ExecutionInstructionKind.UseResource(_, _, releaseOperationId, functionId, _) ->
            [ callback FunctionReferenceRole.Body functionId; ExecutionInstructionUse.CapabilityOperation releaseOperationId ]
        | ExecutionInstructionKind.CapabilityInvoke(_, operationId, _) -> [ ExecutionInstructionUse.CapabilityOperation operationId ]
        | ExecutionInstructionKind.ReadParameter _
        | ExecutionInstructionKind.ReadPatternBinding _
        | ExecutionInstructionKind.StringLiteral _
        | ExecutionInstructionKind.BoolLiteral _
        | ExecutionInstructionKind.UnitLiteral
        | ExecutionInstructionKind.Int64Literal _
        | ExecutionInstructionKind.Float64Literal _
        | ExecutionInstructionKind.StringIsEmpty _
        | ExecutionInstructionKind.Int64ToString _
        | ExecutionInstructionKind.Int64ToHexString _
        | ExecutionInstructionKind.StringParseInt64 _
        | ExecutionInstructionKind.StringParseInt64Hex _
        | ExecutionInstructionKind.Float64ToString _
        | ExecutionInstructionKind.StringParseFloat64 _
        | ExecutionInstructionKind.StringCodePoints _
        | ExecutionInstructionKind.StringToLower _
        | ExecutionInstructionKind.StringToUpper _
        | ExecutionInstructionKind.StringDecodeUtf8Lossy _
        | ExecutionInstructionKind.Float64RoundToInt64 _
        | ExecutionInstructionKind.Float64FloorToInt64 _
        | ExecutionInstructionKind.Float64Sqrt _
        | ExecutionInstructionKind.Float64Abs _
        | ExecutionInstructionKind.Float64Negate _
        | ExecutionInstructionKind.Float64Floor _
        | ExecutionInstructionKind.Float64Ceiling _
        | ExecutionInstructionKind.Float64Truncate _
        | ExecutionInstructionKind.StringUtf8ByteCount _
        | ExecutionInstructionKind.StringFromCodePoint _
        | ExecutionInstructionKind.StringToCodePoint _
        | ExecutionInstructionKind.StringLength _
        | ExecutionInstructionKind.BoolNot _
        | ExecutionInstructionKind.StringSlice _
        | ExecutionInstructionKind.StringContains _
        | ExecutionInstructionKind.StringIndexOf _
        | ExecutionInstructionKind.StringSpan _
        | ExecutionInstructionKind.StringSpanBack _
        | ExecutionInstructionKind.StringLastIndexOf _
        | ExecutionInstructionKind.StringStartsWith _
        | ExecutionInstructionKind.StringEndsWith _
        | ExecutionInstructionKind.StringSplit _
        | ExecutionInstructionKind.StringSplitLines _
        | ExecutionInstructionKind.StringJoin _
        | ExecutionInstructionKind.StringEqual _
        | ExecutionInstructionKind.StringLessThan _
        | ExecutionInstructionKind.ValueEqual _
        | ExecutionInstructionKind.StringConcat _
        | ExecutionInstructionKind.Int64WrappingAdd _
        | ExecutionInstructionKind.Int64WrappingSubtract _
        | ExecutionInstructionKind.Int64WrappingMultiply _
        | ExecutionInstructionKind.Int64FloorDivide _
        | ExecutionInstructionKind.Int64FloorModulo _
        | ExecutionInstructionKind.Int64Min _
        | ExecutionInstructionKind.Int64Max _
        | ExecutionInstructionKind.Int64CheckedAdd _
        | ExecutionInstructionKind.Int64CheckedSubtract _
        | ExecutionInstructionKind.Int64CheckedMultiply _
        | ExecutionInstructionKind.ListRange _
        | ExecutionInstructionKind.StringRepeat _
        | ExecutionInstructionKind.Float64Round _
        | ExecutionInstructionKind.Int64Equal _
        | ExecutionInstructionKind.Int64LessThan _
        | ExecutionInstructionKind.Int64LessThanOrEqual _
        | ExecutionInstructionKind.Int64GreaterThan _
        | ExecutionInstructionKind.Int64GreaterThanOrEqual _
        | ExecutionInstructionKind.Float64Add _
        | ExecutionInstructionKind.Float64Subtract _
        | ExecutionInstructionKind.Float64Multiply _
        | ExecutionInstructionKind.Float64Divide _
        | ExecutionInstructionKind.Float64Equal _
        | ExecutionInstructionKind.Float64LessThan _
        | ExecutionInstructionKind.Float64IsNaN _
        | ExecutionInstructionKind.Float64IsFinite _
        | ExecutionInstructionKind.Float64IsInfinite _
        | ExecutionInstructionKind.Int64ToFloat64 _
        | ExecutionInstructionKind.Int64WrappingAbs _
        | ExecutionInstructionKind.Bits32FromInt64 _
        | ExecutionInstructionKind.Bits32ToInt64 _
        | ExecutionInstructionKind.Bits32Not _
        | ExecutionInstructionKind.Bits32Xor _
        | ExecutionInstructionKind.Bits32And _
        | ExecutionInstructionKind.Bits32Or _
        | ExecutionInstructionKind.Bits32Add _
        | ExecutionInstructionKind.Bits32RotateRight _
        | ExecutionInstructionKind.Bits32ShiftRight _
        | ExecutionInstructionKind.Bits32ShiftLeft _
        | ExecutionInstructionKind.ListEmpty
        | ExecutionInstructionKind.ListConstruct _
        | ExecutionInstructionKind.ListLength _
        | ExecutionInstructionKind.ListReverse _
        | ExecutionInstructionKind.ListAppendItem _
        | ExecutionInstructionKind.ListItemAt _
        | ExecutionInstructionKind.ListSlice _
        | ExecutionInstructionKind.ListConcat _
        | ExecutionInstructionKind.FunctionInvoke _
        | ExecutionInstructionKind.ProductConstruct _
        | ExecutionInstructionKind.FieldAccess _
        | ExecutionInstructionKind.ProductUpdate _
        | ExecutionInstructionKind.UnionConstruct _
        | ExecutionInstructionKind.OptionNone
        | ExecutionInstructionKind.OptionSome _
        | ExecutionInstructionKind.ResultOk _
        | ExecutionInstructionKind.ResultErr _
        | ExecutionInstructionKind.ExtractUnionField _
        | ExecutionInstructionKind.ExtractOptionSome _
        | ExecutionInstructionKind.ExtractResultOk _
        | ExecutionInstructionKind.ExtractResultErr _ -> []

    /// Every function the instruction names, with its role, in payload order.
    let callbackTargets (kind: ExecutionInstructionKind): (FunctionReferenceRole * FunctionId) list =
        uses kind
        |> List.choose (fun used ->
            match used with
            | ExecutionInstructionUse.Callback(role, functionId) -> Some(role, functionId)
            | ExecutionInstructionUse.CapabilityOperation _
            | ExecutionInstructionUse.CapabilityImplementationClass _
            | ExecutionInstructionUse.ConcurrencyRuntime
            | ExecutionInstructionUse.TaskRuntime -> None)

    /// The capability operations the instruction invokes.
    let capabilityOperations (kind: ExecutionInstructionKind): CapabilityOperationId list =
        uses kind
        |> List.choose (fun used ->
            match used with
            | ExecutionInstructionUse.CapabilityOperation operationId -> Some operationId
            | ExecutionInstructionUse.Callback _
            | ExecutionInstructionUse.CapabilityImplementationClass _
            | ExecutionInstructionUse.ConcurrencyRuntime
            | ExecutionInstructionUse.TaskRuntime -> None)

type ExecutionInstruction =
    { Result: ExecutionValue
      OriginNodeId: GraphNodeId
      Kind: ExecutionInstructionKind }

type ExecutionJumpArgument =
    { TargetValueId: ExecutionValueId
      ValueId: ExecutionValueId }

type ExecutionUnionTarget =
    { VariantId: VariantId
      BlockId: ExecutionBlockId }

[<RequireQualifiedAccess>]
type ExecutionTerminator =
    | Abort of originNodeId: GraphNodeId * errorValueId: ExecutionValueId
    | Return of ExecutionValueId
    | Jump of ExecutionBlockId * ExecutionJumpArgument list
    | Branch of conditionValueId: ExecutionValueId * whenTrue: ExecutionBlockId * whenFalse: ExecutionBlockId
    | MatchUnion of inputValueId: ExecutionValueId * ExecutionUnionTarget list
    | MatchOption of inputValueId: ExecutionValueId * someBlock: ExecutionBlockId * noneBlock: ExecutionBlockId
    | MatchResult of inputValueId: ExecutionValueId * okBlock: ExecutionBlockId * errBlock: ExecutionBlockId

type ExecutionBlock =
    { Id: ExecutionBlockId
      Parameters: ExecutionValue list
      Instructions: ExecutionInstruction list
      Terminator: ExecutionTerminator }

type ExecutionFunction =
    { SemanticFunctionId: FunctionId
      Parameters: ExecutionParameter list
      ResultType: TypeRef
      RequiredEffects: EffectId list
      EntryBlockId: ExecutionBlockId
      Blocks: ExecutionBlock list }

type ExecutionProgram =
    { SourceSnapshotId: SnapshotId
      Types: ExecutionType list
      Effects: EffectId list
      Capabilities: ExecutionCapability list
      Functions: ExecutionFunction list }

[<RequireQualifiedAccess>]
type ExecutionIrError =
    | MissingNodeValue of FunctionId * GraphNodeId
    | MissingPatternValue of FunctionId * PatternBindingId
    | MissingFunctionParameter of FunctionId * ParameterId
    | UnresolvedDependencies of FunctionId * GraphNodeId list
    | AmbiguousEffectOrder of FunctionId * GraphNodeId * GraphNodeId
    | UnterminatedBlock of FunctionId * ExecutionBlockId
