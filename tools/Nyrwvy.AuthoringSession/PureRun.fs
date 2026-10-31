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
    /// A Float64 leaf. Its bits are exact; every NaN is the one canonical NaN, so run values compare
    /// Float64 bitwise with all NaNs equal and -0.0 distinct from 0.0. It has no flat container form.
    | Float64 of Float64Bits
    /// A Bits32 leaf, carried as its unsigned value 0..4294967295. It has no flat container form.
    | Bits32 of uint32
    /// A Bytes leaf. The runner carries it as its latin-1 String (one code point per byte). It has
    /// no flat container form.
    | Bytes of byte[]
    | StringList of string list
    | BoolList of bool list
    | Int64List of int64 list
    | StringOption of string option
    | BoolOption of bool option
    | Int64Option of int64 option
    | ResultOk of errorKind: PureRunScalarKind * value: PureRunScalar
    | ResultErr of okKind: PureRunScalarKind * error: PureRunScalar
    /// `typeArguments` instantiate a generic Product's type parameters; empty for a monomorphic one.
    | Product of typeId: TypeId * typeArguments: TypeRef list * fields: (FieldId * PureRunValue) list
    | Union of typeId: TypeId * typeArguments: TypeRef list * variantId: VariantId * fields: (FieldId * PureRunValue) list
    | ListValue of itemType: TypeRef * items: PureRunValue list
    | OptionValue of itemType: TypeRef * item: PureRunValue option
    /// A Map<K, V> as its entries in iteration (insertion) order, each key once.
    | MapValue of keyType: TypeRef * valueType: TypeRef * entries: (PureRunValue * PureRunValue) list
    /// A Set<T> as its items in iteration (insertion) order, each item once.
    | SetValue of itemType: TypeRef * items: PureRunValue list
    /// A typed Result. Scalar payloads normalize to the legacy representation internally while
    /// legacy response compatibility is retained.
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
      /// The backend-neutral kind: "fault", "cancelled", or "fatal" (allocation failure or stack
      /// overflow, which no ExceptionCatch handles).
      Kind: string
      /// The C# backend's .NET exception type, kept as a diagnostic.
      ExceptionType: string
      Message: string
      /// Innermost frame first; frames without a semantic declaration are omitted.
      Frames: PureRunFrame list
      /// True when a fake operation was called after its scripted responses ran out.
      BindingExhausted: bool
      Calls: PureRunCall list }

type internal PureRunInputIssue =
    { Path: string list
      ExpectedType: TypeRef option
      ActualType: TypeRef option
      Reason: string }

/// The backend a run executes on. C# is the default; Zig covers a growing subset and reports the
/// rest as `RunBackendUnsupported`.
[<RequireQualifiedAccess>]
type PureRunBackend =
    | CSharp
    | Zig

