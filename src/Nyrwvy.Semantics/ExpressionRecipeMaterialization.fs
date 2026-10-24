namespace Nyrwvy.Core

/// Materialization of type-checked expression recipes into semantic graph nodes.
module internal ExpressionRecipeMaterialization =
    open StructuralTransactionInternals
    open ExpressionRecipeSupport
    open ExpressionRecipeInference

    let internal bindingTypeFrames (frames: PatternBindingFrame list): PatternTypeFrame list =
        frames |> List.map (fun frame -> { Kind = frame.Kind; Slots = frame.Slots |> List.map (fun (slot, binding) -> slot, binding.Type) })

    let private resolvePatternBinding (frames: PatternBindingFrame list) (reference: RecipePatternReference) =
        match List.tryItem reference.ScopeDepth frames with
        | Some frame ->
            match frame.Slots |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
            | Some(_, binding) -> binding
            | None -> failwith "Pattern recipe passed preflight with an unavailable slot."
        | None -> failwith "Pattern recipe passed preflight with an unavailable scope."

    /// Materialization is deterministic in shape/order with no caller-controlled IDs or CSE.
    let rec internal materializeWithContext
        (index: SnapshotIndex)
        (owner: ExpressionOwnerContext)
        (cache: InferenceCache)
        (frames: PatternBindingFrame list)
        recipe
        =
        let snapshot = index.Snapshot
        let materializeRecipe child = materializeWithContext index owner cache frames child

        let materializeGraph childFrames body =
            let nodes, resultNodeId = materializeWithContext index owner cache childFrames body
            { Nodes = nodes
              ResultNodeId = resultNodeId }

        let unary kind child =
            let childNodes, childResult = materializeRecipe child
            let nodeId = StableIdAllocator.newGraphNodeId ()
            childNodes @ [ { Id = nodeId; Kind = kind childResult } ], nodeId

        let binary kind left right =
            let leftNodes, leftResult = materializeRecipe left
            let rightNodes, rightResult = materializeRecipe right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ rightNodes @ [ { Id = nodeId; Kind = kind (leftResult, rightResult) } ], nodeId

        let rec many = function
            | [] -> [], []
            | head :: tail ->
                let headNodes, headResult = materializeRecipe head
                let tailNodes, tailResults = many tail
                headNodes @ tailNodes, headResult :: tailResults

        /// Materializes context values in the callback's parameter order, like call arguments.
        let materializeContext callbackFunctionId (context: (ParameterId * ExpressionRecipe) list) =
            let callback = snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = callbackFunctionId)
            callback.Parameters
            |> List.choose (fun parameter -> context |> List.tryFind (fun (parameterId, _) -> parameterId = parameter.Id))
            |> List.fold (fun (nodes, bindings) (parameterId, value) ->
                let valueNodes, valueResult = materializeRecipe value
                nodes @ valueNodes, bindings @ [ { TargetParameterId = parameterId; ValueNodeId = valueResult } ]) ([], [])

        let listCallback kind source callbackFunctionId context =
            let sourceNodes, sourceResult = materializeRecipe source
            let contextNodes, contextBindings = materializeContext callbackFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ contextNodes @ [ { Id = nodeId; Kind = kind (sourceResult, callbackFunctionId, contextBindings) } ], nodeId

        let materializeFieldBindings (expectedFields: SemanticField list) (bindings: (FieldId * ExpressionRecipe) list) =
            let rec loop = function
                | [] -> [], []
                | (field: SemanticField) :: rest ->
                    let _, value = bindings |> List.find (fun (fieldId, _) -> fieldId = field.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetFieldId = field.Id
                      ValueNodeId = valueResult } :: restBindings
            loop expectedFields

        let materializeCallArguments (callee: SemanticFunction) (bindings: (ParameterId * ExpressionRecipe) list) =
            let rec loop = function
                | [] -> [], []
                | (parameter: SemanticParameter) :: rest ->
                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetParameterId = parameter.Id
                      ValueNodeId = valueResult } :: restBindings
            loop callee.Parameters

        let materializeCapabilityArguments
            (operation: SemanticCapabilityOperation)
            (bindings: (CapabilityParameterId * ExpressionRecipe) list)
            =
            let rec loop = function
                | [] -> [], []
                | (parameter: SemanticCapabilityParameter) :: rest ->
                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetCapabilityParameterId = parameter.Id
                      ValueNodeId = valueResult } :: restBindings
            loop operation.Parameters

        let inferHere child =
            match inferTypeWithContext index owner cache (bindingTypeFrames frames) None child with
            | Ok(GraphCompletion.Normal typeRef) -> typeRef
            | _ -> failwith "Expression recipe passed preflight but failed materialization type recovery."

        match recipe with
        | ExpressionRecipe.OriginalTarget ->
            match owner.OriginalTarget with
            | Some original -> original.Nodes, original.RootNodeId
            | None -> failwith "originalTarget passed preflight outside a targeted replacement."
        | ExpressionRecipe.Parameter parameterId ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ], nodeId
        | ExpressionRecipe.ParameterSlot index ->
            let parameterId =
                match owner.ParameterSlots |> Option.bind (List.tryItem index) with
                | Some { ParameterId = Some parameterId } -> parameterId
                | _ -> failwith "Parameter slot passed preflight without a materialized parameter ID."
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ], nodeId
        | ExpressionRecipe.PatternReference reference ->
            let binding = resolvePatternBinding frames reference
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.PatternReference binding.Id } ], nodeId
        | ExpressionRecipe.StringLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral value } ], nodeId
        | ExpressionRecipe.BoolLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.BoolLiteral value } ], nodeId
        | ExpressionRecipe.UnitLiteral ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.UnitLiteral } ], nodeId
        | ExpressionRecipe.Int64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Int64Literal value } ], nodeId
        | ExpressionRecipe.Float64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Float64Literal value } ], nodeId
        | ExpressionRecipe.StringIsEmpty value -> unary SemanticNodeKind.StringIsEmpty value
        | ExpressionRecipe.Int64ToString value -> unary SemanticNodeKind.Int64ToString value
        | ExpressionRecipe.Int64ToHexString value -> unary SemanticNodeKind.Int64ToHexString value
        | ExpressionRecipe.StringParseInt64 value -> unary SemanticNodeKind.StringParseInt64 value
        | ExpressionRecipe.StringParseInt64Hex value -> unary SemanticNodeKind.StringParseInt64Hex value
        | ExpressionRecipe.Float64ToString value -> unary SemanticNodeKind.Float64ToString value
        | ExpressionRecipe.StringParseFloat64 value -> unary SemanticNodeKind.StringParseFloat64 value
        | ExpressionRecipe.StringCodePoints value -> unary SemanticNodeKind.StringCodePoints value
        | ExpressionRecipe.StringToLower value -> unary SemanticNodeKind.StringToLower value
        | ExpressionRecipe.StringToUpper value -> unary SemanticNodeKind.StringToUpper value
        | ExpressionRecipe.StringTrim value -> unary SemanticNodeKind.StringTrim value
        | ExpressionRecipe.StringTrimStart value -> unary SemanticNodeKind.StringTrimStart value
        | ExpressionRecipe.StringTrimEnd value -> unary SemanticNodeKind.StringTrimEnd value
        | ExpressionRecipe.StringDecodeUtf8Lossy value -> unary SemanticNodeKind.StringDecodeUtf8Lossy value
        | ExpressionRecipe.StringEncodeUtf8 value -> unary SemanticNodeKind.StringEncodeUtf8 value
        | ExpressionRecipe.Float64RoundToInt64 value -> unary SemanticNodeKind.Float64RoundToInt64 value
        | ExpressionRecipe.Float64FloorToInt64 value -> unary SemanticNodeKind.Float64FloorToInt64 value
        | ExpressionRecipe.Float64Sqrt value -> unary SemanticNodeKind.Float64Sqrt value
        | ExpressionRecipe.Float64Abs value -> unary SemanticNodeKind.Float64Abs value
        | ExpressionRecipe.Float64Negate value -> unary SemanticNodeKind.Float64Negate value
        | ExpressionRecipe.Float64Floor value -> unary SemanticNodeKind.Float64Floor value
        | ExpressionRecipe.Float64Ceiling value -> unary SemanticNodeKind.Float64Ceiling value
        | ExpressionRecipe.Float64Truncate value -> unary SemanticNodeKind.Float64Truncate value
        | ExpressionRecipe.StringUtf8ByteCount value -> unary SemanticNodeKind.StringUtf8ByteCount value
        | ExpressionRecipe.StringFromCodePoint value -> unary SemanticNodeKind.StringFromCodePoint value
        | ExpressionRecipe.StringToCodePoint value -> unary SemanticNodeKind.StringToCodePoint value
        | ExpressionRecipe.StringLength value -> unary SemanticNodeKind.StringLength value
        | ExpressionRecipe.BoolNot operand -> unary SemanticNodeKind.BoolNot operand
        | ExpressionRecipe.StringSlice(value, start, stop) ->
            let valueNodes, valueResult = materializeRecipe value
            let startNodes, startResult = materializeRecipe start
            let stopNodes, stopResult = materializeRecipe stop
            let nodeId = StableIdAllocator.newGraphNodeId ()
            valueNodes @ startNodes @ stopNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.StringSlice(valueResult, startResult, stopResult) } ],
            nodeId
        | ExpressionRecipe.StringContains(value, substring) -> binary SemanticNodeKind.StringContains value substring
        | ExpressionRecipe.StringIndexOf(value, substring, from) ->
            let valueNodes, valueResult = materializeRecipe value
            let substringNodes, substringResult = materializeRecipe substring
            let fromNodes, fromResult = materializeRecipe from
            let nodeId = StableIdAllocator.newGraphNodeId ()
            valueNodes @ substringNodes @ fromNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.StringIndexOf(valueResult, substringResult, fromResult) } ],
            nodeId
        | ExpressionRecipe.StringLastIndexOfBefore(value, substring, bound) ->
            let valueNodes, valueResult = materializeRecipe value
            let substringNodes, substringResult = materializeRecipe substring
            let boundNodes, boundResult = materializeRecipe bound
            let nodeId = StableIdAllocator.newGraphNodeId ()
            valueNodes @ substringNodes @ boundNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.StringLastIndexOfBefore(valueResult, substringResult, boundResult) } ],
            nodeId
        | ExpressionRecipe.StringSpan(value, set, bound)
        | ExpressionRecipe.StringSpanBack(value, set, bound) ->
            let valueNodes, valueResult = materializeRecipe value
            let setNodes, setResult = materializeRecipe set
            let boundNodes, boundResult = materializeRecipe bound
            let nodeId = StableIdAllocator.newGraphNodeId ()
            let kind =
                match recipe with
                | ExpressionRecipe.StringSpan _ -> SemanticNodeKind.StringSpan(valueResult, setResult, boundResult)
                | _ -> SemanticNodeKind.StringSpanBack(valueResult, setResult, boundResult)
            valueNodes @ setNodes @ boundNodes @ [ { Id = nodeId; Kind = kind } ], nodeId
        | ExpressionRecipe.StringLastIndexOf(left, right) -> binary SemanticNodeKind.StringLastIndexOf left right
        | ExpressionRecipe.StringStartsWith(left, right) -> binary SemanticNodeKind.StringStartsWith left right
        | ExpressionRecipe.StringEndsWith(left, right) -> binary SemanticNodeKind.StringEndsWith left right
        | ExpressionRecipe.StringSplit(left, right) -> binary SemanticNodeKind.StringSplit left right
        | ExpressionRecipe.StringSplitLines(value, breaks) -> binary SemanticNodeKind.StringSplitLines value breaks
        | ExpressionRecipe.StringReplace(value, pattern, replacement) ->
            let valueNodes, valueResult = materializeRecipe value
            let patternNodes, patternResult = materializeRecipe pattern
            let replacementNodes, replacementResult = materializeRecipe replacement
            let nodeId = StableIdAllocator.newGraphNodeId ()
            valueNodes @ patternNodes @ replacementNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.StringReplace(valueResult, patternResult, replacementResult) } ],
            nodeId
        | ExpressionRecipe.StringJoin(list, separator) -> binary SemanticNodeKind.StringJoin list separator
        | ExpressionRecipe.StringEqual(left, right) -> binary SemanticNodeKind.StringEqual left right
        | ExpressionRecipe.StringLessThan(left, right) -> binary SemanticNodeKind.StringLessThan left right
        | ExpressionRecipe.ValueEqual(left, right) -> binary SemanticNodeKind.ValueEqual left right
        | ExpressionRecipe.StringConcat(left, right) -> binary SemanticNodeKind.StringConcat left right
        | ExpressionRecipe.Int64WrappingAdd(left, right) -> binary SemanticNodeKind.Int64WrappingAdd left right
        | ExpressionRecipe.Int64WrappingSubtract(left, right) -> binary SemanticNodeKind.Int64WrappingSubtract left right
        | ExpressionRecipe.Int64WrappingMultiply(left, right) -> binary SemanticNodeKind.Int64WrappingMultiply left right
        | ExpressionRecipe.Int64FloorDivide(left, right) -> binary SemanticNodeKind.Int64FloorDivide left right
        | ExpressionRecipe.Int64FloorModulo(left, right) -> binary SemanticNodeKind.Int64FloorModulo left right
        | ExpressionRecipe.Int64Min(left, right) -> binary SemanticNodeKind.Int64Min left right
        | ExpressionRecipe.Int64Max(left, right) -> binary SemanticNodeKind.Int64Max left right
        | ExpressionRecipe.Int64BitAnd(left, right) -> binary SemanticNodeKind.Int64BitAnd left right
        | ExpressionRecipe.Int64BitOr(left, right) -> binary SemanticNodeKind.Int64BitOr left right
        | ExpressionRecipe.Int64BitXor(left, right) -> binary SemanticNodeKind.Int64BitXor left right
        | ExpressionRecipe.Int64ShiftLeft(left, right) -> binary SemanticNodeKind.Int64ShiftLeft left right
        | ExpressionRecipe.Int64ShiftRight(left, right) -> binary SemanticNodeKind.Int64ShiftRight left right
        | ExpressionRecipe.Int64CheckedAdd(left, right) -> binary SemanticNodeKind.Int64CheckedAdd left right
        | ExpressionRecipe.Int64CheckedSubtract(left, right) -> binary SemanticNodeKind.Int64CheckedSubtract left right
        | ExpressionRecipe.Int64CheckedMultiply(left, right) -> binary SemanticNodeKind.Int64CheckedMultiply left right
        | ExpressionRecipe.ListRange(left, right) -> binary SemanticNodeKind.ListRange left right
        | ExpressionRecipe.StringRepeat(left, right) -> binary SemanticNodeKind.StringRepeat left right
        | ExpressionRecipe.Float64Round(left, right) -> binary SemanticNodeKind.Float64Round left right
        | ExpressionRecipe.Int64Equal(left, right) -> binary SemanticNodeKind.Int64Equal left right
        | ExpressionRecipe.Int64LessThan(left, right) -> binary SemanticNodeKind.Int64LessThan left right
        | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> binary SemanticNodeKind.Int64LessThanOrEqual left right
        | ExpressionRecipe.Int64GreaterThan(left, right) -> binary SemanticNodeKind.Int64GreaterThan left right
        | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> binary SemanticNodeKind.Int64GreaterThanOrEqual left right
        | ExpressionRecipe.Float64Add(left, right) -> binary SemanticNodeKind.Float64Add left right
        | ExpressionRecipe.Float64Min(left, right) -> binary SemanticNodeKind.Float64Min left right
        | ExpressionRecipe.Float64Max(left, right) -> binary SemanticNodeKind.Float64Max left right
        | ExpressionRecipe.Float64Subtract(left, right) -> binary SemanticNodeKind.Float64Subtract left right
        | ExpressionRecipe.Float64Multiply(left, right) -> binary SemanticNodeKind.Float64Multiply left right
        | ExpressionRecipe.Float64Divide(left, right) -> binary SemanticNodeKind.Float64Divide left right
        | ExpressionRecipe.Float64FloorModulo(left, right) -> binary SemanticNodeKind.Float64FloorModulo left right
        | ExpressionRecipe.Float64Equal(left, right) -> binary SemanticNodeKind.Float64Equal left right
        | ExpressionRecipe.Float64LessThan(left, right) -> binary SemanticNodeKind.Float64LessThan left right
        | ExpressionRecipe.Float64IsNaN value -> unary SemanticNodeKind.Float64IsNaN value
        | ExpressionRecipe.Float64IsFinite value -> unary SemanticNodeKind.Float64IsFinite value
        | ExpressionRecipe.Float64IsInfinite value -> unary SemanticNodeKind.Float64IsInfinite value
        | ExpressionRecipe.Int64ToFloat64 value -> unary SemanticNodeKind.Int64ToFloat64 value
        | ExpressionRecipe.Int64WrappingAbs value -> unary SemanticNodeKind.Int64WrappingAbs value
        | ExpressionRecipe.Int64BitNot value -> unary SemanticNodeKind.Int64BitNot value
        | ExpressionRecipe.Bits32FromInt64 value -> unary SemanticNodeKind.Bits32FromInt64 value
        | ExpressionRecipe.Bits32ToInt64 value -> unary SemanticNodeKind.Bits32ToInt64 value
        | ExpressionRecipe.Bits32Not value -> unary SemanticNodeKind.Bits32Not value
        | ExpressionRecipe.Bits32Xor(left, right) -> binary SemanticNodeKind.Bits32Xor left right
        | ExpressionRecipe.Bits32And(left, right) -> binary SemanticNodeKind.Bits32And left right
        | ExpressionRecipe.Bits32Or(left, right) -> binary SemanticNodeKind.Bits32Or left right
        | ExpressionRecipe.Bits32Add(left, right) -> binary SemanticNodeKind.Bits32Add left right
        | ExpressionRecipe.Bits32RotateRight(left, right) -> binary SemanticNodeKind.Bits32RotateRight left right
        | ExpressionRecipe.Bits32ShiftRight(left, right) -> binary SemanticNodeKind.Bits32ShiftRight left right
        | ExpressionRecipe.Bits32ShiftLeft(left, right) -> binary SemanticNodeKind.Bits32ShiftLeft left right
        | ExpressionRecipe.ListEmpty itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ListEmpty itemType } ], nodeId
        | ExpressionRecipe.SetEmpty itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.SetEmpty itemType } ], nodeId
        | ExpressionRecipe.MapEmpty(keyType, valueType) ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.MapEmpty(keyType, valueType) } ], nodeId
        | ExpressionRecipe.ListConstruct(itemType, items) ->
            let itemNodes, itemResults = many items
            let nodeId = StableIdAllocator.newGraphNodeId ()
            itemNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListConstruct(itemType, itemResults) } ], nodeId
        | ExpressionRecipe.ListLength list -> unary SemanticNodeKind.ListLength list
        | ExpressionRecipe.MapValues map -> unary SemanticNodeKind.MapValues map
        | ExpressionRecipe.MapKeys map -> unary SemanticNodeKind.MapKeys map
        | ExpressionRecipe.SetToList map -> unary SemanticNodeKind.SetToList map
        | ExpressionRecipe.MapSize map -> unary SemanticNodeKind.MapSize map
        | ExpressionRecipe.SetFromList list -> unary SemanticNodeKind.SetFromList list
        | ExpressionRecipe.SetSize map -> unary SemanticNodeKind.SetSize map
        | ExpressionRecipe.ListReverse list -> unary SemanticNodeKind.ListReverse list
        | ExpressionRecipe.ListLast list -> unary SemanticNodeKind.ListLast list
        | ExpressionRecipe.ListDropLast list -> unary SemanticNodeKind.ListDropLast list
        | ExpressionRecipe.ListAppendItem(list, item) -> binary SemanticNodeKind.ListAppendItem list item
        | ExpressionRecipe.ListItemAt(list, index) -> binary SemanticNodeKind.ListItemAt list index
        | ExpressionRecipe.MapRemove(map, key) -> binary SemanticNodeKind.MapRemove map key
        | ExpressionRecipe.SetInsert(map, key) -> binary SemanticNodeKind.SetInsert map key
        | ExpressionRecipe.SetRemove(map, key) -> binary SemanticNodeKind.SetRemove map key
        | ExpressionRecipe.MapContainsKey(map, key) -> binary SemanticNodeKind.MapContainsKey map key
        | ExpressionRecipe.SetContains(map, key) -> binary SemanticNodeKind.SetContains map key
        | ExpressionRecipe.ListIndexOf(list, item) -> binary SemanticNodeKind.ListIndexOf list item
        | ExpressionRecipe.ListLastIndexOf(list, item) -> binary SemanticNodeKind.ListLastIndexOf list item
        | ExpressionRecipe.ListContains(list, item) -> binary SemanticNodeKind.ListContains list item
        | ExpressionRecipe.MapGet(map, key) -> binary SemanticNodeKind.MapGet map key
        | ExpressionRecipe.MapGetOrDefault(map, key, defaultValue) ->
            let mapNodes, mapResult = materializeRecipe map
            let keyNodes, keyResult = materializeRecipe key
            let defaultNodes, defaultResult = materializeRecipe defaultValue
            let nodeId = StableIdAllocator.newGraphNodeId ()
            mapNodes @ keyNodes @ defaultNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.MapGetOrDefault(mapResult, keyResult, defaultResult) } ],
            nodeId
        | ExpressionRecipe.ListSlice(list, start, stop) ->
            let listNodes, listResult = materializeRecipe list
            let startNodes, startResult = materializeRecipe start
            let stopNodes, stopResult = materializeRecipe stop
            let nodeId = StableIdAllocator.newGraphNodeId ()
            listNodes @ startNodes @ stopNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.ListSlice(listResult, startResult, stopResult) } ],
            nodeId
        | ExpressionRecipe.ListSetAt(list, index, item) ->
            let listNodes, listResult = materializeRecipe list
            let indexNodes, indexResult = materializeRecipe index
            let itemNodes, itemResult = materializeRecipe item
            let nodeId = StableIdAllocator.newGraphNodeId ()
            listNodes @ indexNodes @ itemNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.ListSetAt(listResult, indexResult, itemResult) } ],
            nodeId
        | ExpressionRecipe.ListInsertAt(list, index, item) ->
            let listNodes, listResult = materializeRecipe list
            let indexNodes, indexResult = materializeRecipe index
            let itemNodes, itemResult = materializeRecipe item
            let nodeId = StableIdAllocator.newGraphNodeId ()
            listNodes @ indexNodes @ itemNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.ListInsertAt(listResult, indexResult, itemResult) } ],
            nodeId
        | ExpressionRecipe.ListRemoveAt(list, index) -> binary SemanticNodeKind.ListRemoveAt list index
        | ExpressionRecipe.MapInsert(map, key, value) ->
            let listNodes, listResult = materializeRecipe map
            let startNodes, startResult = materializeRecipe key
            let stopNodes, stopResult = materializeRecipe value
            let nodeId = StableIdAllocator.newGraphNodeId ()
            listNodes @ startNodes @ stopNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.MapInsert(listResult, startResult, stopResult) } ],
            nodeId
        | ExpressionRecipe.ListConcat(left, right) -> binary SemanticNodeKind.ListConcat left right
        | ExpressionRecipe.SetUnion(left, right) -> binary SemanticNodeKind.SetUnion left right
        | ExpressionRecipe.SetIntersection(left, right) -> binary SemanticNodeKind.SetIntersection left right
        | ExpressionRecipe.SetDifference(left, right) -> binary SemanticNodeKind.SetDifference left right
        | ExpressionRecipe.SetIsSubset(left, right) -> binary SemanticNodeKind.SetIsSubset left right
        | ExpressionRecipe.MapUnion(left, right) -> binary SemanticNodeKind.MapUnion left right
        | ExpressionRecipe.ListFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.MapFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MapFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.SetFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.SetFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.ListEffectFold(source, seed, folderFunctionId, context) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let seedNodes, seedResult = materializeRecipe seed
            let contextNodes, contextBindings = materializeContext folderFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ seedNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListEffectFold(sourceResult, seedResult, folderFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.UseResource(acquire, releaseCapability, releaseOperationId, bodyFunctionId, context) ->
            let acquireNodes, acquireResult = materializeRecipe acquire
            let capabilityNodes, capabilityResult = materializeRecipe releaseCapability
            let contextNodes, contextBindings = materializeContext bodyFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            acquireNodes @ capabilityNodes @ contextNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.UseResource(acquireResult, capabilityResult, releaseOperationId, bodyFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.ParallelAll(source, workerFunctionId, context) -> listCallback SemanticNodeKind.ParallelAll source workerFunctionId context
        | ExpressionRecipe.CancellationRequested ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.CancellationRequested } ], nodeId
        | ExpressionRecipe.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
            let bodyNodes, bodyBindings = materializeContext body bodyContext
            let handlerNodes, handlerBindings = materializeContext handler handlerContext
            let nodeId = StableIdAllocator.newGraphNodeId ()
            bodyNodes @ handlerNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ExceptionCatch(body, bodyBindings, handler, handlerBindings) } ], nodeId
        | ExpressionRecipe.ExceptionIsCancellation value -> unary SemanticNodeKind.ExceptionIsCancellation value
        | ExpressionRecipe.ExceptionMessage value -> unary SemanticNodeKind.ExceptionMessage value
        | ExpressionRecipe.ExceptionRethrow(value, resultType) -> unary (fun value -> SemanticNodeKind.ExceptionRethrow(value, resultType)) value
        | ExpressionRecipe.CancellationShield(bodyFunctionId, context) ->
            let contextNodes, contextBindings = materializeContext bodyFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.CancellationShield(bodyFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.TaskScope(bodyFunctionId, context) ->
            let contextNodes, contextBindings = materializeContext bodyFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.TaskScope(bodyFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.TaskStart(scope, workerFunctionId, context, item) ->
            let scopeNodes, scopeResult = materializeRecipe scope
            let itemNodes, itemResult = materializeRecipe item
            let contextNodes, contextBindings = materializeContext workerFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            scopeNodes @ itemNodes @ contextNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.TaskStart(scopeResult, workerFunctionId, contextBindings, itemResult) } ], nodeId
        | ExpressionRecipe.TaskWait(tasks, mode, limit) ->
            let tasksNodes, tasksResult = materializeRecipe tasks
            let limitNodes, limitResult =
                match limit with
                | Some limit -> materializeRecipe limit |> fun (nodes, result) -> nodes, Some result
                | None -> [], None
            let nodeId = StableIdAllocator.newGraphNodeId ()
            tasksNodes @ limitNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.TaskWait(tasksResult, mode, limitResult) } ], nodeId
        | ExpressionRecipe.OneShotCreate(scope, valueType) -> unary (fun scope -> SemanticNodeKind.OneShotCreate(scope, valueType)) scope
        | ExpressionRecipe.OneShotComplete(cell, value) -> binary SemanticNodeKind.OneShotComplete cell value
        | ExpressionRecipe.OneShotWait(cell, limit) ->
            let cellNodes, cellId = materializeRecipe cell
            let limitNodes, limitId = match limit with Some item -> let nodes, id = materializeRecipe item in nodes, Some id | None -> [], None
            let id = StableIdAllocator.newGraphNodeId ()
            cellNodes @ limitNodes @ [ { Id = id; Kind = SemanticNodeKind.OneShotWait(cellId, limitId) } ], id
        | ExpressionRecipe.TaskCancel task -> unary SemanticNodeKind.TaskCancel task
        | ExpressionRecipe.ListMap(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.ListMap source mapperFunctionId context
        | ExpressionRecipe.ListFlatMap(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.ListFlatMap source mapperFunctionId context
        | ExpressionRecipe.ListZipWith(left, right, combinerFunctionId, context) ->
            let leftNodes, leftResult = materializeRecipe left
            let rightNodes, rightResult = materializeRecipe right
            let contextNodes, contextBindings = materializeContext combinerFunctionId context
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ rightNodes @ contextNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListZipWith(leftResult, rightResult, combinerFunctionId, contextBindings) } ], nodeId
        | ExpressionRecipe.MapFromList(source, keyOfFunctionId, context) -> listCallback SemanticNodeKind.MapFromList source keyOfFunctionId context
        | ExpressionRecipe.ListFilter(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListFilter source predicateFunctionId context
        | ExpressionRecipe.MapFilter(source, keepFunctionId, context) -> listCallback SemanticNodeKind.MapFilter source keepFunctionId context
        | ExpressionRecipe.SetFilter(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.SetFilter source predicateFunctionId context
        | ExpressionRecipe.MapMapValues(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.MapMapValues source mapperFunctionId context
        | ExpressionRecipe.SetMap(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.SetMap source mapperFunctionId context
        | ExpressionRecipe.OptionMap(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.OptionMap source mapperFunctionId context
        | ExpressionRecipe.OptionAndThen(source, binderFunctionId, context) -> listCallback SemanticNodeKind.OptionAndThen source binderFunctionId context
        | ExpressionRecipe.ResultMap(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.ResultMap source mapperFunctionId context
        | ExpressionRecipe.ResultMapErr(source, mapperFunctionId, context) -> listCallback SemanticNodeKind.ResultMapErr source mapperFunctionId context
        | ExpressionRecipe.ResultAndThen(source, binderFunctionId, context) -> listCallback SemanticNodeKind.ResultAndThen source binderFunctionId context
        | ExpressionRecipe.ListSortBy(source, lessThanFunctionId, context) -> listCallback SemanticNodeKind.ListSortBy source lessThanFunctionId context
        | ExpressionRecipe.ListMinBy(source, lessThanFunctionId, context) -> listCallback SemanticNodeKind.ListMinBy source lessThanFunctionId context
        | ExpressionRecipe.ListMaxBy(source, lessThanFunctionId, context) -> listCallback SemanticNodeKind.ListMaxBy source lessThanFunctionId context
        | ExpressionRecipe.ListAny(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListAny source predicateFunctionId context
        | ExpressionRecipe.ListFindIndex(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListFindIndex source predicateFunctionId context
        | ExpressionRecipe.ListAll(source, predicateFunctionId, context) -> listCallback SemanticNodeKind.ListAll source predicateFunctionId context
        | ExpressionRecipe.ProductConstruct(typeId, _, fields) ->
            let expectedFields =
                match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                | { Kind = SemanticTypeKind.Product expectedFields } -> expectedFields
                | _ -> failwith "Product recipe passed preflight with a non-product type."
            let fieldNodes, fieldBindings = materializeFieldBindings expectedFields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            // Preflight derived or checked the type arguments; the node stores them explicitly.
            let typeArguments =
                match inferHere recipe with
                | TypeRef.Named(_, arguments) -> arguments
                | _ -> failwith "Product recipe passed preflight with a non-named result type."
            fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ProductConstruct(typeId, typeArguments, fieldBindings) } ], nodeId
        | ExpressionRecipe.FieldAccess(source, fieldId) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.FieldAccess(sourceResult, fieldId) } ], nodeId
        | ExpressionRecipe.ProductUpdate(typeId, source, fields) ->
            let updatedFields =
                match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                | { Kind = SemanticTypeKind.Product declaredFields } ->
                    declaredFields |> List.filter (fun field -> fields |> List.exists (fun (fieldId, _) -> fieldId = field.Id))
                | _ -> failwith "Product update recipe passed preflight with a non-product type."
            let sourceNodes, sourceResult = materializeRecipe source
            let fieldNodes, fieldBindings = materializeFieldBindings updatedFields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ProductUpdate(typeId, sourceResult, fieldBindings) } ], nodeId
        | ExpressionRecipe.UnionConstruct(variantId, _, fields) ->
            let variant =
                snapshot.Types
                |> List.collect (fun typeEntity ->
                    match typeEntity.Kind with
                    | SemanticTypeKind.Product _ -> []
                    | SemanticTypeKind.Union variants -> variants)
                |> List.find (fun variant -> variant.Id = variantId)
            let fieldNodes, fieldBindings = materializeFieldBindings variant.Fields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            let typeArguments =
                match inferHere recipe with
                | TypeRef.Named(_, arguments) -> arguments
                | _ -> failwith "Union recipe passed preflight with a non-named result type."
            fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.UnionConstruct(variantId, typeArguments, fieldBindings) } ], nodeId
        | ExpressionRecipe.OptionNone itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.OptionNone itemType } ], nodeId
        | ExpressionRecipe.OptionSome value -> unary SemanticNodeKind.OptionSome value
        | ExpressionRecipe.OptionGetOrDefault(option, defaultValue) -> binary SemanticNodeKind.OptionGetOrDefault option defaultValue
        | ExpressionRecipe.ResultGetOrDefault(result, defaultValue) -> binary SemanticNodeKind.ResultGetOrDefault result defaultValue
        | ExpressionRecipe.OptionOkOr(option, error) -> binary SemanticNodeKind.OptionOkOr option error
        | ExpressionRecipe.ResultOkValue value -> unary SemanticNodeKind.ResultOkValue value
        | ExpressionRecipe.ResultErrValue value -> unary SemanticNodeKind.ResultErrValue value
        | ExpressionRecipe.OptionIsSome value -> unary SemanticNodeKind.OptionIsSome value
        | ExpressionRecipe.ResultIsOk value -> unary SemanticNodeKind.ResultIsOk value
        | ExpressionRecipe.ResultOk(errorType, value) -> unary (fun valueNodeId -> SemanticNodeKind.ResultOk(errorType, valueNodeId)) value
        | ExpressionRecipe.Abort error -> unary SemanticNodeKind.Abort error
        | ExpressionRecipe.ResultErr(okType, error) -> unary (fun errorNodeId -> SemanticNodeKind.ResultErr(okType, errorNodeId)) error
        | ExpressionRecipe.CapabilityConstruct(capabilityId, implementations) ->
            let id = StableIdAllocator.newGraphNodeId ()
            [ { Id = id; Kind = SemanticNodeKind.CapabilityConstruct(capabilityId, List.sortBy fst implementations) } ], id
        | ExpressionRecipe.FunctionReference target ->
            let id = StableIdAllocator.newGraphNodeId ()
            [ { Id = id; Kind = SemanticNodeKind.FunctionReference target } ], id
        | ExpressionRecipe.FunctionInvoke(value, arguments) ->
            let nodes, values = many (value :: arguments)
            let id = StableIdAllocator.newGraphNodeId ()
            nodes @ [ { Id = id; Kind = SemanticNodeKind.FunctionInvoke(values.Head, values.Tail) } ], id
        | ExpressionRecipe.DirectCall(targetFunctionId, arguments) ->
            let callee = snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = targetFunctionId)
            let argumentNodes, argumentBindings = materializeCallArguments callee arguments
            let nodeId = StableIdAllocator.newGraphNodeId ()
            argumentNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.DirectCall(targetFunctionId, argumentBindings) } ], nodeId
        | ExpressionRecipe.SelfCall arguments ->
            let ownerFunctionId =
                match owner.SelfFunctionId with
                | Some functionId -> functionId
                | None -> failwith "SelfCall passed preflight without a materialized owner function ID."
            let rec loop slot = function
                | [] -> [], []
                | (parameter: ParameterSlotContext) :: rest ->
                    let parameterId =
                        match parameter.ParameterId with
                        | Some parameterId -> parameterId
                        | None -> failwith "SelfCall passed preflight without a materialized parameter ID."
                    let _, value = arguments |> List.find (fun (candidate, _) -> candidate = slot)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop (slot + 1) rest
                    valueNodes @ restNodes,
                    { TargetParameterId = parameterId
                      ValueNodeId = valueResult } :: restBindings
            let argumentNodes, argumentBindings = loop 0 owner.SelfParameters
            let nodeId = StableIdAllocator.newGraphNodeId ()
            argumentNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.DirectCall(ownerFunctionId, argumentBindings) } ], nodeId
        | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
            let capabilityNodes, capabilityResult = materializeRecipe capabilityValue
            let operation =
                snapshot.Capabilities
                |> List.collect (fun capability -> capability.Operations)
                |> List.find (fun operation -> operation.Id = operationId)
            let argumentNodes, argumentBindings = materializeCapabilityArguments operation arguments
            let nodeId = StableIdAllocator.newGraphNodeId ()
            capabilityNodes @ argumentNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.CapabilityInvoke(capabilityResult, operationId, argumentBindings) } ], nodeId
        | ExpressionRecipe.BoolAnd(left, right) ->
            let leftNodes, leftResult = materializeRecipe left
            let rightGraph = materializeGraph frames right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.BoolAnd(leftResult, rightGraph) } ], nodeId
        | ExpressionRecipe.BoolOr(left, right) ->
            let leftNodes, leftResult = materializeRecipe left
            let rightGraph = materializeGraph frames right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.BoolOr(leftResult, rightGraph) } ], nodeId
        | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
            let conditionNodes, conditionResult = materializeRecipe condition
            let trueGraph = materializeGraph frames whenTrue
            let falseGraph = materializeGraph frames whenFalse
            let nodeId = StableIdAllocator.newGraphNodeId ()
            conditionNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.Conditional(conditionResult, trueGraph, falseGraph) } ], nodeId
        | ExpressionRecipe.MatchUnion(input, arms) ->
            let inputNodes, inputResult = materializeRecipe input
            let inputType = inferHere input
            let variants =
                match inputType with
                | TypeRef.Named(typeId, arguments) ->
                    match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                    | { Kind = SemanticTypeKind.Union variants } as inputType ->
                        // Pattern bindings record the field types at the input's type arguments.
                        variants
                        |> List.map (fun variant ->
                            { variant with
                                Fields = variant.Fields |> List.map (fun field -> { field with Type = TypeParameters.instantiate inputType arguments field.Type }) })
                    | _ -> failwith "MatchUnion recipe passed preflight with a non-union type."
                | _ -> failwith "MatchUnion recipe passed preflight with a non-named input type."

            let resolved =
                match resolveUnionArms variants arms with
                | Ok resolved -> resolved
                | Error _ -> failwith "MatchUnion recipe passed preflight with arms that do not resolve."

            let materializedArms: UnionMatchArm list =
                resolved
                |> List.map (fun (armVariants, bodyRecipe, bindsFields) ->
                    // A one-variant arm binds every field, even an `otherwise` arm whose body cannot see them.
                    let fieldBindings =
                        match armVariants with
                        | [ variant ] ->
                            variant.Fields
                            |> List.map (fun field ->
                                { SourceFieldId = field.Id
                                  Binding =
                                    { Id = StableIdAllocator.newPatternBindingId ()
                                      DisplayName = None
                                      Type = field.Type } })
                        | _ -> []
                    let frame: PatternBindingFrame =
                        { Kind = ScopeFrameKind.UnionArm(armVariants |> List.map (fun variant -> variant.Id))
                          Slots =
                            if bindsFields then
                                fieldBindings |> List.map (fun item -> RecipePatternSlot.Field item.SourceFieldId, item.Binding)
                            else [] }
                    { VariantIds = armVariants |> List.map (fun variant -> variant.Id)
                      FieldBindings = fieldBindings
                      Body = materializeGraph (frame :: frames) bodyRecipe })

            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchUnion(inputResult, materializedArms) } ], nodeId
        | ExpressionRecipe.Bind(input, body) ->
            let inputNodes, inputResult = materializeRecipe input
            let binding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = inferHere input }
            let boundBody: ValueMatchArm =
                { ValueBinding = binding
                  Body = materializeGraph ({ Kind = ScopeFrameKind.BindBody; Slots = [ RecipePatternSlot.Value, binding ] } :: frames) body }
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.Bind(inputResult, boundBody) } ], nodeId
        | ExpressionRecipe.MatchOption(input, someBody, noneBody) ->
            let inputNodes, inputResult = materializeRecipe input
            let itemType =
                match inferHere input with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) -> itemType
                | _ -> failwith "MatchOption recipe passed preflight with a non-Option input."
            let someBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = itemType }
            let someArm: ValueMatchArm =
                { ValueBinding = someBinding
                  Body = materializeGraph ({ Kind = ScopeFrameKind.OptionSome; Slots = [ RecipePatternSlot.Value, someBinding ] } :: frames) someBody }
            let noneGraph = materializeGraph ({ Kind = ScopeFrameKind.OptionNone; Slots = [] } :: frames) noneBody
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchOption(inputResult, someArm, noneGraph) } ], nodeId
        | ExpressionRecipe.MatchResult(input, okBody, errBody) ->
            let inputNodes, inputResult = materializeRecipe input
            let okType, errorType =
                match inferHere input with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) -> okType, errorType
                | _ -> failwith "MatchResult recipe passed preflight with a non-Result input."
            let okBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = okType }
            let errBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = errorType }
            let okArm: ValueMatchArm =
                { ValueBinding = okBinding
                  Body = materializeGraph ({ Kind = ScopeFrameKind.ResultOk; Slots = [ RecipePatternSlot.Value, okBinding ] } :: frames) okBody }
            let errArm: ErrorMatchArm =
                { ErrorBinding = errBinding
                  Body = materializeGraph ({ Kind = ScopeFrameKind.ResultErr; Slots = [ RecipePatternSlot.Error, errBinding ] } :: frames) errBody }
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchResult(inputResult, okArm, errArm) } ], nodeId

    let internal materialize index target frames recipe =
        materializeWithContext index (ownerForExistingFunction target) (InferenceCache()) frames recipe
