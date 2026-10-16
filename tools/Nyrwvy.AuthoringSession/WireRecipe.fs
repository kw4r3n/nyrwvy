namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open Nyrwvy.Core

module internal WireRecipe =
    open WireInput

    let rec recipe (element: JsonElement): ExpressionRecipe =
        let binary context constructor =
            ensureFields context [ "kind"; "left"; "right" ] [ "kind"; "left"; "right" ] element
            constructor(recipe (property "left" element), recipe (property "right" element))

        let unary context constructor =
            ensureFields context [ "kind"; "value" ] [ "kind"; "value" ] element
            constructor(recipe (property "value" element))

        let fieldValues name =
            array name element
            |> List.map (fun item ->
                ensureFields "field recipe" [ "fieldId"; "value" ] [ "fieldId"; "value" ] item
                id<FieldTag> "fieldId" item, recipe (property "value" item))

        let callArguments name =
            array name element
            |> List.map (fun item ->
                ensureFields "call argument" [ "parameterId"; "value" ] [ "parameterId"; "value" ] item
                id<ParameterTag> "parameterId" item, recipe (property "value" item))

        /// Optional callback context: `[{ "parameterId", "value" }]`, like call arguments.
        let namedCallbackContext name =
            match tryProperty name element with
            | None -> []
            | Some _ -> callArguments name

        let callbackContext () = namedCallbackContext "context"

        let capabilityArguments name =
            array name element
            |> List.map (fun item ->
                ensureFields "capability argument" [ "capabilityParameterId"; "value" ] [ "capabilityParameterId"; "value" ] item
                id<CapabilityParameterTag> "capabilityParameterId" item, recipe (property "value" item))

        match requiredString "kind" element with
        | "parameter" ->
            ensureFields "parameter recipe" [ "kind"; "parameterId" ] [ "kind"; "parameterId" ] element
            ExpressionRecipe.Parameter(id<ParameterTag> "parameterId" element)
        | "parameterSlot" ->
            ensureFields "parameter-slot recipe" [ "kind"; "index" ] [ "kind"; "index" ] element
            ExpressionRecipe.ParameterSlot(requiredInt "index" element)
        | "patternReference" ->
            ensureFields "pattern-reference recipe" [ "kind"; "scopeDepth"; "slot"; "fieldId" ] [ "kind"; "scopeDepth"; "slot" ] element
            let slot =
                match requiredString "slot" element with
                | "value" -> RecipePatternSlot.Value
                | "error" -> RecipePatternSlot.Error
                | "field" -> RecipePatternSlot.Field(id<FieldTag> "fieldId" element)
                | value -> fail $"Unsupported pattern slot '{value}'."
            ExpressionRecipe.PatternReference { ScopeDepth = requiredInt "scopeDepth" element; Slot = slot }
        | "stringLiteral" ->
            ensureFields "string literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.StringLiteral(requiredRunText "value" element)
        | "boolLiteral" ->
            ensureFields "bool literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.BoolLiteral(requiredBool "value" element)
        | "unitLiteral" ->
            ensureFields "unit literal recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.UnitLiteral
        | "int64Literal" ->
            ensureFields "int64 literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.Int64Literal(requiredInt64 "value" element)
        | "float64Literal" ->
            ensureFields "float64 literal recipe" [ "kind"; "bits" ] [ "kind"; "bits" ] element
            match Float64Bits.tryParseHex (requiredString "bits" element) with
            | Ok bits -> ExpressionRecipe.Float64Literal bits
            | Error error -> fail $"Invalid Float64 bits: {error}."
        | "stringIsEmpty" -> unary "string-is-empty recipe" ExpressionRecipe.StringIsEmpty
        | "int64ToString" -> unary "int64-to-string recipe" ExpressionRecipe.Int64ToString
        | "int64ToHexString" -> unary "int64-to-hex-string recipe" ExpressionRecipe.Int64ToHexString
        | "stringParseInt64" -> unary "string-parse-int64 recipe" ExpressionRecipe.StringParseInt64
        | "stringParseInt64Hex" -> unary "string-parse-int64-hex recipe" ExpressionRecipe.StringParseInt64Hex
        | "float64ToString" -> unary "float64-to-string recipe" ExpressionRecipe.Float64ToString
        | "stringParseFloat64" -> unary "string-parse-float64 recipe" ExpressionRecipe.StringParseFloat64
        | "stringCodePoints" -> unary "string-code-points recipe" ExpressionRecipe.StringCodePoints
        | "stringToLower" -> unary "string-to-lower recipe" ExpressionRecipe.StringToLower
        | "stringToUpper" -> unary "string-to-upper recipe" ExpressionRecipe.StringToUpper
        | "stringDecodeUtf8Lossy" -> unary "string-decode-utf8-lossy recipe" ExpressionRecipe.StringDecodeUtf8Lossy
        | "float64RoundToInt64" -> unary "float64-round-to-int64 recipe" ExpressionRecipe.Float64RoundToInt64
        | "float64FloorToInt64" -> unary "float64-floor-to-int64 recipe" ExpressionRecipe.Float64FloorToInt64
        | "float64Sqrt" -> unary "float64-sqrt recipe" ExpressionRecipe.Float64Sqrt
        | "float64Abs" -> unary "float64-abs recipe" ExpressionRecipe.Float64Abs
        | "float64Negate" -> unary "float64-negate recipe" ExpressionRecipe.Float64Negate
        | "float64Floor" -> unary "float64-floor recipe" ExpressionRecipe.Float64Floor
        | "float64Ceiling" -> unary "float64-ceiling recipe" ExpressionRecipe.Float64Ceiling
        | "float64Truncate" -> unary "float64-truncate recipe" ExpressionRecipe.Float64Truncate
        | "stringUtf8ByteCount" -> unary "string-utf8-byte-count recipe" ExpressionRecipe.StringUtf8ByteCount
        | "stringFromCodePoint" -> unary "string-from-code-point recipe" ExpressionRecipe.StringFromCodePoint
        | "stringToCodePoint" -> unary "string-to-code-point recipe" ExpressionRecipe.StringToCodePoint
        | "stringLength" -> unary "string-length recipe" ExpressionRecipe.StringLength
        | "stringSlice" ->
            ensureFields "string-slice recipe" [ "kind"; "value"; "start"; "end" ] [ "kind"; "value"; "start"; "end" ] element
            ExpressionRecipe.StringSlice(recipe (property "value" element), recipe (property "start" element), recipe (property "end" element))
        | "stringContains" ->
            ensureFields "string-contains recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringContains(recipe (property "value" element), recipe (property "substring" element))
        | "stringIndexOf" ->
            ensureFields "string-index-of recipe" [ "kind"; "value"; "substring"; "from" ] [ "kind"; "value"; "substring"; "from" ] element
            ExpressionRecipe.StringIndexOf(recipe (property "value" element), recipe (property "substring" element), recipe (property "from" element))
        | "stringSpan" ->
            ensureFields "string-span recipe" [ "kind"; "value"; "set"; "from" ] [ "kind"; "value"; "set"; "from" ] element
            ExpressionRecipe.StringSpan(recipe (property "value" element), recipe (property "set" element), recipe (property "from" element))
        | "stringSpanBack" ->
            ensureFields "string-span-back recipe" [ "kind"; "value"; "set"; "end" ] [ "kind"; "value"; "set"; "end" ] element
            ExpressionRecipe.StringSpanBack(recipe (property "value" element), recipe (property "set" element), recipe (property "end" element))
        | "stringLastIndexOf" ->
            ensureFields "string-last-index-of recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringLastIndexOf(recipe (property "value" element), recipe (property "substring" element))
        | "stringStartsWith" ->
            ensureFields "string-starts-with recipe" [ "kind"; "value"; "prefix" ] [ "kind"; "value"; "prefix" ] element
            ExpressionRecipe.StringStartsWith(recipe (property "value" element), recipe (property "prefix" element))
        | "stringEndsWith" ->
            ensureFields "string-ends-with recipe" [ "kind"; "value"; "suffix" ] [ "kind"; "value"; "suffix" ] element
            ExpressionRecipe.StringEndsWith(recipe (property "value" element), recipe (property "suffix" element))
        | "stringSplit" ->
            ensureFields "string-split recipe" [ "kind"; "value"; "separator" ] [ "kind"; "value"; "separator" ] element
            ExpressionRecipe.StringSplit(recipe (property "value" element), recipe (property "separator" element))
        | "stringSplitLines" ->
            ensureFields "string-split-lines recipe" [ "kind"; "value"; "breaks" ] [ "kind"; "value"; "breaks" ] element
            ExpressionRecipe.StringSplitLines(recipe (property "value" element), recipe (property "breaks" element))
        | "stringReplace" ->
            ensureFields "string-replace recipe" [ "kind"; "value"; "pattern"; "replacement" ] [ "kind"; "value"; "pattern"; "replacement" ] element
            ExpressionRecipe.StringReplace(recipe (property "value" element), recipe (property "pattern" element), recipe (property "replacement" element))
        | "stringTrim" -> unary "string-trim recipe" ExpressionRecipe.StringTrim
        | "stringTrimStart" -> unary "string-trim-start recipe" ExpressionRecipe.StringTrimStart
        | "stringTrimEnd" -> unary "string-trim-end recipe" ExpressionRecipe.StringTrimEnd
        | "stringJoin" ->
            ensureFields "string-join recipe" [ "kind"; "list"; "separator" ] [ "kind"; "list"; "separator" ] element
            ExpressionRecipe.StringJoin(recipe (property "list" element), recipe (property "separator" element))
        | "stringEqual" -> binary "string-equal recipe" ExpressionRecipe.StringEqual
        | "stringLessThan" -> binary "string-less-than recipe" ExpressionRecipe.StringLessThan
        | "valueEqual" -> binary "value-equal recipe" ExpressionRecipe.ValueEqual
        | "stringConcat" -> binary "string-concat recipe" ExpressionRecipe.StringConcat
        | "int64WrappingAdd" -> binary "int64-wrapping-add recipe" ExpressionRecipe.Int64WrappingAdd
        | "int64WrappingSubtract" -> binary "int64-wrapping-subtract recipe" ExpressionRecipe.Int64WrappingSubtract
        | "int64WrappingMultiply" -> binary "int64-wrapping-multiply recipe" ExpressionRecipe.Int64WrappingMultiply
        | "int64FloorDivide" -> binary "int64-floor-divide recipe" ExpressionRecipe.Int64FloorDivide
        | "int64FloorModulo" -> binary "int64-floor-modulo recipe" ExpressionRecipe.Int64FloorModulo
        | "int64Min" -> binary "int64-min recipe" ExpressionRecipe.Int64Min
        | "int64Max" -> binary "int64-max recipe" ExpressionRecipe.Int64Max
        | "int64CheckedAdd" -> binary "int64-checked-add recipe" ExpressionRecipe.Int64CheckedAdd
        | "int64CheckedSubtract" -> binary "int64-checked-subtract recipe" ExpressionRecipe.Int64CheckedSubtract
        | "int64CheckedMultiply" -> binary "int64-checked-multiply recipe" ExpressionRecipe.Int64CheckedMultiply
        | "listRange" ->
            ensureFields "list-range recipe" [ "kind"; "start"; "end" ] [ "kind"; "start"; "end" ] element
            ExpressionRecipe.ListRange(recipe (property "start" element), recipe (property "end" element))
        | "stringRepeat" ->
            ensureFields "string-repeat recipe" [ "kind"; "text"; "count" ] [ "kind"; "text"; "count" ] element
            ExpressionRecipe.StringRepeat(recipe (property "text" element), recipe (property "count" element))
        | "float64Round" ->
            ensureFields "float64-round recipe" [ "kind"; "value"; "digits" ] [ "kind"; "value"; "digits" ] element
            ExpressionRecipe.Float64Round(recipe (property "value" element), recipe (property "digits" element))
        | "int64Equal" -> binary "int64-equal recipe" ExpressionRecipe.Int64Equal
        | "int64LessThan" -> binary "int64-less-than recipe" ExpressionRecipe.Int64LessThan
        | "int64LessThanOrEqual" -> binary "int64-less-than-or-equal recipe" ExpressionRecipe.Int64LessThanOrEqual
        | "int64GreaterThan" -> binary "int64-greater-than recipe" ExpressionRecipe.Int64GreaterThan
        | "int64GreaterThanOrEqual" -> binary "int64-greater-than-or-equal recipe" ExpressionRecipe.Int64GreaterThanOrEqual
        | "float64Add" -> binary "float64-add recipe" ExpressionRecipe.Float64Add
        | "float64Subtract" -> binary "float64-subtract recipe" ExpressionRecipe.Float64Subtract
        | "float64Multiply" -> binary "float64-multiply recipe" ExpressionRecipe.Float64Multiply
        | "float64Divide" -> binary "float64-divide recipe" ExpressionRecipe.Float64Divide
        | "float64Equal" -> binary "float64-equal recipe" ExpressionRecipe.Float64Equal
        | "float64LessThan" -> binary "float64-less-than recipe" ExpressionRecipe.Float64LessThan
        | "float64IsNaN" -> unary "float64-is-nan recipe" ExpressionRecipe.Float64IsNaN
        | "float64IsFinite" -> unary "float64-is-finite recipe" ExpressionRecipe.Float64IsFinite
        | "float64IsInfinite" -> unary "float64-is-infinite recipe" ExpressionRecipe.Float64IsInfinite
        | "int64ToFloat64" -> unary "int64-to-float64 recipe" ExpressionRecipe.Int64ToFloat64
        | "int64WrappingAbs" -> unary "int64-wrapping-abs recipe" ExpressionRecipe.Int64WrappingAbs
        | "bits32FromInt64" -> unary "bits32-from-int64 recipe" ExpressionRecipe.Bits32FromInt64
        | "bits32ToInt64" -> unary "bits32-to-int64 recipe" ExpressionRecipe.Bits32ToInt64
        | "bits32Not" -> unary "bits32-not recipe" ExpressionRecipe.Bits32Not
        | "bits32Xor" -> binary "bits32-xor recipe" ExpressionRecipe.Bits32Xor
        | "bits32And" -> binary "bits32-and recipe" ExpressionRecipe.Bits32And
        | "bits32Or" -> binary "bits32-or recipe" ExpressionRecipe.Bits32Or
        | "bits32Add" -> binary "bits32-add recipe" ExpressionRecipe.Bits32Add
        | "bits32RotateRight" ->
            ensureFields "bits32-rotate-right recipe" [ "kind"; "value"; "amount" ] [ "kind"; "value"; "amount" ] element
            ExpressionRecipe.Bits32RotateRight(recipe (property "value" element), recipe (property "amount" element))
        | "bits32ShiftRight" ->
            ensureFields "bits32-shift-right recipe" [ "kind"; "value"; "amount" ] [ "kind"; "value"; "amount" ] element
            ExpressionRecipe.Bits32ShiftRight(recipe (property "value" element), recipe (property "amount" element))
        | "bits32ShiftLeft" ->
            ensureFields "bits32-shift-left recipe" [ "kind"; "value"; "amount" ] [ "kind"; "value"; "amount" ] element
            ExpressionRecipe.Bits32ShiftLeft(recipe (property "value" element), recipe (property "amount" element))
        | "listEmpty" ->
            ensureFields "list-empty recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.ListEmpty(typeRef (property "itemType" element))
        | "setEmpty" ->
            ensureFields "set-empty recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.SetEmpty(typeRef (property "itemType" element))
        | "mapEmpty" ->
            ensureFields "map-empty recipe" [ "kind"; "keyType"; "valueType" ] [ "kind"; "keyType"; "valueType" ] element
            ExpressionRecipe.MapEmpty(typeRef (property "keyType" element), typeRef (property "valueType" element))
        | "listConstruct" ->
            ensureFields "list-construct recipe" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] element
            ExpressionRecipe.ListConstruct(typeRef (property "itemType" element), array "items" element |> List.map recipe)
        | "listLength" -> unary "list-length recipe" ExpressionRecipe.ListLength
        | "mapValues" -> unary "map-values recipe" ExpressionRecipe.MapValues
        | "mapKeys" -> unary "map-keys recipe" ExpressionRecipe.MapKeys
        | "setToList" -> unary "set-to-list recipe" ExpressionRecipe.SetToList
        | "mapSize" -> unary "map-size recipe" ExpressionRecipe.MapSize
        | "setFromList" -> unary "set-from-list recipe" ExpressionRecipe.SetFromList
        | "setSize" -> unary "set-size recipe" ExpressionRecipe.SetSize
        | "listReverse" -> unary "list-reverse recipe" ExpressionRecipe.ListReverse
        | "listLast" -> unary "list-last recipe" ExpressionRecipe.ListLast
        | "listDropLast" -> unary "list-drop-last recipe" ExpressionRecipe.ListDropLast
        | "listAppendItem" ->
            ensureFields "list-append-item recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListAppendItem(recipe (property "list" element), recipe (property "item" element))
        | "listConcat" -> binary "list-concat recipe" ExpressionRecipe.ListConcat
        | "setUnion" -> binary "set-union recipe" ExpressionRecipe.SetUnion
        | "setIntersection" -> binary "set-intersection recipe" ExpressionRecipe.SetIntersection
        | "setDifference" -> binary "set-difference recipe" ExpressionRecipe.SetDifference
        | "listItemAt" ->
            ensureFields "list-item-at recipe" [ "kind"; "list"; "index" ] [ "kind"; "list"; "index" ] element
            ExpressionRecipe.ListItemAt(recipe (property "list" element), recipe (property "index" element))
        | "mapRemove" ->
            ensureFields "map-remove recipe" [ "kind"; "map"; "key" ] [ "kind"; "map"; "key" ] element
            ExpressionRecipe.MapRemove(recipe (property "map" element), recipe (property "key" element))
        | "setInsert" ->
            ensureFields "set-insert recipe" [ "kind"; "set"; "item" ] [ "kind"; "set"; "item" ] element
            ExpressionRecipe.SetInsert(recipe (property "set" element), recipe (property "item" element))
        | "setRemove" ->
            ensureFields "set-remove recipe" [ "kind"; "set"; "item" ] [ "kind"; "set"; "item" ] element
            ExpressionRecipe.SetRemove(recipe (property "set" element), recipe (property "item" element))
        | "mapContainsKey" ->
            ensureFields "map-contains-key recipe" [ "kind"; "map"; "key" ] [ "kind"; "map"; "key" ] element
            ExpressionRecipe.MapContainsKey(recipe (property "map" element), recipe (property "key" element))
        | "setContains" ->
            ensureFields "set-contains recipe" [ "kind"; "set"; "item" ] [ "kind"; "set"; "item" ] element
            ExpressionRecipe.SetContains(recipe (property "set" element), recipe (property "item" element))
        | "listIndexOf" ->
            ensureFields "set-contains recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListIndexOf(recipe (property "list" element), recipe (property "item" element))
        | "listLastIndexOf" ->
            ensureFields "set-contains recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListLastIndexOf(recipe (property "list" element), recipe (property "item" element))
        | "mapGet" ->
            ensureFields "map-get recipe" [ "kind"; "map"; "key" ] [ "kind"; "map"; "key" ] element
            ExpressionRecipe.MapGet(recipe (property "map" element), recipe (property "key" element))
        | "mapGetOrDefault" ->
            ensureFields "map-get-or-default recipe" [ "kind"; "map"; "key"; "default" ] [ "kind"; "map"; "key"; "default" ] element
            ExpressionRecipe.MapGetOrDefault(recipe (property "map" element), recipe (property "key" element), recipe (property "default" element))
        | "listSlice" ->
            ensureFields "list-slice recipe" [ "kind"; "list"; "start"; "end" ] [ "kind"; "list"; "start"; "end" ] element
            ExpressionRecipe.ListSlice(recipe (property "list" element), recipe (property "start" element), recipe (property "end" element))
        | "mapInsert" ->
            ensureFields "map-insert recipe" [ "kind"; "map"; "key"; "value" ] [ "kind"; "map"; "key"; "value" ] element
            ExpressionRecipe.MapInsert(recipe (property "map" element), recipe (property "key" element), recipe (property "value" element))
        | "listFold" ->
            ensureFields "list-fold recipe" [ "kind"; "source"; "seed"; "functionId"; "context" ] [ "kind"; "source"; "seed"; "functionId" ] element
            ExpressionRecipe.ListFold(recipe (property "source" element), recipe (property "seed" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "mapFold" ->
            ensureFields "map-fold recipe" [ "kind"; "map"; "seed"; "functionId"; "context" ] [ "kind"; "map"; "seed"; "functionId" ] element
            ExpressionRecipe.MapFold(recipe (property "map" element), recipe (property "seed" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "listEffectFold" ->
            ensureFields "list-effect-fold recipe" [ "kind"; "source"; "seed"; "functionId"; "context" ] [ "kind"; "source"; "seed"; "functionId" ] element
            ExpressionRecipe.ListEffectFold(recipe (property "source" element), recipe (property "seed" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "useResource" ->
            ensureFields "use-resource recipe" [ "kind"; "acquire"; "releaseCapability"; "releaseOperationId"; "bodyFunctionId"; "context" ] [ "kind"; "acquire"; "releaseCapability"; "releaseOperationId"; "bodyFunctionId" ] element
            ExpressionRecipe.UseResource(
                recipe (property "acquire" element),
                recipe (property "releaseCapability" element),
                id<CapabilityOperationTag> "releaseOperationId" element,
                id<FunctionTag> "bodyFunctionId" element,
                callbackContext ())
        | "parallelAll" ->
            ensureFields "parallel-all recipe" [ "kind"; "source"; "workerFunctionId"; "context" ] [ "kind"; "source"; "workerFunctionId" ] element
            ExpressionRecipe.ParallelAll(recipe (property "source" element), id<FunctionTag> "workerFunctionId" element, callbackContext ())
        | "cancellationRequested" ->
            ensureFields "cancellation-requested recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.CancellationRequested
        | "exceptionCatch" ->
            ensureFields "exception-catch recipe" [ "kind"; "bodyFunctionId"; "handlerFunctionId"; "bodyContext"; "handlerContext" ] [ "kind"; "bodyFunctionId"; "handlerFunctionId" ] element
            ExpressionRecipe.ExceptionCatch(id<FunctionTag> "bodyFunctionId" element, namedCallbackContext "bodyContext", id<FunctionTag> "handlerFunctionId" element, namedCallbackContext "handlerContext")
        | "exceptionIsCancellation" -> unary "exception-is-cancellation recipe" ExpressionRecipe.ExceptionIsCancellation
        | "exceptionMessage" -> unary "exception-message recipe" ExpressionRecipe.ExceptionMessage
        | "exceptionRethrow" ->
            ensureFields "exception-rethrow recipe" [ "kind"; "value"; "resultType" ] [ "kind"; "value"; "resultType" ] element
            ExpressionRecipe.ExceptionRethrow(recipe (property "value" element), typeRef (property "resultType" element))
        | "cancellationShield" ->
            ensureFields "cancellation-shield recipe" [ "kind"; "bodyFunctionId"; "context" ] [ "kind"; "bodyFunctionId" ] element
            ExpressionRecipe.CancellationShield(id<FunctionTag> "bodyFunctionId" element, callbackContext ())
        | "taskScope" ->
            ensureFields "task-scope recipe" [ "kind"; "bodyFunctionId"; "context" ] [ "kind"; "bodyFunctionId" ] element
            ExpressionRecipe.TaskScope(id<FunctionTag> "bodyFunctionId" element, callbackContext ())
        | "taskStart" ->
            ensureFields "task-start recipe" [ "kind"; "scope"; "workerFunctionId"; "item"; "context" ] [ "kind"; "scope"; "workerFunctionId"; "item" ] element
            ExpressionRecipe.TaskStart(recipe (property "scope" element), id<FunctionTag> "workerFunctionId" element, callbackContext (), recipe (property "item" element))
        | "taskWait" ->
            ensureFields "task-wait recipe" [ "kind"; "tasks"; "mode"; "limit" ] [ "kind"; "tasks"; "mode" ] element
            ExpressionRecipe.TaskWait(recipe (property "tasks" element), WireInput.taskWaitMode element, tryProperty "limit" element |> Option.map recipe)
        | "oneShotCreate" ->
            ensureFields "one-shot-create recipe" [ "kind"; "scope"; "valueType" ] [ "kind"; "scope"; "valueType" ] element
            ExpressionRecipe.OneShotCreate(recipe (property "scope" element), typeRef (property "valueType" element))
        | "oneShotComplete" ->
            ensureFields "one-shot-complete recipe" [ "kind"; "cell"; "value" ] [ "kind"; "cell"; "value" ] element
            ExpressionRecipe.OneShotComplete(recipe (property "cell" element), recipe (property "value" element))
        | "oneShotWait" ->
            ensureFields "one-shot-wait recipe" [ "kind"; "cell"; "limit" ] [ "kind"; "cell" ] element
            ExpressionRecipe.OneShotWait(recipe (property "cell" element), tryProperty "limit" element |> Option.map (recipe))
        | "taskCancel" ->
            ensureFields "task-cancel recipe" [ "kind"; "task" ] [ "kind"; "task" ] element
            ExpressionRecipe.TaskCancel(recipe (property "task" element))
        | "mapFilter" ->
            ensureFields "map-filter recipe" [ "kind"; "map"; "functionId"; "context" ] [ "kind"; "map"; "functionId" ] element
            ExpressionRecipe.MapFilter(recipe (property "map" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "mapMapValues" ->
            ensureFields "map-map-values recipe" [ "kind"; "map"; "functionId"; "context" ] [ "kind"; "map"; "functionId" ] element
            ExpressionRecipe.MapMapValues(recipe (property "map" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "listMap"
        | "mapFromList"
        | "listSortBy"
        | "listMinBy"
        | "listFilter"
        | "listAny"
        | "listFindIndex"
        | "listAll" as kind ->
            ensureFields $"{kind} recipe" [ "kind"; "source"; "functionId"; "context" ] [ "kind"; "source"; "functionId" ] element
            let source = recipe (property "source" element)
            let functionId = id<FunctionTag> "functionId" element
            let context = callbackContext ()
            match kind with
            | "listMap" -> ExpressionRecipe.ListMap(source, functionId, context)
            | "mapFromList" -> ExpressionRecipe.MapFromList(source, functionId, context)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, functionId, context)
            | "listSortBy" -> ExpressionRecipe.ListSortBy(source, functionId, context)
            | "listMinBy" -> ExpressionRecipe.ListMinBy(source, functionId, context)
            | "listAny" -> ExpressionRecipe.ListAny(source, functionId, context)
            | "listFindIndex" -> ExpressionRecipe.ListFindIndex(source, functionId, context)
            | _ -> ExpressionRecipe.ListAll(source, functionId, context)
        | "productConstruct" ->
            ensureFields "product-construct recipe" [ "kind"; "typeId"; "fields" ] [ "kind"; "typeId"; "fields" ] element
            ExpressionRecipe.ProductConstruct(id<TypeTag> "typeId" element, fieldValues "fields")
        | "fieldAccess" ->
            ensureFields "field-access recipe" [ "kind"; "source"; "fieldId" ] [ "kind"; "source"; "fieldId" ] element
            ExpressionRecipe.FieldAccess(recipe (property "source" element), id<FieldTag> "fieldId" element)
        | "productUpdate" ->
            ensureFields "product-update recipe" [ "kind"; "typeId"; "source"; "fields" ] [ "kind"; "typeId"; "source"; "fields" ] element
            ExpressionRecipe.ProductUpdate(id<TypeTag> "typeId" element, recipe (property "source" element), fieldValues "fields")
        | "unionConstruct" ->
            ensureFields "union-construct recipe" [ "kind"; "variantId"; "fields" ] [ "kind"; "variantId"; "fields" ] element
            ExpressionRecipe.UnionConstruct(id<VariantTag> "variantId" element, fieldValues "fields")
        | "optionNone" ->
            ensureFields "option-none recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.OptionNone(typeRef (property "itemType" element))
        | "optionSome" -> unary "option-some recipe" ExpressionRecipe.OptionSome
        | "resultOk" ->
            ensureFields "result-ok recipe" [ "kind"; "errorType"; "value" ] [ "kind"; "errorType"; "value" ] element
            ExpressionRecipe.ResultOk(typeRef (property "errorType" element), recipe (property "value" element))
        | "abort" ->
            ensureFields "abort recipe" [ "kind"; "error" ] [ "kind"; "error" ] element
            ExpressionRecipe.Abort(recipe (property "error" element))
        | "resultErr" ->
            ensureFields "result-err recipe" [ "kind"; "okType"; "error" ] [ "kind"; "okType"; "error" ] element
            ExpressionRecipe.ResultErr(typeRef (property "okType" element), recipe (property "error" element))
        | "capabilityConstruct" ->
            ensureFields "Capability construction recipe" [ "kind"; "capabilityId"; "implementations" ] [ "kind"; "capabilityId"; "implementations" ] element
            let implementations = array "implementations" element |> List.map (fun item ->
                ensureFields "Capability implementation" [ "operationId"; "functionId" ] [ "operationId"; "functionId" ] item
                id<CapabilityOperationTag> "operationId" item, id<FunctionTag> "functionId" item)
            ExpressionRecipe.CapabilityConstruct(id<CapabilityTag> "capabilityId" element, implementations)
        | "functionReference" ->
            ensureFields "Function reference recipe" [ "kind"; "functionId" ] [ "kind"; "functionId" ] element
            ExpressionRecipe.FunctionReference(id<FunctionTag> "functionId" element)
        | "functionInvoke" ->
            ensureFields "Function invocation recipe" [ "kind"; "functionValue"; "arguments" ] [ "kind"; "functionValue"; "arguments" ] element
            ExpressionRecipe.FunctionInvoke(recipe (property "functionValue" element), array "arguments" element |> List.map (recipe))
        | "directCall" ->
            ensureFields "direct-call recipe" [ "kind"; "functionId"; "arguments" ] [ "kind"; "functionId"; "arguments" ] element
            ExpressionRecipe.DirectCall(id<FunctionTag> "functionId" element, callArguments "arguments")
        | "selfCall" ->
            ensureFields "self-call recipe" [ "kind"; "arguments" ] [ "kind"; "arguments" ] element
            let arguments =
                array "arguments" element
                |> List.map (fun item ->
                    ensureFields "self-call argument" [ "slot"; "value" ] [ "slot"; "value" ] item
                    requiredInt "slot" item, recipe (property "value" item))
            ExpressionRecipe.SelfCall arguments
        | "capabilityInvoke" ->
            ensureFields "capability-invoke recipe" [ "kind"; "capabilityValue"; "operationId"; "arguments" ] [ "kind"; "capabilityValue"; "operationId"; "arguments" ] element
            ExpressionRecipe.CapabilityInvoke(recipe (property "capabilityValue" element), id<CapabilityOperationTag> "operationId" element, capabilityArguments "arguments")
        | "boolNot" -> unary "bool-not recipe" ExpressionRecipe.BoolNot
        | "boolAnd" -> binary "bool-and recipe" ExpressionRecipe.BoolAnd
        | "boolOr" -> binary "bool-or recipe" ExpressionRecipe.BoolOr
        | "conditional" ->
            ensureFields "conditional recipe" [ "kind"; "condition"; "whenTrue"; "whenFalse" ] [ "kind"; "condition"; "whenTrue"; "whenFalse" ] element
            ExpressionRecipe.Conditional(recipe (property "condition" element), recipe (property "whenTrue" element), recipe (property "whenFalse" element))
        | "matchUnion" ->
            ensureFields "match-union recipe" [ "kind"; "input"; "arms"; "otherwise" ] [ "kind"; "input"; "arms" ] element
            let arms = unionArms "variantId" "variantIds" (stableId<VariantTag> "variantId") recipe element
            ExpressionRecipe.MatchUnion(recipe (property "input" element), arms)
        | "bind" ->
            ensureFields "bind recipe" [ "kind"; "value"; "body" ] [ "kind"; "value"; "body" ] element
            ExpressionRecipe.Bind(recipe (property "value" element), recipe (property "body" element))
        | "matchOption" ->
            ensureFields "match-option recipe" [ "kind"; "input"; "someBody"; "noneBody" ] [ "kind"; "input"; "someBody"; "noneBody" ] element
            ExpressionRecipe.MatchOption(recipe (property "input" element), recipe (property "someBody" element), recipe (property "noneBody" element))
        | "matchResult" ->
            ensureFields "match-result recipe" [ "kind"; "input"; "okBody"; "errBody" ] [ "kind"; "input"; "okBody"; "errBody" ] element
            ExpressionRecipe.MatchResult(recipe (property "input" element), recipe (property "okBody" element), recipe (property "errBody" element))
        | value -> fail $"Unsupported expression recipe kind '{value}'."
