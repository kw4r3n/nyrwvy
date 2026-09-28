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
    /// `Task<T>`: a handle to a task started in a task scope. Its only operations are TaskWait and TaskCancel.
    | Task
    /// `TaskScope`: the opaque scope value a TaskScope body receives; takes no type arguments.
    | TaskScope

[<RequireQualifiedAccess>]
type TaskWaitMode =
    /// Returns once at least one listed task has finished (at once for an empty list).
    | Any
    /// Returns once every listed task has finished.
    | All

[<RequireQualifiedAccess>]
type TypeRef =
    | Primitive of PrimitiveType
    | Named of TypeId
    | Capability of CapabilityId
    /// Pure, capture-free callable; parameter order is semantic, parameter identities are not.
    | Function of parameters: TypeRef list * resultType: TypeRef
    | BuiltinGeneric of BuiltinGenericType * TypeRef list
    /// A type parameter of the function whose parameter types mention it. Its identity is
    /// system-owned; the display name is fixed with it and carried for presentation only.
    | TypeParameter of id: TypeParameterId * displayName: string

/// Completion is a derived control-flow fact, never a language value type.
[<RequireQualifiedAccess>]
type GraphCompletion =
    /// The value type on normal paths; other paths may still abort.
    | Normal of TypeRef
    /// There is no normally completing path through this expression.
    | Aborts

