using CallId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.CallTag>;
using FunctionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.FunctionTag>;
using ObligationId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ObligationTag>;
using ParameterId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ParameterTag>;
using SnapshotId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.SnapshotTag>;
using TypeId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TypeTag>;

namespace Nayamnyam.Spike;

internal static class OwnedReadOnly
{
    public static IReadOnlyList<T> Copy<T>(IEnumerable<T> items, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(items, parameterName);
        return Array.AsReadOnly(items.ToArray());
    }
}

public sealed record TypeEntity
{
    public TypeId Id { get; }

    public string Name { get; }

    public TypeEntity(TypeId id, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Id = id;
        Name = name;
    }
}

public sealed record Parameter
{
    public ParameterId Id { get; }

    public string Name { get; }

    public TypeId TypeId { get; }

    public Parameter(ParameterId id, string name, TypeId typeId)
    {
        ArgumentNullException.ThrowIfNull(name);
        Id = id;
        Name = name;
        TypeId = typeId;
    }
}

public sealed record FunctionEntity
{
    public FunctionId Id { get; }

    public string Name { get; }

    public IReadOnlyList<Parameter> Parameters { get; }

    public FunctionEntity(
        FunctionId id,
        string name,
        IEnumerable<Parameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        Id = id;
        Name = name;
        Parameters = OwnedReadOnly.Copy(parameters, nameof(parameters));
    }
}

public sealed record ArgumentBinding
{
    public ParameterId ParameterId { get; }

    public string ValueRef { get; }

    public ArgumentBinding(ParameterId parameterId, string valueRef)
    {
        ArgumentNullException.ThrowIfNull(valueRef);
        ParameterId = parameterId;
        ValueRef = valueRef;
    }
}

public sealed record CallEntity
{
    public CallId Id { get; }

    public FunctionId CalleeId { get; }

    public IReadOnlyList<ArgumentBinding> Arguments { get; }

    public CallEntity(
        CallId id,
        FunctionId calleeId,
        IEnumerable<ArgumentBinding> arguments)
    {
        Id = id;
        CalleeId = calleeId;
        Arguments = OwnedReadOnly.Copy(arguments, nameof(arguments));
    }
}

public abstract record ObligationKind
{
    private ObligationKind()
    {
    }

    public sealed record MissingArgument(CallId CallId, ParameterId ParameterId)
        : ObligationKind;
}

public abstract record ObligationStatus
{
    private ObligationStatus()
    {
    }

    public sealed record Open : ObligationStatus;
}

public sealed record Obligation
{
    public ObligationId Id { get; }

    public ObligationKind Kind { get; }

    public ObligationStatus Status { get; }

    public Obligation(
        ObligationId id,
        ObligationKind kind,
        ObligationStatus status)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(status);
        Id = id;
        Kind = kind;
        Status = status;
    }
}

public sealed record Snapshot
{
    public int SchemaVersion { get; }

    public SnapshotId Id { get; }

    public IReadOnlyList<TypeEntity> Types { get; }

    public IReadOnlyList<FunctionEntity> Functions { get; }

    public IReadOnlyList<CallEntity> Calls { get; }

    public IReadOnlyList<Obligation> Obligations { get; }

    public Snapshot(
        int schemaVersion,
        SnapshotId id,
        IEnumerable<TypeEntity> types,
        IEnumerable<FunctionEntity> functions,
        IEnumerable<CallEntity> calls,
        IEnumerable<Obligation> obligations)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        Types = OwnedReadOnly.Copy(types, nameof(types));
        Functions = OwnedReadOnly.Copy(functions, nameof(functions));
        Calls = OwnedReadOnly.Copy(calls, nameof(calls));
        Obligations = OwnedReadOnly.Copy(obligations, nameof(obligations));
    }
}

public sealed record Diagnostic
{
    public string Code { get; }

    public string Message { get; }

    public Diagnostic(string code, string message)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
    }
}

public abstract record SemanticChange
{
    private SemanticChange()
    {
    }

    public sealed record FunctionRenamed(
        FunctionId FunctionId,
        string OldName,
        string NewName) : SemanticChange;

    public sealed record ParameterAdded(
        FunctionId FunctionId,
        ParameterId ParameterId) : SemanticChange;

    public sealed record ObligationCreated(ObligationId ObligationId)
        : SemanticChange;
}

public abstract record TransactionResult
{
    private TransactionResult()
    {
    }

    public sealed record Rejected : TransactionResult
    {
        public SnapshotId BaseSnapshotId { get; }

        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public Rejected(
            SnapshotId baseSnapshotId,
            IEnumerable<Diagnostic> diagnostics)
        {
            BaseSnapshotId = baseSnapshotId;
            Diagnostics = OwnedReadOnly.Copy(diagnostics, nameof(diagnostics));
        }
    }

    public sealed record Incomplete : TransactionResult
    {
        public Snapshot Snapshot { get; }

        public IReadOnlyList<SemanticChange> Diff { get; }

        public IReadOnlyList<ObligationId> Blocking { get; }

        public Incomplete(
            Snapshot snapshot,
            IEnumerable<SemanticChange> diff,
            IEnumerable<ObligationId> blocking)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Snapshot = snapshot;
            Diff = OwnedReadOnly.Copy(diff, nameof(diff));
            Blocking = OwnedReadOnly.Copy(blocking, nameof(blocking));
        }
    }

    public sealed record Executable : TransactionResult
    {
        public Snapshot Snapshot { get; }

        public IReadOnlyList<SemanticChange> Diff { get; }

        public Executable(
            Snapshot snapshot,
            IEnumerable<SemanticChange> diff)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Snapshot = snapshot;
            Diff = OwnedReadOnly.Copy(diff, nameof(diff));
        }
    }
}
