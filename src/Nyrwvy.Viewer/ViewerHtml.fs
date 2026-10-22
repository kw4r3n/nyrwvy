namespace Nyrwvy.Core

open System.Net
open System.Text

[<RequireQualifiedAccess>]
module ViewerHtml =
    type private HtmlWriter() =
        let builder = StringBuilder()
        let mutable indent = 0

        member _.Line(text: string) =
            if text.Length > 0 then
                builder.Append(' ', indent * 2) |> ignore
            builder.Append(text).Append('\n') |> ignore

        member _.Push() = indent <- indent + 1
        member _.Pop() = indent <- indent - 1
        override _.ToString() = builder.ToString()

    let private html (value: string): string = WebUtility.HtmlEncode(value)
    let private code (value: string): string = "<code>" + html value + "</code>"
    /// Type parameter display names come from the declaring function or type, never from an
    /// occurrence; a focus document collects them from every declaration it shows.
    let rec private typeRefTextIn (names: Map<TypeParameterId, string>) (typeRef: TypeRef): string =
        let typeRefText = typeRefTextIn names
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> "String"
        | TypeRef.Primitive PrimitiveType.Bool -> "Bool"
        | TypeRef.Primitive PrimitiveType.Unit -> "Unit"
        | TypeRef.Primitive PrimitiveType.Int64 -> "Int64"
        | TypeRef.Primitive PrimitiveType.Float64 -> "Float64"
        | TypeRef.Primitive PrimitiveType.Bits32 -> "Bits32"
        | TypeRef.Named(typeId, []) -> StableIdText.typeId typeId
        | TypeRef.Named(typeId, arguments) -> StableIdText.typeId typeId + "<" + (arguments |> List.map typeRefText |> String.concat ", ") + ">"
        | TypeRef.Capability capabilityId -> StableIdText.capabilityId capabilityId
        | TypeRef.Function(arguments, result) -> "(" + (arguments |> List.map typeRefText |> String.concat ", ") + ") -> " + typeRefText result
        | TypeRef.TypeParameter id -> TypeParameters.nameIn names id
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
            "Option<" + typeRefText item + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            "Result<" + typeRefText okType + ", " + typeRefText errorType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) ->
            "Map<" + typeRefText keyType + ", " + typeRefText valueType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) ->
            "List<" + typeRefText item + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ item ]) ->
            "Set<" + typeRefText item + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ result ]) ->
            "OneShot<" + typeRefText result + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ result ]) ->
            "Task<" + typeRefText result + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, []) -> "Exception"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> "TaskScope"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, arguments) ->
            "OneShot<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) ->
            "Task<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, arguments) ->
            invalidOp $"Exception unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) ->
            "TaskScope<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            "Option<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            "Result<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, arguments) ->
            "Map<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            "List<invalid-arity:" + string arguments.Length + ">"

        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, arguments) ->
            "Set<invalid-arity:" + string arguments.Length + ">"

    let private targetText (target: ViewerTarget): string =
        match target with
        | ViewerTarget.Node id -> StableIdText.graphNodeId id
        | ViewerTarget.Function id -> StableIdText.functionId id
        | ViewerTarget.Parameter id -> StableIdText.parameterId id
        | ViewerTarget.PatternBinding id -> StableIdText.patternBindingId id
        | ViewerTarget.CapabilityOperation id -> StableIdText.capabilityOperationId id
        | ViewerTarget.Type id -> StableIdText.typeId id
        | ViewerTarget.Field id -> StableIdText.fieldId id
        | ViewerTarget.Variant id -> StableIdText.variantId id

    let private relationLabel (kind: ViewerRelationKind): string =
        match kind with
        | ViewerRelationKind.DependsOn -> "depends on"
        | ViewerRelationKind.CallsFunction -> "calls function"
        | ViewerRelationKind.ReferencesFunction -> "references function"
        | ViewerRelationKind.ReadsParameter -> "reads parameter"
        | ViewerRelationKind.ReadsPatternBinding -> "reads pattern binding"
        | ViewerRelationKind.InvokesCapabilityOperation -> "invokes capability operation"
        | ViewerRelationKind.ConstructsType -> "constructs type"
        | ViewerRelationKind.AccessesField -> "accesses field"
        | ViewerRelationKind.ConstructsVariant -> "constructs variant"
        | ViewerRelationKind.BranchCondition -> "branch condition"
        | ViewerRelationKind.MatchInput -> "match input"
        | ViewerRelationKind.BindInput -> "evaluate before body"

    let private valueDependencyLabel (role: ValueDependencyRole): string =
        match role with
        | ValueDependencyRole.Value -> "value"
        | ValueDependencyRole.Error -> "error"
        | ValueDependencyRole.Substring -> "substring"
        | ValueDependencyRole.SliceStart -> "slice start"
        | ValueDependencyRole.TaskScope -> "task scope"
        | ValueDependencyRole.Limit -> "limit"
        | ValueDependencyRole.Index -> "index"
        | ValueDependencyRole.Key -> "key"
        | ValueDependencyRole.Amount -> "amount"
        | ValueDependencyRole.SliceEnd -> "slice end"
        | ValueDependencyRole.SearchFrom -> "search from"
        | ValueDependencyRole.CharacterSet -> "character set"
        | ValueDependencyRole.Prefix -> "prefix"
        | ValueDependencyRole.Suffix -> "suffix"
        | ValueDependencyRole.Separator -> "separator"
        | ValueDependencyRole.Replacement -> "replacement"
        | ValueDependencyRole.LeftOperand -> "left operand"
        | ValueDependencyRole.RightOperand -> "right operand"
        | ValueDependencyRole.Source -> "source"
        | ValueDependencyRole.Item -> "item"
        | ValueDependencyRole.ListItem index -> "list item [" + string index + "]"
        | ValueDependencyRole.FunctionValue -> "function value"
        | ValueDependencyRole.FunctionArgument index -> "function argument " + string index
        | ValueDependencyRole.CallArgument parameterId -> "call argument " + StableIdText.parameterId parameterId
        | ValueDependencyRole.CapabilityValue -> "capability value"
        | ValueDependencyRole.CapabilityArgument parameterId ->
            "capability argument " + StableIdText.capabilityParameterId parameterId
        | ValueDependencyRole.FieldValue fieldId -> "field value " + StableIdText.fieldId fieldId
        | ValueDependencyRole.BranchCondition -> "branch condition"
        | ValueDependencyRole.MatchInput -> "match input"
        | ValueDependencyRole.BindInput -> "evaluate before body"

    /// The label of an operation whose established wording differs from the one `derivedOperationLabel`
    /// gives its kind name. A new builtin needs no entry: it is labelled from its name.
    let private operationLabels: Map<string, string> =
        Map.ofList
            [ "ParameterReference", "parameter reference"
              "PatternReference", "pattern reference"
              "UnitLiteral", "unit literal"
              "StringIsEmpty", "string is empty"
              "Int64ToString", "int64 to string"
              "Int64ToHexString", "int64 to hex string"
              "StringParseInt64", "parse int64"
              "StringParseInt64Hex", "parse hex int64"
              "Float64ToString", "float64 to string"
              "StringParseFloat64", "parse float64"
              "StringCodePoints", "code points"
              "StringToLower", "to lower"
              "StringToUpper", "to upper"
              "StringDecodeUtf8Lossy", "UTF-8 decode (replace)"
              "StringEncodeUtf8", "UTF-8 encode"
              "Float64RoundToInt64", "round to int64"
              "Float64FloorToInt64", "floor to int64"
              "Float64Sqrt", "square root"
              "Float64Abs", "absolute value"
              "Float64Negate", "negate"
              "Float64Floor", "floor"
              "Float64Ceiling", "ceiling"
              "Float64Truncate", "truncate"
              "StringUtf8ByteCount", "utf-8 byte count"
              "StringFromCodePoint", "from code point"
              "StringToCodePoint", "to code point"
              "StringLength", "string length"
              "StringSlice", "string slice"
              "StringContains", "string contains"
              "StringIndexOf", "string index of"
              "StringSpan", "string span"
              "StringSpanBack", "string span back"
              "StringLastIndexOf", "string last index of"
              "StringStartsWith", "string starts with"
              "StringEndsWith", "string ends with"
              "StringSplit", "string split"
              "StringSplitLines", "string split lines"
              "StringReplace", "string replace"
              "StringTrim", "trim whitespace"
              "StringTrimStart", "trim leading whitespace"
              "StringTrimEnd", "trim trailing whitespace"
              "StringJoin", "string join"
              "StringEqual", "string equal"
              "StringLessThan", "string less than"
              "ValueEqual", "value equal"
              "StringConcat", "string concat"
              "Int64BitAnd", "Int64 bitwise and"
              "Int64BitOr", "Int64 bitwise or"
              "Int64BitXor", "Int64 bitwise xor"
              "Int64ShiftRight", "Int64 arithmetic shift right"
              "Float64IsNaN", "Float64 is NaN"
              "Int64ToFloat64", "int64 to float64"
              "Int64BitNot", "Int64 bitwise not"
              "Bits32FromInt64", "bits32 from int64"
              "Bits32ToInt64", "bits32 to int64"
              "Bits32Xor", "bits32 xor"
              "Bits32And", "bits32 and"
              "Bits32Or", "bits32 or"
              "Bits32Not", "bits32 not"
              "Bits32Add", "bits32 add"
              "Bits32RotateRight", "bits32 rotate right"
              "Bits32ShiftRight", "bits32 shift right"
              "Bits32ShiftLeft", "bits32 shift left"
              "MapUnion", "Map union (right wins)"
              "UseResource", "use resource"
              "ParallelAll", "parallel all"
              "CancellationRequested", "cancellation requested"
              "ExceptionCatch", "exception catch"
              "ExceptionIsCancellation", "exception is cancellation"
              "ExceptionMessage", "exception message"
              "ExceptionRethrow", "exception rethrow"
              "CancellationShield", "cancellation shield"
              "TaskScope", "task scope"
              "TaskStart", "task start"
              "OneShotCreate", "one-shot create"
              "OneShotComplete", "one-shot complete (first value)"
              "OneShotWait", "one-shot wait"
              "TaskCancel", "task cancel"
              "ResultMapErr", "Result map error"
              "FunctionReference", "function reference"
              "FunctionInvoke", "function invocation"
              "DirectCall", "direct call"
              "CapabilityInvoke", "capability invoke"
              "ProductConstruct", "product construct"
              "FieldAccess", "field access"
              "ProductUpdate", "product update"
              "UnionConstruct", "union construct"
              "OptionNone", "Option.None"
              "OptionSome", "Option.Some"
              "ResultOk", "Result.Ok"
              "Abort", "Abort (function exit)"
              "ResultErr", "Result.Err"
              "BoolNot", "bool not"
              "BoolAnd", "bool and"
              "BoolOr", "bool or"
              "Conditional", "conditional"
              "MatchUnion", "union match"
              "Bind", "bind once"
              "MatchOption", "Option match"
              "MatchResult", "Result match" ]

    /// "ListMinBy" -> "List min by": the kind name split at its word boundaries, the first word kept
    /// as written (a type name, mostly) and the rest lowercased.
    let private derivedOperationLabel (kind: string): string =
        let words = ResizeArray<string>()
        let current = StringBuilder()
        for index in 0 .. kind.Length - 1 do
            let character = kind[index]
            let startsWord =
                index > 0
                && System.Char.IsUpper character
                && (System.Char.IsLower kind[index - 1]
                    || System.Char.IsDigit kind[index - 1]
                    || (System.Char.IsUpper kind[index - 1] && index + 1 < kind.Length && System.Char.IsLower kind[index + 1]))
            if startsWord then
                words.Add(current.ToString())
                current.Clear() |> ignore
            current.Append(character) |> ignore
        words.Add(current.ToString())
        words
        |> Seq.mapi (fun index word -> if index = 0 then word else word.ToLowerInvariant())
        |> String.concat " "

    let private nodeRoleLabel (role: ViewerNodeRole): string =
        match role with
        | ViewerNodeRole.StringLiteral value -> "string literal “" + value + "”"
        | ViewerNodeRole.BoolLiteral value -> "bool literal " + (if value then "true" else "false")
        | ViewerNodeRole.Int64Literal value -> "Int64 literal " + string value
        | ViewerNodeRole.Float64Literal bits -> "Float64 literal 0x" + Float64Bits.toHex bits
        | ViewerNodeRole.TaskWait TaskWaitMode.Any -> "task wait (any)"
        | ViewerNodeRole.TaskWait TaskWaitMode.All -> "task wait (all)"
        | ViewerNodeRole.CapabilityConstruct(capabilityId, implementations) ->
            "capability construction " + StableId.value capabilityId + " (" +
            (implementations |> List.map (fun (operationId, functionId) -> StableId.value operationId + " -> " + StableId.value functionId) |> String.concat "; ") + ")"
        | ViewerNodeRole.Operation kind ->
            operationLabels |> Map.tryFind kind |> Option.defaultWith (fun () -> derivedOperationLabel kind)

    let private branchLabel (role: ViewerBranchRole): string =
        match role with
        | ViewerBranchRole.WhenTrue -> "when true"
        | ViewerBranchRole.WhenFalse -> "when false"
        | ViewerBranchRole.UnionVariant variantIds -> "variant " + (variantIds |> List.map (StableIdText.variantId) |> String.concat " | ")
        | ViewerBranchRole.BindBody -> "body after input"
        | ViewerBranchRole.OptionSome -> "Option.Some"
        | ViewerBranchRole.OptionNone -> "Option.None"
        | ViewerBranchRole.ResultOk -> "Result.Ok"
        | ViewerBranchRole.ResultErr -> "Result.Err"
        | ViewerBranchRole.RightOperand -> "right operand"

    let private readinessLabel (readiness: ChangeReviewReadiness): string =
        match readiness with
        | ChangeReviewReadiness.Complete ->
            "complete — lowering may proceed if backend readiness also passes"
        | ChangeReviewReadiness.Incomplete ids ->
            "incomplete — blocking obligations: "
            + (ids |> List.map (StableId.value >> fun value -> "obligation:" + value) |> String.concat ", ")
        | ChangeReviewReadiness.Invalid issues ->
            "invalid — "
            + (issues
               |> List.map (fun issue -> issue.Code + ": " + issue.Message)
               |> String.concat " | ")

    let private emitDocumentStart (writer: HtmlWriter) (title: string) =
        writer.Line("<!doctype html>")
        writer.Line("<html lang=\"en\">")
        writer.Line("<head>")
        writer.Push()
        writer.Line("<meta charset=\"utf-8\">")
        writer.Line("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
        writer.Line("<title>" + html title + "</title>")
        writer.Line("<style>")
        writer.Line("  :root { font-family: ui-sans-serif, system-ui, sans-serif; color-scheme: light dark; }")
        writer.Line("  body { max-width: 1180px; margin: 0 auto; padding: 1.5rem; line-height: 1.45; }")
        writer.Line("  header, section, article, details { border: 1px solid CanvasText; border-radius: .55rem; padding: .8rem; margin: .75rem 0; }")
        writer.Line("  .graph { margin-left: .5rem; }")
        writer.Line("  .nodes { display: grid; gap: .65rem; }")
        writer.Line("  .node { border-left: .4rem solid CanvasText; padding-left: .8rem; }")
        writer.Line("  .branch { margin-left: 1rem; border-left: .2rem dashed CanvasText; padding-left: .75rem; }")
        writer.Line("  .status, .missing { font-weight: 700; }")
        writer.Line("  code { overflow-wrap: anywhere; }")
        writer.Line("  table { border-collapse: collapse; width: 100%; }")
        writer.Line("  th, td { border: 1px solid CanvasText; padding: .35rem .5rem; text-align: left; vertical-align: top; }")
        writer.Line("  ul { padding-left: 1.3rem; }")
        writer.Line("</style>")
        writer.Pop()
        writer.Line("</head>")
        writer.Line("<body>")

    let private emitDocumentEnd (writer: HtmlWriter) =
        writer.Line("</body>")
        writer.Line("</html>")

    let private emitParameters (writer: HtmlWriter) (names: Map<TypeParameterId, string>) (parameters: SemanticParameter list) =
        let typeRefText = typeRefTextIn names
        writer.Line("<table>")
        writer.Line("<thead><tr><th>Parameter</th><th>Type</th><th>Stable ID</th></tr></thead>")
        writer.Line("<tbody>")
        writer.Push()
        for parameter in parameters do
            writer.Line(
                "<tr><td>" + html parameter.DisplayName
                + "</td><td>" + code (typeRefText parameter.Type)
                + "</td><td>" + code (StableIdText.parameterId parameter.Id) + "</td></tr>")
        writer.Pop()
        writer.Line("</tbody>")
        writer.Line("</table>")

    let private emitRequiredEffects (writer: HtmlWriter) (effects: EffectId list) =
        let text =
            match effects with
            | [] -> "None."
            | effects ->
                effects
                |> List.sortBy StableId.value
                |> List.map (StableIdText.effectId >> code)
                |> String.concat ", "
        writer.Line("<p>Declared required effects: " + text + "</p>")

    let private emitBodyEffects (writer: HtmlWriter) (report: BodyEffectRequirements) =
        writer.Line("<section><h2>Body effect requirements (derived)</h2>")
        if List.isEmpty report.Evidence then writer.Line("<p>No resolved nominal effect requirements.</p>")
        else
            writer.Line("<ul>")
            for item in report.Evidence do
                let source =
                    match item.Source with
                    | EffectRequirementSource.FunctionContract id -> StableIdText.functionId id
                    | EffectRequirementSource.CapabilityOperation id -> StableIdText.capabilityOperationId id
                writer.Line("<li>" + code (StableIdText.effectId item.EffectId) + " at " + code (StableIdText.graphNodeId item.NodeId) + " from " + code source + "</li>")
            writer.Line("</ul>")
        for id in report.UnresolvedFunctionIds do writer.Line("<p>Unresolved function contract: " + code (StableIdText.functionId id) + "</p>")
        for id in report.UnresolvedOperationIds do writer.Line("<p>Unresolved operation contract: " + code (StableIdText.capabilityOperationId id) + "</p>")
        writer.Line("</section>")

    let private emitCallSites
        (writer: HtmlWriter)
        (heading: string)
        (sites: QueryCallSite list)
        (endpoint: QueryCallSite -> FunctionId) =
        writer.Line("<section>")
        writer.Line("<h3>" + html heading + "</h3>")
        if List.isEmpty sites then
            writer.Line("<p>None.</p>")
        else
            writer.Line("<ul>")
            for site in sites do
                writer.Line(
                    "<li>" + code (StableIdText.functionId (endpoint site))
                    + " via " + code (StableIdText.graphNodeId site.CallNodeId) + "</li>")
            writer.Line("</ul>")
        writer.Line("</section>")

    let private emitRelations (writer: HtmlWriter) (relations: ViewerRelation list) =
        if not (List.isEmpty relations) then
            writer.Line("<ul aria-label=\"node relations\">")
            for relation in relations do
                writer.Line(
                    "<li>" + html (relationLabel relation.Kind) + ": "
                    + code (targetText relation.Target) + "</li>")
            writer.Line("</ul>")

    let private emitValueDependencies (writer: HtmlWriter) (dependencies: ValueDependency list) =
        if not (List.isEmpty dependencies) then
            writer.Line("<ul aria-label=\"value dependencies\">")
            for dependency in dependencies do
                writer.Line(
                    "<li>" + html (valueDependencyLabel dependency.Role) + ": "
                    + code (StableIdText.graphNodeId dependency.NodeId) + "</li>")
            writer.Line("</ul>")

    let rec private emitGraph (writer: HtmlWriter) (names: Map<TypeParameterId, string>) (graph: ViewerGraph) =
        let typeRefText = typeRefTextIn names
        writer.Line("<section class=\"graph\" aria-label=\"value graph\">")
        writer.Line("<p>Graph result: " + code (StableIdText.graphNodeId graph.ResultNodeId) + "</p>")
        writer.Line("<div class=\"nodes\">")
        writer.Push()
        for node in graph.Nodes do
            writer.Line("<article class=\"node\">")
            writer.Line("<h4>" + html (nodeRoleLabel node.Role) + "</h4>")
            writer.Line("<p>Stable ID: " + code (StableIdText.graphNodeId node.Id) + "</p>")
            match node.Type with
            | Some nodeType -> writer.Line("<p>Type: " + code (typeRefText nodeType) + "</p>")
            | None -> writer.Line("<p>Type: <strong>unknown / not derivable</strong></p>")
            emitRelations writer (node.Relations |> List.filter (fun relation -> relation.Kind <> ViewerRelationKind.DependsOn))
            emitValueDependencies writer node.ValueDependencies
            for branch in node.Branches do
                writer.Line("<section class=\"branch\">")
                writer.Line("<h5>Branch: " + html (branchLabel branch.Role) + "</h5>")
                if not (List.isEmpty branch.Bindings) then
                    writer.Line("<ul aria-label=\"branch bindings\">")
                    for binding in branch.Bindings do
                        let displayName = binding.DisplayName |> Option.defaultValue "(unnamed)"
                        let sourceField =
                            match binding.SourceFieldId with
                            | Some fieldId -> "; source field " + code (StableIdText.fieldId fieldId)
                            | None -> ""
                        writer.Line(
                            "<li>" + html displayName + " — "
                            + code (StableIdText.patternBindingId binding.Id)
                            + "; type " + code (typeRefText binding.Type)
                            + sourceField + "</li>")
                    writer.Line("</ul>")
                emitGraph writer names branch.Graph
                writer.Line("</section>")
            writer.Line("</article>")
        writer.Pop()
        writer.Line("</div>")
        writer.Line("</section>")

    /// A declaration's visibility across a dependency boundary, as a short label.
    let private visibility (exported: bool) = if exported then "exported" else "private"

    let private emitTypeEntity (writer: HtmlWriter) (typeEntity: SemanticType) =
        let typeRefText = typeRefTextIn (Map.ofList typeEntity.TypeParameters)
        writer.Line(
            "<details><summary>" + html typeEntity.DisplayName + " — "
            + code (StableIdText.typeId typeEntity.Id) + " — " + visibility typeEntity.Exported + "</summary>")
        match typeEntity.Kind with
        | SemanticTypeKind.Product fields ->
            writer.Line("<p>Product fields</p><ul>")
            for field in fields |> List.sortBy (fun item -> StableId.value item.Id) do
                writer.Line(
                    "<li>" + html field.DisplayName + ": " + code (typeRefText field.Type)
                    + " — " + code (StableIdText.fieldId field.Id) + "</li>")
            writer.Line("</ul>")
        | SemanticTypeKind.Union variants ->
            writer.Line("<p>Union variants</p><ul>")
            for variant in variants |> List.sortBy (fun item -> StableId.value item.Id) do
                writer.Line(
                    "<li>" + html variant.DisplayName + " — "
                    + code (StableIdText.variantId variant.Id) + "</li>")
            writer.Line("</ul>")
        writer.Line("</details>")

    let private emitCapability (writer: HtmlWriter) (capability: SemanticCapability) =
        let typeRefText = typeRefTextIn Map.empty
        writer.Line(
            "<details><summary>" + html capability.DisplayName + " — "
            + code (StableIdText.capabilityId capability.Id) + " — " + visibility capability.Exported + "</summary><ul>")
        for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
            writer.Line(
                "<li>" + html operation.DisplayName + " — "
                + code (StableIdText.capabilityOperationId operation.Id)
                + "; result " + code (typeRefText operation.ResultType)
                + "; effect " + code (StableIdText.effectId operation.EffectId) + "</li>")
        writer.Line("</ul></details>")

    let private emitEffects (writer: HtmlWriter) (effects: SemanticEffect list) =
        writer.Line("<ul>")
        for effect in effects do
            writer.Line(
                "<li>" + html effect.DisplayName + " — "
                + code (StableIdText.effectId effect.Id) + " — " + visibility effect.Exported + "</li>")
        writer.Line("</ul>")

    let private emitDeclarations (writer: HtmlWriter) (model: ViewerFocusModel) =
        writer.Line("<section>")
        writer.Line("<h3>Referenced declarations</h3>")
        writer.Line("<h4>Types</h4>")
        if List.isEmpty model.ReferencedTypes then writer.Line("<p>None.</p>")
        for typeEntity in model.ReferencedTypes do
            emitTypeEntity writer typeEntity

        writer.Line("<h4>Capabilities</h4>")
        if List.isEmpty model.ReferencedCapabilities then writer.Line("<p>None.</p>")
        for capability in model.ReferencedCapabilities do
            emitCapability writer capability

        writer.Line("<h4>Effects</h4>")
        if List.isEmpty model.ReferencedEffects then
            writer.Line("<p>None.</p>")
        else
            emitEffects writer model.ReferencedEffects
        writer.Line("</section>")

    let private emitMissing (writer: HtmlWriter) (model: ViewerFocusModel) =
        let anyMissing =
            not (List.isEmpty model.MissingFunctionIds)
            || not (List.isEmpty model.MissingTypeIds)
            || not (List.isEmpty model.MissingCapabilityIds)
            || not (List.isEmpty model.MissingEffectIds)
        if anyMissing then
            writer.Line("<section class=\"missing\"><h3>Unresolved declarations</h3><ul>")
            for id in model.MissingFunctionIds do
                writer.Line("<li>" + code (StableIdText.functionId id) + "</li>")
            for id in model.MissingTypeIds do
                writer.Line("<li>" + code (StableIdText.typeId id) + "</li>")
            for id in model.MissingCapabilityIds do
                writer.Line("<li>" + code (StableIdText.capabilityId id) + "</li>")
            for id in model.MissingEffectIds do
                writer.Line("<li>" + code (StableIdText.effectId id) + "</li>")
            writer.Line("</ul></section>")

    let private emitObligations (writer: HtmlWriter) (obligations: QueryOpenObligation list) =
        writer.Line("<section><h3>Blocking obligations</h3>")
        if List.isEmpty obligations then
            writer.Line("<p>None.</p>")
        else
            writer.Line("<ul>")
            for obligation in obligations do
                writer.Line(
                    "<li>" + code (StableIdText.obligationId obligation.ObligationId)
                    + " — missing argument for " + code (StableIdText.parameterId obligation.ParameterId)
                    + " at " + code (StableIdText.graphNodeId obligation.CallNodeId)
                    + "; origin " + code (StableIdText.transactionId obligation.OriginTransactionId)
                    + "</li>")
            writer.Line("</ul>")
        writer.Line("</section>")

    let private emitFocusBody (writer: HtmlWriter) (model: ViewerFocusModel) =
        let names =
            [ yield! model.Function.TypeParameters
              for signature in model.RelatedFunctionSignatures do yield! signature.TypeParameters
              for typeEntity in model.ReferencedTypes do yield! typeEntity.TypeParameters ]
            |> Map.ofList
        let typeRefText = typeRefTextIn names
        writer.Line("<header>")
        writer.Line("<h1>Function Focus — " + html model.Function.DisplayName + "</h1>")
        writer.Line("<p>Snapshot: " + code (StableIdText.snapshotId model.SnapshotId) + "</p>")
        writer.Line("<p>Function: " + code (StableIdText.functionId model.Function.FunctionId) + "</p>")
        writer.Line("<p>Visibility: " + visibility model.Function.Exported + "</p>")
        writer.Line("<p>Result type: " + code (typeRefText model.Function.ResultType) + "</p>")
        writer.Line("</header>")
        writer.Line("<section><h2>Signature</h2>")
        emitParameters writer names model.Function.Parameters
        emitRequiredEffects writer model.Function.RequiredEffects
        writer.Line("</section>")
        emitBodyEffects writer model.BodyEffectRequirements
        if not (List.isEmpty model.Aborts) then
            writer.Line("<section><h2>Function abort boundaries</h2>")
            for boundary in model.Aborts do
                writer.Line("<p>Abort " + code (StableId.value boundary.NodeId) + " exits function " + code (StableId.value boundary.OwnerFunctionId)
                    + " with error " + code (StableId.value boundary.ErrorNodeId) + " : " + code (boundary.ErrorType |> Option.map typeRefText |> Option.defaultValue "unknown") + ".</p>")
                let ids values = values |> List.map (StableId.value >> code) |> String.concat ", "
                writer.Line("<p>Enclosing controls: " + ids boundary.EnclosingControlNodeIds + ". Dependent continuations: " + ids boundary.DependentContinuationNodeIds + ".</p>")
            writer.Line("</section>")
        emitCallSites writer "Direct callers" model.Callers (fun site -> site.CallerFunctionId)
        emitCallSites writer "Direct callees" model.Callees (fun site -> site.CalleeFunctionId)
        emitObligations writer model.OpenObligations
        emitMissing writer model
        writer.Line("<section><h2>Structured graph</h2>")
        emitGraph writer names model.Graph
        writer.Line("</section>")
        emitDeclarations writer model
        writer.Line("<details><summary>Related function signatures</summary>")
        for signature in model.RelatedFunctionSignatures do
            writer.Line("<article><h4>" + html signature.DisplayName + "</h4>")
            writer.Line("<p>" + code (StableIdText.functionId signature.FunctionId) + "</p>")
            emitParameters writer names signature.Parameters
            writer.Line("<p>Result: " + code (typeRefText signature.ResultType) + "</p>")
            emitRequiredEffects writer signature.RequiredEffects
            writer.Line("</article>")
        writer.Line("</details>")

    let renderFocusDocument (model: ViewerFocusModel): string =
        let writer = HtmlWriter()
        emitDocumentStart writer ("Nyrwvy Focus — " + model.Function.DisplayName)
        emitFocusBody writer model
        emitDocumentEnd writer
        writer.ToString()

    let private emitDiff (writer: HtmlWriter) (names: Map<TypeParameterId, string>) (diff: SemanticDiff) =
        let typeRefText = typeRefTextIn names
        writer.Line("<section><h2>Semantic change summary</h2>")
        writer.Line("<h3>Renames</h3><ul>")
        for rename in diff.Renames do
            let label, id, owners, oldName, newName =
                match rename with
                | FieldRenamed(typeId, variantId, fieldId, oldName, newName) ->
                    let variant = variantId |> Option.map (fun id -> [ StableIdText.variantId id ]) |> Option.defaultValue []
                    "field", StableIdText.fieldId fieldId, StableIdText.typeId typeId :: variant, oldName, newName
                | VariantRenamed(typeId, variantId, oldName, newName) ->
                    "variant", StableIdText.variantId variantId, [ StableIdText.typeId typeId ], oldName, newName
                | ParameterRenamed(functionId, parameterId, oldName, newName) ->
                    "parameter", StableIdText.parameterId parameterId, [ StableIdText.functionId functionId ], oldName, newName
                | EffectRenamed(effectId, oldName, newName) -> "effect", StableIdText.effectId effectId, [], oldName, newName
                | CapabilityRenamed(capabilityId, oldName, newName) ->
                    "capability", StableIdText.capabilityId capabilityId, [], oldName, newName
                | CapabilityOperationRenamed(capabilityId, operationId, oldName, newName) ->
                    "capability operation", StableIdText.capabilityOperationId operationId, [ StableIdText.capabilityId capabilityId ], oldName, newName
                | CapabilityParameterRenamed(capabilityId, operationId, parameterId, oldName, newName) ->
                    "capability parameter",
                    StableIdText.capabilityParameterId parameterId,
                    [ StableIdText.capabilityId capabilityId; StableIdText.capabilityOperationId operationId ],
                    oldName,
                    newName
                | TypeRenamed(id, oldName, newName) -> "type", StableId.value id, [], oldName, newName
                | FunctionRenamed(id, oldName, newName) -> "function", StableId.value id, [], oldName, newName
            let scope = if List.isEmpty owners then "" else " of " + (owners |> List.map code |> String.concat " / ")
            writer.Line(
                "<li>" + label + " " + code id + scope + ": "
                + html oldName + " → " + html newName + "</li>")
        writer.Line("</ul>")

        // Shown only when present, so reviews of other operations keep their exact earlier page.
        if not (List.isEmpty diff.VisibilityChanges) then
            writer.Line("<h3>Visibility changes</h3><ul>")
            for item in diff.VisibilityChanges do
                writer.Line(
                    "<li>" + html item.Kind + " " + code item.Id + " " + html item.DisplayName
                    + ": now " + visibility item.Exported + "</li>")
            writer.Line("</ul>")
        if not (List.isEmpty diff.ParametersRemoved) then
            writer.Line("<h3>Parameters removed</h3><ul>")
            for item in diff.ParametersRemoved do
                writer.Line(
                    "<li>" + code (StableIdText.functionId item.OwnerFunctionId)
                    + " removed " + html item.DisplayName + " — "
                    + code (StableIdText.parameterId item.RemovedParameterId))
                for callNodeId in item.AffectedCallNodeIds do
                    writer.Line("; call " + code (StableIdText.graphNodeId callNodeId) + " lost its argument")
                if not (List.isEmpty item.RetiredObligations) then
                    writer.Line("<ul>")
                    for obligation in item.RetiredObligations do
                        let status =
                            match obligation.Status with
                            | ObligationStatus.Open -> "open"
                            | ObligationStatus.Resolved _ -> "resolved"
                        writer.Line(
                            "<li>Retired " + status + " obligation " + code (StableIdText.obligationId obligation.Id)
                            + "; call " + code (StableIdText.graphNodeId obligation.CallNodeId) + "</li>")
                    writer.Line("</ul>")
                writer.Line("</li>")
            writer.Line("</ul>")

        if not (List.isEmpty diff.UnionVariantsRemoved) then
            writer.Line("<h3>Union variants removed</h3><ul>")
            for item in diff.UnionVariantsRemoved do
                writer.Line(
                    "<li>" + code (StableIdText.typeId item.OwnerTypeId)
                    + " removed " + html item.DisplayName + " — "
                    + code (StableIdText.variantId item.RemovedVariantId))
                for fieldId in item.RemovedFieldIds do
                    writer.Line("; removed " + code (StableIdText.fieldId fieldId))
                for matchNodeId in item.AffectedMatchNodeIds do
                    writer.Line("; match " + code (StableIdText.graphNodeId matchNodeId) + " pruned")
                writer.Line("</li>")
            writer.Line("</ul>")

        writer.Line("<h3>Parameters added</h3><ul>")
        for item in diff.ParametersAdded do
            writer.Line(
                "<li>" + code (StableIdText.functionId item.OwnerFunctionId)
                + " added " + html item.AddedDisplayName + " — "
                + code (StableIdText.parameterId item.AddedParameterId)
                + "; type " + code (typeRefText item.AddedType) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Capability operations removed</h3><ul>")
        for item in diff.CapabilityOperationsRemoved do
            writer.Line(
                "<li>" + code (StableIdText.capabilityId item.OwnerCapabilityId)
                + " removed " + html item.DisplayName + " — "
                + code (StableIdText.capabilityOperationId item.OperationId))
            for parameterId in item.RemovedParameterIds do
                writer.Line("; removed " + code (StableIdText.capabilityParameterId parameterId))
            writer.Line("</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Required effects changed</h3><ul>")
        for change in diff.FunctionRequiredEffectsChanged do
            let functionId, effectId, verb =
                match change with
                | FunctionRequiredEffectChange.Added(functionId, effectId) -> functionId, effectId, "added"
                | FunctionRequiredEffectChange.Removed(functionId, effectId) -> functionId, effectId, "removed"
            writer.Line(
                "<li>" + code (StableIdText.functionId functionId)
                + " " + verb + " required effect " + code (StableIdText.effectId effectId) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Functions removed</h3><ul>")
        for item in diff.FunctionsRemoved do
            writer.Line("<li>" + html item.DisplayName + " — " + code (StableIdText.functionId item.FunctionId))
            if not (List.isEmpty item.RetiredObligations) then
                writer.Line("<ul>")
                for obligation in item.RetiredObligations do
                    writer.Line(
                        "<li>Retired resolved obligation " + code (StableIdText.obligationId obligation.Id)
                        + "; call " + code (StableIdText.graphNodeId obligation.CallNodeId)
                        + "; parameter " + code (StableIdText.parameterId obligation.ParameterId)
                        + "; origin " + code (StableIdText.transactionId obligation.OriginTransactionId))
                    match obligation.Status with
                    | ObligationStatus.Resolved resolution ->
                        writer.Line("; resolved in " + code (StableIdText.transactionId resolution.TransactionId)
                                    + " with " + code (StableIdText.graphNodeId resolution.ValueNodeId))
                    | ObligationStatus.Open -> ()
                    writer.Line("</li>")
                writer.Line("</ul>")
            writer.Line("</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Arguments attached</h3><ul>")
        for item in diff.ArgumentsAttached do
            writer.Line(
                "<li>" + code (StableIdText.graphNodeId item.CallNodeId) + ": "
                + code (StableIdText.parameterId item.TargetParameterId) + " ← "
                + code (StableIdText.graphNodeId item.ValueNodeId) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Obligations created</h3><ul>")
        for item in diff.ObligationsCreated do
            writer.Line(
                "<li>" + code (StableIdText.obligationId item.ObligationId)
                + " for " + code (StableIdText.graphNodeId item.CallNodeId)
                + " / " + code (StableIdText.parameterId item.ParameterId) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Obligations resolved</h3><ul>")
        for item in diff.ObligationsResolved do
            writer.Line(
                "<li>" + code (StableIdText.obligationId item.ObligationId)
                + " resolved with " + code (StableIdText.graphNodeId item.ValueNodeId) + "</li>")
        writer.Line("</ul></section>")

    let renderChangeReviewDocument (model: ViewerChangeReviewModel): string =
        let writer = HtmlWriter()
        emitDocumentStart writer "Nyrwvy Change Review"
        writer.Line("<header><h1>Change Review</h1>")
        writer.Line("<p>Transaction: " + code (StableIdText.transactionId model.TransactionId) + "</p>")
        writer.Line("<p>Base snapshot: " + code (StableIdText.snapshotId model.BaseSnapshotId) + "</p>")
        writer.Line("<p>Proposed snapshot: " + code (StableIdText.snapshotId model.ProposedSnapshotId) + "</p>")
        writer.Line("<p class=\"status\">Readiness: " + html (readinessLabel model.Readiness) + "</p></header>")
        // The affected functions' focus views name the type parameters an added parameter may use.
        let names =
            [ for focus in model.FunctionFocus do
                  yield! focus.Function.TypeParameters
                  for signature in focus.RelatedFunctionSignatures do yield! signature.TypeParameters ]
            |> Map.ofList
        emitDiff writer names model.Diff
        writer.Line("<section><h2>Affected stable identities</h2><ul>")
        for id in model.AffectedFunctionIds do
            writer.Line("<li>" + code (StableIdText.functionId id) + "</li>")
        for id in model.AffectedTypeIds do
            writer.Line("<li>" + code (StableIdText.typeId id) + "</li>")
        writer.Line("</ul></section>")
        writer.Line("<section><h2>Affected function focus</h2>")
        for focus in model.FunctionFocus do
            writer.Line("<article>")
            emitFocusBody writer focus
            writer.Line("</article>")
        writer.Line("</section>")
        emitDocumentEnd writer
        writer.ToString()

    let private transactionReadinessLabel (readiness: TransactionReadiness): string =
        match readiness with
        | TransactionReadiness.SemanticallyReady -> "semantically ready"
        | TransactionReadiness.Incomplete ids ->
            "incomplete — blocking obligations: "
            + (ids |> List.map (StableId.value >> fun value -> "obligation:" + value) |> String.concat ", ")

    let renderProposalReviewDocument (model: ViewerProposalReviewModel): string =
        let writer = HtmlWriter()
        emitDocumentStart writer ("Nyrwvy Change Review — " + model.ProposalKind)
        writer.Line("<header><h1>Change Review — " + html model.ProposalKind + "</h1>")
        writer.Line("<p>Pending proposal: " + code (StableIdText.transactionId model.TransactionId) + ". Nothing is published until it is committed.</p>")
        writer.Line("<p>Base snapshot: " + code (StableIdText.snapshotId model.BaseSnapshotId) + "</p>")
        writer.Line("<p>Proposed snapshot: " + code (StableIdText.snapshotId model.ProposedSnapshotId) + "</p>")
        if not model.BaseIsCommitted then
            writer.Line("<p class=\"status\">The base snapshot is no longer the committed state; committed focus is not shown.</p>")
        writer.Line("<p class=\"status\">Readiness: " + html (transactionReadinessLabel model.Readiness) + "</p></header>")

        writer.Line("<section><h2>Functions created or rewritten</h2>")
        if List.isEmpty model.ChangedFunctions then writer.Line("<p>None.</p>")
        for item in model.ChangedFunctions do
            let status =
                match item.CommittedVersion with
                | Some _ -> "changed"
                | None when model.BaseIsCommitted -> "created"
                | None -> "proposed"
            writer.Line(
                "<article><h2>" + html item.ProposedVersion.Function.DisplayName + " — " + status + " "
                + code (StableIdText.functionId item.ProposedVersion.Function.FunctionId) + "</h2>")
            match item.CommittedVersion with
            | Some before ->
                writer.Line("<details><summary>Committed</summary>")
                emitFocusBody writer before
                writer.Line("</details>")
                writer.Line("<details open><summary>Proposed</summary>")
                emitFocusBody writer item.ProposedVersion
                writer.Line("</details>")
            | None -> emitFocusBody writer item.ProposedVersion
            writer.Line("</article>")
        writer.Line("</section>")

        if not (List.isEmpty model.DeclaredTypes && List.isEmpty model.DeclaredCapabilities && List.isEmpty model.DeclaredEffects) then
            writer.Line("<section><h2>Declarations created or updated</h2>")
            if not (List.isEmpty model.DeclaredTypes) then
                writer.Line("<h3>Types</h3>")
                for typeEntity in model.DeclaredTypes do
                    emitTypeEntity writer typeEntity
            if not (List.isEmpty model.DeclaredCapabilities) then
                writer.Line("<h3>Capabilities</h3>")
                for capability in model.DeclaredCapabilities do
                    emitCapability writer capability
            if not (List.isEmpty model.DeclaredEffects) then
                writer.Line("<h3>Effects</h3>")
                emitEffects writer model.DeclaredEffects
            writer.Line("</section>")

        writer.Line("<section><h2>Engine review</h2>")
        writer.Line("<p>The complete requested change and resulting diff in the session's structured review format.</p>")
        writer.Line("<pre><code>" + html model.EngineReview + "</code></pre>")
        writer.Line("</section>")
        emitDocumentEnd writer
        writer.ToString()
