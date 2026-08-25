namespace Nayamnyam.Core

open System.Collections.Generic

/// Lowers Typed Semantic IR into explicit backend-neutral execution order.
/// The lowerer never invents ordering between independent effects; that is
/// rejected by ExecutionIrAnalysis before blocks are created.
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

            let addBlock id parameters =
                let block =
                    { Id = id
                      Parameters = parameters
                      Instructions = ResizeArray<ExecutionInstruction>()
                      Terminator = None }
                blocks.Add(id, block)
                block

            let setTerminator (block: MutableBlock) terminator =
                block.Terminator <- Some terminator

            let parameterValues: Map<ParameterId, ExecutionValue> =
                functionEntity.Parameters
                |> List.map (fun parameter ->
                    let value =
                        { Id = ExecutionIrIds.parameterValue parameter.Id
                          Type = parameter.Type
                          Origin = ExecutionOrigin.FunctionParameter parameter.Id }
                    parameter.Id, value)
                |> Map.ofList

            let executionParameters =
                functionEntity.Parameters
                |> List.map (fun parameter ->
                    { SemanticParameterId = parameter.Id
                      Value = Map.find parameter.Id parameterValues })

            let requireNodeValue environment nodeId =
                match Map.tryFind nodeId environment.NodeValues with
                | Some valueId -> Ok valueId
                | None -> Error(ExecutionIrError.MissingNodeValue(functionEntity.Id, nodeId))

            let requirePatternValue environment bindingId =
                match Map.tryFind bindingId environment.PatternValues with
                | Some valueId -> Ok valueId
                | None -> Error(ExecutionIrError.MissingPatternValue(functionEntity.Id, bindingId))

            let makeNodeValue (node: TypedSemanticNode) =
                { Id = ExecutionIrIds.nodeValue node.OriginNodeId
                  Type = node.Type
                  Origin = ExecutionOrigin.SemanticNode node.OriginNodeId }

            let addInstruction (block: MutableBlock) (node: TypedSemanticNode) kind =
                let result = makeNodeValue node
                block.Instructions.Add(
                    { Result = result
                      OriginNodeId = node.OriginNodeId
                      Kind = kind }
                )
                result.Id

            let addPatternExtraction
                (block: MutableBlock)
                matchNodeId
                (binding: TypedSemanticPatternBinding)
                kind =
                let value =
                    { Id = ExecutionIrIds.patternValue binding.Id
                      Type = binding.Type
                      Origin = ExecutionOrigin.PatternBinding(matchNodeId, binding.Id) }
                block.Instructions.Add(
                    { Result = value
                      OriginNodeId = matchNodeId
                      Kind = kind }
                )
                value

            let rec lowerGraph
                (startBlock: MutableBlock)
                (initialEnvironment: LoweringEnvironment)
                (graph: TypedSemanticGraph)
                : Result<MutableBlock * ExecutionValueId, ExecutionIrError> =
                let mutable currentBlock = startBlock
                let mutable environment = initialEnvironment
                let mutable pending =
                    graph.Nodes
                    |> List.map (fun node -> node.OriginNodeId, node)
                    |> Map.ofList
                let mutable failure: ExecutionIrError option = None

                let bindNode nodeId valueId =
                    environment <-
                        { environment with
                            NodeValues = Map.add nodeId valueId environment.NodeValues }

                let readyNodes () =
                    pending
                    |> Map.toList
                    |> List.map snd
                    |> List.filter (fun node ->
                        ExecutionIrAnalysis.dependencies node
                        |> List.forall (fun dependencyId ->
                            Map.containsKey dependencyId environment.NodeValues))
                    |> List.sortBy (fun node -> StableId.value node.OriginNodeId)

                let lowerFieldValues (fields: TypedSemanticFieldValue list) =
                    let rec loop acc remaining =
                        match remaining with
                        | [] ->
                            acc
                            |> List.rev
                            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                            |> Ok
                        | field :: rest ->
                            match requireNodeValue environment field.ValueNodeId with
                            | Error error -> Error error
                            | Ok valueId ->
                                loop
                                    ({ TargetFieldId = field.TargetFieldId
                                       ValueId = valueId } :: acc)
                                    rest
                    loop [] fields

                let lowerCallArguments (arguments: TypedSemanticArgument list) =
                    let rec loop acc remaining =
                        match remaining with
                        | [] ->
                            acc
                            |> List.rev
                            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                            |> Ok
                        | argument :: rest ->
                            match requireNodeValue environment argument.ValueNodeId with
                            | Error error -> Error error
                            | Ok valueId ->
                                loop
                                    ({ TargetParameterId = argument.TargetParameterId
                                       ValueId = valueId } :: acc)
                                    rest
                    loop [] arguments

                let lowerCapabilityArguments (arguments: TypedSemanticCapabilityArgument list) =
                    let rec loop acc remaining =
                        match remaining with
                        | [] ->
                            acc
                            |> List.rev
                            |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                            |> Ok
                        | argument :: rest ->
                            match requireNodeValue environment argument.ValueNodeId with
                            | Error error -> Error error
                            | Ok valueId ->
                                loop
                                    ({ TargetCapabilityParameterId = argument.TargetCapabilityParameterId
                                       ValueId = valueId } :: acc)
                                    rest
                    loop [] arguments

                let lowerSimpleNode (node: TypedSemanticNode) =
                    let produced =
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
                        | TypedSemanticNodeKind.StringIsEmpty valueNodeId ->
                            requireNodeValue environment valueNodeId
                            |> Result.map (fun valueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.StringIsEmpty valueId))
                        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                            match requireNodeValue environment valueNodeId, requireNodeValue environment substringNodeId with
                            | Ok valueId, Ok substringId ->
                                Ok(addInstruction currentBlock node (ExecutionInstructionKind.StringContains(valueId, substringId)))
                            | Error error, _
                            | _, Error error -> Error error
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
                            |> Result.map (fun valueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.OptionSome valueId))
                        | TypedSemanticNodeKind.ResultOk valueNodeId ->
                            requireNodeValue environment valueNodeId
                            |> Result.map (fun valueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.ResultOk valueId))
                        | TypedSemanticNodeKind.ResultErr errorNodeId ->
                            requireNodeValue environment errorNodeId
                            |> Result.map (fun valueId ->
                                addInstruction currentBlock node (ExecutionInstructionKind.ResultErr valueId))
                        | TypedSemanticNodeKind.Conditional _
                        | TypedSemanticNodeKind.MatchUnion _
                        | TypedSemanticNodeKind.MatchOption _
                        | TypedSemanticNodeKind.MatchResult _ ->
                            invalidOp "Structured semantic node reached simple Execution IR lowering."

                    match produced with
                    | Ok valueId -> bindNode node.OriginNodeId valueId
                    | Error error -> failure <- Some error

                let jumpToMerge (endBlock: MutableBlock) mergeValue valueId (mergeBlock: MutableBlock) =
                    setTerminator
                        endBlock
                        (ExecutionTerminator.Jump(
                            mergeBlock.Id,
                            [ { TargetValueId = mergeValue.Id
                                ValueId = valueId } ]
                        ))

                let makeMerge node =
                    let mergeValue = makeNodeValue node
                    let mergeBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "merge") [ mergeValue ]
                    mergeValue, mergeBlock

                let lowerConditional node conditionNodeId whenTrue whenFalse =
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

                let lowerUnionMatch node inputNodeId arms =
                    match requireNodeValue environment inputNodeId with
                    | Error error -> failure <- Some error
                    | Ok inputValueId ->
                        let mergeValue, mergeBlock = makeMerge node
                        let armBlocks =
                            arms
                            |> List.sortBy (fun arm -> StableId.value arm.VariantId)
                            |> List.map (fun arm -> arm, addBlock (ExecutionIrIds.unionBlock node.OriginNodeId arm.VariantId) [])

                        let targets =
                            armBlocks
                            |> List.map (fun (arm, block) ->
                                { VariantId = arm.VariantId
                                  BlockId = block.Id })
                        setTerminator currentBlock (ExecutionTerminator.MatchUnion(inputValueId, targets))

                        for arm, block in armBlocks do
                            if Option.isNone failure then
                                let mutable patternValues = environment.PatternValues
                                for fieldBinding in arm.FieldBindings |> List.sortBy (fun item -> StableId.value item.SourceFieldId) do
                                    let extracted =
                                        addPatternExtraction
                                            block
                                            node.OriginNodeId
                                            fieldBinding.Binding
                                            (ExecutionInstructionKind.ExtractUnionField(inputValueId, arm.VariantId, fieldBinding.SourceFieldId))
                                    patternValues <- Map.add fieldBinding.Binding.Id extracted.Id patternValues

                                let armEnvironment = { environment with PatternValues = patternValues }
                                match lowerGraph block armEnvironment arm.Body with
                                | Ok(endBlock, resultValueId) -> jumpToMerge endBlock mergeValue resultValueId mergeBlock
                                | Error error -> failure <- Some error

                        if Option.isNone failure then
                            currentBlock <- mergeBlock
                            bindNode node.OriginNodeId mergeValue.Id

                let lowerOptionMatch node inputNodeId someArm noneBody =
                    match requireNodeValue environment inputNodeId with
                    | Error error -> failure <- Some error
                    | Ok inputValueId ->
                        let someBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "some") []
                        let noneBlock = addBlock (ExecutionIrIds.nodeBlock node.OriginNodeId "none") []
                        let mergeValue, mergeBlock = makeMerge node
                        setTerminator currentBlock (ExecutionTerminator.MatchOption(inputValueId, someBlock.Id, noneBlock.Id))

                        let extracted =
                            addPatternExtraction
                                someBlock
                                node.OriginNodeId
                                someArm.ValueBinding
                                (ExecutionInstructionKind.ExtractOptionSome inputValueId)
                        let someEnvironment =
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

                let lowerResultMatch node inputNodeId okArm errArm =
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
                        let okEnvironment =
                            { environment with
                                PatternValues = Map.add okArm.ValueBinding.Id okExtracted.Id environment.PatternValues }
                        let errEnvironment =
                            { environment with
                                PatternValues = Map.add errArm.ErrorBinding.Id errExtracted.Id environment.PatternValues }

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
                            Some(
                                ExecutionIrError.UnresolvedDependencies(
                                    functionEntity.Id,
                                    pending |> Map.toList |> List.map fst
                                )
                            )
                    | node :: _ ->
                        pending <- Map.remove node.OriginNodeId pending
                        match node.Kind with
                        | TypedSemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                            lowerConditional node conditionNodeId whenTrue whenFalse
                        | TypedSemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                            lowerUnionMatch node inputNodeId arms
                        | TypedSemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                            lowerOptionMatch node inputNodeId someArm noneBody
                        | TypedSemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                            lowerResultMatch node inputNodeId okArm errArm
                        | TypedSemanticNodeKind.ParameterReference _
                        | TypedSemanticNodeKind.PatternReference _
                        | TypedSemanticNodeKind.StringLiteral _
                        | TypedSemanticNodeKind.BoolLiteral _
                        | TypedSemanticNodeKind.UnitLiteral
                        | TypedSemanticNodeKind.StringIsEmpty _
                        | TypedSemanticNodeKind.StringContains _
                        | TypedSemanticNodeKind.DirectCall _
                        | TypedSemanticNodeKind.CapabilityInvoke _
                        | TypedSemanticNodeKind.ProductConstruct _
                        | TypedSemanticNodeKind.FieldAccess _
                        | TypedSemanticNodeKind.UnionConstruct _
                        | TypedSemanticNodeKind.OptionNone
                        | TypedSemanticNodeKind.OptionSome _
                        | TypedSemanticNodeKind.ResultOk _
                        | TypedSemanticNodeKind.ResultErr _ ->
                            lowerSimpleNode node

                match failure with
                | Some error -> Error error
                | None ->
                    requireNodeValue environment graph.ResultNodeId
                    |> Result.map (fun resultValueId -> currentBlock, resultValueId)

            let entry = addBlock (ExecutionIrIds.entryBlock functionEntity.Id) []
            let initialEnvironment =
                { NodeValues = Map.empty
                  PatternValues = Map.empty }

            match lowerGraph entry initialEnvironment functionEntity.Body with
            | Error error -> Error error
            | Ok(endBlock, resultValueId) ->
                setTerminator endBlock (ExecutionTerminator.Return resultValueId)

                let finalizeBlock (block: MutableBlock) =
                    match block.Terminator with
                    | None -> Error(ExecutionIrError.UnterminatedBlock(functionEntity.Id, block.Id))
                    | Some terminator ->
                        Ok
                            { Id = block.Id
                              Parameters = block.Parameters
                              Instructions = List.ofSeq block.Instructions
                              Terminator = terminator }

                let rec finalize acc remaining =
                    match remaining with
                    | [] -> Ok(List.rev acc)
                    | block :: rest ->
                        match finalizeBlock block with
                        | Error error -> Error error
                        | Ok value -> finalize (value :: acc) rest

                let orderedBlocks =
                    blocks.Values
                    |> Seq.toList
                    |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)

                finalize [] orderedBlocks
                |> Result.map (fun finalizedBlocks ->
                    { SemanticFunctionId = functionEntity.Id
                      Parameters = executionParameters
                      ResultType = functionEntity.ResultType
                      RequiredEffects = functionEntity.RequiredEffects |> List.sortBy StableId.value
                      EntryBlockId = entry.Id
                      Blocks = finalizedBlocks })

    let lower (program: TypedSemanticProgram): Result<ExecutionProgram, ExecutionIrError> =
        let functionsById =
            program.Functions
            |> List.map (fun functionEntity -> functionEntity.Id, functionEntity)
            |> Map.ofList

        let rec lowerFunctions acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | functionEntity :: rest ->
                match lowerFunction functionsById functionEntity with
                | Error error -> Error error
                | Ok lowered -> lowerFunctions (lowered :: acc) rest

        program.Functions
        |> List.sortBy (fun functionEntity -> StableId.value functionEntity.Id)
        |> lowerFunctions []
        |> Result.map (fun functions ->
            { SourceSnapshotId = program.SourceSnapshotId
              Types =
                program.Types
                |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)
                |> List.map ExecutionIrDeclarations.projectType
              Effects = program.Effects |> List.sortBy StableId.value
              Capabilities =
                program.Capabilities
                |> List.sortBy (fun capability -> StableId.value capability.Id)
                |> List.map ExecutionIrDeclarations.projectCapability
              Functions = functions })
