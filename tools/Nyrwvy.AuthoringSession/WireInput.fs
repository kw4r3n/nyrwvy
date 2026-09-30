namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Text.Json
open Nyrwvy.Core

module internal WireInput =
    exception ProtocolInputException of string

    let fail message = raise (ProtocolInputException message)

    /// JSON levels one recipe node can add on the wire: its own object, a child list (`arguments`,
    /// `arms`, `fields`, `context`) and the list entry holding the child recipe, as in
    /// `{"kind":"selfCall","arguments":[{"slot":0,"value":{…}}]}`.
    let recipeJsonLevelsPerNode = 3

    /// Levels above a recipe root in the deepest request envelope, `functionRewrites[i].replacement`.
    let requestEnvelopeJsonLevels = 3

    /// Room at the deepest leaf for an explicit TypeRef (two levels per nested generic).
    /// runPure values (32 composite layers, three levels each) also fit within the bound.
    let leafJsonMargin = 64

    /// A recipe within the node limit can nest as deep as its node count, so every such recipe
    /// parses and is then rejected, if at all, by the recipe bounds with their own diagnostics,
    /// not as invalid JSON: 3 + 3 * 256 + 64 = 835.
    let requestMaxJsonDepth =
        requestEnvelopeJsonLevels + recipeJsonLevelsPerNode * ExpressionRecipeBounds.maxNodes + leafJsonMargin

    let parseDocument (line: string): JsonDocument =
        try JsonDocument.Parse(line, JsonDocumentOptions(MaxDepth = requestMaxJsonDepth))
        with :? JsonException as error -> fail $"Invalid JSON: {error.Message}"

    let private propertyNames (element: JsonElement): Set<string> =
        if element.ValueKind <> JsonValueKind.Object then fail "Expected a JSON object."
        let names = element.EnumerateObject() |> Seq.map (fun item -> item.Name) |> Seq.toList
        let distinct = Set.ofList names
        if names.Length <> distinct.Count then fail "JSON object contains duplicate property names."
        distinct

    let ensureFields (context: string) (allowed: string list) (required: string list) (element: JsonElement): unit =
        let actual = propertyNames element
        let unknown = Set.difference actual (Set.ofList allowed) |> Set.toList |> List.sort
        let missing = Set.difference (Set.ofList required) actual |> Set.toList |> List.sort
        if not (List.isEmpty unknown) then
            let rendered = String.concat ", " unknown
            fail $"{context} contains unsupported properties: {rendered}."
        if not (List.isEmpty missing) then
            let rendered = String.concat ", " missing
            fail $"{context} is missing required properties: {rendered}."

    let property (name: string) (element: JsonElement): JsonElement =
        match element.TryGetProperty(name) with
        | true, value -> value
        | false, _ -> fail $"Missing required property '{name}'."

    let tryProperty (name: string) (element: JsonElement): JsonElement option =
        match element.TryGetProperty(name) with
        | true, value -> Some value
        | false, _ -> None

    let requiredString (name: string) (element: JsonElement): string =
        let value = property name element
        if value.ValueKind <> JsonValueKind.String then fail $"Property '{name}' must be a JSON string."
        value.GetString()

    let taskWaitMode (element: JsonElement): TaskWaitMode =
        match requiredString "mode" element with
        | "any" -> TaskWaitMode.Any
        | "all" -> TaskWaitMode.All
        | value -> fail $"Unsupported task wait mode '{value}'; expected 'any' or 'all'."

    let requiredBool (name: string) (element: JsonElement): bool =
        let value = property name element
        if value.ValueKind <> JsonValueKind.True && value.ValueKind <> JsonValueKind.False then
            fail $"Property '{name}' must be a JSON boolean."
        value.GetBoolean()

    let optionalBool (name: string) (fallback: bool) (element: JsonElement): bool =
        match tryProperty name element with
        | None -> fallback
        | Some value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False -> value.GetBoolean()
        | Some _ -> fail $"Property '{name}' must be a JSON boolean."

    let requiredInt (name: string) (element: JsonElement): int =
        let value = property name element
        match value.TryGetInt32() with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 32-bit integer."

    let requiredInt64 (name: string) (element: JsonElement): int64 =
        let value = property name element
        let parsed =
            match value.ValueKind with
            | JsonValueKind.Number -> value.TryGetInt64()
            | JsonValueKind.String ->
                Int64.TryParse(value.GetString(), Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture)
            | _ -> false, 0L
        match parsed with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 64-bit integer or exact decimal integer string."

    let array (name: string) (element: JsonElement): JsonElement list =
        let value = property name element
        if value.ValueKind <> JsonValueKind.Array then fail $"Property '{name}' must be a JSON array."
        value.EnumerateArray() |> Seq.toList

    let stableId<'Kind> (label: string) (text: string): StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> fail $"{label} is not a valid stable ID: {error}."

    let id<'Kind> (name: string) (element: JsonElement): StableId<'Kind> =
        stableId<'Kind> name (requiredString name element)

    let rec typeRef (element: JsonElement): TypeRef =
        match requiredString "kind" element with
        | "function" ->
            ensureFields "Function type" [ "kind"; "parameters"; "resultType" ] [ "kind"; "parameters"; "resultType" ] element
            TypeRef.Function(array "parameters" element |> List.map (typeRef), typeRef (property "resultType" element))
        | "primitive" ->
            ensureFields "primitive type" [ "kind"; "primitive" ] [ "kind"; "primitive" ] element
            match requiredString "primitive" element with
            | "string" -> TypeRef.Primitive PrimitiveType.String
            | "bool" -> TypeRef.Primitive PrimitiveType.Bool
            | "unit" -> TypeRef.Primitive PrimitiveType.Unit
            | "int64" -> TypeRef.Primitive PrimitiveType.Int64
            | "float64" -> TypeRef.Primitive PrimitiveType.Float64
            | "bits32" -> TypeRef.Primitive PrimitiveType.Bits32
            | value -> fail $"Unsupported primitive type '{value}'."
        | "named" ->
            ensureFields "named type" [ "kind"; "typeId" ] [ "kind"; "typeId" ] element
            TypeRef.Named(id<TypeTag> "typeId" element)
        | "capability" ->
            ensureFields "capability type" [ "kind"; "capabilityId" ] [ "kind"; "capabilityId" ] element
            TypeRef.Capability(id<CapabilityTag> "capabilityId" element)
        | "typeParameter" when (tryProperty "name" element).IsSome ->
            // A request-local name; function creation replaces it with a fresh system-owned identity.
            ensureFields "type parameter" [ "kind"; "name" ] [ "kind"; "name" ] element
            let name = requiredString "name" element
            if System.String.IsNullOrWhiteSpace name then fail "Type parameter names must not be blank."
            TypeRef.TypeParameter(id<TypeParameterTag> "name" element, name)
        | "typeParameter" ->
            ensureFields "type parameter" [ "kind"; "typeParameterId"; "displayName" ] [ "kind"; "typeParameterId"; "displayName" ] element
            TypeRef.TypeParameter(id<TypeParameterTag> "typeParameterId" element, requiredString "displayName" element)
        | "builtinGeneric" ->
            ensureFields "builtin generic type" [ "kind"; "generic"; "arguments" ] [ "kind"; "generic"; "arguments" ] element
            let generic =
                match requiredString "generic" element with
                | "option" -> BuiltinGenericType.Option
                | "result" -> BuiltinGenericType.Result
                | "list" -> BuiltinGenericType.List
                | "task" -> BuiltinGenericType.Task
                | "taskScope" -> BuiltinGenericType.TaskScope
                | value -> fail $"Unsupported builtin generic '{value}'."
            TypeRef.BuiltinGeneric(generic, array "arguments" element |> List.map typeRef)
        | value -> fail $"Unsupported type kind '{value}'."
