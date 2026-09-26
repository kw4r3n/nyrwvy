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

    let private resolveCapability (declarations: AuthoringDeclarations) displayName =
        exactOne "capability" displayName declarations.Capabilities (fun item -> item.DisplayName)

    let private productField (owner: SemanticType) displayName =
        match owner.Kind with
        | SemanticTypeKind.Product fields ->
            exactOne $"field on {owner.DisplayName}" displayName fields (fun item -> item.DisplayName)
        | SemanticTypeKind.Union _ ->
            fail $"Type '{owner.DisplayName}' is not a product type."

    let private unionVariant (owner: SemanticType) displayName =
        match owner.Kind with
        | SemanticTypeKind.Union variants ->
            exactOne $"variant on {owner.DisplayName}" displayName variants (fun item -> item.DisplayName)
        | SemanticTypeKind.Product _ ->
            fail $"Type '{owner.DisplayName}' is not a union type."

    let private variantField (variant: SemanticVariant) displayName =
        exactOne $"field on variant {variant.DisplayName}" displayName variant.Fields (fun item -> item.DisplayName)

    let private capabilityOperation (capability: SemanticCapability) displayName =
        exactOne $"operation on {capability.DisplayName}" displayName capability.Operations (fun item -> item.DisplayName)

    let private capabilityParameter (operation: SemanticCapabilityOperation) displayName =
        exactOne $"parameter on operation {operation.DisplayName}" displayName operation.Parameters (fun item -> item.DisplayName)

    let rec typeRef (declarations: AuthoringDeclarations) (element: JsonElement): TypeRef =
        match requiredString "kind" element with
        | "function" ->
            ensureFields "Function type" [ "kind"; "parameters"; "resultType" ] [ "kind"; "parameters"; "resultType" ] element
            TypeRef.Function(array "parameters" element |> List.map (typeRef declarations), typeRef declarations (property "resultType" element))
        | "primitive" ->
            ensureFields "primitive type" [ "kind"; "primitive" ] [ "kind"; "primitive" ] element
            match requiredString "primitive" element with
            | "string" -> TypeRef.Primitive PrimitiveType.String
            | "bool" -> TypeRef.Primitive PrimitiveType.Bool
            | "unit" -> TypeRef.Primitive PrimitiveType.Unit
            | "int64" -> TypeRef.Primitive PrimitiveType.Int64
            | "float64" -> TypeRef.Primitive PrimitiveType.Float64
            | value -> fail $"Unsupported primitive type '{value}'."
        | "named" ->
            ensureFields "named type" [ "kind"; "name" ] [ "kind"; "name" ] element
            TypeRef.Named((resolveType declarations (requiredString "name" element)).Id)
        | "capability" ->
            ensureFields "capability type" [ "kind"; "name" ] [ "kind"; "name" ] element
            TypeRef.Capability((resolveCapability declarations (requiredString "name" element)).Id)
        | "builtinGeneric" ->
            ensureFields "builtin generic type" [ "kind"; "generic"; "arguments" ] [ "kind"; "generic"; "arguments" ] element
            let generic =
                match requiredString "generic" element with
                | "option" -> BuiltinGenericType.Option
                | "result" -> BuiltinGenericType.Result
                | "list" -> BuiltinGenericType.List
                | value -> fail $"Unsupported builtin generic '{value}'."
            TypeRef.BuiltinGeneric(generic, array "arguments" element |> List.map (typeRef declarations))
        | "typeParameter" ->
            // A request-local type parameter name; function creation allocates its identity.
            ensureFields "type parameter" [ "kind"; "name" ] [ "kind"; "name" ] element
            let name = requiredString "name" element
            if System.String.IsNullOrWhiteSpace name then fail "Type parameter names must not be blank."
            TypeRef.TypeParameter(id<TypeParameterTag> "name" element, name)
        | value -> fail $"Unsupported symbolic type kind '{value}'."

    let private typeFieldIntent declarations (item: JsonElement): AgentTypeFieldIntent =
        ensureFields "type field" [ "displayName"; "type" ] [ "displayName"; "type" ] item
        { DisplayName = requiredString "displayName" item
          Type = typeRef declarations (property "type" item) }

    let typeKindIntent declarations (element: JsonElement): AgentTypeDeclarationKindIntent =
        match requiredString "kind" element with
        | "product" ->
            ensureFields "product type kind" [ "kind"; "fields" ] [ "kind"; "fields" ] element
            AgentTypeDeclarationKindIntent.Product(array "fields" element |> List.map (typeFieldIntent declarations))
        | "union" ->
            ensureFields "union type kind" [ "kind"; "variants" ] [ "kind"; "variants" ] element
            let variants =
                array "variants" element
                |> List.map (fun item ->
                    ensureFields "union variant" [ "displayName"; "fields" ] [ "displayName"; "fields" ] item
                    { DisplayName = requiredString "displayName" item
                      Fields = array "fields" item |> List.map (typeFieldIntent declarations) })
            AgentTypeDeclarationKindIntent.Union variants
        | value -> fail $"Unsupported type declaration kind '{value}'."

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
            if item.ValueKind <> JsonValueKind.String then
                fail "requiredEffects entries must be effect display-name strings."
            (resolveEffect declarations (item.GetString())).Id)

    [<RequireQualifiedAccess>]
    type private ParameterContext =
        | NewFunction of AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent list
        | ExistingFunction of SemanticFunction

    let private parameterRecipe context displayName =
        match context with
        | ParameterContext.NewFunction parameters ->
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
        let unary context constructor =
            ensureFields context [ "kind"; "value" ] [ "kind"; "value" ] element
            constructor(recipe declarations parameterContext (property "value" element))

        let binary context constructor =
            ensureFields context [ "kind"; "left"; "right" ] [ "kind"; "left"; "right" ] element
            constructor(
                recipe declarations parameterContext (property "left" element),
                recipe declarations parameterContext (property "right" element)
            )

        /// Optional callback context: `[{ "parameter": <callback parameter display name>, "value" }]`.
        let callbackContext (callback: SemanticFunction) =
            match tryProperty "context" element with
            | None -> []
            | Some _ ->
                array "context" element
                |> List.map (fun item ->
                    ensureFields "callback context" [ "parameter"; "value" ] [ "parameter"; "value" ] item
                    let parameter =
                        exactOne
                            $"parameter on {callback.DisplayName}"
                            (requiredString "parameter" item)
                            callback.Parameters
                            (fun value -> value.DisplayName)
                    parameter.Id, recipe declarations parameterContext (property "value" item))
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
                let owner = resolveType declarations (requiredString "type" element)
                let variant = unionVariant owner (requiredString "variant" element)
                let field = variantField variant (requiredString "field" element)
                ExpressionRecipe.PatternReference
                    { ScopeDepth = requiredInt "scopeDepth" element
                      Slot = RecipePatternSlot.Field field.Id }
            | value -> fail $"Unsupported pattern slot '{value}'."
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
        | "stringContains" ->
            ensureFields "string-contains recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringContains(
                recipe declarations parameterContext (property "value" element),
                recipe declarations parameterContext (property "substring" element)
            )
        | "stringEqual" -> binary "string-equal recipe" ExpressionRecipe.StringEqual
        | "stringConcat" -> binary "string-concat recipe" ExpressionRecipe.StringConcat
        | "int64WrappingAdd" -> binary "int64-wrapping-add recipe" ExpressionRecipe.Int64WrappingAdd
        | "int64WrappingSubtract" -> binary "int64-wrapping-subtract recipe" ExpressionRecipe.Int64WrappingSubtract
        | "int64WrappingMultiply" -> binary "int64-wrapping-multiply recipe" ExpressionRecipe.Int64WrappingMultiply
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
            ExpressionRecipe.ListEmpty(typeRef declarations (property "itemType" element))
        | "listConstruct" ->
            ensureFields "list-construct recipe" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] element
            ExpressionRecipe.ListConstruct(
                typeRef declarations (property "itemType" element),
                array "items" element |> List.map (recipe declarations parameterContext)
            )
        | "listLength" -> unary "list-length recipe" ExpressionRecipe.ListLength
        | "listAppendItem" ->
            ensureFields "list-append-item recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListAppendItem(
                recipe declarations parameterContext (property "list" element),
                recipe declarations parameterContext (property "item" element)
            )
        | "listConcat" -> binary "list-concat recipe" ExpressionRecipe.ListConcat
        | "listFold" ->
            ensureFields "list-fold recipe" [ "kind"; "source"; "seed"; "function"; "context" ] [ "kind"; "source"; "seed"; "function" ] element
            let callback = resolveFunction declarations (requiredString "function" element)
            ExpressionRecipe.ListFold(
                recipe declarations parameterContext (property "source" element),
                recipe declarations parameterContext (property "seed" element),
                callback.Id,
                callbackContext callback
            )
        | "listEffectFold" ->
            ensureFields "list-effect-fold recipe" [ "kind"; "source"; "seed"; "function"; "context" ] [ "kind"; "source"; "seed"; "function" ] element
            let callback = resolveFunction declarations (requiredString "function" element)
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
            let capability = resolveCapability declarations (requiredString "capability" element)
            let operation = capabilityOperation capability (requiredString "releaseOperation" element)
            let body = resolveFunction declarations (requiredString "body" element)
            ExpressionRecipe.UseResource(
                recipe declarations parameterContext (property "acquire" element),
                recipe declarations parameterContext (property "releaseCapability" element),
                operation.Id,
                body.Id,
                callbackContext body)
        | "parallelAll" ->
            ensureFields "parallel-all recipe" [ "kind"; "source"; "worker"; "context" ] [ "kind"; "source"; "worker" ] element
            let worker = resolveFunction declarations (requiredString "worker" element)
            ExpressionRecipe.ParallelAll(recipe declarations parameterContext (property "source" element), worker.Id, callbackContext worker)
        | "cancellationRequested" ->
            ensureFields "cancellation-requested recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.CancellationRequested
        | "listMap"
        | "listFilter"
        | "listAny"
        | "listAll" as kind ->
            ensureFields $"{kind} recipe" [ "kind"; "source"; "function"; "context" ] [ "kind"; "source"; "function" ] element
            let source = recipe declarations parameterContext (property "source" element)
            let callback = resolveFunction declarations (requiredString "function" element)
            let context = callbackContext callback
            match kind with
            | "listMap" -> ExpressionRecipe.ListMap(source, callback.Id, context)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, callback.Id, context)
            | "listAny" -> ExpressionRecipe.ListAny(source, callback.Id, context)
            | _ -> ExpressionRecipe.ListAll(source, callback.Id, context)
        | "productConstruct" ->
            ensureFields "product-construct recipe" [ "kind"; "type"; "fields" ] [ "kind"; "type"; "fields" ] element
            let owner = resolveType declarations (requiredString "type" element)
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
            let owner = resolveType declarations (requiredString "type" element)
            let field = productField owner (requiredString "field" element)
            ExpressionRecipe.FieldAccess(
                recipe declarations parameterContext (property "source" element),
                field.Id
            )
        | "unionConstruct" ->
            ensureFields
                "union-construct recipe"
                [ "kind"; "type"; "variant"; "fields" ]
                [ "kind"; "type"; "variant"; "fields" ]
                element
            let owner = resolveType declarations (requiredString "type" element)
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
        | "optionSome" -> unary "option-some recipe" ExpressionRecipe.OptionSome
        | "resultOk" ->
            ensureFields "result-ok recipe" [ "kind"; "errorType"; "value" ] [ "kind"; "errorType"; "value" ] element
            ExpressionRecipe.ResultOk(
                typeRef declarations (property "errorType" element),
                recipe declarations parameterContext (property "value" element)
            )
        | "abort" ->
            ensureFields "abort recipe" [ "kind"; "error" ] [ "kind"; "error" ] element
            ExpressionRecipe.Abort(recipe declarations parameterContext (property "error" element))
        | "resultErr" ->
            ensureFields "result-err recipe" [ "kind"; "okType"; "error" ] [ "kind"; "okType"; "error" ] element
            ExpressionRecipe.ResultErr(
                typeRef declarations (property "okType" element),
                recipe declarations parameterContext (property "error" element)
            )
        | "capabilityConstruct" ->
            ensureFields "Capability construction recipe" [ "kind"; "capability"; "implementations" ] [ "kind"; "capability"; "implementations" ] element
            let capability = resolveCapability declarations (requiredString "capability" element)
            let implementations = array "implementations" element |> List.map (fun item ->
                ensureFields "Capability implementation" [ "operation"; "function" ] [ "operation"; "function" ] item
                (capabilityOperation capability (requiredString "operation" item)).Id,
                (resolveFunction declarations (requiredString "function" item)).Id)
            ExpressionRecipe.CapabilityConstruct(capability.Id, implementations)
        | "functionReference" ->
            ensureFields "Function reference recipe" [ "kind"; "function" ] [ "kind"; "function" ] element
            ExpressionRecipe.FunctionReference((resolveFunction declarations (requiredString "function" element)).Id)
        | "functionInvoke" ->
            ensureFields "Function invocation recipe" [ "kind"; "functionValue"; "arguments" ] [ "kind"; "functionValue"; "arguments" ] element
            ExpressionRecipe.FunctionInvoke(recipe declarations parameterContext (property "functionValue" element), array "arguments" element |> List.map (recipe declarations parameterContext))
        | "directCall" ->
            ensureFields
                "direct-call recipe"
                [ "kind"; "function"; "arguments" ]
                [ "kind"; "function"; "arguments" ]
                element
            let callee = resolveFunction declarations (requiredString "function" element)
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
            let capability = resolveCapability declarations (requiredString "capability" element)
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
        | "conditional" ->
            ensureFields
                "conditional recipe"
                [ "kind"; "condition"; "whenTrue"; "whenFalse" ]
                [ "kind"; "condition"; "whenTrue"; "whenFalse" ]
                element
            ExpressionRecipe.Conditional(
                recipe declarations parameterContext (property "condition" element),
                recipe declarations parameterContext (property "whenTrue" element),
                recipe declarations parameterContext (property "whenFalse" element)
            )
        | "matchUnion" ->
            ensureFields "match-union recipe" [ "kind"; "type"; "input"; "arms" ] [ "kind"; "type"; "input"; "arms" ] element
            let owner = resolveType declarations (requiredString "type" element)
            let arms =
                array "arms" element
                |> List.map (fun arm ->
                    ensureFields "union arm" [ "variant"; "body" ] [ "variant"; "body" ] arm
                    let variant = unionVariant owner (requiredString "variant" arm)
                    variant.Id, recipe declarations parameterContext (property "body" arm))
            ExpressionRecipe.MatchUnion(
                recipe declarations parameterContext (property "input" element),
                arms
            )
        | "bind" ->
            ensureFields "bind recipe" [ "kind"; "value"; "body" ] [ "kind"; "value"; "body" ] element
            ExpressionRecipe.Bind(
                recipe declarations parameterContext (property "value" element),
                recipe declarations parameterContext (property "body" element)
            )
        | "matchOption" ->
            ensureFields
                "match-option recipe"
                [ "kind"; "input"; "someBody"; "noneBody" ]
                [ "kind"; "input"; "someBody"; "noneBody" ]
                element
            ExpressionRecipe.MatchOption(
                recipe declarations parameterContext (property "input" element),
                recipe declarations parameterContext (property "someBody" element),
                recipe declarations parameterContext (property "noneBody" element)
            )
        | "matchResult" ->
            ensureFields
                "match-result recipe"
                [ "kind"; "input"; "okBody"; "errBody" ]
                [ "kind"; "input"; "okBody"; "errBody" ]
                element
            ExpressionRecipe.MatchResult(
                recipe declarations parameterContext (property "input" element),
                recipe declarations parameterContext (property "okBody" element),
                recipe declarations parameterContext (property "errBody" element)
            )
        | value -> fail $"Unsupported symbolic expression recipe kind '{value}'."

    let recipeForNewFunction declarations parameters element =
        recipe declarations (ParameterContext.NewFunction parameters) element

    let recipeForExistingFunction declarations functionEntity element =
        recipe declarations (ParameterContext.ExistingFunction functionEntity) element
