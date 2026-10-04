namespace Nyrwvy.Core

open System.Collections.Generic

/// Lowers Typed Semantic IR into explicit backend-neutral execution order.
/// Boundary types are deliberately annotated because the semantic and execution
/// layers contain structurally similar records; inference must not decide which
/// layer a helper operates on from a shared field name.
[<RequireQualifiedAccess>]
module ExecutionIr =
    type private MutableBlock =
        { Id: ExecutionBlockId
          Parameters: ExecutionValue list
          Instructions: ResizeArray<ExecutionInstruction>
          mutable Terminator: ExecutionTerminator option }

    type private LoweringEnvironment =
        { NodeValues: Map<GraphNodeId, ExecutionValueId>
          PatternValues: Map<PatternBindingId, ExecutionValueId> }

    let private lowerFunction
        (functionsById: Map<FunctionId, TypedSemanticFunction>)
        (functionEntity: TypedSemanticFunction)
        : Result<ExecutionFunction, ExecutionIrError> =
        match ExecutionIrAnalysis.validateEffectOrder functionEntity.Id functionsById functionEntity.Body with
        | Error error -> Error error
        | Ok () ->
            let blocks = Dictionary<ExecutionBlockId, MutableBlock>()

            let addBlock (id: ExecutionBlockId) (parameters: ExecutionValue list): MutableBlock =
                let block: MutableBlock =
                    { Id = id
                      Parameters = parameters
                      Instructions = ResizeArray<ExecutionInstruction>()
                      Terminator = None }
                blocks.Add(id, block)
                block

            let setTerminator (block: MutableBlock) (terminator: ExecutionTerminator) =
                block.Terminator <- Some terminator

            let parameterValues: Map<ParameterId, ExecutionValue> =
                functionEntity.Parameters
                |> List.map (fun (parameter: TypedSemanticParameter) ->
                    let value: ExecutionValue =
                        { Id = ExecutionIrIds.parameterValue parameter.Id
                          Type = parameter.Type
                          Origin = ExecutionOrigin.FunctionParameter parameter.Id }
                    parameter.Id, value)
                |> Map.ofList

            let executionParameters: ExecutionParameter list =
                functionEntity.Parameters
                |> List.map (fun (parameter: TypedSemanticParameter) ->
                    { SemanticParameterId = parameter.Id
                      Value = Map.find parameter.Id parameterValues })

            let requireNodeValue
                (environment: LoweringEnvironment)
                (nodeId: GraphNodeId)
                : Result<ExecutionValueId, ExecutionIrError> =
                match Map.tryFind nodeId environment.NodeValues with
                | Some valueId -> Ok valueId
                | None -> Error(ExecutionIrError.MissingNodeValue(functionEntity.Id, nodeId))

            let requirePatternValue
                (environment: LoweringEnvironment)
                (bindingId: PatternBindingId)
                : Result<ExecutionValueId, ExecutionIrError> =
                match Map.tryFind bindingId environment.PatternValues with
                | Some valueId -> Ok valueId
                | None -> Error(ExecutionIrError.MissingPatternValue(functionEntity.Id, bindingId))

            let makeNodeValue (node: TypedSemanticNode): ExecutionValue =
                { Id = ExecutionIrIds.nodeValue node.OriginNodeId
                  Type = node.Type
                  Origin = ExecutionOrigin.SemanticNode node.OriginNodeId }

            let addInstruction
                (block: MutableBlock)
                (node: TypedSemanticNode)
                (kind: ExecutionInstructionKind)
                : ExecutionValueId =
                let result = makeNodeValue node
                let instruction: ExecutionInstruction =
                    { Result = result
                      OriginNodeId = node.OriginNodeId
                      Kind = kind }
                block.Instructions.Add instruction
                result.Id

            let addPatternExtraction
                (block: MutableBlock)
                (matchNodeId: GraphNodeId)
                (binding: TypedSemanticPatternBinding)
                (kind: ExecutionInstructionKind)
                : ExecutionValue =
                let value: ExecutionValue =
                    { Id = ExecutionIrIds.patternValue binding.Id
                      Type = binding.Type
                      Origin = ExecutionOrigin.PatternBinding(matchNodeId, binding.Id) }
                let instruction: ExecutionInstruction =
                    { Result = value
                      OriginNodeId = matchNodeId
                      Kind = kind }
                block.Instructions.Add instruction
                value

            /// The node of a graph in tail position, when it leads to a self DirectCall in tail
            /// position (the call itself, or a structured node with such a call in an arm or body).
            /// The graph's result must be one of its own nodes that no other node of the graph uses;
            /// otherwise something still runs after it and it is not a tail position.
            let rec tailSelfCallNode (graph: TypedSemanticGraph): TypedSemanticNode option =
                graph.Nodes
                |> List.tryFind (fun (node: TypedSemanticNode) -> node.OriginNodeId = graph.ResultNodeId)
                |> Option.filter (fun (result: TypedSemanticNode) ->
                    leadsToTailSelfCall result
                    && graph.Nodes
                       |> List.forall (fun (other: TypedSemanticNode) ->
                           other.OriginNodeId = result.OriginNodeId
                           || not (List.contains result.OriginNodeId (ExecutionIrAnalysis.dependencies other))))

            and leadsToTailSelfCall (node: TypedSemanticNode): bool =
                let any (graphs: TypedSemanticGraph list) = graphs |> List.exists (tailSelfCallNode >> Option.isSome)
                match node.Kind with
                | TypedSemanticNodeKind.DirectCall(target, _) -> target = functionEntity.Id
                | TypedSemanticNodeKind.Bind(_, body) -> any [ body.Body ]
                | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) -> any [ whenTrue; whenFalse ]
                | TypedSemanticNodeKind.BoolAnd(_, right)
                | TypedSemanticNodeKind.BoolOr(_, right) -> any [ right ]
                | TypedSemanticNodeKind.MatchUnion(_, arms) -> arms |> List.map (fun (arm: TypedSemanticUnionArm) -> arm.Body) |> any
                | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) -> any [ someArm.Body; noneBody ]
                | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) -> any [ okArm.Body; errArm.Body ]
                | _ -> false

            /// `isTail`: the graph's result is the function's result (the body, or an arm or Bind
            /// body of a structured node in tail position). Its tail node is lowered after every
            /// other node of the graph, none of which uses it, and a self call there becomes a
            /// `TailCall`. Moving it last keeps effect and abort order: effect-order validation
            /// orders every observable node against an effectful call by dependency, and a pure
            /// call has no order against the graph's other nodes.
            let rec lowerGraph
                (startBlock: MutableBlock)
                (initialEnvironment: LoweringEnvironment)
                (isTail: bool)
                (graph: TypedSemanticGraph)
                : Result<(MutableBlock * ExecutionValueId) option, ExecutionIrError> =
                let mutable currentBlock = startBlock
                let mutable environment = initialEnvironment
                let mutable pending: Map<GraphNodeId, TypedSemanticNode> =
                    graph.Nodes
                    |> List.map (fun (node: TypedSemanticNode) -> node.OriginNodeId, node)
                    |> Map.ofList
                // `dependencies` walks nested graphs, so compute it once per node rather than per scheduling step.
                let dependenciesById: Map<GraphNodeId, GraphNodeId list> =
                    graph.Nodes
                    |> List.map (fun (node: TypedSemanticNode) -> node.OriginNodeId, ExecutionIrAnalysis.dependencies node)
                    |> Map.ofList
                let mutable failure: ExecutionIrError option = None
                let mutable stopped = false
                let tailNodeId: GraphNodeId option =
                    if isTail then tailSelfCallNode graph |> Option.map (fun (node: TypedSemanticNode) -> node.OriginNodeId)
                    else None
                let isTailNode (node: TypedSemanticNode) = tailNodeId = Some node.OriginNodeId

                let bindNode (nodeId: GraphNodeId) (valueId: ExecutionValueId) =
                    environment <-
                        { environment with
                            NodeValues = Map.add nodeId valueId environment.NodeValues }

                let readyNodes (): TypedSemanticNode list =
                    pending
                    |> Map.toList
                    |> List.map snd
                    |> List.filter (fun (node: TypedSemanticNode) ->
                        Map.find node.OriginNodeId dependenciesById
                        |> List.forall (fun dependencyId -> Map.containsKey dependencyId environment.NodeValues))
                    // The tail node waits until it is the last pending node.
                    |> List.filter (fun (node: TypedSemanticNode) -> not (isTailNode node) || Map.count pending = 1)
                    |> List.sortBy (fun node -> StableId.value node.OriginNodeId)

                let lowerFieldValues
                    (fields: TypedSemanticFieldValue list)
                    : Result<ExecutionFieldValue list, ExecutionIrError> =
                    fields
                    |> ResultList.traverse (fun (field: TypedSemanticFieldValue) ->
                        requireNodeValue environment field.ValueNodeId
                        |> Result.map (fun valueId ->
                            let lowered: ExecutionFieldValue =
                                { TargetFieldId = field.TargetFieldId
                                  ValueId = valueId }
                            lowered))
                    |> Result.map (List.sortBy (fun (field: ExecutionFieldValue) -> StableId.value field.TargetFieldId))

                let lowerCallArguments
                    (arguments: TypedSemanticArgument list)
                    : Result<ExecutionCallArgument list, ExecutionIrError> =
                    arguments
                    |> ResultList.traverse (fun (argument: TypedSemanticArgument) ->
                        requireNodeValue environment argument.ValueNodeId
                        |> Result.map (fun valueId ->
                            let lowered: ExecutionCallArgument =
                                { TargetParameterId = argument.TargetParameterId
                                  ValueId = valueId }
                            lowered))
                    |> Result.map (List.sortBy (fun (argument: ExecutionCallArgument) -> StableId.value argument.TargetParameterId))

                let lowerCapabilityArguments
                    (arguments: TypedSemanticCapabilityArgument list)
                    : Result<ExecutionCapabilityArgument list, ExecutionIrError> =
                    arguments
                    |> ResultList.traverse (fun (argument: TypedSemanticCapabilityArgument) ->
                        requireNodeValue environment argument.ValueNodeId
                        |> Result.map (fun valueId ->
                            let lowered: ExecutionCapabilityArgument =
                                { TargetCapabilityParameterId = argument.TargetCapabilityParameterId
                                  ValueId = valueId }
                            lowered))
                    |> Result.map (List.sortBy (fun (argument: ExecutionCapabilityArgument) -> StableId.value argument.TargetCapabilityParameterId))

                let lowerBinaryNode node leftNodeId rightNodeId constructor =
                    match requireNodeValue environment leftNodeId, requireNodeValue environment rightNodeId with
                    | Ok leftValueId, Ok rightValueId ->
                        Ok(addInstruction currentBlock node (constructor leftValueId rightValueId))
                    | Error error, _
                    | _, Error error -> Error error

                let lowerTernaryNode node firstNodeId secondNodeId thirdNodeId constructor =
                    match
                        requireNodeValue environment firstNodeId,
                        requireNodeValue environment secondNodeId,
                        requireNodeValue environment thirdNodeId
                    with
                    | Ok firstValueId, Ok secondValueId, Ok thirdValueId ->
                        Ok(addInstruction currentBlock node (constructor firstValueId secondValueId thirdValueId))
                    | Error error, _, _
                    | _, Error error, _
                    | _, _, Error error -> Error error

                let lowerUnaryNode node valueNodeId constructor =
                    requireNodeValue environment valueNodeId
                    |> Result.map (fun valueId ->
                        addInstruction currentBlock node (constructor valueId))

                let lowerNodeValues (nodeIds: GraphNodeId list): Result<ExecutionValueId list, ExecutionIrError> =
                    nodeIds |> ResultList.traverse (requireNodeValue environment)

                let lowerListCallbackNode node sourceListNodeId callbackFunctionId context constructor =
                    match requireNodeValue environment sourceListNodeId, lowerCallArguments context with
                    | Ok sourceListValueId, Ok lowered ->
                        Ok(addInstruction currentBlock node (constructor sourceListValueId callbackFunctionId lowered))
                    | Error error, _
                    | _, Error error -> Error error

                let lowerSimpleNode (node: TypedSemanticNode) =
                    let produced: Result<ExecutionValueId, ExecutionIrError> =
                        match node.Kind with
                        | TypedSemanticNodeKind.ParameterReference parameterId ->
                            match Map.tryFind parameterId parameterValues with
                            | Some source ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.ReadParameter source.Id))
                            | None -> Error(ExecutionIrError.MissingFunctionParameter(functionEntity.Id, parameterId))
                        | TypedSemanticNodeKind.PatternReference bindingId ->
                            requirePatternValue environment bindingId
                            |> Result.map (fun sourceValueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.ReadPatternBinding sourceValueId))
                        | TypedSemanticNodeKind.StringLiteral value ->
                            Ok(addInstruction currentBlock node (ExecutionInstructionKind.StringLiteral value))
                        | TypedSemanticNodeKind.BoolLiteral value ->
                            Ok(addInstruction currentBlock node (ExecutionInstructionKind.BoolLiteral value))
                        | TypedSemanticNodeKind.UnitLiteral ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.UnitLiteral)
                        | TypedSemanticNodeKind.Int64Literal value ->
                            Ok(addInstruction currentBlock node (ExecutionInstructionKind.Int64Literal value))
                        | TypedSemanticNodeKind.Float64Literal bits ->
                            Ok(addInstruction currentBlock node (ExecutionInstructionKind.Float64Literal bits))
                        | TypedSemanticNodeKind.StringIsEmpty valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringIsEmpty
                        | TypedSemanticNodeKind.Int64ToString valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Int64ToString
                        | TypedSemanticNodeKind.Int64ToHexString valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Int64ToHexString
                        | TypedSemanticNodeKind.StringParseInt64 valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringParseInt64
                        | TypedSemanticNodeKind.StringParseInt64Hex valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringParseInt64Hex
                        | TypedSemanticNodeKind.Float64ToString valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64ToString
                        | TypedSemanticNodeKind.StringParseFloat64 valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringParseFloat64
                        | TypedSemanticNodeKind.StringLength valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringLength
                        | TypedSemanticNodeKind.StringCodePoints valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringCodePoints
                        | TypedSemanticNodeKind.StringToLower valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringToLower
                        | TypedSemanticNodeKind.StringToUpper valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringToUpper
                        | TypedSemanticNodeKind.StringDecodeUtf8Lossy valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringDecodeUtf8Lossy
                        | TypedSemanticNodeKind.Float64RoundToInt64 valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64RoundToInt64
                        | TypedSemanticNodeKind.Float64FloorToInt64 valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64FloorToInt64
                        | TypedSemanticNodeKind.Float64Sqrt valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64Sqrt
                        | TypedSemanticNodeKind.Float64Abs valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64Abs
                        | TypedSemanticNodeKind.Float64Negate valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64Negate
                        | TypedSemanticNodeKind.Float64Floor valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64Floor
                        | TypedSemanticNodeKind.Float64Ceiling valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64Ceiling
                        | TypedSemanticNodeKind.Float64Truncate valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64Truncate
                        | TypedSemanticNodeKind.StringUtf8ByteCount valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringUtf8ByteCount
                        | TypedSemanticNodeKind.StringFromCodePoint valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringFromCodePoint
                        | TypedSemanticNodeKind.StringToCodePoint valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringToCodePoint
                        | TypedSemanticNodeKind.BoolNot operandNodeId ->
                            lowerUnaryNode node operandNodeId ExecutionInstructionKind.BoolNot
                        | TypedSemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
                            lowerTernaryNode node valueNodeId startNodeId endNodeId (fun valueId startId endId ->
                                ExecutionInstructionKind.StringSlice(valueId, startId, endId))
                        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                            match requireNodeValue environment valueNodeId, requireNodeValue environment substringNodeId with
                            | Ok valueId, Ok substringId ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.StringContains(valueId, substringId)))
                            | Error error, _
                            | _, Error error -> Error error
                        | TypedSemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) ->
                            lowerTernaryNode node valueNodeId substringNodeId fromNodeId (fun valueId substringId fromId ->
                                ExecutionInstructionKind.StringIndexOf(valueId, substringId, fromId))
                        | TypedSemanticNodeKind.StringSpan(valueNodeId, setNodeId, boundNodeId) ->
                            lowerTernaryNode node valueNodeId setNodeId boundNodeId (fun valueId setId boundId ->
                                ExecutionInstructionKind.StringSpan(valueId, setId, boundId))
                        | TypedSemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, boundNodeId) ->
                            lowerTernaryNode node valueNodeId setNodeId boundNodeId (fun valueId setId boundId ->
                                ExecutionInstructionKind.StringSpanBack(valueId, setId, boundId))
                        | TypedSemanticNodeKind.StringLastIndexOf(valueNodeId, otherNodeId) ->
                            lowerBinaryNode node valueNodeId otherNodeId (fun value other -> ExecutionInstructionKind.StringLastIndexOf(value, other))
                        | TypedSemanticNodeKind.StringStartsWith(valueNodeId, otherNodeId) ->
                            lowerBinaryNode node valueNodeId otherNodeId (fun value other -> ExecutionInstructionKind.StringStartsWith(value, other))
                        | TypedSemanticNodeKind.StringEndsWith(valueNodeId, otherNodeId) ->
                            lowerBinaryNode node valueNodeId otherNodeId (fun value other -> ExecutionInstructionKind.StringEndsWith(value, other))
                        | TypedSemanticNodeKind.StringSplit(valueNodeId, otherNodeId) ->
                            lowerBinaryNode node valueNodeId otherNodeId (fun value other -> ExecutionInstructionKind.StringSplit(value, other))
                        | TypedSemanticNodeKind.StringSplitLines(valueNodeId, breaksNodeId) ->
                            lowerBinaryNode node valueNodeId breaksNodeId (fun value breaks -> ExecutionInstructionKind.StringSplitLines(value, breaks))
                        | TypedSemanticNodeKind.StringJoin(listNodeId, separatorNodeId) ->
                            lowerBinaryNode node listNodeId separatorNodeId (fun list separator -> ExecutionInstructionKind.StringJoin(list, separator))
                        | TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.StringEqual(left, right))
                        | TypedSemanticNodeKind.StringLessThan(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.StringLessThan(left, right))
                        | TypedSemanticNodeKind.ValueEqual(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.ValueEqual(left, right))
                        | TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.StringConcat(left, right))
                        | TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64WrappingAdd(left, right))
                        | TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64WrappingSubtract(left, right))
                        | TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64WrappingMultiply(left, right))
                        | TypedSemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64FloorDivide(left, right))
                        | TypedSemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64FloorModulo(left, right))
                        | TypedSemanticNodeKind.Int64Min(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64Min(left, right))
                        | TypedSemanticNodeKind.Int64Max(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64Max(left, right))
                        | TypedSemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64CheckedAdd(left, right))
                        | TypedSemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64CheckedSubtract(left, right))
                        | TypedSemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64CheckedMultiply(left, right))
                        | TypedSemanticNodeKind.ListRange(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.ListRange(left, right))
                        | TypedSemanticNodeKind.StringRepeat(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.StringRepeat(left, right))
                        | TypedSemanticNodeKind.Float64Round(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Float64Round(left, right))
                        | TypedSemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64Equal(left, right))
                        | TypedSemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64LessThan(left, right))
                        | TypedSemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64LessThanOrEqual(left, right))
                        | TypedSemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64GreaterThan(left, right))
                        | TypedSemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64GreaterThanOrEqual(left, right))
                        | TypedSemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Float64Add(left, right))
                        | TypedSemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Float64Subtract(left, right))
                        | TypedSemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Float64Multiply(left, right))
                        | TypedSemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Float64Divide(left, right))
                        | TypedSemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Float64Equal(left, right))
                        | TypedSemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Float64LessThan(left, right))
                        | TypedSemanticNodeKind.Float64IsNaN valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64IsNaN
                        | TypedSemanticNodeKind.Float64IsFinite valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64IsFinite
                        | TypedSemanticNodeKind.Float64IsInfinite valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Float64IsInfinite
                        | TypedSemanticNodeKind.Int64ToFloat64 valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Int64ToFloat64
                        | TypedSemanticNodeKind.Int64WrappingAbs valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.Int64WrappingAbs
                        | TypedSemanticNodeKind.Bits32FromInt64 valueNodeId -> lowerUnaryNode node valueNodeId ExecutionInstructionKind.Bits32FromInt64
                        | TypedSemanticNodeKind.Bits32ToInt64 valueNodeId -> lowerUnaryNode node valueNodeId ExecutionInstructionKind.Bits32ToInt64
                        | TypedSemanticNodeKind.Bits32Not valueNodeId -> lowerUnaryNode node valueNodeId ExecutionInstructionKind.Bits32Not
                        | TypedSemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Bits32Xor(left, right))
                        | TypedSemanticNodeKind.Bits32And(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Bits32And(left, right))
                        | TypedSemanticNodeKind.Bits32Or(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Bits32Or(left, right))
                        | TypedSemanticNodeKind.Bits32Add(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Bits32Add(left, right))
                        | TypedSemanticNodeKind.Bits32RotateRight(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Bits32RotateRight(left, right))
                        | TypedSemanticNodeKind.Bits32ShiftRight(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Bits32ShiftRight(left, right))
                        | TypedSemanticNodeKind.Bits32ShiftLeft(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Bits32ShiftLeft(left, right))
                        | TypedSemanticNodeKind.ListEmpty ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.ListEmpty)
                        | TypedSemanticNodeKind.SetEmpty ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.SetEmpty)
                        | TypedSemanticNodeKind.MapEmpty ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.MapEmpty)
                        | TypedSemanticNodeKind.ListConstruct itemNodeIds ->
                            lowerNodeValues itemNodeIds
                            |> Result.map (fun valueIds ->
                                addInstruction currentBlock node (ExecutionInstructionKind.ListConstruct valueIds))
                        | TypedSemanticNodeKind.ListLength listNodeId ->
                            lowerUnaryNode node listNodeId ExecutionInstructionKind.ListLength
                        | TypedSemanticNodeKind.MapValues mapNodeId ->
                            lowerUnaryNode node mapNodeId ExecutionInstructionKind.MapValues
                        | TypedSemanticNodeKind.MapKeys mapNodeId ->
                            lowerUnaryNode node mapNodeId ExecutionInstructionKind.MapKeys
                        | TypedSemanticNodeKind.SetToList mapNodeId ->
                            lowerUnaryNode node mapNodeId ExecutionInstructionKind.SetToList
                        | TypedSemanticNodeKind.MapSize mapNodeId ->
                            lowerUnaryNode node mapNodeId ExecutionInstructionKind.MapSize
                        | TypedSemanticNodeKind.SetFromList mapNodeId ->
                            lowerUnaryNode node mapNodeId ExecutionInstructionKind.SetFromList
                        | TypedSemanticNodeKind.SetSize mapNodeId ->
                            lowerUnaryNode node mapNodeId ExecutionInstructionKind.SetSize
                        | TypedSemanticNodeKind.ListReverse listNodeId ->
                            lowerUnaryNode node listNodeId ExecutionInstructionKind.ListReverse
                        | TypedSemanticNodeKind.ListLast listNodeId ->
                            lowerUnaryNode node listNodeId ExecutionInstructionKind.ListLast
                        | TypedSemanticNodeKind.ListDropLast listNodeId ->
                            lowerUnaryNode node listNodeId ExecutionInstructionKind.ListDropLast
                        | TypedSemanticNodeKind.ListItemAt(listNodeId, indexNodeId) ->
                            lowerBinaryNode node listNodeId indexNodeId (fun listValueId indexValueId ->
                                ExecutionInstructionKind.ListItemAt(listValueId, indexValueId))
                        | TypedSemanticNodeKind.MapRemove(mapNodeId, keyNodeId) ->
                            lowerBinaryNode node mapNodeId keyNodeId (fun mapValueId keyValueId ->
                                ExecutionInstructionKind.MapRemove(mapValueId, keyValueId))
                        | TypedSemanticNodeKind.SetInsert(mapNodeId, itemNodeId) ->
                            lowerBinaryNode node mapNodeId itemNodeId (fun setValueId itemValueId ->
                                ExecutionInstructionKind.SetInsert(setValueId, itemValueId))
                        | TypedSemanticNodeKind.SetRemove(mapNodeId, itemNodeId) ->
                            lowerBinaryNode node mapNodeId itemNodeId (fun setValueId itemValueId ->
                                ExecutionInstructionKind.SetRemove(setValueId, itemValueId))
                        | TypedSemanticNodeKind.MapContainsKey(mapNodeId, keyNodeId) ->
                            lowerBinaryNode node mapNodeId keyNodeId (fun mapValueId keyValueId ->
                                ExecutionInstructionKind.MapContainsKey(mapValueId, keyValueId))
                        | TypedSemanticNodeKind.SetContains(mapNodeId, itemNodeId) ->
                            lowerBinaryNode node mapNodeId itemNodeId (fun setValueId itemValueId ->
                                ExecutionInstructionKind.SetContains(setValueId, itemValueId))
                        | TypedSemanticNodeKind.ListIndexOf(mapNodeId, itemNodeId) ->
                            lowerBinaryNode node mapNodeId itemNodeId (fun listValueId itemValueId ->
                                ExecutionInstructionKind.ListIndexOf(listValueId, itemValueId))
                        | TypedSemanticNodeKind.MapGet(mapNodeId, keyNodeId) ->
                            lowerBinaryNode node mapNodeId keyNodeId (fun mapValueId keyValueId ->
                                ExecutionInstructionKind.MapGet(mapValueId, keyValueId))
                        | TypedSemanticNodeKind.MapGetOrDefault(mapNodeId, keyNodeId, defaultNodeId) ->
                            lowerTernaryNode node mapNodeId keyNodeId defaultNodeId (fun mapValueId keyValueId defaultValueId ->
                                ExecutionInstructionKind.MapGetOrDefault(mapValueId, keyValueId, defaultValueId))
                        | TypedSemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) ->
                            lowerTernaryNode node listNodeId startNodeId endNodeId (fun listValueId startId endId ->
                                ExecutionInstructionKind.ListSlice(listValueId, startId, endId))
                        | TypedSemanticNodeKind.MapInsert(mapNodeId, keyNodeId, valueNodeId) ->
                            lowerTernaryNode node mapNodeId keyNodeId valueNodeId (fun mapValueId startId endId ->
                                ExecutionInstructionKind.MapInsert(mapValueId, startId, endId))
                        | TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
                            lowerBinaryNode node listNodeId itemNodeId (fun listValueId itemValueId ->
                                ExecutionInstructionKind.ListAppendItem(listValueId, itemValueId))
                        | TypedSemanticNodeKind.SetUnion(leftSetNodeId, rightSetNodeId) ->
                            lowerBinaryNode node leftSetNodeId rightSetNodeId (fun leftValueId rightValueId ->
                                ExecutionInstructionKind.SetUnion(leftValueId, rightValueId))
                        | TypedSemanticNodeKind.SetIntersection(leftSetNodeId, rightSetNodeId) ->
                            lowerBinaryNode node leftSetNodeId rightSetNodeId (fun leftValueId rightValueId ->
                                ExecutionInstructionKind.SetIntersection(leftValueId, rightValueId))
                        | TypedSemanticNodeKind.SetDifference(leftSetNodeId, rightSetNodeId) ->
                            lowerBinaryNode node leftSetNodeId rightSetNodeId (fun leftValueId rightValueId ->
                                ExecutionInstructionKind.SetDifference(leftValueId, rightValueId))
                        | TypedSemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
                            lowerBinaryNode node leftListNodeId rightListNodeId (fun leftValueId rightValueId ->
                                ExecutionInstructionKind.ListConcat(leftValueId, rightValueId))
                        | TypedSemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                            match lowerCallArguments context with
                            | Error error -> Error error
                            | Ok lowered ->
                                lowerBinaryNode node sourceListNodeId seedNodeId (fun source seed ->
                                    ExecutionInstructionKind.ListFold(source, seed, folderFunctionId, lowered))
                        | TypedSemanticNodeKind.MapFold(sourceMapNodeId, seedNodeId, folderFunctionId, context) ->
                            match lowerCallArguments context with
                            | Error error -> Error error
                            | Ok lowered ->
                                lowerBinaryNode node sourceMapNodeId seedNodeId (fun source seed ->
                                    ExecutionInstructionKind.MapFold(source, seed, folderFunctionId, lowered))
                        | TypedSemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                            match lowerCallArguments context with
                            | Error error -> Error error
                            | Ok lowered ->
                                lowerBinaryNode node sourceListNodeId seedNodeId (fun source seed ->
                                    ExecutionInstructionKind.ListEffectFold(source, seed, folderFunctionId, lowered))
                        | TypedSemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, context) ->
                            match requireNodeValue environment acquireNodeId, requireNodeValue environment releaseCapabilityNodeId, lowerCallArguments context with
                            | Ok acquire, Ok capability, Ok lowered ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.UseResource(acquire, capability, releaseOperationId, bodyFunctionId, lowered)))
                            | Error error, _, _
                            | _, Error error, _
                            | _, _, Error error -> Error error
                        | TypedSemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId workerFunctionId context (fun source worker lowered ->
                                ExecutionInstructionKind.ParallelAll(source, worker, lowered))
                        | TypedSemanticNodeKind.CancellationRequested ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.CancellationRequested)
                        | TypedSemanticNodeKind.TaskScope(bodyFunctionId, context) ->
                            lowerCallArguments context
                            |> Result.map (fun lowered -> addInstruction currentBlock node (ExecutionInstructionKind.TaskScope(bodyFunctionId, lowered)))
                        | TypedSemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, context, itemNodeId) ->
                            match requireNodeValue environment scopeNodeId, requireNodeValue environment itemNodeId, lowerCallArguments context with
                            | Ok scope, Ok item, Ok lowered ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.TaskStart(scope, workerFunctionId, lowered, item)))
                            | Error error, _, _
                            | _, Error error, _
                            | _, _, Error error -> Error error
                        | TypedSemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId) ->
                            let limit =
                                match limitNodeId with
                                | Some limitNodeId -> requireNodeValue environment limitNodeId |> Result.map Some
                                | None -> Ok None
                            match requireNodeValue environment tasksNodeId, limit with
                            | Ok tasks, Ok limit -> Ok(addInstruction currentBlock node (ExecutionInstructionKind.TaskWait(tasks, mode, limit)))
                            | Error error, _
                            | _, Error error -> Error error
                        | TypedSemanticNodeKind.TaskCancel taskNodeId ->
                            requireNodeValue environment taskNodeId
                            |> Result.map (fun task -> addInstruction currentBlock node (ExecutionInstructionKind.TaskCancel task))
                        | TypedSemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId mapperFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListMap(source, callback, lowered))
                        | TypedSemanticNodeKind.MapFromList(sourceListNodeId, keyOfFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId keyOfFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.MapFromList(source, callback, lowered))
                        | TypedSemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId predicateFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListFilter(source, callback, lowered))
                        | TypedSemanticNodeKind.MapFilter(sourceMapNodeId, keepFunctionId, context) ->
                            lowerListCallbackNode node sourceMapNodeId keepFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.MapFilter(source, callback, lowered))
                        | TypedSemanticNodeKind.ListSortBy(sourceListNodeId, lessThanFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId lessThanFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListSortBy(source, callback, lowered))
                        | TypedSemanticNodeKind.ListMinBy(sourceListNodeId, lessThanFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId lessThanFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListMinBy(source, callback, lowered))
                        | TypedSemanticNodeKind.MapMapValues(sourceMapNodeId, mapperFunctionId, context) ->
                            lowerListCallbackNode node sourceMapNodeId mapperFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.MapMapValues(source, callback, lowered))
                        | TypedSemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId predicateFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListAny(source, callback, lowered))
                        | TypedSemanticNodeKind.ListFindIndex(sourceListNodeId, predicateFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId predicateFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListFindIndex(source, callback, lowered))
                        | TypedSemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId predicateFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListAll(source, callback, lowered))
                        | TypedSemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
                            Ok(addInstruction currentBlock node (ExecutionInstructionKind.CapabilityConstruct(capabilityId, implementations)))
                        | TypedSemanticNodeKind.FunctionReference target ->
                            Ok(addInstruction currentBlock node (ExecutionInstructionKind.FunctionReference target))
                        | TypedSemanticNodeKind.FunctionInvoke(value, arguments) ->
                            lowerNodeValues (value :: arguments)
                            |> Result.map (fun values -> addInstruction currentBlock node (ExecutionInstructionKind.FunctionInvoke(values.Head, values.Tail)))
                        | TypedSemanticNodeKind.DirectCall(functionId, arguments) ->
                            lowerCallArguments arguments
                            |> Result.map (fun lowered ->
                                addInstruction currentBlock node (ExecutionInstructionKind.DirectCall(functionId, lowered)))
                        | TypedSemanticNodeKind.CapabilityInvoke(capabilityNodeId, operationId, arguments) ->
                            match requireNodeValue environment capabilityNodeId, lowerCapabilityArguments arguments with
                            | Ok capabilityValueId, Ok lowered ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, operationId, lowered)))
                            | Error error, _
                            | _, Error error -> Error error
                        | TypedSemanticNodeKind.ProductConstruct fields ->
                            lowerFieldValues fields
                            |> Result.map (fun lowered ->
                                addInstruction currentBlock node (ExecutionInstructionKind.ProductConstruct lowered))
                        | TypedSemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
                            requireNodeValue environment sourceNodeId
                            |> Result.map (fun sourceValueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.FieldAccess(sourceValueId, fieldId)))
                        | TypedSemanticNodeKind.ProductUpdate(sourceNodeId, fields) ->
                            match requireNodeValue environment sourceNodeId, lowerFieldValues fields with
                            | Ok sourceValueId, Ok lowered ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.ProductUpdate(sourceValueId, lowered)))
                            | Error error, _
                            | _, Error error -> Error error
                        | TypedSemanticNodeKind.UnionConstruct(variantId, fields) ->
                            lowerFieldValues fields
                            |> Result.map (fun lowered ->
                                addInstruction currentBlock node (ExecutionInstructionKind.UnionConstruct(variantId, lowered)))
                        | TypedSemanticNodeKind.OptionNone ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.OptionNone)
                        | TypedSemanticNodeKind.OptionSome valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.OptionSome
                        | TypedSemanticNodeKind.ResultOk valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.ResultOk
                        | TypedSemanticNodeKind.ResultErr errorNodeId ->
                            lowerUnaryNode node errorNodeId ExecutionInstructionKind.ResultErr
                        | TypedSemanticNodeKind.Abort _
                        | TypedSemanticNodeKind.BoolAnd _
                        | TypedSemanticNodeKind.BoolOr _
                        | TypedSemanticNodeKind.Conditional _
                        | TypedSemanticNodeKind.MatchUnion _
                        | TypedSemanticNodeKind.Bind _
                        | TypedSemanticNodeKind.MatchOption _
                        | TypedSemanticNodeKind.MatchResult _ ->
                            invalidOp "Structured semantic node reached simple Execution IR lowering."

                    match produced with
                    | Ok valueId -> bindNode node.OriginNodeId valueId
                    | Error error -> failure <- Some error

                let jumpToMerge
                    (endBlock: MutableBlock)
                    (mergeValue: ExecutionValue)
                    (valueId: ExecutionValueId)
                    (mergeBlock: MutableBlock) =
                    let argument: ExecutionJumpArgument =
                        { TargetValueId = mergeValue.Id
                          ValueId = valueId }
                    setTerminator endBlock (ExecutionTerminator.Jump(mergeBlock.Id, [ argument ]))

                let makeMerge (node: TypedSemanticNode): (ExecutionValue * MutableBlock) option =
                    match node.Completion with
                    | GraphCompletion.Aborts -> None
                    | GraphCompletion.Normal _ ->
                        let value = makeNodeValue node
                        Some(value, addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "merge") [ value ])

                let connect endpoint merge =
                    match endpoint, merge with
                    | None, _ -> ()
                    | Some(endBlock, valueId), Some(value, block) -> jumpToMerge endBlock value valueId block
                    | Some _, None -> invalidOp "Normal endpoint of an abrupt typed graph."

                let continueAt (node: TypedSemanticNode) (merge: (ExecutionValue * MutableBlock) option) =
                    match merge with
                    | None -> stopped <- true
                    | Some(value, block) ->
                        currentBlock <- block
                        bindNode node.OriginNodeId value.Id

                /// Connects the arm endpoints to the merge and continues there. When no arm reaches the
                /// merge, because each one aborts or ends in a tail call, the merge block is dropped and
                /// the graph stops here, as it does after an abort.
                let finishArms (node: TypedSemanticNode) (merge: (ExecutionValue * MutableBlock) option) endpoints =
                    match merge with
                    | Some(_, block) when endpoints |> List.forall Option.isNone ->
                        blocks.Remove block.Id |> ignore
                        stopped <- true
                    | _ ->
                        for endpoint in endpoints do
                            connect endpoint merge
                        continueAt node merge

                let lowerBind (node: TypedSemanticNode) inputNodeId (body: TypedSemanticValueArm) =
                    match requireNodeValue environment inputNodeId with
                    | Error error -> failure <- Some error
                    | Ok inputValueId ->
                        // A block parameter preserves binding provenance without a synthetic
                        // match/extraction. The jump makes the input/body boundary explicit.
                        let boundValue: ExecutionValue =
                            { Id = ExecutionIrIds.patternValue body.ValueBinding.Id
                              Type = body.ValueBinding.Type
                              Origin = ExecutionOrigin.PatternBinding(node.OriginNodeId, body.ValueBinding.Id) }
                        let bodyBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "body") [ boundValue ]
                        jumpToMerge currentBlock boundValue inputValueId bodyBlock
                        let bodyEnvironment: LoweringEnvironment =
                            { environment with
                                PatternValues = Map.add body.ValueBinding.Id boundValue.Id environment.PatternValues }
                        match lowerGraph bodyBlock bodyEnvironment (isTailNode node) body.Body with
                        | Error error -> failure <- Some error
                        | Ok endpoint -> finishArms node (makeMerge node) [ endpoint ]

                let lowerConditional
                    (node: TypedSemanticNode)
                    (conditionNodeId: GraphNodeId)
                    (whenTrue: TypedSemanticGraph)
                    (whenFalse: TypedSemanticGraph) =
                    match requireNodeValue environment conditionNodeId with
                    | Error error -> failure <- Some error
                    | Ok conditionValueId ->
                        let trueBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "when-true") []
                        let falseBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "when-false") []
                        let merge = makeMerge node
                        setTerminator currentBlock (ExecutionTerminator.Branch(conditionValueId, trueBlock.Id, falseBlock.Id))
                        let armsTail = isTailNode node
                        match lowerGraph trueBlock environment armsTail whenTrue, lowerGraph falseBlock environment armsTail whenFalse with
                        | Ok trueEnd, Ok falseEnd -> finishArms node merge [ trueEnd; falseEnd ]
                        | Error error, _
                        | _, Error error -> failure <- Some error

                /// BoolAnd/BoolOr use the branch shape of their conditional expansions. The block
                /// where left decides the result forwards left itself (false for And, true for Or)
                /// to the merge; only the other block lowers, and so evaluates, the right graph.
                let lowerBoolJunction
                    (node: TypedSemanticNode)
                    (leftNodeId: GraphNodeId)
                    (right: TypedSemanticGraph)
                    (evaluateRightWhen: bool) =
                    match requireNodeValue environment leftNodeId with
                    | Error error -> failure <- Some error
                    | Ok leftValueId ->
                        let rightBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "right") []
                        let decidedBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "decided") []
                        let merge = makeMerge node
                        let trueBlock, falseBlock =
                            if evaluateRightWhen then rightBlock, decidedBlock else decidedBlock, rightBlock
                        setTerminator currentBlock (ExecutionTerminator.Branch(leftValueId, trueBlock.Id, falseBlock.Id))
                        match lowerGraph rightBlock environment (isTailNode node) right with
                        | Ok rightEnd -> finishArms node merge [ rightEnd; Some(decidedBlock, leftValueId) ]
                        | Error error -> failure <- Some error

                let lowerUnionMatch
                    (node: TypedSemanticNode)
                    (inputNodeId: GraphNodeId)
                    (arms: TypedSemanticUnionArm list) =
                    match requireNodeValue environment inputNodeId with
                    | Error error -> failure <- Some error
                    | Ok inputValueId ->
                        let merge = makeMerge node
                        let armBlocks: (TypedSemanticUnionArm * MutableBlock) list =
                            arms
                            |> List.sortBy (fun (arm: TypedSemanticUnionArm) -> StableId.value arm.VariantIds.Head)
                            |> List.map (fun arm -> arm, addBlock (ExecutionIrIds.unionBlock node.OriginNodeId arm.VariantIds.Head) [])
                        // One block per arm, named by its first variant in canonical order; one target per variant.
                        let targets: ExecutionUnionTarget list =
                            armBlocks
                            |> List.collect (fun (arm, block) ->
                                arm.VariantIds
                                |> List.map (fun variantId ->
                                    { VariantId = variantId
                                      BlockId = block.Id }))
                        setTerminator currentBlock (ExecutionTerminator.MatchUnion(inputValueId, targets))
                        let endpoints = ResizeArray<(MutableBlock * ExecutionValueId) option>()

                        for (arm: TypedSemanticUnionArm), (block: MutableBlock) in armBlocks do
                            if Option.isNone failure then
                                let mutable patternValues = environment.PatternValues
                                for fieldBinding in arm.FieldBindings |> List.sortBy (fun (item: TypedSemanticPatternField) -> StableId.value item.SourceFieldId) do
                                    let extracted =
                                        addPatternExtraction
                                            block
                                            node.OriginNodeId
                                            fieldBinding.Binding
                                            (ExecutionInstructionKind.ExtractUnionField(inputValueId, List.exactlyOne arm.VariantIds, fieldBinding.SourceFieldId))
                                    patternValues <- Map.add fieldBinding.Binding.Id extracted.Id patternValues
                                let armEnvironment: LoweringEnvironment =
                                    { environment with PatternValues = patternValues }
                                match lowerGraph block armEnvironment (isTailNode node) arm.Body with
                                | Ok endpoint -> endpoints.Add endpoint
                                | Error error -> failure <- Some error

                        if Option.isNone failure then
                            finishArms node merge (List.ofSeq endpoints)

                let lowerOptionMatch
                    (node: TypedSemanticNode)
                    (inputNodeId: GraphNodeId)
                    (someArm: TypedSemanticValueArm)
                    (noneBody: TypedSemanticGraph) =
                    match requireNodeValue environment inputNodeId with
                    | Error error -> failure <- Some error
                    | Ok inputValueId ->
                        let someBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "some") []
                        let noneBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "none") []
                        let merge = makeMerge node
                        setTerminator currentBlock (ExecutionTerminator.MatchOption(inputValueId, someBlock.Id, noneBlock.Id))
                        let extracted =
                            addPatternExtraction someBlock node.OriginNodeId someArm.ValueBinding (ExecutionInstructionKind.ExtractOptionSome inputValueId)
                        let someEnvironment: LoweringEnvironment =
                            { environment with
                                PatternValues = Map.add someArm.ValueBinding.Id extracted.Id environment.PatternValues }
                        let armsTail = isTailNode node
                        match lowerGraph someBlock someEnvironment armsTail someArm.Body, lowerGraph noneBlock environment armsTail noneBody with
                        | Ok someEnd, Ok noneEnd -> finishArms node merge [ someEnd; noneEnd ]
                        | Error error, _
                        | _, Error error -> failure <- Some error

                let lowerResultMatch
                    (node: TypedSemanticNode)
                    (inputNodeId: GraphNodeId)
                    (okArm: TypedSemanticValueArm)
                    (errArm: TypedSemanticErrorArm) =
                    match requireNodeValue environment inputNodeId with
                    | Error error -> failure <- Some error
                    | Ok inputValueId ->
                        let okBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "ok") []
                        let errBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "err") []
                        let merge = makeMerge node
                        setTerminator currentBlock (ExecutionTerminator.MatchResult(inputValueId, okBlock.Id, errBlock.Id))
                        let okExtracted =
                            addPatternExtraction okBlock node.OriginNodeId okArm.ValueBinding (ExecutionInstructionKind.ExtractResultOk inputValueId)
                        let errExtracted =
                            addPatternExtraction errBlock node.OriginNodeId errArm.ErrorBinding (ExecutionInstructionKind.ExtractResultErr inputValueId)
                        let okEnvironment: LoweringEnvironment =
                            { environment with PatternValues = Map.add okArm.ValueBinding.Id okExtracted.Id environment.PatternValues }
                        let errEnvironment: LoweringEnvironment =
                            { environment with PatternValues = Map.add errArm.ErrorBinding.Id errExtracted.Id environment.PatternValues }
                        let armsTail = isTailNode node
                        match lowerGraph okBlock okEnvironment armsTail okArm.Body, lowerGraph errBlock errEnvironment armsTail errArm.Body with
                        | Ok okEnd, Ok errEnd -> finishArms node merge [ okEnd; errEnd ]
                        | Error error, _
                        | _, Error error -> failure <- Some error

                while not stopped && not (Map.isEmpty pending) && Option.isNone failure do
                    match readyNodes () with
                    | [] ->
                        failure <-
                            Some(ExecutionIrError.UnresolvedDependencies(functionEntity.Id, pending |> Map.toList |> List.map fst))
                    | (node: TypedSemanticNode) :: _ ->
                        pending <- Map.remove node.OriginNodeId pending
                        match node.Kind with
                        | TypedSemanticNodeKind.Abort errorNodeId ->
                            match requireNodeValue environment errorNodeId with
                            | Error error -> failure <- Some error
                            | Ok valueId ->
                                setTerminator currentBlock (ExecutionTerminator.Abort(node.OriginNodeId, valueId))
                                stopped <- true
                        | TypedSemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                            lowerConditional node conditionNodeId whenTrue whenFalse
                        | TypedSemanticNodeKind.BoolAnd(leftNodeId, right) -> lowerBoolJunction node leftNodeId right true
                        | TypedSemanticNodeKind.BoolOr(leftNodeId, right) -> lowerBoolJunction node leftNodeId right false
                        | TypedSemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                            lowerUnionMatch node inputNodeId arms
                        | TypedSemanticNodeKind.Bind(inputNodeId, body) -> lowerBind node inputNodeId body
                        | TypedSemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                            lowerOptionMatch node inputNodeId someArm noneBody
                        | TypedSemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                            lowerResultMatch node inputNodeId okArm errArm
                        | TypedSemanticNodeKind.DirectCall(_, arguments) when isTailNode node ->
                            // `tailSelfCallNode` admits only a self call here. Nothing in the graph uses
                            // its value, so it binds none; the block ends and the graph stops.
                            match lowerCallArguments arguments with
                            | Error error -> failure <- Some error
                            | Ok lowered ->
                                setTerminator currentBlock (ExecutionTerminator.TailCall(node.OriginNodeId, lowered))
                                stopped <- true
                        // Every other kind is a leaf. `lowerSimpleNode` enumerates every kind and rejects
                        // the structured ones above, so a new kind is a compile error there, not here.
                        | _ -> lowerSimpleNode node

                match failure with
                | Some error -> Error error
                | None when stopped -> Ok None
                | None ->
                    requireNodeValue environment graph.ResultNodeId
                    |> Result.map (fun resultValueId -> Some(currentBlock, resultValueId))

            let entry = addBlock (ExecutionIrIds.entryBlock functionEntity.Id) []
            let initialEnvironment: LoweringEnvironment =
                { NodeValues = Map.empty
                  PatternValues = Map.empty }

            match lowerGraph entry initialEnvironment true functionEntity.Body with
            | Error error -> Error error
            | Ok endpoint ->
                match endpoint with
                | Some(endBlock, resultValueId) -> setTerminator endBlock (ExecutionTerminator.Return resultValueId)
                | None -> ()

                let finalizeBlock (block: MutableBlock): Result<ExecutionBlock, ExecutionIrError> =
                    match block.Terminator with
                    | None -> Error(ExecutionIrError.UnterminatedBlock(functionEntity.Id, block.Id))
                    | Some terminator ->
                        let finalized: ExecutionBlock =
                            { Id = block.Id
                              Parameters = block.Parameters
                              Instructions = List.ofSeq block.Instructions
                              Terminator = terminator }
                        Ok finalized

                let orderedBlocks: MutableBlock list =
                    blocks.Values
                    |> Seq.toList
                    |> List.sortBy (fun (block: MutableBlock) -> ExecutionBlockId.value block.Id)

                ResultList.traverse finalizeBlock orderedBlocks
                |> Result.map (fun finalizedBlocks ->
                    { SemanticFunctionId = functionEntity.Id
                      Parameters = executionParameters
                      ResultType = functionEntity.ResultType
                      RequiredEffects = functionEntity.RequiredEffects |> List.sortBy StableId.value
                      EntryBlockId = entry.Id
                      Blocks = finalizedBlocks })

    let lower (program: TypedSemanticProgram): Result<ExecutionProgram, ExecutionIrError> =
        let functionsById: Map<FunctionId, TypedSemanticFunction> =
            program.Functions
            |> List.map (fun (functionEntity: TypedSemanticFunction) -> functionEntity.Id, functionEntity)
            |> Map.ofList

        program.Functions
        |> List.sortBy (fun (functionEntity: TypedSemanticFunction) -> StableId.value functionEntity.Id)
        |> ResultList.traverse (lowerFunction functionsById)
        |> Result.map (fun functions ->
            { SourceSnapshotId = program.SourceSnapshotId
              Types =
                program.Types
                |> List.sortBy (fun (typeEntity: TypedSemanticType) -> StableId.value typeEntity.Id)
                |> List.map ExecutionIrDeclarations.projectType
              Effects = program.Effects |> List.sortBy StableId.value
              Capabilities =
                program.Capabilities
                |> List.sortBy (fun (capability: TypedSemanticCapability) -> StableId.value capability.Id)
                |> List.map ExecutionIrDeclarations.projectCapability
              Functions = functions })
