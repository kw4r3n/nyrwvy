namespace Nyrwvy.Tools.AuthoringSession

open System.Globalization
open Nyrwvy.Core

/// Structured read projection. This is not a second snapshot codec or an edit format.
module internal WireGraph =
    open WireJson
    open WireEncodeCommon

    let private identity value = box (StableId.value value)
    let private identities values = values |> List.map identity |> arrayValue
    let private tagged kind fields = objectValue (("kind", box kind) :: fields)

    let private binding (value: PatternBinding) =
        objectValue
            [ "id", identity value.Id
              "displayName", value.DisplayName |> Option.map box |> Option.defaultValue null
              "type", typeRef value.Type ]

    let private fields (values: FieldValueBinding list) =
        values
        |> List.sortBy (fun value -> StableId.value value.TargetFieldId)
        |> List.map (fun value ->
            objectValue [ "fieldId", identity value.TargetFieldId; "valueNodeId", identity value.ValueNodeId ])
        |> arrayValue

    let rec private graph (value: ValueGraph): obj =
        objectValue
            [ "resultNodeId", identity value.ResultNodeId
              "nodes", value.Nodes |> List.sortBy (fun node -> StableId.value node.Id) |> List.map node |> arrayValue ]

    and private node (value: SemanticNode): obj =
        objectValue [ "id", identity value.Id; "kind", kind value.Kind ]

    and private kind (value: SemanticNodeKind): obj =
        let unary name operand = tagged name [ "valueNodeId", identity operand ]
        let binary name left right = tagged name [ "leftNodeId", identity left; "rightNodeId", identity right ]
        let argumentList (bindings: ArgumentBinding list) =
            bindings |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId) |> List.map (fun argument ->
                objectValue [ "parameterId", identity argument.TargetParameterId; "valueNodeId", identity argument.ValueNodeId ]) |> arrayValue
        let context (bindings: ArgumentBinding list) =
            if List.isEmpty bindings then [] else [ "context", argumentList bindings ]
        let traversal name source functionId bindings =
            tagged name ([ "sourceNodeId", identity source; "functionId", identity functionId ] @ context bindings)
        let valueArm (arm: ValueMatchArm) =
            objectValue [ "valueBinding", binding arm.ValueBinding; "body", graph arm.Body ]
        match value with
        | SemanticNodeKind.ParameterReference parameterId -> tagged "parameter" [ "parameterId", identity parameterId ]
        | SemanticNodeKind.PatternReference bindingId -> tagged "patternReference" [ "bindingId", identity bindingId ]
        | SemanticNodeKind.StringLiteral value -> tagged "stringLiteral" [ "value", runString value ]
        | SemanticNodeKind.BoolLiteral value -> tagged "boolLiteral" [ "value", box value ]
        | SemanticNodeKind.UnitLiteral -> tagged "unitLiteral" []
        | SemanticNodeKind.Int64Literal value -> tagged "int64Literal" [ "value", box (value.ToString(CultureInfo.InvariantCulture)) ]
        | SemanticNodeKind.Float64Literal value -> tagged "float64Literal" [ "bits", box (Float64Bits.toHex value) ]
        | SemanticNodeKind.StringIsEmpty value -> unary "stringIsEmpty" value
        | SemanticNodeKind.Int64ToString value -> unary "int64ToString" value
        | SemanticNodeKind.Int64ToHexString value -> unary "int64ToHexString" value
        | SemanticNodeKind.StringParseInt64 value -> unary "stringParseInt64" value
        | SemanticNodeKind.StringParseInt64Hex value -> unary "stringParseInt64Hex" value
        | SemanticNodeKind.Float64ToString value -> unary "float64ToString" value
        | SemanticNodeKind.StringParseFloat64 value -> unary "stringParseFloat64" value
        | SemanticNodeKind.StringCodePoints value -> unary "stringCodePoints" value
        | SemanticNodeKind.StringToLower value -> unary "stringToLower" value
        | SemanticNodeKind.StringToUpper value -> unary "stringToUpper" value
        | SemanticNodeKind.StringDecodeUtf8Lossy value -> unary "stringDecodeUtf8Lossy" value
        | SemanticNodeKind.Float64RoundToInt64 value -> unary "float64RoundToInt64" value
        | SemanticNodeKind.Float64FloorToInt64 value -> unary "float64FloorToInt64" value
        | SemanticNodeKind.Float64Sqrt value -> unary "float64Sqrt" value
        | SemanticNodeKind.Float64Abs value -> unary "float64Abs" value
        | SemanticNodeKind.Float64Negate value -> unary "float64Negate" value
        | SemanticNodeKind.Float64Floor value -> unary "float64Floor" value
        | SemanticNodeKind.Float64Ceiling value -> unary "float64Ceiling" value
        | SemanticNodeKind.Float64Truncate value -> unary "float64Truncate" value
        | SemanticNodeKind.StringUtf8ByteCount value -> unary "stringUtf8ByteCount" value
        | SemanticNodeKind.StringFromCodePoint value -> unary "stringFromCodePoint" value
        | SemanticNodeKind.StringToCodePoint value -> unary "stringToCodePoint" value
        | SemanticNodeKind.StringLength value -> unary "stringLength" value
        | SemanticNodeKind.StringSlice(value, start, stop) ->
            tagged "stringSlice" [ "valueNodeId", identity value; "startNodeId", identity start; "endNodeId", identity stop ]
        | SemanticNodeKind.StringContains(value, substring) -> tagged "stringContains" [ "valueNodeId", identity value; "substringNodeId", identity substring ]
        | SemanticNodeKind.StringIndexOf(value, substring, from) ->
            tagged "stringIndexOf" [ "valueNodeId", identity value; "substringNodeId", identity substring; "fromNodeId", identity from ]
        | SemanticNodeKind.StringSpan(value, set, from) ->
            tagged "stringSpan" [ "valueNodeId", identity value; "setNodeId", identity set; "fromNodeId", identity from ]
        | SemanticNodeKind.StringSpanBack(value, set, stop) ->
            tagged "stringSpanBack" [ "valueNodeId", identity value; "setNodeId", identity set; "endNodeId", identity stop ]
        | SemanticNodeKind.StringLastIndexOf(value, other) -> tagged "stringLastIndexOf" [ "valueNodeId", identity value; "substringNodeId", identity other ]
        | SemanticNodeKind.StringStartsWith(value, other) -> tagged "stringStartsWith" [ "valueNodeId", identity value; "prefixNodeId", identity other ]
        | SemanticNodeKind.StringEndsWith(value, other) -> tagged "stringEndsWith" [ "valueNodeId", identity value; "suffixNodeId", identity other ]
        | SemanticNodeKind.StringSplit(value, other) -> tagged "stringSplit" [ "valueNodeId", identity value; "separatorNodeId", identity other ]
        | SemanticNodeKind.StringSplitLines(value, breaks) -> tagged "stringSplitLines" [ "valueNodeId", identity value; "breaksNodeId", identity breaks ]
        | SemanticNodeKind.StringJoin(list, separator) -> tagged "stringJoin" [ "listNodeId", identity list; "separatorNodeId", identity separator ]
        | SemanticNodeKind.StringEqual(left, right) -> binary "stringEqual" left right
        | SemanticNodeKind.StringLessThan(left, right) -> binary "stringLessThan" left right
        | SemanticNodeKind.ValueEqual(left, right) -> binary "valueEqual" left right
        | SemanticNodeKind.StringConcat(left, right) -> binary "stringConcat" left right
        | SemanticNodeKind.Int64WrappingAdd(left, right) -> binary "int64WrappingAdd" left right
        | SemanticNodeKind.Int64WrappingSubtract(left, right) -> binary "int64WrappingSubtract" left right
        | SemanticNodeKind.Int64WrappingMultiply(left, right) -> binary "int64WrappingMultiply" left right
        | SemanticNodeKind.Int64FloorDivide(left, right) -> binary "int64FloorDivide" left right
        | SemanticNodeKind.Int64FloorModulo(left, right) -> binary "int64FloorModulo" left right
        | SemanticNodeKind.Int64Min(left, right) -> binary "int64Min" left right
        | SemanticNodeKind.Int64Max(left, right) -> binary "int64Max" left right
        | SemanticNodeKind.Int64CheckedAdd(left, right) -> binary "int64CheckedAdd" left right
        | SemanticNodeKind.Int64CheckedSubtract(left, right) -> binary "int64CheckedSubtract" left right
        | SemanticNodeKind.Int64CheckedMultiply(left, right) -> binary "int64CheckedMultiply" left right
        | SemanticNodeKind.ListRange(start, stop) -> tagged "listRange" [ "startNodeId", identity start; "endNodeId", identity stop ]
        | SemanticNodeKind.StringRepeat(text, count) -> tagged "stringRepeat" [ "textNodeId", identity text; "countNodeId", identity count ]
        | SemanticNodeKind.Float64Round(text, count) -> tagged "float64Round" [ "valueNodeId", identity text; "digitsNodeId", identity count ]
        | SemanticNodeKind.Int64Equal(left, right) -> binary "int64Equal" left right
        | SemanticNodeKind.Int64LessThan(left, right) -> binary "int64LessThan" left right
        | SemanticNodeKind.Int64LessThanOrEqual(left, right) -> binary "int64LessThanOrEqual" left right
        | SemanticNodeKind.Int64GreaterThan(left, right) -> binary "int64GreaterThan" left right
        | SemanticNodeKind.Int64GreaterThanOrEqual(left, right) -> binary "int64GreaterThanOrEqual" left right
        | SemanticNodeKind.Float64Add(left, right) -> binary "float64Add" left right
        | SemanticNodeKind.Float64Subtract(left, right) -> binary "float64Subtract" left right
        | SemanticNodeKind.Float64Multiply(left, right) -> binary "float64Multiply" left right
        | SemanticNodeKind.Float64Divide(left, right) -> binary "float64Divide" left right
        | SemanticNodeKind.Float64Equal(left, right) -> binary "float64Equal" left right
        | SemanticNodeKind.Float64LessThan(left, right) -> binary "float64LessThan" left right
        | SemanticNodeKind.Float64IsNaN value -> unary "float64IsNaN" value
        | SemanticNodeKind.Float64IsFinite value -> unary "float64IsFinite" value
        | SemanticNodeKind.Float64IsInfinite value -> unary "float64IsInfinite" value
        | SemanticNodeKind.Int64ToFloat64 value -> unary "int64ToFloat64" value
        | SemanticNodeKind.Int64WrappingAbs value -> unary "int64WrappingAbs" value
        | SemanticNodeKind.Bits32FromInt64 value -> unary "bits32FromInt64" value
        | SemanticNodeKind.Bits32ToInt64 value -> unary "bits32ToInt64" value
        | SemanticNodeKind.Bits32Not value -> unary "bits32Not" value
        | SemanticNodeKind.Bits32Xor(left, right) -> binary "bits32Xor" left right
        | SemanticNodeKind.Bits32And(left, right) -> binary "bits32And" left right
        | SemanticNodeKind.Bits32Or(left, right) -> binary "bits32Or" left right
        | SemanticNodeKind.Bits32Add(left, right) -> binary "bits32Add" left right
        | SemanticNodeKind.Bits32RotateRight(value, amount) -> tagged "bits32RotateRight" [ "valueNodeId", identity value; "amountNodeId", identity amount ]
        | SemanticNodeKind.Bits32ShiftRight(value, amount) -> tagged "bits32ShiftRight" [ "valueNodeId", identity value; "amountNodeId", identity amount ]
        | SemanticNodeKind.Bits32ShiftLeft(value, amount) -> tagged "bits32ShiftLeft" [ "valueNodeId", identity value; "amountNodeId", identity amount ]
        | SemanticNodeKind.ListEmpty itemType -> tagged "listEmpty" [ "itemType", typeRef itemType ]
        | SemanticNodeKind.SetEmpty itemType -> tagged "setEmpty" [ "itemType", typeRef itemType ]
        | SemanticNodeKind.MapEmpty(keyType, valueType) -> tagged "mapEmpty" [ "keyType", typeRef keyType; "valueType", typeRef valueType ]
        | SemanticNodeKind.ListConstruct(itemType, items) -> tagged "listConstruct" [ "itemType", typeRef itemType; "itemNodeIds", identities items ]
        | SemanticNodeKind.ListLength value -> unary "listLength" value
        | SemanticNodeKind.MapValues value -> unary "mapValues" value
        | SemanticNodeKind.MapKeys value -> unary "mapKeys" value
        | SemanticNodeKind.SetToList value -> unary "setToList" value
        | SemanticNodeKind.MapSize value -> unary "mapSize" value
        | SemanticNodeKind.SetFromList value -> unary "setFromList" value
        | SemanticNodeKind.SetSize value -> unary "setSize" value
        | SemanticNodeKind.ListReverse value -> unary "listReverse" value
        | SemanticNodeKind.ListLast value -> unary "listLast" value
        | SemanticNodeKind.ListDropLast value -> unary "listDropLast" value
        | SemanticNodeKind.ListAppendItem(list, item) -> tagged "listAppendItem" [ "listNodeId", identity list; "itemNodeId", identity item ]
        | SemanticNodeKind.ListItemAt(list, index) -> tagged "listItemAt" [ "listNodeId", identity list; "indexNodeId", identity index ]
        | SemanticNodeKind.MapRemove(map, key) -> tagged "mapRemove" [ "mapNodeId", identity map; "keyNodeId", identity key ]
        | SemanticNodeKind.SetInsert(map, key) -> tagged "setInsert" [ "setNodeId", identity map; "itemNodeId", identity key ]
        | SemanticNodeKind.SetRemove(map, key) -> tagged "setRemove" [ "setNodeId", identity map; "itemNodeId", identity key ]
        | SemanticNodeKind.SetUnion(left, right) -> binary "setUnion" left right
        | SemanticNodeKind.SetIntersection(left, right) -> binary "setIntersection" left right
        | SemanticNodeKind.SetDifference(left, right) -> binary "setDifference" left right
        | SemanticNodeKind.MapContainsKey(map, key) -> tagged "mapContainsKey" [ "mapNodeId", identity map; "keyNodeId", identity key ]
        | SemanticNodeKind.SetContains(map, key) -> tagged "setContains" [ "setNodeId", identity map; "itemNodeId", identity key ]
        | SemanticNodeKind.ListIndexOf(map, key) -> tagged "listIndexOf" [ "listNodeId", identity map; "itemNodeId", identity key ]
        | SemanticNodeKind.ListLastIndexOf(map, key) -> tagged "listLastIndexOf" [ "listNodeId", identity map; "itemNodeId", identity key ]
        | SemanticNodeKind.MapGet(map, key) -> tagged "mapGet" [ "mapNodeId", identity map; "keyNodeId", identity key ]
        | SemanticNodeKind.MapGetOrDefault(map, key, fallback) ->
            tagged "mapGetOrDefault" [ "mapNodeId", identity map; "keyNodeId", identity key; "defaultNodeId", identity fallback ]
        | SemanticNodeKind.ListSlice(list, start, stop) ->
            tagged "listSlice" [ "listNodeId", identity list; "startNodeId", identity start; "endNodeId", identity stop ]
        | SemanticNodeKind.MapInsert(map, key, value) ->
            tagged "mapInsert" [ "mapNodeId", identity map; "keyNodeId", identity key; "valueNodeId", identity value ]
        | SemanticNodeKind.ListConcat(left, right) -> binary "listConcat" left right
        | SemanticNodeKind.ListFold(source, seed, folder, bindings) ->
            tagged "listFold" ([ "sourceListNodeId", identity source; "seedNodeId", identity seed; "folderFunctionId", identity folder ] @ context bindings)
        | SemanticNodeKind.MapFold(source, seed, folder, bindings) ->
            tagged "mapFold" ([ "sourceMapNodeId", identity source; "seedNodeId", identity seed; "folderFunctionId", identity folder ] @ context bindings)
        | SemanticNodeKind.ListEffectFold(source, seed, folder, bindings) ->
            tagged "listEffectFold" ([ "sourceListNodeId", identity source; "seedNodeId", identity seed; "folderFunctionId", identity folder ] @ context bindings)
        | SemanticNodeKind.UseResource(acquire, releaseCapability, releaseOperation, body, bindings) ->
            tagged "useResource"
                ([ "acquireNodeId", identity acquire
                   "releaseCapabilityNodeId", identity releaseCapability
                   "releaseOperationId", identity releaseOperation
                   "bodyFunctionId", identity body ]
                 @ context bindings)
        | SemanticNodeKind.ParallelAll(source, worker, bindings) ->
            tagged "parallelAll" ([ "sourceNodeId", identity source; "workerFunctionId", identity worker ] @ context bindings)
        | SemanticNodeKind.CancellationRequested -> tagged "cancellationRequested" []
        | SemanticNodeKind.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
            let namedContext name bindings = context bindings |> List.map (fun (_, value) -> name, value)
            tagged "exceptionCatch" ([ "bodyFunctionId", identity body; "handlerFunctionId", identity handler ] @ namedContext "bodyContext" bodyContext @ namedContext "handlerContext" handlerContext)
        | SemanticNodeKind.ExceptionIsCancellation value -> tagged "exceptionIsCancellation" [ "valueNodeId", identity value ]
        | SemanticNodeKind.ExceptionMessage value -> tagged "exceptionMessage" [ "valueNodeId", identity value ]
        | SemanticNodeKind.ExceptionRethrow(value, resultType) -> tagged "exceptionRethrow" [ "valueNodeId", identity value; "resultType", typeRef resultType ]
        | SemanticNodeKind.CancellationShield(body, bindings) -> tagged "cancellationShield" ([ "bodyFunctionId", identity body ] @ context bindings)
        | SemanticNodeKind.TaskScope(body, bindings) -> tagged "taskScope" ([ "bodyFunctionId", identity body ] @ context bindings)
        | SemanticNodeKind.TaskStart(scope, worker, bindings, item) ->
            tagged "taskStart" ([ "scopeNodeId", identity scope; "workerFunctionId", identity worker; "itemNodeId", identity item ] @ context bindings)
        | SemanticNodeKind.TaskWait(tasks, mode, limit) ->
            tagged "taskWait"
                ([ "tasksNodeId", identity tasks; "mode", box (match mode with TaskWaitMode.Any -> "any" | TaskWaitMode.All -> "all") ]
                 @ (limit |> Option.map (fun limit -> "limitNodeId", identity limit) |> Option.toList))
        | SemanticNodeKind.OneShotCreate(scope, valueType) -> tagged "oneShotCreate" [ "scopeNodeId", identity scope; "valueType", WireEncodeCommon.typeRef valueType ]
        | SemanticNodeKind.OneShotComplete(cell, value) -> tagged "oneShotComplete" [ "cellNodeId", identity cell; "valueNodeId", identity value ]
        | SemanticNodeKind.OneShotWait(cell, limit) -> tagged "oneShotWait" ([ "cellNodeId", identity cell ] @ (limit |> Option.map (fun id -> "limitNodeId", identity id) |> Option.toList))
        | SemanticNodeKind.TaskCancel task -> tagged "taskCancel" [ "taskNodeId", identity task ]
        | SemanticNodeKind.ListMap(source, callback, bindings) -> traversal "listMap" source callback bindings
        | SemanticNodeKind.MapFromList(source, callback, bindings) -> traversal "mapFromList" source callback bindings
        | SemanticNodeKind.ListFilter(source, callback, bindings) -> traversal "listFilter" source callback bindings
        | SemanticNodeKind.MapFilter(source, callback, bindings) ->
            tagged "mapFilter" ([ "sourceMapNodeId", identity source; "keepFunctionId", identity callback ] @ context bindings)
        | SemanticNodeKind.ListSortBy(source, callback, bindings) -> traversal "listSortBy" source callback bindings
        | SemanticNodeKind.ListMinBy(source, callback, bindings) -> traversal "listMinBy" source callback bindings
        | SemanticNodeKind.MapMapValues(source, callback, bindings) ->
            tagged "mapMapValues" ([ "sourceMapNodeId", identity source; "mapperFunctionId", identity callback ] @ context bindings)
        | SemanticNodeKind.ListAny(source, callback, bindings) -> traversal "listAny" source callback bindings
        | SemanticNodeKind.ListFindIndex(source, callback, bindings) -> traversal "listFindIndex" source callback bindings
        | SemanticNodeKind.ListAll(source, callback, bindings) -> traversal "listAll" source callback bindings
        | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
            tagged "capabilityConstruct"
                [ "capabilityId", identity capabilityId
                  "implementations", implementations |> List.sortBy fst |> List.map (fun (operationId, functionId) ->
                      objectValue [ "operationId", identity operationId; "functionId", identity functionId ]) |> arrayValue ]
        | SemanticNodeKind.FunctionReference target -> tagged "functionReference" [ "functionId", identity target ]
        | SemanticNodeKind.FunctionInvoke(value, arguments) -> tagged "functionInvoke" [ "functionValueNodeId", identity value; "arguments", identities arguments ]
        | SemanticNodeKind.DirectCall(functionId, arguments) ->
            tagged "directCall"
                [ "functionId", identity functionId
                  "arguments", argumentList arguments ]
        | SemanticNodeKind.CapabilityInvoke(receiver, operationId, arguments) ->
            tagged "capabilityInvoke"
                [ "capabilityValueNodeId", identity receiver
                  "operationId", identity operationId
                  "arguments", arguments |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId) |> List.map (fun argument ->
                    objectValue [ "capabilityParameterId", identity argument.TargetCapabilityParameterId; "valueNodeId", identity argument.ValueNodeId ]) |> arrayValue ]
        | SemanticNodeKind.ProductConstruct(typeId, values) -> tagged "productConstruct" [ "typeId", identity typeId; "fields", fields values ]
        | SemanticNodeKind.FieldAccess(source, fieldId) -> tagged "fieldAccess" [ "sourceNodeId", identity source; "fieldId", identity fieldId ]
        | SemanticNodeKind.ProductUpdate(typeId, source, values) ->
            tagged "productUpdate" [ "typeId", identity typeId; "sourceNodeId", identity source; "fields", fields values ]
        | SemanticNodeKind.UnionConstruct(variantId, values) -> tagged "unionConstruct" [ "variantId", identity variantId; "fields", fields values ]
        | SemanticNodeKind.OptionNone itemType -> tagged "optionNone" [ "itemType", typeRef itemType ]
        | SemanticNodeKind.OptionSome value -> unary "optionSome" value
        | SemanticNodeKind.ResultOk(errorType, value) -> tagged "resultOk" [ "errorType", typeRef errorType; "valueNodeId", identity value ]
        | SemanticNodeKind.Abort error -> tagged "abort" [ "errorNodeId", identity error ]
        | SemanticNodeKind.ResultErr(okType, error) -> tagged "resultErr" [ "okType", typeRef okType; "errorNodeId", identity error ]
        | SemanticNodeKind.BoolNot operand -> tagged "boolNot" [ "operandNodeId", identity operand ]
        | SemanticNodeKind.BoolAnd(left, right) -> tagged "boolAnd" [ "leftNodeId", identity left; "right", graph right ]
        | SemanticNodeKind.BoolOr(left, right) -> tagged "boolOr" [ "leftNodeId", identity left; "right", graph right ]
        | SemanticNodeKind.Conditional(condition, whenTrue, whenFalse) ->
            tagged "conditional" [ "conditionNodeId", identity condition; "whenTrue", graph whenTrue; "whenFalse", graph whenFalse ]
        | SemanticNodeKind.MatchUnion(input, arms) ->
            tagged "matchUnion"
                [ "inputNodeId", identity input
                  "arms", arms |> List.sortBy (fun arm -> UnionMatchArm.key arm) |> List.map (fun arm ->
                    objectValue
                        [ (match UnionMatchArm.canonicalVariantIds arm with
                           | [ variantId ] -> "variantId", identity variantId
                           | variantIds -> "variantIds", variantIds |> List.map identity |> arrayValue)
                          "fieldBindings", arm.FieldBindings |> List.sortBy (fun field -> StableId.value field.SourceFieldId) |> List.map (fun field ->
                            objectValue [ "fieldId", identity field.SourceFieldId; "binding", binding field.Binding ]) |> arrayValue
                          "body", graph arm.Body ]) |> arrayValue ]
        | SemanticNodeKind.Bind(input, body) -> tagged "bind" [ "inputNodeId", identity input; "body", valueArm body ]
        | SemanticNodeKind.MatchOption(input, someArm, noneBody) ->
            tagged "matchOption" [ "inputNodeId", identity input; "someArm", valueArm someArm; "noneBody", graph noneBody ]
        | SemanticNodeKind.MatchResult(input, okArm, errArm) ->
            tagged "matchResult"
                [ "inputNodeId", identity input
                  "okArm", valueArm okArm
                  "errArm", objectValue [ "errorBinding", binding errArm.ErrorBinding; "body", graph errArm.Body ] ]

    let functionEntity (value: SemanticFunction) =
        objectValue
            [ "id", identity value.Id
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map (fun parameter ->
                objectValue [ "id", identity parameter.Id; "displayName", box parameter.DisplayName; "type", typeRef parameter.Type ]) |> arrayValue
              "resultType", typeRef value.ResultType
              "requiredEffects", identities (value.RequiredEffects |> List.sortBy StableId.value)
              "body", graph value.Body ]

    let focus value = WireEncodeCommon.focusWith functionEntity value
