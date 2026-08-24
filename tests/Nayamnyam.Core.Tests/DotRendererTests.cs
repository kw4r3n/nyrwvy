using Nayamnyam.Core;

namespace Nayamnyam.Core.Tests;

public sealed class DotRendererTests
{
    [Fact]
    public void RendersFocusedSignatureWithLabeledEdges()
    {
        var id = new StableId(IdDomain.Function, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var type = new FocusEntityRef(new StableId(IdDomain.Type, Guid.Parse("00000000-0000-0000-0000-000000000002")), "Text");
        var focus = new FunctionFocusPayload(new StableId(IdDomain.Snapshot, Guid.Parse("00000000-0000-0000-0000-000000000003")), new FocusEntityRef(id, "say\"hi"), [], type, [], [], [], []);
        var dot = DotRenderer.RenderFunctionFocus(focus);
        Assert.Contains("digraph function_focus", dot, StringComparison.Ordinal);
        Assert.Contains("returns", dot, StringComparison.Ordinal);
        Assert.Contains("say\\\"hi", dot, StringComparison.Ordinal);
    }
}