[<RequireQualifiedAccess>]
module GraphCompletion =
    let normalType = function
        | GraphCompletion.Normal value -> Some value
        | GraphCompletion.Aborts -> None

    /// Unknown or inconsistent arms remain unknown; only abrupt arms are ignored.
    let merge (arms: GraphCompletion option list) =
        if List.isEmpty arms || List.exists Option.isNone arms then None
        else
            let normal = arms |> List.choose (Option.bind normalType)
            match normal with
            | [] -> Some GraphCompletion.Aborts
            | first :: rest when List.forall ((=) first) rest -> Some(GraphCompletion.Normal first)
            | _ -> None

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
    /// Number of code points in the String, as an Int64. A surrogate pair counts once; an unpaired
    /// surrogate code unit counts as one code point.
    | StringLength of valueNodeId: GraphNodeId
    /// The code points at positions [start, end) of the String. Both bounds are clamped to
    /// [0, StringLength], and end <= start yields the empty String, so slicing never fails.
    | StringSlice of valueNodeId: GraphNodeId * startNodeId: GraphNodeId * endNodeId: GraphNodeId
    | StringContains of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    | StringEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Ordinal String ordering: true when the left String sorts strictly before the right one,
    /// comparing Unicode code points lexicographically (a proper prefix sorts first). Scalar values
    /// above U+FFFF sort after every BMP code point, unlike UTF-16 code-unit order. Never fails.
    | StringLessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Structural equality of two values of one equatable type (see `EquatableTypes`): the same
    /// variant with pairwise equal fields, lists by length and item by item in order. Never fails.
    | ValueEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | StringConcat of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64FloorDivide of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64FloorModulo of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
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
    /// The Float64 nearest to the Int64: exact for |value| <= 2^53, otherwise IEEE 754 round to nearest,
    /// ties to even (C# `(double)long`, Python `float(int)`). Every Int64 is finite as a Float64. Never fails.
    | Int64ToFloat64 of valueNodeId: GraphNodeId
    | ListEmpty of itemType: TypeRef
    | ListConstruct of itemType: TypeRef * itemNodeIds: GraphNodeId list
    | ListLength of listNodeId: GraphNodeId
    | ListAppendItem of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// `Some item` at a zero-based Int64 index in range, else `None`; never fails.
    | ListItemAt of listNodeId: GraphNodeId * indexNodeId: GraphNodeId
    | ListConcat of leftListNodeId: GraphNodeId * rightListNodeId: GraphNodeId
    /// List callbacks reference an existing pure function. `context` binds some of its parameters,
    /// by identity, to values evaluated once before iteration; the remaining parameters, in
    /// declaration order, receive the iteration values (accumulator and item for ListFold, the
    /// item otherwise). An empty context is the plain callback.
    | ListFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: ArgumentBinding list
    /// Acquires a resource with `acquire : Result<R, E>`; on Ok runs the body function with R (other
    /// parameters bound by `context`) and then always releases R through a capability operation.
    /// Runs the worker for every source item concurrently and waits for all of them. The first Err
    /// (or host exception) requests cancellation of the others; the result is the Err of the lowest
    /// failing index, or Ok of every worker's value in source order.
    | ParallelAll of sourceListNodeId: GraphNodeId * workerFunctionId: FunctionId * context: ArgumentBinding list
    /// Whether cancellation was requested for the ParallelAll worker, task or TaskScope body evaluating it; false elsewhere.
    | CancellationRequested
    | UseResource of acquireNodeId: GraphNodeId * releaseCapabilityNodeId: GraphNodeId * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: ArgumentBinding list
    /// Runs the body function with a new `TaskScope` value (other parameters bound by `context`).
    /// When the body exits by Ok, Err or host exception, every unfinished task of the scope is
    /// cancelled and awaited before the node completes. The result is the body's `Result<T, E>`.
    | TaskScope of bodyFunctionId: FunctionId * context: ArgumentBinding list
    /// Starts the worker with the item as a new task of the scope and returns its `Task<Result<U, E>>`
    /// handle at once. A task's Err does not cancel its siblings.
    | TaskStart of scopeNodeId: GraphNodeId * workerFunctionId: FunctionId * context: ArgumentBinding list * itemNodeId: GraphNodeId
    /// Waits until the mode is satisfied or the optional `Option<Int64>` millisecond limit elapses, and
    /// returns `Some result` for every finished task and `None` for every running one, in input order.
    /// Tasks keep running after it returns. An absent limit operand means no limit, as `None` does.
    | TaskWait of tasksNodeId: GraphNodeId * mode: TaskWaitMode * limitNodeId: GraphNodeId option
    /// Requests cooperative cancellation of one task without waiting for it. Idempotent.
    | TaskCancel of taskNodeId: GraphNodeId
    | ListEffectFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: ArgumentBinding list
    | ListMap of sourceListNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    | ListFilter of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    /// The source items in a stable order under a pure `(T, T) -> Bool` "less than" callback whose
    /// two unbound parameters receive (left, right). The result is fixed by a top-down merge sort:
    /// split at length / 2, sort both halves, and merge taking the right head only when
    /// lessThan(rightHead, leftHead) holds. A consistent strict order gives the stable sort; any
    /// other callback still yields a deterministic permutation of the source. Never fails.
    | ListSortBy of sourceListNodeId: GraphNodeId * lessThanFunctionId: FunctionId * context: ArgumentBinding list
    | ListAny of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    | ListAll of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    | DirectCall of FunctionId * ArgumentBinding list
    /// Construct a stateless capability using named pure functions, one per operation.
    | CapabilityConstruct of CapabilityId * implementations: (CapabilityOperationId * FunctionId) list
    | FunctionReference of FunctionId
    | FunctionInvoke of functionValueNodeId: GraphNodeId * arguments: GraphNodeId list
    | CapabilityInvoke of capabilityValueNodeId: GraphNodeId * operationId: CapabilityOperationId * arguments: CapabilityArgumentBinding list
    | ProductConstruct of TypeId * FieldValueBinding list
    | FieldAccess of GraphNodeId * FieldId
    | UnionConstruct of VariantId * FieldValueBinding list
    | OptionNone of itemType: TypeRef
    | OptionSome of valueNodeId: GraphNodeId
    | ResultOk of errorType: TypeRef * valueNodeId: GraphNodeId
    | Abort of errorNodeId: GraphNodeId
    | ResultErr of okType: TypeRef * errorNodeId: GraphNodeId
    /// Logical negation of a Bool operand; the dedicated form of Conditional(operand, false, true).
    | BoolNot of operandNodeId: GraphNodeId
    /// True when both operands are true. The nested right graph follows Conditional branch rules and
    /// is evaluated only when left is true; the dedicated form of Conditional(left, right, false).
    | BoolAnd of leftNodeId: GraphNodeId * right: ValueGraph
    /// True when either operand is true. The nested right graph follows Conditional branch rules and
    /// is evaluated only when left is false; the dedicated form of Conditional(left, true, right).
    | BoolOr of leftNodeId: GraphNodeId * right: ValueGraph
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
                | SemanticNodeKind.BoolAnd(_, right)
                | SemanticNodeKind.BoolOr(_, right) -> collectNodes right
                | SemanticNodeKind.MatchUnion(_, arms) ->
                    arms |> List.collect (fun arm -> collectNodes arm.Body)
                | SemanticNodeKind.Bind(_, body) -> collectNodes body.Body
                | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                    collectNodes someArm.Body @ collectNodes noneBody
                | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                    collectNodes okArm.Body @ collectNodes errArm.Body
                | SemanticNodeKind.StringConcat _
                | SemanticNodeKind.StringEqual _
                | SemanticNodeKind.StringLessThan _
                | SemanticNodeKind.ValueEqual _
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
                | SemanticNodeKind.StringLength _
                | SemanticNodeKind.StringSlice _
                | SemanticNodeKind.BoolNot _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64FloorDivide _
                | SemanticNodeKind.Int64FloorModulo _
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
                | SemanticNodeKind.Int64ToFloat64 _
                | SemanticNodeKind.ListEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.ListItemAt _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListFold _
                | SemanticNodeKind.ListEffectFold _
                | SemanticNodeKind.UseResource _
                | SemanticNodeKind.ParallelAll _
                | SemanticNodeKind.CancellationRequested
                | SemanticNodeKind.TaskScope _
                | SemanticNodeKind.TaskStart _
                | SemanticNodeKind.TaskWait _
                | SemanticNodeKind.TaskCancel _
                | SemanticNodeKind.ListMap _
                | SemanticNodeKind.ListFilter _
                | SemanticNodeKind.ListSortBy _
                | SemanticNodeKind.ListAny _
                | SemanticNodeKind.ListAll _
                | SemanticNodeKind.CapabilityConstruct _
                | SemanticNodeKind.FunctionReference _
                | SemanticNodeKind.FunctionInvoke _
                | SemanticNodeKind.DirectCall _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.Abort _
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
                | SemanticNodeKind.BoolAnd(leftNodeId, right) ->
                    SemanticNodeKind.BoolAnd(leftNodeId, mapNodes transform right)
                | SemanticNodeKind.BoolOr(leftNodeId, right) ->
                    SemanticNodeKind.BoolOr(leftNodeId, mapNodes transform right)
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
                | SemanticNodeKind.StringLessThan _
                | SemanticNodeKind.ValueEqual _
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
                | SemanticNodeKind.StringLength _
                | SemanticNodeKind.StringSlice _
                | SemanticNodeKind.BoolNot _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64FloorDivide _
                | SemanticNodeKind.Int64FloorModulo _
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
                | SemanticNodeKind.Int64ToFloat64 _
                | SemanticNodeKind.ListEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.ListItemAt _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListFold _
                | SemanticNodeKind.ListEffectFold _
                | SemanticNodeKind.UseResource _
                | SemanticNodeKind.ParallelAll _
                | SemanticNodeKind.CancellationRequested
                | SemanticNodeKind.TaskScope _
                | SemanticNodeKind.TaskStart _
                | SemanticNodeKind.TaskWait _
                | SemanticNodeKind.TaskCancel _
                | SemanticNodeKind.ListMap _
                | SemanticNodeKind.ListFilter _
                | SemanticNodeKind.ListSortBy _
                | SemanticNodeKind.ListAny _
                | SemanticNodeKind.ListAll _
                | SemanticNodeKind.CapabilityConstruct _
                | SemanticNodeKind.FunctionReference _
                | SemanticNodeKind.FunctionInvoke _
                | SemanticNodeKind.DirectCall _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.Abort _
                | SemanticNodeKind.ResultErr _ -> node.Kind

            transform { node with Kind = kind }

        { graph with Nodes = graph.Nodes |> List.map mapNode }

