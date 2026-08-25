namespace Nayamnyam.Core

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

    let private html value = WebUtility.HtmlEncode(value)
    let private stableValue id = StableId.value id |> html

    let private typeRefText typeRef =
        let rec render value =
            match value with
            | TypeRef.Primitive PrimitiveType.String -> "String"
            | TypeRef.Primitive PrimitiveType.Bool -> "Bool"
            | TypeRef.Primitive PrimitiveType.Unit -> "Unit"
            | TypeRef.Named typeId -> "type:" + StableId.value typeId
            | TypeRef.Capability capabilityId -> "capability:" + StableId.value capabilityId
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) ->
                "Option<" + render item + ">"
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                "Result<" + render okType + ", " + render errorType + ">"
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
                "Option<invalid-arity:" + string arguments.Length + ">"
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
                "Result<invalid-arity:" + string arguments.Length + ">"
        render typeRef |> html

    let private targetText target =
        match target with
        | ViewerTarget.Node id -> "node:" + StableId.value id
        | ViewerTarget.Function id -> "function:" + StableId.value id
        | ViewerTarget.Parameter id -> "parameter:" + StableId.value id
        | ViewerTarget.PatternBinding id -> "pattern-binding:" + StableId.value id
        | ViewerTarget.CapabilityOperation id -> "capability-operation:" + StableId.value id
        | ViewerTarget.Type id -> "type:" + StableId.value id
        | ViewerTarget.Field id -> "field:" + StableId.value id
        | ViewerTarget.Variant id -> "variant:" + StableId.value id
        |> html

    let private relationLabel = function
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

    let private nodeRoleLabel = function
        | ViewerNodeRole.ParameterReference -> "parameter reference"
        | ViewerNodeRole.PatternReference -> "pattern reference"
        | ViewerNodeRole.StringLiteral value -> "string literal “" + html value + "”"
        | ViewerNodeRole.BoolLiteral value -> "bool literal " + if value then "true" else "false"
        | ViewerNodeRole.UnitLiteral -> "unit literal"
        | ViewerNodeRole.StringIsEmpty -> "string is empty"
        | ViewerNodeRole.StringContains -> "string contains"
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
        | ViewerNodeRole.MatchOption -> "Option match"
        | ViewerNodeRole.MatchResult -> "Result match"

    let private branchLabel = function
        | ViewerBranchRole.WhenTrue -> "when true"
        | ViewerBranchRole.WhenFalse -> "when false"
        | ViewerBranchRole.UnionVariant variantId -> "variant " + stableValue variantId
        | ViewerBranchRole.OptionSome -> "Option.Some"
        | ViewerBranchRole.OptionNone -> "Option.None"
        | ViewerBranchRole.ResultOk -> "Result.Ok"
        | ViewerBranchRole.ResultErr -> "Result.Err"

    let private readinessLabel = function
        | ChangeReviewReadiness.Complete -> "complete — lowering may proceed if backend readiness also passes"
        | ChangeReviewReadiness.Incomplete ids ->
            "incomplete — blocking obligations: "
            + (ids |> List.map StableId.value |> String.concat ", " |> html)
        | ChangeReviewReadiness.Invalid issues ->
            "invalid — "
            + (issues
               |> List.map (fun issue -> issue.Code + ": " + issue.Message)
               |> String.concat " | "
               |> html)

    let private emitDocumentStart (writer: HtmlWriter) title =
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
        writer.Line("  .status { font-weight: 700; }")
        writer.Line("  code { overflow-wrap: anywhere; }")
        writer.Line("  table { border-collapse: collapse; width: 100%; }")
        writer.Line("  th, td { border: 1px solid CanvasText; padding: .35rem .5rem; text-align: left; vertical-align: top; }")
        writer.Line("  ul { padding-left: 1.3rem; }")
        writer.Line("  .missing { font-weight: 700; }")
        writer.Line("</style>")
        writer.Pop()
        writer.Line("</head>")
        writer.Line("<body>")

    let private emitDocumentEnd (writer: HtmlWriter) =
        writer.Line("</body>")
        writer.Line("</html>")

    let private emitParameters (writer: HtmlWriter) parameters =
        writer.Line("<table>")
        writer.Line("<thead><tr><th>Parameter</th><th>Type</th><th>Stable ID</th></tr></thead>")
        writer.Line("<tbody>")
        writer.Push()
        for parameter in parameters do
            writer.Line(
                "<tr><td>" + html parameter.DisplayName + "</td><td><code>"
                + typeRefText parameter.Type + "</code></td><td><code>parameter:"
                + stableValue parameter.Id + "</code></td></tr>")
        writer.Pop()
        writer.Line("</tbody>")
        writer.Line("</table>")

    let private emitCallSites (writer: HtmlWriter) heading sites direction =
        writer.Line("<section>")
        writer.Line("<h3>" + html heading + "</h3>")
        if List.isEmpty sites then
            writer.Line("<p>None.</p>")
        else
            writer.Line("<ul>")
            for site in sites do
                let endpoint = direction site
                writer.Line(
                    "<li><code>function:" + stableValue endpoint
                    + "</code> via <code>node:" + stableValue site.CallNodeId + "</code></li>")
            writer.Line("</ul>")
        writer.Line("</section>")

    let private emitRelations (writer: HtmlWriter) relations =
        if not (List.isEmpty relations) then
            writer.Line("<ul aria-label=\"node relations\">")
            for relation in relations do
                writer.Line(
                    "<li>" + html (relationLabel relation.Kind) + ": <code>"
                    + targetText relation.Target + "</code></li>")
            writer.Line("</ul>")

    let rec private emitGraph (writer: HtmlWriter) (graph: ViewerGraph) =
        writer.Line("<section class=\"graph\" aria-label=\"value graph\">")
        writer.Line("<p>Graph result: <code>node:" + stableValue graph.ResultNodeId + "</code></p>")
        writer.Line("<div class=\"nodes\">")
        writer.Push()
        for node in graph.Nodes do
            writer.Line("<article class=\"node\">")
            writer.Line("<h4>" + nodeRoleLabel node.Role + "</h4>")
            writer.Line("<p>Stable ID: <code>node:" + stableValue node.Id + "</code></p>")
            match node.Type with
            | Some nodeType -> writer.Line("<p>Type: <code>" + typeRefText nodeType + "</code></p>")
            | None -> writer.Line("<p>Type: <strong>unknown / not derivable</strong></p>")
            emitRelations writer node.Relations
            for branch in node.Branches do
                writer.Line("<section class=\"branch\">")
                writer.Line("<h5>Branch: " + branchLabel branch.Role + "</h5>")
                if not (List.isEmpty branch.Bindings) then
                    writer.Line("<ul aria-label=\"branch bindings\">")
                    for binding in branch.Bindings do
                        let name = binding.DisplayName |> Option.defaultValue "(unnamed)" |> html
                        let fieldText =
                            match binding.SourceFieldId with
                            | Some fieldId -> "; source field <code>field:" + stableValue fieldId + "</code>"
                            | None -> ""
                        writer.Line(
                            "<li>" + name + " — <code>pattern-binding:" + stableValue binding.Id
                            + "</code>; type <code>" + typeRefText binding.Type + "</code>" + fieldText + "</li>")
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
            writer.Line("<details><summary>" + html typeEntity.DisplayName + " — <code>type:" + stableValue typeEntity.Id + "</code></summary>")
            match typeEntity.Kind with
            | SemanticTypeKind.Product fields ->
                writer.Line("<p>Product fields</p><ul>")
                for field in fields |> List.sortBy (fun item -> StableId.value item.Id) do
                    writer.Line("<li>" + html field.DisplayName + ": <code>" + typeRefText field.Type + "</code> — <code>field:" + stableValue field.Id + "</code></li>")
                writer.Line("</ul>")
            | SemanticTypeKind.Union variants ->
                writer.Line("<p>Union variants</p><ul>")
                for variant in variants |> List.sortBy (fun item -> StableId.value item.Id) do
                    writer.Line("<li>" + html variant.DisplayName + " — <code>variant:" + stableValue variant.Id + "</code></li>")
                writer.Line("</ul>")
            writer.Line("</details>")

        writer.Line("<h4>Capabilities</h4>")
        if List.isEmpty model.ReferencedCapabilities then writer.Line("<p>None.</p>")
        for capability in model.ReferencedCapabilities do
            writer.Line("<details><summary>" + html capability.DisplayName + " — <code>capability:" + stableValue capability.Id + "</code></summary><ul>")
            for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
                writer.Line("<li>" + html operation.DisplayName + " — <code>capability-operation:" + stableValue operation.Id + "</code>; result <code>" + typeRefText operation.ResultType + "</code>; effect <code>effect:" + stableValue operation.EffectId + "</code></li>")
            writer.Line("</ul></details>")

        writer.Line("<h4>Effects</h4>")
        if List.isEmpty model.ReferencedEffects then writer.Line("<p>None.</p>") else writer.Line("<ul>")
        for effect in model.ReferencedEffects do
            writer.Line("<li>" + html effect.DisplayName + " — <code>effect:" + stableValue effect.Id + "</code></li>")
        if not (List.isEmpty model.ReferencedEffects) then writer.Line("</ul>")
        writer.Line("</section>")

    let private emitMissing (writer: HtmlWriter) (model: ViewerFocusModel) =
        let anyMissing =
            not (List.isEmpty model.MissingFunctionIds)
            || not (List.isEmpty model.MissingTypeIds)
            || not (List.isEmpty model.MissingCapabilityIds)
            || not (List.isEmpty model.MissingEffectIds)
        if anyMissing then
            writer.Line("<section class=\"missing\"><h3>Unresolved declarations</h3><ul>")
            for id in model.MissingFunctionIds do writer.Line("<li>function:<code>" + stableValue id + "</code></li>")
            for id in model.MissingTypeIds do writer.Line("<li>type:<code>" + stableValue id + "</code></li>")
            for id in model.MissingCapabilityIds do writer.Line("<li>capability:<code>" + stableValue id + "</code></li>")
            for id in model.MissingEffectIds do writer.Line("<li>effect:<code>" + stableValue id + "</code></li>")
            writer.Line("</ul></section>")

    let private emitObligations (writer: HtmlWriter) obligations =
        writer.Line("<section><h3>Blocking obligations</h3>")
        if List.isEmpty obligations then
            writer.Line("<p>None.</p>")
        else
            writer.Line("<ul>")
            for obligation in obligations do
                writer.Line(
                    "<li><code>obligation:" + stableValue obligation.ObligationId
                    + "</code> — missing argument for <code>parameter:" + stableValue obligation.ParameterId
                    + "</code> at <code>node:" + stableValue obligation.CallNodeId
                    + "</code>; origin <code>transaction:" + stableValue obligation.OriginTransactionId + "</code></li>")
            writer.Line("</ul>")
        writer.Line("</section>")

    let private emitFocusBody (writer: HtmlWriter) (model: ViewerFocusModel) =
        writer.Line("<header>")
        writer.Line("<h1>Function Focus — " + html model.Function.DisplayName + "</h1>")
        writer.Line("<p>Snapshot: <code>snapshot:" + stableValue model.SnapshotId + "</code></p>")
        writer.Line("<p>Function: <code>function:" + stableValue model.Function.FunctionId + "</code></p>")
        writer.Line("<p>Result type: <code>" + typeRefText model.Function.ResultType + "</code></p>")
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
            writer.Line("<article><h4>" + html signature.DisplayName + "</h4><p><code>function:" + stableValue signature.FunctionId + "</code></p>")
            emitParameters writer signature.Parameters
            writer.Line("<p>Result: <code>" + typeRefText signature.ResultType + "</code></p></article>")
        writer.Line("</details>")

    let renderFocusDocument (model: ViewerFocusModel): string =
        let writer = HtmlWriter()
        emitDocumentStart writer ("Nayamnyam Focus — " + model.Function.DisplayName)
        emitFocusBody writer model
        emitDocumentEnd writer
        writer.ToString()

    let private emitDiff (writer: HtmlWriter) (diff: SemanticDiff) =
        writer.Line("<section><h2>Semantic change summary</h2>")
        writer.Line("<h3>Renames</h3><ul>")
        for rename in diff.Renames do
            match rename with
            | TypeRenamed(id, oldName, newName) ->
                writer.Line("<li>type <code>" + stableValue id + "</code>: " + html oldName + " → " + html newName + "</li>")
            | FunctionRenamed(id, oldName, newName) ->
                writer.Line("<li>function <code>" + stableValue id + "</code>: " + html oldName + " → " + html newName + "</li>")
        writer.Line("</ul>")
        writer.Line("<h3>Parameters added</h3><ul>")
        for item in diff.ParametersAdded do
            writer.Line("<li><code>function:" + stableValue item.OwnerFunctionId + "</code> added " + html item.AddedDisplayName + " — <code>parameter:" + stableValue item.AddedParameterId + "</code>; type <code>" + typeRefText item.AddedType + "</code></li>")
        writer.Line("</ul>")
        writer.Line("<h3>Arguments attached</h3><ul>")
        for item in diff.ArgumentsAttached do
            writer.Line("<li><code>node:" + stableValue item.CallNodeId + "</code>: <code>parameter:" + stableValue item.TargetParameterId + "</code> ← <code>node:" + stableValue item.ValueNodeId + "</code></li>")
        writer.Line("</ul>")
        writer.Line("<h3>Obligations created</h3><ul>")
        for item in diff.ObligationsCreated do
            writer.Line("<li><code>obligation:" + stableValue item.ObligationId + "</code> for <code>node:" + stableValue item.CallNodeId + "</code> / <code>parameter:" + stableValue item.ParameterId + "</code></li>")
        writer.Line("</ul>")
        writer.Line("<h3>Obligations resolved</h3><ul>")
        for item in diff.ObligationsResolved do
            writer.Line("<li><code>obligation:" + stableValue item.ObligationId + "</code> resolved with <code>node:" + stableValue item.ValueNodeId + "</code></li>")
        writer.Line("</ul></section>")

    let renderChangeReviewDocument (model: ViewerChangeReviewModel): string =
        let writer = HtmlWriter()
        emitDocumentStart writer "Nayamnyam Change Review"
        writer.Line("<header><h1>Change Review</h1>")
        writer.Line("<p>Transaction: <code>transaction:" + stableValue model.TransactionId + "</code></p>")
        writer.Line("<p>Base snapshot: <code>snapshot:" + stableValue model.BaseSnapshotId + "</code></p>")
        writer.Line("<p>Proposed snapshot: <code>snapshot:" + stableValue model.ProposedSnapshotId + "</code></p>")
        writer.Line("<p class=\"status\">Readiness: " + html (readinessLabel model.Readiness) + "</p></header>")
        emitDiff writer model.Diff
        writer.Line("<section><h2>Affected stable identities</h2><ul>")
        for id in model.AffectedFunctionIds do writer.Line("<li><code>function:" + stableValue id + "</code></li>")
        for id in model.AffectedTypeIds do writer.Line("<li><code>type:" + stableValue id + "</code></li>")
        writer.Line("</ul></section>")
        writer.Line("<section><h2>Affected function focus</h2>")
        for focus in model.FunctionFocus do
            writer.Line("<article>")
            emitFocusBody writer focus
            writer.Line("</article>")
        writer.Line("</section>")
        emitDocumentEnd writer
        writer.ToString()
