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
        | "stringContains" ->
            ensureFields "string-contains recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringContains(recipe (property "value" element), recipe (property "substring" element))
        | "stringEqual" -> binary "string-equal recipe" ExpressionRecipe.StringEqual
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
            ExpressionRecipe.ListEmpty(typeRef (property "itemType" element))
        | "listConstruct" ->
            ensureFields "list-construct recipe" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] element
            ExpressionRecipe.ListConstruct(typeRef (property "itemType" element), array "items" element |> List.map recipe)
        | "listLength" -> unary "list-length recipe" ExpressionRecipe.ListLength
        | "listAppendItem" ->
            ensureFields "list-append-item recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListAppendItem(recipe (property "list" element), recipe (property "item" element))
        | "listConcat" -> binary "list-concat recipe" ExpressionRecipe.ListConcat
        | "listFold" ->
            ensureFields "list-fold recipe" [ "kind"; "source"; "seed"; "functionId" ] [ "kind"; "source"; "seed"; "functionId" ] element
            ExpressionRecipe.ListFold(recipe (property "source" element), recipe (property "seed" element), id<FunctionTag> "functionId" element)
        | "listMap"
        | "listFilter"
        | "listAny"
        | "listAll" as kind ->
            ensureFields $"{kind} recipe" [ "kind"; "source"; "functionId" ] [ "kind"; "source"; "functionId" ] element
            let source = recipe (property "source" element)
            let functionId = id<FunctionTag> "functionId" element
            match kind with
            | "listMap" -> ExpressionRecipe.ListMap(source, functionId)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, functionId)
            | "listAny" -> ExpressionRecipe.ListAny(source, functionId)
            | _ -> ExpressionRecipe.ListAll(source, functionId)
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
        | "resultErr" ->
            ensureFields "result-err recipe" [ "kind"; "okType"; "error" ] [ "kind"; "okType"; "error" ] element
            ExpressionRecipe.ResultErr(typeRef (property "okType" element), recipe (property "error" element))
        | "directCall" ->
            ensureFields "direct-call recipe" [ "kind"; "functionId"; "arguments" ] [ "kind"; "functionId"; "arguments" ] element
            ExpressionRecipe.DirectCall(id<FunctionTag> "functionId" element, callArguments "arguments")
        | "capabilityInvoke" ->
            ensureFields "capability-invoke recipe" [ "kind"; "capabilityValue"; "operationId"; "arguments" ] [ "kind"; "capabilityValue"; "operationId"; "arguments" ] element
            ExpressionRecipe.CapabilityInvoke(recipe (property "capabilityValue" element), id<CapabilityOperationTag> "operationId" element, capabilityArguments "arguments")
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
