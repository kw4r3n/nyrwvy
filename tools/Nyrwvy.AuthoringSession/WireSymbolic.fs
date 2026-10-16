namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open Nyrwvy.Core

module internal WireSymbolic =
    open WireInput

    let private exactOne label displayName items getDisplayName =
        match items |> List.filter (fun item -> getDisplayName item = displayName) with
        | [ item ] -> item
        | [] -> fail $"Unknown {label} '{displayName}'."
        | _ -> fail $"Ambiguous {label} '{displayName}'."

    let resolveType (declarations: AuthoringDeclarations) displayName =
        exactOne "type" displayName declarations.Types (fun item -> item.DisplayName)

    let resolveFunction (declarations: AuthoringDeclarations) displayName =
        exactOne "function" displayName declarations.Functions (fun item -> item.DisplayName)

    let resolveEffect (declarations: AuthoringDeclarations) displayName =
        exactOne "effect" displayName declarations.Effects (fun item -> item.DisplayName)

    let resolveCapability (declarations: AuthoringDeclarations) displayName =
        exactOne "capability" displayName declarations.Capabilities (fun item -> item.DisplayName)

    /// Explicit selectors lower immediately to current exact semantic identities.
    /// An optional name/pin/ownership is a guard, never a fallback for an unknown ID.
    let private resolveSelector kind (declarations: AuthoringDeclarations) items getId getName (selector: JsonElement) =
        if selector.ValueKind = JsonValueKind.String then exactOne kind (selector.GetString()) items getName
        else
            ensureFields (kind + " selector") [ "id"; "name"; "dependencySnapshotId"; "ownership" ] [] selector
            let text key = tryProperty key selector |> Option.map (fun _ -> requiredString key selector)
            let id, name, dependency, ownership = text "id", text "name", text "dependencySnapshotId", text "ownership"
            if id.IsNone && name.IsNone then fail "A declaration selector needs id or name."
            if ownership |> Option.exists (fun value -> value <> "local" && value <> "imported") then fail "Selector ownership must be local or imported."
            let candidates = items |> List.filter (fun item ->
                let itemId = getId item
                let provenance = declarations.Provenance |> List.tryFind (fun p -> p.Kind = kind && p.Id = itemId)
                (id |> Option.forall ((=) itemId))
                && (name |> Option.forall ((=) (getName item)))
                && (dependency |> Option.forall (fun selected -> provenance |> Option.exists (fun p -> p.Sources |> List.exists (fun source -> source.SnapshotId = selected))))
                && (ownership |> Option.forall (fun owned -> provenance |> Option.exists (fun p -> p.IsLocal = (owned = "local")))))
            match candidates with
            | [ item ] -> item
            | [] ->
                // Advice never participates in resolution. An explicit ID remains authoritative
                // even when a supplied name happens to identify some other declaration.
                let related =
                    items |> List.filter (fun item ->
                        match id, name with
                        | Some selectedId, _ -> getId item = selectedId
                        | None, Some selectedName -> getName item = selectedName
                        | _ -> false)
                    |> List.sortBy getId
                let advice =
                    related |> List.truncate 5 |> List.map (fun item ->
                        let itemId = getId item
                        let provenance = declarations.Provenance |> List.tryFind (fun p -> p.Kind = kind && p.Id = itemId)
                        let owner = provenance |> Option.map (fun p -> if p.IsLocal then "local" else "imported") |> Option.defaultValue "unknown"
                        let pins = provenance |> Option.map (fun p -> p.Sources |> List.map (fun s -> s.SnapshotId) |> List.sort) |> Option.defaultValue []
                        let mismatched =
                            [ if name |> Option.exists ((<>) (getName item)) then "name"
                              if dependency |> Option.exists (fun pin -> not (List.contains pin pins)) then "dependencySnapshotId"
                              if ownership |> Option.exists ((<>) owner) then "ownership" ]
                        let shownPins = pins |> List.truncate 5 |> String.concat ", "
                        let omittedPins = if pins.Length > 5 then $" (+{pins.Length - 5} more)" else ""
                        let mismatchedGuards = String.concat ", " mismatched
                        $"id={itemId}, name={JsonSerializer.Serialize(getName item)}, ownership={owner}, providing pins=[{shownPins}]{omittedPins}, mismatched guards=[{mismatchedGuards}]")
                let omitted = if related.Length > 5 then $" (+{related.Length - 5} more candidates)" else ""
                let context = if advice.IsEmpty then "" else " Current candidates (advice only): " + String.concat "; " advice + omitted + "."
                fail ($"No current {kind} matches the explicit declaration selector; inspect declarations and its provenance." + context)
            | _ -> fail $"Ambiguous {kind} selector; specify an exact id or a providing dependencySnapshotId."

    let resolveTypeSelector declarations selector = resolveSelector "type" declarations declarations.Types (fun (t: SemanticType) -> StableId.value t.Id) (fun t -> t.DisplayName) selector
    let resolveFunctionSelector declarations selector = resolveSelector "function" declarations declarations.Functions (fun (f: SemanticFunction) -> StableId.value f.Id) (fun f -> f.DisplayName) selector
    let resolveEffectSelector declarations selector = resolveSelector "effect" declarations declarations.Effects (fun (e: SemanticEffect) -> StableId.value e.Id) (fun e -> e.DisplayName) selector
    let resolveCapabilitySelector declarations selector = resolveSelector "capability" declarations declarations.Capabilities (fun (c: SemanticCapability) -> StableId.value c.Id) (fun c -> c.DisplayName) selector
    let resolveTypeAt declarations key element = resolveTypeSelector declarations (property key element)
    let resolveFunctionAt declarations key element = resolveFunctionSelector declarations (property key element)
    let resolveEffectAt declarations key element = resolveEffectSelector declarations (property key element)
    let resolveCapabilityAt declarations key element = resolveCapabilitySelector declarations (property key element)

    let productField (owner: SemanticType) displayName =
        match owner.Kind with
        | SemanticTypeKind.Product fields ->
            exactOne $"field on {owner.DisplayName}" displayName fields (fun item -> item.DisplayName)
        | SemanticTypeKind.Union _ ->
            fail $"Type '{owner.DisplayName}' is not a product type."

    let unionVariant (owner: SemanticType) displayName =
        match owner.Kind with
        | SemanticTypeKind.Union variants ->
            exactOne $"variant on {owner.DisplayName}" displayName variants (fun item -> item.DisplayName)
        | SemanticTypeKind.Product _ ->
            fail $"Type '{owner.DisplayName}' is not a union type."

    let variantField (variant: SemanticVariant) displayName =
        exactOne $"field on variant {variant.DisplayName}" displayName variant.Fields (fun item -> item.DisplayName)

    let capabilityOperation (capability: SemanticCapability) displayName =
        exactOne $"operation on {capability.DisplayName}" displayName capability.Operations (fun item -> item.DisplayName)

    let functionParameter (fn: SemanticFunction) displayName =
        exactOne $"parameter on {fn.DisplayName}" displayName fn.Parameters (fun item -> item.DisplayName)

    let capabilityParameter (operation: SemanticCapabilityOperation) displayName =
        exactOne $"parameter on operation {operation.DisplayName}" displayName operation.Parameters (fun item -> item.DisplayName)

    /// Resolves a symbolic TypeRef. A name in `group` (types being created together) resolves to its
    /// request-local reference before existing declarations are consulted.
    let rec private typeRefIn (group: Map<string, TypeId>) (declarations: AuthoringDeclarations) (element: JsonElement): TypeRef =
        let typeRef = typeRefIn group
        match requiredString "kind" element with
        | "function" ->
            ensureFields "Function type" [ "kind"; "parameters"; "resultType" ] [ "kind"; "parameters"; "resultType" ] element
            TypeRef.Function(array "parameters" element |> List.map (typeRef declarations), typeRef declarations (property "resultType" element))
        | "primitive" ->
            primitiveType element
        | "named" ->
            ensureFields "named type" [ "kind"; "name" ] [ "kind"; "name" ] element
            let selector = property "name" element
            let reference = if selector.ValueKind = JsonValueKind.String then Map.tryFind (selector.GetString()) group else None
            match reference with
            | Some reference -> TypeRef.Named reference
            | None -> TypeRef.Named((resolveTypeSelector declarations selector).Id)
        | "capability" ->
            ensureFields "capability type" [ "kind"; "name" ] [ "kind"; "name" ] element
            TypeRef.Capability((resolveCapabilityAt declarations "name" element).Id)
        | "builtinGeneric" ->
            builtinGenericType (typeRef declarations) element
        | "typeParameter" ->
            // A request-local type parameter name; function creation allocates its identity.
            ensureFields "type parameter" [ "kind"; "name" ] [ "kind"; "name" ] element
            let name = requiredString "name" element
            if System.String.IsNullOrWhiteSpace name then fail "Type parameter names must not be blank."
            TypeRef.TypeParameter(id<TypeParameterTag> "name" element, name)
        | value -> fail $"Unsupported symbolic type kind '{value}'."

    let typeRef declarations element = typeRefIn Map.empty declarations element

    let private typeFieldIntent group declarations (item: JsonElement): AgentTypeFieldIntent =
        ensureFields "type field" [ "displayName"; "type" ] [ "displayName"; "type" ] item
        { DisplayName = requiredString "displayName" item
          Type = typeRefIn group declarations (property "type" item) }

    /// Resolves a type declaration's kind; names in `group` resolve to request-local references.
    let typeKindIntentIn group declarations (element: JsonElement): AgentTypeDeclarationKindIntent =
        match requiredString "kind" element with
        | "product" ->
            ensureFields "product type kind" [ "kind"; "fields" ] [ "kind"; "fields" ] element
            AgentTypeDeclarationKindIntent.Product(array "fields" element |> List.map (typeFieldIntent group declarations))
        | "union" ->
            ensureFields "union type kind" [ "kind"; "variants" ] [ "kind"; "variants" ] element
            let variants =
                array "variants" element
                |> List.map (fun item ->
                    ensureFields "union variant" [ "displayName"; "fields" ] [ "displayName"; "fields" ] item
                    { DisplayName = requiredString "displayName" item
                      Fields = array "fields" item |> List.map (typeFieldIntent group declarations) })
            AgentTypeDeclarationKindIntent.Union variants
        | value -> fail $"Unsupported type declaration kind '{value}'."

    let typeKindIntent declarations element = typeKindIntentIn Map.empty declarations element

    /// Whether a declaration kind names `reference` anywhere in its field types.
    let kindReferences (reference: TypeId) (kind: AgentTypeDeclarationKindIntent) =
        let rec mentions typeRef =
            match typeRef with
            | TypeRef.Named id -> id = reference
            | TypeRef.Function(arguments, result) -> List.exists mentions arguments || mentions result
            | TypeRef.BuiltinGeneric(_, arguments) -> List.exists mentions arguments
            | _ -> false
        let fields =
            match kind with
            | AgentTypeDeclarationKindIntent.Product fields -> fields
            | AgentTypeDeclarationKindIntent.Union variants -> variants |> List.collect (fun variant -> variant.Fields)
        fields |> List.exists (fun field -> mentions field.Type)

    /// Name-resolved capability operation: types use symbolic TypeRefs and `effect` names an existing effect.
    let capabilityOperationIntent declarations (item: JsonElement): AgentCapabilityOperationIntent =
        ensureFields
            "capability operation"
            [ "displayName"; "parameters"; "resultType"; "failureType"; "effect" ]
            [ "displayName"; "parameters"; "resultType"; "effect" ]
            item
        let failureType =
            match tryProperty "failureType" item with
            | None -> None
            | Some value when value.ValueKind = JsonValueKind.Null -> None
            | Some value -> Some(typeRef declarations value)
        { DisplayName = requiredString "displayName" item
          Parameters =
            array "parameters" item
            |> List.map (fun parameter ->
                ensureFields "capability parameter" [ "displayName"; "type" ] [ "displayName"; "type" ] parameter
                { DisplayName = requiredString "displayName" parameter
                  Type = typeRef declarations (property "type" parameter) })
          ResultType = typeRef declarations (property "resultType" item)
          FailureType = failureType
          EffectId = (resolveEffectAt declarations "effect" item).Id }

    let functionParameters declarations (element: JsonElement) =
        array "parameters" element
        |> List.map (fun item ->
            ensureFields "function parameter" [ "displayName"; "type" ] [ "displayName"; "type" ] item
            let parameter: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
                { DisplayName = requiredString "displayName" item
                  Type = typeRef declarations (property "type" item) }
            parameter)

    let requiredEffects declarations (element: JsonElement) =
        array "requiredEffects" element
        |> List.map (fun item ->
            (resolveEffectSelector declarations item).Id)

    [<RequireQualifiedAccess>]
    type private ParameterContext =
        | NewFunction of displayName: string * AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent list
        | ExistingFunction of SemanticFunction

    /// Resolves a callback, worker or function value. Only a `directCall` naming the function being
    /// created becomes a self call, so a reference naming it has no function to resolve to yet.
    let private resolveReferencedFunction (declarations: AuthoringDeclarations) context (selector: JsonElement) =
        let displayName = if selector.ValueKind = JsonValueKind.String then selector.GetString() else null
        match context with
        | ParameterContext.NewFunction(ownerName, _) when
            ownerName = displayName
            && not (declarations.Functions |> List.exists (fun item -> item.DisplayName = displayName))
            ->
            fail
                $"Function '{displayName}' is the function being created: only a directCall to it is a self call. A callback, worker or functionReference naming it (and mutual recursion through another new function) needs it to exist, so create it with a placeholder body, create the functions it calls back, then give it the real body with simulateReplaceBodyByName."
        | _ -> resolveFunctionSelector declarations selector

    let private parameterRecipe context displayName =
        match context with
        | ParameterContext.NewFunction(_, parameters) ->
            let index, _ =
                parameters
                |> List.indexed
                |> fun items ->
                    exactOne
                        "new function parameter"
                        displayName
                        items
                        (fun (_, parameter) -> parameter.DisplayName)
            ExpressionRecipe.ParameterSlot index
        | ParameterContext.ExistingFunction functionEntity ->
            let parameter =
                exactOne
                    $"parameter on {functionEntity.DisplayName}"
                    displayName
                    functionEntity.Parameters
                    (fun item -> item.DisplayName)
            ExpressionRecipe.Parameter parameter.Id

    let rec private recipe
        (declarations: AuthoringDeclarations)
        (parameterContext: ParameterContext)
        (element: JsonElement)
        : ExpressionRecipe =
        /// Optional callback context: `[{ "parameter": <callback parameter display name>, "value" }]`.
        let namedCallbackContext name (callback: SemanticFunction) =
            match tryProperty name element with
            | None -> []
            | Some _ ->
                array name element
                |> List.map (fun item ->
                    ensureFields "callback context" [ "parameter"; "value" ] [ "parameter"; "value" ] item
                    let parameter =
                        exactOne
                            $"parameter on {callback.DisplayName}"
                            (requiredString "parameter" item)
                            callback.Parameters
                            (fun value -> value.DisplayName)
                    parameter.Id, recipe declarations parameterContext (property "value" item))
        let callbackContext callback = namedCallbackContext "context" callback
        match requiredString "kind" element with
        | "parameter" ->
            ensureFields "parameter recipe" [ "kind"; "name" ] [ "kind"; "name" ] element
            parameterRecipe parameterContext (requiredString "name" element)
        | "patternReference" ->
            match requiredString "slot" element with
            | "value" ->
                ensureFields "pattern-reference recipe" [ "kind"; "scopeDepth"; "slot" ] [ "kind"; "scopeDepth"; "slot" ] element
                ExpressionRecipe.PatternReference
                    { ScopeDepth = requiredInt "scopeDepth" element
                      Slot = RecipePatternSlot.Value }
            | "error" ->
                ensureFields "pattern-reference recipe" [ "kind"; "scopeDepth"; "slot" ] [ "kind"; "scopeDepth"; "slot" ] element
                ExpressionRecipe.PatternReference
                    { ScopeDepth = requiredInt "scopeDepth" element
                      Slot = RecipePatternSlot.Error }
            | "field" ->
                ensureFields
                    "pattern-reference recipe"
                    [ "kind"; "scopeDepth"; "slot"; "type"; "variant"; "field" ]
                    [ "kind"; "scopeDepth"; "slot"; "type"; "variant"; "field" ]
                    element
                let owner = resolveTypeAt declarations "type" element
                let variant = unionVariant owner (requiredString "variant" element)
                let field = variantField variant (requiredString "field" element)
                ExpressionRecipe.PatternReference
                    { ScopeDepth = requiredInt "scopeDepth" element
                      Slot = RecipePatternSlot.Field field.Id }
            | value -> fail $"Unsupported pattern slot '{value}'."
        | "listEmpty" ->
            ensureFields "list-empty recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.ListEmpty(typeRef declarations (property "itemType" element))
        | "setEmpty" ->
            ensureFields "set-empty recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.SetEmpty(typeRef declarations (property "itemType" element))
        | "mapEmpty" ->
            ensureFields "map-empty recipe" [ "kind"; "keyType"; "valueType" ] [ "kind"; "keyType"; "valueType" ] element
            ExpressionRecipe.MapEmpty(typeRef declarations (property "keyType" element), typeRef declarations (property "valueType" element))
        | "listConstruct" ->
            ensureFields "list-construct recipe" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] element
            ExpressionRecipe.ListConstruct(
                typeRef declarations (property "itemType" element),
                array "items" element |> List.map (recipe declarations parameterContext)
            )
        | "listFold" ->
            ensureFields "list-fold recipe" [ "kind"; "source"; "seed"; "function"; "context" ] [ "kind"; "source"; "seed"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.ListFold(
                recipe declarations parameterContext (property "source" element),
                recipe declarations parameterContext (property "seed" element),
                callback.Id,
                callbackContext callback
            )
        | "mapFold" ->
            ensureFields "map-fold recipe" [ "kind"; "map"; "seed"; "function"; "context" ] [ "kind"; "map"; "seed"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.MapFold(
                recipe declarations parameterContext (property "map" element),
                recipe declarations parameterContext (property "seed" element),
                callback.Id,
                callbackContext callback
            )
        | "listEffectFold" ->
            ensureFields "list-effect-fold recipe" [ "kind"; "source"; "seed"; "function"; "context" ] [ "kind"; "source"; "seed"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.ListEffectFold(
                recipe declarations parameterContext (property "source" element),
                recipe declarations parameterContext (property "seed" element),
                callback.Id,
                callbackContext callback
            )
        | "useResource" ->
            ensureFields
                "use-resource recipe"
                [ "kind"; "acquire"; "releaseCapability"; "capability"; "releaseOperation"; "body"; "context" ]
                [ "kind"; "acquire"; "releaseCapability"; "capability"; "releaseOperation"; "body" ]
                element
            let capability = resolveCapabilityAt declarations "capability" element
            let operation = capabilityOperation capability (requiredString "releaseOperation" element)
            let body = resolveReferencedFunction declarations parameterContext (property "body" element)
            ExpressionRecipe.UseResource(
                recipe declarations parameterContext (property "acquire" element),
                recipe declarations parameterContext (property "releaseCapability" element),
                operation.Id,
                body.Id,
                callbackContext body)
        | "parallelAll" ->
            ensureFields "parallel-all recipe" [ "kind"; "source"; "worker"; "context" ] [ "kind"; "source"; "worker" ] element
            let worker = resolveReferencedFunction declarations parameterContext (property "worker" element)
            ExpressionRecipe.ParallelAll(recipe declarations parameterContext (property "source" element), worker.Id, callbackContext worker)
        | "exceptionCatch" ->
            ensureFields "exception-catch recipe" [ "kind"; "body"; "handler"; "bodyContext"; "handlerContext" ] [ "kind"; "body"; "handler" ] element
            let body = resolveReferencedFunction declarations parameterContext (property "body" element)
            let handler = resolveReferencedFunction declarations parameterContext (property "handler" element)
            ExpressionRecipe.ExceptionCatch(body.Id, namedCallbackContext "bodyContext" body, handler.Id, namedCallbackContext "handlerContext" handler)
        | "exceptionRethrow" ->
            ensureFields "exception-rethrow recipe" [ "kind"; "value"; "resultType" ] [ "kind"; "value"; "resultType" ] element
            ExpressionRecipe.ExceptionRethrow(recipe declarations parameterContext (property "value" element), typeRef declarations (property "resultType" element))
        | "cancellationShield" ->
            ensureFields "cancellation-shield recipe" [ "kind"; "body"; "context" ] [ "kind"; "body" ] element
            let body = resolveReferencedFunction declarations parameterContext (property "body" element)
            ExpressionRecipe.CancellationShield(body.Id, callbackContext body)
        | "taskScope" ->
            ensureFields "task-scope recipe" [ "kind"; "body"; "context" ] [ "kind"; "body" ] element
            let body = resolveReferencedFunction declarations parameterContext (property "body" element)
            ExpressionRecipe.TaskScope(body.Id, callbackContext body)
        | "taskStart" ->
            ensureFields "task-start recipe" [ "kind"; "scope"; "worker"; "item"; "context" ] [ "kind"; "scope"; "worker"; "item" ] element
            let worker = resolveReferencedFunction declarations parameterContext (property "worker" element)
            ExpressionRecipe.TaskStart(
                recipe declarations parameterContext (property "scope" element),
                worker.Id,
                callbackContext worker,
                recipe declarations parameterContext (property "item" element))
        | "taskWait" ->
            ensureFields "task-wait recipe" [ "kind"; "tasks"; "mode"; "limit" ] [ "kind"; "tasks"; "mode" ] element
            ExpressionRecipe.TaskWait(
                recipe declarations parameterContext (property "tasks" element),
                WireInput.taskWaitMode element,
                tryProperty "limit" element |> Option.map (recipe declarations parameterContext))
        | "oneShotCreate" ->
            ensureFields "one-shot-create recipe" [ "kind"; "scope"; "valueType" ] [ "kind"; "scope"; "valueType" ] element
            ExpressionRecipe.OneShotCreate(recipe declarations parameterContext (property "scope" element), typeRef declarations (property "valueType" element))
        | "mapFilter" ->
            ensureFields "map-filter recipe" [ "kind"; "map"; "function"; "context" ] [ "kind"; "map"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.MapFilter(recipe declarations parameterContext (property "map" element), callback.Id, callbackContext callback)
        | "optionMap" ->
            ensureFields "option-map recipe" [ "kind"; "option"; "function"; "context" ] [ "kind"; "option"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.OptionMap(recipe declarations parameterContext (property "option" element), callback.Id, callbackContext callback)
        | "optionAndThen" ->
            ensureFields "option-and-then recipe" [ "kind"; "option"; "function"; "context" ] [ "kind"; "option"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.OptionAndThen(recipe declarations parameterContext (property "option" element), callback.Id, callbackContext callback)
        | "resultMap" ->
            ensureFields "result-map recipe" [ "kind"; "result"; "function"; "context" ] [ "kind"; "result"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.ResultMap(recipe declarations parameterContext (property "result" element), callback.Id, callbackContext callback)
        | "resultMapErr" ->
            ensureFields "result-map-err recipe" [ "kind"; "result"; "function"; "context" ] [ "kind"; "result"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.ResultMapErr(recipe declarations parameterContext (property "result" element), callback.Id, callbackContext callback)
        | "resultAndThen" ->
            ensureFields "result-and-then recipe" [ "kind"; "result"; "function"; "context" ] [ "kind"; "result"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.ResultAndThen(recipe declarations parameterContext (property "result" element), callback.Id, callbackContext callback)
        | "mapMapValues" ->
            ensureFields "map-map-values recipe" [ "kind"; "map"; "function"; "context" ] [ "kind"; "map"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.MapMapValues(recipe declarations parameterContext (property "map" element), callback.Id, callbackContext callback)
        | "listZipWith" ->
            ensureFields "list-zip-with recipe" [ "kind"; "left"; "right"; "function"; "context" ] [ "kind"; "left"; "right"; "function" ] element
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            ExpressionRecipe.ListZipWith(
                recipe declarations parameterContext (property "left" element),
                recipe declarations parameterContext (property "right" element),
                callback.Id,
                callbackContext callback
            )
        | "listFlatMap"
        | "listMap"
        | "mapFromList"
        | "listSortBy"
        | "listMinBy"
        | "listFilter"
        | "listAny"
        | "listFindIndex"
        | "listAll" as kind ->
            ensureFields $"{kind} recipe" [ "kind"; "source"; "function"; "context" ] [ "kind"; "source"; "function" ] element
            let source = recipe declarations parameterContext (property "source" element)
            let callback = resolveReferencedFunction declarations parameterContext (property "function" element)
            let context = callbackContext callback
            match kind with
            | "listMap" -> ExpressionRecipe.ListMap(source, callback.Id, context)
            | "listFlatMap" -> ExpressionRecipe.ListFlatMap(source, callback.Id, context)
            | "mapFromList" -> ExpressionRecipe.MapFromList(source, callback.Id, context)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, callback.Id, context)
            | "listSortBy" -> ExpressionRecipe.ListSortBy(source, callback.Id, context)
            | "listMinBy" -> ExpressionRecipe.ListMinBy(source, callback.Id, context)
            | "listAny" -> ExpressionRecipe.ListAny(source, callback.Id, context)
            | "listFindIndex" -> ExpressionRecipe.ListFindIndex(source, callback.Id, context)
            | _ -> ExpressionRecipe.ListAll(source, callback.Id, context)
        | "productConstruct" ->
            ensureFields "product-construct recipe" [ "kind"; "type"; "fields" ] [ "kind"; "type"; "fields" ] element
            let owner = resolveTypeAt declarations "type" element
            let fields =
                array "fields" element
                |> List.map (fun item ->
                    ensureFields "product field recipe" [ "field"; "value" ] [ "field"; "value" ] item
                    let field = productField owner (requiredString "field" item)
                    field.Id, recipe declarations parameterContext (property "value" item))
            ExpressionRecipe.ProductConstruct(owner.Id, fields)
        | "fieldAccess" ->
            ensureFields
                "field-access recipe"
                [ "kind"; "source"; "type"; "field" ]
                [ "kind"; "source"; "type"; "field" ]
                element
            let owner = resolveTypeAt declarations "type" element
            let field = productField owner (requiredString "field" element)
            ExpressionRecipe.FieldAccess(
                recipe declarations parameterContext (property "source" element),
                field.Id
            )
        | "productUpdate" ->
            ensureFields
                "product-update recipe"
                [ "kind"; "type"; "source"; "fields" ]
                [ "kind"; "type"; "source"; "fields" ]
                element
            let owner = resolveTypeAt declarations "type" element
            let fields =
                array "fields" element
                |> List.map (fun item ->
                    ensureFields "product field recipe" [ "field"; "value" ] [ "field"; "value" ] item
                    let field = productField owner (requiredString "field" item)
                    field.Id, recipe declarations parameterContext (property "value" item))
            ExpressionRecipe.ProductUpdate(owner.Id, recipe declarations parameterContext (property "source" element), fields)
        | "unionConstruct" ->
            ensureFields
                "union-construct recipe"
                [ "kind"; "type"; "variant"; "fields" ]
                [ "kind"; "type"; "variant"; "fields" ]
                element
            let owner = resolveTypeAt declarations "type" element
            let variant = unionVariant owner (requiredString "variant" element)
            let fields =
                array "fields" element
                |> List.map (fun item ->
                    ensureFields "union field recipe" [ "field"; "value" ] [ "field"; "value" ] item
                    let field = variantField variant (requiredString "field" item)
                    field.Id, recipe declarations parameterContext (property "value" item))
            ExpressionRecipe.UnionConstruct(variant.Id, fields)
        | "optionNone" ->
            ensureFields "option-none recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.OptionNone(typeRef declarations (property "itemType" element))
        | "resultOk" ->
            ensureFields "result-ok recipe" [ "kind"; "errorType"; "value" ] [ "kind"; "errorType"; "value" ] element
            ExpressionRecipe.ResultOk(
                typeRef declarations (property "errorType" element),
                recipe declarations parameterContext (property "value" element)
            )
        | "resultErr" ->
            ensureFields "result-err recipe" [ "kind"; "okType"; "error" ] [ "kind"; "okType"; "error" ] element
            ExpressionRecipe.ResultErr(
                typeRef declarations (property "okType" element),
                recipe declarations parameterContext (property "error" element)
            )
        | "capabilityConstruct" ->
            ensureFields "Capability construction recipe" [ "kind"; "capability"; "implementations" ] [ "kind"; "capability"; "implementations" ] element
            let capability = resolveCapabilityAt declarations "capability" element
            let implementations = array "implementations" element |> List.map (fun item ->
                ensureFields "Capability implementation" [ "operation"; "function" ] [ "operation"; "function" ] item
                (capabilityOperation capability (requiredString "operation" item)).Id,
                (resolveReferencedFunction declarations parameterContext (property "function" item)).Id)
            ExpressionRecipe.CapabilityConstruct(capability.Id, implementations)
        | "functionReference" ->
            ensureFields "Function reference recipe" [ "kind"; "function" ] [ "kind"; "function" ] element
            ExpressionRecipe.FunctionReference((resolveReferencedFunction declarations parameterContext (property "function" element)).Id)
        | "directCall" ->
            ensureFields
                "direct-call recipe"
                [ "kind"; "function"; "arguments"; "keepUnlisted" ]
                [ "kind"; "function"; "arguments" ]
                element
            let calleeSelector = property "function" element
            let calleeName = if calleeSelector.ValueKind = JsonValueKind.String then calleeSelector.GetString() else null
            // Resolve object guards before classifying recursion; matching the display name
            // alone could turn an explicitly selected same-named other function into self.
            let selectedCallee =
                if calleeSelector.ValueKind = JsonValueKind.String then None
                else Some(resolveFunctionSelector declarations calleeSelector)
            let keepUnlisted = optionalBool "keepUnlisted" false element
            // A call naming the function being created or replaced resolves to SelfCall,
            // with arguments keyed by the owner's parameter slots.
            let selfParameters =
                match parameterContext with
                | ParameterContext.NewFunction(ownerName, parameters) when ownerName = calleeName ->
                    if declarations.Functions |> List.exists (fun item -> item.DisplayName = calleeName) then
                        fail $"Ambiguous function '{calleeName}'."
                    Some(ownerName, parameters |> List.map (fun parameter -> parameter.DisplayName))
                | ParameterContext.ExistingFunction owner when owner.DisplayName = calleeName ->
                    // The owner's ID is known: its own name is a self call even if another
                    // function shares that name.
                    Some(owner.DisplayName, owner.Parameters |> List.map (fun parameter -> parameter.DisplayName))
                | ParameterContext.ExistingFunction owner when selectedCallee |> Option.exists (fun callee -> callee.Id = owner.Id) ->
                    Some(owner.DisplayName, owner.Parameters |> List.map (fun parameter -> parameter.DisplayName))
                | _ -> None
            match selfParameters with
            | Some(ownerName, parameterNames) ->
                let arguments =
                    array "arguments" element
                    |> List.map (fun item ->
                        ensureFields "call argument" [ "parameter"; "value" ] [ "parameter"; "value" ] item
                        let slot, _ =
                            exactOne
                                $"parameter on {ownerName}"
                                (requiredString "parameter" item)
                                (List.indexed parameterNames)
                                snd
                        slot, recipe declarations parameterContext (property "value" item))
                // keepUnlisted passes every parameter the call does not name through unchanged,
                // as if it were written as an argument naming that parameter.
                let kept =
                    if not keepUnlisted then
                        []
                    else
                        let named = arguments |> List.map fst |> Set.ofList
                        parameterNames
                        |> List.indexed
                        |> List.filter (fun (slot, _) -> not (Set.contains slot named))
                        |> List.map (fun (slot, name) -> slot, parameterRecipe parameterContext name)
                ExpressionRecipe.SelfCall(arguments @ kept)
            | None when keepUnlisted ->
                fail $"keepUnlisted applies only to a call of the function being created or replaced; '{calleeName}' is another function."
            | None ->
                let callee = selectedCallee |> Option.defaultWith (fun () -> resolveFunctionSelector declarations calleeSelector)
                let arguments =
                    array "arguments" element
                    |> List.map (fun item ->
                        ensureFields "call argument" [ "parameter"; "value" ] [ "parameter"; "value" ] item
                        let parameter =
                            exactOne
                                $"parameter on {callee.DisplayName}"
                                (requiredString "parameter" item)
                                callee.Parameters
                                (fun value -> value.DisplayName)
                        parameter.Id, recipe declarations parameterContext (property "value" item))
                ExpressionRecipe.DirectCall(callee.Id, arguments)
        | "capabilityInvoke" ->
            ensureFields
                "capability-invoke recipe"
                [ "kind"; "capabilityValue"; "capability"; "operation"; "arguments" ]
                [ "kind"; "capabilityValue"; "capability"; "operation"; "arguments" ]
                element
            let capability = resolveCapabilityAt declarations "capability" element
            let operation = capabilityOperation capability (requiredString "operation" element)
            let arguments =
                array "arguments" element
                |> List.map (fun item ->
                    ensureFields "capability argument" [ "parameter"; "value" ] [ "parameter"; "value" ] item
                    let parameter = capabilityParameter operation (requiredString "parameter" item)
                    parameter.Id, recipe declarations parameterContext (property "value" item))
            ExpressionRecipe.CapabilityInvoke(
                recipe declarations parameterContext (property "capabilityValue" element),
                operation.Id,
                arguments
            )
        | "matchUnion" ->
            ensureFields "match-union recipe" [ "kind"; "type"; "input"; "arms"; "otherwise" ] [ "kind"; "type"; "input"; "arms" ] element
            let owner = resolveTypeAt declarations "type" element
            let arms =
                unionArms "variant" "variants" (fun name -> (unionVariant owner name).Id) (recipe declarations parameterContext) element
            ExpressionRecipe.MatchUnion(
                recipe declarations parameterContext (property "input" element),
                arms
            )
        | value ->
            match WireRecipeCommon.tryReferenceFree (recipe declarations parameterContext) element value with
            | Some decoded -> decoded
            | None -> fail $"Unsupported symbolic expression recipe kind '{value}'."

    /// Bind explicit symbolic type parameters to the existing owner, for both signatures and
    /// expression recipes. Unknown names must not introduce fresh parameters during maintenance.
    let resolveFunctionTypeParameters (fn: SemanticFunction) current =
        let declared = TypeParameters.ofFunction fn
        let substitution =
            TypeParameters.occurring current
            |> List.map (fun (id, name) ->
                let originalId, originalName = exactOne $"type parameter on {fn.DisplayName}" name declared snd
                id, TypeRef.TypeParameter(originalId, originalName))
            |> Map.ofList
        TypeParameters.substitute substitution current

    let typeRefForExistingFunction declarations fn element =
        typeRef declarations element |> resolveFunctionTypeParameters fn

    let recipeForNewFunction declarations displayName parameters element =
        recipe declarations (ParameterContext.NewFunction(displayName, parameters)) element

    let recipeForExistingFunction declarations functionEntity element =
        recipe declarations (ParameterContext.ExistingFunction functionEntity) element
        |> ExpressionRecipeTypes.map (resolveFunctionTypeParameters functionEntity)
