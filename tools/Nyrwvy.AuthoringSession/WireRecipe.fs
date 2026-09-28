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
        let callbackContext () =
            match tryProperty "context" element with
            | None -> []
            | Some _ -> callArguments "context"

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
            ExpressionRecipe.StringLiteral(requiredString "value" element)
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
        | "stringParseInt64" -> unary "string-parse-int64 recipe" ExpressionRecipe.StringParseInt64
        | "stringLength" -> unary "string-length recipe" ExpressionRecipe.StringLength
        | "stringSlice" ->
            ensureFields "string-slice recipe" [ "kind"; "value"; "start"; "end" ] [ "kind"; "value"; "start"; "end" ] element
            ExpressionRecipe.StringSlice(recipe (property "value" element), recipe (property "start" element), recipe (property "end" element))
        | "stringContains" ->
            ensureFields "string-contains recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringContains(recipe (property "value" element), recipe (property "substring" element))
        | "stringEqual" -> binary "string-equal recipe" ExpressionRecipe.StringEqual
        | "stringLessThan" -> binary "string-less-than recipe" ExpressionRecipe.StringLessThan
        | "valueEqual" -> binary "value-equal recipe" ExpressionRecipe.ValueEqual
        | "stringConcat" -> binary "string-concat recipe" ExpressionRecipe.StringConcat
        | "int64WrappingAdd" -> binary "int64-wrapping-add recipe" ExpressionRecipe.Int64WrappingAdd
        | "int64WrappingSubtract" -> binary "int64-wrapping-subtract recipe" ExpressionRecipe.Int64WrappingSubtract
        | "int64WrappingMultiply" -> binary "int64-wrapping-multiply recipe" ExpressionRecipe.Int64WrappingMultiply
        | "int64FloorDivide" -> binary "int64-floor-divide recipe" ExpressionRecipe.Int64FloorDivide
        | "int64FloorModulo" -> binary "int64-floor-modulo recipe" ExpressionRecipe.Int64FloorModulo
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
        | "listEmpty" ->
            ensureFields "list-empty recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.ListEmpty(typeRef (property "itemType" element))
        | "listConstruct" ->
            ensureFields "list-construct recipe" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] element
            ExpressionRecipe.ListConstruct(typeRef (property "itemType" element), array "items" element |> List.map recipe)
        | "listLength" -> unary "list-length recipe" ExpressionRecipe.ListLength
        | "listAppendItem" ->
            ensureFields "list-append-item recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListAppendItem(recipe (property "list" element), recipe (property "item" element))
        | "listConcat" -> binary "list-concat recipe" ExpressionRecipe.ListConcat
        | "listItemAt" ->
            ensureFields "list-item-at recipe" [ "kind"; "list"; "index" ] [ "kind"; "list"; "index" ] element
            ExpressionRecipe.ListItemAt(recipe (property "list" element), recipe (property "index" element))
        | "listFold" ->
            ensureFields "list-fold recipe" [ "kind"; "source"; "seed"; "functionId"; "context" ] [ "kind"; "source"; "seed"; "functionId" ] element
            ExpressionRecipe.ListFold(recipe (property "source" element), recipe (property "seed" element), id<FunctionTag> "functionId" element, callbackContext ())
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
        | "taskScope" ->
            ensureFields "task-scope recipe" [ "kind"; "bodyFunctionId"; "context" ] [ "kind"; "bodyFunctionId" ] element
            ExpressionRecipe.TaskScope(id<FunctionTag> "bodyFunctionId" element, callbackContext ())
        | "taskStart" ->
            ensureFields "task-start recipe" [ "kind"; "scope"; "workerFunctionId"; "item"; "context" ] [ "kind"; "scope"; "workerFunctionId"; "item" ] element
            ExpressionRecipe.TaskStart(recipe (property "scope" element), id<FunctionTag> "workerFunctionId" element, callbackContext (), recipe (property "item" element))
        | "taskWait" ->
            ensureFields "task-wait recipe" [ "kind"; "tasks"; "mode"; "limit" ] [ "kind"; "tasks"; "mode" ] element
            ExpressionRecipe.TaskWait(recipe (property "tasks" element), WireInput.taskWaitMode element, tryProperty "limit" element |> Option.map recipe)
        | "taskCancel" ->
            ensureFields "task-cancel recipe" [ "kind"; "task" ] [ "kind"; "task" ] element
            ExpressionRecipe.TaskCancel(recipe (property "task" element))
        | "listMap"
        | "listSortBy"
        | "listFilter"
        | "listAny"
        | "listAll" as kind ->
            ensureFields $"{kind} recipe" [ "kind"; "source"; "functionId"; "context" ] [ "kind"; "source"; "functionId" ] element
            let source = recipe (property "source" element)
            let functionId = id<FunctionTag> "functionId" element
            let context = callbackContext ()
            match kind with
            | "listMap" -> ExpressionRecipe.ListMap(source, functionId, context)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, functionId, context)
            | "listSortBy" -> ExpressionRecipe.ListSortBy(source, functionId, context)
            | "listAny" -> ExpressionRecipe.ListAny(source, functionId, context)
            | _ -> ExpressionRecipe.ListAll(source, functionId, context)
        | "productConstruct" ->
            ensureFields "product-construct recipe" [ "kind"; "typeId"; "fields" ] [ "kind"; "typeId"; "fields" ] element
            ExpressionRecipe.ProductConstruct(id<TypeTag> "typeId" element, fieldValues "fields")
        | "fieldAccess" ->
            ensureFields "field-access recipe" [ "kind"; "source"; "fieldId" ] [ "kind"; "source"; "fieldId" ] element
            ExpressionRecipe.FieldAccess(recipe (property "source" element), id<FieldTag> "fieldId" element)
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
            ensureFields "match-union recipe" [ "kind"; "input"; "arms" ] [ "kind"; "input"; "arms" ] element
            let arms =
                array "arms" element
                |> List.map (fun arm ->
                    ensureFields "union arm" [ "variantId"; "body" ] [ "variantId"; "body" ] arm
                    id<VariantTag> "variantId" arm, recipe (property "body" arm))
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
