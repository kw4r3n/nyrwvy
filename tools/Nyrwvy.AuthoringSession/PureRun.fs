namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core

[<RequireQualifiedAccess>]
type PureRunScalarKind =
    | String
    | Bool
    | Int64

[<RequireQualifiedAccess>]
type PureRunScalar =
    | String of string
    | Bool of bool
    | Int64 of int64

[<RequireQualifiedAccess>]
type PureRunValue =
    | String of string
    | Bool of bool
    | Int64 of int64
    | StringList of string list
    | BoolList of bool list
    | Int64List of int64 list
    | StringOption of string option
    | BoolOption of bool option
    | Int64Option of int64 option
    | ResultOk of errorKind: PureRunScalarKind * value: PureRunScalar
    | ResultErr of okKind: PureRunScalarKind * error: PureRunScalar
    | Product of typeId: TypeId * fields: (FieldId * PureRunScalar) list
    | Union of typeId: TypeId * variantId: VariantId * fields: (FieldId * PureRunScalar) list
    | ListValue of itemType: TypeRef * items: PureRunValue list
    | OptionValue of itemType: TypeRef * item: PureRunValue option

[<RequireQualifiedAccess>]
type PureRunError =
    | Unsupported of string * string
    | SessionFailure of AuthoringSessionError

type PureRunResult =
    { SnapshotId: SnapshotId
      FunctionId: FunctionId
      Actual: PureRunValue
      Expected: PureRunValue option
      Matches: bool option }

