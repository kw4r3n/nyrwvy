namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open Nyrwvy.Core

/// Reads C# mapping tables ([decision](../../docs/decisions/csharp-mapping-tables.md)): resolves their
/// display names against the session's declarations once, so the backend sees only stable IDs.
[<RequireQualifiedAccess>]
module internal CSharpMappingTable =
    open WireInput

    let format = "nyrwvy.csharp-mapping"
    let version = 1

    /// `lib.csharp.json` → `lib.csharp.override.json`.
    let overridePath (path: string) =
        if path.EndsWith(".json", System.StringComparison.Ordinal) then path.Substring(0, path.Length - 5) + ".override.json"
        else path + ".override.json"

    type private Capability = { Handle: bool option; Operations: Map<string, JsonElement> }

    let private objectMap (context: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then fail $"{context} must be an object."
        element.EnumerateObject() |> Seq.map (fun item -> item.Name, item.Value) |> Map.ofSeq

    let private read (path: string) (table: JsonElement) =
        ensureFields $"mapping table {path}" [ "format"; "version"; "library"; "capabilities" ] [ "format"; "version"; "capabilities" ] table
        if requiredString "format" table <> format then fail $"mapping table {path}: format must be '{format}'."
        if requiredInt "version" table <> version then fail $"mapping table {path}: only version {version} is supported."
        let library = tryProperty "library" table |> Option.map (fun _ -> requiredString "library" table)
        let capabilities =
            objectMap $"{path}: capabilities" (property "capabilities" table)
            |> Map.map (fun name item ->
                ensureFields $"{path}: capability {name}" [ "handle"; "operations" ] [] item
                { Handle = tryProperty "handle" item |> Option.map (fun _ -> requiredBool "handle" item)
                  Operations = tryProperty "operations" item |> Option.map (objectMap $"{path}: {name}.operations") |> Option.defaultValue Map.empty })
        library, capabilities

    /// The override's library, handle flags and operation entries replace the generated ones.
    let private merge (library, capabilities) (overrideLibrary, overrideCapabilities: Map<string, Capability>) =
        let merged =
            overrideCapabilities |> Map.fold (fun (state: Map<string, Capability>) name item ->
                match Map.tryFind name state with
                | None -> Map.add name item state
                | Some generated ->
                    Map.add name
                        { Handle = Option.orElse generated.Handle item.Handle
                          Operations = Map.fold (fun operations key entry -> Map.add key entry operations) generated.Operations item.Operations }
                        state) capabilities
        Option.orElse library overrideLibrary, merged

    let private nativeValueOf (context: string) (c: string) (element: JsonElement) (isResult: bool) =
        let integer bits signed = CSharpNativeValue.Integer(bits, signed)
        let value =
            match c with
            | "int8" -> integer 8 true | "int16" -> integer 16 true | "int32" -> integer 32 true | "int64" -> integer 64 true
            | "uint8" -> integer 8 false | "uint16" -> integer 16 false | "uint32" -> integer 32 false | "uint64" -> integer 64 false
            | "bool" -> CSharpNativeValue.Bool
            | "intBool" -> CSharpNativeValue.IntBool
            | "utf8" -> CSharpNativeValue.Utf8(tryProperty "free" element |> Option.map (fun _ -> requiredString "free" element))
            | "bytes" -> CSharpNativeValue.Bytes
            | "handle" -> CSharpNativeValue.Handle(optionalBool "release" false element)
            | "void" -> CSharpNativeValue.Void
            | "bits64" -> CSharpNativeValue.Bits64
            | "float32" -> CSharpNativeValue.Float 32
            | "float64" -> CSharpNativeValue.Float 64
            | "pointer" -> CSharpNativeValue.Pointer
            | other -> fail $"{context}: unknown C type '{other}'."
        match value with
        | CSharpNativeValue.Utf8(Some _) when not isResult -> fail $"{context}: free applies to a result only."
        | CSharpNativeValue.Handle true when isResult -> fail $"{context}: release applies to an argument only."
        | _ -> value

    let private nativeValue (context: string) (element: JsonElement) (isResult: bool) =
        nativeValueOf context (requiredString "c" element) element isResult

    let private byPointer (context: string) (element: JsonElement) =
        match tryProperty "by" element |> Option.map (fun _ -> requiredString "by" element) with
        | None | Some "value" -> false
        | Some "pointer" -> true
        | Some other -> fail $"{context}: by must be 'value' or 'pointer', not '{other}'."

    let private int64Of (context: string) (name: string) (element: JsonElement) =
        match (property name element).TryGetInt64() with
        | true, value -> value
        | _ -> fail $"{context}: {name} must be an integer."

    /// One C argument (or, `isField`, one struct field) of an entry.
    let rec private argument (context: string) (operation: SemanticCapabilityOperation) (isField: bool) (item: JsonElement) =
        let parameterId () =
            let name = requiredString "from" item
            match operation.Parameters |> List.filter (fun parameter -> parameter.DisplayName = name) with
            | [ parameter ] -> parameter.Id
            | _ -> fail $"{context}: '{name}' names no single parameter of the operation."
        let has name = tryProperty name item |> Option.isSome
        if item.ValueKind <> JsonValueKind.Object then fail $"{context} must be an object."
        if has "self" && not isField then
            ensureFields context [ "self"; "release" ] [ "self" ] item
            if not (optionalBool "self" false item) then fail $"{context}: self must be true."
            CSharpNativeArgument.Self(optionalBool "release" false item)
        elif has "out" && not isField then
            ensureFields context [ "out"; "capacity"; "retry" ] [ "out" ] item
            match requiredString "out" item with
            | "bytes" ->
                ensureFields context [ "out"; "capacity"; "retry" ] [ "out"; "capacity" ] item
                CSharpNativeArgument.OutBytes(requiredInt "capacity" item, if has "retry" then Some(int64Of context "retry" item) else None)
            | c ->
                ensureFields context [ "out" ] [ "out" ] item
                CSharpNativeArgument.Out(nativeValueOf context c item true)
        elif has "struct" && not isField then
            ensureFields context [ "struct"; "size"; "by" ] [ "struct" ] item
            let fields = property "struct" item
            if fields.ValueKind <> JsonValueKind.Array then fail $"{context}: struct must be an array of fields."
            CSharpNativeArgument.Struct(
                fields.EnumerateArray() |> Seq.mapi (fun index field -> argument $"{context} field {index}" operation true field) |> Seq.toList,
                (if has "size" then Some(requiredInt "size" item) else None),
                byPointer context item)
        elif has "sizeof" && isField then
            ensureFields context [ "sizeof"; "c" ] [ "sizeof"; "c" ] item
            CSharpNativeArgument.SizeOf(nativeValue context item false)
        elif has "value" then
            ensureFields context [ "value"; "c"; "name" ] [ "value"; "c" ] item
            let isNull = (property "value" item).ValueKind = JsonValueKind.Null
            if isNull <> (requiredString "c" item = "pointer") then fail $"{context}: null is the only pointer constant."
            let value = if isNull then 0L else int64Of context "value" item
            CSharpNativeArgument.Constant(value, nativeValue context item false)
        else
            ensureFields context [ "from"; "c"; "release"; "by" ] [ "from"; "c" ] item
            if isField && (has "release" || has "by") then fail $"{context}: a struct field takes only from and c."
            if byPointer context item then CSharpNativeArgument.Reference(parameterId (), nativeValue context item false)
            else CSharpNativeArgument.Parameter(parameterId (), nativeValue context item false)

    let private entry (context: string) (library: string option) (operation: SemanticCapabilityOperation) (element: JsonElement) =
        ensureFields context [ "symbol"; "arguments"; "result"; "failure"; "unavailable"; "library" ] [] element
        if optionalBool "unavailable" false element then
            if tryProperty "symbol" element |> Option.isSome then fail $"{context}: an unavailable operation names no symbol."
            None
        else
            let library =
                match tryProperty "library" element |> Option.map (fun _ -> requiredString "library" element), library with
                | Some name, _ | None, Some name -> name
                | None, None -> fail $"{context}: no library is named."
            let arguments =
                tryProperty "arguments" element
                |> Option.map (fun items ->
                    if items.ValueKind <> JsonValueKind.Array then fail $"{context}: arguments must be an array."
                    items.EnumerateArray() |> Seq.toList)
                |> Option.defaultValue []
                |> List.mapi (fun index item -> argument $"{context} argument {index}" operation false item)
            let result = tryProperty "result" element
            result |> Option.iter (ensureFields $"{context} result" [ "c"; "free" ] [ "c" ])
            let failure =
                tryProperty "failure" element
                |> Option.map (fun _ ->
                    match requiredString "failure" element with
                    | "null" -> CSharpNativeFailure.NullPointer
                    | "negative" -> CSharpNativeFailure.Negative
                    | "nonzero" -> CSharpNativeFailure.Nonzero
                    | other -> fail $"{context}: unknown failure '{other}' (null, negative or nonzero).")
            Some
                { Library = library
                  Symbol = requiredString "symbol" element
                  Arguments = arguments
                  Returns = result |> Option.map (fun item -> nativeValue $"{context} result" item true) |> Option.defaultValue CSharpNativeValue.Void
                  Failure = failure }

    /// Bindings for every operation of every capability the tables name. `load` reads one confined
    /// JSON file, and `exists` says whether an override file is present.
    let bindings (declarations: AuthoringDeclarations) (load: string -> JsonElement) (exists: string -> bool) (paths: string list) =
        paths
        |> List.collect (fun path ->
            let generated = read path (load path)
            let overrideFile = overridePath path
            let library, capabilities =
                if exists overrideFile then merge generated (read overrideFile (load overrideFile)) else generated
            capabilities
            |> Map.toList
            |> List.collect (fun (name, table) ->
                let capability =
                    match declarations.Capabilities |> List.filter (fun item -> item.DisplayName = name) with
                    | [ capability ] -> capability
                    | [] -> fail $"mapping table {path}: no capability is named '{name}'."
                    | _ -> fail $"mapping table {path}: several capabilities are named '{name}'."
                for key in table.Operations |> Map.keys do
                    if not (capability.Operations |> List.exists (fun operation -> operation.DisplayName = key)) then
                        fail $"mapping table {path}: capability {name} has no operation '{key}'."
                capability.Operations
                |> List.map (fun operation ->
                    let context = $"mapping table {path}: {name}.{operation.DisplayName}"
                    match Map.tryFind operation.DisplayName table.Operations with
                    | None -> fail $"{context} is neither bound nor marked unavailable."
                    | Some element ->
                        { OperationId = operation.Id
                          Kind =
                            CSharpCapabilityBindingKind.Native
                                { Handle = Option.defaultValue false table.Handle
                                  Call = entry context library operation element } })))
