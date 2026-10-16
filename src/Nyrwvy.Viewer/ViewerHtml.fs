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
    let private stableText prefix (id: StableId<'Kind>): string = prefix + StableId.value id

    let rec private typeRefText (typeRef: TypeRef): string =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> "String"
        | TypeRef.Primitive PrimitiveType.Bool -> "Bool"
        | TypeRef.Primitive PrimitiveType.Unit -> "Unit"
        | TypeRef.Primitive PrimitiveType.Int64 -> "Int64"
        | TypeRef.Primitive PrimitiveType.Float64 -> "Float64"
        | TypeRef.Primitive PrimitiveType.Bits32 -> "Bits32"
        | TypeRef.Named typeId -> stableText "type:" typeId
        | TypeRef.Capability capabilityId -> stableText "capability:" capabilityId
        | TypeRef.Function(arguments, result) -> "(" + (arguments |> List.map typeRefText |> String.concat ", ") + ") -> " + typeRefText result
        | TypeRef.TypeParameter(_, displayName) -> displayName
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
        | ViewerTarget.Node id -> stableText "node:" id
        | ViewerTarget.Function id -> stableText "function:" id
        | ViewerTarget.Parameter id -> stableText "parameter:" id
        | ViewerTarget.PatternBinding id -> stableText "pattern-binding:" id
        | ViewerTarget.CapabilityOperation id -> stableText "capability-operation:" id
        | ViewerTarget.Type id -> stableText "type:" id
        | ViewerTarget.Field id -> stableText "field:" id
        | ViewerTarget.Variant id -> stableText "variant:" id

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
        | ValueDependencyRole.CallArgument parameterId -> "call argument " + stableText "parameter:" parameterId
        | ValueDependencyRole.CapabilityValue -> "capability value"
        | ValueDependencyRole.CapabilityArgument parameterId ->
            "capability argument " + stableText "capability-parameter:" parameterId
        | ValueDependencyRole.FieldValue fieldId -> "field value " + stableText "field:" fieldId
        | ValueDependencyRole.BranchCondition -> "branch condition"
        | ValueDependencyRole.MatchInput -> "match input"
        | ValueDependencyRole.BindInput -> "evaluate before body"

    let private nodeRoleLabel (role: ViewerNodeRole): string =
        match role with
        | ViewerNodeRole.ParameterReference -> "parameter reference"
        | ViewerNodeRole.PatternReference -> "pattern reference"
        | ViewerNodeRole.StringLiteral value -> "string literal “" + value + "”"
        | ViewerNodeRole.BoolLiteral value -> "bool literal " + (if value then "true" else "false")
        | ViewerNodeRole.UnitLiteral -> "unit literal"
        | ViewerNodeRole.Int64Literal value -> "Int64 literal " + string value
        | ViewerNodeRole.Float64Literal bits -> "Float64 literal 0x" + Float64Bits.toHex bits
        | ViewerNodeRole.StringIsEmpty -> "string is empty"
        | ViewerNodeRole.Int64ToString -> "int64 to string"
        | ViewerNodeRole.Int64ToHexString -> "int64 to hex string"
        | ViewerNodeRole.StringParseInt64 -> "parse int64"
        | ViewerNodeRole.StringParseInt64Hex -> "parse hex int64"
        | ViewerNodeRole.Float64ToString -> "float64 to string"
        | ViewerNodeRole.StringParseFloat64 -> "parse float64"
        | ViewerNodeRole.StringCodePoints -> "code points"
        | ViewerNodeRole.StringToLower -> "to lower"
        | ViewerNodeRole.StringToUpper -> "to upper"
        | ViewerNodeRole.StringDecodeUtf8Lossy -> "UTF-8 decode (replace)"
        | ViewerNodeRole.Float64RoundToInt64 -> "round to int64"
        | ViewerNodeRole.Float64FloorToInt64 -> "floor to int64"
        | ViewerNodeRole.Float64Sqrt -> "square root"
        | ViewerNodeRole.Float64Abs -> "absolute value"
        | ViewerNodeRole.Float64Negate -> "negate"
        | ViewerNodeRole.Float64Floor -> "floor"
        | ViewerNodeRole.Float64Ceiling -> "ceiling"
        | ViewerNodeRole.Float64Truncate -> "truncate"
        | ViewerNodeRole.StringUtf8ByteCount -> "utf-8 byte count"
        | ViewerNodeRole.StringFromCodePoint -> "from code point"
        | ViewerNodeRole.StringToCodePoint -> "to code point"
        | ViewerNodeRole.StringLength -> "string length"
        | ViewerNodeRole.StringSlice -> "string slice"
        | ViewerNodeRole.StringContains -> "string contains"
        | ViewerNodeRole.StringIndexOf -> "string index of"
        | ViewerNodeRole.StringSpan -> "string span"
        | ViewerNodeRole.StringSpanBack -> "string span back"
        | ViewerNodeRole.StringLastIndexOf -> "string last index of"
        | ViewerNodeRole.StringStartsWith -> "string starts with"
        | ViewerNodeRole.StringEndsWith -> "string ends with"
        | ViewerNodeRole.StringSplit -> "string split"
        | ViewerNodeRole.StringSplitLines -> "string split lines"
        | ViewerNodeRole.StringReplace -> "string replace"
        | ViewerNodeRole.StringTrim -> "trim whitespace"
        | ViewerNodeRole.StringTrimStart -> "trim leading whitespace"
        | ViewerNodeRole.StringTrimEnd -> "trim trailing whitespace"
        | ViewerNodeRole.StringJoin -> "string join"
        | ViewerNodeRole.StringEqual -> "string equal"
        | ViewerNodeRole.StringLessThan -> "string less than"
        | ViewerNodeRole.ValueEqual -> "value equal"
        | ViewerNodeRole.StringConcat -> "string concat"
        | ViewerNodeRole.Int64WrappingAdd -> "Int64 wrapping add"
        | ViewerNodeRole.Int64WrappingSubtract -> "Int64 wrapping subtract"
        | ViewerNodeRole.Int64WrappingMultiply -> "Int64 wrapping multiply"
        | ViewerNodeRole.Int64FloorDivide -> "Int64 floor divide"
        | ViewerNodeRole.Int64FloorModulo -> "Int64 floor modulo"
        | ViewerNodeRole.Int64Min -> "Int64 min"
        | ViewerNodeRole.Int64Max -> "Int64 max"
        | ViewerNodeRole.Int64BitAnd -> "Int64 bitwise and"
        | ViewerNodeRole.Int64BitOr -> "Int64 bitwise or"
        | ViewerNodeRole.Int64BitXor -> "Int64 bitwise xor"
        | ViewerNodeRole.Int64ShiftLeft -> "Int64 shift left"
        | ViewerNodeRole.Int64ShiftRight -> "Int64 arithmetic shift right"
        | ViewerNodeRole.Int64CheckedAdd -> "Int64 checked add"
        | ViewerNodeRole.Int64CheckedSubtract -> "Int64 checked subtract"
        | ViewerNodeRole.Int64CheckedMultiply -> "Int64 checked multiply"
        | ViewerNodeRole.ListRange -> "List range"
        | ViewerNodeRole.StringRepeat -> "String repeat"
        | ViewerNodeRole.Float64Round -> "Float64 round"
        | ViewerNodeRole.Int64Equal -> "Int64 equal"
        | ViewerNodeRole.Int64LessThan -> "Int64 less than"
        | ViewerNodeRole.Int64LessThanOrEqual -> "Int64 less than or equal"
        | ViewerNodeRole.Int64GreaterThan -> "Int64 greater than"
        | ViewerNodeRole.Int64GreaterThanOrEqual -> "Int64 greater than or equal"
        | ViewerNodeRole.Float64Add -> "Float64 add"
        | ViewerNodeRole.Float64Min -> "Float64 min"
        | ViewerNodeRole.Float64Max -> "Float64 max"
        | ViewerNodeRole.Float64Subtract -> "Float64 subtract"
        | ViewerNodeRole.Float64Multiply -> "Float64 multiply"
        | ViewerNodeRole.Float64Divide -> "Float64 divide"
        | ViewerNodeRole.Float64Equal -> "Float64 equal"
        | ViewerNodeRole.Float64LessThan -> "Float64 less than"
        | ViewerNodeRole.Float64IsNaN -> "Float64 is NaN"
        | ViewerNodeRole.Float64IsFinite -> "Float64 is finite"
        | ViewerNodeRole.Float64IsInfinite -> "Float64 is infinite"
        | ViewerNodeRole.Int64ToFloat64 -> "int64 to float64"
        | ViewerNodeRole.Int64WrappingAbs -> "Int64 wrapping abs"
        | ViewerNodeRole.Int64BitNot -> "Int64 bitwise not"
        | ViewerNodeRole.Bits32FromInt64 -> "bits32 from int64"
        | ViewerNodeRole.Bits32ToInt64 -> "bits32 to int64"
        | ViewerNodeRole.Bits32Xor -> "bits32 xor"
        | ViewerNodeRole.Bits32And -> "bits32 and"
        | ViewerNodeRole.Bits32Or -> "bits32 or"
        | ViewerNodeRole.Bits32Not -> "bits32 not"
        | ViewerNodeRole.Bits32Add -> "bits32 add"
        | ViewerNodeRole.Bits32RotateRight -> "bits32 rotate right"
        | ViewerNodeRole.Bits32ShiftRight -> "bits32 shift right"
        | ViewerNodeRole.Bits32ShiftLeft -> "bits32 shift left"
        | ViewerNodeRole.ListEmpty -> "List empty"
        | ViewerNodeRole.SetEmpty -> "Set empty"
        | ViewerNodeRole.MapEmpty -> "Map empty"
        | ViewerNodeRole.ListConstruct -> "List construct"
        | ViewerNodeRole.ListLength -> "List length"
        | ViewerNodeRole.MapValues -> "Map values"
        | ViewerNodeRole.MapKeys -> "Map keys"
        | ViewerNodeRole.SetToList -> "Set to list"
        | ViewerNodeRole.MapSize -> "Map size"
        | ViewerNodeRole.SetFromList -> "Set from list"
        | ViewerNodeRole.SetSize -> "Set size"
        | ViewerNodeRole.ListReverse -> "List reverse"
        | ViewerNodeRole.ListLast -> "List last"
        | ViewerNodeRole.ListDropLast -> "List drop last"
        | ViewerNodeRole.ListAppendItem -> "List append item"
        | ViewerNodeRole.ListItemAt -> "List item at"
        | ViewerNodeRole.MapRemove -> "Map remove"
        | ViewerNodeRole.SetInsert -> "Set insert"
        | ViewerNodeRole.SetRemove -> "Set remove"
        | ViewerNodeRole.SetUnion -> "Set union"
        | ViewerNodeRole.SetIntersection -> "Set intersection"
        | ViewerNodeRole.SetDifference -> "Set difference"
        | ViewerNodeRole.SetIsSubset -> "Set is subset"
        | ViewerNodeRole.MapUnion -> "Map union (right wins)"
        | ViewerNodeRole.MapContainsKey -> "Map contains key"
        | ViewerNodeRole.SetContains -> "Set contains"
        | ViewerNodeRole.ListIndexOf -> "List index of"
        | ViewerNodeRole.ListLastIndexOf -> "List last index of"
        | ViewerNodeRole.MapGet -> "Map get"
        | ViewerNodeRole.MapGetOrDefault -> "Map get or default"
        | ViewerNodeRole.ListSlice -> "List slice"
        | ViewerNodeRole.ListSetAt -> "List set at"
        | ViewerNodeRole.ListInsertAt -> "List insert at"
        | ViewerNodeRole.ListRemoveAt -> "List remove at"
        | ViewerNodeRole.MapInsert -> "Map insert"
        | ViewerNodeRole.ListConcat -> "List concat"
        | ViewerNodeRole.ListFold -> "List fold"
        | ViewerNodeRole.MapFold -> "Map fold"
        | ViewerNodeRole.ListEffectFold -> "List effect fold"
        | ViewerNodeRole.UseResource -> "use resource"
        | ViewerNodeRole.ParallelAll -> "parallel all"
        | ViewerNodeRole.CancellationRequested -> "cancellation requested"
        | ViewerNodeRole.ExceptionCatch -> "exception catch"
        | ViewerNodeRole.ExceptionIsCancellation -> "exception is cancellation"
        | ViewerNodeRole.ExceptionMessage -> "exception message"
        | ViewerNodeRole.ExceptionRethrow -> "exception rethrow"
        | ViewerNodeRole.CancellationShield -> "cancellation shield"
        | ViewerNodeRole.TaskScope -> "task scope"
        | ViewerNodeRole.TaskStart -> "task start"
        | ViewerNodeRole.TaskWait TaskWaitMode.Any -> "task wait (any)"
        | ViewerNodeRole.TaskWait TaskWaitMode.All -> "task wait (all)"
        | ViewerNodeRole.OneShotCreate -> "one-shot create"
        | ViewerNodeRole.OneShotComplete -> "one-shot complete (first value)"
        | ViewerNodeRole.OneShotWait -> "one-shot wait"
        | ViewerNodeRole.TaskCancel -> "task cancel"
        | ViewerNodeRole.ListMap -> "List map"
        | ViewerNodeRole.ListZipWith -> "List zip with"
        | ViewerNodeRole.ListFlatMap -> "List flat map"
        | ViewerNodeRole.MapFromList -> "Map from list"
        | ViewerNodeRole.ListFilter -> "List filter"
        | ViewerNodeRole.MapFilter -> "Map filter"
        | ViewerNodeRole.ListSortBy -> "List sort by"
        | ViewerNodeRole.ListMinBy -> "List min by"
        | ViewerNodeRole.MapMapValues -> "Map map values"
        | ViewerNodeRole.OptionGetOrDefault -> "Option get or default"
        | ViewerNodeRole.OptionMap -> "Option map"
        | ViewerNodeRole.OptionAndThen -> "Option and then"
        | ViewerNodeRole.ResultMap -> "Result map"
        | ViewerNodeRole.ResultMapErr -> "Result map error"
        | ViewerNodeRole.ResultAndThen -> "Result and then"
        | ViewerNodeRole.ListAny -> "List any"
        | ViewerNodeRole.ListFindIndex -> "List find index"
        | ViewerNodeRole.ListAll -> "List all"
        | ViewerNodeRole.CapabilityConstruct(capabilityId, implementations) ->
            "capability construction " + StableId.value capabilityId + " (" +
            (implementations |> List.map (fun (operationId, functionId) -> StableId.value operationId + " -> " + StableId.value functionId) |> String.concat "; ") + ")"
        | ViewerNodeRole.FunctionReference -> "function reference"
        | ViewerNodeRole.FunctionInvoke -> "function invocation"
        | ViewerNodeRole.DirectCall -> "direct call"
        | ViewerNodeRole.CapabilityInvoke -> "capability invoke"
        | ViewerNodeRole.ProductConstruct -> "product construct"
        | ViewerNodeRole.FieldAccess -> "field access"
        | ViewerNodeRole.ProductUpdate -> "product update"
        | ViewerNodeRole.UnionConstruct -> "union construct"
        | ViewerNodeRole.OptionNone -> "Option.None"
        | ViewerNodeRole.OptionSome -> "Option.Some"
        | ViewerNodeRole.ResultOk -> "Result.Ok"
        | ViewerNodeRole.Abort -> "Abort (function exit)"
        | ViewerNodeRole.ResultErr -> "Result.Err"
        | ViewerNodeRole.BoolNot -> "bool not"
        | ViewerNodeRole.BoolAnd -> "bool and"
        | ViewerNodeRole.BoolOr -> "bool or"
        | ViewerNodeRole.Conditional -> "conditional"
        | ViewerNodeRole.MatchUnion -> "union match"
        | ViewerNodeRole.Bind -> "bind once"
        | ViewerNodeRole.MatchOption -> "Option match"
        | ViewerNodeRole.MatchResult -> "Result match"

    let private branchLabel (role: ViewerBranchRole): string =
        match role with
        | ViewerBranchRole.WhenTrue -> "when true"
        | ViewerBranchRole.WhenFalse -> "when false"
        | ViewerBranchRole.UnionVariant variantIds -> "variant " + (variantIds |> List.map (stableText "variant:") |> String.concat " | ")
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

    let private emitParameters (writer: HtmlWriter) (parameters: SemanticParameter list) =
        writer.Line("<table>")
        writer.Line("<thead><tr><th>Parameter</th><th>Type</th><th>Stable ID</th></tr></thead>")
        writer.Line("<tbody>")
        writer.Push()
        for parameter in parameters do
            writer.Line(
                "<tr><td>" + html parameter.DisplayName
                + "</td><td>" + code (typeRefText parameter.Type)
                + "</td><td>" + code (stableText "parameter:" parameter.Id) + "</td></tr>")
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
                |> List.map (stableText "effect:" >> code)
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
                    | EffectRequirementSource.FunctionContract id -> stableText "function:" id
                    | EffectRequirementSource.CapabilityOperation id -> stableText "operation:" id
                writer.Line("<li>" + code (stableText "effect:" item.EffectId) + " at " + code (stableText "node:" item.NodeId) + " from " + code source + "</li>")
            writer.Line("</ul>")
        for id in report.UnresolvedFunctionIds do writer.Line("<p>Unresolved function contract: " + code (stableText "function:" id) + "</p>")
        for id in report.UnresolvedOperationIds do writer.Line("<p>Unresolved operation contract: " + code (stableText "operation:" id) + "</p>")
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
                    "<li>" + code (stableText "function:" (endpoint site))
                    + " via " + code (stableText "node:" site.CallNodeId) + "</li>")
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
                    + code (stableText "node:" dependency.NodeId) + "</li>")
            writer.Line("</ul>")

    let rec private emitGraph (writer: HtmlWriter) (graph: ViewerGraph) =
        writer.Line("<section class=\"graph\" aria-label=\"value graph\">")
        writer.Line("<p>Graph result: " + code (stableText "node:" graph.ResultNodeId) + "</p>")
        writer.Line("<div class=\"nodes\">")
        writer.Push()
        for node in graph.Nodes do
            writer.Line("<article class=\"node\">")
            writer.Line("<h4>" + html (nodeRoleLabel node.Role) + "</h4>")
            writer.Line("<p>Stable ID: " + code (stableText "node:" node.Id) + "</p>")
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
                            | Some fieldId -> "; source field " + code (stableText "field:" fieldId)
                            | None -> ""
                        writer.Line(
                            "<li>" + html displayName + " — "
                            + code (stableText "pattern-binding:" binding.Id)
                            + "; type " + code (typeRefText binding.Type)
                            + sourceField + "</li>")
                    writer.Line("</ul>")
                emitGraph writer branch.Graph
                writer.Line("</section>")
            writer.Line("</article>")
        writer.Pop()
        writer.Line("</div>")
        writer.Line("</section>")

    let private emitTypeEntity (writer: HtmlWriter) (typeEntity: SemanticType) =
        writer.Line(
            "<details><summary>" + html typeEntity.DisplayName + " — "
            + code (stableText "type:" typeEntity.Id) + "</summary>")
        match typeEntity.Kind with
        | SemanticTypeKind.Product fields ->
            writer.Line("<p>Product fields</p><ul>")
            for field in fields |> List.sortBy (fun item -> StableId.value item.Id) do
                writer.Line(
                    "<li>" + html field.DisplayName + ": " + code (typeRefText field.Type)
                    + " — " + code (stableText "field:" field.Id) + "</li>")
            writer.Line("</ul>")
        | SemanticTypeKind.Union variants ->
            writer.Line("<p>Union variants</p><ul>")
            for variant in variants |> List.sortBy (fun item -> StableId.value item.Id) do
                writer.Line(
                    "<li>" + html variant.DisplayName + " — "
                    + code (stableText "variant:" variant.Id) + "</li>")
            writer.Line("</ul>")
        writer.Line("</details>")

    let private emitCapability (writer: HtmlWriter) (capability: SemanticCapability) =
        writer.Line(
            "<details><summary>" + html capability.DisplayName + " — "
            + code (stableText "capability:" capability.Id) + "</summary><ul>")
        for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
            writer.Line(
                "<li>" + html operation.DisplayName + " — "
                + code (stableText "capability-operation:" operation.Id)
                + "; result " + code (typeRefText operation.ResultType)
                + "; effect " + code (stableText "effect:" operation.EffectId) + "</li>")
        writer.Line("</ul></details>")

    let private emitEffects (writer: HtmlWriter) (effects: SemanticEffect list) =
        writer.Line("<ul>")
        for effect in effects do
            writer.Line(
                "<li>" + html effect.DisplayName + " — "
                + code (stableText "effect:" effect.Id) + "</li>")
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
                writer.Line("<li>" + code (stableText "function:" id) + "</li>")
            for id in model.MissingTypeIds do
                writer.Line("<li>" + code (stableText "type:" id) + "</li>")
            for id in model.MissingCapabilityIds do
                writer.Line("<li>" + code (stableText "capability:" id) + "</li>")
            for id in model.MissingEffectIds do
                writer.Line("<li>" + code (stableText "effect:" id) + "</li>")
            writer.Line("</ul></section>")

    let private emitObligations (writer: HtmlWriter) (obligations: QueryOpenObligation list) =
        writer.Line("<section><h3>Blocking obligations</h3>")
        if List.isEmpty obligations then
            writer.Line("<p>None.</p>")
        else
            writer.Line("<ul>")
            for obligation in obligations do
                writer.Line(
                    "<li>" + code (stableText "obligation:" obligation.ObligationId)
                    + " — missing argument for " + code (stableText "parameter:" obligation.ParameterId)
                    + " at " + code (stableText "node:" obligation.CallNodeId)
                    + "; origin " + code (stableText "transaction:" obligation.OriginTransactionId)
                    + "</li>")
            writer.Line("</ul>")
        writer.Line("</section>")

    let private emitFocusBody (writer: HtmlWriter) (model: ViewerFocusModel) =
        writer.Line("<header>")
        writer.Line("<h1>Function Focus — " + html model.Function.DisplayName + "</h1>")
        writer.Line("<p>Snapshot: " + code (stableText "snapshot:" model.SnapshotId) + "</p>")
        writer.Line("<p>Function: " + code (stableText "function:" model.Function.FunctionId) + "</p>")
        writer.Line("<p>Result type: " + code (typeRefText model.Function.ResultType) + "</p>")
        writer.Line("</header>")
        writer.Line("<section><h2>Signature</h2>")
        emitParameters writer model.Function.Parameters
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
        emitGraph writer model.Graph
        writer.Line("</section>")
        emitDeclarations writer model
        writer.Line("<details><summary>Related function signatures</summary>")
        for signature in model.RelatedFunctionSignatures do
            writer.Line("<article><h4>" + html signature.DisplayName + "</h4>")
            writer.Line("<p>" + code (stableText "function:" signature.FunctionId) + "</p>")
            emitParameters writer signature.Parameters
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

    let private emitDiff (writer: HtmlWriter) (diff: SemanticDiff) =
        writer.Line("<section><h2>Semantic change summary</h2>")
        writer.Line("<h3>Renames</h3><ul>")
        for rename in diff.Renames do
            match rename with
            | TypeRenamed(id, oldName, newName) ->
                writer.Line(
                    "<li>type " + code (StableId.value id) + ": "
                    + html oldName + " → " + html newName + "</li>")
            | FunctionRenamed(id, oldName, newName) ->
                writer.Line(
                    "<li>function " + code (StableId.value id) + ": "
                    + html oldName + " → " + html newName + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Parameters added</h3><ul>")
        for item in diff.ParametersAdded do
            writer.Line(
                "<li>" + code (stableText "function:" item.OwnerFunctionId)
                + " added " + html item.AddedDisplayName + " — "
                + code (stableText "parameter:" item.AddedParameterId)
                + "; type " + code (typeRefText item.AddedType) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Capability operations removed</h3><ul>")
        for item in diff.CapabilityOperationsRemoved do
            writer.Line(
                "<li>" + code (stableText "capability:" item.OwnerCapabilityId)
                + " removed " + html item.DisplayName + " — "
                + code (stableText "operation:" item.OperationId))
            for parameterId in item.RemovedParameterIds do
                writer.Line("; removed " + code (stableText "capabilityParameter:" parameterId))
            writer.Line("</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Required effects changed</h3><ul>")
        for change in diff.FunctionRequiredEffectsChanged do
            let functionId, effectId, verb =
                match change with
                | FunctionRequiredEffectChange.Added(functionId, effectId) -> functionId, effectId, "added"
                | FunctionRequiredEffectChange.Removed(functionId, effectId) -> functionId, effectId, "removed"
            writer.Line(
                "<li>" + code (stableText "function:" functionId)
                + " " + verb + " required effect " + code (stableText "effect:" effectId) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Functions removed</h3><ul>")
        for item in diff.FunctionsRemoved do
            writer.Line("<li>" + html item.DisplayName + " — " + code (stableText "function:" item.FunctionId))
            if not (List.isEmpty item.RetiredObligations) then
                writer.Line("<ul>")
                for obligation in item.RetiredObligations do
                    writer.Line(
                        "<li>Retired resolved obligation " + code (stableText "obligation:" obligation.Id)
                        + "; call " + code (stableText "node:" obligation.CallNodeId)
                        + "; parameter " + code (stableText "parameter:" obligation.ParameterId)
                        + "; origin " + code (stableText "transaction:" obligation.OriginTransactionId))
                    match obligation.Status with
                    | ObligationStatus.Resolved resolution ->
                        writer.Line("; resolved in " + code (stableText "transaction:" resolution.TransactionId)
                                    + " with " + code (stableText "node:" resolution.ValueNodeId))
                    | ObligationStatus.Open -> ()
                    writer.Line("</li>")
                writer.Line("</ul>")
            writer.Line("</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Arguments attached</h3><ul>")
        for item in diff.ArgumentsAttached do
            writer.Line(
                "<li>" + code (stableText "node:" item.CallNodeId) + ": "
                + code (stableText "parameter:" item.TargetParameterId) + " ← "
                + code (stableText "node:" item.ValueNodeId) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Obligations created</h3><ul>")
        for item in diff.ObligationsCreated do
            writer.Line(
                "<li>" + code (stableText "obligation:" item.ObligationId)
                + " for " + code (stableText "node:" item.CallNodeId)
                + " / " + code (stableText "parameter:" item.ParameterId) + "</li>")
        writer.Line("</ul>")

        writer.Line("<h3>Obligations resolved</h3><ul>")
        for item in diff.ObligationsResolved do
            writer.Line(
                "<li>" + code (stableText "obligation:" item.ObligationId)
                + " resolved with " + code (stableText "node:" item.ValueNodeId) + "</li>")
        writer.Line("</ul></section>")

    let renderChangeReviewDocument (model: ViewerChangeReviewModel): string =
        let writer = HtmlWriter()
        emitDocumentStart writer "Nyrwvy Change Review"
        writer.Line("<header><h1>Change Review</h1>")
        writer.Line("<p>Transaction: " + code (stableText "transaction:" model.TransactionId) + "</p>")
        writer.Line("<p>Base snapshot: " + code (stableText "snapshot:" model.BaseSnapshotId) + "</p>")
        writer.Line("<p>Proposed snapshot: " + code (stableText "snapshot:" model.ProposedSnapshotId) + "</p>")
        writer.Line("<p class=\"status\">Readiness: " + html (readinessLabel model.Readiness) + "</p></header>")
        emitDiff writer model.Diff
        writer.Line("<section><h2>Affected stable identities</h2><ul>")
        for id in model.AffectedFunctionIds do
            writer.Line("<li>" + code (stableText "function:" id) + "</li>")
        for id in model.AffectedTypeIds do
            writer.Line("<li>" + code (stableText "type:" id) + "</li>")
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
        writer.Line("<p>Pending proposal: " + code (stableText "transaction:" model.TransactionId) + ". Nothing is published until it is committed.</p>")
        writer.Line("<p>Base snapshot: " + code (stableText "snapshot:" model.BaseSnapshotId) + "</p>")
        writer.Line("<p>Proposed snapshot: " + code (stableText "snapshot:" model.ProposedSnapshotId) + "</p>")
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
                + code (stableText "function:" item.ProposedVersion.Function.FunctionId) + "</h2>")
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
        writer.Line("<p>The requested change and resulting diff exactly as the authoring session returned them to the agent.</p>")
        writer.Line("<pre><code>" + html model.EngineReview + "</code></pre>")
        writer.Line("</section>")
        emitDocumentEnd writer
        writer.ToString()