/// Bounded T3 execution path: pure typed values and outer List/Option nesting, no host bindings or
/// caller-authored C# source. Runs in a separate, bounded child process.
[<RequireQualifiedAccess>]
module PureRun =
    let private listType primitive = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Primitive primitive ])

    let private optionType primitive = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive primitive ])

    let private scalarValue = function
        | PureRunScalar.String text -> PureRunValue.String text
        | PureRunScalar.Bool value -> PureRunValue.Bool value
        | PureRunScalar.Int64 value -> PureRunValue.Int64 value

    let private scalarType = function
        | PureRunScalarKind.String -> TypeRef.Primitive PrimitiveType.String
        | PureRunScalarKind.Bool -> TypeRef.Primitive PrimitiveType.Bool
        | PureRunScalarKind.Int64 -> TypeRef.Primitive PrimitiveType.Int64

    let private scalarSourceType = function
        | PureRunScalarKind.String -> "string"
        | PureRunScalarKind.Bool -> "bool"
        | PureRunScalarKind.Int64 -> "long"

    let private scalarKind = function
        | PureRunScalar.String _ -> PureRunScalarKind.String
        | PureRunScalar.Bool _ -> PureRunScalarKind.Bool
        | PureRunScalar.Int64 _ -> PureRunScalarKind.Int64

    let private valueType = function
        | PureRunValue.ListValue(itemType, _) -> TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
        | PureRunValue.OptionValue(itemType, _) -> TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
        | PureRunValue.Product(typeId, _)
        | PureRunValue.Union(typeId, _, _) -> TypeRef.Named typeId
        | PureRunValue.ResultOk(errorKind, value) ->
            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ scalarType (scalarKind value); scalarType errorKind ])
        | PureRunValue.ResultErr(okKind, error) ->
            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ scalarType okKind; scalarType (scalarKind error) ])
        | PureRunValue.String _ -> TypeRef.Primitive PrimitiveType.String
        | PureRunValue.Bool _ -> TypeRef.Primitive PrimitiveType.Bool
        | PureRunValue.Int64 _ -> TypeRef.Primitive PrimitiveType.Int64
        | PureRunValue.StringList _ -> listType PrimitiveType.String
        | PureRunValue.BoolList _ -> listType PrimitiveType.Bool
        | PureRunValue.Int64List _ -> listType PrimitiveType.Int64
        | PureRunValue.StringOption _ -> optionType PrimitiveType.String
        | PureRunValue.BoolOption _ -> optionType PrimitiveType.Bool
        | PureRunValue.Int64Option _ -> optionType PrimitiveType.Int64

    let rec private sourceType = function
        | TypeRef.Primitive PrimitiveType.String -> "string"
        | TypeRef.Primitive PrimitiveType.Bool -> "bool"
        | TypeRef.Primitive PrimitiveType.Int64 -> "long"
        | TypeRef.Named typeId -> CSharpBackendNames.typeName typeId
        | TypeRef.BuiltinGeneric(kind, arguments) ->
            let name = match kind with BuiltinGenericType.List -> "NyList" | BuiltinGenericType.Option -> "NyOption" | BuiltinGenericType.Result -> "NyResult"
            name + "<" + (arguments |> List.map sourceType |> String.concat ", ") + ">"
        | _ -> invalidOp "Unsupported runner type passed validation."

    let rec private sourceArgument value =
        let listSource name items =
            let arguments = items |> List.map sourceArgument |> String.concat ", "
            $"NyList<{name}>.Create({arguments})"
        let optionSource name item =
            match item with
            | None -> $"NyOption<{name}>.None()"
            | Some value -> $"NyOption<{name}>.Some({sourceArgument value})"
        match value with
        | PureRunValue.ListValue(itemType, items) -> listSource (sourceType itemType) items
        | PureRunValue.OptionValue(itemType, item) -> optionSource (sourceType itemType) item
        | PureRunValue.Product(_, fields)
        | PureRunValue.Union(_, _, fields) ->
            let name =
                match value with
                | PureRunValue.Product(typeId, _) -> CSharpBackendNames.typeName typeId
                | PureRunValue.Union(_, variantId, _) -> CSharpBackendNames.variantName variantId
                | _ -> invalidOp "Expected a named value."
            let arguments = fields |> List.map (fun (fieldId, item) ->
                CSharpBackendNames.fieldName fieldId + ": " + sourceArgument (scalarValue item)) |> String.concat ", "
            "new " + name + "(" + arguments + ")"
        | PureRunValue.ResultOk(errorKind, item) ->
            let okType, errorType = scalarSourceType (scalarKind item), scalarSourceType errorKind
            let source = sourceArgument (scalarValue item)
            $"NyResult<{okType}, {errorType}>.Ok({source})"
        | PureRunValue.ResultErr(okKind, item) ->
            let okType, errorType = scalarSourceType okKind, scalarSourceType (scalarKind item)
            let source = sourceArgument (scalarValue item)
            $"NyResult<{okType}, {errorType}>.Err({source})"
        | PureRunValue.StringOption item -> optionSource "string" (item |> Option.map PureRunValue.String)
        | PureRunValue.BoolOption item -> optionSource "bool" (item |> Option.map PureRunValue.Bool)
        | PureRunValue.Int64Option item -> optionSource "long" (item |> Option.map PureRunValue.Int64)
        | PureRunValue.StringList items -> listSource "string" (items |> List.map PureRunValue.String)
        | PureRunValue.BoolList items -> listSource "bool" (items |> List.map PureRunValue.Bool)
        | PureRunValue.Int64List items -> listSource "long" (items |> List.map PureRunValue.Int64)
        | PureRunValue.String text ->
            let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            $"Encoding.UTF8.GetString(Convert.FromBase64String(\"{encoded}\"))"
        | PureRunValue.Bool value -> if value then "true" else "false"
        | PureRunValue.Int64 value ->
            let literal = value.ToString(CultureInfo.InvariantCulture)
            $"long.Parse(\"{literal}\", CultureInfo.InvariantCulture)"

    let private diagnostic code message = PureRunError.Unsupported(code, message)

    let private wellFormedUtf16 (text: string) =
        if isNull text then false else
            let mutable index = 0
            let mutable valid = true
            while valid && index < text.Length do
                if Char.IsHighSurrogate(text[index]) then
                    if index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]) then index <- index + 2
                    else valid <- false
                elif Char.IsLowSurrogate(text[index]) then valid <- false
                else index <- index + 1
            valid

    let rec private strings = function
        | PureRunValue.ListValue(_, items) -> items |> List.collect strings
        | PureRunValue.OptionValue(_, item) -> item |> Option.toList |> List.collect strings
        | PureRunValue.Product(_, fields)
        | PureRunValue.Union(_, _, fields) ->
            fields |> List.choose (fun (_, value) -> match value with PureRunScalar.String text -> Some text | _ -> None)
        | PureRunValue.ResultOk(_, PureRunScalar.String text)
        | PureRunValue.ResultErr(_, PureRunScalar.String text) -> [ text ]
        | PureRunValue.String text -> [ text ]
        | PureRunValue.StringList items -> items
        | PureRunValue.StringOption item -> Option.toList item
        | _ -> []

    let rec private oversized = function
        | PureRunValue.ListValue(_, items) -> items.Length > 4096 || (items |> List.exists oversized)
        | PureRunValue.OptionValue(_, item) -> item |> Option.exists oversized
        | PureRunValue.Product(_, fields)
        | PureRunValue.Union(_, _, fields) ->
            fields.Length > 256 || (fields |> List.sumBy (fun (_, value) ->
                match value with
                | PureRunScalar.String text -> if isNull text then 65537L else int64 text.Length
                | _ -> 0L)) > 65536L
        | PureRunValue.StringList items ->
            items.Length > 4096 || (items |> List.sumBy (fun text -> if isNull text then 65537L else int64 text.Length)) > 65536L
        | PureRunValue.BoolList items -> items.Length > 4096
        | PureRunValue.Int64List items -> items.Length > 4096
        | _ -> false

    let private launch (workingDirectory: string) (args: string list) (timeoutMs: int) =
        try
            let info = ProcessStartInfo("dotnet")
            info.WorkingDirectory <- workingDirectory
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            for arg in args do info.ArgumentList.Add(arg)
            use child = Process.Start(info)
            if isNull child then Error(diagnostic "RunStartupFailure" "Could not start dotnet.") else
                let stdout = child.StandardOutput.ReadToEndAsync()
                let stderr = child.StandardError.ReadToEndAsync()
                if not (child.WaitForExit(timeoutMs)) then
                    try child.Kill(true)
                    with
                    | :? InvalidOperationException
                    | :? System.ComponentModel.Win32Exception -> ()
                    if child.WaitForExit(5000) then
                        Error(diagnostic "RunTimeout" "The build or pure function exceeded its time limit.")
                    else
                        Error(diagnostic "RunReapFailure" "The timed-out child has not been confirmed terminated.")
                elif not (System.Threading.Tasks.Task.WaitAll([| stdout :> System.Threading.Tasks.Task; stderr :> System.Threading.Tasks.Task |], 5000)) then
                    Error(diagnostic "RunOutputTimeout" "The process exited but its output streams did not close.")
                else
                    let output = stdout.Result
                    let errors = stderr.Result
                    if child.ExitCode <> 0 then
                        let details = output + "\n" + errors
                        let message = if details.Length > 4096 then details.Substring(0, 4096) else details
                        Error(diagnostic "RunProcessFailure" message)
                    elif output.Length > 1048576 then
                        Error(diagnostic "RunOutputTooLarge" "The process produced more than 1 MiB of output.")
                    else Ok output
        with
        | :? System.ComponentModel.Win32Exception as error ->
            Error(diagnostic "RunStartupFailure" error.Message)
        | :? IOException as error -> Error(diagnostic "RunIoFailure" error.Message)
        | :? UnauthorizedAccessException as error -> Error(diagnostic "RunIoFailure" error.Message)
        | :? AggregateException as error -> Error(diagnostic "RunIoFailure" error.Message)

    let private supportedScalar = function
        | TypeRef.Primitive PrimitiveType.String
        | TypeRef.Primitive PrimitiveType.Bool
        | TypeRef.Primitive PrimitiveType.Int64 -> true
        | _ -> false

    let private supportedResult = function
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            supportedScalar okType && supportedScalar errorType
        | _ -> false

    let private supportedFields (fields: SemanticField list) =
        fields.Length <= 256 && fields |> List.forall (fun field -> supportedScalar field.Type)

    let private unionVariants (types: SemanticType list) typeId =
        types |> List.tryFind (fun item -> item.Id = typeId)
        |> Option.bind (fun item ->
            match item.Kind with
            | SemanticTypeKind.Union variants when not (List.isEmpty variants) && variants.Length <= 256
                                                  && variants |> List.forall (fun variant -> supportedFields variant.Fields) ->
                Some(variants |> List.sortBy (fun variant -> StableId.value variant.Id)
                     |> List.map (fun variant -> { variant with Fields = variant.Fields |> List.sortBy (fun field -> StableId.value field.Id) }))
            | _ -> None)

    let private productFields (types: SemanticType list) typeId =
        types |> List.tryFind (fun item -> item.Id = typeId)
        |> Option.bind (fun item ->
            match item.Kind with
            | SemanticTypeKind.Product fields when supportedFields fields ->
                Some(fields |> List.sortBy (fun field -> StableId.value field.Id))
            | _ -> None)

    let private validFields declared fields =
        let sorted = fields |> List.sortBy (fst >> StableId.value)
        match declared with
        | None -> false
        | Some declared ->
            List.length declared = sorted.Length
            && List.forall2 (fun (field: SemanticField) (fieldId, value) ->
                field.Id = fieldId && field.Type = scalarType (scalarKind value)) declared sorted

    let rec private validProduct types = function
        | PureRunValue.ListValue(_, items) -> items |> List.forall (validProduct types)
        | PureRunValue.OptionValue(_, item) -> item |> Option.forall (validProduct types)
        | PureRunValue.Product(typeId, fields) -> validFields (productFields types typeId) fields
        | _ -> true

    let rec private validUnion types = function
        | PureRunValue.ListValue(_, items) -> items |> List.forall (validUnion types)
        | PureRunValue.OptionValue(_, item) -> item |> Option.forall (validUnion types)
        | PureRunValue.Union(typeId, variantId, fields) ->
            let declared = unionVariants types typeId
                           |> Option.bind (List.tryFind (fun variant -> variant.Id = variantId))
                           |> Option.map (fun variant -> variant.Fields)
            validFields declared fields
        | _ -> true

    let rec private canonicalValue = function
        | PureRunValue.ListValue(itemType, items) -> PureRunValue.ListValue(itemType, List.map canonicalValue items)
        | PureRunValue.OptionValue(itemType, item) -> PureRunValue.OptionValue(itemType, Option.map canonicalValue item)
        | PureRunValue.Union(typeId, variantId, fields) ->
            PureRunValue.Union(typeId, variantId, fields |> List.sortBy (fst >> StableId.value))
        | PureRunValue.Product(typeId, fields) ->
            PureRunValue.Product(typeId, fields |> List.sortBy (fst >> StableId.value))
        | value -> value

    let private parseScalar itemType (item: JsonElement) =
        match itemType with
        | TypeRef.Primitive PrimitiveType.String when item.ValueKind = JsonValueKind.String ->
            let text = item.GetString()
            if wellFormedUtf16 text then Some(PureRunScalar.String text) else None
        | TypeRef.Primitive PrimitiveType.Bool when item.ValueKind = JsonValueKind.True || item.ValueKind = JsonValueKind.False ->
            Some(PureRunScalar.Bool(item.GetBoolean()))
        | TypeRef.Primitive PrimitiveType.Int64 when item.ValueKind = JsonValueKind.Number && fst (item.TryGetInt64()) ->
            Some(PureRunScalar.Int64(item.GetInt64()))
        | _ -> None

    let private parseFields (fields: SemanticField list) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Array || fields.Length <> value.GetArrayLength() then
            Error(diagnostic "RunInvalidResult" "The result fields do not match the declaration.")
        else
            let parsed = fields |> List.mapi (fun index field ->
                parseScalar field.Type value[index] |> Option.map (fun item -> field.Id, item))
            if parsed |> List.exists Option.isNone then
                Error(diagnostic "RunInvalidResult" "A result field has the wrong type or invalid UTF-16.")
            else Ok(parsed |> List.choose id)

    let rec private parseResult types resultType (output: string) =
        try
            use document = JsonDocument.Parse(output)
            let value = document.RootElement
            match resultType, value.ValueKind with
            | TypeRef.BuiltinGeneric((BuiltinGenericType.List | BuiltinGenericType.Option) as kind, [ itemType ]), JsonValueKind.Array
                when not (supportedScalar itemType) ->
                let items = value.EnumerateArray() |> Seq.toList
                if (kind = BuiltinGenericType.Option && items.Length > 1) || items.Length > 4096 then
                    Error(diagnostic "RunInvalidResult" "Invalid container result size.")
                else
                    let parsed = items |> List.fold (fun state item ->
                        state |> Result.bind (fun acc -> parseResult types itemType (item.GetRawText()) |> Result.map (fun value -> value :: acc))) (Ok [])
                    parsed |> Result.map (fun items ->
                        let items = List.rev items
                        if kind = BuiltinGenericType.List then PureRunValue.ListValue(itemType, items)
                        else PureRunValue.OptionValue(itemType, List.tryHead items))
            | TypeRef.Named typeId, JsonValueKind.Array ->
                match productFields types typeId with
                | Some fields -> parseFields fields value |> Result.map (fun parsed -> PureRunValue.Product(typeId, parsed))
                | None ->
                    let variant =
                        if value.GetArrayLength() <> 2 || value[0].ValueKind <> JsonValueKind.String then None
                        else unionVariants types typeId
                             |> Option.bind (List.tryFind (fun variant -> StableId.value variant.Id = value[0].GetString()))
                    match variant with
                    | Some variant -> parseFields variant.Fields value[1]
                                      |> Result.map (fun parsed -> PureRunValue.Union(typeId, variant.Id, parsed))
                    | None -> Error(diagnostic "RunInvalidResult" "The Union result does not match its declaration.")
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]), JsonValueKind.Array
                when supportedResult resultType && value.GetArrayLength() = 2
                     && (value[0].ValueKind = JsonValueKind.True || value[0].ValueKind = JsonValueKind.False) ->
                let isOk = value[0].GetBoolean()
                let item = value[1]
                let selectedType = if isOk then okType else errorType
                let parsed = parseScalar selectedType item
                let kind = function
                    | TypeRef.Primitive PrimitiveType.String -> PureRunScalarKind.String
                    | TypeRef.Primitive PrimitiveType.Bool -> PureRunScalarKind.Bool
                    | _ -> PureRunScalarKind.Int64
                match parsed with
                | Some item when isOk -> Ok(PureRunValue.ResultOk(kind errorType, item))
                | Some item -> Ok(PureRunValue.ResultErr(kind okType, item))
                | None -> Error(diagnostic "RunInvalidResult" "A Result payload has the wrong type or invalid UTF-16.")
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive primitive ]), JsonValueKind.Array
                when value.GetArrayLength() <= 1 ->
                let item = value.EnumerateArray() |> Seq.tryHead
                match primitive, item with
                | PrimitiveType.String, None -> Ok(PureRunValue.StringOption None)
                | PrimitiveType.Bool, None -> Ok(PureRunValue.BoolOption None)
                | PrimitiveType.Int64, None -> Ok(PureRunValue.Int64Option None)
                | PrimitiveType.String, Some item when item.ValueKind = JsonValueKind.String ->
                    let text = item.GetString()
                    if wellFormedUtf16 text then Ok(PureRunValue.StringOption(Some text))
                    else Error(diagnostic "RunInvalidResult" "Invalid UTF-16 Option result.")
                | PrimitiveType.Bool, Some item when item.ValueKind = JsonValueKind.True || item.ValueKind = JsonValueKind.False ->
                    Ok(PureRunValue.BoolOption(Some(item.GetBoolean())))
                | PrimitiveType.Int64, Some item when item.ValueKind = JsonValueKind.Number && fst (item.TryGetInt64()) ->
                    Ok(PureRunValue.Int64Option(Some(item.GetInt64())))
                | _ -> Error(diagnostic "RunInvalidResult" "An Option result has the wrong type.")
            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Primitive primitive ]), JsonValueKind.Array ->
                if value.GetArrayLength() > 4096 then
                    Error(diagnostic "RunOutputTooLarge" "List results may contain at most 4096 elements.")
                else
                    let items = value.EnumerateArray() |> Seq.toList
                    match primitive with
                    | PrimitiveType.String when items |> List.forall (fun item -> item.ValueKind = JsonValueKind.String) ->
                        let texts = items |> List.map (fun item -> item.GetString())
                        if texts |> List.exists (wellFormedUtf16 >> not) then
                            Error(diagnostic "RunInvalidResult" "Invalid UTF-16 List result.")
                        else Ok(PureRunValue.StringList texts)
                    | PrimitiveType.Bool when items |> List.forall (fun item -> item.ValueKind = JsonValueKind.True || item.ValueKind = JsonValueKind.False) ->
                        Ok(PureRunValue.BoolList(items |> List.map (fun item -> item.GetBoolean())))
                    | PrimitiveType.Int64 when items |> List.forall (fun item -> item.ValueKind = JsonValueKind.Number && fst (item.TryGetInt64())) ->
                        Ok(PureRunValue.Int64List(items |> List.map (fun item -> item.GetInt64())))
                    | _ -> Error(diagnostic "RunInvalidResult" "A List result item has the wrong type.")
            | TypeRef.Primitive PrimitiveType.String, JsonValueKind.String ->
                Ok(PureRunValue.String(value.GetString()))
            | TypeRef.Primitive PrimitiveType.Bool, JsonValueKind.True
            | TypeRef.Primitive PrimitiveType.Bool, JsonValueKind.False ->
                Ok(PureRunValue.Bool(value.GetBoolean()))
            | TypeRef.Primitive PrimitiveType.Int64, JsonValueKind.Number ->
                match value.TryGetInt64() with
                | true, number -> Ok(PureRunValue.Int64 number)
                | false, _ -> Error(diagnostic "RunInvalidResult" "The result is not an Int64.")
            | _ -> Error(diagnostic "RunInvalidResult" "The result does not have the declared type.")
        with :? JsonException ->
            Error(diagnostic "RunInvalidResult" "The process did not return one JSON result.")

    let rec private supportedType types depth itemType =
        if depth > 8 then false else
            match itemType with
            | TypeRef.BuiltinGeneric((BuiltinGenericType.List | BuiltinGenericType.Option), [ item ]) ->
                supportedType types (depth + 1) item
            | TypeRef.Named typeId ->
                Option.isSome (productFields types typeId) || Option.isSome (unionVariants types typeId)
            | _ -> supportedScalar itemType || supportedResult itemType

    let rec private validContainers types = function
        | PureRunValue.ListValue(itemType, items) ->
            not (supportedScalar itemType) && supportedType types 1 itemType
            && (items |> List.forall (fun item -> valueType item = itemType && validContainers types item))
        | PureRunValue.OptionValue(itemType, item) ->
            not (supportedScalar itemType) && supportedType types 1 itemType
            && (item |> Option.forall (fun value -> valueType value = itemType && validContainers types value))
        | _ -> true

    let private boundedValue value =
        let mutable remaining = 8192
        let rec visit depth value =
            remaining <- remaining - 1
            if depth > 8 || remaining < 0 then false else
                let count size = remaining <- remaining - size; remaining >= 0
                match value with
                | PureRunValue.ListValue(_, items) -> items |> List.forall (visit (depth + 1))
                | PureRunValue.OptionValue(_, item) -> item |> Option.forall (visit (depth + 1))
                | PureRunValue.StringList items -> count items.Length
                | PureRunValue.BoolList items -> count items.Length
                | PureRunValue.Int64List items -> count items.Length
                | PureRunValue.StringOption item -> count (if Option.isSome item then 1 else 0)
                | PureRunValue.BoolOption item -> count (if Option.isSome item then 1 else 0)
                | PureRunValue.Int64Option item -> count (if Option.isSome item then 1 else 0)
                | PureRunValue.ResultOk _
                | PureRunValue.ResultErr _ -> count 1
                | PureRunValue.Product(_, fields)
                | PureRunValue.Union(_, _, fields) -> count fields.Length
                | _ -> true
        visit 0 value

    let private runVerified
        (session: AuthoringSession)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        : Result<PureRunResult, PureRunError> =
        let declarations = session.Declarations()
        let arguments = arguments |> List.map canonicalValue
        let expected = expected |> Option.map canonicalValue
        match declarations.Functions |> List.tryFind (fun item -> item.Id = functionId) with
        | None -> Error(diagnostic "RunFunctionNotFound" "The entry function does not exist.")
        | Some fn when not (List.isEmpty fn.RequiredEffects) ->
            Error(diagnostic "RunEffectsUnsupported" "This entrypoint requires effects; the pure runner has no bindings.")
        | Some fn when fn.Parameters.Length <> arguments.Length ->
            Error(diagnostic "RunArgumentCountMismatch" "The number of arguments differs from the function signature.")
        | Some fn when List.exists2 (fun (parameter: SemanticParameter) value -> parameter.Type <> valueType value) fn.Parameters arguments ->
            Error(diagnostic "RunArgumentTypeMismatch" "An argument has the wrong type.")
        | Some fn when arguments |> List.exists (validProduct declarations.Types >> not) ->
            Error(diagnostic "RunProductShapeMismatch" "Product arguments must supply each declared scalar field exactly once with its exact type (at most 256 fields).")
        | Some fn when arguments |> List.exists (validUnion declarations.Types >> not) ->
            Error(diagnostic "RunUnionShapeMismatch" "Union arguments must name a variant of the declared scalar-field Union and supply each field exactly once with its exact type.")
        | Some fn when arguments |> List.exists (validContainers declarations.Types >> not) ->
            Error(diagnostic "RunContainerTypeMismatch" "Nested container item types must be supported and match every payload; scalar items use the flat value representation.")
        | Some fn when not (supportedType declarations.Types 0 fn.ResultType) ->
            Error(diagnostic "RunResultTypeUnsupported" "Supported values are scalars, scalar Results/Products/Unions and up to eight outer List/Option layers.")
        | Some fn when expected |> Option.exists (fun value -> valueType value <> fn.ResultType) ->
            Error(diagnostic "RunExpectedTypeMismatch" "The expected result type differs from the declared result type.")
        | Some fn when expected |> Option.exists (validProduct declarations.Types >> not) ->
            Error(diagnostic "RunProductShapeMismatch" "A Product expectation must supply each declared scalar field exactly once with its exact type.")
        | Some fn when expected |> Option.exists (validUnion declarations.Types >> not) ->
            Error(diagnostic "RunUnionShapeMismatch" "A Union expectation must name a variant of the declared scalar-field Union and supply each field exactly once with its exact type.")
        | Some fn when expected |> Option.exists (validContainers declarations.Types >> not) ->
            Error(diagnostic "RunContainerTypeMismatch" "A nested expectation has an unsupported or mismatched item type.")
        | Some fn when arguments @ Option.toList expected |> List.exists oversized ->
            Error(diagnostic "RunArgumentTooLarge" "Lists are limited to 4096 items; Product/Union constructors to 256 fields; each value to 65536 total String UTF-16 code units.")
        | Some fn when arguments @ Option.toList expected |> List.collect strings |> List.exists (fun text -> isNull text || text.Length > 65536) ->
            Error(diagnostic "RunArgumentTooLarge" "A String argument or expectation is null or exceeds 65536 UTF-16 code units.")
        | Some fn when arguments @ Option.toList expected |> List.exists (fun value ->
            strings value |> List.sumBy (fun text -> int64 text.Length) |> fun length -> length > 65536L) ->
            Error(diagnostic "RunArgumentTooLarge" "Each value is limited to 65536 aggregate String UTF-16 code units.")
        | Some fn when arguments @ Option.toList expected |> List.collect strings |> List.exists (wellFormedUtf16 >> not) ->
            Error(diagnostic "RunInvalidString" "String arguments and expectations must contain well-formed UTF-16.")
        | Some fn ->
            match session.EmitCSharp CSharpBackendProfile.empty, session.Status() with
            | Error error, _
            | _, Error error -> Error(PureRunError.SessionFailure error)
            | Ok artifact, Ok status ->
                let root = Path.Combine(Path.GetTempPath(), "nyrwvy-pure-run-" + Guid.NewGuid().ToString("N"))
                try
                    try
                        Directory.CreateDirectory(root) |> ignore
                        File.WriteAllText(Path.Combine(root, "Generated.csproj"),
                            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
                        File.WriteAllText(Path.Combine(root, "Generated.cs"), artifact.Source)
                        let args = arguments |> List.map sourceArgument |> String.concat ", "
                        let entry = CSharpBackendNames.functionName fn.Id
                        let mutable nextName = 0
                        let fresh () =
                            nextName <- nextName + 1
                            "v" + string nextName
                        let rec encode itemType access =
                            let array items = "new object[] { " + String.concat ", " items + " }"
                            let fields owner (fields: SemanticField list) =
                                fields |> List.map (fun field -> encode field.Type (owner + "." + CSharpBackendNames.fieldName field.Id)) |> array
                            let expression =
                                match itemType with
                                | TypeRef.Primitive PrimitiveType.String -> "CheckedString(" + access + ")"
                                | TypeRef.Primitive _ -> "(object)" + access
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) ->
                                    let name, accumulator = fresh (), fresh ()
                                    let child = encode item name
                                    $"{access}.Fold(new System.Collections.Generic.List<object>(), ({accumulator}, {name}) => {{ if ({accumulator}.Count >= 4096) throw new InvalidOperationException(\"List result exceeds 4096 items.\"); {accumulator}.Add({child}); return {accumulator}; }})"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
                                    let child = encode item (access + ".Value")
                                    $"({access}.HasValue ? {array [ child ]} : Array.Empty<object>())"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                    let ok = array [ "true"; encode okType (access + ".OkValue") ]
                                    let error = array [ "false"; encode errorType (access + ".ErrorValue") ]
                                    $"({access}.IsOk ? {ok} : {error})"
                                | TypeRef.Named typeId ->
                                    match productFields declarations.Types typeId with
                                    | Some declared -> fields access declared
                                    | None ->
                                        let cases = unionVariants declarations.Types typeId |> Option.get
                                        let fallback = "throw new InvalidOperationException(\"Unknown or null Union variant.\")"
                                        List.foldBack (fun (variant: SemanticVariant) rest ->
                                            let name = fresh ()
                                            let variantName = CSharpBackendNames.variantName variant.Id
                                            let tag = JsonSerializer.Serialize(StableId.value variant.Id)
                                            let payload = array [ tag; fields name variant.Fields ]
                                            $"({access} is {variantName} {name} ? {payload} : {rest})") cases fallback
                                | _ -> invalidOp "Unsupported output type passed validation."
                            "Count(" + expression + ")"
                        let encoded = encode fn.ResultType "result"
                        let program =
                            "using System; using System.Globalization; using System.Text; using System.Text.Json; using Nyrwvy.Generated;\n"
                            + $"var result = NyFunctions.{entry}({args});\n"
                            + "int remaining = 8192;\n"
                            + (if encoded.Contains("CheckedString(") then "long textRemaining = 65536;\n" else "")
                            + $"Console.WriteLine(JsonSerializer.Serialize({encoded}));\n"
                            + "object Count(object value) { if (--remaining < 0) throw new InvalidOperationException(\"Result exceeds 8192 value nodes.\"); return value; }\n"
                            + (if encoded.Contains("CheckedString(") then
                                "string CheckedString(string value) { if (value == null) throw new InvalidOperationException(\"Null String result.\"); textRemaining -= value.Length; if (textRemaining < 0) throw new InvalidOperationException(\"Result exceeds 65536 String code units.\"); new UTF8Encoding(false, true).GetByteCount(value); return value; }\n"
                               else "")
                        File.WriteAllText(Path.Combine(root, "Program.cs"), program)
                        launch root [ "build"; "Generated.csproj"; "--nologo"; "-v:q"; "--ignore-failed-sources" ] 30000
                        |> Result.bind (fun _ ->
                            launch root [ Path.Combine(root, "bin", "Debug", "net10.0", "Generated.dll") ] 5000)
                        |> Result.bind (parseResult declarations.Types fn.ResultType)
                        |> Result.map (fun actual ->
                            { SnapshotId = status.SnapshotId
                              FunctionId = fn.Id
                              Actual = actual
                              Expected = expected
                              Matches = expected |> Option.map ((=) actual) })
                    with
                    | :? IOException as error -> Error(diagnostic "RunIoFailure" error.Message)
                    | :? UnauthorizedAccessException as error -> Error(diagnostic "RunIoFailure" error.Message)
                finally
                    if Directory.Exists(root) then
                        try Directory.Delete(root, true)
                        with
                        | :? IOException
                        | :? UnauthorizedAccessException -> ()

    let run
        (session: AuthoringSession)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        : Result<PureRunResult, PureRunError> =
        match session.VerifyPersistedSource() with
        | Error error -> Error(PureRunError.SessionFailure error)
        | Ok() when arguments @ Option.toList expected |> List.exists (boundedValue >> not) ->
            Error(diagnostic "RunArgumentTooLarge" "Values are limited to eight nested container layers and 8192 total value nodes.")
        | Ok() -> runVerified session functionId arguments expected
