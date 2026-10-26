namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open Nyrwvy.Core

/// Decoding shared by the stable-ID (WireRecipe) and display-name (WireSymbolic) recipe protocols:
/// the kinds that name no declaration, parameter or pattern binding, so both protocols accept the same
/// JSON shape for them. `recipe` decodes a nested recipe in the caller's protocol.
module internal WireRecipeCommon =
    open WireInput

    let tryReferenceFree (recipe: JsonElement -> ExpressionRecipe) (element: JsonElement) (kind: string) : ExpressionRecipe option =
        let unary context constructor =
            ensureFields context [ "kind"; "value" ] [ "kind"; "value" ] element
            constructor(recipe (property "value" element))

        let binary context constructor =
            ensureFields context [ "kind"; "left"; "right" ] [ "kind"; "left"; "right" ] element
            constructor(recipe (property "left" element), recipe (property "right" element))

        match kind with
        | "stringLiteral" ->
            ensureFields "string literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.StringLiteral(requiredRunText "value" element) |> Some
        | "boolLiteral" ->
            ensureFields "bool literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.BoolLiteral(requiredBool "value" element) |> Some
        | "originalTarget" ->
            ensureFields "original-target recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.OriginalTarget |> Some
        | "unitLiteral" ->
            ensureFields "unit literal recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.UnitLiteral |> Some
        | "int64Literal" ->
            ensureFields "int64 literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.Int64Literal(requiredInt64 "value" element) |> Some
        | "float64Literal" ->
            ensureFields "float64 literal recipe" [ "kind"; "bits" ] [ "kind"; "bits" ] element
            match Float64Bits.tryParseHex (requiredString "bits" element) with
            | Ok bits -> Some(ExpressionRecipe.Float64Literal bits)
            | Error error -> fail $"Invalid Float64 bits: {error}."
        | "stringIsEmpty" -> unary "string-is-empty recipe" ExpressionRecipe.StringIsEmpty |> Some
        | "int64ToString" -> unary "int64-to-string recipe" ExpressionRecipe.Int64ToString |> Some
        | "int64ToHexString" -> unary "int64-to-hex-string recipe" ExpressionRecipe.Int64ToHexString |> Some
        | "stringParseInt64" -> unary "string-parse-int64 recipe" ExpressionRecipe.StringParseInt64 |> Some
        | "stringParseInt64Hex" -> unary "string-parse-int64-hex recipe" ExpressionRecipe.StringParseInt64Hex |> Some
        | "float64ToString" -> unary "float64-to-string recipe" ExpressionRecipe.Float64ToString |> Some
        | "stringParseFloat64" -> unary "string-parse-float64 recipe" ExpressionRecipe.StringParseFloat64 |> Some
        | "stringCodePoints" -> unary "string-code-points recipe" ExpressionRecipe.StringCodePoints |> Some
        | "stringToLower" -> unary "string-to-lower recipe" ExpressionRecipe.StringToLower |> Some
        | "stringToUpper" -> unary "string-to-upper recipe" ExpressionRecipe.StringToUpper |> Some
        | "stringDecodeUtf8Lossy" -> unary "string-decode-utf8-lossy recipe" ExpressionRecipe.StringDecodeUtf8Lossy |> Some
        | "stringEncodeUtf8" -> unary "string-encode-utf8 recipe" ExpressionRecipe.StringEncodeUtf8 |> Some
        | "float64RoundToInt64" -> unary "float64-round-to-int64 recipe" ExpressionRecipe.Float64RoundToInt64 |> Some
        | "float64FloorToInt64" -> unary "float64-floor-to-int64 recipe" ExpressionRecipe.Float64FloorToInt64 |> Some
        | "float64Sqrt" -> unary "float64-sqrt recipe" ExpressionRecipe.Float64Sqrt |> Some
        | "float64Abs" -> unary "float64-abs recipe" ExpressionRecipe.Float64Abs |> Some
        | "float64Negate" -> unary "float64-negate recipe" ExpressionRecipe.Float64Negate |> Some
        | "float64Floor" -> unary "float64-floor recipe" ExpressionRecipe.Float64Floor |> Some
        | "float64Ceiling" -> unary "float64-ceiling recipe" ExpressionRecipe.Float64Ceiling |> Some
        | "float64Truncate" -> unary "float64-truncate recipe" ExpressionRecipe.Float64Truncate |> Some
        | "stringUtf8ByteCount" -> unary "string-utf8-byte-count recipe" ExpressionRecipe.StringUtf8ByteCount |> Some
        | "stringFromCodePoint" -> unary "string-from-code-point recipe" ExpressionRecipe.StringFromCodePoint |> Some
        | "stringToCodePoint" -> unary "string-to-code-point recipe" ExpressionRecipe.StringToCodePoint |> Some
        | "stringLength" -> unary "string-length recipe" ExpressionRecipe.StringLength |> Some
        | "stringSlice" ->
            ensureFields "string-slice recipe" [ "kind"; "value"; "start"; "end" ] [ "kind"; "value"; "start"; "end" ] element
            ExpressionRecipe.StringSlice(recipe (property "value" element), recipe (property "start" element), recipe (property "end" element)) |> Some
        | "stringContains" ->
            ensureFields "string-contains recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringContains(recipe (property "value" element), recipe (property "substring" element)) |> Some
        | "stringIndexOf" ->
            ensureFields "string-index-of recipe" [ "kind"; "value"; "substring"; "from" ] [ "kind"; "value"; "substring"; "from" ] element
            ExpressionRecipe.StringIndexOf(recipe (property "value" element), recipe (property "substring" element), recipe (property "from" element)) |> Some
        | "stringSpan" ->
            ensureFields "string-span recipe" [ "kind"; "value"; "set"; "from" ] [ "kind"; "value"; "set"; "from" ] element
            ExpressionRecipe.StringSpan(recipe (property "value" element), recipe (property "set" element), recipe (property "from" element)) |> Some
        | "stringSpanBack" ->
            ensureFields "string-span-back recipe" [ "kind"; "value"; "set"; "end" ] [ "kind"; "value"; "set"; "end" ] element
            ExpressionRecipe.StringSpanBack(recipe (property "value" element), recipe (property "set" element), recipe (property "end" element)) |> Some
        | "stringLastIndexOfBefore" ->
            ensureFields "string-last-index-of-before recipe" [ "kind"; "value"; "substring"; "end" ] [ "kind"; "value"; "substring"; "end" ] element
            ExpressionRecipe.StringLastIndexOfBefore(recipe (property "value" element), recipe (property "substring" element), recipe (property "end" element)) |> Some
        | "stringLastIndexOf" ->
            ensureFields "string-last-index-of recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringLastIndexOf(recipe (property "value" element), recipe (property "substring" element)) |> Some
        | "stringStartsWith" ->
            ensureFields "string-starts-with recipe" [ "kind"; "value"; "prefix" ] [ "kind"; "value"; "prefix" ] element
            ExpressionRecipe.StringStartsWith(recipe (property "value" element), recipe (property "prefix" element)) |> Some
        | "stringEndsWith" ->
            ensureFields "string-ends-with recipe" [ "kind"; "value"; "suffix" ] [ "kind"; "value"; "suffix" ] element
            ExpressionRecipe.StringEndsWith(recipe (property "value" element), recipe (property "suffix" element)) |> Some
        | "stringSplit" ->
            ensureFields "string-split recipe" [ "kind"; "value"; "separator" ] [ "kind"; "value"; "separator" ] element
            ExpressionRecipe.StringSplit(recipe (property "value" element), recipe (property "separator" element)) |> Some
        | "stringSplitLines" ->
            ensureFields "string-split-lines recipe" [ "kind"; "value"; "breaks" ] [ "kind"; "value"; "breaks" ] element
            ExpressionRecipe.StringSplitLines(recipe (property "value" element), recipe (property "breaks" element)) |> Some
        | "stringReplace" ->
            ensureFields "string-replace recipe" [ "kind"; "value"; "pattern"; "replacement" ] [ "kind"; "value"; "pattern"; "replacement" ] element
            ExpressionRecipe.StringReplace(recipe (property "value" element), recipe (property "pattern" element), recipe (property "replacement" element)) |> Some
        | "stringTrim" -> unary "string-trim recipe" ExpressionRecipe.StringTrim |> Some
        | "stringTrimStart" -> unary "string-trim-start recipe" ExpressionRecipe.StringTrimStart |> Some
        | "stringTrimEnd" -> unary "string-trim-end recipe" ExpressionRecipe.StringTrimEnd |> Some
        | "stringJoin" ->
            ensureFields "string-join recipe" [ "kind"; "list"; "separator" ] [ "kind"; "list"; "separator" ] element
            ExpressionRecipe.StringJoin(recipe (property "list" element), recipe (property "separator" element)) |> Some
        | "stringEqual" -> binary "string-equal recipe" ExpressionRecipe.StringEqual |> Some
        | "stringLessThan" -> binary "string-less-than recipe" ExpressionRecipe.StringLessThan |> Some
        | "valueEqual" -> binary "value-equal recipe" ExpressionRecipe.ValueEqual |> Some
        | "stringConcat" -> binary "string-concat recipe" ExpressionRecipe.StringConcat |> Some
        | "int64WrappingAdd" -> binary "int64-wrapping-add recipe" ExpressionRecipe.Int64WrappingAdd |> Some
        | "int64WrappingSubtract" -> binary "int64-wrapping-subtract recipe" ExpressionRecipe.Int64WrappingSubtract |> Some
        | "int64WrappingMultiply" -> binary "int64-wrapping-multiply recipe" ExpressionRecipe.Int64WrappingMultiply |> Some
        | "int64FloorDivide" -> binary "int64-floor-divide recipe" ExpressionRecipe.Int64FloorDivide |> Some
        | "int64FloorModulo" -> binary "int64-floor-modulo recipe" ExpressionRecipe.Int64FloorModulo |> Some
        | "int64Min" -> binary "int64-min recipe" ExpressionRecipe.Int64Min |> Some
        | "int64Max" -> binary "int64-max recipe" ExpressionRecipe.Int64Max |> Some
        | "int64BitAnd" -> binary "int64-bit-and recipe" ExpressionRecipe.Int64BitAnd |> Some
        | "int64BitOr" -> binary "int64-bit-or recipe" ExpressionRecipe.Int64BitOr |> Some
        | "int64BitXor" -> binary "int64-bit-xor recipe" ExpressionRecipe.Int64BitXor |> Some
        | "int64ShiftLeft" ->
            ensureFields "int64-shift-left recipe" [ "kind"; "value"; "count" ] [ "kind"; "value"; "count" ] element
            ExpressionRecipe.Int64ShiftLeft(recipe (property "value" element), recipe (property "count" element)) |> Some
        | "int64ShiftRight" ->
            ensureFields "int64-shift-right recipe" [ "kind"; "value"; "count" ] [ "kind"; "value"; "count" ] element
            ExpressionRecipe.Int64ShiftRight(recipe (property "value" element), recipe (property "count" element)) |> Some
        | "int64CheckedAdd" -> binary "int64-checked-add recipe" ExpressionRecipe.Int64CheckedAdd |> Some
        | "int64CheckedSubtract" -> binary "int64-checked-subtract recipe" ExpressionRecipe.Int64CheckedSubtract |> Some
        | "int64CheckedMultiply" -> binary "int64-checked-multiply recipe" ExpressionRecipe.Int64CheckedMultiply |> Some
        | "listRange" ->
            ensureFields "list-range recipe" [ "kind"; "start"; "end" ] [ "kind"; "start"; "end" ] element
            ExpressionRecipe.ListRange(recipe (property "start" element), recipe (property "end" element)) |> Some
        | "stringRepeat" ->
            ensureFields "string-repeat recipe" [ "kind"; "text"; "count" ] [ "kind"; "text"; "count" ] element
            ExpressionRecipe.StringRepeat(recipe (property "text" element), recipe (property "count" element)) |> Some
        | "float64Round" ->
            ensureFields "float64-round recipe" [ "kind"; "value"; "digits" ] [ "kind"; "value"; "digits" ] element
            ExpressionRecipe.Float64Round(recipe (property "value" element), recipe (property "digits" element)) |> Some
        | "int64Equal" -> binary "int64-equal recipe" ExpressionRecipe.Int64Equal |> Some
        | "int64LessThan" -> binary "int64-less-than recipe" ExpressionRecipe.Int64LessThan |> Some
        | "int64LessThanOrEqual" -> binary "int64-less-than-or-equal recipe" ExpressionRecipe.Int64LessThanOrEqual |> Some
        | "int64GreaterThan" -> binary "int64-greater-than recipe" ExpressionRecipe.Int64GreaterThan |> Some
        | "int64GreaterThanOrEqual" -> binary "int64-greater-than-or-equal recipe" ExpressionRecipe.Int64GreaterThanOrEqual |> Some
        | "float64Add" -> binary "float64-add recipe" ExpressionRecipe.Float64Add |> Some
        | "float64Min" -> binary "float64-min recipe" ExpressionRecipe.Float64Min |> Some
        | "float64Max" -> binary "float64-max recipe" ExpressionRecipe.Float64Max |> Some
        | "float64Subtract" -> binary "float64-subtract recipe" ExpressionRecipe.Float64Subtract |> Some
        | "float64Multiply" -> binary "float64-multiply recipe" ExpressionRecipe.Float64Multiply |> Some
        | "float64Divide" -> binary "float64-divide recipe" ExpressionRecipe.Float64Divide |> Some
        | "float64FloorModulo" -> binary "float64-floor-modulo recipe" ExpressionRecipe.Float64FloorModulo |> Some
        | "float64Equal" -> binary "float64-equal recipe" ExpressionRecipe.Float64Equal |> Some
        | "float64LessThan" -> binary "float64-less-than recipe" ExpressionRecipe.Float64LessThan |> Some
        | "float64IsNaN" -> unary "float64-is-nan recipe" ExpressionRecipe.Float64IsNaN |> Some
        | "float64IsFinite" -> unary "float64-is-finite recipe" ExpressionRecipe.Float64IsFinite |> Some
        | "float64IsInfinite" -> unary "float64-is-infinite recipe" ExpressionRecipe.Float64IsInfinite |> Some
        | "int64ToFloat64" -> unary "int64-to-float64 recipe" ExpressionRecipe.Int64ToFloat64 |> Some
        | "int64WrappingAbs" -> unary "int64-wrapping-abs recipe" ExpressionRecipe.Int64WrappingAbs |> Some
        | "int64BitNot" -> unary "int64-bit-not recipe" ExpressionRecipe.Int64BitNot |> Some
        | "bits32FromInt64" -> unary "bits32-from-int64 recipe" ExpressionRecipe.Bits32FromInt64 |> Some
        | "bits32ToInt64" -> unary "bits32-to-int64 recipe" ExpressionRecipe.Bits32ToInt64 |> Some
        | "bits32Not" -> unary "bits32-not recipe" ExpressionRecipe.Bits32Not |> Some
        | "bits32Xor" -> binary "bits32-xor recipe" ExpressionRecipe.Bits32Xor |> Some
        | "bits32And" -> binary "bits32-and recipe" ExpressionRecipe.Bits32And |> Some
        | "bits32Or" -> binary "bits32-or recipe" ExpressionRecipe.Bits32Or |> Some
        | "bits32Add" -> binary "bits32-add recipe" ExpressionRecipe.Bits32Add |> Some
        | "bits32RotateRight" ->
            ensureFields "bits32-rotate-right recipe" [ "kind"; "value"; "amount" ] [ "kind"; "value"; "amount" ] element
            ExpressionRecipe.Bits32RotateRight(recipe (property "value" element), recipe (property "amount" element)) |> Some
        | "bits32ShiftRight" ->
            ensureFields "bits32-shift-right recipe" [ "kind"; "value"; "amount" ] [ "kind"; "value"; "amount" ] element
            ExpressionRecipe.Bits32ShiftRight(recipe (property "value" element), recipe (property "amount" element)) |> Some
        | "bits32ShiftLeft" ->
            ensureFields "bits32-shift-left recipe" [ "kind"; "value"; "amount" ] [ "kind"; "value"; "amount" ] element
            ExpressionRecipe.Bits32ShiftLeft(recipe (property "value" element), recipe (property "amount" element)) |> Some
        | "bytesEmpty" ->
            ensureFields "bytes-empty recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.BytesEmpty |> Some
        | "bytesFill" ->
            ensureFields "bytes-fill recipe" [ "kind"; "length"; "value" ] [ "kind"; "length"; "value" ] element
            ExpressionRecipe.BytesFill(recipe (property "length" element), recipe (property "value" element)) |> Some
        | "bytesLength" -> unary "bytes-length recipe" ExpressionRecipe.BytesLength |> Some
        | "bytesGet" ->
            ensureFields "bytes-get recipe" [ "kind"; "bytes"; "index" ] [ "kind"; "bytes"; "index" ] element
            ExpressionRecipe.BytesGet(recipe (property "bytes" element), recipe (property "index" element)) |> Some
        | "bytesSet" ->
            ensureFields "bytes-set recipe" [ "kind"; "bytes"; "index"; "value" ] [ "kind"; "bytes"; "index"; "value" ] element
            ExpressionRecipe.BytesSet(recipe (property "bytes" element), recipe (property "index" element), recipe (property "value" element)) |> Some
        | "bytesAppend" ->
            ensureFields "bytes-append recipe" [ "kind"; "bytes"; "value" ] [ "kind"; "bytes"; "value" ] element
            ExpressionRecipe.BytesAppend(recipe (property "bytes" element), recipe (property "value" element)) |> Some
        | "bytesConcat" -> binary "bytes-concat recipe" ExpressionRecipe.BytesConcat |> Some
        | "bytesSlice" ->
            ensureFields "bytes-slice recipe" [ "kind"; "bytes"; "start"; "end" ] [ "kind"; "bytes"; "start"; "end" ] element
            ExpressionRecipe.BytesSlice(recipe (property "bytes" element), recipe (property "start" element), recipe (property "end" element)) |> Some
        | "bytesFromLatin1" -> unary "bytes-from-latin1 recipe" ExpressionRecipe.BytesFromLatin1 |> Some
        | "bytesToLatin1" -> unary "bytes-to-latin1 recipe" ExpressionRecipe.BytesToLatin1 |> Some
        | "listLength" -> unary "list-length recipe" ExpressionRecipe.ListLength |> Some
        | "mapValues" -> unary "map-values recipe" ExpressionRecipe.MapValues |> Some
        | "mapKeys" -> unary "map-keys recipe" ExpressionRecipe.MapKeys |> Some
        | "setToList" -> unary "set-to-list recipe" ExpressionRecipe.SetToList |> Some
        | "mapSize" -> unary "map-size recipe" ExpressionRecipe.MapSize |> Some
        | "setFromList" -> unary "set-from-list recipe" ExpressionRecipe.SetFromList |> Some
        | "setSize" -> unary "set-size recipe" ExpressionRecipe.SetSize |> Some
        | "listReverse" -> unary "list-reverse recipe" ExpressionRecipe.ListReverse |> Some
        | "listLast" -> unary "list-last recipe" ExpressionRecipe.ListLast |> Some
        | "listDropLast" -> unary "list-drop-last recipe" ExpressionRecipe.ListDropLast |> Some
        | "listAppendItem" ->
            ensureFields "list-append-item recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListAppendItem(recipe (property "list" element), recipe (property "item" element)) |> Some
        | "listConcat" -> binary "list-concat recipe" ExpressionRecipe.ListConcat |> Some
        | "setUnion" -> binary "set-union recipe" ExpressionRecipe.SetUnion |> Some
        | "setIntersection" -> binary "set-intersection recipe" ExpressionRecipe.SetIntersection |> Some
        | "setDifference" -> binary "set-difference recipe" ExpressionRecipe.SetDifference |> Some
        | "setIsSubset" -> binary "set-is-subset recipe" ExpressionRecipe.SetIsSubset |> Some
        | "mapUnion" -> binary "map-union recipe" ExpressionRecipe.MapUnion |> Some
        | "listItemAt" ->
            ensureFields "list-item-at recipe" [ "kind"; "list"; "index" ] [ "kind"; "list"; "index" ] element
            ExpressionRecipe.ListItemAt(recipe (property "list" element), recipe (property "index" element)) |> Some
        | "mapRemove" ->
            ensureFields "map-remove recipe" [ "kind"; "map"; "key" ] [ "kind"; "map"; "key" ] element
            ExpressionRecipe.MapRemove(recipe (property "map" element), recipe (property "key" element)) |> Some
        | "setInsert" ->
            ensureFields "set-insert recipe" [ "kind"; "set"; "item" ] [ "kind"; "set"; "item" ] element
            ExpressionRecipe.SetInsert(recipe (property "set" element), recipe (property "item" element)) |> Some
        | "setRemove" ->
            ensureFields "set-remove recipe" [ "kind"; "set"; "item" ] [ "kind"; "set"; "item" ] element
            ExpressionRecipe.SetRemove(recipe (property "set" element), recipe (property "item" element)) |> Some
        | "mapContainsKey" ->
            ensureFields "map-contains-key recipe" [ "kind"; "map"; "key" ] [ "kind"; "map"; "key" ] element
            ExpressionRecipe.MapContainsKey(recipe (property "map" element), recipe (property "key" element)) |> Some
        | "setContains" ->
            ensureFields "set-contains recipe" [ "kind"; "set"; "item" ] [ "kind"; "set"; "item" ] element
            ExpressionRecipe.SetContains(recipe (property "set" element), recipe (property "item" element)) |> Some
        | "listIndexOf" ->
            ensureFields "set-contains recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListIndexOf(recipe (property "list" element), recipe (property "item" element)) |> Some
        | "listLastIndexOf" ->
            ensureFields "set-contains recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListLastIndexOf(recipe (property "list" element), recipe (property "item" element)) |> Some
        | "listContains" ->
            ensureFields "list-contains recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListContains(recipe (property "list" element), recipe (property "item" element)) |> Some
        | "mapGet" ->
            ensureFields "map-get recipe" [ "kind"; "map"; "key" ] [ "kind"; "map"; "key" ] element
            ExpressionRecipe.MapGet(recipe (property "map" element), recipe (property "key" element)) |> Some
        | "optionGetOrDefault" ->
            ensureFields "option-get-or-default recipe" [ "kind"; "option"; "default" ] [ "kind"; "option"; "default" ] element
            ExpressionRecipe.OptionGetOrDefault(recipe (property "option" element), recipe (property "default" element)) |> Some
        | "resultGetOrDefault" ->
            ensureFields "result-get-or-default recipe" [ "kind"; "result"; "default" ] [ "kind"; "result"; "default" ] element
            ExpressionRecipe.ResultGetOrDefault(recipe (property "result" element), recipe (property "default" element)) |> Some
        | "optionOkOr" ->
            ensureFields "option-ok-or recipe" [ "kind"; "option"; "error" ] [ "kind"; "option"; "error" ] element
            ExpressionRecipe.OptionOkOr(recipe (property "option" element), recipe (property "error" element)) |> Some
        | "resultOkValue" -> unary "result-ok-value recipe" ExpressionRecipe.ResultOkValue |> Some
        | "resultErrValue" -> unary "result-err-value recipe" ExpressionRecipe.ResultErrValue |> Some
        | "optionIsSome" -> unary "option-is-some recipe" ExpressionRecipe.OptionIsSome |> Some
        | "resultIsOk" -> unary "result-is-ok recipe" ExpressionRecipe.ResultIsOk |> Some
        | "mapGetOrDefault" ->
            ensureFields "map-get-or-default recipe" [ "kind"; "map"; "key"; "default" ] [ "kind"; "map"; "key"; "default" ] element
            ExpressionRecipe.MapGetOrDefault(recipe (property "map" element), recipe (property "key" element), recipe (property "default" element)) |> Some
        | "listSlice" ->
            ensureFields "list-slice recipe" [ "kind"; "list"; "start"; "end" ] [ "kind"; "list"; "start"; "end" ] element
            ExpressionRecipe.ListSlice(recipe (property "list" element), recipe (property "start" element), recipe (property "end" element)) |> Some
        | "listSetAt" ->
            ensureFields "list-set-at recipe" [ "kind"; "list"; "index"; "item" ] [ "kind"; "list"; "index"; "item" ] element
            ExpressionRecipe.ListSetAt(recipe (property "list" element), recipe (property "index" element), recipe (property "item" element)) |> Some
        | "listInsertAt" ->
            ensureFields "list-insert-at recipe" [ "kind"; "list"; "index"; "item" ] [ "kind"; "list"; "index"; "item" ] element
            ExpressionRecipe.ListInsertAt(recipe (property "list" element), recipe (property "index" element), recipe (property "item" element)) |> Some
        | "listRemoveAt" ->
            ensureFields "list-remove-at recipe" [ "kind"; "list"; "index" ] [ "kind"; "list"; "index" ] element
            ExpressionRecipe.ListRemoveAt(recipe (property "list" element), recipe (property "index" element)) |> Some
        | "mapInsert" ->
            ensureFields "map-insert recipe" [ "kind"; "map"; "key"; "value" ] [ "kind"; "map"; "key"; "value" ] element
            ExpressionRecipe.MapInsert(recipe (property "map" element), recipe (property "key" element), recipe (property "value" element)) |> Some
        | "cancellationRequested" ->
            ensureFields "cancellation-requested recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.CancellationRequested |> Some
        | "exceptionIsCancellation" -> unary "exception-is-cancellation recipe" ExpressionRecipe.ExceptionIsCancellation |> Some
        | "exceptionMessage" -> unary "exception-message recipe" ExpressionRecipe.ExceptionMessage |> Some
        | "oneShotComplete" ->
            ensureFields "one-shot-complete recipe" [ "kind"; "cell"; "value" ] [ "kind"; "cell"; "value" ] element
            ExpressionRecipe.OneShotComplete(recipe (property "cell" element), recipe (property "value" element)) |> Some
        | "oneShotWait" ->
            ensureFields "one-shot-wait recipe" [ "kind"; "cell"; "limit" ] [ "kind"; "cell" ] element
            ExpressionRecipe.OneShotWait(recipe (property "cell" element), tryProperty "limit" element |> Option.map (recipe)) |> Some
        | "taskCancel" ->
            ensureFields "task-cancel recipe" [ "kind"; "task" ] [ "kind"; "task" ] element
            ExpressionRecipe.TaskCancel(recipe (property "task" element)) |> Some
        | "optionSome" -> unary "option-some recipe" ExpressionRecipe.OptionSome |> Some
        | "abort" ->
            ensureFields "abort recipe" [ "kind"; "error" ] [ "kind"; "error" ] element
            ExpressionRecipe.Abort(recipe (property "error" element)) |> Some
        | "functionInvoke" ->
            ensureFields "Function invocation recipe" [ "kind"; "functionValue"; "arguments" ] [ "kind"; "functionValue"; "arguments" ] element
            ExpressionRecipe.FunctionInvoke(recipe (property "functionValue" element), array "arguments" element |> List.map (recipe)) |> Some
        | "boolNot" -> unary "bool-not recipe" ExpressionRecipe.BoolNot |> Some
        | "boolAnd" -> binary "bool-and recipe" ExpressionRecipe.BoolAnd |> Some
        | "boolOr" -> binary "bool-or recipe" ExpressionRecipe.BoolOr |> Some
        | "conditional" ->
            ensureFields "conditional recipe" [ "kind"; "condition"; "whenTrue"; "whenFalse" ] [ "kind"; "condition"; "whenTrue"; "whenFalse" ] element
            ExpressionRecipe.Conditional(recipe (property "condition" element), recipe (property "whenTrue" element), recipe (property "whenFalse" element)) |> Some
        | "bind" ->
            ensureFields "bind recipe" [ "kind"; "value"; "body" ] [ "kind"; "value"; "body" ] element
            ExpressionRecipe.Bind(recipe (property "value" element), recipe (property "body" element)) |> Some
        | "matchOption" ->
            ensureFields "match-option recipe" [ "kind"; "input"; "someBody"; "noneBody" ] [ "kind"; "input"; "someBody"; "noneBody" ] element
            ExpressionRecipe.MatchOption(recipe (property "input" element), recipe (property "someBody" element), recipe (property "noneBody" element)) |> Some
        | "matchResult" ->
            ensureFields "match-result recipe" [ "kind"; "input"; "okBody"; "errBody" ] [ "kind"; "input"; "okBody"; "errBody" ] element
            ExpressionRecipe.MatchResult(recipe (property "input" element), recipe (property "okBody" element), recipe (property "errBody" element)) |> Some
        | _ -> None