/// The backends a workspace targets, in preference order, from the hand-edited sidecar
/// `<snapshot>.backends.json` (`{"targets": ["csharp", "zig"]}`). It is not semantic, so it never
/// enters the snapshot or its fingerprint. There is no default: creating a workspace writes
/// `["csharp"]`, and a workspace without the sidecar must name the backend on each run.
[<RequireQualifiedAccess>]
module PureRunBackends =
    let parse (name: string) =
        match name with
        | "csharp" -> Ok PureRunBackend.CSharp
        | "zig" -> Ok PureRunBackend.Zig
        | other -> Error $"Unknown backend '{other}'; expected \"csharp\" or \"zig\"."

    let declared (snapshotPath: string) : Result<PureRunBackend list, string> =
        let path = snapshotPath + ".backends.json"
        if not (File.Exists path) then
            Error $"The workspace declares no target backend. Create {path} with {{\"targets\": [\"csharp\"]}} (or list \"zig\"), or name the backend on the request."
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                match document.RootElement.TryGetProperty "targets" with
                | true, targets when targets.ValueKind = JsonValueKind.Array && targets.GetArrayLength() > 0 ->
                    let names = targets.EnumerateArray() |> Seq.map (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() else "") |> Seq.toList
                    if List.length (List.distinct names) <> names.Length then Error $"{path} lists a backend twice."
                    else
                        names |> List.fold (fun state name -> state |> Result.bind (fun parsed -> parse name |> Result.map (fun backend -> parsed @ [ backend ]))) (Ok [])
                | _ -> Error $"{path} must be an object with a non-empty \"targets\" array."
            with :? JsonException as error -> Error $"{path} is not valid JSON: {error.Message}"

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
/// host sources. Runs in the session's bounded runner worker (see PureRunWorker), or in a child
/// process of its own when host sources are involved.
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
        | PureRunScalarKind.String -> "NyStr"
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
        | PureRunValue.MapValue(keyType, valueType, _) -> TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ])
        | PureRunValue.SetValue(itemType, _) -> TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ])
        | PureRunValue.OptionValue(itemType, _) -> TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
        | PureRunValue.Product(typeId, typeArguments, _)
        | PureRunValue.Union(typeId, typeArguments, _, _) -> TypeRef.Named(typeId, typeArguments)
        | PureRunValue.ResultOk(errorKind, value) ->
            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ scalarType (scalarKind value); scalarType errorKind ])
        | PureRunValue.ResultErr(okKind, error) ->
            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ scalarType okKind; scalarType (scalarKind error) ])
        | PureRunValue.String _ -> TypeRef.Primitive PrimitiveType.String
        | PureRunValue.Bool _ -> TypeRef.Primitive PrimitiveType.Bool
        | PureRunValue.Int64 _ -> TypeRef.Primitive PrimitiveType.Int64
        | PureRunValue.Float64 _ -> TypeRef.Primitive PrimitiveType.Float64
        | PureRunValue.Bits32 _ -> TypeRef.Primitive PrimitiveType.Bits32
        | PureRunValue.Bytes _ -> TypeRef.Primitive PrimitiveType.Bytes
        | PureRunValue.StringList _ -> listType PrimitiveType.String
        | PureRunValue.BoolList _ -> listType PrimitiveType.Bool
        | PureRunValue.Int64List _ -> listType PrimitiveType.Int64
        | PureRunValue.StringOption _ -> optionType PrimitiveType.String
        | PureRunValue.BoolOption _ -> optionType PrimitiveType.Bool
        | PureRunValue.Int64Option _ -> optionType PrimitiveType.Int64

    let private sourceType = CSharpBackend.formatType

    /// A String as one input token: "s" and the Base64 of its UTF-16 code units in this machine's
    /// byte order, which the child process shares.

    /// `<A, B>` after a generic type's or variant's C# name; empty for a monomorphic type.
    let private typeArgumentSuffix (typeArguments: TypeRef list) =
        if List.isEmpty typeArguments then "" else "<" + (typeArguments |> List.map sourceType |> String.concat ", ") + ">"

    let private namedParts (named: TypeRef) =
        match named with
        | TypeRef.Named(typeId, typeArguments) -> typeId, typeArguments
        | _ -> invalidOp "A runner value helper was requested for a type that is not named."

    /// A runner helper name for one named type instance; each generic instance gets a numbered name.
    let private instanceName (prefix: string) (existing: System.Collections.Generic.Dictionary<TypeRef, string>) (named: TypeRef) =
        let typeId, typeArguments = namedParts named
        prefix + CSharpBackendNames.typeName typeId + (if List.isEmpty typeArguments then "" else "_" + string existing.Count)
    let private textToken (text: string) =
        "s" + Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan()).ToArray())

    /// Appends a run value's input tokens in the order the generated decoder reads them. Arguments and
    /// fake scripts reach the child process as this text instead of being compiled into the program,
    /// so every call of one entry over one snapshot builds the same source. Product and Union fields
    /// follow canonical field order; a Union starts with its variant's index in canonical order.
    let rec private inputTokens (types: SemanticType list) (tokens: ResizeArray<string>) value =
        let add (token: string) = tokens.Add token
        let items (values: PureRunValue list) =
            add (string values.Length)
            values |> List.iter (inputTokens types tokens)
        let option (value: PureRunValue option) =
            match value with
            | None -> add "0"
            | Some item ->
                add "1"
                inputTokens types tokens item
        let result isOk payload =
            add (if isOk then "1" else "0")
            inputTokens types tokens payload
        let fields (values: (FieldId * PureRunValue) list) =
            values |> List.sortBy (fst >> StableId.value) |> List.iter (snd >> inputTokens types tokens)
        match value with
        | PureRunValue.Unit -> add "u"
        | PureRunValue.String text -> add (textToken text)
        | PureRunValue.Bool value -> add (if value then "1" else "0")
        | PureRunValue.Int64 value -> add (value.ToString(CultureInfo.InvariantCulture))
        | PureRunValue.Float64 bits -> add ((Float64Bits.value bits).ToString("x16", CultureInfo.InvariantCulture))
        | PureRunValue.Bits32 value -> add (value.ToString(CultureInfo.InvariantCulture))
        | PureRunValue.Bytes value -> add (textToken (String(value |> Array.map char)))
        | PureRunValue.StringList values -> items (List.map PureRunValue.String values)
        | PureRunValue.BoolList values -> items (List.map PureRunValue.Bool values)
        | PureRunValue.Int64List values -> items (List.map PureRunValue.Int64 values)
        | PureRunValue.StringOption value -> option (Option.map PureRunValue.String value)
        | PureRunValue.BoolOption value -> option (Option.map PureRunValue.Bool value)
        | PureRunValue.Int64Option value -> option (Option.map PureRunValue.Int64 value)
        | PureRunValue.ListValue(_, values) -> items values
        | PureRunValue.SetValue(_, values) -> items values
        | PureRunValue.MapValue(_, _, entries) ->
            add (string entries.Length)
            entries |> List.iter (fun (key, value) -> inputTokens types tokens key; inputTokens types tokens value)
        | PureRunValue.OptionValue(_, value) -> option value
        | PureRunValue.ResultOk(_, payload) -> result true (scalarValue payload)
        | PureRunValue.ResultErr(_, payload) -> result false (scalarValue payload)
        | PureRunValue.ResultValue(_, _, isOk, payload) -> result isOk payload
        | PureRunValue.Product(_, _, values) -> fields values
        | PureRunValue.Union(typeId, _, variantId, values) ->
            let variants = types |> List.tryFind (fun item -> item.Id = typeId) |> Option.bind (fun item ->
                match item.Kind with
                | SemanticTypeKind.Union variants -> Some(variants |> List.sortBy (fun variant -> StableId.value variant.Id))
                | _ -> None)
            add (string (variants |> Option.get |> List.findIndex (fun variant -> variant.Id = variantId)))
            fields values
        | PureRunValue.FakeCapability _
        | PureRunValue.HostCapability _ -> invalidOp "Capability bindings are not input values."

    /// Appends a fake binding's script: per operation of the capability in canonical order, whether it
    /// is keyed, then its queues. A keyed case carries its complete arguments as one key token, the
    /// input tokens of each argument in canonical parameter order joined by commas, which the child
    /// compares with the same encoding of the actual call arguments.
    let private fakeInput types (operations: CapabilityOperationId list) (script: PureRunFakeOperation list) (tokens: ResizeArray<string>) =
        let rec response item =
            match item with
            | PureRunFakeResponse.Return value ->
                tokens.Add "0"
                inputTokens types tokens value
            | PureRunFakeResponse.Throw message ->
                tokens.Add "1"
                tokens.Add(textToken message)
            | PureRunFakeResponse.AwaitCancellation value ->
                tokens.Add "2"
                inputTokens types tokens value
            | PureRunFakeResponse.Delayed(delay, inner) ->
                tokens.Add "3"
                tokens.Add(delay.ToString(CultureInfo.InvariantCulture))
                response inner
        let queue (responses: PureRunFakeResponse list) =
            tokens.Add(string responses.Length)
            List.iter response responses
        for operationId in operations do
            match script |> List.tryFind (fun item -> item.OperationId = operationId) with
            | Some { Cases = Some cases } ->
                tokens.Add "1"
                tokens.Add(string cases.Length)
                for item in cases do
                    let key = ResizeArray<string>()
                    item.Arguments |> List.sortBy (fst >> StableId.value) |> List.iter (snd >> inputTokens types key)
                    tokens.Add("k" + String.Join(",", key))
                    queue item.Responses
            | Some item ->
                tokens.Add "0"
                tokens.Add "1"
                queue item.Responses
            | None ->
                tokens.Add "0"
                tokens.Add "1"
                tokens.Add "0"

    let private diagnostic code message = PureRunError.Unsupported(code, message)

    /// Reads a String the runner reported as a text token (see `textToken`). Any sequence of UTF-16
    /// code units, including lone surrogates, survives the round trip.
    let private tokenText (item: JsonElement) =
        if item.ValueKind <> JsonValueKind.String then None else
            let token = item.GetString()
            if isNull token || not (token.StartsWith("s", StringComparison.Ordinal)) then None else
                try
                    let bytes = Convert.FromBase64String(token.Substring 1)
                    if bytes.Length % 2 <> 0 then None
                    else Some(String(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(ReadOnlySpan<byte>(bytes))))
                with :? FormatException -> None

    let private children = function
        | PureRunValue.ListValue(_, items) -> items
        | PureRunValue.MapValue(_, _, entries) -> entries |> List.collect (fun (key, value) -> [ key; value ])
        | PureRunValue.SetValue(_, items) -> items
        | PureRunValue.OptionValue(_, item) -> Option.toList item
        | PureRunValue.Product(_, _, fields)
        | PureRunValue.Union(_, _, _, fields) -> fields |> List.map snd
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
            | PureRunValue.MapValue(_, _, entries) -> entries.Length > 4096
            | PureRunValue.SetValue(_, items) -> items.Length > 4096
            | PureRunValue.Product(_, _, fields)
            | PureRunValue.Union(_, _, _, fields) -> fields.Length > 256
            | PureRunValue.FakeCapability(_, operations) ->
                operations.Length > 256 || operations |> List.exists (fun operation -> (PureRunFakeOperation.responses operation).Length > 256 || (operation.Cases |> Option.exists (fun cases -> cases.Length > 256 || cases |> List.exists (fun item -> item.Arguments.Length > 256))))
            | PureRunValue.StringList items ->
                items.Length > 4096 || (items |> List.sumBy (fun text -> if isNull text then 65537L else int64 text.Length)) > 65536L
            | PureRunValue.BoolList items -> items.Length > 4096
            | PureRunValue.Int64List items -> items.Length > 4096
            | _ -> false
        local || (children value |> List.exists oversized)

    /// Runs a process and returns its exit code with bounded output (see `ZigTools.launch`).
    let private launchProcess executable phase workingDirectory args timeoutMs =
        ZigTools.launch executable phase workingDirectory args timeoutMs |> Result.mapError (fun (code, message) -> diagnostic code message)

    let private launchRaw phase workingDirectory args timeoutMs = launchProcess "dotnet" phase workingDirectory args timeoutMs

    let private processFailure output errors = ZigTools.processFailure output errors |> fun (code, message) -> diagnostic code message

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
                    firstDifference (step fieldId :: path) left right)
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
            | PureRunValue.Product(typeId, _, left), PureRunValue.Product(_, _, right) ->
                fields (fun fieldId -> PureRunPathStep.Field(typeId, fieldId)) left right
            | PureRunValue.Union(typeId, _, leftVariant, left), PureRunValue.Union(_, _, rightVariant, right) when leftVariant = rightVariant ->
                fields (fun fieldId -> PureRunPathStep.Field(typeId, fieldId)) left right
            | _ ->
                match items expected, items actual, payload expected, payload actual, result expected, result actual with
                | Some left, Some right, _, _, _, _ ->
                    List.zip (List.truncate right.Length left) (List.truncate left.Length right)
                    |> List.indexed
                    |> List.tryPick (fun (index, (left, right)) ->
                        firstDifference (PureRunPathStep.Item index :: path) left right)
                    |> Option.orElse (here ())
                | _, _, Some(Some left), Some(Some right), _, _ -> firstDifference (PureRunPathStep.SomeValue :: path) left right
                | _, _, _, _, Some(leftOk, left), Some(rightOk, right) when leftOk = rightOk ->
                    firstDifference ((if leftOk then PureRunPathStep.OkValue else PureRunPathStep.ErrValue) :: path) left right
                | _ -> here ()

    /// Locates the first difference between an expectation and an actual value, if any.
    let mismatch expected actual = firstDifference [] expected actual

    let private scalarKindOfType = function
        | TypeRef.Primitive PrimitiveType.String -> Some PureRunScalarKind.String
        | TypeRef.Primitive PrimitiveType.Bool -> Some PureRunScalarKind.Bool
        | TypeRef.Primitive PrimitiveType.Int64 -> Some PureRunScalarKind.Int64
        | _ -> None

    let private supportedScalar itemType = scalarKindOfType itemType |> Option.isSome

    /// Unit, Float64, Bits32 and Bytes are leaves without a flat container form, so their containers use the nested form.
    let private supportedLeaf itemType =
        supportedScalar itemType
        || itemType = TypeRef.Primitive PrimitiveType.Unit
        || itemType = TypeRef.Primitive PrimitiveType.Float64
        || itemType = TypeRef.Primitive PrimitiveType.Bits32
        || itemType = TypeRef.Primitive PrimitiveType.Bytes

    /// A Product's fields in ID order, at the given type arguments of a generic Product.
    let private productFields (types: SemanticType list) typeId typeArguments =
        types |> List.tryFind (fun item -> item.Id = typeId)
        |> Option.bind (fun item ->
            match item.Kind with
            | SemanticTypeKind.Product fields ->
                Some(fields
                     |> List.sortBy (fun field -> StableId.value field.Id)
                     |> List.map (fun field -> { field with Type = TypeParameters.instantiate item typeArguments field.Type }))
            | _ -> None)

    /// A Union's variants and their fields in ID order, at the given type arguments of a generic Union.
    let private unionVariants (types: SemanticType list) typeId typeArguments =
        types |> List.tryFind (fun item -> item.Id = typeId)
        |> Option.bind (fun item ->
            match item.Kind with
            | SemanticTypeKind.Union variants ->
                Some(variants |> List.sortBy (fun variant -> StableId.value variant.Id)
                     |> List.map (fun variant ->
                         { variant with
                             Fields =
                                 variant.Fields
                                 |> List.sortBy (fun field -> StableId.value field.Id)
                                 |> List.map (fun field -> { field with Type = TypeParameters.instantiate item typeArguments field.Type }) }))
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
        let visiting = System.Collections.Generic.HashSet<TypeRef>(HashIdentity.Structural)
        let rec cost depth itemType =
            match itemType with
            | _ when supportedLeaf itemType -> Some 1L
            | TypeRef.Named _ when visiting.Contains itemType -> Some 1L
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
                        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) -> total [ keyType; valueType ]
                        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ item ]) -> total [ item ]
                        | TypeRef.Named(typeId, typeArguments) when visiting.Add itemType ->
                            try
                                match productFields types typeId typeArguments, unionVariants types typeId typeArguments with
                                | Some fields, _ when fields.Length <= 256 -> total (fields |> List.map (fun field -> field.Type))
                                | None, Some variants when not (List.isEmpty variants) && variants.Length <= 256
                                                           && variants |> List.forall (fun variant -> variant.Fields.Length <= 256) ->
                                    total (variants |> List.collect (fun variant -> variant.Fields |> List.map (fun field -> field.Type)))
                                | _ -> None
                            finally visiting.Remove itemType |> ignore
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
        | PureRunValue.Product(typeId, typeArguments, fields) when not (validFields (productFields types typeId typeArguments) fields) -> false
        | _ -> children value |> List.forall (validProduct types)

    let rec private validUnion types value =
        match value with
        | PureRunValue.Union(typeId, typeArguments, variantId, fields) ->
            let declared = unionVariants types typeId typeArguments
                           |> Option.bind (List.tryFind (fun variant -> variant.Id = variantId))
                           |> Option.map (fun variant -> variant.Fields)
            validFields declared fields && (children value |> List.forall (validUnion types))
        | _ -> children value |> List.forall (validUnion types)

    let rec private canonicalValue = function
        // Accept typed scalar inputs without changing the existing scalar response contract.
        | PureRunValue.ListValue(TypeRef.Primitive PrimitiveType.String, items)
            when items |> List.forall (function PureRunValue.String _ -> true | _ -> false) ->
            PureRunValue.StringList(items |> List.choose (function PureRunValue.String value -> Some value | _ -> None))
        | PureRunValue.ListValue(TypeRef.Primitive PrimitiveType.Bool, items)
            when items |> List.forall (function PureRunValue.Bool _ -> true | _ -> false) ->
            PureRunValue.BoolList(items |> List.choose (function PureRunValue.Bool value -> Some value | _ -> None))
        | PureRunValue.ListValue(TypeRef.Primitive PrimitiveType.Int64, items)
            when items |> List.forall (function PureRunValue.Int64 _ -> true | _ -> false) ->
            PureRunValue.Int64List(items |> List.choose (function PureRunValue.Int64 value -> Some value | _ -> None))
        | PureRunValue.OptionValue(TypeRef.Primitive PrimitiveType.String, None) -> PureRunValue.StringOption None
        | PureRunValue.OptionValue(TypeRef.Primitive PrimitiveType.String, Some(PureRunValue.String value)) -> PureRunValue.StringOption(Some value)
        | PureRunValue.OptionValue(TypeRef.Primitive PrimitiveType.Bool, None) -> PureRunValue.BoolOption None
        | PureRunValue.OptionValue(TypeRef.Primitive PrimitiveType.Bool, Some(PureRunValue.Bool value)) -> PureRunValue.BoolOption(Some value)
        | PureRunValue.OptionValue(TypeRef.Primitive PrimitiveType.Int64, None) -> PureRunValue.Int64Option None
        | PureRunValue.OptionValue(TypeRef.Primitive PrimitiveType.Int64, Some(PureRunValue.Int64 value)) -> PureRunValue.Int64Option(Some value)
        | PureRunValue.ListValue(itemType, items) -> PureRunValue.ListValue(itemType, List.map canonicalValue items)
        | PureRunValue.MapValue(keyType, valueType, entries) ->
            PureRunValue.MapValue(keyType, valueType, entries |> List.map (fun (key, value) -> canonicalValue key, canonicalValue value))
        | PureRunValue.SetValue(itemType, items) -> PureRunValue.SetValue(itemType, List.map canonicalValue items)
        | PureRunValue.OptionValue(itemType, item) -> PureRunValue.OptionValue(itemType, Option.map canonicalValue item)
        | PureRunValue.Union(typeId, typeArguments, variantId, fields) ->
            PureRunValue.Union(typeId, typeArguments, variantId, fields |> List.map (fun (fieldId, item) -> fieldId, canonicalValue item)
                                                  |> List.sortBy (fst >> StableId.value))
        | PureRunValue.Product(typeId, typeArguments, fields) ->
            PureRunValue.Product(typeId, typeArguments, fields |> List.map (fun (fieldId, item) -> fieldId, canonicalValue item)
                                         |> List.sortBy (fst >> StableId.value))
        | PureRunValue.ResultValue(okType, errorType, isOk, payload) ->
            let scalar =
                match payload with
                | PureRunValue.String value -> Some(PureRunScalar.String value)
                | PureRunValue.Bool value -> Some(PureRunScalar.Bool value)
                | PureRunValue.Int64 value -> Some(PureRunScalar.Int64 value)
                | _ -> None
            match scalarKindOfType okType, scalarKindOfType errorType, scalar with
            | Some okKind, Some errorKind, Some value when scalarKind value = (if isOk then okKind else errorKind) ->
                if isOk then PureRunValue.ResultOk(errorKind, value) else PureRunValue.ResultErr(okKind, value)
            | _ -> PureRunValue.ResultValue(okType, errorType, isOk, canonicalValue payload)
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
            tokenText item |> Option.map PureRunScalar.String
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
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]), JsonValueKind.Array ->
                let items = value.EnumerateArray() |> Seq.toList
                if items.Length > 4096 then Error(diagnostic "RunInvalidResult" "Invalid container result size.")
                else
                    items
                    |> List.fold (fun state item ->
                        state |> Result.bind (fun acc -> parseElement types itemType item |> Result.map (fun parsed -> parsed :: acc))) (Ok [])
                    |> Result.map (fun items -> PureRunValue.SetValue(itemType, List.rev items))
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]), JsonValueKind.Array ->
                let pairs = value.EnumerateArray() |> Seq.toList
                if pairs.Length > 4096 then Error(diagnostic "RunInvalidResult" "Invalid container result size.")
                elif pairs |> List.exists (fun pair -> pair.ValueKind <> JsonValueKind.Array || pair.GetArrayLength() <> 2) then
                    Error(diagnostic "RunInvalidResult" "A Map result entry is not a key and value pair.")
                else
                    pairs
                    |> List.fold (fun state pair ->
                        state |> Result.bind (fun acc ->
                            parseElement types keyType pair[0] |> Result.bind (fun key ->
                                parseElement types valueType pair[1] |> Result.map (fun item -> (key, item) :: acc)))) (Ok [])
                    |> Result.map (fun entries -> PureRunValue.MapValue(keyType, valueType, List.rev entries))
            | TypeRef.Named(typeId, typeArguments), JsonValueKind.Array ->
                match productFields types typeId typeArguments with
                | Some fields -> parseFields types fields value |> Result.map (fun parsed -> PureRunValue.Product(typeId, typeArguments, parsed))
                | None ->
                    let variant =
                        if value.GetArrayLength() <> 2 || value[0].ValueKind <> JsonValueKind.String then None
                        else unionVariants types typeId typeArguments
                             |> Option.bind (List.tryFind (fun variant -> StableId.value variant.Id = value[0].GetString()))
                    match variant with
                    | Some variant -> parseFields types variant.Fields value[1]
                                      |> Result.map (fun parsed -> PureRunValue.Union(typeId, typeArguments, variant.Id, parsed))
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
                    match tokenText item with
                    | Some text -> Ok(PureRunValue.StringOption(Some text))
                    | None -> Error(diagnostic "RunInvalidResult" "Invalid String Option result.")
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
                        let texts = items |> List.map tokenText
                        if texts |> List.exists Option.isNone then
                            Error(diagnostic "RunInvalidResult" "Invalid String List result.")
                        else Ok(PureRunValue.StringList(texts |> List.map Option.get))
                    | PrimitiveType.Bool when items |> List.forall (fun item -> item.ValueKind = JsonValueKind.True || item.ValueKind = JsonValueKind.False) ->
                        Ok(PureRunValue.BoolList(items |> List.map (fun item -> item.GetBoolean())))
                    | PrimitiveType.Int64 when items |> List.forall (fun item -> item.ValueKind = JsonValueKind.Number && fst (item.TryGetInt64())) ->
                        Ok(PureRunValue.Int64List(items |> List.map (fun item -> item.GetInt64())))
                    | _ -> Error(diagnostic "RunInvalidResult" "A List result item has the wrong type.")
            | TypeRef.Primitive PrimitiveType.Unit, JsonValueKind.String when value.GetString() = "unit" ->
                Ok PureRunValue.Unit
            // The runner reports a Float64 as its 16 hexadecimal bit digits, so no value is rounded or lost.
            | TypeRef.Primitive PrimitiveType.Float64, JsonValueKind.String ->
                match Float64Bits.tryParseHex (value.GetString()) with
                | Ok bits -> Ok(PureRunValue.Float64 bits)
                | Error _ -> Error(diagnostic "RunInvalidResult" "The result is not Float64 bits.")
            | TypeRef.Primitive PrimitiveType.Bits32, JsonValueKind.Number ->
                match value.TryGetUInt32() with
                | true, bits -> Ok(PureRunValue.Bits32 bits)
                | false, _ -> Error(diagnostic "RunInvalidResult" "The result is not a Bits32.")
            // The runner reports Bytes as the String token of its latin-1 form.
            | TypeRef.Primitive PrimitiveType.Bytes, JsonValueKind.String ->
                match tokenText value with
                | Some text when text |> Seq.forall (fun unit -> unit <= 'ÿ') -> Ok(PureRunValue.Bytes(text |> Seq.map byte |> Seq.toArray))
                | _ -> Error(diagnostic "RunInvalidResult" "The result is not Bytes.")
            | TypeRef.Primitive PrimitiveType.String, JsonValueKind.String ->
                match tokenText value with
                | Some text -> Ok(PureRunValue.String text)
                | None -> Error(diagnostic "RunInvalidResult" "The result is not a String.")
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
        | Exception of kind: string * exceptionType: string * message: string * frames: string list * calls: PureRunCall list * exhausted: bool

    let private parseEnvelope types resultType (sites: CallSite list) (output: string) =
        let invalid () = Error(diagnostic "RunInvalidResult" "The process did not return one JSON result.")
        let collect (items: Result<'T, PureRunError> list) =
            List.foldBack (fun item state -> state |> Result.bind (fun rest -> item |> Result.map (fun value -> value :: rest))) items (Ok [])
        let supported = typeSupport types
        let recorded (operation: ExecutionCapabilityOperation) = operation.Parameters |> List.filter (fun parameter -> supported 0 parameter.Type)
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
                    | Some site when call[1].ValueKind = JsonValueKind.Array && call[1].GetArrayLength() = (recorded site.Operation).Length ->
                        recorded site.Operation
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
                | Some "exception", 7 when (text root[1]).IsSome && (tokenText root[2]).IsSome && root[3].ValueKind = JsonValueKind.Array
                                           && (root[5].ValueKind = JsonValueKind.True || root[5].ValueKind = JsonValueKind.False)
                                           && (text root[6]).IsSome ->
                    let frames = root[3].EnumerateArray() |> Seq.choose text |> Seq.toList
                    parseCalls root[4]
                    |> Result.map (fun calls -> Envelope.Exception(root[6].GetString(), root[1].GetString(), (tokenText root[2]).Value, frames, calls, root[5].GetBoolean()))
                | _ -> invalid ()
        with :? JsonException -> invalid ()

    let rec private validContainers (supported: int -> TypeRef -> bool) value =
        let local =
            match value with
            | PureRunValue.ListValue(itemType, items) ->
                supported 1 itemType
                && (items |> List.forall (fun item -> valueType item = itemType))
            | PureRunValue.OptionValue(itemType, item) ->
                supported 1 itemType
                && (item |> Option.forall (fun value -> valueType value = itemType))
            | PureRunValue.ResultValue(okType, errorType, isOk, payload) ->
                supported 1 okType && supported 1 errorType
                && valueType payload = (if isOk then okType else errorType)
            | PureRunValue.SetValue(itemType, items) ->
                supported 1 itemType
                && (items |> List.forall (fun item -> valueType item = itemType))
                && (items |> List.map canonicalValue |> List.distinct |> List.length) = items.Length
            | PureRunValue.MapValue(keyType, valueType', entries) ->
                supported 1 keyType && supported 1 valueType'
                && (entries |> List.forall (fun (key, item) -> valueType key = keyType && valueType item = valueType'))
                // Keys compare in canonical form, so Product fields listed in another order are the same key.
                && (entries |> List.map (fst >> canonicalValue) |> List.distinct |> List.length) = entries.Length
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
            | PureRunValue.Int64 _
            | PureRunValue.Float64 _
            | PureRunValue.Bits32 _ -> remaining >= 0
            | PureRunValue.Bytes _ -> remaining >= 0
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
                    match op.Cases with
                    | Some cases -> cases |> List.sumBy (fun item -> 1 + item.Arguments.Length + item.Responses.Length)
                    | None -> op.Responses |> List.sumBy (fun response ->
                        match PureRunFakeResponse.immediate response with
                        | PureRunFakeResponse.Throw _ -> 1
                        | _ -> 0)))
                && (children value |> List.forall (visit (depth + 1)))
            | _ -> children value |> List.forall (visit (depth + 1))
        visit 0 value

    let rec private containsFake value =
        children value |> List.exists (fun child ->
            match child with
            | PureRunValue.FakeCapability _
            | PureRunValue.HostCapability _ -> true
            | _ -> containsFake child)

    /// Diagnose the first semantic input defect without changing runner acceptance rules.
    /// Walk only bounded values, in declaration order, so reordered fields give the same path.
    let private inputIssueMatching category (declarations: AuthoringDeclarations) functionId arguments expected =
        let typeName id = declarations.Types |> List.tryFind (fun t -> t.Id = id) |> Option.map (fun t -> t.DisplayName) |> Option.defaultValue (StableId.value id)
        let issue code path expected actual reason =
            if category |> Option.forall ((=) code) then Some { Path = path; ExpectedType = expected; ActualType = actual; Reason = reason } else None
        let rec walk mismatchCode path declared value =
            let actual = valueType value
            let mismatch = if declared <> actual then issue mismatchCode path (Some declared) (Some actual) "The value type differs from the declared type." else None
            if mismatch.IsSome then mismatch
            else
                let fields shapeCode path typeId typeArguments (declared: SemanticField list option) (fields: (FieldId * PureRunValue) list) =
                    match declared with
                    | None -> issue shapeCode path (Some(TypeRef.Named(typeId, typeArguments))) (Some actual) "The Product type or Union variant is not declared."
                    | Some declared ->
                        let known = declared |> List.map (fun f -> f.Id) |> Set.ofList
                        match fields |> List.tryFind (fun (id, _) -> not (Set.contains id known)) with
                        | Some(id, value) -> issue shapeCode (path @ [ "field:" + StableId.value id ]) None (Some(valueType value)) "This field does not belong to the selected constructor."
                        | None ->
                            declared |> List.tryPick (fun field ->
                                let path = path @ [ field.DisplayName ]
                                match fields |> List.filter (fun (id, _) -> id = field.Id) with
                                | [] -> issue shapeCode path (Some field.Type) None "A required field is missing."
                                | [ (_, value) ] -> walk shapeCode path field.Type value
                                | _ -> issue shapeCode path (Some field.Type) None "A field is supplied more than once.")
                let items path itemType values = values |> List.mapi (fun index value -> index, value) |> List.tryPick (fun (index, value) -> walk "RunContainerTypeMismatch" (path @ [ $"items[{index}]" ]) itemType value)
                match value with
                | PureRunValue.FakeCapability(capabilityId, operations) ->
                    let capability = declarations.Capabilities |> List.tryFind (fun item -> item.Id = capabilityId)
                    operations |> List.mapi (fun index operation -> index, operation) |> List.tryPick (fun (operationIndex, operation) ->
                        let declaration = capability |> Option.bind (fun cap -> cap.Operations |> List.tryFind (fun item -> item.Id = operation.OperationId))
                        declaration |> Option.bind (fun declaration ->
                            let operationPath = path @ [ $"operations[{operationIndex}]" ]
                            let responses path values =
                                values |> List.mapi (fun index response -> index, response) |> List.tryPick (fun (responseIndex, response) ->
                                    match PureRunFakeResponse.immediate response with
                                    | PureRunFakeResponse.Return value
                                    | PureRunFakeResponse.AwaitCancellation value ->
                                        walk "RunBindingShapeMismatch" (path @ [ $"responses[{responseIndex}]"; "value" ]) declaration.ResultType value
                                    | PureRunFakeResponse.Throw _
                                    | PureRunFakeResponse.Delayed _ -> None)
                            match operation.Cases with
                            | None -> responses operationPath operation.Responses
                            | Some cases ->
                                cases |> List.mapi (fun index item -> index, item) |> List.tryPick (fun (caseIndex, item) ->
                                    let casePath = operationPath @ [ $"cases[{caseIndex}]" ]
                                    item.Arguments |> List.mapi (fun index argument -> index, argument) |> List.tryPick (fun (argumentIndex, (id, value)) ->
                                        declaration.Parameters |> List.tryFind (fun parameter -> parameter.Id = id)
                                        |> Option.bind (fun parameter -> walk "RunBindingShapeMismatch" (casePath @ [ $"arguments[{argumentIndex}]"; "value" ]) parameter.Type value))
                                    |> Option.orElseWith (fun () -> responses casePath item.Responses))))
                | PureRunValue.Product(typeId, typeArguments, values) -> fields "RunProductShapeMismatch" (path @ [ typeName typeId ]) typeId typeArguments (productFields declarations.Types typeId typeArguments) values
                | PureRunValue.Union(typeId, typeArguments, variantId, values) ->
                    let variant = unionVariants declarations.Types typeId typeArguments |> Option.bind (List.tryFind (fun v -> v.Id = variantId))
                    let name = variant |> Option.map (fun v -> v.DisplayName) |> Option.defaultValue ("variant:" + StableId.value variantId)
                    fields "RunUnionShapeMismatch" (path @ [ typeName typeId; name ]) typeId typeArguments (variant |> Option.map (fun v -> v.Fields)) values
                | PureRunValue.ListValue(itemType, values) ->
                    items path itemType values
                | PureRunValue.OptionValue(itemType, value) ->
                    value |> Option.bind (walk "RunContainerTypeMismatch" (path @ [ "some" ]) itemType)
                | PureRunValue.SetValue(itemType, values) ->
                    items path itemType values |> Option.orElseWith (fun () ->
                        let canonical = List.map canonicalValue values
                        canonical |> List.mapi (fun index value -> index, value) |> List.tryPick (fun (index, value) ->
                            if canonical |> List.take index |> List.contains value then issue "RunContainerTypeMismatch" (path @ [ $"items[{index}]" ]) (Some itemType) (Some itemType) "Set items must be distinct." else None))
                | PureRunValue.MapValue(keyType, itemType, entries) ->
                    let keys = entries |> List.map (fst >> canonicalValue)
                    entries |> List.mapi (fun index pair -> index, pair) |> List.tryPick (fun (index, (key, value)) ->
                        let keyPath = path @ [ $"entries[{index}].key" ]
                        walk "RunContainerTypeMismatch" keyPath keyType key
                        |> Option.orElseWith (fun () -> walk "RunContainerTypeMismatch" (path @ [ $"entries[{index}].value" ]) itemType value)
                        |> Option.orElseWith (fun () -> if keys |> List.take index |> List.contains (canonicalValue key) then issue "RunContainerTypeMismatch" keyPath (Some keyType) (Some keyType) "Map keys must be distinct." else None))
                | PureRunValue.ResultValue(okType, errorType, isOk, value) ->
                    walk "RunContainerTypeMismatch" (path @ [ if isOk then "ok" else "err" ]) (if isOk then okType else errorType) value
                | _ -> None
        if arguments @ Option.toList expected |> List.exists (boundedValue >> not) then None
        else
            declarations.Functions |> List.tryFind (fun f -> f.Id = functionId) |> Option.bind (fun fn ->
                if fn.Parameters.Length <> arguments.Length then None
                else
                    List.zip fn.Parameters arguments |> List.tryPick (fun (parameter, value) -> walk "RunArgumentTypeMismatch" [ "arguments"; parameter.DisplayName ] parameter.Type value)
                    |> Option.orElseWith (fun () -> expected |> Option.bind (walk "RunExpectedTypeMismatch" [ "expected" ] fn.ResultType)))

    let internal inputIssue declarations functionId arguments expected =
        inputIssueMatching None declarations functionId arguments expected

    /// Diagnostics follow the runner's selected validation category, not argument order.
    /// Entry/runtime/support failures must never acquire unrelated input details.
    let internal inputIssueForError declarations functionId arguments expected = function
        | PureRunError.Unsupported(code, _) when
            [ "RunArgumentTypeMismatch"; "RunProductShapeMismatch"; "RunUnionShapeMismatch"; "RunContainerTypeMismatch"; "RunExpectedTypeMismatch" ] |> List.contains code ->
            inputIssueMatching (Some code) declarations functionId arguments expected
        | _ -> None

    let private enrichInputError declarations functionId arguments expected = function
        | PureRunError.Unsupported(code, message) as error ->
            match inputIssueForError declarations functionId arguments expected error with
            | None -> error
            | Some issue -> diagnostic code (message + " At " + String.concat "." issue.Path + ": " + issue.Reason)
        | error -> error

    let private hostTypeName =
        System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)

    /// A type that mentions a capability, directly or through type arguments. Such values (handles a
    /// host returns) are not run values: calls leave out arguments of these types, and a fake can only
    /// throw from an operation that returns one.
    let rec private bearsCapability itemType =
        match itemType with
        | TypeRef.Capability _ -> true
        | TypeRef.BuiltinGeneric(_, arguments)
        | TypeRef.Named(_, arguments) -> List.exists bearsCapability arguments
        | _ -> false

    /// Checks one binding against its capability declaration. Every operation of a bound capability must
    /// have runner-supported or capability-bearing parameter and result types, because the program may
    /// call any of them and the runner records each call.
    let private fakeProblem (declarations: AuthoringDeclarations) (supported: int -> TypeRef -> bool) value =
        let bindable itemType = supported 0 itemType || bearsCapability itemType
        let capabilityProblem capabilityId =
            match declarations.Capabilities |> List.tryFind (fun item -> item.Id = capabilityId) with
            | None -> Error("RunBindingShapeMismatch", "The binding names an unknown capability.")
            | Some capability when capability.Operations |> List.exists (fun operation ->
                not (bindable operation.ResultType) || operation.Parameters |> List.exists (fun parameter -> not (bindable parameter.Type))) ->
                Error("RunBindingTypeUnsupported", "Every operation of a bound capability needs parameter and result types that runPure values support, or that mention a capability.")
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

    /// The fixed runner classes that read input tokens, write keys and replay fake scripts.
    let private runnerSupport =
        String.concat "" [
            "sealed class RunnerInput\n{\n"
            "    readonly string[] tokens;\n    int next;\n"
            "    public RunnerInput(string text) { tokens = text.Length == 0 ? Array.Empty<string>() : text.Split(' '); }\n"
            "    string Next() => next < tokens.Length ? tokens[next++] : throw new InvalidOperationException(\"Runner input ended early.\");\n"
            "    public NyUnit Unit() { Next(); return NyUnit.Value; }\n"
            "    public bool Bool() => Next() == \"1\";\n"
            "    public int Count() => int.Parse(Next(), NumberStyles.None, CultureInfo.InvariantCulture);\n"
            "    public long Long() => long.Parse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);\n"
            "    public uint UInt() => uint.Parse(Next(), NumberStyles.None, CultureInfo.InvariantCulture);\n"
            "    public double Double() => BitConverter.UInt64BitsToDouble(ulong.Parse(Next(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));\n"
            "    public string String() => RunnerText.FromToken(Next());\n"
            "    public string Key() => Next().Substring(1);\n"
            "    public T[] Items<T>(Func<T> read) { var items = new T[Count()]; for (int index = 0; index < items.Length; index++) items[index] = read(); return items; }\n"
            "}\n"
            "static class RunnerText\n{\n"
            "    public static string FromToken(string token) => new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(Convert.FromBase64String(token.Substring(1)).AsSpan()));\n"
            "    public static string ToToken(string text) => \"s\" + Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan()));\n"
            "}\n"
            "sealed class RunnerKeyOverflow() : Exception(\"The key exceeds every case key.\");\n"
            // A key is the input token form of the call arguments; writing stops once it is longer than every case key.
            "sealed class RunnerKey\n{\n"
            "    readonly StringBuilder text = new();\n    readonly int limit;\n    bool first = true;\n"
            "    RunnerKey(int limit) { this.limit = limit; }\n"
            "    public static string? Of(int limit, Action<RunnerKey> write) { var key = new RunnerKey(limit); try { write(key); } catch (RunnerKeyOverflow) { return null; } return key.text.ToString(); }\n"
            "    public void Add(string token) { if (!first) text.Append(','); first = false; text.Append(token); if (text.Length > limit) throw new RunnerKeyOverflow(); }\n"
            "    public void Add(long value) => Add(value.ToString(CultureInfo.InvariantCulture));\n"
            "    public void Null() => Add(\"null\");\n"
            "    public void String(string? value) { if (value == null) Null(); else if (value.Length > limit) throw new RunnerKeyOverflow(); else Add(RunnerText.ToToken(value)); }\n"
            "    public void Double(double value) => Add((double.IsNaN(value) ? 0x7ff8000000000000UL : BitConverter.DoubleToUInt64Bits(value)).ToString(\"x16\", CultureInfo.InvariantCulture));\n"
            "}\n"
            // Kind: 0 return, 1 throw, 2 await cancellation, 3 delay, 4 no matching case, 5 exhausted.
            "sealed class FakeResponse<T>\n{\n"
            "    public static readonly FakeResponse<T> Unmatched = new() { Kind = 4 };\n"
            "    public static readonly FakeResponse<T> Exhausted = new() { Kind = 5 };\n"
            "    public int Kind;\n    public T Value = default!;\n    public string Message = \"\";\n    public long Delay;\n    public FakeResponse<T>? Inner;\n"
            "    public static FakeResponse<T> Read(RunnerInput input, Func<T> read)\n    {\n"
            "        var response = new FakeResponse<T> { Kind = input.Count() };\n"
            "        switch (response.Kind)\n        {\n"
            "            case 0: case 2: response.Value = read(); break;\n"
            "            case 1: response.Message = input.String(); break;\n"
            "            case 3: response.Delay = input.Long(); response.Inner = Read(input, read); break;\n"
            "            default: throw new InvalidOperationException(\"Invalid runner input.\");\n"
            "        }\n        return response;\n    }\n}\n"
            // An ordered script is one queue; a keyed script has one queue per case.
            "sealed class FakeScript<T>\n{\n"
            "    public readonly bool Keyed;\n    public readonly int KeyLimit;\n"
            "    readonly string[] keys;\n    readonly FakeResponse<T>[][] queues;\n    readonly int[] next;\n"
            "    public FakeScript(RunnerInput input, Func<T> read)\n    {\n"
            "        Keyed = input.Bool();\n        int count = input.Count();\n"
            "        keys = new string[count];\n        queues = new FakeResponse<T>[count][];\n        next = new int[count];\n"
            "        for (int index = 0; index < count; index++)\n        {\n"
            "            if (Keyed) { keys[index] = input.Key(); KeyLimit = Math.Max(KeyLimit, keys[index].Length); }\n"
            "            queues[index] = input.Items(() => FakeResponse<T>.Read(input, read));\n"
            "        }\n    }\n"
            "    public FakeResponse<T> Select(string? key)\n    {\n"
            "        int selected = Keyed ? (key == null ? -1 : Array.IndexOf(keys, key)) : 0;\n"
            "        if (selected < 0) return FakeResponse<T>.Unmatched;\n"
            "        int local = next[selected]++;\n"
            "        return local < queues[selected].Length ? queues[selected][local] : FakeResponse<T>.Exhausted;\n"
            "    }\n}\n" ]

    let private zigExecutable = ZigTools.executable
    let private zigDirectory = ZigTools.directory
    let private zigCache = ZigTools.cache
    let private createPrivateDirectory = ZigTools.createPrivateDirectory

    /// Publish only a completed runner; concurrent sessions own separate build inputs and outputs.
    let internal buildZigRunner compiler workingDirectory cache directory (runner: string) =
        let executable = Path.Combine(directory, "runner")
        if File.Exists executable then Ok()
        else
            createPrivateDirectory cache
            createPrivateDirectory directory
            let token = Guid.NewGuid().ToString("N")
            // The source keeps the name runner.zig so both hosts pass the compiler the same file name.
            let sourceDirectory = Path.Combine(directory, token)
            let source = Path.Combine(sourceDirectory, "runner.zig")
            let partial = executable + "." + token
            try
                createPrivateDirectory sourceDirectory
                File.WriteAllText(source, runner)
                // Every path is absolute, so the start directory only picks the pinned Zig.
                launchProcess compiler "Zig build" workingDirectory
                    [ "build-exe"; source; "-O"; "ReleaseSafe"
                      "--cache-dir"; Path.Combine(cache, "cache"); "-femit-bin=" + partial ] 300000
                |> Result.bind (fun (code, output, errors) ->
                    if code = 0 && File.Exists partial then
                        File.Move(partial, executable, true)
                        Ok()
                    else Error(processFailure output errors))
            finally
                if Directory.Exists sourceDirectory then Directory.Delete(sourceDirectory, true)
                File.Delete partial

    /// A Zig tool (`tools/zig/<name>`), built once per process (see `ZigTools.tool`).
    let zigTool (name: string) = ZigTools.tool name |> Result.mapError (fun (code, message) -> diagnostic code message)

    // The Zig emitter's output by emitter build, entry and IR hash: a repeated run skips the emitter;
    // the emitter check below still runs on every call.
    let private emitCache = Collections.Generic.Dictionary<string, Result<string, string list>>()
    let mutable internal emitCacheHits = 0

    /// The complete runPure runner for an Execution IR text from the Zig emitter (see
    /// `ZigBackend.runner`), or the constructs it does not support. With `NYRWVY_ZIG_EMITTER_CHECK`
    /// set (the extended tests set it), the F# emitter's output on `reference ()`, the F# program of
    /// the same IR, must match byte for byte, also for a cached output.
    let zigEmit (ir: string) (reference: unit -> Result<ExecutionProgram, PureRunError>) (entry: FunctionId option) : Result<Result<string, string list>, PureRunError> =
        zigTool "emitter"
        |> Result.bind (fun emitter ->
            // The tool's path holds the hash of its sources, so a changed emitter misses.
            let key = emitter + "\n" + (entry |> Option.map StableId.value |> Option.defaultValue "-") + "\n" + Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes ir))
            match lock emitCache (fun () -> match emitCache.TryGetValue key with | true, output -> Some output | _ -> None) with
            | Some output ->
                Threading.Interlocked.Increment(&emitCacheHits) |> ignore
                Ok output
            | None ->
                let input = Path.Combine(zigCache, "ir-" + Guid.NewGuid().ToString("N") + ".json")
                (try
                    try
                        File.WriteAllText(input, ir)
                        launchProcess emitter "Zig emitter" zigCache (input :: (entry |> Option.map StableId.value |> Option.toList)) 60000
                    finally File.Delete input
                 with
                 | :? IOException as error -> Error(diagnostic "RunIoFailure" error.Message)
                 | :? UnauthorizedAccessException as error -> Error(diagnostic "RunIoFailure" error.Message))
                |> Result.bind (fun (code, output, errors) ->
                    match code with
                    | 0 -> Ok(Ok output)
                    | 2 ->
                        // A diagnostic can contain TypeRef's multiline display; each line frames one JSON string.
                        try
                            let problems = output.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map JsonSerializer.Deserialize<string>
                            if problems.Length = 0 || Array.exists isNull problems then
                                Error(diagnostic "RunInvalidResult" "The Zig emitter did not return its diagnostics.")
                            else Ok(Error(List.ofArray problems))
                        with :? JsonException -> Error(diagnostic "RunInvalidResult" "The Zig emitter did not return its diagnostics.")
                    | _ -> Error(processFailure output errors))
                |> Result.map (fun output ->
                    lock emitCache (fun () ->
                        // shortcut: a few snapshots' runners at most, cleared whole; an LRU if sessions alternate more.
                        if emitCache.Count >= 8 then emitCache.Clear()
                        emitCache[key] <- output)
                    output))
        |> Result.bind (fun emitted ->
            match Environment.GetEnvironmentVariable "NYRWVY_ZIG_EMITTER_CHECK" with
            | null | "" -> Ok emitted
            | _ ->
                reference ()
                |> Result.bind (fun program ->
                let reference = ZigBackend.runner program entry
                match emitted, reference with
                | Ok zig, Ok fsharp when zig <> fsharp ->
                    let left, right = zig.Split('\n'), fsharp.Split('\n')
                    let index = Seq.zip left right |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min left.Length right.Length)
                    let at (lines: string[]) = if index < lines.Length then lines[index] else "<end>"
                    Error(diagnostic "RunBackendMismatch" $"The Zig emitter differs from the F# emitter at line {index + 1}: Zig `{at left}`, F# `{at right}`.")
                | Ok _, Error problems -> Error(diagnostic "RunBackendMismatch" ("Only the F# emitter rejects the program: " + String.Join(", ", problems) + "."))
                | Error problems, Ok _ -> Error(diagnostic "RunBackendMismatch" ("Only the Zig emitter rejects the program: " + String.Join(", ", problems) + "."))
                | _ -> Ok emitted))

    // Set once `model-reader` could not be built or run: the process then lowers with F#.
    let mutable private loweringUnavailable = 0
    // The Zig lowerings of one snapshot (the last fingerprint seen), by entry ("-" for the whole).
    let private lowered = Collections.Generic.Dictionary<string, string option>()
    let mutable private loweredFingerprint = ""

    /// `model-reader --execution-ir` on the context's snapshot: `Ok(Some ir)`, `Ok None` when the
    /// lowering reports an error (the F# projection then decides), or the reason the tool did not run.
    let private zigLower (context: AuthoringExecutionContext) (entry: FunctionId option) : Result<string option, string> =
        let target = entry |> Option.map StableId.value |> Option.defaultValue "-"
        let cached =
            lock lowered (fun () ->
                if loweredFingerprint <> context.Fingerprint then
                    lowered.Clear()
                    loweredFingerprint <- context.Fingerprint
                match lowered.TryGetValue target with
                | true, ir -> Some ir
                | _ -> None)
        match cached with
        | Some ir -> Ok ir
        | None ->
            // The tool runs outside the lock; two runs of one entry at once both lower it.
            match context.ModelJson () with
            | Error error -> Error $"the snapshot does not serialize: {(Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(error, typeof<SnapshotCodecError>) |> fst).Name}"
            | Ok(json, validated) ->
                // Bytes the Zig validator accepted are not validated again, except under the check,
                // which compares the tool's full readiness with F#'s.
                let check = not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NYRWVY_ZIG_LOWERING_CHECK"))
                let mode = if validated && not check then [ "--execution-ir"; "--validated" ] else [ "--execution-ir" ]
                let stem = Path.Combine(zigCache, "lower-" + Guid.NewGuid().ToString("N"))
                let result =
                    try
                        createPrivateDirectory zigCache
                        File.WriteAllText(stem + ".json", json, Text.UTF8Encoding false)
                        ZigSnapshotReader.run "model-reader" (mode @ [ stem + ".json"; stem + ".out.json"; target ])
                        |> Result.bind (function
                            | [ "ok" ] -> Ok(Some(File.ReadAllText(stem + ".out.json")))
                            | [ line ] when line.StartsWith("error ", StringComparison.Ordinal) -> Ok None
                            | lines -> Error("lines outside the protocol: " + String.Join(" | ", lines)))
                    with
                    | :? IOException as error -> Error("RunIoFailure: " + error.Message)
                    | :? UnauthorizedAccessException as error -> Error("RunIoFailure: " + error.Message)
                for file in [ stem + ".json"; stem + ".out.json" ] do
                    try File.Delete file with _ -> ()
                result |> Result.map (fun ir ->
                    lock lowered (fun () -> if loweredFingerprint = context.Fingerprint then lowered[target] <- ir)
                    ir)

    /// The Execution IR text of `entry` (or of the whole snapshot) from the Zig lowering; `fsharp` is
    /// the same program from the F# lowering, which decides when Zig reports an error, serves when
    /// `model-reader` cannot run (sticky for a build or launch failure, noted once on stderr) and,
    /// with `NYRWVY_ZIG_LOWERING_CHECK` set, must give the same text.
    let private lowerIr context entry (fsharp: unit -> Result<ExecutionProgram, PureRunError>) : Result<string, PureRunError> =
        let check = not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NYRWVY_ZIG_LOWERING_CHECK"))
        let what = match entry with Some id -> $"entry {StableId.value id}" | None -> "the whole snapshot"
        let mismatch (text: string) = Error(diagnostic "RunBackendMismatch" $"The Zig lowering of {what} {text}.")
        let zig = if not check && loweringUnavailable <> 0 then Error "unavailable" else zigLower context entry
        match zig with
        | Error problem when check -> mismatch $"could not run: {problem}"
        | Error problem ->
            // Only a tool that cannot be built or started sticks; anything else belongs to this run.
            let sticky = problem.StartsWith("RunProcessFailure", StringComparison.Ordinal) || problem.StartsWith("RunStartupFailure", StringComparison.Ordinal)
            if sticky && Threading.Interlocked.Exchange(&loweringUnavailable, 1) = 0 then
                eprintfn "nyrwvy: the Zig lowering is unavailable (%s); runPure lowers with F#." problem
            fsharp () |> Result.map ExecutionIrSerializer.serialize
        | Ok None ->
            match fsharp () with
            | Ok program when check -> mismatch "fails where the F# lowering succeeds" |> Result.map (fun () -> ExecutionIrSerializer.serialize program)
            | result -> result |> Result.map ExecutionIrSerializer.serialize
        | Ok(Some ir) when check ->
            match fsharp () with
            | Ok program when ExecutionIrSerializer.serialize program = ir -> Ok ir
            | Ok _ -> mismatch "differs from the F# lowering"
            | Error _ -> mismatch "succeeds where the F# lowering fails"
        | Ok(Some ir) -> Ok ir

    /// Runs the entry on the Zig backend: emits the program and a runner that reads argument tokens
    /// from its command line and prints the same envelope the C# runner does.
    let private runZig
        (context: AuthoringExecutionContext)
        (fn: SemanticFunction)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        : Result<PureRunResult, PureRunError> =
        let declarations = context.Declarations
        let unsupported (problems: string list) =
            Error(diagnostic "RunBackendUnsupported" ("The Zig backend does not yet support: " + String.Join(", ", problems |> List.truncate 8) + "."))
        // The F# programs: the fallback, the reference of the checks, and the source of the errors.
        let reach () = context.Project fn.Id |> Result.mapError PureRunError.SessionFailure
        let whole () =
            match context.ProjectAll |> Option.map (fun project -> project ()) with
            | Some(Ok program) -> Ok program
            | Some(Error error) -> Error(PureRunError.SessionFailure error)
            | None -> Error(diagnostic "RunIoFailure" "No whole program for a proposal's snapshot.")
        // One runner serves every runnable entry of the snapshot, so a session builds once per
        // snapshot; a program the backend cannot emit whole falls back to this entry's reach. A
        // proposal's snapshot, or one whose whole program does not lower, starts from the reach.
        let emitted =
            if not (ZigTools.enabled ()) then
                // The default: the F# lowering and the F# emitter.
                reach ()
                |> Result.map (fun reachProgram ->
                    let first = (if context.ProjectAll.IsNone then None else whole () |> Result.toOption) |> Option.defaultValue reachProgram
                    match ZigBackend.runner first None with
                    | Ok runner -> Ok runner
                    | Error _ -> ZigBackend.runner reachProgram (Some fn.Id))
            else
                lowerIr context (Some fn.Id) reach
                |> Result.bind (fun reachIr ->
                    let wholeIr =
                        if context.ProjectAll.IsNone then None
                        else lowerIr context None whole |> Result.toOption
                    let first, firstReference = match wholeIr with Some ir -> ir, whole | None -> reachIr, reach
                    zigEmit first firstReference None
                    |> Result.bind (function
                        | Ok runner -> Ok(Ok runner)
                        | Error _ -> zigEmit reachIr reach (Some fn.Id)))
        // As on the C# backend, waiting fake responses need a program that uses the concurrency runtime.
        let waitingResponses =
            arguments |> List.exists (function
                | PureRunValue.FakeCapability(_, operations) ->
                    operations |> List.exists (fun operation ->
                        PureRunFakeOperation.responses operation |> List.exists (function
                            | PureRunFakeResponse.AwaitCancellation _
                            | PureRunFakeResponse.Delayed _ -> true
                            | _ -> false))
                | _ -> false)
        match emitted with
        | Error error -> Error error
        | Ok(Error problems) -> unsupported problems
        | Ok(Ok _) when waitingResponses && not (reach () |> Result.map (fun program -> CSharpBackend.usesConcurrencyRuntime (ExecutionCallGraph.forEntry program fn.Id)) |> Result.defaultValue true) ->
            Error(diagnostic "RunBindingShapeMismatch" "awaitCancellation and delayMillis responses need a program that uses ParallelAll, a task construct or CancellationRequested.")
        | Ok(Ok runner) ->
            let hash = Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes runner)).Substring(0, 32)
            let directory = Path.Combine(zigCache, hash)
            let executable = Path.Combine(directory, "runner")
            let tokens = ResizeArray<string>()
            arguments |> List.iter (function
                | PureRunValue.FakeCapability(capabilityId, script) ->
                    let capability = declarations.Capabilities |> List.find (fun item -> item.Id = capabilityId)
                    fakeInput declarations.Types (capability.Operations |> List.map (fun operation -> operation.Id) |> List.sortBy StableId.value) script tokens
                | value -> inputTokens declarations.Types tokens value)
            let sites =
                List.zip fn.Parameters arguments |> List.collect (fun (parameter, value) ->
                    match value with
                    | PureRunValue.FakeCapability(capabilityId, _) ->
                        let capability = declarations.Capabilities |> List.find (fun item -> item.Id = capabilityId)
                        capability.Operations |> List.sortBy (fun operation -> StableId.value operation.Id)
                        |> List.map (fun operation ->
                            // The operation as the Execution IR declares it (`ExecutionIrDeclarations.projectCapability`).
                            let lowered: ExecutionCapabilityOperation =
                                { Id = operation.Id
                                  Parameters = operation.Parameters |> List.map (fun item -> ({ Id = item.Id; Type = item.Type }: ExecutionCapabilityParameter))
                                  ResultType = operation.ResultType
                                  FailureType = operation.FailureType
                                  EffectId = operation.EffectId }
                            { ParameterId = parameter.Id; Operation = lowered })
                    | _ -> [])
            try
                let built = buildZigRunner (zigExecutable ()) zigDirectory.Value zigCache directory runner
                built
                |> Result.bind (fun () -> launchProcess executable "pure function" directory (ZigBackend.functionName fn.Id :: List.ofSeq tokens) 5000)
                |> Result.bind (fun (code, output, errors) ->
                    // A Zig panic (allocation failure) or a crash (stack overflow) ends the run: fatal.
                    if code <> 0 then
                        let panic = errors.Split('\n') |> Array.tryFind (fun line -> line.StartsWith("panic: ", StringComparison.Ordinal))
                        Error(PureRunError.RuntimeFailure
                                { SnapshotId = context.SnapshotId
                                  FunctionId = fn.Id
                                  Kind = "fatal"
                                  ExceptionType = (if panic.IsSome then "ZigPanic" else "ZigCrash")
                                  Message = panic |> Option.map (fun line -> line.Substring 7) |> Option.defaultValue $"The Zig program stopped with exit code {code} (for example a stack overflow)."
                                  Frames = []
                                  BindingExhausted = false
                                  Calls = [] })
                    else
                        parseEnvelope declarations.Types fn.ResultType sites output
                        |> Result.bind (function
                            | Envelope.Exception(kind, exceptionType, message, _, calls, exhausted) ->
                                Error(PureRunError.RuntimeFailure
                                        { SnapshotId = context.SnapshotId
                                          FunctionId = fn.Id
                                          Kind = kind
                                          ExceptionType = exceptionType
                                          Message = message
                                          Frames = []
                                          BindingExhausted = exhausted
                                          Calls = calls })
                            | Envelope.Limit message -> Error(diagnostic "RunProcessFailure" message)
                            | Envelope.Value(actual, calls) ->
                                Ok { SnapshotId = context.SnapshotId
                                     FunctionId = fn.Id
                                     Actual = actual
                                     Expected = expected
                                     Matches = expected |> Option.map ((=) actual)
                                     Mismatch = expected |> Option.bind (fun expected -> mismatch expected actual)
                                     Calls = calls }))
            with
            | :? IOException as error -> Error(diagnostic "RunIoFailure" error.Message)
            | :? UnauthorizedAccessException as error -> Error(diagnostic "RunIoFailure" error.Message)

    let private runVerified
        (context: AuthoringExecutionContext)
        (backend: PureRunBackend)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        (hostSources: (string * string) list)
        : Result<PureRunResult, PureRunError> =
        let declarations = context.Declarations
        let supported = typeSupport declarations.Types
        let arguments = arguments |> List.map canonicalValue
        let expected = expected |> Option.map canonicalValue
        match declarations.Functions |> List.tryFind (fun item -> item.Id = functionId) with
        | None -> Error(diagnostic "RunFunctionNotFound" "The entry function does not exist.")
        | Some fn when not (List.isEmpty (TypeParameters.ofFunction fn)) ->
            Error(diagnostic "RunGenericEntryUnsupported" "A generic function cannot be an entry point; run a concrete function that calls it.")
        | Some fn when fn.ResultType :: (fn.Parameters |> List.map (fun parameter -> parameter.Type)) |> List.exists (TaskTypes.bearing declarations.Types) ->
            Error(diagnostic "RunTaskTypeUnsupported" "Task, OneShot and TaskScope values, directly or inside a declared type, are not runPure values; run a function that waits for its tasks and returns their results.")
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
            Error(diagnostic "RunContainerTypeMismatch" "List/Option item, Set item, Map key and value, and Result payload types must be supported and match every payload, and Set items and Map keys must be distinct. Scalar containers accept typed values or their legacy flat form.")
        | Some fn when not (supported 0 fn.ResultType) ->
            Error(diagnostic "RunResultTypeUnsupported" "Supported results are String, Bool, Int64, Float64, Bits32, Bytes and Unit values and List/Option/Result/Product/Union compositions of them with at most eight composite layers along any non-recursive path; function, capability and type-parameter types are not run values.")
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
        | Some fn when backend = PureRunBackend.Zig ->
            if not (List.isEmpty hostSources) then
                Error(diagnostic "RunBackendUnsupported" "The Zig backend does not yet support host sources.")
            else runZig context fn arguments expected
        | Some fn ->
            // Check only functions reachable from this entry; unrelated parallel functions do not
            // make a sequential host binding require a concurrency assertion.
            let emitted =
                context.Project functionId
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
            match emitted with
            | Error error -> Error(PureRunError.SessionFailure error)
            | Ok(program, _) when
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
            | Ok(program, artifact) ->
                let snapshotId = context.SnapshotId
                let root = Path.Combine(Path.GetTempPath(), "nyrwvy-pure-run-" + Guid.NewGuid().ToString("N"))
                try
                    try
                        Directory.CreateDirectory(root) |> ignore
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
                        // One encoder per named type instance: a generic type gets one per type-argument list.
                        let encoders = System.Collections.Generic.Dictionary<TypeRef, string>(HashIdentity.Structural)
                        let pending = System.Collections.Generic.Queue<TypeRef>()
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
                                | TypeRef.Primitive PrimitiveType.Bytes -> "CheckedString(" + access + ".ToLatin1())"
                                | TypeRef.Primitive PrimitiveType.Float64 ->
                                    "(double.IsNaN(" + access + ") ? 0x7ff8000000000000UL : BitConverter.DoubleToUInt64Bits(" + access + ")).ToString(\"x16\", CultureInfo.InvariantCulture)"
                                | TypeRef.Primitive _ -> "(object)" + access
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) ->
                                    let name, accumulator = fresh (), fresh ()
                                    let child = encode item name depthBase (offset + 1)
                                    layer $"{access}.Fold(new System.Collections.Generic.List<object>(), ({accumulator}, {name}) => {{ if ({accumulator}.Count >= 4096) throw new RunnerException(\"List result exceeds 4096 items.\"); {accumulator}.Add({child}); return {accumulator}; }})"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ item ]) ->
                                    let name, accumulator = fresh (), fresh ()
                                    let child = encode item name depthBase (offset + 1)
                                    layer $"{access}.Fold(new System.Collections.Generic.List<object>(), ({accumulator}, {name}) => {{ if ({accumulator}.Count >= 4096) throw new RunnerException(\"Set result exceeds 4096 items.\"); {accumulator}.Add({child}); return {accumulator}; }})"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) ->
                                    let key, value, accumulator = fresh (), fresh (), fresh ()
                                    let pair = array [ encode keyType key depthBase (offset + 1); encode valueType value depthBase (offset + 1) ]
                                    layer $"{access}.Fold(new System.Collections.Generic.List<object>(), ({accumulator}, {key}, {value}) => {{ if ({accumulator}.Count >= 4096) throw new RunnerException(\"Map result exceeds 4096 entries.\"); {accumulator}.Add({pair}); return {accumulator}; }})"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
                                    let child = encode item (access + ".Value") depthBase (offset + 1)
                                    layer $"({access}.HasValue ? {array [ child ]} : Array.Empty<object>())"
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                    let ok = array [ "true"; encode okType (access + ".OkValue") depthBase (offset + 1) ]
                                    let error = array [ "false"; encode errorType (access + ".ErrorValue") depthBase (offset + 1) ]
                                    layer $"({access}.IsOk ? {ok} : {error})"
                                | TypeRef.Named _ ->
                                    if not (encoders.ContainsKey itemType) then
                                        encoders[itemType] <- instanceName "Encode" encoders itemType
                                        pending.Enqueue itemType
                                    encoders[itemType] + "(" + access + ", " + depth + ")"
                                | _ -> invalidOp "Unsupported output type passed validation."
                            "Count(" + expression + ")"
                        let encodeNamed (named: TypeRef) =
                            let typeId, typeArguments = namedParts named
                            let fields owner (fields: SemanticField list) =
                                fields |> List.map (fun field -> encode field.Type (owner + "." + CSharpBackendNames.fieldName field.Id) "depth" 1)
                                |> fun items -> "new object[] { " + String.concat ", " items + " }"
                            let body =
                                match productFields declarations.Types typeId typeArguments with
                                | Some declared -> fields "value" declared
                                | None ->
                                    let cases = unionVariants declarations.Types typeId typeArguments |> Option.get
                                    let fallback = "throw new RunnerException(\"Unknown or null Union variant.\")"
                                    List.foldBack (fun (variant: SemanticVariant) rest ->
                                        let name = fresh ()
                                        let variantName = CSharpBackendNames.variantName variant.Id + typeArgumentSuffix typeArguments
                                        let tag = JsonSerializer.Serialize(StableId.value variant.Id)
                                        $"(value is {variantName} {name} ? new object[] {{ {tag}, {fields name variant.Fields} }} : {rest})") cases fallback
                            $"object {encoders[named]}({sourceType named} value, int depth) => depth >= {layerLimit} ? throw new RunnerException(\"Result exceeds {layerLimit} composite layers.\") : {body};\n"
                        let encoded = encode fn.ResultType "result" "0" 0
                        let callArms =
                            sites |> List.mapi (fun site call ->
                                let arguments =
                                    call.Operation.Parameters |> List.mapi (fun index parameter -> index, parameter)
                                    |> List.filter (fun (_, parameter) -> supported 0 parameter.Type)
                                    |> List.map (fun (index, parameter) -> encode parameter.Type $"(({sourceType parameter.Type})arguments[{index}])" "0" 0)
                                    |> String.concat ", "
                                $"    {site} => new object[] {{ {site}, new object[] {{ {arguments} }} }},\n")
                            |> String.concat ""
                        // Argument values and fake scripts are decoded from the input file, never compiled in:
                        // one decoder and one key writer per named type, like the encoders.
                        let decoders = System.Collections.Generic.Dictionary<TypeRef, string>(HashIdentity.Structural)
                        let keyWriters = System.Collections.Generic.Dictionary<TypeRef, string>(HashIdentity.Structural)
                        let decodePending = System.Collections.Generic.Queue<TypeRef>()
                        let keyPending = System.Collections.Generic.Queue<TypeRef>()
                        let rec decode itemType =
                            match itemType with
                            | TypeRef.Primitive PrimitiveType.Unit -> "input.Unit()"
                            | TypeRef.Primitive PrimitiveType.String -> "input.String()"
                            | TypeRef.Primitive PrimitiveType.Bool -> "input.Bool()"
                            | TypeRef.Primitive PrimitiveType.Int64 -> "input.Long()"
                            | TypeRef.Primitive PrimitiveType.Float64 -> "input.Double()"
                            | TypeRef.Primitive PrimitiveType.Bits32 -> "input.UInt()"
                            | TypeRef.Primitive PrimitiveType.Bytes -> "NyBytes.FromLatin1(input.String())"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) ->
                                let name = sourceType item
                                $"NyList<{name}>.Create(input.Items<{name}>(() => {decode item}))"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
                                let name = sourceType item
                                $"(input.Bool() ? NyOption<{name}>.Some({decode item}) : NyOption<{name}>.None())"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ item ]) ->
                                let name = sourceType item
                                $"{sourceType itemType}.FromList(NyList<{name}>.Create(input.Items<{name}>(() => {decode item})))"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) ->
                                let mapName, keyName, valueName = sourceType itemType, sourceType keyType, sourceType valueType
                                $"System.Linq.Enumerable.Aggregate(input.Items<({keyName}, {valueName})>(() => ({decode keyType}, {decode valueType})), {mapName}.Empty(), (map, entry) => map.Insert(entry.Item1, entry.Item2))"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                let name = sourceType itemType
                                $"(input.Bool() ? {name}.Ok({decode okType}) : {name}.Err({decode errorType}))"
                            | TypeRef.Named _ ->
                                if not (decoders.ContainsKey itemType) then
                                    decoders[itemType] <- instanceName "Decode" decoders itemType
                                    decodePending.Enqueue itemType
                                "RunnerValues." + decoders[itemType] + "(input)"
                            | _ -> invalidOp "Unsupported input type passed validation."
                        let decodeFields (fields: SemanticField list) =
                            fields |> List.map (fun field -> CSharpBackendNames.fieldName field.Id + ": " + decode field.Type) |> String.concat ", "
                        let decodeNamed (named: TypeRef) =
                            let typeId, typeArguments = namedParts named
                            let typeName = sourceType named
                            let body =
                                match productFields declarations.Types typeId typeArguments with
                                | Some declared -> $"new {typeName}({decodeFields declared})"
                                | None ->
                                    let arms =
                                        unionVariants declarations.Types typeId typeArguments |> Option.get
                                        |> List.mapi (fun index variant ->
                                            $"{index} => ({typeName})new {CSharpBackendNames.variantName variant.Id}{typeArgumentSuffix typeArguments}({decodeFields variant.Fields}), ")
                                        |> String.concat ""
                                    "input.Count() switch { " + arms + "_ => throw new InvalidOperationException(\"Invalid runner input.\") }"
                            $"    public static {typeName} {decoders[named]}(RunnerInput input) => {body};\n"
                        // Writes a value in the input token form, for comparison with a keyed case.
                        let rec keyStatement itemType access =
                            match itemType with
                            | TypeRef.Primitive PrimitiveType.Unit -> "key.Add(\"u\");"
                            | TypeRef.Primitive PrimitiveType.String -> $"key.String({access});"
                            | TypeRef.Primitive PrimitiveType.Bool -> $"key.Add({access} ? \"1\" : \"0\");"
                            | TypeRef.Primitive PrimitiveType.Int64 -> $"key.Add({access});"
                            | TypeRef.Primitive PrimitiveType.Bits32 -> $"key.Add((long){access});"
                            | TypeRef.Primitive PrimitiveType.Bytes -> $"key.String({access}?.ToLatin1());"
                            | TypeRef.Primitive PrimitiveType.Float64 -> $"key.Double({access});"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) ->
                                let state, name = fresh (), fresh ()
                                $"if ({access} == null) key.Null(); else {{ key.Add({access}.Length); {access}.Fold(0, ({state}, {name}) => {{ {keyStatement item name} return {state}; }}); }}"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ item ]) ->
                                let state, name = fresh (), fresh ()
                                $"if ({access} == null) key.Null(); else {{ key.Add({access}.Size); {access}.Fold(0, ({state}, {name}) => {{ {keyStatement item name} return {state}; }}); }}"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) ->
                                let state, key, value = fresh (), fresh (), fresh ()
                                $"if ({access} == null) key.Null(); else {{ key.Add({access}.Size); {access}.Fold(0, ({state}, {key}, {value}) => {{ {keyStatement keyType key} {keyStatement valueType value} return {state}; }}); }}"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
                                let some = keyStatement item (access + ".Value")
                                $"if ({access}.HasValue) {{ key.Add(\"1\"); {some} }} else key.Add(\"0\");"
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                let ok, error = keyStatement okType (access + ".OkValue"), keyStatement errorType (access + ".ErrorValue")
                                $"if ({access}.IsOk) {{ key.Add(\"1\"); {ok} }} else {{ key.Add(\"0\"); {error} }}"
                            | TypeRef.Named _ ->
                                if not (keyWriters.ContainsKey itemType) then
                                    keyWriters[itemType] <- instanceName "Key" keyWriters itemType
                                    keyPending.Enqueue itemType
                                $"RunnerValues.{keyWriters[itemType]}(key, {access});"
                            | TypeRef.Capability _ -> "key.Add(\"c\");"
                            | _ -> invalidOp "Unsupported key type passed validation."
                        let keyFields owner (fields: SemanticField list) =
                            fields |> List.map (fun field -> keyStatement field.Type (owner + "." + CSharpBackendNames.fieldName field.Id)) |> String.concat " "
                        let keyNamed (named: TypeRef) =
                            let typeId, typeArguments = namedParts named
                            let typeName = sourceType named
                            let body =
                                match productFields declarations.Types typeId typeArguments with
                                // The cast keeps the check valid when the backend emits the Product as a record struct.
                                | Some declared -> "if ((object)value == null) { key.Null(); return; } " + keyFields "value" declared
                                | None ->
                                    let arms =
                                        unionVariants declarations.Types typeId typeArguments |> Option.get
                                        |> List.mapi (fun index variant ->
                                            let variantName = CSharpBackendNames.variantName variant.Id + typeArgumentSuffix typeArguments
                                            if List.isEmpty variant.Fields then $"case {variantName}: key.Add({index}); break; "
                                            else
                                                let name = fresh ()
                                                $"case {variantName} {name}: key.Add({index}); {keyFields name variant.Fields} break; ")
                                        |> String.concat ""
                                    "switch (value) { " + arms + "default: key.Null(); break; }"
                            $"    public static void {keyWriters[named]}(RunnerKey key, {typeName} value) {{ {body} }}\n"
                        let concurrent = CSharpBackend.usesConcurrencyRuntime program
                        let fakeClasses =
                            let mutable site = 0
                            fakes |> List.map (fun (index, _, capability, binding) ->
                                let interfaceName = CSharpBackendNames.capabilityName capability.Id
                                let operations = capability.Operations |> List.sortBy (fun operation -> StableId.value operation.Id)
                                let members =
                                    operations
                                    |> List.map (fun operation ->
                                        let name = CSharpBackendNames.capabilityOperationName operation.Id
                                        let resultType = sourceType operation.ResultType
                                        let parameters =
                                            operation.Parameters |> List.map (fun parameter -> sourceType parameter.Type + " " + CSharpBackendNames.capabilityParameterName parameter.Id)
                                            |> String.concat ", "
                                        let values =
                                            operation.Parameters |> List.map (fun parameter -> "(object)" + CSharpBackendNames.capabilityParameterName parameter.Id)
                                            |> String.concat ", "
                                        let names =
                                            operation.Parameters |> List.map (fun parameter -> CSharpBackendNames.capabilityParameterName parameter.Id)
                                            |> String.concat ", "
                                        let text =
                                            match binding with
                                            | Choice1Of2 _ ->
                                                let key =
                                                    operation.Parameters |> List.sortBy (fun parameter -> StableId.value parameter.Id)
                                                    |> List.map (fun parameter -> keyStatement parameter.Type (CSharpBackendNames.capabilityParameterName parameter.Id))
                                                    |> String.concat " "
                                                let waits =
                                                    if concurrent then
                                                        "                case 2: NyRuntime.WaitForCancellation(); return response.Value;\n"
                                                        + "                case 3: NyRuntime.Delay(response.Delay); response = response.Inner!; break;\n"
                                                    else ""
                                                $"    public {resultType} {name}({parameters})\n    {{\n        FakeResponse<{resultType}> response;\n"
                                                + $"        lock (FakeLog.Calls)\n        {{\n            FakeLog.Record({site}, new object[] {{ {values} }});\n"
                                                + $"            response = {name}Script.Select({name}Script.Keyed ? RunnerKey.Of({name}Script.KeyLimit, key => {{ {key} }}) : null);\n        }}\n"
                                                + "        while (true)\n        {\n            switch (response.Kind)\n            {\n"
                                                + "                case 0: return response.Value;\n"
                                                + "                case 1: throw new FakeCapabilityException(response.Message);\n"
                                                + waits
                                                + "                case 4: throw new FakeArgumentsUnmatchedException();\n"
                                                + "                default: throw new FakeResponsesExhaustedException();\n"
                                                + "            }\n        }\n    }\n"
                                            | Choice2Of2 _ ->
                                                $"    public {resultType} {name}({parameters})\n    {{\n        FakeLog.Record({site}, new object[] {{ {values} }});\n        return inner.{name}({names});\n    }}\n"
                                        site <- site + 1
                                        text)
                                    |> String.concat ""
                                match binding with
                                | Choice1Of2 _ ->
                                    let scripts =
                                        operations |> List.map (fun operation ->
                                            $"    readonly FakeScript<{sourceType operation.ResultType}> {CSharpBackendNames.capabilityOperationName operation.Id}Script;\n")
                                        |> String.concat ""
                                    let reads =
                                        operations |> List.map (fun operation ->
                                            let resultType = sourceType operation.ResultType
                                            let read = if supported 0 operation.ResultType then decode operation.ResultType else "throw new RunnerException(\"A fake cannot return a capability value.\")"
                                            $"        {CSharpBackendNames.capabilityOperationName operation.Id}Script = new FakeScript<{resultType}>(input, () => {read});\n")
                                        |> String.concat ""
                                    $"sealed class Fake{index} : {interfaceName}\n{{\n{scripts}    public Fake{index}(RunnerInput input)\n    {{\n{reads}    }}\n{members}}}\n"
                                | Choice2Of2 implementation ->
                                    // Records each call, then forwards it to the registered host implementation.
                                    $"sealed class Host{index} : {interfaceName}\n{{\n    readonly {interfaceName} inner = new {implementation}();\n{members}}}\n")
                            |> String.concat ""
                        let args =
                            List.zip fn.Parameters arguments |> List.mapi (fun index (parameter, value) ->
                                match value with
                                | PureRunValue.FakeCapability _ -> $"new Fake{index}(input)"
                                | PureRunValue.HostCapability _ -> $"new Host{index}()"
                                | _ -> decode parameter.Type)
                            |> String.concat ", "
                        let runnerValues = StringBuilder()
                        while decodePending.Count > 0 || keyPending.Count > 0 do
                            if decodePending.Count > 0 then runnerValues.Append(decodeNamed (decodePending.Dequeue())) |> ignore
                            else runnerValues.Append(keyNamed (keyPending.Dequeue())) |> ignore
                        let runnerValues = runnerValues.ToString()
                        let input = ResizeArray<string>()
                        arguments |> List.iter (function
                            | PureRunValue.FakeCapability(capabilityId, script) ->
                                let capability = program.Capabilities |> List.find (fun item -> item.Id = capabilityId)
                                let operations = capability.Operations |> List.map (fun operation -> operation.Id) |> List.sortBy StableId.value
                                fakeInput declarations.Types operations script input
                            | PureRunValue.HostCapability _ -> ()
                            | value -> inputTokens declarations.Types input value)
                        let namedEncoders = StringBuilder()
                        while pending.Count > 0 do
                            namedEncoders.Append(encodeNamed (pending.Dequeue())) |> ignore
                        let namedEncoders = namedEncoders.ToString()
                        let checksText = (encoded + callArms + namedEncoders).Contains("CheckedString(")
                        // The child prints one envelope: ["value", v], ["limit", message] for runner-owned
                        // conversion limits, or ["exception", type, message, frames, calls, exhausted, kind] for a program failure.
                        // String values and the exception message are text tokens ("s" and the Base64 of the
                        // UTF-16 code units), so lone surrogates reach the session unchanged.
                        let program =
                            "using System; using System.Globalization; using System.Linq; using System.Text; using System.Text.Json; using Nyrwvy.Generated;\n"
                            // A stack overflow aborts the child. Where the OS captures core dumps (for example
                            // systemd-coredump), writing the dump delays process exit by seconds under load and
                            // consumes the run's time limit, so the runner opts out of core dumps first.
                            + "if (OperatingSystem.IsLinux()) RunnerProcess.prctl(4 /* PR_SET_DUMPABLE */, 0, 0, 0, 0);\n"
                            // Arguments and fake scripts arrive as input, so the program does not depend on them.
                            + "var input = new RunnerInput(System.IO.File.ReadAllText(args[0]));\n"
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
                            + "    try { Console.WriteLine(JsonSerializer.Serialize(new object[] { \"exception\", error.GetType().FullName ?? \"\", RunnerText.ToToken(error.Message), frames, EncodeCalls(), error is FakeResponsesExhaustedException, error is OutOfMemoryException or InsufficientExecutionStackException ? \"fatal\" : error is OperationCanceledException ? \"cancelled\" : \"fault\" }, options)); }\n"
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
                                "string CheckedString(string value) { if (value == null) throw new RunnerException(\"Null String result.\"); textRemaining -= value.Length; if (textRemaining < 0) throw new RunnerException(\"Result exceeds 65536 String code units.\"); return RunnerText.ToToken(value); }\n"
                               else "")
                            + namedEncoders
                            + "static class RunnerProcess { [System.Runtime.InteropServices.DllImport(\"libc\")] public static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5); }\n"
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
                            + runnerSupport
                            + "static class RunnerValues\n{\n" + runnerValues + "}\n"
                            + fakeClasses
                        let frames = frameResolver artifact.Symbols
                        let stackOverflowMessage =
                            "Stack overflow: recursion was too deep. A self DirectCall in tail position runs as a loop (the body's result, "
                            + "a Bind body, a Conditional branch, a match arm or otherwise, or the right operand of BoolAnd/BoolOr); "
                            + "any other self or mutual DirectCall recursion runs on the runner's thread stack, which holds only several "
                            + "thousand frames of a typical function. Make the self call the last step of each continuing branch, "
                            + "or iterate a List with ListFold (for text, over StringCodePoints)."
                        let runtimeFailure kind exceptionType message frameNames exhausted calls =
                            PureRunError.RuntimeFailure
                                { SnapshotId = snapshotId
                                  FunctionId = fn.Id
                                  Kind = kind
                                  ExceptionType = exceptionType
                                  Message = message
                                  Frames = frames frameNames
                                  BindingExhausted = exhausted
                                  Calls = calls }
                        let sources =
                            [ "Generated.cs", artifact.Source ]
                            @ (hostSources |> List.mapi (fun index (_, text) -> $"HostSource{index}.cs", text))
                            @ [ "Program.cs", program ]
                        let inputPath = Path.Combine(root, "input.txt")
                        File.WriteAllText(inputPath, String.Join(" ", input))
                        match GeneratedBuild.compileCached root sources 30000 with
                        | Error GeneratedBuild.TimedOut -> Error(diagnostic "RunTimeout" "The build exceeded its time limit of 30 s.")
                        | Error(GeneratedBuild.Rejected errors) -> Error(processFailure "" errors)
                        | Ok assembly ->
                            // Arbitrary host C# may change process-wide state, so it runs in a process of
                            // its own; generated programs with values and fakes share the session's worker.
                            if not (List.isEmpty hostSources) then launchRaw "pure function" root [ assembly; inputPath ] 5000
                            else
                                match context.Worker.Run("pure function", assembly, Path.ChangeExtension(assembly, ".pdb"), inputPath, 5000) with
                                | PureRunWorkerOutcome.Completed output ->
                                    if output.Length > 1048576 then Error(diagnostic "RunOutputTooLarge" "The process produced more than 1 MiB of output.")
                                    else Ok(0, output, "")
                                | PureRunWorkerOutcome.Crashed(output, errors) -> Ok(1, output, errors)
                                | PureRunWorkerOutcome.Failed(code, message) -> Error(diagnostic code message)
                        |> Result.bind (fun (exitCode, output, errors) ->
                            if exitCode = 0 then
                                parseEnvelope declarations.Types fn.ResultType sites output
                                |> Result.bind (function
                                    | Envelope.Value(actual, calls) -> Ok(actual, calls)
                                    | Envelope.Limit message -> Error(diagnostic "RunProcessFailure" message)
                                    | Envelope.Exception(kind, exceptionType, message, frameNames, calls, exhausted) ->
                                        Error(runtimeFailure kind exceptionType message frameNames exhausted calls))
                            elif errors.Contains("Stack overflow", StringComparison.Ordinal) then
                                // A stack overflow cannot be caught; the runtime prints the stack to stderr.
                                let names = stackOverflowFrames.Matches(errors) |> Seq.map (fun item -> item.Value) |> Seq.toList
                                Error(runtimeFailure "fatal" "System.StackOverflowException" stackOverflowMessage names false [])
                            else Error(processFailure output errors))
                        |> Result.map (fun (actual, calls) ->
                            { SnapshotId = snapshotId
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

    /// Pays ahead what the session's first run would otherwise wait for: Roslyn start-up, the first
    /// compile of the generated runtime support and runner helpers, and the runner worker. It reads no
    /// session state besides the worker, so it may run beside requests; any failure is ignored and the
    /// first run builds as usual.
    let internal warm (worker: PureRunWorker) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-pure-run-warm-" + Guid.NewGuid().ToString("N"))
        try
            try
                Directory.CreateDirectory root |> ignore
                let emitted =
                    StableId.tryCreate<SnapshotTag> "warm"
                    |> Result.mapError string
                    |> Result.bind (fun snapshotId ->
                        let empty: ExecutionProgram = { SourceSnapshotId = snapshotId; Types = []; Effects = []; Capabilities = []; Functions = [] }
                        CSharpBackend.emit empty CSharpBackendProfile.empty |> Result.mapError string)
                match emitted with
                | Ok artifact ->
                    let program =
                        "using System; using System.Globalization; using System.Linq; using System.Text; using System.Text.Json; using Nyrwvy.Generated;\n"
                        + "Console.WriteLine(0);\n"
                        + "sealed class RunnerException(string message) : Exception(message);\n"
                        + runnerSupport
                    GeneratedBuild.compile root [ "Generated.cs", artifact.Source; "Program.cs", program ] 30000 |> ignore
                | Error _ -> ()
                worker.Warm()
            with _ -> ()
        finally
            try Directory.Delete(root, true)
            with _ -> ()

    /// Reads explicitly registered host C# sources. Relative paths resolve against the directory
    /// containing the session's snapshot file; at most 16 existing `.cs` files of at most 1 MiB
    /// each are accepted.
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
    let runWithHostOn
        (backend: PureRunBackend)
        (session: AuthoringSession)
        (functionId: FunctionId)
        (arguments: PureRunValue list)
        (expected: PureRunValue option)
        (hostSources: string list)
        : Result<PureRunResult, PureRunError> =
        match session.VerifyPersistedSource() with
        | Error error -> Error(PureRunError.SessionFailure error)
        | Ok() when arguments @ Option.toList expected |> List.exists (boundedValue >> not) ->
            Error(diagnostic "RunArgumentTooLarge" $"Each argument or expected value is limited to {maxValueLayers} nested composite layers and 8192 value nodes. Split the input into smaller batches or return an aggregate.")
        | Ok() when not session.HostSourcesAllowed
                   && (not (List.isEmpty hostSources)
                       || arguments |> List.exists (function PureRunValue.HostCapability _ -> true | _ -> false)) ->
            Error(diagnostic "RunHostSourcesDisabled" "Host sources and hostCapability bindings compile and run arbitrary C# with the user's permissions, so they are disabled unless the session was started with --allow-host-sources. Use fakeCapability bindings, or ask the operator to enable host sources.")
        | Ok() ->
            session.ExecutionContext None
            |> Result.mapError PureRunError.SessionFailure
            |> Result.bind (fun context ->
                readHostSources session hostSources |> Result.bind (runVerified context backend functionId arguments expected)
                |> Result.mapError (enrichInputError context.Declarations functionId arguments expected))

    let runWithHost session functionId arguments expected hostSources =
        runWithHostOn PureRunBackend.CSharp session functionId arguments expected hostSources

    let run session functionId arguments expected = runWithHost session functionId arguments expected []

    /// Read-only candidate execution, restricted to scripted fake capabilities.
    let internal runContext context functionId arguments expected =
        if arguments @ Option.toList expected |> List.exists (boundedValue >> not) then
            Error(diagnostic "RunArgumentTooLarge" "Verification values exceed the runPure bound.")
        elif arguments |> List.exists (function PureRunValue.HostCapability _ -> true | _ -> false) then
            Error(diagnostic "VerificationHostUnsupported" "Change verification accepts fake capabilities only.")
        else runVerified context PureRunBackend.CSharp functionId arguments expected [] |> Result.mapError (enrichInputError context.Declarations functionId arguments expected)

