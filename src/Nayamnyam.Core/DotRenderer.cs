using System.Text;

namespace Nayamnyam.Core;

public static class DotRenderer
{
    public static string RenderFunctionFocus(FunctionFocusPayload focus)
    {
        var text = new StringBuilder("digraph function_focus {\n  graph [rankdir=LR, ordering=out, outputorder=edgesfirst];\n  node [fontname=\"sans-serif\"];\n  edge [fontname=\"sans-serif\"];\n");
        Node(text, focus.Function, "box", "function");
        Node(text, focus.OutputType, "hexagon", "type"); Edge(text, focus.Function.Id, focus.OutputType.Id, "returns");
        foreach (var input in focus.Inputs.OrderBy(input => input.Parameter.Id.ToString(), StringComparer.Ordinal)) { Node(text, input.Parameter, "ellipse", "parameter"); Node(text, input.Type, "hexagon", "type"); Edge(text, focus.Function.Id, input.Parameter.Id, "input"); Edge(text, input.Parameter.Id, input.Type.Id, "has type"); }
        foreach (var call in focus.DirectCallers.OrderBy(call => call.CallId.ToString(), StringComparer.Ordinal)) { Node(text, call.Function, "box", "function"); Node(text, new FocusEntityRef(call.CallId, "call"), "diamond", "call"); Edge(text, call.Function.Id, call.CallId, "makes call"); Edge(text, call.CallId, focus.Function.Id, "calls"); }
        foreach (var call in focus.DirectCallees.OrderBy(call => call.CallId.ToString(), StringComparer.Ordinal)) { Node(text, call.Function, "box", "function"); Node(text, new FocusEntityRef(call.CallId, "call"), "diamond", "call"); Edge(text, focus.Function.Id, call.CallId, "makes call"); Edge(text, call.CallId, call.Function.Id, "calls"); }
        foreach (var effect in focus.DeclaredEffects.OrderBy(effect => effect.Id.ToString(), StringComparer.Ordinal)) { Node(text, effect, "octagon", "effect"); Edge(text, focus.Function.Id, effect.Id, "declares effect"); }
        foreach (var obligation in focus.OpenMissingArguments.OrderBy(item => item.ObligationId.ToString(), StringComparer.Ordinal)) { var item = new FocusEntityRef(obligation.ObligationId, "MissingArgument"); Node(text, item, "note", "obligation"); Node(text, obligation.MissingParameter, "ellipse", "parameter"); Node(text, new FocusEntityRef(obligation.CallId, "call"), "diamond", "call"); Edge(text, item.Id, obligation.CallId, "blocks call"); Edge(text, item.Id, obligation.MissingParameter.Id, "missing argument"); }
        return text.Append("}\n").ToString();
    }

    private static void Node(StringBuilder text, FocusEntityRef entity, string shape, string kind) => text.Append("  ").Append(Id(entity.Id)).Append(" [shape=").Append(shape).Append(", label=\"").Append(Escape($"{kind}\\n{entity.DisplayName}")).Append("\", tooltip=\"").Append(Escape(entity.Id.ToString())).Append("\"];\n");
    private static void Edge(StringBuilder text, StableId source, StableId target, string label) => text.Append("  ").Append(Id(source)).Append(" -> ").Append(Id(target)).Append(" [label=\"").Append(Escape(label)).Append("\", tooltip=\"").Append(Escape(label)).Append("\"];\n");
    private static string Id(StableId id) => $"\"{id}\"";
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
