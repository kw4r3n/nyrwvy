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
    | Unit
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
    | Product of typeId: TypeId * fields: (FieldId * PureRunValue) list
    | Union of typeId: TypeId * variantId: VariantId * fields: (FieldId * PureRunValue) list
    | ListValue of itemType: TypeRef * items: PureRunValue list
    | OptionValue of itemType: TypeRef * item: PureRunValue option
    /// A Result with at least one non-scalar side. Results whose Ok and Err types are both scalars
    /// always use the flat ResultOk/ResultErr representation.
    | ResultValue of okType: TypeRef * errorType: TypeRef * isOk: bool * payload: PureRunValue
    /// A scripted stand-in for a capability, accepted only as a top-level argument. Each operation
    /// selects an ordered response queue or a queue keyed by complete argument values.
    /// Missing matches and exhausted queues fail explicitly when called.
    | FakeCapability of capabilityId: CapabilityId * operations: PureRunFakeOperation list
    /// An explicitly registered host implementation of the capability interface, compiled from the
    /// request's host sources. `implementation` is a C# type name with a parameterless constructor.
    /// `supportsConcurrentCalls` asserts the shared-state contract across all operations and aliases.
    | HostCapability of capabilityId: CapabilityId * implementation: string * supportsConcurrentCalls: bool

and PureRunFakeOperation =
    { OperationId: CapabilityOperationId
      Responses: PureRunFakeResponse list
      /// None retains the ordered script. Some selects independent queues by complete argument values.
      Cases: PureRunFakeCase list option }

and PureRunFakeCase =
    { Arguments: (CapabilityParameterId * PureRunValue) list
      Responses: PureRunFakeResponse list }

and [<RequireQualifiedAccess>] PureRunFakeResponse =
    | Return of PureRunValue
    /// Raises a host exception from the operation, as a failing external implementation would.
    | Throw of message: string
    /// Blocks like a long-running external process until the calling ParallelAll worker's or
    /// task's cancellation is requested, then returns the value.
    | AwaitCancellation of PureRunValue
    /// Answers with the inner response after `delayMillis` of virtual time. Delays run on runPure's
    /// virtual clock and are not interrupted by cancellation.
    | Delayed of delayMillis: int64 * response: PureRunFakeResponse

[<RequireQualifiedAccess>]
module PureRunFakeResponse =
    /// The response without its delays.
    let rec immediate response =
        match response with
        | PureRunFakeResponse.Delayed(_, inner) -> immediate inner
        | other -> other

[<RequireQualifiedAccess>]
module PureRunFakeOperation =
    let responses (operation: PureRunFakeOperation) =
        operation.Responses @ (operation.Cases |> Option.defaultValue [] |> List.collect (fun item -> item.Responses))

    let arguments (operation: PureRunFakeOperation) =
        operation.Cases |> Option.defaultValue [] |> List.collect (fun item -> item.Arguments |> List.map snd)

/// One call the program made to a fake capability, with its arguments in canonical ID order.
type PureRunCall =
    { ParameterId: ParameterId
      OperationId: CapabilityOperationId
      Arguments: (CapabilityParameterId * PureRunValue) list }

/// One step from a value to a component, named by semantic identity rather than generated symbols.
[<RequireQualifiedAccess>]
type PureRunPathStep =
    | Field of typeId: TypeId * fieldId: FieldId
    | Item of index: int
    | SomeValue
    | OkValue
    | ErrValue

/// The first difference between an expectation and the actual result, in canonical field order.
type PureRunMismatch =
    { Path: PureRunPathStep list
      Expected: PureRunValue
      Actual: PureRunValue }

/// A generated stack frame mapped back to the semantic declaration it implements.
[<RequireQualifiedAccess>]
type PureRunFrame =
    | Function of FunctionId
    | CapabilityOperation of CapabilityOperationId

/// A host exception or crash raised while the program itself ran, as opposed to a runner limit.
type PureRunRuntimeFailure =
    { SnapshotId: SnapshotId
      FunctionId: FunctionId
      ExceptionType: string
      Message: string
      /// Innermost frame first; frames without a semantic declaration are omitted.
      Frames: PureRunFrame list
      /// True when a fake operation was called after its scripted responses ran out.
      BindingExhausted: bool
      Calls: PureRunCall list }

[<RequireQualifiedAccess>]
type PureRunError =
    | Unsupported of string * string
    | SessionFailure of AuthoringSessionError
    | RuntimeFailure of PureRunRuntimeFailure

type PureRunResult =
    { SnapshotId: SnapshotId
      FunctionId: FunctionId
      Actual: PureRunValue
      Expected: PureRunValue option
      Matches: bool option
      Mismatch: PureRunMismatch option
      Calls: PureRunCall list }

