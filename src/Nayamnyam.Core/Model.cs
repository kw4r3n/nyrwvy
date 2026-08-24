using System.Collections.ObjectModel;

namespace Nayamnyam.Core;

public enum TypeKind { Builtin, Product, Union }

public sealed record SemanticField(StableId Id, string DisplayName, StableId TypeId);
public sealed record SemanticVariant(StableId Id, string DisplayName);

public sealed record SemanticType
{
    public SemanticType(StableId id, string displayName, TypeKind kind, IEnumerable<SemanticField> fields, IEnumerable<SemanticVariant> variants)
    {
        Id = id; DisplayName = displayName; Kind = kind; Fields = Snapshot.Freeze(fields); Variants = Snapshot.Freeze(variants);
    }
    public StableId Id { get; }
    public string DisplayName { get; }
    public TypeKind Kind { get; }
    public IReadOnlyList<SemanticField> Fields { get; }
    public IReadOnlyList<SemanticVariant> Variants { get; }
}

public sealed record Parameter(StableId Id, string DisplayName, StableId TypeId);
public abstract record FunctionBody;
public sealed record StringLiteralBody(string Value) : FunctionBody;
public sealed record BoolLiteralBody(bool Value) : FunctionBody;
public sealed record ParameterReferenceBody(StableId ParameterId) : FunctionBody;
public sealed record ConditionalBody(FunctionBody Condition, FunctionBody WhenTrue, FunctionBody WhenFalse) : FunctionBody;
public sealed record DirectCallBody(StableId Id, StableId CalleeFunctionId, IReadOnlyList<BodyArgument> Arguments) : FunctionBody
{
    public DirectCallBody(StableId id, StableId calleeFunctionId, IEnumerable<BodyArgument> arguments) : this(id, calleeFunctionId, Snapshot.Freeze(arguments)) { }
}
public sealed record BodyArgument(StableId TargetParameterId, FunctionBody Value);
public sealed record ArgumentBinding(StableId TargetParameterId, StableId SourceParameterId);
public sealed record Call
{
    public Call(StableId id, StableId calleeFunctionId, IEnumerable<ArgumentBinding> arguments)
    {
        Id = id;
        CalleeFunctionId = calleeFunctionId;
        Arguments = Snapshot.Freeze(arguments);
    }

    public StableId Id { get; }
    public StableId CalleeFunctionId { get; }
    public IReadOnlyList<ArgumentBinding> Arguments { get; }
}

public sealed record SemanticFunction
{
    public SemanticFunction(StableId id, string displayName, IEnumerable<Parameter> parameters, StableId resultTypeId, IEnumerable<StableId> declaredEffectIds)
        : this(id, displayName, parameters, resultTypeId, declaredEffectIds, []) { }

    public SemanticFunction(StableId id, string displayName, IEnumerable<Parameter> parameters, StableId resultTypeId, IEnumerable<StableId> declaredEffectIds, IEnumerable<Call> calls)
        : this(id, displayName, parameters, resultTypeId, declaredEffectIds, calls, null) { }

    public SemanticFunction(StableId id, string displayName, IEnumerable<Parameter> parameters, StableId resultTypeId, IEnumerable<StableId> declaredEffectIds, IEnumerable<Call> calls, FunctionBody? body)
    {
        Id = id; DisplayName = displayName; Parameters = Snapshot.Freeze(parameters); ResultTypeId = resultTypeId; DeclaredEffectIds = Snapshot.Freeze(declaredEffectIds); Calls = Snapshot.Freeze(calls); Body = body;
    }
    public StableId Id { get; }
    public string DisplayName { get; }
    public IReadOnlyList<Parameter> Parameters { get; }
    public StableId ResultTypeId { get; }
    public IReadOnlyList<StableId> DeclaredEffectIds { get; }
    public IReadOnlyList<Call> Calls { get; }
    public FunctionBody? Body { get; }
}

public sealed record Effect(StableId Id, string DisplayName);
public sealed record Capability(StableId Id, string DisplayName, StableId EffectId);
public sealed record BuiltinTypes(StableId StringTypeId, StableId BoolTypeId);
public enum ObligationStatus { Open, Resolved }
public sealed record MissingArgumentObligation(
    StableId Id,
    ObligationStatus Status,
    StableId OriginatingTransactionId,
    StableId CallId,
    StableId ParameterId,
    StableId? ResolutionTransactionId,
    StableId? ResolutionSourceParameterId);

public sealed record Snapshot
{
    public const int SchemaVersion = 4;
    public Snapshot(StableId id, StableId? parentId, IEnumerable<SemanticType> types, IEnumerable<SemanticFunction> functions, IEnumerable<Effect> effects, IEnumerable<Capability> capabilities, IEnumerable<MissingArgumentObligation> obligations, BuiltinTypes? builtinTypes = null)
    {
        Id = id; ParentId = parentId; Types = Freeze(types); Functions = Freeze(functions); Effects = Freeze(effects); Capabilities = Freeze(capabilities); Obligations = Freeze(obligations); BuiltinTypes = builtinTypes;
    }
    public StableId Id { get; }
    public StableId? ParentId { get; }
    public IReadOnlyList<SemanticType> Types { get; }
    public IReadOnlyList<SemanticFunction> Functions { get; }
    public IReadOnlyList<Effect> Effects { get; }
    public IReadOnlyList<Capability> Capabilities { get; }
    public IReadOnlyList<MissingArgumentObligation> Obligations { get; }
    public BuiltinTypes? BuiltinTypes { get; }

    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => new ReadOnlyCollection<T>(values.ToArray());
}
