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

[<RequireQualifiedAccess>]
type BuiltinGenericType =
    | Option
    | Result

[<RequireQualifiedAccess>]
type TypeRef =
    | Primitive of PrimitiveType
    | Named of TypeId
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

type SemanticParameter =
    { Id: ParameterId
      DisplayName: string
      Type: TypeRef }

type ArgumentBinding =
    { TargetParameterId: ParameterId
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

and OptionSomeArm =
    { SomeValueBinding: PatternBinding
      SomeBody: ValueGraph }

and ResultOkArm =
    { OkValueBinding: PatternBinding
      OkBody: ValueGraph }

and ResultErrArm =
    { ErrValueBinding: PatternBinding
      ErrBody: ValueGraph }

and [<RequireQualifiedAccess>] SemanticNodeKind =
    | ParameterReference of ParameterId
    | PatternReference of PatternBindingId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | DirectCall of FunctionId * ArgumentBinding list
    | ProductConstruct of TypeId * FieldValueBinding list
    | FieldAccess of GraphNodeId * FieldId
    | UnionConstruct of VariantId * FieldValueBinding list
    | OptionNone of itemType: TypeRef
    | OptionSome of valueNodeId: GraphNodeId
    | ResultOk of errorType: TypeRef * valueNodeId: GraphNodeId
    | ResultErr of okType: TypeRef * errorNodeId: GraphNodeId
    | Conditional of conditionNodeId: GraphNodeId * whenTrue: ValueGraph * whenFalse: ValueGraph
    | MatchUnion of inputNodeId: GraphNodeId * arms: UnionMatchArm list
    | MatchOption of inputNodeId: GraphNodeId * someArm: OptionSomeArm * noneBody: ValueGraph
    | MatchResult of inputNodeId: GraphNodeId * okArm: ResultOkArm * errArm: ResultErrArm

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
                    collectNodes someArm.SomeBody @ collectNodes noneBody
                | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                    collectNodes okArm.OkBody @ collectNodes errArm.ErrBody
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.DirectCall _
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
                    let mappedSomeArm = { someArm with SomeBody = mapNodes transform someArm.SomeBody }
                    SemanticNodeKind.MatchOption(inputNodeId, mappedSomeArm, mapNodes transform noneBody)
                | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                    let mappedOkArm = { okArm with OkBody = mapNodes transform okArm.OkBody }
                    let mappedErrArm = { errArm with ErrBody = mapNodes transform errArm.ErrBody }
                    SemanticNodeKind.MatchResult(inputNodeId, mappedOkArm, mappedErrArm)
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.DirectCall _
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
          Functions = []
          Obligations = [] }
