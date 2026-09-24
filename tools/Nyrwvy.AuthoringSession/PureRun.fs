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

/// Bounded T3 execution path: pure scalar/flat-container entrypoints, no host bindings or
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

    let rec private sourceArgument value =
        let listSource name items =
            let arguments = items |> List.map sourceArgument |> String.concat ", "
            $"NyList<{name}>.Create({arguments})"
        let optionSource name item =
            match item with
            | None -> $"NyOption<{name}>.None()"
            | Some value -> $"NyOption<{name}>.Some({sourceArgument value})"
        match value with
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

    let private strings = function
        | PureRunValue.Product(_, fields)
        | PureRunValue.Union(_, _, fields) ->
            fields |> List.choose (fun (_, value) -> match value with PureRunScalar.String text -> Some text | _ -> None)
        | PureRunValue.ResultOk(_, PureRunScalar.String text)
        | PureRunValue.ResultErr(_, PureRunScalar.String text) -> [ text ]
        | PureRunValue.String text -> [ text ]
        | PureRunValue.StringList items -> items
        | PureRunValue.StringOption item -> Option.toList item
        | _ -> []

    let private oversized = function
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

    let private validProduct types = function
        | PureRunValue.Product(typeId, fields) -> validFields (productFields types typeId) fields
        | _ -> true

    let private validUnion types = function
        | PureRunValue.Union(typeId, variantId, fields) ->
            let declared = unionVariants types typeId
                           |> Option.bind (List.tryFind (fun variant -> variant.Id = variantId))
                           |> Option.map (fun variant -> variant.Fields)
            validFields declared fields
        | _ -> true

    let private canonicalValue = function
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

    let private parseResult types resultType (output: string) =
        try
            use document = JsonDocument.Parse(output)
            let value = document.RootElement
            match resultType, value.ValueKind with
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

    let private runVerified
        (session: AuthoringSession)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        : Result<PureRunResult, PureRunError> =
        let declarations = session.Declarations()
        let arguments = arguments |> List.map canonicalValue
        let expected = expected |> Option.map canonicalValue
        let supportedNamed = function
            | TypeRef.Named typeId ->
                (productFields declarations.Types typeId |> Option.isSome)
                || (unionVariants declarations.Types typeId |> Option.isSome)
            | _ -> false
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
        | Some fn when not (supportedNamed fn.ResultType) && not (supportedResult fn.ResultType) && not (List.contains fn.ResultType
            [ TypeRef.Primitive PrimitiveType.String
              TypeRef.Primitive PrimitiveType.Bool
              TypeRef.Primitive PrimitiveType.Int64
              listType PrimitiveType.String
              listType PrimitiveType.Bool
              listType PrimitiveType.Int64
              optionType PrimitiveType.String
              optionType PrimitiveType.Bool
              optionType PrimitiveType.Int64 ]) ->
            Error(diagnostic "RunResultTypeUnsupported" "Only String, Bool, Int64, flat Lists/Options/Results, and scalar-field Products/Unions are supported (at most 256 fields per constructor and 256 variants per Union).")
        | Some fn when expected |> Option.exists (fun value -> valueType value <> fn.ResultType) ->
            Error(diagnostic "RunExpectedTypeMismatch" "The expected result type differs from the declared result type.")
        | Some fn when expected |> Option.exists (validProduct declarations.Types >> not) ->
            Error(diagnostic "RunProductShapeMismatch" "A Product expectation must supply each declared scalar field exactly once with its exact type.")
        | Some fn when expected |> Option.exists (validUnion declarations.Types >> not) ->
            Error(diagnostic "RunUnionShapeMismatch" "A Union expectation must name a variant of the declared scalar-field Union and supply each field exactly once with its exact type.")
        | Some fn when arguments @ Option.toList expected |> List.exists oversized ->
            Error(diagnostic "RunArgumentTooLarge" "Lists are limited to 4096 items; Product/Union constructors to 256 fields; each value to 65536 total String UTF-16 code units.")
        | Some fn when arguments @ Option.toList expected |> List.collect strings |> List.exists (fun text -> isNull text || text.Length > 65536) ->
            Error(diagnostic "RunArgumentTooLarge" "A String argument or expectation is null or exceeds 65536 UTF-16 code units.")
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
                        let program =
                            let prefix =
                                "using System; using System.Globalization; using System.Text; using System.Text.Json; using Nyrwvy.Generated;\n"
                                + $"var result = NyFunctions.{entry}({args});\n"
                            match fn.ResultType with
                            | TypeRef.Named typeId ->
                                let outputFields owner (fields: SemanticField list) =
                                    let guards = fields |> List.choose (fun field ->
                                        if field.Type = TypeRef.Primitive PrimitiveType.String then
                                            let access = owner + "." + CSharpBackendNames.fieldName field.Id
                                            Some($"if ({access} == null) throw new InvalidOperationException(\"Null String field.\"); new UTF8Encoding(false, true).GetByteCount({access});\n")
                                        else None) |> String.concat ""
                                    let values = fields |> List.map (fun field -> owner + "." + CSharpBackendNames.fieldName field.Id) |> String.concat ", "
                                    guards, $"new object[] {{ {values} }}"
                                match productFields declarations.Types typeId with
                                | Some fields ->
                                    let guards, values = outputFields "result" fields
                                    prefix + guards + $"Console.WriteLine(JsonSerializer.Serialize({values}));\n"
                                | None ->
                                    let cases = unionVariants declarations.Types typeId |> Option.get
                                                |> List.mapi (fun index variant ->
                                                    let owner = "selected" + string index
                                                    let name = CSharpBackendNames.variantName variant.Id
                                                    let tag = JsonSerializer.Serialize(StableId.value variant.Id)
                                                    let guards, values = outputFields owner variant.Fields
                                                    $"if (result is {name} {owner}) {{ {guards}Console.WriteLine(JsonSerializer.Serialize(new object[] {{ {tag}, {values} }})); }}\n")
                                                |> String.concat "else "
                                    prefix + cases + "else throw new InvalidOperationException(\"Unknown or null Union variant.\");\n"
                            | TypeRef.Primitive PrimitiveType.String ->
                                prefix
                                + "if (!WellFormed(result)) { Console.Error.WriteLine(\"Invalid UTF-16 result.\"); Environment.ExitCode = 1; }\n"
                                + "else Console.WriteLine(JsonSerializer.Serialize(result));\n"
                                + "static bool WellFormed(string value) { if (value == null) return false; for (int i = 0; i < value.Length; i++) { if (char.IsHighSurrogate(value[i])) { if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false; } else if (char.IsLowSurrogate(value[i])) return false; } return true; }\n"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                let guard field itemType =
                                    if itemType = TypeRef.Primitive PrimitiveType.String then
                                        $"if (result.{field} == null) throw new InvalidOperationException(\"Null String payload.\"); new UTF8Encoding(false, true).GetByteCount(result.{field}); "
                                    else ""
                                prefix
                                + "if (result.IsOk) { " + guard "OkValue" okType
                                + "Console.WriteLine(JsonSerializer.Serialize(new object[] { true, result.OkValue })); }\n"
                                + "else { " + guard "ErrorValue" errorType
                                + "Console.WriteLine(JsonSerializer.Serialize(new object[] { false, result.ErrorValue })); }\n"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive primitive ]) ->
                                let itemType =
                                    match primitive with
                                    | PrimitiveType.String -> "string"
                                    | PrimitiveType.Bool -> "bool"
                                    | _ -> "long"
                                let guard =
                                    if primitive = PrimitiveType.String then
                                        "if (result.HasValue) { if (result.Value == null) throw new InvalidOperationException(\"Null String value.\"); new UTF8Encoding(false, true).GetByteCount(result.Value); }\n"
                                    else ""
                                prefix + guard
                                + $"Console.WriteLine(JsonSerializer.Serialize(result.HasValue ? new {itemType}[] {{ result.Value }} : Array.Empty<{itemType}>()));\n"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Primitive primitive ]) ->
                                let itemType =
                                    match primitive with
                                    | PrimitiveType.String -> "string"
                                    | PrimitiveType.Bool -> "bool"
                                    | _ -> "long"
                                let guard =
                                    if primitive = PrimitiveType.String then
                                        "if (item == null) throw new InvalidOperationException(\"Null String item.\"); new UTF8Encoding(false, true).GetByteCount(item); "
                                    else ""
                                prefix
                                + $"var values = result.Fold(new System.Collections.Generic.List<{itemType}>(), (items, item) => {{ if (items.Count >= 4096) throw new InvalidOperationException(\"List result exceeds 4096 items.\"); {guard}items.Add(item); return items; }});\n"
                                + "Console.WriteLine(JsonSerializer.Serialize(values));\n"
                            | _ -> prefix + "Console.WriteLine(JsonSerializer.Serialize(result));\n"
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
        | Ok() -> runVerified session functionId arguments expected
