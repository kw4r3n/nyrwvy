namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// `bind-c`: reads a C header through clang's JSON AST and proposes, for the functions the user
/// selects, capability declarations (by-name session requests) and a C# mapping table
/// ([decision](../../docs/decisions/csharp-mapping-tables.md)). What it cannot map is reported.
[<RequireQualifiedAccess>]
module CBindingGenerator =
    let usage =
        "Usage: Nyrwvy.AuthoringSession bind-c --header <file.h> --library <native-name> --capability <Name> --effect <Name> --functions <f1,f2,...> --requests <out.jsonl> --table <out.csharp.json> [--prefix <c_prefix_>] [--release <function>]... [--free <function>=<free-function>]... [--failure <function>=null|negative|nonzero]... [--clang-arg <arg>]..."

    [<RequireQualifiedAccess>]
    type private CType =
        | Integer of bits: int * signed: bool
        | Bool
        | Void
        | Text of isConst: bool
        | BytePointer
        | Handle of tag: string
        | Unmapped of string

    type private Options =
        { Header: string; Library: string; Capability: string; Effect: string; Prefix: string
          Functions: string list; Requests: string; Table: string
          Releases: Set<string>; Frees: Map<string, string>; Failures: Map<string, string>; ClangArgs: string list }

    let private parse (arguments: string list) =
        let rec pairs collected = function
            | [] -> Some(List.rev collected)
            | (flag: string) :: value :: rest when flag.StartsWith("--", StringComparison.Ordinal) -> pairs ((flag, value) :: collected) rest
            | _ -> None
        match pairs [] arguments with
        | None -> None
        | Some pairs ->
            let all flag = pairs |> List.filter (fst >> (=) flag) |> List.map snd
            let one flag = match all flag with [ value ] -> Some value | _ -> None
            let known = set [ "--header"; "--library"; "--capability"; "--effect"; "--functions"; "--requests"; "--table"; "--prefix"; "--release"; "--free"; "--failure"; "--clang-arg" ]
            let assignments flag =
                all flag |> List.map (fun (item: string) ->
                    match item.Split('=', 2) with
                    | [| key; value |] -> key, value
                    | _ -> "", "") |> Map.ofList
            if pairs |> List.exists (fun (flag, _) -> not (Set.contains flag known)) then None
            else
                match one "--header", one "--library", one "--capability", one "--effect", one "--functions", one "--requests", one "--table" with
                | Some header, Some library, Some capability, Some effect, Some functions, Some requests, Some table ->
                    Some
                        { Header = header; Library = library; Capability = capability; Effect = effect
                          Prefix = defaultArg (one "--prefix") ""
                          Functions = functions.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> List.ofArray
                          Requests = requests; Table = table
                          Releases = all "--release" |> Set.ofList
                          Frees = assignments "--free"; Failures = assignments "--failure"
                          ClangArgs = all "--clang-arg" }
                | _ -> None

    type private Declarations =
        { Typedefs: Map<string, string>
          CompleteRecords: Set<string>
          Functions: Map<string, string * (string * string) list> }

    let private declarations (root: JsonElement) =
        let nodes = root.GetProperty("inner").EnumerateArray() |> Seq.toList
        let text (node: JsonElement) (name: string) = match node.TryGetProperty name with | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() | _ -> null
        let kind node = text node "kind"
        let qualType (node: JsonElement) = match node.TryGetProperty "type" with | true, value -> text value "qualType" | _ -> null
        let named node = not (isNull (text node "name"))
        { Typedefs =
            nodes |> List.filter (fun node -> kind node = "TypedefDecl" && named node)
            |> List.map (fun node -> text node "name", qualType node) |> Map.ofList
          CompleteRecords =
            nodes |> List.filter (fun node ->
                kind node = "RecordDecl" && named node
                && (match node.TryGetProperty "completeDefinition" with | true, value -> value.ValueKind = JsonValueKind.True | _ -> false))
            |> List.map (fun node -> text node "name") |> Set.ofList
          Functions =
            nodes |> List.filter (fun node -> kind node = "FunctionDecl" && named node)
            |> List.rev // the first declaration wins
            |> List.map (fun node ->
                let parameters =
                    match node.TryGetProperty "inner" with
                    | true, inner -> inner.EnumerateArray() |> Seq.filter (fun item -> kind item = "ParmVarDecl") |> Seq.map (fun item -> text item "name", qualType item) |> Seq.toList
                    | _ -> []
                text node "name", (qualType node, parameters))
            |> Map.ofList }

    let private exactIntegers =
        Map.ofList
            [ "int8_t", (8, true); "int16_t", (16, true); "int32_t", (32, true); "int64_t", (64, true)
              "uint8_t", (8, false); "uint16_t", (16, false); "uint32_t", (32, false); "uint64_t", (64, false)
              // shortcut: assumes an LP64 target (Linux, macOS); Windows needs long as 32 bits.
              "size_t", (64, false); "ssize_t", (64, true); "intptr_t", (64, true); "uintptr_t", (64, false)
              "char", (8, true); "signed char", (8, true); "unsigned char", (8, false)
              "short", (16, true); "unsigned short", (16, false); "int", (32, true); "unsigned int", (32, false)
              "long", (64, true); "unsigned long", (64, false); "long long", (64, true); "unsigned long long", (64, false) ]

    let private classify (declarations: Declarations) (qualType: string) =
        let stripConst (text: string) = if text.StartsWith("const ", StringComparison.Ordinal) then text.Substring(6).Trim(), true else text, false
        let rec resolve (name: string) depth =
            if depth > 16 || Map.containsKey name exactIntegers then name
            else match Map.tryFind name declarations.Typedefs with
                 | Some target when target <> name -> resolve (fst (stripConst target)) (depth + 1)
                 | _ -> name
        let text = (if isNull qualType then "" else qualType).Trim()
        if text.EndsWith("*", StringComparison.Ordinal) then
            let pointee, isConst = stripConst (text.Substring(0, text.Length - 1).Trim())
            let target = resolve pointee 0
            if target.Contains '*' || target.Contains '(' then CType.Unmapped $"'{text}' is a pointer to a pointer or function"
            elif target = "char" then CType.Text isConst
            elif isConst && (target = "unsigned char" || target = "uint8_t") then CType.BytePointer
            elif target.StartsWith("struct ", StringComparison.Ordinal) then
                let tag = target.Substring(7).Trim()
                if Set.contains tag declarations.CompleteRecords then CType.Unmapped $"'{text}' points to a complete struct (structs are out of scope)"
                else CType.Handle tag
            else CType.Unmapped $"'{text}' is a pointer with no mapping"
        else
            match resolve (fst (stripConst text)) 0 with
            | "void" -> CType.Void
            | "_Bool" | "bool" -> CType.Bool
            | name ->
                match Map.tryFind name exactIntegers with
                | Some(bits, signed) -> CType.Integer(bits, signed)
                | None -> CType.Unmapped $"'{text}' has no mapping (structs by value, enums and floats are out of scope)"

    let private camel (text: string) =
        let parts = text.Split('_', StringSplitOptions.RemoveEmptyEntries)
        parts |> Array.mapi (fun index part -> if index = 0 then part.ToLowerInvariant() else string (Char.ToUpperInvariant part[0]) + part.Substring(1).ToLowerInvariant()) |> String.concat ""

    let private pascal (text: string) =
        let value = camel text
        if value = "" then value else string (Char.ToUpperInvariant value[0]) + value.Substring(1)

    let private strip (prefix: string) (text: string) =
        if prefix <> "" && text.StartsWith(prefix, StringComparison.Ordinal) then text.Substring(prefix.Length) else text

    let private primitive name : JsonNode = JsonObject([ Collections.Generic.KeyValuePair("kind", JsonValue.Create "primitive" :> JsonNode); Collections.Generic.KeyValuePair("primitive", JsonValue.Create(name: string) :> JsonNode) ]) :> JsonNode
    let private node (pairs: (string * JsonNode) list) = JsonObject(pairs |> List.map (fun (key, value) -> Collections.Generic.KeyValuePair(key, value)))
    let private str (text: string) = JsonValue.Create text :> JsonNode
    let private array (items: JsonNode list) = JsonArray(items |> Array.ofList) :> JsonNode

    type private Operation =
        { Owner: string; Name: string; Declaration: JsonNode; Entry: JsonNode; Handles: string list }

    /// One function as an operation, or why it cannot be mapped.
    let private operation (options: Options) (declarations: Declarations) (functionName: string) =
        match Map.tryFind functionName declarations.Functions with
        | None -> Error "not declared in the header"
        | Some(functionType, parameters) ->
            let returnText = functionType.Substring(0, max 0 (functionType.IndexOf '('))
            if functionType.Contains "(*" || returnText.Trim() = "" then Error $"'{functionType}' returns a function pointer" else
            let handleName tag = pascal (strip options.Prefix tag)
            let classified = parameters |> List.map (fun (name, qualType) -> name, classify declarations qualType)
            let unmapped =
                classified |> List.tryPick (fun (name, ctype) ->
                    match ctype with
                    | _ when String.IsNullOrEmpty name -> Some "a parameter has no name"
                    | CType.Unmapped reason -> Some $"parameter {name}: {reason}"
                    | _ -> None)
            match unmapped with
            | Some reason -> Error reason
            | None ->
            // A const byte pointer followed by an integer is one Bytes argument.
            let rec arguments = function
                | [] -> Ok []
                | (name, CType.BytePointer) :: (_, CType.Integer _) :: rest -> arguments rest |> Result.map (fun tail -> (name, CType.BytePointer) :: tail)
                | (name, CType.BytePointer) :: _ -> Error $"parameter {name}: a byte pointer must be followed by its length"
                | (name, CType.Text false) :: _ -> Error $"parameter {name}: 'char *' may be written by the library; only 'const char *' maps to String"
                | (name, CType.Void) :: _ -> Error $"parameter {name}: void"
                | item :: rest -> arguments rest |> Result.map (fun tail -> item :: tail)
            match arguments classified with
            | Error reason -> Error reason
            | Ok arguments ->
            let isRelease = Set.contains functionName options.Releases
            let receiver =
                match arguments with
                | (_, CType.Handle tag) :: _ when not isRelease -> Some tag
                | _ -> None
            let releaseProblem =
                if isRelease && (arguments |> List.filter (fun (_, ctype) -> match ctype with CType.Handle _ -> true | _ -> false) |> List.length) <> 1 then
                    Some "a release function takes exactly one handle"
                else None
            let typeOf ctype =
                match ctype with
                | CType.Integer _ -> primitive "int64"
                | CType.Bool -> primitive "bool"
                | CType.Text _ -> primitive "string"
                | CType.BytePointer -> primitive "bytes"
                | CType.Handle tag -> node [ "kind", str "capability"; "name", str (handleName tag) ]
                | CType.Void -> primitive "unit"
                | CType.Unmapped _ -> invalidOp "unmapped"
            let cName ctype =
                match ctype with
                | CType.Integer(bits, signed) -> (if signed then "int" else "uint") + string bits
                | CType.Bool -> "bool"
                | CType.Text _ -> "utf8"
                | CType.BytePointer -> "bytes"
                | CType.Handle _ -> "handle"
                | CType.Void -> "void"
                | CType.Unmapped _ -> invalidOp "unmapped"
            let result = classify declarations returnText
            let failure = Map.tryFind functionName options.Failures
            let free = Map.tryFind functionName options.Frees
            let resultProblem =
                match result, failure, free with
                | CType.Unmapped reason, _, _ -> Some $"result: {reason}"
                | CType.BytePointer, _, _ -> Some "result: a byte pointer has no length (Bytes results are out of scope)"
                | CType.Text false, _, None -> Some "result: 'char *' is owned by the caller; name its free function with --free"
                | CType.Text true, _, Some _ -> Some "result: a 'const char *' result is borrowed; --free does not apply"
                | _, Some "null", _ when (match result with CType.Text _ | CType.Handle _ -> false | _ -> true) -> Some "--failure null needs a pointer result"
                | _, Some "negative", _ when (match result with CType.Integer(_, true) -> false | _ -> true) -> Some "--failure negative needs a signed integer result"
                | _, Some "nonzero", _ when (match result with CType.Integer _ -> false | _ -> true) -> Some "--failure nonzero needs an integer result"
                | _, Some value, _ when not (List.contains value [ "null"; "negative"; "nonzero" ]) -> Some $"unknown failure '{value}'"
                | _, _, Some _ when (match result with CType.Text false -> false | _ -> true) -> Some "--free applies to a 'char *' result"
                | _ -> None
            match Option.orElse resultProblem releaseProblem with
            | Some reason -> Error reason
            | None ->
            let okType = if failure = Some "nonzero" then primitive "unit" else typeOf result
            let resultType =
                match failure with
                | Some _ -> node [ "kind", str "builtinGeneric"; "generic", str "result"; "arguments", array [ okType; primitive "string" ] ] :> JsonNode
                | None -> okType
            let stripped = strip options.Prefix functionName
            let owner, name =
                match receiver with
                | Some tag -> handleName tag, camel (strip (strip options.Prefix tag + "_") stripped)
                | None -> options.Capability, camel stripped
            let ownParameters = match receiver with Some _ -> List.tail arguments | None -> arguments
            let declaration =
                node
                    [ "displayName", str name
                      "parameters", array (ownParameters |> List.map (fun (parameter, ctype) -> node [ "displayName", str (camel parameter); "type", typeOf ctype ] :> JsonNode))
                      "resultType", resultType
                      "effect", str options.Effect ]
            let entryArguments =
                (match receiver with Some _ -> [ node [ "self", JsonValue.Create true ] :> JsonNode ] | None -> [])
                @ (ownParameters |> List.map (fun (parameter, ctype) ->
                    let item = node [ "from", str (camel parameter); "c", str (cName ctype) ]
                    if isRelease && (match ctype with CType.Handle _ -> true | _ -> false) then item["release"] <- JsonValue.Create true
                    item :> JsonNode))
            let resultNode = node [ "c", str (cName result) ]
            free |> Option.iter (fun symbol -> resultNode["free"] <- str symbol)
            let entry = node [ "symbol", str functionName; "arguments", array entryArguments; "result", resultNode ]
            failure |> Option.iter (fun value -> entry["failure"] <- str value)
            let handles =
                (result :: (arguments |> List.map snd))
                |> List.choose (function CType.Handle tag -> Some(handleName tag) | _ -> None)
            Ok { Owner = owner; Name = name; Declaration = declaration; Entry = entry; Handles = handles }

    let private clang (options: Options) =
        let info = ProcessStartInfo("clang")
        for argument in [ "-x"; "c"; "-Xclang"; "-ast-dump=json"; "-fsyntax-only" ] @ options.ClangArgs @ [ options.Header ] do
            info.ArgumentList.Add argument
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        use child = Process.Start info
        let errors = child.StandardError.ReadToEndAsync()
        let output = child.StandardOutput.ReadToEnd()
        child.WaitForExit()
        if child.ExitCode <> 0 then Error $"clang failed: {errors.Result}" else Ok output

    /// Writes the requests and the table and returns the report, or an error message.
    let private generate (options: Options) =
        match clang options with
        | Error message -> Error message
        | Ok ast ->
            use document = JsonDocument.Parse(ast, JsonDocumentOptions(MaxDepth = 4096))
            let declarations = declarations document.RootElement
            let results = options.Functions |> List.map (fun name -> name, operation options declarations name)
            let mapped = results |> List.choose (fun (_, result) -> match result with Ok item -> Some item | Error _ -> None)
            let duplicates =
                mapped |> List.countBy (fun item -> item.Owner, item.Name) |> List.filter (fun (_, count) -> count > 1) |> List.map fst
            let unmapped =
                (results |> List.choose (fun (name, result) -> match result with Error reason -> Some(name, reason) | Ok _ -> None))
                @ (mapped |> List.filter (fun item -> List.contains (item.Owner, item.Name) duplicates)
                   |> List.map (fun item -> (item.Entry["symbol"].GetValue<string>()), $"operation name {item.Owner}.{item.Name} is not unique"))
            let mapped = mapped |> List.filter (fun item -> not (List.contains (item.Owner, item.Name) duplicates))
            let owners = mapped |> List.map (fun item -> item.Owner)
            let handles = (mapped |> List.collect (fun item -> item.Handles)) @ owners |> List.distinct |> List.filter ((<>) options.Capability)
            // A capability is created after every handle capability its operations name.
            let rec order created pending =
                match pending |> List.tryFind (fun name ->
                    mapped |> List.filter (fun item -> item.Owner = name) |> List.forall (fun item ->
                        item.Handles |> List.forall (fun handle -> handle = name || List.contains handle created))) with
                | Some next -> order (created @ [ next ]) (List.filter ((<>) next) pending)
                | None -> created, pending
            let ordered, cyclic = order [] (List.sort handles)
            let capabilities = ordered @ (if cyclic.IsEmpty then [ options.Capability ] else [])
            // Each line is a by-name request against committed declarations: commit each before the next.
            let request index (op: string) (fields: (string * JsonNode) list) = (node (("id", str $"bind-c-{index}") :: ("op", str op) :: fields)).ToJsonString()
            let lines =
                request 0 "simulateCreateEffect" [ "displayName", str options.Effect; "exported", JsonValue.Create true ]
                :: (capabilities |> List.mapi (fun index name ->
                    request (index + 1) "simulateCreateCapabilityByName"
                        [ "displayName", str name; "exported", JsonValue.Create true
                          "operations", array (mapped |> List.filter (fun item -> item.Owner = name) |> List.map (fun item -> item.Declaration.DeepClone())) ]))
            let table =
                node
                    [ "format", str CSharpMappingTable.format
                      "version", JsonValue.Create CSharpMappingTable.version
                      "library", str options.Library
                      "capabilities",
                      node (capabilities |> List.map (fun name ->
                          let operations = node (mapped |> List.filter (fun item -> item.Owner = name) |> List.map (fun item -> item.Name, item.Entry.DeepClone()))
                          name, (if name = options.Capability then node [ "operations", operations ] else node [ "handle", JsonValue.Create true; "operations", operations ]) :> JsonNode)) ]
            File.WriteAllLines(options.Requests, lines)
            File.WriteAllText(options.Table, table.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")
            let report =
                node
                    [ "requests", str options.Requests
                      "table", str options.Table
                      "capabilities", array (capabilities |> List.map str)
                      "unmapped", array (unmapped |> List.map (fun (name, reason) -> node [ "function", str name; "reason", str reason ] :> JsonNode))
                      "cyclicHandles", array (cyclic |> List.map str) ]
            Ok(report.ToJsonString())

    /// The CLI mode: exit 0 with the report on stdout, 2 on bad arguments or a clang failure.
    let run (arguments: string list) =
        match parse arguments with
        | None ->
            Console.Error.WriteLine usage
            2
        | Some options ->
            match generate options with
            | Error message ->
                Console.Error.WriteLine message
                2
            | Ok report ->
                Console.Out.WriteLine report
                0
