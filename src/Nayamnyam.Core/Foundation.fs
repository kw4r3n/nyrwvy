namespace Nayamnyam.Core

/// Semantic facts are intentionally explicit and locally inspectable. A node kind
/// should carry enough information that an agent can understand its role from the
/// node plus directly referenced declarations, without relying on display names,
/// serialized order, or a second representation of the same fact.
[<RequireQualifiedAccess>]
type PrimitiveType =
    | String
    | Bool
    | Unit
    | Int64

[<RequireQualifiedAccess>]
type BuiltinGenericType =
    | Option
    | Result
    | List

[<RequireQualifiedAccess>]
type TypeRef =
    | Primitive of PrimitiveType
    | Named of TypeId
    | Capability of CapabilityId
    | BuiltinGeneric of BuiltinGenericType * TypeRef list

type SemanticField =
    { Id: FieldId
      DisplayName: string
      Type: TypeRef }

type SemanticVariant =
    { Id: VariantId
      DisplayName: string
      Fields: SemanticField list }

[<RequireQualifiedAccess>]
type SemanticTypeKind =
    | Product of SemanticField list
    | Union of SemanticVariant list

type SemanticType =
    { Id: TypeId
      DisplayName: string
      Kind: SemanticTypeKind }

type SemanticEffect =
    { Id: EffectId
      DisplayName: string }

type SemanticCapabilityParameter =
    { Id: CapabilityParameterId
      DisplayName: string
      Type: TypeRef }

type SemanticCapabilityOperation =
    { Id: CapabilityOperationId
      DisplayName: string
      Parameters: SemanticCapabilityParameter list
      ResultType: TypeRef
      FailureType: TypeRef option
      EffectId: EffectId }

type SemanticCapability =
    { Id: CapabilityId
      DisplayName: string
      Operations: SemanticCapabilityOperation list }

type SemanticParameter =
    { Id: ParameterId
      DisplayName: string
      Type: TypeRef }

type ArgumentBinding =
    { TargetParameterId: ParameterId
      ValueNodeId: GraphNodeId }

type CapabilityArgumentBinding =
    { TargetCapabilityParameterId: CapabilityParameterId
      ValueNodeId: GraphNodeId }

type FieldValueBinding =
    { TargetFieldId: FieldId
      ValueNodeId: GraphNodeId }

type PatternBinding =
    { Id: PatternBindingId
      DisplayName: string option
      Type: TypeRef }

type PatternFieldBinding =
    { SourceFieldId: FieldId
      Binding: PatternBinding }

type UnionMatchArm =
    { VariantId: VariantId
      FieldBindings: PatternFieldBinding list
      Body: ValueGraph }

/// Option.Some and Result.Ok intentionally share one semantic shape instead of
/// duplicating near-identical record schemas. Their parent node kind supplies the
/// role; the arm itself represents only "bind one produced value, then evaluate".
and ValueMatchArm =
    { ValueBinding: PatternBinding
      Body: ValueGraph }

/// Result.Err is structurally distinct because the binding has an error role.
and ErrorMatchArm =
    { ErrorBinding: PatternBinding
      Body: ValueGraph }

and [<RequireQualifiedAccess>] SemanticNodeKind =
    | ParameterReference of ParameterId
    | PatternReference of PatternBindingId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | StringIsEmpty of valueNodeId: GraphNodeId
    | StringContains of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    | Int64WrappingAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | ListEmpty of itemType: TypeRef
    | ListConstruct of itemType: TypeRef * itemNodeIds: GraphNodeId list
    | ListLength of listNodeId: GraphNodeId
    | ListAppendItem of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | ListConcat of leftListNodeId: GraphNodeId * rightListNodeId: GraphNodeId
    | ListMap of sourceListNodeId: GraphNodeId * mapperFunctionId: FunctionId
    | ListFilter of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId
    | ListAny of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId
    | ListAll of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId
    | DirectCall of FunctionId * ArgumentBinding list
    | CapabilityInvoke of capabilityValueNodeId: GraphNodeId * operationId: CapabilityOperationId * arguments: CapabilityArgumentBinding list
    | ProductConstruct of TypeId * FieldValueBinding list
    | FieldAccess of GraphNodeId * FieldId
    | UnionConstruct of VariantId * FieldValueBinding list
    | OptionNone of itemType: TypeRef
    | OptionSome of valueNodeId: GraphNodeId
    | ResultOk of errorType: TypeRef * valueNodeId: GraphNodeId
    | ResultErr of okType: TypeRef * errorNodeId: GraphNodeId
    | Conditional of conditionNodeId: GraphNodeId * whenTrue: ValueGraph * whenFalse: ValueGraph
    | MatchUnion of inputNodeId: GraphNodeId * arms: UnionMatchArm list
    | MatchOption of inputNodeId: GraphNodeId * someArm: ValueMatchArm * noneBody: ValueGraph
    | MatchResult of inputNodeId: GraphNodeId * okArm: ValueMatchArm * errArm: ErrorMatchArm

