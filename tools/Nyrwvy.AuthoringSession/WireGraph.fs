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
        let context (bindings: ArgumentBinding list) =
            if List.isEmpty bindings then []
            else
                [ "context",
                  bindings |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId) |> List.map (fun argument ->
                      objectValue [ "parameterId", identity argument.TargetParameterId; "valueNodeId", identity argument.ValueNodeId ]) |> arrayValue ]
        let traversal name source functionId bindings =
            tagged name ([ "sourceNodeId", identity source; "functionId", identity functionId ] @ context bindings)
        let valueArm (arm: ValueMatchArm) =
            objectValue [ "valueBinding", binding arm.ValueBinding; "body", graph arm.Body ]
        match value with
        | SemanticNodeKind.ParameterReference parameterId -> tagged "parameter" [ "parameterId", identity parameterId ]
        | SemanticNodeKind.PatternReference bindingId -> tagged "patternReference" [ "bindingId", identity bindingId ]
        | SemanticNodeKind.StringLiteral value -> tagged "stringLiteral" [ "value", box value ]
        | SemanticNodeKind.BoolLiteral value -> tagged "boolLiteral" [ "value", box value ]
        | SemanticNodeKind.UnitLiteral -> tagged "unitLiteral" []
        | SemanticNodeKind.Int64Literal value -> tagged "int64Literal" [ "value", box (value.ToString(CultureInfo.InvariantCulture)) ]
        | SemanticNodeKind.Float64Literal value -> tagged "float64Literal" [ "bits", box (Float64Bits.toHex value) ]
        | SemanticNodeKind.StringIsEmpty value -> unary "stringIsEmpty" value
        | SemanticNodeKind.Int64ToString value -> unary "int64ToString" value
        | SemanticNodeKind.StringParseInt64 value -> unary "stringParseInt64" value
        | SemanticNodeKind.StringCodePoints value -> unary "stringCodePoints" value
        | SemanticNodeKind.StringLength value -> unary "stringLength" value
        | SemanticNodeKind.StringSlice(value, start, stop) ->
            tagged "stringSlice" [ "valueNodeId", identity value; "startNodeId", identity start; "endNodeId", identity stop ]
        | SemanticNodeKind.StringContains(value, substring) -> tagged "stringContains" [ "valueNodeId", identity value; "substringNodeId", identity substring ]
        | SemanticNodeKind.StringIndexOf(value, substring, from) ->
            tagged "stringIndexOf" [ "valueNodeId", identity value; "substringNodeId", identity substring; "fromNodeId", identity from ]
        | SemanticNodeKind.StringLastIndexOf(value, other) -> tagged "stringLastIndexOf" [ "valueNodeId", identity value; "substringNodeId", identity other ]
        | SemanticNodeKind.StringStartsWith(value, other) -> tagged "stringStartsWith" [ "valueNodeId", identity value; "prefixNodeId", identity other ]
        | SemanticNodeKind.StringEndsWith(value, other) -> tagged "stringEndsWith" [ "valueNodeId", identity value; "suffixNodeId", identity other ]
        | SemanticNodeKind.StringSplit(value, other) -> tagged "stringSplit" [ "valueNodeId", identity value; "separatorNodeId", identity other ]
        | SemanticNodeKind.StringEqual(left, right) -> tagged "stringEqual" [ "leftNodeId", identity left; "rightNodeId", identity right ]
        | SemanticNodeKind.StringLessThan(left, right) -> tagged "stringLessThan" [ "leftNodeId", identity left; "rightNodeId", identity right ]
        | SemanticNodeKind.ValueEqual(left, right) -> tagged "valueEqual" [ "leftNodeId", identity left; "rightNodeId", identity right ]
        | SemanticNodeKind.StringConcat(left, right) -> tagged "stringConcat" [ "leftNodeId", identity left; "rightNodeId", identity right ]
        | SemanticNodeKind.Int64WrappingAdd(left, right) -> binary "int64WrappingAdd" left right
        | SemanticNodeKind.Int64WrappingSubtract(left, right) -> binary "int64WrappingSubtract" left right
        | SemanticNodeKind.Int64WrappingMultiply(left, right) -> binary "int64WrappingMultiply" left right
        | SemanticNodeKind.Int64FloorDivide(left, right) -> binary "int64FloorDivide" left right
        | SemanticNodeKind.Int64FloorModulo(left, right) -> binary "int64FloorModulo" left right
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
        | SemanticNodeKind.ListEmpty itemType -> tagged "listEmpty" [ "itemType", typeRef itemType ]
        | SemanticNodeKind.ListConstruct(itemType, items) -> tagged "listConstruct" [ "itemType", typeRef itemType; "itemNodeIds", identities items ]
        | SemanticNodeKind.ListLength value -> unary "listLength" value
        | SemanticNodeKind.ListAppendItem(list, item) -> tagged "listAppendItem" [ "listNodeId", identity list; "itemNodeId", identity item ]
        | SemanticNodeKind.ListItemAt(list, index) -> tagged "listItemAt" [ "listNodeId", identity list; "indexNodeId", identity index ]
        | SemanticNodeKind.ListConcat(left, right) -> binary "listConcat" left right
        | SemanticNodeKind.ListFold(source, seed, folder, bindings) ->
            tagged "listFold" ([ "sourceListNodeId", identity source; "seedNodeId", identity seed; "folderFunctionId", identity folder ] @ context bindings)
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
        | SemanticNodeKind.TaskScope(body, bindings) -> tagged "taskScope" ([ "bodyFunctionId", identity body ] @ context bindings)
        | SemanticNodeKind.TaskStart(scope, worker, bindings, item) ->
            tagged "taskStart" ([ "scopeNodeId", identity scope; "workerFunctionId", identity worker; "itemNodeId", identity item ] @ context bindings)
        | SemanticNodeKind.TaskWait(tasks, mode, limit) ->
            tagged "taskWait"
                ([ "tasksNodeId", identity tasks; "mode", box (match mode with TaskWaitMode.Any -> "any" | TaskWaitMode.All -> "all") ]
                 @ (limit |> Option.map (fun limit -> "limitNodeId", identity limit) |> Option.toList))
        | SemanticNodeKind.TaskCancel task -> tagged "taskCancel" [ "taskNodeId", identity task ]
        | SemanticNodeKind.ListMap(source, callback, bindings) -> traversal "listMap" source callback bindings
        | SemanticNodeKind.ListFilter(source, callback, bindings) -> traversal "listFilter" source callback bindings
        | SemanticNodeKind.ListSortBy(source, callback, bindings) -> traversal "listSortBy" source callback bindings
        | SemanticNodeKind.ListAny(source, callback, bindings) -> traversal "listAny" source callback bindings
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
                  "arguments", arguments |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId) |> List.map (fun argument ->
                    objectValue [ "parameterId", identity argument.TargetParameterId; "valueNodeId", identity argument.ValueNodeId ]) |> arrayValue ]
        | SemanticNodeKind.CapabilityInvoke(receiver, operationId, arguments) ->
            tagged "capabilityInvoke"
                [ "capabilityValueNodeId", identity receiver
                  "operationId", identity operationId
                  "arguments", arguments |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId) |> List.map (fun argument ->
                    objectValue [ "capabilityParameterId", identity argument.TargetCapabilityParameterId; "valueNodeId", identity argument.ValueNodeId ]) |> arrayValue ]
        | SemanticNodeKind.ProductConstruct(typeId, values) -> tagged "productConstruct" [ "typeId", identity typeId; "fields", fields values ]
        | SemanticNodeKind.FieldAccess(source, fieldId) -> tagged "fieldAccess" [ "sourceNodeId", identity source; "fieldId", identity fieldId ]
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
                  "arms", arms |> List.sortBy (fun arm -> StableId.value arm.VariantId) |> List.map (fun arm ->
                    objectValue
                        [ "variantId", identity arm.VariantId
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
