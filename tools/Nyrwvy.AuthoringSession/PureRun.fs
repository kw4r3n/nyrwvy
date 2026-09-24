namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core

[<RequireQualifiedAccess>]
type PureRunValue =
    | String of string
    | Bool of bool
    | Int64 of int64

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

/// First bounded T3 execution path: pure scalar entrypoints, no host bindings or
/// caller-authored C# source. Runs in a separate, bounded child process.
[<RequireQualifiedAccess>]
module PureRun =
    let private valueType = function
        | PureRunValue.String _ -> TypeRef.Primitive PrimitiveType.String
        | PureRunValue.Bool _ -> TypeRef.Primitive PrimitiveType.Bool
        | PureRunValue.Int64 _ -> TypeRef.Primitive PrimitiveType.Int64

    let private sourceArgument = function
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

    let private parseResult resultType (output: string) =
        try
            use document = JsonDocument.Parse(output)
            let value = document.RootElement
            match resultType, value.ValueKind with
            | TypeRef.Primitive PrimitiveType.String, JsonValueKind.String ->
                Ok(PureRunValue.String(value.GetString()))
            | TypeRef.Primitive PrimitiveType.Bool, JsonValueKind.True
            | TypeRef.Primitive PrimitiveType.Bool, JsonValueKind.False ->
                Ok(PureRunValue.Bool(value.GetBoolean()))
            | TypeRef.Primitive PrimitiveType.Int64, JsonValueKind.Number ->
                match value.TryGetInt64() with
                | true, number -> Ok(PureRunValue.Int64 number)
                | false, _ -> Error(diagnostic "RunInvalidResult" "The result is not an Int64.")
            | _ -> Error(diagnostic "RunInvalidResult" "The result does not have the declared scalar type.")
        with :? JsonException ->
            Error(diagnostic "RunInvalidResult" "The process did not return one JSON scalar result.")

    let private runVerified
        (session: AuthoringSession)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        : Result<PureRunResult, PureRunError> =
        let declarations = session.Declarations()
        match declarations.Functions |> List.tryFind (fun item -> item.Id = functionId) with
        | None -> Error(diagnostic "RunFunctionNotFound" "The entry function does not exist.")
        | Some fn when not (List.isEmpty fn.RequiredEffects) ->
            Error(diagnostic "RunEffectsUnsupported" "This entrypoint requires effects; the pure runner has no bindings.")
        | Some fn when fn.Parameters.Length <> arguments.Length ->
            Error(diagnostic "RunArgumentCountMismatch" "The number of arguments differs from the function signature.")
        | Some fn when List.exists2 (fun (parameter: SemanticParameter) value -> parameter.Type <> valueType value) fn.Parameters arguments ->
            Error(diagnostic "RunArgumentTypeMismatch" "An argument has the wrong scalar type.")
        | Some fn when not (List.contains fn.ResultType
            [ TypeRef.Primitive PrimitiveType.String
              TypeRef.Primitive PrimitiveType.Bool
              TypeRef.Primitive PrimitiveType.Int64 ]) ->
            Error(diagnostic "RunResultTypeUnsupported" "Only String, Bool and Int64 results are supported.")
        | Some fn when expected |> Option.exists (fun value -> valueType value <> fn.ResultType) ->
            Error(diagnostic "RunExpectedTypeMismatch" "The expected result type differs from the declared result type.")
        | Some fn when arguments |> List.exists (function | PureRunValue.String text -> isNull text || text.Length > 65536 | _ -> false)
            || expected |> Option.exists (function | PureRunValue.String text -> isNull text || text.Length > 65536 | _ -> false) ->
            Error(diagnostic "RunArgumentTooLarge" "A String argument or expectation is null or exceeds 65536 UTF-16 code units.")
        | Some fn when arguments |> List.exists (function | PureRunValue.String text -> not (wellFormedUtf16 text) | _ -> false)
            || expected |> Option.exists (function | PureRunValue.String text -> not (wellFormedUtf16 text) | _ -> false) ->
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
                            | TypeRef.Primitive PrimitiveType.String ->
                                prefix
                                + "if (!WellFormed(result)) { Console.Error.WriteLine(\"Invalid UTF-16 result.\"); Environment.ExitCode = 1; }\n"
                                + "else Console.WriteLine(JsonSerializer.Serialize(result));\n"
                                + "static bool WellFormed(string value) { if (value == null) return false; for (int i = 0; i < value.Length; i++) { if (char.IsHighSurrogate(value[i])) { if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false; } else if (char.IsLowSurrogate(value[i])) return false; } return true; }\n"
                            | _ -> prefix + "Console.WriteLine(JsonSerializer.Serialize(result));\n"
                        File.WriteAllText(Path.Combine(root, "Program.cs"), program)
                        launch root [ "build"; "Generated.csproj"; "--nologo"; "-v:q"; "--ignore-failed-sources" ] 30000
                        |> Result.bind (fun _ ->
                            launch root [ Path.Combine(root, "bin", "Debug", "net10.0", "Generated.dll") ] 5000)
                        |> Result.bind (parseResult fn.ResultType)
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
