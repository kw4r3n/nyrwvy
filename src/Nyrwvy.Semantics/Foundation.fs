namespace Nyrwvy.Core

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
    | Float64

/// Authoritative IEEE 754 binary64 literal representation. Construction always
/// canonicalizes every NaN payload/sign variant to one quiet NaN bit pattern while
/// preserving every non-NaN bit exactly, including signed zero and infinities.
[<Struct>]
type Float64Bits = private Float64Bits of uint64

[<RequireQualifiedAccess>]
module Float64Bits =
    let canonicalNaNBits = 0x7ff8000000000000UL
    let private exponentMask = 0x7ff0000000000000UL
    let private fractionMask = 0x000fffffffffffffUL

    let private isRawNaN bits =
        bits &&& exponentMask = exponentMask && bits &&& fractionMask <> 0UL

    let create bits =
        Float64Bits(if isRawNaN bits then canonicalNaNBits else bits)

    let value (Float64Bits bits) = bits

    let ofFloat (number: float) =
        System.BitConverter.DoubleToUInt64Bits number |> create

    let toFloat bits =
        bits |> value |> System.BitConverter.UInt64BitsToDouble

    let isNaN bits =
        bits |> value |> isRawNaN

    let toHex bits =
        (value bits).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)

    let tryParseHex (text: string): Result<Float64Bits, string> =
        if isNull text || text.Length <> 16 then
            Error "Float64 bits must contain exactly 16 hexadecimal digits."
        else
            match
                System.UInt64.TryParse(
                    text,
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, bits -> Ok(create bits)
            | false, _ -> Error "Float64 bits contain invalid hexadecimal digits."

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
    /// A type parameter of the function whose parameter types mention it. Its identity is
    /// system-owned; the display name is fixed with it and carried for presentation only.
    | TypeParameter of id: TypeParameterId * displayName: string

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

/// Bind, Option.Some and Result.Ok intentionally share one semantic shape instead of
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
    | Float64Literal of Float64Bits
    | StringIsEmpty of valueNodeId: GraphNodeId
    /// Canonical invariant decimal text of an Int64, with a leading '-' only for negative values.
    | Int64ToString of valueNodeId: GraphNodeId
    /// Some(value) only for an optional '-' followed by one or more ASCII digits within the Int64
    /// range; any other text, including '+', whitespace or an empty String, is None.
    | StringParseInt64 of valueNodeId: GraphNodeId
    | StringContains of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    | StringEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | StringConcat of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Add of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Subtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Multiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Divide of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64IsNaN of valueNodeId: GraphNodeId
    | Float64IsFinite of valueNodeId: GraphNodeId
    | Float64IsInfinite of valueNodeId: GraphNodeId
    | ListEmpty of itemType: TypeRef
    | ListConstruct of itemType: TypeRef * itemNodeIds: GraphNodeId list
    | ListLength of listNodeId: GraphNodeId
    | ListAppendItem of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | ListConcat of leftListNodeId: GraphNodeId * rightListNodeId: GraphNodeId
    /// List callbacks reference an existing pure function. `context` binds some of its parameters,
    /// by identity, to values evaluated once before iteration; the remaining parameters, in
    /// declaration order, receive the iteration values (accumulator and item for ListFold, the
    /// item otherwise). An empty context is the plain callback.
    | ListFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: ArgumentBinding list
    /// Acquires a resource with `acquire : Result<R, E>`; on Ok runs the body function with R (other
    /// parameters bound by `context`) and then always releases R through a capability operation.
    | UseResource of acquireNodeId: GraphNodeId * releaseCapabilityNodeId: GraphNodeId * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: ArgumentBinding list
    | ListEffectFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: ArgumentBinding list
    | ListMap of sourceListNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    | ListFilter of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    | ListAny of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    | ListAll of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
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
    | Bind of inputNodeId: GraphNodeId * body: ValueMatchArm
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
                | SemanticNodeKind.Bind(_, body) -> collectNodes body.Body
                | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                    collectNodes someArm.Body @ collectNodes noneBody
                | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                    collectNodes okArm.Body @ collectNodes errArm.Body
                | SemanticNodeKind.StringConcat _
                | SemanticNodeKind.StringEqual _
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.Int64Literal _
                | SemanticNodeKind.Float64Literal _
                | SemanticNodeKind.StringIsEmpty _
                | SemanticNodeKind.Int64ToString _
                | SemanticNodeKind.StringParseInt64 _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64Equal _
                | SemanticNodeKind.Int64LessThan _
                | SemanticNodeKind.Int64LessThanOrEqual _
                | SemanticNodeKind.Int64GreaterThan _
                | SemanticNodeKind.Int64GreaterThanOrEqual _
                | SemanticNodeKind.Float64Add _
                | SemanticNodeKind.Float64Subtract _
                | SemanticNodeKind.Float64Multiply _
                | SemanticNodeKind.Float64Divide _
                | SemanticNodeKind.Float64Equal _
                | SemanticNodeKind.Float64LessThan _
                | SemanticNodeKind.Float64IsNaN _
                | SemanticNodeKind.Float64IsFinite _
                | SemanticNodeKind.Float64IsInfinite _
                | SemanticNodeKind.ListEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListFold _
                | SemanticNodeKind.ListEffectFold _
                | SemanticNodeKind.UseResource _
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
                | SemanticNodeKind.Bind(inputNodeId, body) ->
                    SemanticNodeKind.Bind(inputNodeId, { body with Body = mapNodes transform body.Body })
                | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                    let mappedOkArm = { okArm with Body = mapNodes transform okArm.Body }
                    let mappedErrArm = { errArm with Body = mapNodes transform errArm.Body }
                    SemanticNodeKind.MatchResult(inputNodeId, mappedOkArm, mappedErrArm)
                | SemanticNodeKind.StringConcat _
                | SemanticNodeKind.StringEqual _
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.Int64Literal _
                | SemanticNodeKind.Float64Literal _
                | SemanticNodeKind.StringIsEmpty _
                | SemanticNodeKind.Int64ToString _
                | SemanticNodeKind.StringParseInt64 _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64Equal _
                | SemanticNodeKind.Int64LessThan _
                | SemanticNodeKind.Int64LessThanOrEqual _
                | SemanticNodeKind.Int64GreaterThan _
                | SemanticNodeKind.Int64GreaterThanOrEqual _
                | SemanticNodeKind.Float64Add _
                | SemanticNodeKind.Float64Subtract _
                | SemanticNodeKind.Float64Multiply _
                | SemanticNodeKind.Float64Divide _
                | SemanticNodeKind.Float64Equal _
                | SemanticNodeKind.Float64LessThan _
                | SemanticNodeKind.Float64IsNaN _
                | SemanticNodeKind.Float64IsFinite _
                | SemanticNodeKind.Float64IsInfinite _
                | SemanticNodeKind.ListEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListFold _
                | SemanticNodeKind.ListEffectFold _
                | SemanticNodeKind.UseResource _
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

/// Function type parameters. A function's type parameters are those occurring in its parameter
/// types; at a call or callback, type arguments are derived by matching each parameter type
/// against the concrete argument type, so no inference beyond structural matching is involved.
[<RequireQualifiedAccess>]
module TypeParameters =
    /// Type parameters occurring in a type, in first-occurrence order.
    let occurring (typeRef: TypeRef): (TypeParameterId * string) list =
        let rec collect (collected: (TypeParameterId * string) list) current =
            match current with
            | TypeRef.TypeParameter(id, name) when collected |> List.exists (fun (seen, _) -> seen = id) -> collected
            | TypeRef.TypeParameter(id, name) -> collected @ [ id, name ]
            | TypeRef.BuiltinGeneric(_, arguments) -> arguments |> List.fold collect collected
            | TypeRef.Primitive _
            | TypeRef.Named _
            | TypeRef.Capability _ -> collected
        collect [] typeRef

    /// Type parameters declared by a function's parameter types, in first-occurrence order.
    let ofFunction (functionEntity: SemanticFunction): (TypeParameterId * string) list =
        functionEntity.Parameters
        |> List.collect (fun parameter -> occurring parameter.Type)
        |> List.distinctBy fst

    let rec substitute (substitution: Map<TypeParameterId, TypeRef>) (typeRef: TypeRef): TypeRef =
        match typeRef with
        | TypeRef.TypeParameter(id, _) -> Map.tryFind id substitution |> Option.defaultValue typeRef
        | TypeRef.BuiltinGeneric(kind, arguments) -> TypeRef.BuiltinGeneric(kind, arguments |> List.map (substitute substitution))
        | TypeRef.Primitive _
        | TypeRef.Named _
        | TypeRef.Capability _ -> typeRef

    /// Extends `substitution` so that `declared` becomes `actual`, or returns None on a conflict.
    let rec matchType (substitution: Map<TypeParameterId, TypeRef>) (declared: TypeRef) (actual: TypeRef) =
        match declared, actual with
        | TypeRef.TypeParameter(id, _), _ ->
            match Map.tryFind id substitution with
            | Some bound when bound = actual -> Some substitution
            | Some _ -> None
            | None -> Some(Map.add id actual substitution)
        | TypeRef.BuiltinGeneric(declaredKind, declaredArguments), TypeRef.BuiltinGeneric(actualKind, actualArguments)
            when declaredKind = actualKind && declaredArguments.Length = actualArguments.Length ->
            List.zip declaredArguments actualArguments
            |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some substitution)
        | _ when declared = actual -> Some substitution
        | _ -> None

    /// Matches declared parameter types against actual types in order; None when any pair conflicts.
    let matchAll (pairs: (TypeRef * TypeRef) list) =
        pairs |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some Map.empty)