/// Bounded T3 execution path: typed values, including nesting through List/Option layers,
/// Product/Union fields and Result payloads. Capabilities use scripted fakes or explicitly registered
/// host sources. Runs in a separate, bounded child process.
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
        | PureRunValue.Unit -> TypeRef.Primitive PrimitiveType.Unit
        | PureRunValue.FakeCapability(capabilityId, _)
        | PureRunValue.HostCapability(capabilityId, _, _) -> TypeRef.Capability capabilityId
        | PureRunValue.ResultValue(okType, errorType, _, _) -> TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])
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
        | TypeRef.Primitive PrimitiveType.Unit -> "NyUnit"
        | TypeRef.Primitive PrimitiveType.String -> "string"
        | TypeRef.Primitive PrimitiveType.Bool -> "bool"
        | TypeRef.Primitive PrimitiveType.Int64 -> "long"
        | TypeRef.Named typeId -> CSharpBackendNames.typeName typeId
        | TypeRef.BuiltinGeneric(kind, arguments) ->
            let name = match kind with BuiltinGenericType.List -> "NyList" | BuiltinGenericType.Option -> "NyOption" | BuiltinGenericType.Result -> "NyResult" | BuiltinGenericType.Task | BuiltinGenericType.TaskScope -> invalidOp "Task values are not run values."
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
        | PureRunValue.Unit -> "NyUnit.Value"
        | PureRunValue.FakeCapability _
        | PureRunValue.HostCapability _ -> invalidOp "Capability bindings are rendered by argument position."
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
                CSharpBackendNames.fieldName fieldId + ": " + sourceArgument item) |> String.concat ", "
            "new " + name + "(" + arguments + ")"
        | PureRunValue.ResultValue(okType, errorType, isOk, item) ->
            let constructor = if isOk then "Ok" else "Err"
            $"NyResult<{sourceType okType}, {sourceType errorType}>.{constructor}({sourceArgument item})"
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

    /// Compare the supported value structure, never CLR container reference identity.
    /// Generation is bounded by the already validated key, including recursive named types.
    let private sourceMatches prefix expected actual =
        let mutable next = 0
        let fresh () = next <- next + 1; prefix + "Key" + string next
        let rec compare value actual =
            let fields owner values =
                values |> List.map (fun (id, value) -> compare value (owner + "." + CSharpBackendNames.fieldName id))
                |> String.concat " && "
            let list values =
                let state, item = fresh (), fresh ()
                let arms = values |> List.mapi (fun i value -> string i + " => " + compare value item) |> String.concat ", "
                let cases = if List.isEmpty values then "_ => false" else arms + ", _ => false"
                $"({actual} != null && {actual}.Length == {values.Length}L && {actual}.Fold((Index: 0, Equal: true), ({state}, {item}) => ({state}.Index + 1, {state}.Equal && ({state}.Index switch {{ {cases} }}))).Equal)"
            let option value =
                match value with
                | None -> $"(!{actual}.HasValue)"
                | Some item ->
                    let selected = compare item (actual + ".Value")
                    $"({actual}.HasValue && {selected})"
            let result isOk payload =
                let selected = if isOk then ".OkValue" else ".ErrorValue"
                let flag = if isOk then "" else "!"
                $"({flag}{actual}.IsOk && {compare payload (actual + selected)})"
            match value with
            | PureRunValue.Unit -> $"System.Collections.Generic.EqualityComparer<NyUnit>.Default.Equals({actual}, NyUnit.Value)"
            | PureRunValue.String _ -> $"string.Equals({actual}, {sourceArgument value}, StringComparison.Ordinal)"
            | PureRunValue.Bool _ | PureRunValue.Int64 _ -> $"({actual} == {sourceArgument value})"
            | PureRunValue.StringList items -> list (List.map PureRunValue.String items)
            | PureRunValue.BoolList items -> list (List.map PureRunValue.Bool items)
            | PureRunValue.Int64List items -> list (List.map PureRunValue.Int64 items)
            | PureRunValue.ListValue(_, items) -> list items
            | PureRunValue.StringOption item -> option (Option.map PureRunValue.String item)
            | PureRunValue.BoolOption item -> option (Option.map PureRunValue.Bool item)
            | PureRunValue.Int64Option item -> option (Option.map PureRunValue.Int64 item)
            | PureRunValue.OptionValue(_, item) -> option item
            | PureRunValue.ResultOk(_, payload) -> result true (scalarValue payload)
            | PureRunValue.ResultErr(_, payload) -> result false (scalarValue payload)
            | PureRunValue.ResultValue(_, _, isOk, payload) -> result isOk payload
            | PureRunValue.Product(_, values) ->
                let body = fields actual values
                if List.isEmpty values then $"({actual} != null)" else $"({actual} != null && {body})"
            | PureRunValue.Union(_, variant, values) ->
                let name = fresh ()
                let head = actual + " is " + CSharpBackendNames.variantName variant + " " + name
                if List.isEmpty values then "(" + head + ")" else "(" + head + " && " + fields name values + ")"
            | PureRunValue.FakeCapability _ | PureRunValue.HostCapability _ -> invalidOp "Capability values cannot be fake keys."
        compare expected actual

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

    let private children = function
        | PureRunValue.ListValue(_, items) -> items
        | PureRunValue.OptionValue(_, item) -> Option.toList item
        | PureRunValue.Product(_, fields)
        | PureRunValue.Union(_, _, fields) -> fields |> List.map snd
        | PureRunValue.ResultValue(_, _, _, payload) -> [ payload ]
        | PureRunValue.FakeCapability(_, operations) ->
            operations |> List.collect (fun operation ->
                PureRunFakeOperation.arguments operation @
                (PureRunFakeOperation.responses operation |> List.map PureRunFakeResponse.immediate |> List.choose (function
                    | PureRunFakeResponse.Return value
                    | PureRunFakeResponse.AwaitCancellation value -> Some value
                    | PureRunFakeResponse.Throw _
                    | PureRunFakeResponse.Delayed _ -> None)))
        | _ -> []

    let rec private strings value =
        match value with
        | PureRunValue.ResultOk(_, PureRunScalar.String text)
        | PureRunValue.ResultErr(_, PureRunScalar.String text) -> [ text ]
        | PureRunValue.String text -> [ text ]
        | PureRunValue.StringList items -> items
        | PureRunValue.StringOption item -> Option.toList item
        | PureRunValue.FakeCapability(_, operations) ->
            (operations |> List.collect (fun operation ->
                PureRunFakeOperation.responses operation |> List.choose (PureRunFakeResponse.immediate >> function PureRunFakeResponse.Throw message -> Some message | _ -> None)))
            @ (children value |> List.collect strings)
        | _ -> children value |> List.collect strings

    let rec private oversized value =
        let local =
            match value with
            | PureRunValue.ListValue(_, items) -> items.Length > 4096
            | PureRunValue.Product(_, fields)
            | PureRunValue.Union(_, _, fields) -> fields.Length > 256
            | PureRunValue.FakeCapability(_, operations) ->
                operations.Length > 256 || operations |> List.exists (fun operation -> (PureRunFakeOperation.responses operation).Length > 256 || (operation.Cases |> Option.exists (fun cases -> cases.Length > 256 || cases |> List.exists (fun item -> item.Arguments.Length > 256))))
            | PureRunValue.StringList items ->
                items.Length > 4096 || (items |> List.sumBy (fun text -> if isNull text then 65537L else int64 text.Length)) > 65536L
            | PureRunValue.BoolList items -> items.Length > 4096
            | PureRunValue.Int64List items -> items.Length > 4096
            | _ -> false
        local || (children value |> List.exists oversized)

    /// Runs dotnet and returns its exit code with bounded output; timeouts and I/O failures are errors.
    let private launchRaw (workingDirectory: string) (args: string list) (timeoutMs: int) =
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
                    if output.Length > 1048576 then
                        Error(diagnostic "RunOutputTooLarge" "The process produced more than 1 MiB of output.")
                    else Ok(child.ExitCode, output, stderr.Result)
        with
        | :? System.ComponentModel.Win32Exception as error ->
            Error(diagnostic "RunStartupFailure" error.Message)
        | :? IOException as error -> Error(diagnostic "RunIoFailure" error.Message)
        | :? UnauthorizedAccessException as error -> Error(diagnostic "RunIoFailure" error.Message)
        | :? AggregateException as error -> Error(diagnostic "RunIoFailure" error.Message)

    let private processFailure (output: string) (errors: string) =
        let details = output + "\n" + errors
        diagnostic "RunProcessFailure" (if details.Length > 4096 then details.Substring(0, 4096) else details)

    let private launch workingDirectory args timeoutMs =
        launchRaw workingDirectory args timeoutMs
        |> Result.bind (fun (exitCode, output, errors) ->
            if exitCode <> 0 then Error(processFailure output errors) else Ok output)

    /// Maps generated symbol names back to semantic identities using the emitted symbol manifest.
    let private frameResolver (symbols: CSharpSymbolManifest) =
        let functions = symbols.Functions |> List.map (fun symbol -> symbol.Name, PureRunFrame.Function symbol.Id)
        let operations = symbols.CapabilityOperations |> List.map (fun symbol -> symbol.Name, PureRunFrame.CapabilityOperation symbol.Id)
        let known = Map.ofList (functions @ operations)
        fun (names: string seq) ->
            names |> Seq.choose (fun name -> Map.tryFind name known) |> Seq.truncate 256 |> Seq.toList

    let private stackOverflowFrames =
        System.Text.RegularExpressions.Regex(@"\b(?:NyF|Op)_[0-9A-F]+\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant)

    let rec private firstDifference (path: PureRunPathStep list) expected actual =
        let here () = Some { Path = List.rev path; Expected = expected; Actual = actual }
        let fields step (left: (FieldId * PureRunValue) list) (right: (FieldId * PureRunValue) list) =
            if List.map fst left <> List.map fst right then here ()
            else
                List.zip left right
                |> List.tryPick (fun ((fieldId, left), (_, right)) ->
                    if left = right then None else firstDifference (step fieldId :: path) left right)
                |> Option.orElse (here ())
        let items = function
            | PureRunValue.ListValue(_, items) -> Some items
            | PureRunValue.StringList items -> Some(items |> List.map PureRunValue.String)
            | PureRunValue.BoolList items -> Some(items |> List.map PureRunValue.Bool)
            | PureRunValue.Int64List items -> Some(items |> List.map PureRunValue.Int64)
            | _ -> None
        let payload = function
            | PureRunValue.OptionValue(_, item) -> Some item
            | PureRunValue.StringOption item -> Some(item |> Option.map PureRunValue.String)
            | PureRunValue.BoolOption item -> Some(item |> Option.map PureRunValue.Bool)
            | PureRunValue.Int64Option item -> Some(item |> Option.map PureRunValue.Int64)
            | _ -> None
        let result = function
            | PureRunValue.ResultValue(_, _, isOk, item) -> Some(isOk, item)
            | PureRunValue.ResultOk(_, item) -> Some(true, scalarValue item)
            | PureRunValue.ResultErr(_, item) -> Some(false, scalarValue item)
            | _ -> None
        if expected = actual then None
        elif valueType expected <> valueType actual then here ()
        else
            match expected, actual with
            | PureRunValue.Product(typeId, left), PureRunValue.Product(_, right) ->
                fields (fun fieldId -> PureRunPathStep.Field(typeId, fieldId)) left right
            | PureRunValue.Union(typeId, leftVariant, left), PureRunValue.Union(_, rightVariant, right) when leftVariant = rightVariant ->
                fields (fun fieldId -> PureRunPathStep.Field(typeId, fieldId)) left right
            | _ ->
                match items expected, items actual, payload expected, payload actual, result expected, result actual with
                | Some left, Some right, _, _, _, _ ->
                    List.zip (List.truncate right.Length left) (List.truncate left.Length right)
                    |> List.indexed
                    |> List.tryPick (fun (index, (left, right)) ->
                        if left = right then None else firstDifference (PureRunPathStep.Item index :: path) left right)
                    |> Option.orElse (here ())
                | _, _, Some(Some left), Some(Some right), _, _ -> firstDifference (PureRunPathStep.SomeValue :: path) left right
                | _, _, _, _, Some(leftOk, left), Some(rightOk, right) when leftOk = rightOk ->
                    firstDifference ((if leftOk then PureRunPathStep.OkValue else PureRunPathStep.ErrValue) :: path) left right
                | _ -> here ()

    /// Locates the first difference between an expectation and an actual value, if any.
    let mismatch expected actual = firstDifference [] expected actual

    let private supportedScalar = function
        | TypeRef.Primitive PrimitiveType.String
        | TypeRef.Primitive PrimitiveType.Bool
        | TypeRef.Primitive PrimitiveType.Int64 -> true
        | _ -> false

    /// Unit is a leaf but has no flat container form, so containers of Unit use the nested form.
    let private supportedLeaf itemType = supportedScalar itemType || itemType = TypeRef.Primitive PrimitiveType.Unit

    let private productFields (types: SemanticType list) typeId =
        types |> List.tryFind (fun item -> item.Id = typeId)
        |> Option.bind (fun item ->
            match item.Kind with
            | SemanticTypeKind.Product fields -> Some(fields |> List.sortBy (fun field -> StableId.value field.Id))
            | _ -> None)

    let private unionVariants (types: SemanticType list) typeId =
        types |> List.tryFind (fun item -> item.Id = typeId)
        |> Option.bind (fun item ->
            match item.Kind with
            | SemanticTypeKind.Union variants ->
                Some(variants |> List.sortBy (fun variant -> StableId.value variant.Id)
                     |> List.map (fun variant -> { variant with Fields = variant.Fields |> List.sortBy (fun field -> StableId.value field.Id) }))
            | _ -> None)

    /// Maximum expanded size of a supported type, counting every type node reached through
    /// containers, Result arguments and declared fields. It bounds generated runner code.
    let private maxTypeNodes = 131072L

    /// Maximum composite layers (List, Option, Result, Product, Union) along any path of one value.
    /// Only values of recursive named types can reach it; other types are bounded by typeSupport.
    let maxValueLayers = 32

    /// Returns a memoized support check. Scalars are leaves; any acyclic path from the checked type may
    /// pass through at most eight composite layers (List, Option, Result, Product, Union), counting from
    /// the given depth. A reference back to a named type on the current path is a recursive type: it is
    /// supported because the runner encodes each named type once, and its values are bounded by
    /// maxValueLayers instead.
    let private typeSupport (types: SemanticType list) =
        let memo = System.Collections.Generic.Dictionary<TypeRef * int, int64 option>(HashIdentity.Structural)
        let visiting = System.Collections.Generic.HashSet<TypeId>()
        let rec cost depth itemType =
            match itemType with
            | _ when supportedLeaf itemType -> Some 1L
            | TypeRef.Named typeId when visiting.Contains typeId -> Some 1L
            | _ when depth > 7 -> None
            | _ ->
                match memo.TryGetValue((itemType, depth)) with
                | true, known -> known
                | _ ->
                    let total (items: TypeRef list) =
                        items |> List.fold (fun state item ->
                            state |> Option.bind (fun sum ->
                                cost (depth + 1) item |> Option.map (fun size -> min (maxTypeNodes + 1L) (sum + size)))) (Some 1L)
                    let computed =
                        match itemType with
                        | TypeRef.BuiltinGeneric((BuiltinGenericType.List | BuiltinGenericType.Option), [ item ]) -> total [ item ]
                        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) -> total [ okType; errorType ]
                        | TypeRef.Named typeId when visiting.Add typeId ->
                            try
                                match productFields types typeId, unionVariants types typeId with
                                | Some fields, _ when fields.Length <= 256 -> total (fields |> List.map (fun field -> field.Type))
                                | None, Some variants when not (List.isEmpty variants) && variants.Length <= 256
                                                           && variants |> List.forall (fun variant -> variant.Fields.Length <= 256) ->
                                    total (variants |> List.collect (fun variant -> variant.Fields |> List.map (fun field -> field.Type)))
                                | _ -> None
                            finally visiting.Remove typeId |> ignore
                        | _ -> None
                    memo[(itemType, depth)] <- computed
                    computed
        fun depth itemType ->
            match cost depth itemType with
            | Some size -> size <= maxTypeNodes
            | None -> false

    let private validFields (declared: SemanticField list option) fields =
        let sorted = fields |> List.sortBy (fst >> StableId.value)
        match declared with
        | None -> false
        | Some declared ->
            List.length declared = sorted.Length
            && List.forall2 (fun (field: SemanticField) (fieldId, value) ->
                field.Id = fieldId && field.Type = valueType value) declared sorted

    let rec private validProduct types value =
        match value with
        | PureRunValue.Product(typeId, fields) when not (validFields (productFields types typeId) fields) -> false
        | _ -> children value |> List.forall (validProduct types)

    let rec private validUnion types value =
        match value with
        | PureRunValue.Union(typeId, variantId, fields) ->
            let declared = unionVariants types typeId
                           |> Option.bind (List.tryFind (fun variant -> variant.Id = variantId))
                           |> Option.map (fun variant -> variant.Fields)
            validFields declared fields && (children value |> List.forall (validUnion types))
        | _ -> children value |> List.forall (validUnion types)

    let rec private canonicalValue = function
        | PureRunValue.ListValue(itemType, items) -> PureRunValue.ListValue(itemType, List.map canonicalValue items)
        | PureRunValue.OptionValue(itemType, item) -> PureRunValue.OptionValue(itemType, Option.map canonicalValue item)
        | PureRunValue.Union(typeId, variantId, fields) ->
            PureRunValue.Union(typeId, variantId, fields |> List.map (fun (fieldId, item) -> fieldId, canonicalValue item)
                                                  |> List.sortBy (fst >> StableId.value))
        | PureRunValue.Product(typeId, fields) ->
            PureRunValue.Product(typeId, fields |> List.map (fun (fieldId, item) -> fieldId, canonicalValue item)
                                         |> List.sortBy (fst >> StableId.value))
        | PureRunValue.ResultValue(okType, errorType, isOk, payload) ->
            PureRunValue.ResultValue(okType, errorType, isOk, canonicalValue payload)
        | PureRunValue.FakeCapability(capabilityId, operations) ->
            let rec response = function
                | PureRunFakeResponse.Delayed(delay, inner) -> PureRunFakeResponse.Delayed(delay, response inner)
                | PureRunFakeResponse.Return value -> PureRunFakeResponse.Return(canonicalValue value)
                | PureRunFakeResponse.AwaitCancellation value -> PureRunFakeResponse.AwaitCancellation(canonicalValue value)
                | throw -> throw
            PureRunValue.FakeCapability(capabilityId,
                operations |> List.map (fun operation ->
                    { operation with
                        Responses = List.map response operation.Responses
                        Cases = operation.Cases |> Option.map (List.map (fun item ->
                            { Arguments = item.Arguments |> List.map (fun (id, value) -> id, canonicalValue value) |> List.sortBy (fst >> StableId.value)
                              Responses = item.Responses |> List.map response })) })
                |> List.sortBy (fun operation -> StableId.value operation.OperationId))
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

    let rec private parseFields types (fields: SemanticField list) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Array || fields.Length <> value.GetArrayLength() then
            Error(diagnostic "RunInvalidResult" "The result fields do not match the declaration.")
        else
            fields |> List.mapi (fun index field -> field, value[index])
            |> List.fold (fun state (field, item) ->
                state |> Result.bind (fun parsed ->
                    parseElement types field.Type item |> Result.map (fun item -> (field.Id, item) :: parsed))) (Ok [])
            |> Result.map List.rev

    and private parseElement types resultType (value: JsonElement) : Result<PureRunValue, PureRunError> =
            match resultType, value.ValueKind with
            | TypeRef.BuiltinGeneric((BuiltinGenericType.List | BuiltinGenericType.Option) as kind, [ itemType ]), JsonValueKind.Array
                when not (supportedScalar itemType) ->
                let items = value.EnumerateArray() |> Seq.toList
                if (kind = BuiltinGenericType.Option && items.Length > 1) || items.Length > 4096 then
                    Error(diagnostic "RunInvalidResult" "Invalid container result size.")
                else
                    let parsed = items |> List.fold (fun state item ->
                        state |> Result.bind (fun acc -> parseElement types itemType item |> Result.map (fun value -> value :: acc))) (Ok [])
                    parsed |> Result.map (fun items ->
                        let items = List.rev items
                        if kind = BuiltinGenericType.List then PureRunValue.ListValue(itemType, items)
                        else PureRunValue.OptionValue(itemType, List.tryHead items))
            | TypeRef.Named typeId, JsonValueKind.Array ->
                match productFields types typeId with
                | Some fields -> parseFields types fields value |> Result.map (fun parsed -> PureRunValue.Product(typeId, parsed))
                | None ->
                    let variant =
                        if value.GetArrayLength() <> 2 || value[0].ValueKind <> JsonValueKind.String then None
                        else unionVariants types typeId
                             |> Option.bind (List.tryFind (fun variant -> StableId.value variant.Id = value[0].GetString()))
                    match variant with
                    | Some variant -> parseFields types variant.Fields value[1]
                                      |> Result.map (fun parsed -> PureRunValue.Union(typeId, variant.Id, parsed))
                    | None -> Error(diagnostic "RunInvalidResult" "The Union result does not match its declaration.")
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]), JsonValueKind.Array
                when value.GetArrayLength() = 2
                     && (value[0].ValueKind = JsonValueKind.True || value[0].ValueKind = JsonValueKind.False)
                     && not (supportedScalar okType && supportedScalar errorType) ->
                let isOk = value[0].GetBoolean()
                parseElement types (if isOk then okType else errorType) value[1]
                |> Result.map (fun payload -> PureRunValue.ResultValue(okType, errorType, isOk, payload))
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]), JsonValueKind.Array
                when value.GetArrayLength() = 2
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
            | TypeRef.Primitive PrimitiveType.Unit, JsonValueKind.String when value.GetString() = "unit" ->
                Ok PureRunValue.Unit
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

    /// A generated call site: the entry parameter bound to a fake and one operation of its capability.
    type private CallSite =
        { ParameterId: ParameterId
          Operation: ExecutionCapabilityOperation }

    [<RequireQualifiedAccess>]
    type private Envelope =
        | Value of PureRunValue * PureRunCall list
        | Limit of string
        | Exception of exceptionType: string * message: string * frames: string list * calls: PureRunCall list * exhausted: bool

    let private parseEnvelope types resultType (sites: CallSite list) (output: string) =
        let invalid () = Error(diagnostic "RunInvalidResult" "The process did not return one JSON result.")
        let collect (items: Result<'T, PureRunError> list) =
            List.foldBack (fun item state -> state |> Result.bind (fun rest -> item |> Result.map (fun value -> value :: rest))) items (Ok [])
        let parseCalls (value: JsonElement) =
            if value.ValueKind <> JsonValueKind.Array then invalid () else
                value.EnumerateArray() |> Seq.toList |> List.map (fun call ->
                    let site =
                        if call.ValueKind = JsonValueKind.Array && call.GetArrayLength() = 2 && call[0].ValueKind = JsonValueKind.Number then
                            match call[0].TryGetInt32() with
                            | true, index when index >= 0 && index < sites.Length -> Some(sites[index])
                            | _ -> None
                        else None
                    match site with
                    | Some site when call[1].ValueKind = JsonValueKind.Array && call[1].GetArrayLength() = site.Operation.Parameters.Length ->
                        site.Operation.Parameters
                        |> List.mapi (fun index parameter ->
                            let item = call[1].[index]
                            parseElement types parameter.Type item |> Result.map (fun value -> parameter.Id, value))
                        |> collect
                        |> Result.map (fun arguments ->
                            { ParameterId = site.ParameterId
                              OperationId = site.Operation.Id
                              Arguments = arguments |> List.sortBy (fst >> StableId.value) })
                    | _ -> invalid ())
                |> collect
        try
            use document = JsonDocument.Parse(output, JsonDocumentOptions(MaxDepth = 256))
            let root = document.RootElement
            let text (item: JsonElement) = if item.ValueKind = JsonValueKind.String then Some(item.GetString()) else None
            if root.ValueKind <> JsonValueKind.Array || root.GetArrayLength() < 2 then invalid () else
                match text root[0], root.GetArrayLength() with
                | Some "value", 3 ->
                    parseElement types resultType root[1]
                    |> Result.bind (fun value -> parseCalls root[2] |> Result.map (fun calls -> Envelope.Value(value, calls)))
                | Some "limit", 2 when (text root[1]).IsSome -> Ok(Envelope.Limit(root[1].GetString()))
                | Some "exception", 6 when (text root[1]).IsSome && (text root[2]).IsSome && root[3].ValueKind = JsonValueKind.Array
                                           && (root[5].ValueKind = JsonValueKind.True || root[5].ValueKind = JsonValueKind.False) ->
                    let frames = root[3].EnumerateArray() |> Seq.choose text |> Seq.toList
                    parseCalls root[4]
                    |> Result.map (fun calls -> Envelope.Exception(root[1].GetString(), root[2].GetString(), frames, calls, root[5].GetBoolean()))
                | _ -> invalid ()
        with :? JsonException -> invalid ()

    let rec private validContainers (supported: int -> TypeRef -> bool) value =
        let local =
            match value with
            | PureRunValue.ListValue(itemType, items) ->
                not (supportedScalar itemType) && supported 1 itemType
                && (items |> List.forall (fun item -> valueType item = itemType))
            | PureRunValue.OptionValue(itemType, item) ->
                not (supportedScalar itemType) && supported 1 itemType
                && (item |> Option.forall (fun value -> valueType value = itemType))
            | PureRunValue.ResultValue(okType, errorType, isOk, payload) ->
                not (supportedScalar okType && supportedScalar errorType)
                && supported 1 okType && supported 1 errorType
                && valueType payload = (if isOk then okType else errorType)
            | _ -> true
        local && (children value |> List.forall (validContainers supported))

    let private boundedValue value =
        let mutable remaining = 8192
        let rec visit depth value =
            remaining <- remaining - 1
            let count size = remaining <- remaining - size; remaining >= 0
            match value with
            | PureRunValue.Unit
            | PureRunValue.String _
            | PureRunValue.Bool _
            | PureRunValue.Int64 _ -> remaining >= 0
            | _ when depth >= maxValueLayers || remaining < 0 -> false
            | PureRunValue.StringList items -> count items.Length
            | PureRunValue.BoolList items -> count items.Length
            | PureRunValue.Int64List items -> count items.Length
            | PureRunValue.StringOption item -> count (if Option.isSome item then 1 else 0)
            | PureRunValue.BoolOption item -> count (if Option.isSome item then 1 else 0)
            | PureRunValue.Int64Option item -> count (if Option.isSome item then 1 else 0)
            | PureRunValue.ResultOk _
            | PureRunValue.ResultErr _ -> count 1
            | PureRunValue.FakeCapability(_, operations) ->
                count (operations |> List.sumBy (fun op ->
                    op.Cases |> Option.defaultValue [] |> List.sumBy (fun item -> 1 + item.Arguments.Length + item.Responses.Length)))
                && (children value |> List.forall (visit (depth + 1)))
            | _ -> children value |> List.forall (visit (depth + 1))
        visit 0 value

    let rec private containsFake value =
        children value |> List.exists (fun child ->
            match child with
            | PureRunValue.FakeCapability _
            | PureRunValue.HostCapability _ -> true
            | _ -> containsFake child)

    let private hostTypeName =
        System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)

    /// Checks one binding against its capability declaration. Every operation of a bound capability must
    /// have runner-supported parameter and result types, because the program may call any of them and
    /// the runner records each call.
    let private fakeProblem (declarations: AuthoringDeclarations) (supported: int -> TypeRef -> bool) value =
        let capabilityProblem capabilityId =
            match declarations.Capabilities |> List.tryFind (fun item -> item.Id = capabilityId) with
            | None -> Error("RunBindingShapeMismatch", "The binding names an unknown capability.")
            | Some capability when capability.Operations |> List.exists (fun operation ->
                not (supported 0 operation.ResultType) || operation.Parameters |> List.exists (fun parameter -> not (supported 0 parameter.Type))) ->
                Error("RunBindingTypeUnsupported", "Every operation of a bound capability needs parameter and result types that runPure values support.")
            | Some capability -> Ok capability
        match value with
        | PureRunValue.HostCapability(capabilityId, implementation, _) ->
            match capabilityProblem capabilityId with
            | Error problem -> Some problem
            | Ok _ when isNull implementation || implementation.Length > 512 || not (hostTypeName.IsMatch implementation) ->
                Some("RunBindingShapeMismatch", "A host implementation must be a dotted C# type name.")
            | Ok _ -> None
        | PureRunValue.FakeCapability(capabilityId, operations) ->
            match capabilityProblem capabilityId with
            | Error problem -> Some problem
            | Ok capability ->
                let declared = capability.Operations |> List.map (fun operation -> operation.Id, operation) |> Map.ofList
                let ids = operations |> List.map (fun operation -> operation.OperationId)
                if List.distinct ids <> ids || ids |> List.exists (fun operationId -> not (Map.containsKey operationId declared)) then
                    Some("RunBindingShapeMismatch", "Fake operations must be distinct operations of the faked capability.")
                elif operations |> List.exists (fun operation ->
                    operation.Cases |> Option.exists (fun cases ->
                        let parameters = declared[operation.OperationId].Parameters |> List.sortBy (fun p -> StableId.value p.Id)
                        not (List.isEmpty operation.Responses)
                        || cases |> List.exists (fun item ->
                            item.Arguments.Length <> parameters.Length
                            || not (List.forall2 (fun (id, value) (p: SemanticCapabilityParameter) -> id = p.Id && valueType value = p.Type) item.Arguments parameters))
                        || (cases |> List.map (fun item -> item.Arguments) |> List.distinct |> List.length) <> cases.Length)) then
                    Some("RunBindingShapeMismatch", "Keyed fakes require unique complete typed argument sets and cannot mix cases with ordered responses.")
                elif operations |> List.exists (fun operation ->
                    PureRunFakeOperation.responses operation |> List.exists (PureRunFakeResponse.immediate >> function
                        | PureRunFakeResponse.Return response
                        | PureRunFakeResponse.AwaitCancellation response -> valueType response <> declared[operation.OperationId].ResultType
                        | PureRunFakeResponse.Throw _
                        | PureRunFakeResponse.Delayed _ -> false)) then
                    Some("RunBindingShapeMismatch", "Each fake return value must have the operation's declared result type.")
                else None
        | _ -> None

    let private runVerified
        (session: AuthoringSession)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        (hostSources: (string * string) list)
        : Result<PureRunResult, PureRunError> =
        let declarations = session.Declarations()
        let supported = typeSupport declarations.Types
        let arguments = arguments |> List.map canonicalValue
        let expected = expected |> Option.map canonicalValue
        match declarations.Functions |> List.tryFind (fun item -> item.Id = functionId) with
        | None -> Error(diagnostic "RunFunctionNotFound" "The entry function does not exist.")
        | Some fn when not (List.isEmpty (TypeParameters.ofFunction fn)) ->
            Error(diagnostic "RunGenericEntryUnsupported" "A generic function cannot be an entry point; run a concrete function that calls it.")
        | Some fn when fn.Parameters.Length <> arguments.Length ->
            Error(diagnostic "RunArgumentCountMismatch" "The number of arguments differs from the function signature.")
        | Some fn when List.exists2 (fun (parameter: SemanticParameter) value -> parameter.Type <> valueType value) fn.Parameters arguments ->
            Error(diagnostic "RunArgumentTypeMismatch" "An argument has the wrong type; capability parameters take a fakeCapability binding.")
        | Some fn when (arguments |> List.exists (function PureRunValue.HostCapability _ -> true | _ -> false)) <> not (List.isEmpty hostSources) ->
            Error(diagnostic "RunHostSourceInvalid" "hostSources are required exactly when an argument is a hostCapability binding.")
        | Some fn when arguments @ Option.toList expected |> List.exists containsFake ->
            Error(diagnostic "RunBindingPlacement" "Fake capabilities are accepted only as top-level arguments for capability parameters.")
        | Some fn when arguments |> List.exists (fakeProblem declarations supported >> Option.isSome) ->
            let code, message = arguments |> List.pick (fakeProblem declarations supported)
            Error(diagnostic code message)
        | Some fn when arguments |> List.exists (validProduct declarations.Types >> not) ->
            Error(diagnostic "RunProductShapeMismatch" "Product arguments must supply each declared field exactly once with its exact type (at most 256 fields), including nested Products.")
        | Some fn when arguments |> List.exists (validUnion declarations.Types >> not) ->
            Error(diagnostic "RunUnionShapeMismatch" "Union arguments must name a variant of the declared Union and supply each field exactly once with its exact type, including nested Unions.")
        | Some fn when arguments |> List.exists (validContainers supported >> not) ->
            Error(diagnostic "RunContainerTypeMismatch" "Nested List/Option item and Result payload types must be supported and match every payload; scalar-only containers and Results use the flat value representation.")
        | Some fn when not (supported 0 fn.ResultType) ->
            Error(diagnostic "RunResultTypeUnsupported" "Supported results are scalars and List/Option/Result/Product/Union compositions with at most eight composite layers along any non-recursive path.")
        | Some fn when expected |> Option.exists (fun value -> valueType value <> fn.ResultType) ->
            Error(diagnostic "RunExpectedTypeMismatch" "The expected result type differs from the declared result type.")
        | Some fn when expected |> Option.exists (validProduct declarations.Types >> not) ->
            Error(diagnostic "RunProductShapeMismatch" "A Product expectation must supply each declared field exactly once with its exact type.")
        | Some fn when expected |> Option.exists (validUnion declarations.Types >> not) ->
            Error(diagnostic "RunUnionShapeMismatch" "A Union expectation must name a variant of the declared Union and supply each field exactly once with its exact type.")
        | Some fn when expected |> Option.exists (validContainers supported >> not) ->
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
            // Check only functions reachable from this entry; unrelated parallel functions do not
            // make a sequential host binding require a concurrency assertion.
            let emitted =
                session.ProjectExecution()
                |> Result.bind (fun program ->
                    let program = ExecutionCallGraph.forEntry program functionId
                    let operationIds = program.Capabilities |> List.collect (fun capability -> capability.Operations |> List.map (fun operation -> operation.Id))
                    // The assertion is capability-wide: one unsafe instance prevents a safe fake or
                    // another asserted host of the same type from laundering it into the profile.
                    let sequentialHosts =
                        arguments |> List.choose (function
                            | PureRunValue.HostCapability(id, _, false) -> Some id
                            | _ -> None) |> Set.ofList
                    let concurrentIds =
                        program.Capabilities |> List.filter (fun capability -> not (List.isEmpty capability.Operations))
                        |> List.map (fun capability -> capability.Id)
                        |> List.filter (fun id -> not (Set.contains id sequentialHosts))
                    let profile =
                        CSharpBackendProfile.externalImplementations operationIds
                        |> CSharpBackendProfile.withConcurrentCapabilities concurrentIds
                    CSharpBackend.emit program profile
                    |> Result.mapError AuthoringSessionError.BackendFailure
                    |> Result.map (fun artifact -> program, artifact))
            match emitted, session.Status() with
            | Error error, _
            | _, Error error -> Error(PureRunError.SessionFailure error)
            | Ok(program, _), Ok _ when
                not (CSharpBackend.usesConcurrencyRuntime program)
                && arguments |> List.exists (function
                    | PureRunValue.FakeCapability(_, operations) ->
                        operations |> List.exists (fun operation ->
                            PureRunFakeOperation.responses operation |> List.exists (function
                                | PureRunFakeResponse.AwaitCancellation _
                                | PureRunFakeResponse.Delayed _ -> true
                                | _ -> false))
                    | _ -> false) ->
                Error(diagnostic "RunBindingShapeMismatch" "awaitCancellation and delayMillis responses need a program that uses ParallelAll, a task construct or CancellationRequested.")
            | Ok(program, artifact), Ok status ->
                let root = Path.Combine(Path.GetTempPath(), "nyrwvy-pure-run-" + Guid.NewGuid().ToString("N"))
                try
                    try
                        Directory.CreateDirectory(root) |> ignore
                        File.WriteAllText(Path.Combine(root, "Generated.csproj"),
                            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
                        File.WriteAllText(Path.Combine(root, "Generated.cs"), artifact.Source)
                        hostSources |> List.iteri (fun index (_, text) -> File.WriteAllText(Path.Combine(root, $"HostSource{index}.cs"), text))
                        let args =
                            arguments |> List.mapi (fun index value ->
                                match value with
                                | PureRunValue.FakeCapability _ -> $"new Fake{index}()"
                                | PureRunValue.HostCapability _ -> $"new Host{index}()"
                                | value -> sourceArgument value)
                            |> String.concat ", "
                        let fakes =
                            List.zip fn.Parameters arguments |> List.indexed |> List.choose (fun (index, (parameter, value)) ->
                                let capability capabilityId = program.Capabilities |> List.find (fun item -> item.Id = capabilityId)
                                match value with
                                | PureRunValue.FakeCapability(capabilityId, operations) ->
                                    Some(index, parameter.Id, capability capabilityId, Choice1Of2 operations)
                                | PureRunValue.HostCapability(capabilityId, implementation, _) ->
                                    Some(index, parameter.Id, capability capabilityId, Choice2Of2 implementation)
                                | _ -> None)
                        let sites =
                            fakes |> List.collect (fun (_, parameterId, capability, _) ->
                                capability.Operations |> List.sortBy (fun operation -> StableId.value operation.Id)
                                |> List.map (fun operation -> { ParameterId = parameterId; Operation = operation }))
                        let entry = CSharpBackendNames.functionName fn.Id
                        let mutable nextName = 0
                        let fresh () =
                            nextName <- nextName + 1
                            "v" + string nextName
                        // Each named type is encoded by one local function, so recursive types stay finite.
                        let encoders = System.Collections.Generic.Dictionary<TypeId, string>()
                        let pending = System.Collections.Generic.Queue<TypeId>()
                        let layerLimit = string maxValueLayers
                        let rec encode itemType access (depthBase: string) (offset: int) =
                            let depth = if offset = 0 then depthBase else depthBase + " + " + string offset
                            let layer expression =
                                $"({depth} >= {layerLimit} ? throw new RunnerException(\"Result exceeds {layerLimit} composite layers.\") : {expression})"
                            let array items = "new object[] { " + String.concat ", " items + " }"
                            let expression =
                                match itemType with
                                | TypeRef.Primitive PrimitiveType.Unit -> "\"unit\""
                                | TypeRef.Primitive PrimitiveType.String -> "CheckedString(" + access + ")"
                                | TypeRef.Primitive _ -> "(object)" + access
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) ->
                                    let name, accumulator = fresh (), fresh ()
                                    let child = encode item name depthBase (offset + 1)
                                    layer $"{access}.Fold(new System.Collections.Generic.List<object>(), ({accumulator}, {name}) => {{ if ({accumulator}.Count >= 4096) throw new RunnerException(\"List result exceeds 4096 items.\"); {accumulator}.Add({child}); return {accumulator}; }})"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
                                    let child = encode item (access + ".Value") depthBase (offset + 1)
                                    layer $"({access}.HasValue ? {array [ child ]} : Array.Empty<object>())"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                    let ok = array [ "true"; encode okType (access + ".OkValue") depthBase (offset + 1) ]
                                    let error = array [ "false"; encode errorType (access + ".ErrorValue") depthBase (offset + 1) ]
                                    layer $"({access}.IsOk ? {ok} : {error})"
                                | TypeRef.Named typeId ->
                                    if not (encoders.ContainsKey typeId) then
                                        encoders[typeId] <- "Encode" + CSharpBackendNames.typeName typeId
                                        pending.Enqueue typeId
                                    encoders[typeId] + "(" + access + ", " + depth + ")"
                                | _ -> invalidOp "Unsupported output type passed validation."
                            "Count(" + expression + ")"
                        let encodeNamed typeId =
                            let fields owner (fields: SemanticField list) =
                                fields |> List.map (fun field -> encode field.Type (owner + "." + CSharpBackendNames.fieldName field.Id) "depth" 1)
                                |> fun items -> "new object[] { " + String.concat ", " items + " }"
                            let body =
                                match productFields declarations.Types typeId with
                                | Some declared -> fields "value" declared
                                | None ->
                                    let cases = unionVariants declarations.Types typeId |> Option.get
                                    let fallback = "throw new RunnerException(\"Unknown or null Union variant.\")"
                                    List.foldBack (fun (variant: SemanticVariant) rest ->
                                        let name = fresh ()
                                        let variantName = CSharpBackendNames.variantName variant.Id
                                        let tag = JsonSerializer.Serialize(StableId.value variant.Id)
                                        $"(value is {variantName} {name} ? new object[] {{ {tag}, {fields name variant.Fields} }} : {rest})") cases fallback
                            $"object {encoders[typeId]}({CSharpBackendNames.typeName typeId} value, int depth) => depth >= {layerLimit} ? throw new RunnerException(\"Result exceeds {layerLimit} composite layers.\") : {body};\n"
                        let encoded = encode fn.ResultType "result" "0" 0
                        let callArms =
                            sites |> List.mapi (fun site call ->
                                let arguments =
                                    call.Operation.Parameters |> List.mapi (fun index parameter ->
                                        encode parameter.Type $"(({sourceType parameter.Type})arguments[{index}])" "0" 0)
                                    |> String.concat ", "
                                $"    {site} => new object[] {{ {site}, new object[] {{ {arguments} }} }},\n")
                            |> String.concat ""
                        let fakeClasses =
                            let mutable site = 0
                            fakes |> List.map (fun (index, _, capability, binding) ->
                                let interfaceName = CSharpBackendNames.capabilityName capability.Id
                                let methods =
                                    capability.Operations |> List.sortBy (fun operation -> StableId.value operation.Id)
                                    |> List.map (fun operation ->
                                        let name = CSharpBackendNames.capabilityOperationName operation.Id
                                        let parameters =
                                            operation.Parameters |> List.map (fun parameter -> sourceType parameter.Type + " " + CSharpBackendNames.capabilityParameterName parameter.Id)
                                            |> String.concat ", "
                                        let values =
                                            operation.Parameters |> List.map (fun parameter -> "(object)" + CSharpBackendNames.capabilityParameterName parameter.Id)
                                            |> String.concat ", "
                                        let names =
                                            operation.Parameters |> List.map (fun parameter -> CSharpBackendNames.capabilityParameterName parameter.Id)
                                            |> String.concat ", "
                                        let script =
                                            match binding with
                                            | Choice1Of2 scripted -> scripted |> List.tryFind (fun item -> item.OperationId = operation.Id)
                                            | Choice2Of2 _ -> None
                                        let keyed = script |> Option.bind (fun item -> item.Cases)
                                        let responses = script |> Option.map PureRunFakeOperation.responses |> Option.defaultValue []
                                        let selectResponse =
                                            match keyed with
                                            | None -> $"            response = {name}Next++;\n"
                                            | Some keys ->
                                                let mutable offset = 0
                                                let branches =
                                                    keys |> List.mapi (fun index item ->
                                                        let conditions = item.Arguments |> List.map (fun (id, value) -> sourceMatches ("case" + string index + CSharpBackendNames.capabilityParameterName id) value (CSharpBackendNames.capabilityParameterName id))
                                                        let condition = if List.isEmpty conditions then "true" else String.concat " && " conditions
                                                        let prefix = if List.isEmpty conditions then "" elif index = 0 then "if (" + condition + ")" else "else if (" + condition + ")"
                                                        let branch = $"            {prefix}\n            {{\n                int local = {name}Next[{index}]++;\n                if (local >= {item.Responses.Length}) throw new FakeResponsesExhaustedException();\n                response = {offset} + local;\n            }}\n"
                                                        offset <- offset + item.Responses.Length
                                                        branch) |> String.concat ""
                                                branches + (if List.isEmpty keys then "            throw new FakeArgumentsUnmatchedException();\n" elif List.isEmpty operation.Parameters then "" else "            else throw new FakeArgumentsUnmatchedException();\n")
                                        let rec answer response =
                                            match response with
                                            | PureRunFakeResponse.Return value -> $"return {sourceArgument value};"
                                            | PureRunFakeResponse.Throw message -> $"throw new FakeCapabilityException({sourceArgument (PureRunValue.String message)});"
                                            | PureRunFakeResponse.AwaitCancellation value -> $"NyRuntime.WaitForCancellation(); return {sourceArgument value};"
                                            | PureRunFakeResponse.Delayed(delay, inner) -> $"NyRuntime.Delay({delay}L); " + answer inner
                                        let cases =
                                            responses |> List.mapi (fun position response -> $"            case {position}: {answer response}\n")
                                            |> String.concat ""
                                        let answer =
                                            match binding with
                                            | Choice1Of2 _ when keyed = Some [] ->
                                                $"        FakeLog.Record({site}, new object[] {{ {values} }});\n        throw new FakeArgumentsUnmatchedException();\n"
                                            | Choice1Of2 _ ->
                                                $"        int response;\n        lock (FakeLog.Calls)\n        {{\n            FakeLog.Record({site}, new object[] {{ {values} }});\n{selectResponse}        }}\n        switch (response)\n        {{\n{cases}            default: throw new FakeResponsesExhaustedException();\n        }}\n"
                                            | Choice2Of2 _ -> $"        FakeLog.Record({site}, new object[] {{ {values} }});\n        return inner.{name}({names});\n"
                                        let text =
                                            (match binding, keyed with
                                             | Choice1Of2 _, Some [] -> ""
                                             | Choice1Of2 _, Some keys -> $"    readonly int[] {name}Next = new int[{keys.Length}];\n"
                                             | Choice1Of2 _, None -> $"    int {name}Next;\n"
                                             | Choice2Of2 _, _ -> "")
                                            + $"    public {sourceType operation.ResultType} {name}({parameters})\n    {{\n"
                                            + answer + "    }\n"
                                        site <- site + 1
                                        text)
                                    |> String.concat ""
                                match binding with
                                | Choice1Of2 _ -> $"sealed class Fake{index} : {interfaceName}\n{{\n{methods}}}\n"
                                | Choice2Of2 implementation ->
                                    // Records each call, then forwards it to the registered host implementation.
                                    $"sealed class Host{index} : {interfaceName}\n{{\n    readonly {interfaceName} inner = new {implementation}();\n{methods}}}\n")
                            |> String.concat ""
                        let namedEncoders = StringBuilder()
                        while pending.Count > 0 do
                            namedEncoders.Append(encodeNamed (pending.Dequeue())) |> ignore
                        let namedEncoders = namedEncoders.ToString()
                        let checksText = (encoded + callArms + namedEncoders).Contains("CheckedString(")
                        // The child prints one envelope: ["value", v], ["limit", message] for runner-owned
                        // conversion limits, or ["exception", type, message, frames] for a program failure.
                        let program =
                            "using System; using System.Globalization; using System.Linq; using System.Text; using System.Text.Json; using Nyrwvy.Generated;\n"
                            + "var options = new JsonSerializerOptions { MaxDepth = 256 };\n"
                            + "int remaining = 8192;\n"
                            + (if checksText then "long textRemaining = 65536;\n" else "")
                            + "try\n{\n"
                            // Concurrent programs run on a virtual clock, so waits and delays are deterministic.
                            + (if CSharpBackend.usesConcurrencyRuntime program then "    NyRuntime.Clock = new NyVirtualClock();\n" else "")
                            + $"    var result = NyFunctions.{entry}({args});\n"
                            + $"    Console.WriteLine(JsonSerializer.Serialize(new object[] {{ \"value\", {encoded}, EncodeCalls() }}, options));\n"
                            + "}\n"
                            + "catch (RunnerException error) { Console.WriteLine(JsonSerializer.Serialize(new object[] { \"limit\", error.Message })); }\n"
                            + "catch (Exception error)\n{\n"
                            + "    var frames = (new System.Diagnostics.StackTrace(error, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>()).Select(frame => frame.GetMethod()?.Name ?? \"\").ToArray();\n"
                            + "    try { Console.WriteLine(JsonSerializer.Serialize(new object[] { \"exception\", error.GetType().FullName ?? \"\", error.Message, frames, EncodeCalls(), error is FakeResponsesExhaustedException }, options)); }\n"
                            + "    catch (RunnerException limit) { Console.WriteLine(JsonSerializer.Serialize(new object[] { \"limit\", limit.Message })); }\n"
                            + "}\n"
                            // Calls are encoded after the result, with their own value budget.
                            + "object[] EncodeCalls()\n{\n    remaining = 8192;\n"
                            + (if checksText then "    textRemaining = 65536;\n" else "")
                            + "    return FakeLog.Calls.Select(call => EncodeCall(call.Site, call.Arguments)).ToArray();\n}\n"
                            + "object EncodeCall(int site, object[] arguments) => site switch\n{\n"
                            + callArms
                            + "    _ => throw new RunnerException(\"Unknown capability call site.\"),\n};\n"
                            + "object Count(object value) { if (--remaining < 0) throw new RunnerException(\"Result exceeds 8192 value nodes.\"); return value; }\n"
                            + (if checksText then
                                "string CheckedString(string value) { if (value == null) throw new RunnerException(\"Null String result.\"); textRemaining -= value.Length; if (textRemaining < 0) throw new RunnerException(\"Result exceeds 65536 String code units.\"); new UTF8Encoding(false, true).GetByteCount(value); return value; }\n"
                               else "")
                            + namedEncoders
                            + "sealed class RunnerException(string message) : Exception(message);\n"
                            + "sealed class FakeCapabilityException(string message) : Exception(message);\n"
                            + "sealed class FakeArgumentsUnmatchedException() : Exception(\"No fake case matches the complete call arguments.\");\n"
                            + "sealed class FakeResponsesExhaustedException() : Exception(\"The fake operation has no scripted response left for this call.\");\n"
                            + "static class FakeLog\n{\n"
                            + "    public static readonly System.Collections.Generic.List<(int Site, object[] Arguments)> Calls = new();\n"
                            + "    public static void Record(int site, object[] arguments)\n    {\n"
                            // Workers of ParallelAll and tasks may call fakes concurrently.
                            + "        lock (Calls)\n        {\n"
                            + "            if (Calls.Count >= 1024) throw new RunnerException(\"The program made more than 1024 capability calls.\");\n"
                            + "            Calls.Add((site, arguments));\n        }\n    }\n}\n"
                            + fakeClasses
                        File.WriteAllText(Path.Combine(root, "Program.cs"), program)
                        let frames = frameResolver artifact.Symbols
                        let runtimeFailure exceptionType message frameNames exhausted calls =
                            PureRunError.RuntimeFailure
                                { SnapshotId = status.SnapshotId
                                  FunctionId = fn.Id
                                  ExceptionType = exceptionType
                                  Message = message
                                  Frames = frames frameNames
                                  BindingExhausted = exhausted
                                  Calls = calls }
                        launch root [ "build"; "Generated.csproj"; "--nologo"; "-v:q"; "--ignore-failed-sources" ] 30000
                        |> Result.bind (fun _ ->
                            launchRaw root [ Path.Combine(root, "bin", "Debug", "net10.0", "Generated.dll") ] 5000)
                        |> Result.bind (fun (exitCode, output, errors) ->
                            if exitCode = 0 then
                                parseEnvelope declarations.Types fn.ResultType sites output
                                |> Result.bind (function
                                    | Envelope.Value(actual, calls) -> Ok(actual, calls)
                                    | Envelope.Limit message -> Error(diagnostic "RunProcessFailure" message)
                                    | Envelope.Exception(exceptionType, message, frameNames, calls, exhausted) ->
                                        Error(runtimeFailure exceptionType message frameNames exhausted calls))
                            elif errors.Contains("Stack overflow", StringComparison.Ordinal) then
                                // A stack overflow cannot be caught; the runtime prints the stack to stderr.
                                let names = stackOverflowFrames.Matches(errors) |> Seq.map (fun item -> item.Value) |> Seq.toList
                                Error(runtimeFailure "System.StackOverflowException" "Stack overflow." names false [])
                            else Error(processFailure output errors))
                        |> Result.map (fun (actual, calls) ->
                            { SnapshotId = status.SnapshotId
                              FunctionId = fn.Id
                              Actual = actual
                              Expected = expected
                              Matches = expected |> Option.map ((=) actual)
                              Mismatch = expected |> Option.bind (fun expected -> mismatch expected actual)
                              Calls = calls })
                    with
                    | :? IOException as error -> Error(diagnostic "RunIoFailure" error.Message)
                    | :? UnauthorizedAccessException as error -> Error(diagnostic "RunIoFailure" error.Message)
                finally
                    if Directory.Exists(root) then
                        try Directory.Delete(root, true)
                        with
                        | :? IOException
                        | :? UnauthorizedAccessException -> ()

    /// Reads explicitly registered host C# sources. Relative paths resolve against the workspace
    /// directory; at most 16 existing `.cs` files of at most 1 MiB each are accepted.
    let private readHostSources (session: AuthoringSession) (paths: string list) =
        let baseDirectory = Path.GetDirectoryName(Path.GetFullPath session.Path)
        if paths.Length > 16 then Error(diagnostic "RunHostSourceInvalid" "At most 16 host source files are accepted.") else
            paths |> List.fold (fun state path ->
                state |> Result.bind (fun loaded ->
                    try
                        let full = Path.GetFullPath(path, baseDirectory)
                        let info = FileInfo(full)
                        if not (String.Equals(info.Extension, ".cs", StringComparison.OrdinalIgnoreCase)) || not info.Exists then
                            Error(diagnostic "RunHostSourceInvalid" $"Host source '{path}' is not an existing .cs file.")
                        elif info.Length > 1048576L then
                            Error(diagnostic "RunHostSourceInvalid" $"Host source '{path}' exceeds 1 MiB.")
                        else Ok(loaded @ [ full, File.ReadAllText full ])
                    with
                    | :? ArgumentException
                    | :? IOException
                    | :? UnauthorizedAccessException
                    | :? NotSupportedException -> Error(diagnostic "RunHostSourceInvalid" $"Host source '{path}' cannot be read."))) (Ok [])

    /// Runs a function whose capability parameters may be bound to host implementations compiled from
    /// the given C# sources, alongside scripted fakes and ordinary values.
    let runWithHost
        (session: AuthoringSession)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        (hostSources: string list)
        : Result<PureRunResult, PureRunError> =
        match session.VerifyPersistedSource() with
        | Error error -> Error(PureRunError.SessionFailure error)
        | Ok() when arguments @ Option.toList expected |> List.exists (boundedValue >> not) ->
            Error(diagnostic "RunArgumentTooLarge" $"Values are limited to {maxValueLayers} nested composite layers and 8192 total value nodes.")
        | Ok() -> readHostSources session hostSources |> Result.bind (runVerified session functionId arguments expected)

    let run session functionId arguments expected = runWithHost session functionId arguments expected []
