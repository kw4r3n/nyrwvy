namespace Nyrwvy.Core

/// Renders types and functions for diagnostics in the vocabulary an author writes: display names
/// in wire `TypeRef` notation (`Option<String>`, `List<Tail>`, `(String, Int64) -> Int64`), never
/// StableIds or F# union dumps. A declaration that cannot be found keeps its ID so the text stays
/// unambiguous.
[<RequireQualifiedAccess>]
module DiagnosticText =
    /// `withTypeParameterIds` appends a type parameter's ID to its display name, for a message that
    /// compares two type parameters of one name.
    let rec private render (snapshot: Snapshot) (withTypeParameterIds: bool) (value: TypeRef): string =
        let render = render snapshot withTypeParameterIds
        let generic name (arguments: TypeRef list) =
            name + "<" + (arguments |> List.map render |> String.concat ", ") + ">"
        match value with
        | TypeRef.Primitive PrimitiveType.String -> "String"
        | TypeRef.Primitive PrimitiveType.Bool -> "Bool"
        | TypeRef.Primitive PrimitiveType.Unit -> "Unit"
        | TypeRef.Primitive PrimitiveType.Int64 -> "Int64"
        | TypeRef.Primitive PrimitiveType.Float64 -> "Float64"
        | TypeRef.Primitive PrimitiveType.Bits32 -> "Bits32"
        | TypeRef.Named typeId ->
            match snapshot.Types |> List.tryFind (fun typeEntity -> typeEntity.Id = typeId) with
            | Some typeEntity -> typeEntity.DisplayName
            | None -> $"type '{StableId.value typeId}'"
        | TypeRef.Capability capabilityId ->
            match snapshot.Capabilities |> List.tryFind (fun capability -> capability.Id = capabilityId) with
            | Some capability -> capability.DisplayName
            | None -> $"capability '{StableId.value capabilityId}'"
        | TypeRef.Function(parameters, result) ->
            "(" + (parameters |> List.map render |> String.concat ", ") + ") -> " + render result
        | TypeRef.TypeParameter(id, displayName) when withTypeParameterIds -> $"{displayName} (type parameter {StableId.value id})"
        | TypeRef.TypeParameter(_, displayName) -> displayName
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> "TaskScope"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) -> generic "Option" arguments
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) -> generic "Result" arguments
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, arguments) -> generic "Map" arguments
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) -> generic "List" arguments
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, arguments) -> generic "Set" arguments
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, arguments) -> generic "OneShot" arguments
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) -> generic "Task" arguments
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) -> generic "TaskScope" arguments

    let typeRef (snapshot: Snapshot) (value: TypeRef): string = render snapshot false value

    /// Two types of a mismatch, rendered so that they read differently: when their display texts
    /// agree although the types differ (two type parameters named `T`, one of another function or
    /// of the request itself), every type parameter carries its ID.
    let typeRefPair (snapshot: Snapshot) (expected: TypeRef) (actual: TypeRef): string * string =
        let expectedText, actualText = typeRef snapshot expected, typeRef snapshot actual
        if expectedText = actualText && expected <> actual then
            render snapshot true expected, render snapshot true actual
        else
            expectedText, actualText

    /// A named type with its ID, for messages that compare two named types.
    let namedType (snapshot: Snapshot) (typeId: TypeId) =
        match snapshot.Types |> List.tryFind (fun typeEntity -> typeEntity.Id = typeId) with
        | Some typeEntity -> StableId.describe typeEntity.DisplayName typeId
        | None -> $"type '{StableId.value typeId}'"

    /// A type with the ID of a top-level named type appended, e.g. `'Tail' (01a0…)`.
    let typeRefWithId (snapshot: Snapshot) (value: TypeRef) =
        match value with
        | TypeRef.Named typeId -> namedType snapshot typeId
        | _ -> typeRef snapshot value

    /// The normal-completion type, or a note that the expression never completes normally.
    let completion (snapshot: Snapshot) (value: GraphCompletion) =
        match value with
        | GraphCompletion.Normal normal -> typeRef snapshot normal
        | GraphCompletion.Aborts -> "no value (the expression always aborts)"

    /// Two completions of a mismatch, with type-parameter IDs when their texts would otherwise agree.
    let completionPair (snapshot: Snapshot) (left: GraphCompletion) (right: GraphCompletion): string * string =
        match left, right with
        | GraphCompletion.Normal leftType, GraphCompletion.Normal rightType -> typeRefPair snapshot leftType rightType
        | _ -> completion snapshot left, completion snapshot right

    /// A function by display name and ID: `'countStep' (01a0…)`.
    let functionName (functionEntity: SemanticFunction) =
        StableId.describe functionEntity.DisplayName functionEntity.Id

    /// A function's declared signature: `(line: String, acc: Int64) -> Int64`.
    let signature (snapshot: Snapshot) (functionEntity: SemanticFunction) =
        let parameters =
            functionEntity.Parameters
            |> List.map (fun parameter -> $"{parameter.DisplayName}: {typeRef snapshot parameter.Type}")
            |> String.concat ", "
        $"({parameters}) -> {typeRef snapshot functionEntity.ResultType}"
