using Nayamnyam.Core;

namespace Nayamnyam.Core.Tests;

public sealed class ArchitectureQueriesTests
{
    [Fact]
    public void FindsSymbolsAndDirectCallsByStableId()
    {
        var type = new SemanticType(Id(IdDomain.Type, 1), "String", TypeKind.Builtin, [], []);
        var callee = new SemanticFunction(Id(IdDomain.Function, 1), "validate", [], type.Id, []);
        var call = new Call(Id(IdDomain.GraphNode, 1), callee.Id, []);
        var caller = new SemanticFunction(Id(IdDomain.Function, 2), "run", [], type.Id, [], [call]);
        var queries = new ArchitectureQueries(new Snapshot(Id(IdDomain.Snapshot, 1), null, [type], [callee, caller], [], [], []));

        Assert.Same(callee, queries.Find(callee.Id));
        Assert.Equal(call.Id, Assert.Single(queries.GetDirectCallers(callee.Id)).CallId);
        Assert.Equal(call.CalleeFunctionId, Assert.Single(queries.GetDirectCallees(caller.Id)).CalleeFunctionId);
        var focus = queries.GetFunctionFocus(callee.Id);
        Assert.NotNull(focus);
        Assert.Equal(callee.Id, focus.Function.Id);
        Assert.Equal(caller.Id, Assert.Single(focus.DirectCallers).Function.Id);
        Assert.Empty(focus.DirectCallees);
        Assert.Null(queries.GetFunctionFocus(type.Id));
    }

    [Fact]
    public void SummarizesProposedAndRejectedChanges()
    {
        var type = new SemanticType(Id(IdDomain.Type, 1), "String", TypeKind.Builtin, [], []);
        var function = new SemanticFunction(Id(IdDomain.Function, 1), "validate", [], type.Id, []);
        var snapshot = new Snapshot(Id(IdDomain.Snapshot, 1), null, [type], [function], [], [], []);
        var engine = new TransactionEngine(snapshot);
        var request = new TransactionRequest(Id(IdDomain.Transaction, 10), snapshot.Id, [new RenameSymbol(function.Id, "check")]);
        var proposed = ArchitectureQueries.SummarizeChange(request, engine.Simulate(request));
        var rejected = ArchitectureQueries.SummarizeChange(request, engine.Simulate(new TransactionRequest(Id(IdDomain.Transaction, 20), Id(IdDomain.Snapshot, 99), [])));

        Assert.Equal(ChangeReviewStatus.StructurallyComplete, proposed.Status);
        Assert.Equal(function.Id, Assert.Single(proposed.AffectedTargetIds));
        Assert.Equal(ChangeReviewStatus.Rejected, rejected.Status);
        Assert.Equal("StaleBaseSnapshot", Assert.Single(rejected.Diagnostics).Code);
    }

    private static StableId Id(IdDomain domain, int value) => new(domain, new Guid($"00000000-0000-0000-0000-{value:D12}"));
}
