namespace Nayamnyam.Core

open System.Globalization
open System.Text.Json

/// Canonical serialization for backend-neutral Execution IR.
/// Collection order is normalized only where order is non-semantic.
/// Instruction order and signature/block-parameter order are preserved.
[<RequireQualifiedAccess>]
module ExecutionIrCodec =
    let schemaVersion = 4

    let private quote (value: string): string =
        JsonSerializer.Serialize(value)

    let private property (name: string) (value: string): string =
        quote name + ":" + value

    let private objectValue (properties: string list): string =
        "{" + String.concat "," properties + "}"

    let private arrayValue (items: string list): string =
        "[" + String.concat "," items + "]"

    let private stableIdValue (id: StableId<'tag>): string =
        id |> StableId.value |> quote

    let private executionValueIdValue (id: ExecutionValueId): string =
        id |> ExecutionValueId.value |> quote

    let private executionBlockIdValue (id: ExecutionBlockId): string =
        id |> ExecutionBlockId.value |> quote

    let rec private writeTypeRef (typeRef: TypeRef): string =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String ->
            objectValue [ property "kind" (quote "primitiveString") ]
        | TypeRef.Primitive PrimitiveType.Bool ->
            objectValue [ property "kind" (quote "primitiveBool") ]
        | TypeRef.Primitive PrimitiveType.Unit ->
            objectValue [ property "kind" (quote "primitiveUnit") ]
        | TypeRef.Primitive PrimitiveType.Int64 ->
            objectValue [ property "kind" (quote "primitiveInt64") ]
        | TypeRef.Named typeId ->
            objectValue
                [ property "kind" (quote "named")
                  property "typeId" (stableIdValue typeId) ]
        | TypeRef.Capability capabilityId ->
            objectValue
                [ property "kind" (quote "capability")
                  property "capabilityId" (stableIdValue capabilityId) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            objectValue
                [ property "kind" (quote "option")
                  property "itemType" (writeTypeRef itemType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            objectValue
                [ property "kind" (quote "result")
                  property "okType" (writeTypeRef okType)
                  property "errorType" (writeTypeRef errorType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            objectValue
                [ property "kind" (quote "list")
                  property "itemType" (writeTypeRef itemType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"Execution IR contains Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Execution IR contains Result with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"Execution IR contains List with {arguments.Length} type arguments."

    let private writeOrigin (origin: ExecutionOrigin): string =
        match origin with
        | ExecutionOrigin.FunctionParameter parameterId ->
            objectValue
                [ property "kind" (quote "functionParameter")
                  property "parameterId" (stableIdValue parameterId) ]
        | ExecutionOrigin.SemanticNode nodeId ->
            objectValue
                [ property "kind" (quote "semanticNode")
                  property "nodeId" (stableIdValue nodeId) ]
        | ExecutionOrigin.PatternBinding(matchNodeId, bindingId) ->
            objectValue
                [ property "kind" (quote "patternBinding")
                  property "matchNodeId" (stableIdValue matchNodeId)
                  property "bindingId" (stableIdValue bindingId) ]

    let private writeValue (value: ExecutionValue): string =
        objectValue
            [ property "id" (executionValueIdValue value.Id)
              property "type" (writeTypeRef value.Type)
              property "origin" (writeOrigin value.Origin) ]

    let private writeField (field: ExecutionField): string =
        objectValue
            [ property "id" (stableIdValue field.Id)
              property "type" (writeTypeRef field.Type) ]

    let private writeVariant (variant: ExecutionVariant): string =
        objectValue
            [ property "id" (stableIdValue variant.Id)
              property
                  "fields"
                  (variant.Fields
                   |> List.sortBy (fun field -> StableId.value field.Id)
                   |> List.map writeField
                   |> arrayValue) ]

    let private writeType (typeEntity: ExecutionType): string =
        match typeEntity.Kind with
        | ExecutionTypeKind.Product fields ->
            objectValue
                [ property "id" (stableIdValue typeEntity.Id)
                  property "kind" (quote "product")
                  property
                      "fields"
                      (fields
                       |> List.sortBy (fun field -> StableId.value field.Id)
                       |> List.map writeField
                       |> arrayValue) ]
        | ExecutionTypeKind.Union variants ->
            objectValue
                [ property "id" (stableIdValue typeEntity.Id)
                  property "kind" (quote "union")
                  property
                      "variants"
                      (variants
                       |> List.sortBy (fun variant -> StableId.value variant.Id)
                       |> List.map writeVariant
                       |> arrayValue) ]

    let private writeCapabilityParameter (parameter: ExecutionCapabilityParameter): string =
        objectValue
            [ property "id" (stableIdValue parameter.Id)
              property "type" (writeTypeRef parameter.Type) ]

    let private writeCapabilityOperation (operation: ExecutionCapabilityOperation): string =
        let baseProperties =
            [ property "id" (stableIdValue operation.Id)
              property "parameters" (operation.Parameters |> List.map writeCapabilityParameter |> arrayValue)
              property "resultType" (writeTypeRef operation.ResultType) ]

        let failureProperties =
            match operation.FailureType with
            | Some failureType -> [ property "failureType" (writeTypeRef failureType) ]
            | None -> []

        objectValue
            (baseProperties
             @ failureProperties
             @ [ property "effectId" (stableIdValue operation.EffectId) ])

    let private writeCapability (capability: ExecutionCapability): string =
        objectValue
            [ property "id" (stableIdValue capability.Id)
              property
                  "operations"
                  (capability.Operations
                   |> List.sortBy (fun operation -> StableId.value operation.Id)
                   |> List.map writeCapabilityOperation
                   |> arrayValue) ]

    let private writeCallArgument (argument: ExecutionCallArgument): string =
        objectValue
            [ property "targetParameterId" (stableIdValue argument.TargetParameterId)
              property "valueId" (executionValueIdValue argument.ValueId) ]

    let private writeCapabilityArgument (argument: ExecutionCapabilityArgument): string =
        objectValue
            [ property "targetCapabilityParameterId" (stableIdValue argument.TargetCapabilityParameterId)
              property "valueId" (executionValueIdValue argument.ValueId) ]

    let private writeFieldValue (field: ExecutionFieldValue): string =
        objectValue
            [ property "targetFieldId" (stableIdValue field.TargetFieldId)
              property "valueId" (executionValueIdValue field.ValueId) ]

    let private writeBinaryInstruction kind leftValueId rightValueId =
        [ property "kind" (quote kind)
          property "leftValueId" (executionValueIdValue leftValueId)
          property "rightValueId" (executionValueIdValue rightValueId) ]

    let private writeListCallbackInstruction kind sourceListValueId callbackFieldName callbackFunctionId =
        [ property "kind" (quote kind)
          property "sourceListValueId" (executionValueIdValue sourceListValueId)
          property callbackFieldName (stableIdValue callbackFunctionId) ]

    let private writeInstructionKind (kind: ExecutionInstructionKind): string list =
        match kind with
        | ExecutionInstructionKind.ReadParameter sourceValueId ->
            [ property "kind" (quote "readParameter")
              property "sourceValueId" (executionValueIdValue sourceValueId) ]
        | ExecutionInstructionKind.ReadPatternBinding sourceValueId ->
            [ property "kind" (quote "readPatternBinding")
              property "sourceValueId" (executionValueIdValue sourceValueId) ]
        | ExecutionInstructionKind.StringLiteral value ->
            [ property "kind" (quote "stringLiteral")
              property "value" (quote value) ]
        | ExecutionInstructionKind.BoolLiteral value ->
            [ property "kind" (quote "boolLiteral")
              property "value" (if value then "true" else "false") ]
        | ExecutionInstructionKind.UnitLiteral ->
            [ property "kind" (quote "unitLiteral") ]
        | ExecutionInstructionKind.Int64Literal value ->
            [ property "kind" (quote "int64Literal")
              property "value" (value.ToString(CultureInfo.InvariantCulture)) ]
        | ExecutionInstructionKind.StringIsEmpty valueId ->
            [ property "kind" (quote "stringIsEmpty")
              property "valueId" (executionValueIdValue valueId) ]
        | ExecutionInstructionKind.StringContains(valueId, substringId) ->
            [ property "kind" (quote "stringContains")
              property "valueId" (executionValueIdValue valueId)
              property "substringId" (executionValueIdValue substringId) ]
        | ExecutionInstructionKind.Int64WrappingAdd(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64WrappingAdd" leftValueId rightValueId
        | ExecutionInstructionKind.Int64WrappingSubtract(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64WrappingSubtract" leftValueId rightValueId
        | ExecutionInstructionKind.Int64WrappingMultiply(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64WrappingMultiply" leftValueId rightValueId
        | ExecutionInstructionKind.Int64Equal(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64Equal" leftValueId rightValueId
        | ExecutionInstructionKind.Int64LessThan(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64LessThan" leftValueId rightValueId
        | ExecutionInstructionKind.Int64LessThanOrEqual(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64LessThanOrEqual" leftValueId rightValueId
        | ExecutionInstructionKind.Int64GreaterThan(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64GreaterThan" leftValueId rightValueId
        | ExecutionInstructionKind.Int64GreaterThanOrEqual(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64GreaterThanOrEqual" leftValueId rightValueId
        | ExecutionInstructionKind.ListEmpty ->
            [ property "kind" (quote "listEmpty") ]
        | ExecutionInstructionKind.ListConstruct itemValueIds ->
            [ property "kind" (quote "listConstruct")
              // List item order is semantic. Do not sort.
              property "itemValueIds" (itemValueIds |> List.map executionValueIdValue |> arrayValue) ]
        | ExecutionInstructionKind.ListLength listValueId ->
            [ property "kind" (quote "listLength")
              property "listValueId" (executionValueIdValue listValueId) ]
        | ExecutionInstructionKind.ListAppendItem(listValueId, itemValueId) ->
            [ property "kind" (quote "listAppendItem")
              property "listValueId" (executionValueIdValue listValueId)
              property "itemValueId" (executionValueIdValue itemValueId) ]
        | ExecutionInstructionKind.ListConcat(leftListValueId, rightListValueId) ->
            [ property "kind" (quote "listConcat")
              property "leftListValueId" (executionValueIdValue leftListValueId)
              property "rightListValueId" (executionValueIdValue rightListValueId) ]
        | ExecutionInstructionKind.ListMap(sourceListValueId, mapperFunctionId) ->
            writeListCallbackInstruction "listMap" sourceListValueId "mapperFunctionId" mapperFunctionId
        | ExecutionInstructionKind.ListFilter(sourceListValueId, predicateFunctionId) ->
            writeListCallbackInstruction "listFilter" sourceListValueId "predicateFunctionId" predicateFunctionId
        | ExecutionInstructionKind.ListAny(sourceListValueId, predicateFunctionId) ->
            writeListCallbackInstruction "listAny" sourceListValueId "predicateFunctionId" predicateFunctionId
        | ExecutionInstructionKind.ListAll(sourceListValueId, predicateFunctionId) ->
            writeListCallbackInstruction "listAll" sourceListValueId "predicateFunctionId" predicateFunctionId
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            [ property "kind" (quote "directCall")
              property "functionId" (stableIdValue functionId)
              property
                  "arguments"
                  (arguments
                   |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                   |> List.map writeCallArgument
                   |> arrayValue) ]
        | ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, operationId, arguments) ->
            [ property "kind" (quote "capabilityInvoke")
              property "capabilityValueId" (executionValueIdValue capabilityValueId)
              property "operationId" (stableIdValue operationId)
              property
                  "arguments"
                  (arguments
                   |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                   |> List.map writeCapabilityArgument
                   |> arrayValue) ]
        | ExecutionInstructionKind.ProductConstruct fields ->
            [ property "kind" (quote "productConstruct")
              property
                  "fields"
                  (fields
                   |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                   |> List.map writeFieldValue
                   |> arrayValue) ]
        | ExecutionInstructionKind.FieldAccess(sourceValueId, fieldId) ->
            [ property "kind" (quote "fieldAccess")
              property "sourceValueId" (executionValueIdValue sourceValueId)
              property "fieldId" (stableIdValue fieldId) ]
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) ->
            [ property "kind" (quote "unionConstruct")
              property "variantId" (stableIdValue variantId)
              property
                  "fields"
                  (fields
                   |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                   |> List.map writeFieldValue
                   |> arrayValue) ]
        | ExecutionInstructionKind.OptionNone ->
            [ property "kind" (quote "optionNone") ]
        | ExecutionInstructionKind.OptionSome valueId ->
            [ property "kind" (quote "optionSome")
              property "valueId" (executionValueIdValue valueId) ]
        | ExecutionInstructionKind.ResultOk valueId ->
            [ property "kind" (quote "resultOk")
              property "valueId" (executionValueIdValue valueId) ]
        | ExecutionInstructionKind.ResultErr errorValueId ->
            [ property "kind" (quote "resultErr")
              property "errorValueId" (executionValueIdValue errorValueId) ]
        | ExecutionInstructionKind.ExtractUnionField(inputValueId, variantId, fieldId) ->
            [ property "kind" (quote "extractUnionField")
              property "inputValueId" (executionValueIdValue inputValueId)
              property "variantId" (stableIdValue variantId)
              property "fieldId" (stableIdValue fieldId) ]
        | ExecutionInstructionKind.ExtractOptionSome inputValueId ->
            [ property "kind" (quote "extractOptionSome")
              property "inputValueId" (executionValueIdValue inputValueId) ]
        | ExecutionInstructionKind.ExtractResultOk inputValueId ->
            [ property "kind" (quote "extractResultOk")
              property "inputValueId" (executionValueIdValue inputValueId) ]
        | ExecutionInstructionKind.ExtractResultErr inputValueId ->
            [ property "kind" (quote "extractResultErr")
              property "inputValueId" (executionValueIdValue inputValueId) ]

    let private writeInstruction (instruction: ExecutionInstruction): string =
        objectValue
            ([ property "result" (writeValue instruction.Result)
               property "originNodeId" (stableIdValue instruction.OriginNodeId) ]
             @ writeInstructionKind instruction.Kind)

    let private writeJumpArgument (argument: ExecutionJumpArgument): string =
        objectValue
            [ property "targetValueId" (executionValueIdValue argument.TargetValueId)
              property "valueId" (executionValueIdValue argument.ValueId) ]

    let private writeUnionTarget (target: ExecutionUnionTarget): string =
        objectValue
            [ property "variantId" (stableIdValue target.VariantId)
              property "blockId" (executionBlockIdValue target.BlockId) ]

    let private writeTerminator (terminator: ExecutionTerminator): string =
        match terminator with
        | ExecutionTerminator.Return valueId ->
            objectValue
                [ property "kind" (quote "return")
                  property "valueId" (executionValueIdValue valueId) ]
        | ExecutionTerminator.Jump(blockId, arguments) ->
            objectValue
                [ property "kind" (quote "jump")
                  property "blockId" (executionBlockIdValue blockId)
                  property
                      "arguments"
                      (arguments
                       |> List.sortBy (fun argument -> ExecutionValueId.value argument.TargetValueId)
                       |> List.map writeJumpArgument
                       |> arrayValue) ]
        | ExecutionTerminator.Branch(conditionValueId, whenTrue, whenFalse) ->
            objectValue
                [ property "kind" (quote "branch")
                  property "conditionValueId" (executionValueIdValue conditionValueId)
                  property "whenTrue" (executionBlockIdValue whenTrue)
                  property "whenFalse" (executionBlockIdValue whenFalse) ]
        | ExecutionTerminator.MatchUnion(inputValueId, targets) ->
            objectValue
                [ property "kind" (quote "matchUnion")
                  property "inputValueId" (executionValueIdValue inputValueId)
                  property
                      "targets"
                      (targets
                       |> List.sortBy (fun target -> StableId.value target.VariantId)
                       |> List.map writeUnionTarget
                       |> arrayValue) ]
        | ExecutionTerminator.MatchOption(inputValueId, someBlock, noneBlock) ->
            objectValue
                [ property "kind" (quote "matchOption")
                  property "inputValueId" (executionValueIdValue inputValueId)
                  property "someBlock" (executionBlockIdValue someBlock)
                  property "noneBlock" (executionBlockIdValue noneBlock) ]
        | ExecutionTerminator.MatchResult(inputValueId, okBlock, errBlock) ->
            objectValue
                [ property "kind" (quote "matchResult")
                  property "inputValueId" (executionValueIdValue inputValueId)
                  property "okBlock" (executionBlockIdValue okBlock)
                  property "errBlock" (executionBlockIdValue errBlock) ]

    let private writeBlock (block: ExecutionBlock): string =
        objectValue
            [ property "id" (executionBlockIdValue block.Id)
              property "parameters" (block.Parameters |> List.map writeValue |> arrayValue)
              // Instruction order is executable meaning. Do not sort.
              property "instructions" (block.Instructions |> List.map writeInstruction |> arrayValue)
              property "terminator" (writeTerminator block.Terminator) ]

    let private writeParameter (parameter: ExecutionParameter): string =
        objectValue
            [ property "semanticParameterId" (stableIdValue parameter.SemanticParameterId)
              property "value" (writeValue parameter.Value) ]

    let private writeFunction (functionEntity: ExecutionFunction): string =
        objectValue
            [ property "semanticFunctionId" (stableIdValue functionEntity.SemanticFunctionId)
              property "parameters" (functionEntity.Parameters |> List.map writeParameter |> arrayValue)
              property "resultType" (writeTypeRef functionEntity.ResultType)
              property
                  "requiredEffects"
                  (functionEntity.RequiredEffects
                   |> List.sortBy StableId.value
                   |> List.map stableIdValue
                   |> arrayValue)
              property "entryBlockId" (executionBlockIdValue functionEntity.EntryBlockId)
              property
                  "blocks"
                  (functionEntity.Blocks
                   |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)
                   |> List.map writeBlock
                   |> arrayValue) ]

    let serialize (program: ExecutionProgram): string =
        objectValue
            [ property "schemaVersion" (string schemaVersion)
              property "sourceSnapshotId" (stableIdValue program.SourceSnapshotId)
              property
                  "types"
                  (program.Types
                   |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)
                   |> List.map writeType
                   |> arrayValue)
              property
                  "effects"
                  (program.Effects
                   |> List.sortBy StableId.value
                   |> List.map stableIdValue
                   |> arrayValue)
              property
                  "capabilities"
                  (program.Capabilities
                   |> List.sortBy (fun capability -> StableId.value capability.Id)
                   |> List.map writeCapability
                   |> arrayValue)
              property
                  "functions"
                  (program.Functions
                   |> List.sortBy (fun functionEntity -> StableId.value functionEntity.SemanticFunctionId)
                   |> List.map writeFunction
                   |> arrayValue) ]
