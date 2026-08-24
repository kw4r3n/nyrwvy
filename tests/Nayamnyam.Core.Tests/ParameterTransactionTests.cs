using Nayamnyam.Core;

namespace Nayamnyam.Core.Tests;

public sealed class ParameterTransactionTests
{
    [Fact]
    public void AddParameterCreatesPerCallObligationAndResolutionAttachesArgument()
    {
        var stringType = Id(IdDomain.Type, 1);
        var calleeId = Id(IdDomain.Function, 1);
        var callerId = Id(IdDomain.Function, 2);
        var sourceId = Id(IdDomain.Parameter, 1);
        var callId = Id(IdDomain.GraphNode, 1);
        var snapshot = new Snapshot(Id(IdDomain.Snapshot, 1), null, [new SemanticType(stringType, "String", TypeKind.Builtin, [], [])], [new SemanticFunction(calleeId, "validate_user", [], stringType, []), new SemanticFunction(callerId, "run", [new Parameter(sourceId, "input", stringType)], stringType, [], [new Call(callId, calleeId, [])])], [], [], []);
        var engine = new TransactionEngine(snapshot);
        var parameterId = Id(IdDomain.Parameter, 2);
        var obligationId = Id(IdDomain.Obligation, 1);

        var addResult = engine.Simulate(new TransactionRequest(Id(IdDomain.Transaction, 10), snapshot.Id, [new AddFunctionParameter(calleeId, parameterId, "policy", stringType, [new LeaveArgumentMissing(callId, obligationId)])]));
        if (addResult is SimulationRejected rejected) throw new Xunit.Sdk.XunitException(Assert.Single(rejected.Diagnostics).Code);
        var add = Assert.IsType<SimulationProposed>(addResult).Simulation;
        Assert.True(add.HasBlockingObligations);
        Assert.Single(add.Diff.ParametersAdded);
        Assert.Single(add.Diff.ObligationsCreated);
        engine.Commit(add, new(true));

        var resolve = Assert.IsType<SimulationProposed>(engine.Simulate(new TransactionRequest(Id(IdDomain.Transaction, 20), engine.CurrentSnapshot.Id, [new ResolveMissingArgument(obligationId, sourceId)]))).Simulation;
        Assert.False(resolve.HasBlockingObligations);
        Assert.Equal(ObligationStatus.Resolved, Assert.Single(resolve.ProposedSnapshot.Obligations).Status);
        Assert.Single(resolve.ProposedSnapshot.Functions.Single(function => function.Id == callerId).Calls.Single().Arguments);
        Assert.Single(resolve.Diff.ArgumentsAttached);
        Assert.Single(resolve.Diff.ObligationsResolved);
    }

    [Fact]
    public void CallDefensivelyCopiesArgumentsAndEngineReservesParentSnapshotId()
    {
        var arguments = new List<ArgumentBinding>();
        var call = new Call(Id(IdDomain.GraphNode, 1), Id(IdDomain.Function, 1), arguments);
        arguments.Add(new ArgumentBinding(Id(IdDomain.Parameter, 1), Id(IdDomain.Parameter, 2)));
        Assert.Empty(call.Arguments);

        var type = new SemanticType(Id(IdDomain.Type, 1), "String", TypeKind.Builtin, [], []);
        var ancestorId = Id(IdDomain.Snapshot, 5);
        var current = new Snapshot(Id(IdDomain.Snapshot, 1), ancestorId, [type], [new SemanticFunction(Id(IdDomain.Function, 1), "f", [], type.Id, [])], [], [], []);
        var engine = new TransactionEngine(current);
        var result = Assert.IsType<SimulationRejected>(engine.Simulate(new TransactionRequest(Id(IdDomain.Transaction, 5), current.Id, [new RenameSymbol(current.Functions[0].Id, "g")])));

        Assert.Equal("SnapshotIdCollision", Assert.Single(result.Diagnostics).Code);
    }

    private static StableId Id(IdDomain domain, int value) => new(domain, new Guid($"00000000-0000-0000-0000-{value:D12}"));
}
