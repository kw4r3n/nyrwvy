namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open Nyrwvy.Core

module internal WireRecipe =
    open WireInput

    let rec recipe (element: JsonElement): ExpressionRecipe =
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
        | "listFold" ->
            ensureFields "list-fold recipe" [ "kind"; "source"; "seed"; "functionId"; "context" ] [ "kind"; "source"; "seed"; "functionId" ] element
            ExpressionRecipe.ListFold(recipe (property "source" element), recipe (property "seed" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "mapFold" ->
            ensureFields "map-fold recipe" [ "kind"; "map"; "seed"; "functionId"; "context" ] [ "kind"; "map"; "seed"; "functionId" ] element
            ExpressionRecipe.MapFold(recipe (property "map" element), recipe (property "seed" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "setFold" ->
            ensureFields "set-fold recipe" [ "kind"; "set"; "seed"; "functionId"; "context" ] [ "kind"; "set"; "seed"; "functionId" ] element
            ExpressionRecipe.SetFold(recipe (property "set" element), recipe (property "seed" element), id<FunctionTag> "functionId" element, callbackContext ())
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
        | "exceptionCatch" ->
            ensureFields "exception-catch recipe" [ "kind"; "bodyFunctionId"; "handlerFunctionId"; "bodyContext"; "handlerContext" ] [ "kind"; "bodyFunctionId"; "handlerFunctionId" ] element
            ExpressionRecipe.ExceptionCatch(id<FunctionTag> "bodyFunctionId" element, namedCallbackContext "bodyContext", id<FunctionTag> "handlerFunctionId" element, namedCallbackContext "handlerContext")
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
        | "mapFilter" ->
            ensureFields "map-filter recipe" [ "kind"; "map"; "functionId"; "context" ] [ "kind"; "map"; "functionId" ] element
            ExpressionRecipe.MapFilter(recipe (property "map" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "setFilter" ->
            ensureFields "set-filter recipe" [ "kind"; "set"; "functionId"; "context" ] [ "kind"; "set"; "functionId" ] element
            ExpressionRecipe.SetFilter(recipe (property "set" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "optionMap" ->
            ensureFields "option-map recipe" [ "kind"; "option"; "functionId"; "context" ] [ "kind"; "option"; "functionId" ] element
            ExpressionRecipe.OptionMap(recipe (property "option" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "optionAndThen" ->
            ensureFields "option-and-then recipe" [ "kind"; "option"; "functionId"; "context" ] [ "kind"; "option"; "functionId" ] element
            ExpressionRecipe.OptionAndThen(recipe (property "option" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "resultMap" ->
            ensureFields "result-map recipe" [ "kind"; "result"; "functionId"; "context" ] [ "kind"; "result"; "functionId" ] element
            ExpressionRecipe.ResultMap(recipe (property "result" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "resultMapErr" ->
            ensureFields "result-map-err recipe" [ "kind"; "result"; "functionId"; "context" ] [ "kind"; "result"; "functionId" ] element
            ExpressionRecipe.ResultMapErr(recipe (property "result" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "resultAndThen" ->
            ensureFields "result-and-then recipe" [ "kind"; "result"; "functionId"; "context" ] [ "kind"; "result"; "functionId" ] element
            ExpressionRecipe.ResultAndThen(recipe (property "result" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "mapMapValues" ->
            ensureFields "map-map-values recipe" [ "kind"; "map"; "functionId"; "context" ] [ "kind"; "map"; "functionId" ] element
            ExpressionRecipe.MapMapValues(recipe (property "map" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "setMap" ->
            ensureFields "set-map recipe" [ "kind"; "set"; "functionId"; "context" ] [ "kind"; "set"; "functionId" ] element
            ExpressionRecipe.SetMap(recipe (property "set" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "listZipWith" ->
            ensureFields "list-zip-with recipe" [ "kind"; "left"; "right"; "functionId"; "context" ] [ "kind"; "left"; "right"; "functionId" ] element
            ExpressionRecipe.ListZipWith(recipe (property "left" element), recipe (property "right" element), id<FunctionTag> "functionId" element, callbackContext ())
        | "listFlatMap"
        | "listMap"
        | "mapFromList"
        | "listSortBy"
        | "listMinBy"
        | "listMaxBy"
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
            | "listFlatMap" -> ExpressionRecipe.ListFlatMap(source, functionId, context)
            | "mapFromList" -> ExpressionRecipe.MapFromList(source, functionId, context)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, functionId, context)
            | "listSortBy" -> ExpressionRecipe.ListSortBy(source, functionId, context)
            | "listMinBy" -> ExpressionRecipe.ListMinBy(source, functionId, context)
            | "listMaxBy" -> ExpressionRecipe.ListMaxBy(source, functionId, context)
            | "listAny" -> ExpressionRecipe.ListAny(source, functionId, context)
            | "listFindIndex" -> ExpressionRecipe.ListFindIndex(source, functionId, context)
            | _ -> ExpressionRecipe.ListAll(source, functionId, context)
        | "productConstruct" ->
            ensureFields "product-construct recipe" [ "kind"; "typeId"; "typeArguments"; "fields" ] [ "kind"; "typeId"; "fields" ] element
            ExpressionRecipe.ProductConstruct(id<TypeTag> "typeId" element, optionalTypeArguments "typeArguments" typeRef element, fieldValues "fields")
        | "fieldAccess" ->
            ensureFields "field-access recipe" [ "kind"; "source"; "fieldId" ] [ "kind"; "source"; "fieldId" ] element
            ExpressionRecipe.FieldAccess(recipe (property "source" element), id<FieldTag> "fieldId" element)
        | "productUpdate" ->
            ensureFields "product-update recipe" [ "kind"; "typeId"; "source"; "fields" ] [ "kind"; "typeId"; "source"; "fields" ] element
            ExpressionRecipe.ProductUpdate(id<TypeTag> "typeId" element, recipe (property "source" element), fieldValues "fields")
        | "unionConstruct" ->
            ensureFields "union-construct recipe" [ "kind"; "variantId"; "typeArguments"; "fields" ] [ "kind"; "variantId"; "fields" ] element
            ExpressionRecipe.UnionConstruct(id<VariantTag> "variantId" element, optionalTypeArguments "typeArguments" typeRef element, fieldValues "fields")
        | "optionNone" ->
            ensureFields "option-none recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.OptionNone(typeRef (property "itemType" element))
        | "resultOk" ->
            ensureFields "result-ok recipe" [ "kind"; "errorType"; "value" ] [ "kind"; "errorType"; "value" ] element
            ExpressionRecipe.ResultOk(typeRef (property "errorType" element), recipe (property "value" element))
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
        | "matchUnion" ->
            ensureFields "match-union recipe" [ "kind"; "input"; "arms"; "otherwise" ] [ "kind"; "input"; "arms" ] element
            let arms = unionArms "variantId" "variantIds" (stableId<VariantTag> "variantId") recipe element
            ExpressionRecipe.MatchUnion(recipe (property "input" element), arms)
        | value ->
            match WireRecipeCommon.tryReferenceFree recipe element value with
            | Some decoded -> decoded
            | None -> fail $"Unsupported expression recipe kind '{value}'."
