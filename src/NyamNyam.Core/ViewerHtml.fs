namespace NyamNyam.Core

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
        | TypeRef.Named typeId -> stableText "type:" typeId
        | TypeRef.Capability capabilityId -> stableText "capability:" capabilityId
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
            "Option<" + typeRefText item + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            "Result<" + typeRefText okType + ", " + typeRefText errorType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) ->
            "List<" + typeRefText item + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            "Option<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            "Result<invalid-arity:" + string arguments.Length + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            "List<invalid-arity:" + string arguments.Length + ">"

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
        | ViewerRelationKind.ReadsParameter -> "reads parameter"
        | ViewerRelationKind.ReadsPatternBinding -> "reads pattern binding"
        | ViewerRelationKind.InvokesCapabilityOperation -> "invokes capability operation"
        | ViewerRelationKind.ConstructsType -> "constructs type"
        | ViewerRelationKind.AccessesField -> "accesses field"
        | ViewerRelationKind.ConstructsVariant -> "constructs variant"
        | ViewerRelationKind.BranchCondition -> "branch condition"
        | ViewerRelationKind.MatchInput -> "match input"
        | ViewerRelationKind.BindInput -> "evaluate before body"

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
        | ViewerNodeRole.StringContains -> "string contains"
        | ViewerNodeRole.Int64WrappingAdd -> "Int64 wrapping add"
        | ViewerNodeRole.Int64WrappingSubtract -> "Int64 wrapping subtract"
        | ViewerNodeRole.Int64WrappingMultiply -> "Int64 wrapping multiply"
        | ViewerNodeRole.Int64Equal -> "Int64 equal"
        | ViewerNodeRole.Int64LessThan -> "Int64 less than"
        | ViewerNodeRole.Int64LessThanOrEqual -> "Int64 less than or equal"
        | ViewerNodeRole.Int64GreaterThan -> "Int64 greater than"
        | ViewerNodeRole.Int64GreaterThanOrEqual -> "Int64 greater than or equal"
        | ViewerNodeRole.Float64Add -> "Float64 add"
        | ViewerNodeRole.Float64Subtract -> "Float64 subtract"
        | ViewerNodeRole.Float64Multiply -> "Float64 multiply"
        | ViewerNodeRole.Float64Divide -> "Float64 divide"
        | ViewerNodeRole.Float64Equal -> "Float64 equal"
        | ViewerNodeRole.Float64LessThan -> "Float64 less than"
        | ViewerNodeRole.Float64IsNaN -> "Float64 is NaN"
        | ViewerNodeRole.Float64IsFinite -> "Float64 is finite"
        | ViewerNodeRole.Float64IsInfinite -> "Float64 is infinite"
        | ViewerNodeRole.ListEmpty -> "List empty"
        | ViewerNodeRole.ListConstruct -> "List construct"
        | ViewerNodeRole.ListLength -> "List length"
        | ViewerNodeRole.ListAppendItem -> "List append item"
        | ViewerNodeRole.ListConcat -> "List concat"
        | ViewerNodeRole.ListMap -> "List map"
        | ViewerNodeRole.ListFilter -> "List filter"
        | ViewerNodeRole.ListAny -> "List any"
        | ViewerNodeRole.ListAll -> "List all"
        | ViewerNodeRole.DirectCall -> "direct call"
        | ViewerNodeRole.CapabilityInvoke -> "capability invoke"
        | ViewerNodeRole.ProductConstruct -> "product construct"
        | ViewerNodeRole.FieldAccess -> "field access"
        | ViewerNodeRole.UnionConstruct -> "union construct"
        | ViewerNodeRole.OptionNone -> "Option.None"
        | ViewerNodeRole.OptionSome -> "Option.Some"
        | ViewerNodeRole.ResultOk -> "Result.Ok"
        | ViewerNodeRole.ResultErr -> "Result.Err"
        | ViewerNodeRole.Conditional -> "conditional"
        | ViewerNodeRole.MatchUnion -> "union match"
        | ViewerNodeRole.Bind -> "bind once"
        | ViewerNodeRole.MatchOption -> "Option match"
        | ViewerNodeRole.MatchResult -> "Result match"

    let private branchLabel (role: ViewerBranchRole): string =
        match role with
        | ViewerBranchRole.WhenTrue -> "when true"
        | ViewerBranchRole.WhenFalse -> "when false"
        | ViewerBranchRole.UnionVariant variantId -> "variant " + stableText "variant:" variantId
        | ViewerBranchRole.BindBody -> "body after input"
        | ViewerBranchRole.OptionSome -> "Option.Some"
        | ViewerBranchRole.OptionNone -> "Option.None"
        | ViewerBranchRole.ResultOk -> "Result.Ok"
        | ViewerBranchRole.ResultErr -> "Result.Err"

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
            emitRelations writer node.Relations
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

    let private emitDeclarations (writer: HtmlWriter) (model: ViewerFocusModel) =
        writer.Line("<section>")
        writer.Line("<h3>Referenced declarations</h3>")
        writer.Line("<h4>Types</h4>")
        if List.isEmpty model.ReferencedTypes then writer.Line("<p>None.</p>")
        for typeEntity in model.ReferencedTypes do
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

        writer.Line("<h4>Capabilities</h4>")
        if List.isEmpty model.ReferencedCapabilities then writer.Line("<p>None.</p>")
        for capability in model.ReferencedCapabilities do
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

        writer.Line("<h4>Effects</h4>")
        if List.isEmpty model.ReferencedEffects then
            writer.Line("<p>None.</p>")
        else
            writer.Line("<ul>")
            for effect in model.ReferencedEffects do
                writer.Line(
                    "<li>" + html effect.DisplayName + " — "
                    + code (stableText "effect:" effect.Id) + "</li>")
            writer.Line("</ul>")
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
            writer.Line("<p>Result: " + code (typeRefText signature.ResultType) + "</p></article>")
        writer.Line("</details>")

    let renderFocusDocument (model: ViewerFocusModel): string =
        let writer = HtmlWriter()
        emitDocumentStart writer ("NyamNyam Focus — " + model.Function.DisplayName)
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
        emitDocumentStart writer "NyamNyam Change Review"
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
