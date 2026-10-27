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
        "Usage: Nyrwvy.AuthoringSession bind-c --header <file.h> --library <native-name> --capability <Name> --effect <Name> --functions <f1,f2,...> --requests <out.jsonl> --table <out.csharp.json> [--prefix <c_prefix_>] [--type-prefix <CPrefix>] [--release <function>]... [--free <function>=<free-function>]... [--failure <function or result type>=null|negative|nonzero]... [--constant <parameter>=null|<integer>|<ENUMERATOR>]... [--buffer <capacity>[:<retry status>]] [--spec <operations.json>] [--clang-arg <arg>]..."

    [<RequireQualifiedAccess>]
    type private CType =
        | Integer of bits: int * signed: bool
        | Bool
        | Void
        | Text of isConst: bool
        | BytePointer
        | MutableBytes
        | Handle of tag: string
        /// A pointer to a handle: the function writes a new handle into it.
        | HandleOut of tag: string
        | Unmapped of string

    type private Options =
        { Header: string; Library: string; Capability: string; Effect: string; Prefix: string; TypePrefix: string
          Functions: string list; Requests: string; Table: string
          Releases: Set<string>; Frees: Map<string, string>; Failures: Map<string, string>
          Constants: Map<string, string>; Buffer: int; Retry: string option; Spec: string option; ClangArgs: string list }

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
            let known =
                set [ "--header"; "--library"; "--capability"; "--effect"; "--functions"; "--requests"; "--table"; "--prefix"; "--type-prefix"
                      "--release"; "--free"; "--failure"; "--constant"; "--buffer"; "--spec"; "--clang-arg" ]
            let assignments flag =
                all flag |> List.map (fun (item: string) ->
                    match item.Split('=', 2) with
                    | [| key; value |] -> key, value
                    | _ -> "", "") |> Map.ofList
            let buffer =
                match one "--buffer" |> Option.map (fun text -> text.Split(':', 2)) with
                | None -> Some(256, None)
                | Some [| capacity |] -> (match Int32.TryParse capacity with | true, value when value > 0 -> Some(value, None) | _ -> None)
                | Some [| capacity; retry |] -> (match Int32.TryParse capacity with | true, value when value > 0 -> Some(value, Some retry) | _ -> None)
                | Some _ -> None
            if pairs |> List.exists (fun (flag, _) -> not (Set.contains flag known)) then None
            else
                match one "--header", one "--library", one "--capability", one "--effect", one "--functions", one "--requests", one "--table", buffer with
                | Some header, Some library, Some capability, Some effect, Some functions, Some requests, Some table, Some(capacity, retry) ->
                    Some
                        { Header = header; Library = library; Capability = capability; Effect = effect
                          Prefix = defaultArg (one "--prefix") ""
                          TypePrefix = defaultArg (one "--type-prefix") ""
                          Functions = functions.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> List.ofArray
                          Requests = requests; Table = table
                          Releases = all "--release" |> Set.ofList
                          Frees = assignments "--free"; Failures = assignments "--failure"; Constants = assignments "--constant"
                          Buffer = capacity; Retry = retry; Spec = one "--spec"
                          ClangArgs = all "--clang-arg" }
                | _ -> None

    type private Declarations =
        { Typedefs: Map<string, string>
          CompleteRecords: Set<string>
          /// Name → (function type, parameters, variadic).
          Functions: Map<string, string * (string * string) list * bool>
          /// None: the value does not fit Int64.
          Enumerators: Map<string, int64 option> }

    let private declarations (root: JsonElement) =
        let nodes = root.GetProperty("inner").EnumerateArray() |> Seq.toList
        let text (node: JsonElement) (name: string) = match node.TryGetProperty name with | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() | _ -> null
        let kind node = text node "kind"
        let qualType (node: JsonElement) = match node.TryGetProperty "type" with | true, value -> text value "qualType" | _ -> null
        let named node = not (isNull (text node "name"))
        let children (node: JsonElement) = match node.TryGetProperty "inner" with | true, inner -> inner.EnumerateArray() |> Seq.toList | _ -> []
        // An enumerator without an initializer carries no value in the AST: it is the previous one plus one.
        let enumerators =
            nodes |> List.filter (fun node -> kind node = "EnumDecl") |> List.collect (fun node ->
                children node |> List.filter (fun item -> kind item = "EnumConstantDecl")
                |> List.mapFold (fun next item ->
                    let value =
                        // The ConstantExpr may sit under an implicit cast (an enumerator wider than int).
                        let rec constant (node: JsonElement) =
                            match kind node, text node "value" with
                            | "ConstantExpr", value when not (isNull value) -> Some value
                            | _ -> children node |> List.tryPick constant
                        match children item |> List.tryPick constant with
                        | Some literal -> (match Int64.TryParse literal with | true, number -> Some number | _ -> None)
                        | None -> next
                    (text item "name", value), (match value with Some number when number < Int64.MaxValue -> Some(number + 1L) | _ -> None)) (Some 0L)
                |> fst)
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
                let parameters = children node |> List.filter (fun item -> kind item = "ParmVarDecl") |> List.map (fun item -> text item "name", qualType item)
                let variadic = match node.TryGetProperty "variadic" with | true, value -> value.ValueKind = JsonValueKind.True | _ -> false
                text node "name", (qualType node, parameters, variadic))
            |> Map.ofList
          Enumerators = Map.ofList enumerators }

    let private exactIntegers =
        Map.ofList
            [ "int8_t", (8, true); "int16_t", (16, true); "int32_t", (32, true); "int64_t", (64, true)
              "uint8_t", (8, false); "uint16_t", (16, false); "uint32_t", (32, false); "uint64_t", (64, false)
              // LP64 only (checked before reading the header); Windows needs long as 32 bits.
              "size_t", (64, false); "ssize_t", (64, true); "intptr_t", (64, true); "uintptr_t", (64, false)
              "signed char", (8, true); "unsigned char", (8, false)
              "short", (16, true); "unsigned short", (16, false); "int", (32, true); "unsigned int", (32, false)
              "long", (64, true); "unsigned long", (64, false); "long long", (64, true); "unsigned long long", (64, false) ]

    let rec private classify (declarations: Declarations) (qualType: string) =
        let stripConst (text: string) = if text.StartsWith("const ", StringComparison.Ordinal) then text.Substring(6).Trim(), true else text, false
        let incomplete (target: string) =
            target.StartsWith("struct ", StringComparison.Ordinal) && not (Set.contains (target.Substring(7).Trim()) declarations.CompleteRecords)
        // A typedef of a pointer to an incomplete struct is a handle type named after the typedef.
        let handleTypedef (name: string) =
            match Map.tryFind name declarations.Typedefs with
            | Some target when target.EndsWith("*", StringComparison.Ordinal) && incomplete (fst (stripConst (target.Substring(0, target.Length - 1).Trim()))) -> Some name
            | _ -> None
        let rec resolve (name: string) depth =
            if depth > 16 || Map.containsKey name exactIntegers then name
            else match Map.tryFind name declarations.Typedefs with
                 | Some target when target <> name -> resolve (fst (stripConst target)) (depth + 1)
                 | _ -> name
        let text = (if isNull qualType then "" else qualType).Trim()
        if text.EndsWith("*", StringComparison.Ordinal) then
            let pointee, isConst = stripConst (text.Substring(0, text.Length - 1).Trim())
            match handleTypedef pointee with
            | Some name when not isConst -> CType.HandleOut name
            | Some _ -> CType.Unmapped $"'{text}' points to a handle the function only reads (describe it in --spec)"
            | None ->
            let target = resolve pointee 0
            if target.Contains '*' || target.Contains '(' then CType.Unmapped $"'{text}' is a pointer to a pointer or function"
            elif target = "char" then CType.Text isConst
            elif target = "unsigned char" || target = "uint8_t" then (if isConst then CType.BytePointer else CType.MutableBytes)
            elif incomplete target then CType.Handle(target.Substring(7).Trim())
            elif target.StartsWith("struct ", StringComparison.Ordinal) then CType.Unmapped $"'{text}' points to a complete struct (describe it in --spec)"
            else CType.Unmapped $"'{text}' is a pointer with no mapping (describe it in --spec)"
        else
            let bare = fst (stripConst text)
            match handleTypedef bare with
            | Some name -> CType.Handle name
            | None ->
            match resolve bare 0 with
            | "void" -> CType.Void
            | "_Bool" | "bool" -> CType.Bool
            | "char" -> CType.Unmapped "plain 'char' has target-dependent signedness; use signed char, unsigned char or int8_t/uint8_t"
            // shortcut: enums are taken as C int (true of every enum that fits int, and of `enum : int`); read fixedUnderlyingType when a library uses wider ones.
            | name when name.StartsWith("enum ", StringComparison.Ordinal) -> CType.Integer(32, true)
            | name when name.EndsWith("*", StringComparison.Ordinal) -> classify declarations name
            | name ->
                match Map.tryFind name exactIntegers with
                | Some(bits, signed) -> CType.Integer(bits, signed)
                | None -> CType.Unmapped $"'{text}' has no mapping (structs by value and floats: describe them in --spec)"

    let private camel (text: string) =
        let parts = text.Split('_', StringSplitOptions.RemoveEmptyEntries)
        parts |> Array.mapi (fun index part -> if index = 0 then part.ToLowerInvariant() else string (Char.ToUpperInvariant part[0]) + part.Substring(1).ToLowerInvariant()) |> String.concat ""

    let private pascal (text: string) =
        let value = camel text
        if value = "" then value else string (Char.ToUpperInvariant value[0]) + value.Substring(1)

    let private snake (text: string) =
        text |> Seq.mapi (fun index c -> if index > 0 && Char.IsUpper c then "_" + string (Char.ToLowerInvariant c) else string (Char.ToLowerInvariant c)) |> String.concat ""

    let private strip (prefix: string) (text: string) =
        if prefix <> "" && text.StartsWith(prefix, StringComparison.Ordinal) then text.Substring(prefix.Length) else text

    let private primitive name : JsonNode = JsonObject([ Collections.Generic.KeyValuePair("kind", JsonValue.Create "primitive" :> JsonNode); Collections.Generic.KeyValuePair("primitive", JsonValue.Create(name: string) :> JsonNode) ]) :> JsonNode
    let private node (pairs: (string * JsonNode) list) = JsonObject(pairs |> List.map (fun (key, value) -> Collections.Generic.KeyValuePair(key, value)))
    let private str (text: string) = JsonValue.Create text :> JsonNode
    let private array (items: JsonNode list) = JsonArray(items |> Array.ofList) :> JsonNode
    let private text (item: JsonNode) (name: string) =
        match item with
        | :? JsonObject as o when o.ContainsKey name && not (isNull o[name]) && o[name].GetValueKind() = JsonValueKind.String -> Some(o[name].GetValue<string>())
        | _ -> None

    type private Operation =
        { Owner: string; Name: string; Declaration: JsonNode; Entry: JsonNode; Handles: string list }

    /// A handle capability's name: the C name without its prefixes, in PascalCase.
    let private handleName (options: Options) (tag: string) =
        let name = strip options.TypePrefix (strip options.Prefix tag)
        if name.Contains '_' || name = "" then pascal name else string (Char.ToUpperInvariant name[0]) + name.Substring(1)

    let private cName ctype =
        match ctype with
        | CType.Integer(bits, signed) -> Some((if signed then "int" else "uint") + string bits)
        | CType.Bool -> Some "bool"
        | CType.Text true -> Some "utf8"
        | CType.Handle _ -> Some "handle"
        | CType.Void -> Some "void"
        | _ -> None

    let private graphType (c: string) =
        match c with
        | "bool" | "intBool" -> primitive "bool"
        | "float32" | "float64" -> primitive "float64"
        | "utf8" -> primitive "string"
        | "bytes" -> primitive "bytes"
        | "void" -> primitive "unit"
        | _ -> primitive "int64"

    /// A constant as the header names it: null, an integer, or an enumerator.
    let private constant (declarations: Declarations) (value: JsonNode) =
        match value with
        | null -> Ok(None, None)
        | _ when value.GetValueKind() = JsonValueKind.Number -> Ok(Some(value.GetValue<int64>()), None)
        | _ when value.GetValueKind() = JsonValueKind.String ->
            let name = value.GetValue<string>()
            match Int64.TryParse name with
            | true, number -> Ok(Some number, None)
            | _ when name = "null" -> Ok(None, None)
            | _ ->
                match Map.tryFind name declarations.Enumerators with
                | Some(Some number) -> Ok(Some number, Some name)
                | Some None -> Error $"enumerator '{name}' does not fit Int64"
                | None -> Error $"'{name}' is not an enumerator of the header"
        | _ -> Error "a constant is null, an integer or an enumerator name"

    /// Completes an entry against the C declaration (constant names, omitted C types, result and
    /// failure) and derives the operation's declaration from it.
    let private complete (options: Options) (declarations: Declarations) (owner: string) (name: string) (entry: JsonObject) =
        let symbol = defaultArg (text entry "symbol") ""
        match Map.tryFind symbol declarations.Functions with
        | None -> Error $"'{symbol}' is not declared in the header"
        | Some(functionType, parameters, _) ->
        let returnText = functionType.Substring(0, max 0 (functionType.IndexOf '(')).Trim()
        let classified = parameters |> List.map (fun (_, qualType) -> qualType, classify declarations qualType) |> Array.ofList
        let handles = ResizeArray<string>()
        let parametersOut = ResizeArray<JsonNode>()
        let problems = ResizeArray<string>()
        let mutable position = 0
        let mutable outType : JsonNode option = None
        let at index = if index < classified.Length then Some classified[index] else None
        let handleAt index wantOut =
            match at index with
            | Some(_, CType.Handle tag) when not wantOut -> Some(handleName options tag)
            | Some(_, CType.HandleOut tag) when wantOut -> Some(handleName options tag)
            | _ -> None
        let capability handle =
            handles.Add handle
            node [ "kind", str "capability"; "name", str handle ] :> JsonNode
        let inferC (item: JsonObject) index =
            if not (item.ContainsKey "c") then
                match at index |> Option.bind (snd >> cName) with
                | Some c -> item["c"] <- str c
                | None -> problems.Add $"argument {index}: name its C type with \"c\""
        let rec visit (item: JsonObject) inStruct =
            let index = position
            let has key = item.ContainsKey key
            if has "self" then position <- position + 1
            elif has "out" then
                let c = defaultArg (text item "out") ""
                if c = "bytes" then
                    position <- position + 3
                    outType <- Some(primitive "bytes")
                    if has "retry" then
                        match constant declarations item["retry"] with
                        | Ok(Some value, _) -> item["retry"] <- JsonValue.Create value
                        | Ok(None, _) -> problems.Add "retry is an integer status"
                        | Error reason -> problems.Add reason
                elif c = "handle" then
                    position <- position + 1
                    match handleAt index true with
                    | Some handle -> outType <- Some(capability handle)
                    | None -> problems.Add $"argument {index}: not a pointer to a handle"
                else
                    position <- position + 1
                    outType <- Some(graphType c)
            elif has "struct" then
                if not inStruct then position <- position + 1
                match item["struct"] with
                | :? JsonArray as fields -> for field in fields do (match field with :? JsonObject as o -> visit o true | _ -> problems.Add "a struct field is an object")
                | _ -> problems.Add "struct is an array of fields"
            elif has "sizeof" then ()
            elif has "value" then
                if not inStruct then
                    position <- position + 1
                    let pointer = match at index with Some(qualType, _) -> (qualType: string).TrimEnd().EndsWith "*" | None -> false
                    match constant declarations item["value"] with
                    | Error reason -> problems.Add reason
                    | Ok(value, enumerator) ->
                        item["value"] <- (match value with Some number -> JsonValue.Create number :> JsonNode | None -> null)
                        enumerator |> Option.iter (fun label -> item["name"] <- str label)
                        if not (item.ContainsKey "c") then (if pointer && value.IsNone then item["c"] <- str "pointer" else inferC item index)
                else
                    match constant declarations item["value"] with
                    | Ok(value, enumerator) ->
                        item["value"] <- (match value with Some number -> JsonValue.Create number :> JsonNode | None -> null)
                        enumerator |> Option.iter (fun label -> item["name"] <- str label)
                    | Error reason -> problems.Add reason
            else
                if not inStruct then
                    position <- position + 1
                    inferC item index
                let c = defaultArg (text item "c") ""
                if c = "bytes" then position <- position + 1
                // A handle the header passes as `void *` names its capability; the hint is not part of the table.
                let hint = text item "capability"
                item.Remove "capability" |> ignore
                let parameterType =
                    if c = "handle" then
                        match handleAt index false |> Option.orElse hint with
                        | Some handle -> capability handle
                        | None -> problems.Add $"argument {index}: not a handle (name its \"capability\")"; primitive "unit"
                    else graphType c
                parametersOut.Add(node [ "displayName", str (defaultArg (text item "from") ""); "type", parameterType ])
        match entry["arguments"] with
        | :? JsonArray as items -> for item in items do (match item with :? JsonObject as o -> visit o false | _ -> problems.Add "an argument is an object")
        | null -> ()
        | _ -> problems.Add "arguments is an array"
        if problems.Count = 0 && position <> classified.Length then problems.Add $"the arguments cover {position} of {classified.Length} C parameters"
        let failure =
            match text entry "failure" with
            | Some value -> Some value
            | None ->
                Map.tryFind symbol options.Failures |> Option.orElse (Map.tryFind returnText options.Failures)
                |> Option.map (fun value -> entry["failure"] <- str value; value)
        let returned = classify declarations returnText
        if not (entry.ContainsKey "result") then
            match cName returned with
            | Some c -> entry["result"] <- node [ "c", str c ]
            | None -> problems.Add $"result: name its C type with \"result\""
        let resultC = match entry["result"] with :? JsonObject as result -> defaultArg (text result "c") "" | _ -> ""
        let okType =
            match outType with
            | Some value -> value
            | None when failure = Some "nonzero" -> primitive "unit"
            | None when resultC = "handle" ->
                match returned with
                | CType.Handle tag -> capability (handleName options tag)
                | _ -> problems.Add "result: not a handle"; primitive "unit"
            | None -> graphType resultC
        if problems.Count > 0 then Error(String.concat "; " problems)
        else
        let resultType =
            match failure with
            | Some _ -> node [ "kind", str "builtinGeneric"; "generic", str "result"; "arguments", array [ okType; primitive "string" ] ] :> JsonNode
            | None -> okType
        Ok
            { Owner = owner; Name = name; Entry = entry; Handles = List.ofSeq handles
              Declaration =
                node
                    [ "displayName", str name
                      "parameters", array (List.ofSeq parametersOut)
                      "resultType", resultType
                      "effect", str options.Effect ] }

    /// One function mapped from its header declaration alone, or why it cannot be.
    let private operation (options: Options) (declarations: Declarations) (functionName: string) =
        match Map.tryFind functionName declarations.Functions with
        | None -> Error "not declared in the header"
        | Some(functionType, parameters, variadic) ->
            let returnText = functionType.Substring(0, max 0 (functionType.IndexOf '('))
            if functionType.Contains "(*" || returnText.Trim() = "" then Error $"'{functionType}' returns a function pointer"
            elif variadic || functionType.Contains "..." then Error "variadic functions are out of scope"
            elif functionType.TrimEnd().EndsWith("()", StringComparison.Ordinal) then Error "the declaration does not specify its parameters (write '(void)')"
            else
            let isSize qualType = not (isNull qualType) && (qualType: string).Trim() = "size_t"
            let isSizePointer (qualType: string) = not (isNull qualType) && qualType.Replace(" ", "") = "size_t*"
            let isRelease = Set.contains functionName options.Releases
            let item (pairs: (string * JsonNode) list) = node pairs :> JsonNode
            // C parameters in order, with the size_t that follows a byte pointer folded into it.
            let rec arguments index (rest: (string * string) list) =
                match rest with
                | [] -> Ok []
                | (name, _) :: _ when String.IsNullOrEmpty name -> Error "a parameter has no name"
                | (name, qualType) :: tail when Map.containsKey name options.Constants ->
                    let pointer = not (isNull qualType) && qualType.TrimEnd().EndsWith "*"
                    let value = Map.find name options.Constants
                    let c = if pointer then Some "pointer" else cName (classify declarations qualType)
                    match c with
                    | None -> Error $"parameter {name}: a constant needs an integer or pointer parameter"
                    | Some c -> arguments (index + 1) tail |> Result.map (fun items -> item [ "value", str value; "c", str c ] :: items)
                | (name, qualType) :: (_, size) :: (_, written) :: tail
                    when (match classify declarations qualType with CType.Text false | CType.MutableBytes -> true | _ -> false) && isSize size && isSizePointer written ->
                    let fields = [ "out", str "bytes"; "capacity", JsonValue.Create options.Buffer :> JsonNode ] @ (options.Retry |> Option.map (fun retry -> [ "retry", str retry ]) |> Option.defaultValue [])
                    arguments (index + 3) tail |> Result.map (fun items -> item fields :: items)
                | (name, qualType) :: (_, size) :: tail
                    when (match classify declarations qualType with CType.Text _ | CType.BytePointer | CType.MutableBytes -> true | _ -> false) && isSize size ->
                    arguments (index + 2) tail |> Result.map (fun items -> item [ "from", str (camel name); "c", str "bytes" ] :: items)
                | (name, qualType) :: tail ->
                    match classify declarations qualType with
                    | CType.Unmapped reason -> Error $"parameter {name}: {reason}"
                    | CType.BytePointer | CType.MutableBytes -> Error $"parameter {name}: a byte pointer must be followed by its size_t length"
                    | CType.Text false -> Error $"parameter {name}: 'char *' may be written by the library; only 'const char *' maps to String"
                    | CType.Void -> Error $"parameter {name}: void"
                    | CType.HandleOut _ -> arguments (index + 1) tail |> Result.map (fun items -> item [ "out", str "handle" ] :: items)
                    | CType.Handle _ when index = 0 && not isRelease -> arguments (index + 1) tail |> Result.map (fun items -> item [ "self", JsonValue.Create true ] :: items)
                    | ctype ->
                        let fields = [ "from", str (camel name); "c", str (cName ctype).Value ]
                        let fields = if isRelease && ctype.IsHandle then fields @ [ "release", JsonValue.Create true :> JsonNode ] else fields
                        arguments (index + 1) tail |> Result.map (fun items -> item fields :: items)
            match arguments 0 parameters with
            | Error reason -> Error reason
            | Ok items ->
            let handleCount = parameters |> List.filter (fun (_, qualType) -> (classify declarations qualType).IsHandle) |> List.length
            let receiver =
                match items, parameters with
                | first :: _, (_, qualType) :: _ when (text first "from").IsNone && first.AsObject().ContainsKey "self" ->
                    match classify declarations qualType with CType.Handle tag -> Some tag | _ -> None
                | _ -> None
            let result = classify declarations returnText
            let failure = Map.tryFind functionName options.Failures |> Option.orElse (Map.tryFind (returnText.Trim()) options.Failures)
            let free = Map.tryFind functionName options.Frees
            let hasOut = items |> List.exists (fun item -> item.AsObject().ContainsKey "out")
            let resultProblem =
                match result, failure, free with
                | _ when isRelease && handleCount <> 1 -> Some "a release function takes exactly one handle"
                | _ when hasOut && (items |> List.filter (fun item -> item.AsObject().ContainsKey "out") |> List.length) > 1 -> Some "more than one out-parameter"
                | CType.Unmapped reason, _, _ -> Some $"result: {reason}"
                | (CType.BytePointer | CType.MutableBytes), _, _ -> Some "result: a byte pointer has no length (Bytes results are out of scope)"
                | CType.Text false, _, None -> Some "result: 'char *' is owned by the caller; name its free function with --free"
                | CType.Text true, _, Some _ -> Some "result: a 'const char *' result is borrowed; --free does not apply"
                | _, Some "null", _ when (match result with CType.Text _ | CType.Handle _ -> false | _ -> true) -> Some "--failure null needs a pointer result"
                | _, Some "negative", _ when (match result with CType.Integer(_, true) -> false | _ -> true) -> Some "--failure negative needs a signed integer result"
                | _, Some "nonzero", _ when (match result with CType.Integer _ -> false | _ -> true) -> Some "--failure nonzero needs an integer result"
                | _, Some value, _ when not (List.contains value [ "null"; "negative"; "nonzero" ]) -> Some $"unknown failure '{value}'"
                | _, _, Some _ when (match result with CType.Text false -> false | _ -> true) -> Some "--free applies to a 'char *' result"
                | _ -> None
            match resultProblem with
            | Some reason -> Error reason
            | None ->
            let resultNode = node [ "c", str (if result = CType.Text false then "utf8" else (cName result).Value) ]
            free |> Option.iter (fun symbol -> resultNode["free"] <- str symbol)
            let entry = node [ "symbol", str functionName; "arguments", array items; "result", resultNode ]
            failure |> Option.iter (fun value -> entry["failure"] <- str value)
            let stripped = strip options.Prefix functionName
            let owner, name =
                match receiver with
                | Some tag -> handleName options tag, camel (strip (snake (handleName options tag) + "_") stripped)
                | None -> options.Capability, camel stripped
            complete options declarations owner name entry

    let private clangWith (arguments: string list) =
        let info = ProcessStartInfo("clang")
        for argument in arguments do
            info.ArgumentList.Add argument
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        use child = Process.Start info
        let errors = child.StandardError.ReadToEndAsync()
        let output = child.StandardOutput.ReadToEnd()
        child.WaitForExit()
        if child.ExitCode <> 0 then Error $"clang failed: {errors.Result}" else Ok output

    /// The integer mapping (long and size_t as 64 bits) holds only for LP64 targets.
    let private clang (options: Options) =
        match clangWith ([ "-x"; "c"; "-dM"; "-E" ] @ options.ClangArgs @ [ "/dev/null" ]) with
        | Error message -> Error message
        | Ok macros when not (macros.Contains "#define __LP64__ 1") -> Error "bind-c supports LP64 targets only (Linux, macOS); clang's target is not LP64."
        | Ok _ -> clangWith ([ "-x"; "c"; "-Xclang"; "-ast-dump=json"; "-fsyntax-only" ] @ options.ClangArgs @ [ options.Header ])

    /// Hand-described operations: `{ "capabilities": { Name: { "operations": { name: entry } } } }`, entries
    /// as in the table, where a constant may name an enumerator and `c`, `result` and `failure` may be
    /// left to the header and the flags.
    let private specOperations (options: Options) (declarations: Declarations) =
        match options.Spec with
        | None -> Ok []
        | Some path ->
            try
                let spec = JsonNode.Parse(File.ReadAllText path)
                Ok
                    [ for capability in spec["capabilities"].AsObject() do
                          for operation in capability.Value["operations"].AsObject() do
                              let entry = operation.Value.DeepClone().AsObject()
                              let symbol = defaultArg (text entry "symbol") ""
                              yield symbol, complete options declarations capability.Key operation.Key entry |> Result.mapError (fun reason -> $"{capability.Key}.{operation.Key}: {reason}") ]
            with error -> Error $"--spec {path}: {error.Message}"

    /// Writes the requests and the table and returns the report, or an error message.
    let private generate (options: Options) =
        match clang options with
        | Error message -> Error message
        | Ok ast ->
            use document = JsonDocument.Parse(ast, JsonDocumentOptions(MaxDepth = 4096))
            let declarations = declarations document.RootElement
            match specOperations options declarations with
            | Error message -> Error message
            | Ok described ->
            let describedSymbols = described |> List.map fst |> Set.ofList
            let results =
                (options.Functions |> List.filter (fun name -> not (Set.contains name describedSymbols)) |> List.map (fun name -> name, operation options declarations name))
                @ described
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
