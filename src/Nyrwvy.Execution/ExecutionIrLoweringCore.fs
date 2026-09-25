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

            let rec lowerGraph
                (startBlock: MutableBlock)
                (initialEnvironment: LoweringEnvironment)
                (graph: TypedSemanticGraph)
                : Result<MutableBlock * ExecutionValueId, ExecutionIrError> =
                let mutable currentBlock = startBlock
                let mutable environment = initialEnvironment
                let mutable pending: Map<GraphNodeId, TypedSemanticNode> =
                    graph.Nodes
                    |> List.map (fun (node: TypedSemanticNode) -> node.OriginNodeId, node)
                    |> Map.ofList
                let mutable failure: ExecutionIrError option = None

                let bindNode (nodeId: GraphNodeId) (valueId: ExecutionValueId) =
                    environment <-
                        { environment with
                            NodeValues = Map.add nodeId valueId environment.NodeValues }

                let readyNodes (): TypedSemanticNode list =
                    pending
                    |> Map.toList
                    |> List.map snd
                    |> List.filter (fun (node: TypedSemanticNode) ->
                        ExecutionIrAnalysis.dependencies node
                        |> List.forall (fun dependencyId -> Map.containsKey dependencyId environment.NodeValues))
                    |> List.sortBy (fun node -> StableId.value node.OriginNodeId)

                let lowerFieldValues
                    (fields: TypedSemanticFieldValue list)
                    : Result<ExecutionFieldValue list, ExecutionIrError> =
                    let rec loop
                        (acc: ExecutionFieldValue list)
                        (remaining: TypedSemanticFieldValue list)
                        : Result<ExecutionFieldValue list, ExecutionIrError> =
                        match remaining with
                        | [] ->
                            acc
                            |> List.rev
                            |> List.sortBy (fun (field: ExecutionFieldValue) -> StableId.value field.TargetFieldId)
                            |> Ok
                        | field :: rest ->
                            match requireNodeValue environment field.ValueNodeId with
                            | Error error -> Error error
                            | Ok valueId ->
                                let lowered: ExecutionFieldValue =
                                    { TargetFieldId = field.TargetFieldId
                                      ValueId = valueId }
                                loop (lowered :: acc) rest
                    loop [] fields

                let lowerCallArguments
                    (arguments: TypedSemanticArgument list)
                    : Result<ExecutionCallArgument list, ExecutionIrError> =
                    let rec loop
                        (acc: ExecutionCallArgument list)
                        (remaining: TypedSemanticArgument list)
                        : Result<ExecutionCallArgument list, ExecutionIrError> =
                        match remaining with
                        | [] ->
                            acc
                            |> List.rev
                            |> List.sortBy (fun (argument: ExecutionCallArgument) -> StableId.value argument.TargetParameterId)
                            |> Ok
                        | argument :: rest ->
                            match requireNodeValue environment argument.ValueNodeId with
                            | Error error -> Error error
                            | Ok valueId ->
                                let lowered: ExecutionCallArgument =
                                    { TargetParameterId = argument.TargetParameterId
                                      ValueId = valueId }
                                loop (lowered :: acc) rest
                    loop [] arguments

                let lowerCapabilityArguments
                    (arguments: TypedSemanticCapabilityArgument list)
                    : Result<ExecutionCapabilityArgument list, ExecutionIrError> =
                    let rec loop
                        (acc: ExecutionCapabilityArgument list)
                        (remaining: TypedSemanticCapabilityArgument list)
                        : Result<ExecutionCapabilityArgument list, ExecutionIrError> =
                        match remaining with
                        | [] ->
                            acc
                            |> List.rev
                            |> List.sortBy (fun (argument: ExecutionCapabilityArgument) -> StableId.value argument.TargetCapabilityParameterId)
                            |> Ok
                        | argument :: rest ->
                            match requireNodeValue environment argument.ValueNodeId with
                            | Error error -> Error error
                            | Ok valueId ->
                                let lowered: ExecutionCapabilityArgument =
                                    { TargetCapabilityParameterId = argument.TargetCapabilityParameterId
                                      ValueId = valueId }
                                loop (lowered :: acc) rest
                    loop [] arguments

                let lowerBinaryNode node leftNodeId rightNodeId constructor =
                    match requireNodeValue environment leftNodeId, requireNodeValue environment rightNodeId with
                    | Ok leftValueId, Ok rightValueId ->
                        Ok(addInstruction currentBlock node (constructor leftValueId rightValueId))
                    | Error error, _
                    | _, Error error -> Error error

                let lowerUnaryNode node valueNodeId constructor =
                    requireNodeValue environment valueNodeId
                    |> Result.map (fun valueId ->
                        addInstruction currentBlock node (constructor valueId))

                let lowerNodeValues (nodeIds: GraphNodeId list): Result<ExecutionValueId list, ExecutionIrError> =
                    let rec loop acc remaining =
                        match remaining with
                        | [] -> Ok(List.rev acc)
                        | nodeId :: rest ->
                            match requireNodeValue environment nodeId with
                            | Error error -> Error error
                            | Ok valueId -> loop (valueId :: acc) rest
                    loop [] nodeIds

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
                            requireNodeValue environment valueNodeId
                            |> Result.map (fun valueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.StringIsEmpty valueId))
                        | TypedSemanticNodeKind.Int64ToString valueNodeId ->
                            requireNodeValue environment valueNodeId
                            |> Result.map (fun valueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.Int64ToString valueId))
                        | TypedSemanticNodeKind.StringParseInt64 valueNodeId ->
                            requireNodeValue environment valueNodeId
                            |> Result.map (fun valueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.StringParseInt64 valueId))
                        | TypedSemanticNodeKind.StringLength valueNodeId ->
                            lowerUnaryNode node valueNodeId ExecutionInstructionKind.StringLength
                        | TypedSemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
                            match
                                requireNodeValue environment valueNodeId,
                                requireNodeValue environment startNodeId,
                                requireNodeValue environment endNodeId
                            with
                            | Ok valueId, Ok startId, Ok endId ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.StringSlice(valueId, startId, endId)))
                            | Error error, _, _
                            | _, Error error, _
                            | _, _, Error error -> Error error
                        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                            match requireNodeValue environment valueNodeId, requireNodeValue environment substringNodeId with
                            | Ok valueId, Ok substringId ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.StringContains(valueId, substringId)))
                            | Error error, _
                            | _, Error error -> Error error
                        | TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.StringEqual(left, right))
                        | TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.StringConcat(left, right))
                        | TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64WrappingAdd(left, right))
                        | TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64WrappingSubtract(left, right))
                        | TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
                            lowerBinaryNode node leftNodeId rightNodeId (fun left right -> ExecutionInstructionKind.Int64WrappingMultiply(left, right))
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
                        | TypedSemanticNodeKind.ListEmpty ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.ListEmpty)
                        | TypedSemanticNodeKind.ListConstruct itemNodeIds ->
                            lowerNodeValues itemNodeIds
                            |> Result.map (fun valueIds ->
                                addInstruction currentBlock node (ExecutionInstructionKind.ListConstruct valueIds))
                        | TypedSemanticNodeKind.ListLength listNodeId ->
                            requireNodeValue environment listNodeId
                            |> Result.map (fun listValueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.ListLength listValueId))
                        | TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
                            lowerBinaryNode node listNodeId itemNodeId (fun listValueId itemValueId ->
                                ExecutionInstructionKind.ListAppendItem(listValueId, itemValueId))
                        | TypedSemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
                            lowerBinaryNode node leftListNodeId rightListNodeId (fun leftValueId rightValueId ->
                                ExecutionInstructionKind.ListConcat(leftValueId, rightValueId))
                        | TypedSemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                            match lowerCallArguments context with
                            | Error error -> Error error
                            | Ok lowered ->
                                lowerBinaryNode node sourceListNodeId seedNodeId (fun source seed ->
                                    ExecutionInstructionKind.ListFold(source, seed, folderFunctionId, lowered))
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
                        | TypedSemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId mapperFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListMap(source, callback, lowered))
                        | TypedSemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId predicateFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListFilter(source, callback, lowered))
                        | TypedSemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId predicateFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListAny(source, callback, lowered))
                        | TypedSemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, context) ->
                            lowerListCallbackNode node sourceListNodeId predicateFunctionId context (fun source callback lowered ->
                                ExecutionInstructionKind.ListAll(source, callback, lowered))
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
                        | TypedSemanticNodeKind.UnionConstruct(variantId, fields) ->
                            lowerFieldValues fields
                            |> Result.map (fun lowered ->
                                addInstruction currentBlock node (ExecutionInstructionKind.UnionConstruct(variantId, lowered)))
                        | TypedSemanticNodeKind.OptionNone ->
                            Ok(addInstruction currentBlock node ExecutionInstructionKind.OptionNone)
                        | TypedSemanticNodeKind.OptionSome valueNodeId ->
                            requireNodeValue environment valueNodeId
                            |> Result.map (fun valueId -> addInstruction currentBlock node (ExecutionInstructionKind.OptionSome valueId))
                        | TypedSemanticNodeKind.ResultOk valueNodeId ->
                            requireNodeValue environment valueNodeId
                            |> Result.map (fun valueId -> addInstruction currentBlock node (ExecutionInstructionKind.ResultOk valueId))
                        | TypedSemanticNodeKind.ResultErr errorNodeId ->
                            requireNodeValue environment errorNodeId
                            |> Result.map (fun valueId -> addInstruction currentBlock node (ExecutionInstructionKind.ResultErr valueId))
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

                let makeMerge (node: TypedSemanticNode): ExecutionValue * MutableBlock =
                    let mergeValue = makeNodeValue node
                    let mergeBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "merge") [ mergeValue ]
                    mergeValue, mergeBlock

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
                        match lowerGraph bodyBlock bodyEnvironment body.Body with
                        | Error error -> failure <- Some error
                        | Ok(endBlock, resultValueId) ->
                            let resultValue, continuation = makeMerge node
                            jumpToMerge endBlock resultValue resultValueId continuation
                            currentBlock <- continuation
                            bindNode node.OriginNodeId resultValue.Id

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
                        let mergeValue, mergeBlock = makeMerge node
                        setTerminator currentBlock (ExecutionTerminator.Branch(conditionValueId, trueBlock.Id, falseBlock.Id))
                        match lowerGraph trueBlock environment whenTrue, lowerGraph falseBlock environment whenFalse with
                        | Ok(trueEnd, trueValueId), Ok(falseEnd, falseValueId) ->
                            jumpToMerge trueEnd mergeValue trueValueId mergeBlock
                            jumpToMerge falseEnd mergeValue falseValueId mergeBlock
                            currentBlock <- mergeBlock
                            bindNode node.OriginNodeId mergeValue.Id
                        | Error error, _
                        | _, Error error -> failure <- Some error

                let lowerUnionMatch
                    (node: TypedSemanticNode)
                    (inputNodeId: GraphNodeId)
                    (arms: TypedSemanticUnionArm list) =
                    match requireNodeValue environment inputNodeId with
                    | Error error -> failure <- Some error
                    | Ok inputValueId ->
                        let mergeValue, mergeBlock = makeMerge node
                        let armBlocks: (TypedSemanticUnionArm * MutableBlock) list =
                            arms
                            |> List.sortBy (fun (arm: TypedSemanticUnionArm) -> StableId.value arm.VariantId)
                            |> List.map (fun arm -> arm, addBlock (ExecutionIrIds.unionBlock node.OriginNodeId arm.VariantId) [])
                        let targets: ExecutionUnionTarget list =
                            armBlocks
                            |> List.map (fun (arm, block) ->
                                { VariantId = arm.VariantId
                                  BlockId = block.Id })
                        setTerminator currentBlock (ExecutionTerminator.MatchUnion(inputValueId, targets))

                        for (arm: TypedSemanticUnionArm), (block: MutableBlock) in armBlocks do
                            if Option.isNone failure then
                                let mutable patternValues = environment.PatternValues
                                for fieldBinding in arm.FieldBindings |> List.sortBy (fun (item: TypedSemanticPatternField) -> StableId.value item.SourceFieldId) do
                                    let extracted =
                                        addPatternExtraction
                                            block
                                            node.OriginNodeId
                                            fieldBinding.Binding
                                            (ExecutionInstructionKind.ExtractUnionField(inputValueId, arm.VariantId, fieldBinding.SourceFieldId))
                                    patternValues <- Map.add fieldBinding.Binding.Id extracted.Id patternValues
                                let armEnvironment: LoweringEnvironment =
                                    { environment with PatternValues = patternValues }
                                match lowerGraph block armEnvironment arm.Body with
                                | Ok(endBlock, resultValueId) -> jumpToMerge endBlock mergeValue resultValueId mergeBlock
                                | Error error -> failure <- Some error

                        if Option.isNone failure then
                            currentBlock <- mergeBlock
                            bindNode node.OriginNodeId mergeValue.Id

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
                        let mergeValue, mergeBlock = makeMerge node
                        setTerminator currentBlock (ExecutionTerminator.MatchOption(inputValueId, someBlock.Id, noneBlock.Id))
                        let extracted =
                            addPatternExtraction someBlock node.OriginNodeId someArm.ValueBinding (ExecutionInstructionKind.ExtractOptionSome inputValueId)
                        let someEnvironment: LoweringEnvironment =
                            { environment with
                                PatternValues = Map.add someArm.ValueBinding.Id extracted.Id environment.PatternValues }
                        match lowerGraph someBlock someEnvironment someArm.Body, lowerGraph noneBlock environment noneBody with
                        | Ok(someEnd, someValueId), Ok(noneEnd, noneValueId) ->
                            jumpToMerge someEnd mergeValue someValueId mergeBlock
                            jumpToMerge noneEnd mergeValue noneValueId mergeBlock
                            currentBlock <- mergeBlock
                            bindNode node.OriginNodeId mergeValue.Id
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
                        let mergeValue, mergeBlock = makeMerge node
                        setTerminator currentBlock (ExecutionTerminator.MatchResult(inputValueId, okBlock.Id, errBlock.Id))
                        let okExtracted =
                            addPatternExtraction okBlock node.OriginNodeId okArm.ValueBinding (ExecutionInstructionKind.ExtractResultOk inputValueId)
                        let errExtracted =
                            addPatternExtraction errBlock node.OriginNodeId errArm.ErrorBinding (ExecutionInstructionKind.ExtractResultErr inputValueId)
                        let okEnvironment: LoweringEnvironment =
                            { environment with PatternValues = Map.add okArm.ValueBinding.Id okExtracted.Id environment.PatternValues }
                        let errEnvironment: LoweringEnvironment =
                            { environment with PatternValues = Map.add errArm.ErrorBinding.Id errExtracted.Id environment.PatternValues }
                        match lowerGraph okBlock okEnvironment okArm.Body, lowerGraph errBlock errEnvironment errArm.Body with
                        | Ok(okEnd, okValueId), Ok(errEnd, errValueId) ->
                            jumpToMerge okEnd mergeValue okValueId mergeBlock
                            jumpToMerge errEnd mergeValue errValueId mergeBlock
                            currentBlock <- mergeBlock
                            bindNode node.OriginNodeId mergeValue.Id
                        | Error error, _
                        | _, Error error -> failure <- Some error

                while not (Map.isEmpty pending) && Option.isNone failure do
                    match readyNodes () with
                    | [] ->
                        failure <-
                            Some(ExecutionIrError.UnresolvedDependencies(functionEntity.Id, pending |> Map.toList |> List.map fst))
                    | (node: TypedSemanticNode) :: _ ->
                        pending <- Map.remove node.OriginNodeId pending
                        match node.Kind with
                        | TypedSemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                            lowerConditional node conditionNodeId whenTrue whenFalse
                        | TypedSemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                            lowerUnionMatch node inputNodeId arms
                        | TypedSemanticNodeKind.Bind(inputNodeId, body) -> lowerBind node inputNodeId body
                        | TypedSemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                            lowerOptionMatch node inputNodeId someArm noneBody
                        | TypedSemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                            lowerResultMatch node inputNodeId okArm errArm
                        | TypedSemanticNodeKind.ParameterReference _
                        | TypedSemanticNodeKind.PatternReference _
                        | TypedSemanticNodeKind.StringLiteral _
                        | TypedSemanticNodeKind.BoolLiteral _
                        | TypedSemanticNodeKind.UnitLiteral
                        | TypedSemanticNodeKind.Int64Literal _
                        | TypedSemanticNodeKind.Float64Literal _
                        | TypedSemanticNodeKind.StringIsEmpty _
                        | TypedSemanticNodeKind.Int64ToString _
                        | TypedSemanticNodeKind.StringParseInt64 _
                        | TypedSemanticNodeKind.StringLength _
                        | TypedSemanticNodeKind.StringSlice _
                        | TypedSemanticNodeKind.StringContains _
                        | TypedSemanticNodeKind.StringEqual _
                        | TypedSemanticNodeKind.StringConcat _
                        | TypedSemanticNodeKind.Int64WrappingAdd _
                        | TypedSemanticNodeKind.Int64WrappingSubtract _
                        | TypedSemanticNodeKind.Int64WrappingMultiply _
                        | TypedSemanticNodeKind.Int64Equal _
                        | TypedSemanticNodeKind.Int64LessThan _
                        | TypedSemanticNodeKind.Int64LessThanOrEqual _
                        | TypedSemanticNodeKind.Int64GreaterThan _
                        | TypedSemanticNodeKind.Int64GreaterThanOrEqual _
                        | TypedSemanticNodeKind.Float64Add _
                        | TypedSemanticNodeKind.Float64Subtract _
                        | TypedSemanticNodeKind.Float64Multiply _
                        | TypedSemanticNodeKind.Float64Divide _
                        | TypedSemanticNodeKind.Float64Equal _
                        | TypedSemanticNodeKind.Float64LessThan _
                        | TypedSemanticNodeKind.Float64IsNaN _
                        | TypedSemanticNodeKind.Float64IsFinite _
                        | TypedSemanticNodeKind.Float64IsInfinite _
                        | TypedSemanticNodeKind.ListEmpty
                        | TypedSemanticNodeKind.ListConstruct _
                        | TypedSemanticNodeKind.ListLength _
                        | TypedSemanticNodeKind.ListAppendItem _
                        | TypedSemanticNodeKind.ListConcat _
                        | TypedSemanticNodeKind.ListFold _
                        | TypedSemanticNodeKind.ListEffectFold _
                        | TypedSemanticNodeKind.UseResource _
                        | TypedSemanticNodeKind.ParallelAll _
                        | TypedSemanticNodeKind.CancellationRequested
                        | TypedSemanticNodeKind.ListMap _
                        | TypedSemanticNodeKind.ListFilter _
                        | TypedSemanticNodeKind.ListAny _
                        | TypedSemanticNodeKind.ListAll _
                        | TypedSemanticNodeKind.DirectCall _
                        | TypedSemanticNodeKind.CapabilityInvoke _
                        | TypedSemanticNodeKind.ProductConstruct _
                        | TypedSemanticNodeKind.FieldAccess _
                        | TypedSemanticNodeKind.UnionConstruct _
                        | TypedSemanticNodeKind.OptionNone
                        | TypedSemanticNodeKind.OptionSome _
                        | TypedSemanticNodeKind.ResultOk _
                        | TypedSemanticNodeKind.ResultErr _ -> lowerSimpleNode node

                match failure with
                | Some error -> Error error
                | None ->
                    requireNodeValue environment graph.ResultNodeId
                    |> Result.map (fun resultValueId -> currentBlock, resultValueId)

            let entry = addBlock (ExecutionIrIds.entryBlock functionEntity.Id) []
            let initialEnvironment: LoweringEnvironment =
                { NodeValues = Map.empty
                  PatternValues = Map.empty }

            match lowerGraph entry initialEnvironment functionEntity.Body with
            | Error error -> Error error
            | Ok(endBlock, resultValueId) ->
                setTerminator endBlock (ExecutionTerminator.Return resultValueId)

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

                let rec finalize
                    (acc: ExecutionBlock list)
                    (remaining: MutableBlock list)
                    : Result<ExecutionBlock list, ExecutionIrError> =
                    match remaining with
                    | [] -> Ok(List.rev acc)
                    | block :: rest ->
                        match finalizeBlock block with
                        | Error error -> Error error
                        | Ok value -> finalize (value :: acc) rest

                let orderedBlocks: MutableBlock list =
                    blocks.Values
                    |> Seq.toList
                    |> List.sortBy (fun (block: MutableBlock) -> ExecutionBlockId.value block.Id)

                finalize [] orderedBlocks
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

        let rec lowerFunctions
            (acc: ExecutionFunction list)
            (remaining: TypedSemanticFunction list)
            : Result<ExecutionFunction list, ExecutionIrError> =
            match remaining with
            | [] -> Ok(List.rev acc)
            | functionEntity :: rest ->
                match lowerFunction functionsById functionEntity with
                | Error error -> Error error
                | Ok lowered -> lowerFunctions (lowered :: acc) rest

        program.Functions
        |> List.sortBy (fun (functionEntity: TypedSemanticFunction) -> StableId.value functionEntity.Id)
        |> lowerFunctions []
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
