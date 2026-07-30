using FunctionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.FunctionTag>;
using ObligationId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ObligationTag>;
using SnapshotId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.SnapshotTag>;
using TransactionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TransactionTag>;

namespace Nayamnyam.Spike;

public static class Operations
{
    public static TransactionResult RenameFunction(
        Snapshot snapshot,
        SnapshotId baseSnapshotId,
        TransactionId transactionId,
        FunctionId functionId,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(newName);

        var earlyRejection = ValidateBaseAndSnapshot(snapshot, baseSnapshotId);
        if (earlyRejection is not null)
        {
            return earlyRejection;
        }

        var target = snapshot.Functions.SingleOrDefault(item => item.Id == functionId);
        if (target is null)
        {
            return Reject(
                baseSnapshotId,
                "unknown_function",
                $"Function '{functionId}' does not exist.");
        }

        var renamed = new FunctionEntity(target.Id, newName, target.Parameters);
        var functions = snapshot.Functions.Select(
            item => item.Id == functionId ? renamed : item);
        var candidate = new Snapshot(
            1,
            DeriveSnapshotId(transactionId),
            snapshot.Types,
            functions,
            snapshot.Calls,
            snapshot.Obligations);
        SemanticChange[] diff =
        [
            new SemanticChange.FunctionRenamed(functionId, target.Name, newName),
        ];

        return CompleteCandidate(candidate, diff);
    }

    public static TransactionResult AddParameter(
        Snapshot snapshot,
        SnapshotId baseSnapshotId,
        TransactionId transactionId,
        FunctionId functionId,
        Parameter parameter)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameter);

        var earlyRejection = ValidateBaseAndSnapshot(snapshot, baseSnapshotId);
        if (earlyRejection is not null)
        {
            return earlyRejection;
        }

        var target = snapshot.Functions.SingleOrDefault(item => item.Id == functionId);
        if (target is null)
        {
            return Reject(
                baseSnapshotId,
                "unknown_function",
                $"Function '{functionId}' does not exist.");
        }

        if (snapshot.Functions
            .SelectMany(item => item.Parameters)
            .Any(item => item.Id == parameter.Id))
        {
            return Reject(
                baseSnapshotId,
                "duplicate_parameter_id",
                $"Parameter ID '{parameter.Id}' already exists.");
        }

        if (!snapshot.Types.Any(item => item.Id == parameter.TypeId))
        {
            return Reject(
                baseSnapshotId,
                "unknown_parameter_type",
                $"Parameter '{parameter.Id}' references unknown type "
                    + $"'{parameter.TypeId}'.");
        }

        var appendedParameters = target.Parameters.Append(parameter);
        var changedFunction = new FunctionEntity(
            target.Id,
            target.Name,
            appendedParameters);
        var changedFunctions = snapshot.Functions.Select(
            item => item.Id == functionId ? changedFunction : item);
        var newObligations = snapshot.Calls
            .Where(call =>
                call.CalleeId == functionId
                && call.Arguments.All(argument => argument.ParameterId != parameter.Id))
            .Select(call => new Obligation(
                DeriveObligationId(transactionId, call.Id, parameter.Id),
                new ObligationKind.MissingArgument(call.Id, parameter.Id),
                new ObligationStatus.Open()))
            .ToArray();

        var duplicateObligationId = newObligations
            .Select(item => item.Id)
            .FirstOrDefault(id => snapshot.Obligations.Any(item => item.Id == id));
        if (duplicateObligationId != default)
        {
            return Reject(
                baseSnapshotId,
                "duplicate_obligation_id",
                $"Derived obligation ID '{duplicateObligationId}' already exists.");
        }

        var candidate = new Snapshot(
            1,
            DeriveSnapshotId(transactionId),
            snapshot.Types,
            changedFunctions,
            snapshot.Calls,
            snapshot.Obligations.Concat(newObligations));
        var diff = new List<SemanticChange>
        {
            new SemanticChange.ParameterAdded(functionId, parameter.Id),
        };
        diff.AddRange(newObligations
            .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
            .Select(item => new SemanticChange.ObligationCreated(item.Id)));

        return CompleteCandidate(candidate, diff);
    }

    private static TransactionResult.Rejected? ValidateBaseAndSnapshot(
        Snapshot snapshot,
        SnapshotId baseSnapshotId)
    {
        if (snapshot.Id != baseSnapshotId)
        {
            return Reject(
                baseSnapshotId,
                "stale_base_snapshot",
                $"Base snapshot '{baseSnapshotId}' does not match "
                    + $"current snapshot '{snapshot.Id}'.");
        }

        var diagnostics = Validation.SnapshotDiagnostics(snapshot);
        return diagnostics.Count == 0
            ? null
            : new TransactionResult.Rejected(baseSnapshotId, diagnostics);
    }

    private static TransactionResult CompleteCandidate(
        Snapshot candidate,
        IReadOnlyList<SemanticChange> diff)
    {
        var blocking = candidate.Obligations
            .Where(item => item.Status is ObligationStatus.Open)
            .Select(item => item.Id)
            .OrderBy(item => item.Value, StringComparer.Ordinal)
            .ToArray();

        return blocking.Length == 0
            ? new TransactionResult.Executable(candidate, diff)
            : new TransactionResult.Incomplete(candidate, diff, blocking);
    }

    private static TransactionResult.Rejected Reject(
        SnapshotId baseSnapshotId,
        string code,
        string message)
    {
        return new TransactionResult.Rejected(
            baseSnapshotId,
            [new Diagnostic(code, message)]);
    }

    private static SnapshotId DeriveSnapshotId(TransactionId transactionId)
    {
        return new SnapshotId($"snapshot:{transactionId.AtomicTail}");
    }

    private static ObligationId DeriveObligationId(
        TransactionId transactionId,
        StableId<CallTag> callId,
        StableId<ParameterTag> parameterId)
    {
        return new ObligationId(
            $"obligation:{transactionId.AtomicTail}:{callId.AtomicTail}:"
                + parameterId.AtomicTail);
    }
}