and SemanticNode =
    { Id: GraphNodeId
      Kind: SemanticNodeKind }

and ValueGraph =
    { Nodes: SemanticNode list
      ResultNodeId: GraphNodeId }

type SemanticFunction =
    { Id: FunctionId
      DisplayName: string
      Parameters: SemanticParameter list
      ResultType: TypeRef
      RequiredEffects: EffectId list
      Body: ValueGraph }

type ObligationResolution =
    { TransactionId: TransactionId
      ValueNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
type ObligationStatus =
    | Open
    | Resolved of ObligationResolution

type MissingArgumentObligation =
    { Id: ObligationId
      OriginTransactionId: TransactionId
      CallNodeId: GraphNodeId
      ParameterId: ParameterId
      Status: ObligationStatus }

type Snapshot =
    { Id: SnapshotId
      ParentId: SnapshotId option
      Types: SemanticType list
      Effects: SemanticEffect list
      Capabilities: SemanticCapability list
      Functions: SemanticFunction list
      Obligations: MissingArgumentObligation list }

[<RequireQualifiedAccess>]
module ValueGraph =
    let rec collectNodes (graph: ValueGraph): SemanticNode list =
        let collectNode (node: SemanticNode) =
            let nested =
                match node.Kind with
                | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                    collectNodes whenTrue @ collectNodes whenFalse
                | SemanticNodeKind.MatchUnion(_, arms) ->
                    arms |> List.collect (fun arm -> collectNodes arm.Body)
                | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                    collectNodes someArm.Body @ collectNodes noneBody
                | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                    collectNodes okArm.Body @ collectNodes errArm.Body
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.Int64Literal _
                | SemanticNodeKind.StringIsEmpty _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64Equal _
                | SemanticNodeKind.Int64LessThan _
                | SemanticNodeKind.Int64LessThanOrEqual _
                | SemanticNodeKind.Int64GreaterThan _
                | SemanticNodeKind.Int64GreaterThanOrEqual _
                | SemanticNodeKind.ListEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListMap _
                | SemanticNodeKind.ListFilter _
                | SemanticNodeKind.ListAny _
                | SemanticNodeKind.ListAll _
                | SemanticNodeKind.DirectCall _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.ResultErr _ -> []

            node :: nested

        graph.Nodes |> List.collect collectNode

    let rec mapNodes (transform: SemanticNode -> SemanticNode) (graph: ValueGraph): ValueGraph =
        let mapNode (node: SemanticNode) =
            let kind =
                match node.Kind with
                | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                    SemanticNodeKind.Conditional(
                        conditionNodeId,
                        mapNodes transform whenTrue,
                        mapNodes transform whenFalse
                    )
                | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                    let mappedArms =
                        arms
                        |> List.map (fun arm -> { arm with Body = mapNodes transform arm.Body })
                    SemanticNodeKind.MatchUnion(inputNodeId, mappedArms)
                | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                    let mappedSomeArm = { someArm with Body = mapNodes transform someArm.Body }
                    SemanticNodeKind.MatchOption(inputNodeId, mappedSomeArm, mapNodes transform noneBody)
                | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                    let mappedOkArm = { okArm with Body = mapNodes transform okArm.Body }
                    let mappedErrArm = { errArm with Body = mapNodes transform errArm.Body }
                    SemanticNodeKind.MatchResult(inputNodeId, mappedOkArm, mappedErrArm)
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.Int64Literal _
                | SemanticNodeKind.StringIsEmpty _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64Equal _
                | SemanticNodeKind.Int64LessThan _
                | SemanticNodeKind.Int64LessThanOrEqual _
                | SemanticNodeKind.Int64GreaterThan _
                | SemanticNodeKind.Int64GreaterThanOrEqual _
                | SemanticNodeKind.ListEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListMap _
                | SemanticNodeKind.ListFilter _
                | SemanticNodeKind.ListAny _
                | SemanticNodeKind.ListAll _
                | SemanticNodeKind.DirectCall _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.ResultErr _ -> node.Kind

            transform { node with Kind = kind }

        { graph with Nodes = graph.Nodes |> List.map mapNode }

[<RequireQualifiedAccess>]
module Snapshot =
    let empty id =
        { Id = id
          ParentId = None
          Types = []
          Effects = []
          Capabilities = []
          Functions = []
          Obligations = [] }
