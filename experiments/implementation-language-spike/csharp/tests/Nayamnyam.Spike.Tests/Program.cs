using System.Text;
using Nayamnyam.Spike;

using CallId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.CallTag>;
using FunctionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.FunctionTag>;
using ParameterId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ParameterTag>;
using SnapshotId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.SnapshotTag>;
using TransactionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TransactionTag>;
using TypeId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TypeTag>;

namespace Nayamnyam.Spike.Tests;

internal static class Program
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "initial-snapshot-v1.json");

    private static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("stable IDs reject blank strings", StableIdsRejectBlankStrings),
            (
                "canonical fixture loads and re-serializes byte-for-byte",
                CanonicalFixtureRoundTripsByteForByte),
            (
                "function rename preserves FunctionId and call target",
                FunctionRenamePreservesIdAndCallTarget),
            (
                "stale-base rename returns Rejected and preserves the original object graph",
                StaleBaseRenameRejectsWithoutMutation),
            ("add-parameter appends one parameter", AddParameterAppendsOneParameter),
            (
                "add-parameter creates exactly one missing-argument obligation for the initial call",
                AddParameterCreatesMissingArgumentObligation),
            ("add-parameter returns Incomplete", AddParameterReturnsIncomplete),
            (
                "canonical output order is deterministic across two construction orders",
                ConstructionOrderDoesNotAffectCanonicalOutput),
        };

        foreach (var (name, body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }

        return 0;
    }

    private static void StableIdsRejectBlankStrings()
    {
        Assertions.Throws<ArgumentException>(() => new FunctionId(" "));
    }

    private static void CanonicalFixtureRoundTripsByteForByte()
    {
        var expected = File.ReadAllBytes(FixturePath);
        var snapshot = CanonicalJson.ReadSnapshot(expected);

        Assertions.SequenceEqual(expected, CanonicalJson.WriteSnapshot(snapshot));
    }

    private static void FunctionRenamePreservesIdAndCallTarget()
    {
        var snapshot = ReadInitialSnapshot();
        var functionId = new FunctionId("function:validate-user");
        var result = Operations.RenameFunction(
            snapshot,
            snapshot.Id,
            new TransactionId("transaction:rename"),
            functionId,
            "validate");
        var executable = Assertions.IsAndReturn<TransactionResult.Executable>(result);
        var renamed = executable.Snapshot.Functions.Single(item => item.Id == functionId);

        Assertions.Equal(functionId, renamed.Id);
        Assertions.Equal("validate", renamed.Name);
        Assertions.Equal(functionId, executable.Snapshot.Calls.Single().CalleeId);
        Assertions.Equal("validate_user", snapshot.Functions.Single(item => item.Id == functionId).Name);
    }

    private static void StaleBaseRenameRejectsWithoutMutation()
    {
        var snapshot = ReadInitialSnapshot();
        var originalFunction = snapshot.Functions.Single(
            item => item.Id == new FunctionId("function:validate-user"));
        var originalBytes = CanonicalJson.WriteSnapshot(snapshot);
        var result = Operations.RenameFunction(
            snapshot,
            new SnapshotId("snapshot:stale"),
            new TransactionId("transaction:rename"),
            originalFunction.Id,
            "validate");

        Assertions.Is<TransactionResult.Rejected>(result);
        Assertions.Same(
            originalFunction,
            snapshot.Functions.Single(item => item.Id == originalFunction.Id));
        Assertions.SequenceEqual(originalBytes, CanonicalJson.WriteSnapshot(snapshot));
    }

    private static void AddParameterAppendsOneParameter()
    {
        var result = AddEmailParameter(ReadInitialSnapshot());
        var incomplete = Assertions.IsAndReturn<TransactionResult.Incomplete>(result);
        var parameters = incomplete.Snapshot.Functions.Single(
            item => item.Id == new FunctionId("function:validate-user")).Parameters;

        Assertions.Equal(2, parameters.Count);
        Assertions.Equal(new ParameterId("parameter:input"), parameters[0].Id);
        Assertions.Equal(new ParameterId("parameter:email"), parameters[1].Id);
    }

    private static void AddParameterCreatesMissingArgumentObligation()
    {
        var result = AddEmailParameter(ReadInitialSnapshot());
        var incomplete = Assertions.IsAndReturn<TransactionResult.Incomplete>(result);

        Assertions.Equal(1, incomplete.Snapshot.Obligations.Count);
        var obligation = incomplete.Snapshot.Obligations.Single();
        Assertions.Equal(
            new StableId<ObligationTag>("obligation:add-email:main-validate:email"),
            obligation.Id);
        var kind = Assertions.IsAndReturn<ObligationKind.MissingArgument>(obligation.Kind);
        Assertions.Equal(new CallId("call:main-validate"), kind.CallId);
        Assertions.Equal(new ParameterId("parameter:email"), kind.ParameterId);
        Assertions.SequenceEqual(new[] { obligation.Id }, incomplete.Blocking);
    }

    private static void AddParameterReturnsIncomplete()
    {
        Assertions.Is<TransactionResult.Incomplete>(AddEmailParameter(ReadInitialSnapshot()));
    }

    private static void ConstructionOrderDoesNotAffectCanonicalOutput()
    {
        var canonical = ReadInitialSnapshot();
        var reverseConstructed = new Snapshot(
            canonical.SchemaVersion,
            canonical.Id,
            canonical.Types.Reverse(),
            canonical.Functions.Reverse(),
            canonical.Calls.Reverse(),
            canonical.Obligations.Reverse());

        Assertions.SequenceEqual(
            CanonicalJson.WriteSnapshot(canonical),
            CanonicalJson.WriteSnapshot(reverseConstructed));
    }

    private static Snapshot ReadInitialSnapshot()
    {
        return CanonicalJson.ReadSnapshot(File.ReadAllBytes(FixturePath));
    }

    private static TransactionResult AddEmailParameter(Snapshot snapshot)
    {
        return Operations.AddParameter(
            snapshot,
            snapshot.Id,
            new TransactionId("transaction:add-email"),
            new FunctionId("function:validate-user"),
            new Parameter(
                new ParameterId("parameter:email"),
                "email",
                new TypeId("type:string")));
    }
}
