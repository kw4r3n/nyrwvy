namespace Nayamnyam.Core

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
    | ReadParameter of ParameterId
    | ReadPatternBinding of ExecutionValueId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | StringIsEmpty of ExecutionValueId
    | StringContains of valueId: ExecutionValueId * substringId: ExecutionValueId
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
