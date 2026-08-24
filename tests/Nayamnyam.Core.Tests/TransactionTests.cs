using Nayamnyam.Core;

namespace Nayamnyam.Core.Tests;

public sealed class TransactionTests
{
    [Fact]
    public void SimulateRenamePreservesBaseAndIdentity()
    {
        var engine = new TransactionEngine(CreateSnapshot());
        var baseJson = SnapshotSerializer.Serialize(engine.CurrentSnapshot);
        var request = Request(10, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "validate_profile"));

        var proposed = Assert.IsType<SimulationProposed>(engine.Simulate(request)).Simulation;

        Assert.Equal(baseJson, SnapshotSerializer.Serialize(engine.CurrentSnapshot));
        Assert.Equal(engine.CurrentSnapshot.Functions[0].Id, proposed.ProposedSnapshot.Functions[0].Id);
        Assert.Equal("validate_profile", proposed.ProposedSnapshot.Functions[0].DisplayName);
        var rename = Assert.Single(proposed.Diff.Renames);
        Assert.Equal("validate_user", rename.OldDisplayName);
        Assert.Equal("validate_profile", rename.NewDisplayName);
    }

    [Fact]
    public void CommitPublishesExactPreviewAndRejectLeavesCurrentUnchanged()
    {
        var engine = new TransactionEngine(CreateSnapshot());
        var proposed = Assert.IsType<SimulationProposed>(engine.Simulate(Request(10, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Types[0].Id, "Text")))).Simulation;
        var rejected = engine.Reject(proposed);

        Assert.Equal(proposed.BaseSnapshotId, engine.CurrentSnapshot.Id);
        Assert.Equal(engine.CurrentSnapshot.Id, rejected.CurrentSnapshotId);
        var committed = Assert.IsType<CommitSucceeded>(engine.Commit(proposed, new()));

        Assert.Same(proposed.ProposedSnapshot, committed.Snapshot);
        Assert.Equal(proposed.BaseSnapshotId, committed.Snapshot.ParentId);
        Assert.Equal("Text", engine.CurrentSnapshot.Types[0].DisplayName);
    }

    [Fact]
    public void StalePreviewCannotOverwriteCurrentSnapshot()
    {
        var engine = new TransactionEngine(CreateSnapshot());
        var first = Assert.IsType<SimulationProposed>(engine.Simulate(Request(10, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "first")))).Simulation;
        var second = Assert.IsType<SimulationProposed>(engine.Simulate(Request(20, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "second")))).Simulation;

        engine.Commit(first, new());
        var rejected = Assert.IsType<CommitRejected>(engine.Commit(second, new()));

        Assert.Equal("StaleBaseSnapshot", Assert.Single(rejected.Diagnostics).Code);
        Assert.Equal("first", engine.CurrentSnapshot.Functions[0].DisplayName);
    }

    [Fact]
    public void InvalidOperationRejectsAtomically()
    {
        var engine = new TransactionEngine(CreateSnapshot());
        var baseJson = SnapshotSerializer.Serialize(engine.CurrentSnapshot);
        var result = Assert.IsType<SimulationRejected>(engine.Simulate(Request(10, engine.CurrentSnapshot.Id,
            new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "validate_profile"),
            new RenameSymbol(Id(IdDomain.Function, 99), "missing"))));

        Assert.Equal("TargetNotFound", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(baseJson, SnapshotSerializer.Serialize(engine.CurrentSnapshot));
    }

    [Fact]
    public async Task ConcurrentCommitsPublishOnlyOnePreview()
    {
        var engine = new TransactionEngine(CreateSnapshot());
        var first = Assert.IsType<SimulationProposed>(engine.Simulate(Request(10, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "first")))).Simulation;
        var second = Assert.IsType<SimulationProposed>(engine.Simulate(Request(20, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "second")))).Simulation;
        using var ready = new Barrier(2);

        var results = await Task.WhenAll(
            Task.Run(() => { ready.SignalAndWait(); return engine.Commit(first, new()); }),
            Task.Run(() => { ready.SignalAndWait(); return engine.Commit(second, new()); }));

        Assert.Single(results.OfType<CommitSucceeded>());
        Assert.Single(results.OfType<CommitRejected>());
    }

    [Fact]
    public void ReusedTransactionIdIsRejectedAfterPublication()
    {
        var engine = new TransactionEngine(CreateSnapshot());
        var first = Assert.IsType<SimulationProposed>(engine.Simulate(Request(10, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "first")))).Simulation;
        engine.Commit(first, new());

        var result = Assert.IsType<SimulationRejected>(engine.Simulate(Request(10, engine.CurrentSnapshot.Id, new RenameSymbol(engine.CurrentSnapshot.Functions[0].Id, "second"))));

        Assert.Equal("SnapshotIdCollision", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void RenameDiffIsNetChangeAndCanceledRenameIsRejected()
    {
        var engine = new TransactionEngine(CreateSnapshot());
        var id = engine.CurrentSnapshot.Functions[0].Id;
        var net = Assert.IsType<SimulationProposed>(engine.Simulate(Request(10, engine.CurrentSnapshot.Id, new RenameSymbol(id, "b"), new RenameSymbol(id, "c")))).Simulation;

        var rename = Assert.Single(net.Diff.Renames);
        Assert.Equal("validate_user", rename.OldDisplayName);
        Assert.Equal("c", rename.NewDisplayName);

        var canceled = Assert.IsType<SimulationRejected>(engine.Simulate(Request(20, engine.CurrentSnapshot.Id, new RenameSymbol(id, "b"), new RenameSymbol(id, "validate_user"))));
        Assert.Equal("NoChange", Assert.Single(canceled.Diagnostics).Code);
    }

    [Fact]
    public void StoreReservesKnownHistoricalIds()
    {
        var snapshot = CreateSnapshot();
        var historicalId = Id(IdDomain.Snapshot, 99);
        var engine = new TransactionEngine(new InMemorySnapshotStore(snapshot, [historicalId]));

        var result = Assert.IsType<SimulationRejected>(engine.Simulate(Request(99, snapshot.Id, new RenameSymbol(snapshot.Functions[0].Id, "renamed"))));

        Assert.Equal("SnapshotIdCollision", Assert.Single(result.Diagnostics).Code);
    }

    private static TransactionRequest Request(int id, StableId baseId, params SemanticOperation[] operations) => new(Id(IdDomain.Transaction, id), baseId, operations);

    private static Snapshot CreateSnapshot()
    {
        var type = new SemanticType(Id(IdDomain.Type, 1), "String", TypeKind.Builtin, [], []);
        return new Snapshot(Id(IdDomain.Snapshot, 1), null, [type], [new SemanticFunction(Id(IdDomain.Function, 1), "validate_user", [], type.Id, [])], [], [], []);
    }

    private static StableId Id(IdDomain domain, int value) => new(domain, new Guid($"00000000-0000-0000-0000-{value:D12}"));
}
