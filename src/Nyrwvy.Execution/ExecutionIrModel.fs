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
    | StringParseInt64 of ExecutionValueId
    | StringLength of ExecutionValueId
    | BoolNot of ExecutionValueId
    | StringSlice of valueId: ExecutionValueId * startId: ExecutionValueId * endId: ExecutionValueId
    | StringContains of valueId: ExecutionValueId * substringId: ExecutionValueId
    | StringEqual of leftId: ExecutionValueId * rightId: ExecutionValueId
    /// Structural equality of two values of one equatable type.
    | ValueEqual of leftId: ExecutionValueId * rightId: ExecutionValueId
    | StringConcat of leftId: ExecutionValueId * rightId: ExecutionValueId
    | Int64WrappingAdd of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64WrappingSubtract of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64WrappingMultiply of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64FloorDivide of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
    | Int64FloorModulo of leftValueId: ExecutionValueId * rightValueId: ExecutionValueId
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
    | ListEmpty
    | ListConstruct of ExecutionValueId list
    | ListLength of listValueId: ExecutionValueId
    | ListAppendItem of listValueId: ExecutionValueId * itemValueId: ExecutionValueId
    | ListItemAt of listValueId: ExecutionValueId * indexValueId: ExecutionValueId
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
    | UnionConstruct of VariantId * ExecutionFieldValue list
    | OptionNone
    | OptionSome of ExecutionValueId
    | ResultOk of ExecutionValueId
    | ResultErr of ExecutionValueId
    | ExtractUnionField of inputValueId: ExecutionValueId * VariantId * FieldId
    | ExtractOptionSome of inputValueId: ExecutionValueId
    | ExtractResultOk of inputValueId: ExecutionValueId
    | ExtractResultErr of inputValueId: ExecutionValueId

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