[<RequireQualifiedAccess>]
module FunctionAbort =
    let isUsed (snapshot: Snapshot) =
        snapshot.Functions |> List.exists (fun fn ->
            ValueGraph.collectNodes fn.Body |> List.exists (fun node ->
                match node.Kind with SemanticNodeKind.Abort _ -> true | _ -> false))

/// A TaskWait with a limit operand needs a persisted schema newer than the one that introduced tasks.
[<RequireQualifiedAccess>]
module TaskTimeLimits =
    let isUsed (snapshot: Snapshot) =
        snapshot.Functions |> List.exists (fun fn ->
            ValueGraph.collectNodes fn.Body |> List.exists (fun node ->
                match node.Kind with SemanticNodeKind.TaskWait(_, _, Some _) -> true | _ -> false))

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

/// `Task<T>` and `TaskScope` values exist only inside one run of a function. A declared field may
/// hold a `Task<T>` but not a `TaskScope`, which is a lexical construct; a type that holds a task
/// anywhere, including through declared types, never crosses a capability or runPure boundary.
[<RequireQualifiedAccess>]
module TaskTypes =
    let rec private occurs (matches: BuiltinGenericType -> bool) (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.BuiltinGeneric(kind, _) when matches kind -> true
        | TypeRef.BuiltinGeneric(_, arguments) -> List.exists (occurs matches) arguments
        | TypeRef.Function(parameters, result) -> List.exists (occurs matches) parameters || occurs matches result
        | TypeRef.Primitive _
        | TypeRef.Named _
        | TypeRef.Capability _
        | TypeRef.TypeParameter _ -> false

    /// Whether the type itself mentions `Task` or `TaskScope`, without looking into declared types.
    let mentions (typeRef: TypeRef) =
        occurs (function BuiltinGenericType.Task | BuiltinGenericType.TaskScope -> true | _ -> false) typeRef

    /// Whether the type itself mentions `TaskScope`, without looking into declared types.
    let mentionsScope (typeRef: TypeRef) =
        occurs (function BuiltinGenericType.TaskScope -> true | _ -> false) typeRef

    /// Whether a value of the type can hold a task handle or scope: the type mentions one, or reaches
    /// a declared Product or Union whose fields do, through any nesting. Recursive and unknown named
    /// types are handled; each named type is inspected once.
    let bearing (types: SemanticType list) (typeRef: TypeRef) =
        let byId = types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let visited = System.Collections.Generic.HashSet<TypeId>()
        let rec named (typeRef: TypeRef) =
            match typeRef with
            | TypeRef.Named typeId -> [ typeId ]
            | TypeRef.BuiltinGeneric(_, arguments) -> List.collect named arguments
            | TypeRef.Function(parameters, result) -> List.collect named (result :: parameters)
            | TypeRef.Primitive _
            | TypeRef.Capability _
            | TypeRef.TypeParameter _ -> []
        let rec visit (typeRef: TypeRef) =
            mentions typeRef
            || named typeRef
               |> List.exists (fun typeId ->
                   visited.Add typeId
                   && (match Map.tryFind typeId byId with
                       | None -> false
                       | Some typeEntity ->
                           let fields =
                               match typeEntity.Kind with
                               | SemanticTypeKind.Product fields -> fields
                               | SemanticTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields)
                           fields |> List.exists (fun field -> visit field.Type)))
        visit typeRef

