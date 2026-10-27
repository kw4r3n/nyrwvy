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

    let private nativeValue (context: string) (element: JsonElement) (isResult: bool) =
        let c = requiredString "c" element
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
            | other -> fail $"{context}: unknown C type '{other}'."
        match value with
        | CSharpNativeValue.Utf8(Some _) when not isResult -> fail $"{context}: free applies to a result only."
        | CSharpNativeValue.Handle true when isResult -> fail $"{context}: release applies to an argument only."
        | _ -> value

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
                |> List.mapi (fun index item ->
                    let at = $"{context} argument {index}"
                    if optionalBool "self" false item then
                        ensureFields at [ "self"; "release" ] [ "self" ] item
                        CSharpNativeArgument.Self(optionalBool "release" false item)
                    else
                        ensureFields at [ "from"; "c"; "release" ] [ "from"; "c" ] item
                        let name = requiredString "from" item
                        match operation.Parameters |> List.filter (fun parameter -> parameter.DisplayName = name) with
                        | [ parameter ] -> CSharpNativeArgument.Parameter(parameter.Id, nativeValue at item false)
                        | _ -> fail $"{at}: '{name}' names no single parameter of the operation.")
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