/// `ValueEqual` compares two values of one *equatable* type: String, Int64, Bool, Unit, `List`,
/// `Option` and `Result` of equatable types, and declared Products and Unions whose fields are all
/// equatable, through any nesting and including recursive types. Float64 (NaN; use Float64Equal),
/// function types, Task, TaskScope, capabilities, type parameters and anything reaching them are
/// not equatable.
[<RequireQualifiedAccess>]
module EquatableTypes =
    /// Presentation text of a type using display names; unknown named types show their StableId.
    let rec describe (types: SemanticType list) (typeRef: TypeRef) =
        let go = describe types
        match typeRef with
        | TypeRef.Primitive primitive -> string primitive
        | TypeRef.Named typeId ->
            types
            |> List.tryFind (fun item -> item.Id = typeId)
            |> Option.map (fun item -> item.DisplayName)
            |> Option.defaultValue (StableId.value typeId)
        | TypeRef.Capability capabilityId -> $"capability '{StableId.value capabilityId}'"
        | TypeRef.Function(parameters, result) ->
            "(" + (parameters |> List.map go |> String.concat ", ") + ") -> " + go result
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, _) -> "TaskScope"
        | TypeRef.BuiltinGeneric(kind, arguments) -> $"{kind}<" + (arguments |> List.map go |> String.concat ", ") + ">"
        | TypeRef.TypeParameter(_, displayName) -> displayName

    /// The first part that makes the type not equatable, as a path of display-name steps from the
    /// type to that part (empty when the type itself is the part) and the offending type; `None`
    /// when the type is equatable. A declared type under inspection counts as equatable where it
    /// recurs, so recursive types are equatable exactly when their fields are; unknown named types
    /// are left to the declaration checks.
    let firstNotEquatable (types: SemanticType list) (typeRef: TypeRef) : (string list * TypeRef) option =
        let byId = types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let visited = System.Collections.Generic.HashSet<TypeId>()
        let rec visit (path: string list) (typeRef: TypeRef) =
            match typeRef with
            | TypeRef.Primitive(PrimitiveType.String | PrimitiveType.Int64 | PrimitiveType.Bool | PrimitiveType.Unit) -> None
            | TypeRef.Primitive PrimitiveType.Float64
            | TypeRef.Function _
            | TypeRef.Capability _
            | TypeRef.TypeParameter _
            | TypeRef.BuiltinGeneric((BuiltinGenericType.Task | BuiltinGenericType.TaskScope), _) -> Some(List.rev path, typeRef)
            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
                arguments |> List.tryPick (visit ("List item" :: path))
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
                arguments |> List.tryPick (visit ("Option value" :: path))
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
                arguments
                |> List.mapi (fun index argument -> (if index = 0 then "Result ok" else "Result err"), argument)
                |> List.tryPick (fun (step, argument) -> visit (step :: path) argument)
            | TypeRef.Named typeId ->
                if not (visited.Add typeId) then
                    None
                else
                    match Map.tryFind typeId byId with
                    | None -> None
                    | Some typeEntity ->
                        match typeEntity.Kind with
                        | SemanticTypeKind.Product fields ->
                            fields
                            |> List.tryPick (fun field -> visit ($"{typeEntity.DisplayName}.{field.DisplayName}" :: path) field.Type)
                        | SemanticTypeKind.Union variants ->
                            variants
                            |> List.tryPick (fun variant ->
                                variant.Fields
                                |> List.tryPick (fun field ->
                                    visit ($"{typeEntity.DisplayName}.{variant.DisplayName}.{field.DisplayName}" :: path) field.Type))
        visit [] typeRef

    let isEquatable (types: SemanticType list) (typeRef: TypeRef) =
        (firstNotEquatable types typeRef).IsNone

    /// Diagnostic text: why the type is not equatable, naming the path to the offending part.
    let explain (types: SemanticType list) (typeRef: TypeRef) =
        match firstNotEquatable types typeRef with
        | None -> $"{describe types typeRef} is equatable."
        | Some(path, offending) ->
            let reason =
                match offending with
                | TypeRef.Primitive PrimitiveType.Float64 -> "Float64 is not equatable because of NaN; compare with Float64Equal"
                | TypeRef.Function _ -> "function values are not equatable"
                | TypeRef.BuiltinGeneric((BuiltinGenericType.Task | BuiltinGenericType.TaskScope), _) -> "task handles and scopes are not equatable"
                | TypeRef.Capability _ -> "capability values are not equatable"
                | TypeRef.TypeParameter _ -> "a type parameter is not known to be equatable"
                | _ -> "it is not equatable"
            let location =
                match path with
                | [] -> ""
                | steps -> " at " + String.concat " -> " steps
            $"{describe types typeRef} is not equatable: {describe types offending}{location}; {reason}."

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
            | TypeRef.Function(arguments, result) -> (arguments @ [ result ]) |> List.fold collect collected
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
        | TypeRef.Function(arguments, result) -> TypeRef.Function(arguments |> List.map (substitute substitution), substitute substitution result)
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
        | TypeRef.Function(declaredArguments, declaredResult), TypeRef.Function(actualArguments, actualResult) when declaredArguments.Length = actualArguments.Length ->
            List.zip (declaredArguments @ [ declaredResult ]) (actualArguments @ [ actualResult ])
            |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some substitution)
        | TypeRef.BuiltinGeneric(declaredKind, declaredArguments), TypeRef.BuiltinGeneric(actualKind, actualArguments)
            when declaredKind = actualKind && declaredArguments.Length = actualArguments.Length ->
            List.zip declaredArguments actualArguments
            |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some substitution)
        | _ when declared = actual -> Some substitution
        | _ -> None

    /// Matches declared parameter types against actual types in order; None when any pair conflicts.
    let matchAll (pairs: (TypeRef * TypeRef) list) =
        pairs |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some Map.empty)
