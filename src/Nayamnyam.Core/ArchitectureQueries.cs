namespace Nayamnyam.Core;

public sealed record DirectCall(StableId CallId, StableId CallerFunctionId, StableId CalleeFunctionId);
public enum ChangeReviewStatus { Rejected, Incomplete, StructurallyComplete }
public sealed record ChangeReviewPayload(
    StableId TransactionId,
    StableId BaseSnapshotId,
    StableId? ProposedSnapshotId,
    ChangeReviewStatus Status,
    SemanticDiff? Diff,
    IReadOnlyList<StableId> AffectedTargetIds,
    IReadOnlyList<MissingArgumentObligation> OpenObligations,
    IReadOnlyList<Diagnostic> Diagnostics);
public sealed record FocusEntityRef(StableId Id, string DisplayName);
public sealed record FocusParameter(FocusEntityRef Parameter, FocusEntityRef Type);
public sealed record FocusCall(StableId CallId, FocusEntityRef Function);
public sealed record FocusOpenMissingArgument(StableId ObligationId, StableId CallId, FocusEntityRef MissingParameter, StableId OriginatingTransactionId);
public sealed record FunctionFocusPayload(StableId SnapshotId, FocusEntityRef Function, IReadOnlyList<FocusParameter> Inputs, FocusEntityRef OutputType, IReadOnlyList<FocusCall> DirectCallers, IReadOnlyList<FocusCall> DirectCallees, IReadOnlyList<FocusEntityRef> DeclaredEffects, IReadOnlyList<FocusOpenMissingArgument> OpenMissingArguments);

public sealed class ArchitectureQueries
{
    private readonly Snapshot snapshot;

    public ArchitectureQueries(Snapshot snapshot) => this.snapshot = snapshot;

    public object? Find(StableId id)
    {
        foreach (var type in snapshot.Types)
        {
            if (type.Id == id) return type;
            if (type.Fields.FirstOrDefault(field => field.Id == id) is { } field) return field;
            if (type.Variants.FirstOrDefault(variant => variant.Id == id) is { } variant) return variant;
        }
        foreach (var function in snapshot.Functions)
        {
            if (function.Id == id) return function;
            if (function.Parameters.FirstOrDefault(parameter => parameter.Id == id) is { } parameter) return parameter;
            if (function.Calls.FirstOrDefault(call => call.Id == id) is { } call) return call;
        }
        if (snapshot.Effects.FirstOrDefault(effect => effect.Id == id) is { } effect) return effect;
        if (snapshot.Capabilities.FirstOrDefault(capability => capability.Id == id) is { } capability) return capability;
        return snapshot.Obligations.FirstOrDefault(obligation => obligation.Id == id);
    }

    public IReadOnlyList<FocusEntityRef> GetFunctions() => snapshot.Functions.OrderBy(function => function.Id.ToString(), StringComparer.Ordinal).Select(function => new FocusEntityRef(function.Id, function.DisplayName)).ToArray();

    public IReadOnlyList<DirectCall> GetDirectCallers(StableId functionId) => Calls().Where(call => call.CalleeFunctionId == functionId).OrderBy(call => call.CallId.ToString(), StringComparer.Ordinal).ToArray();
    public IReadOnlyList<DirectCall> GetDirectCallees(StableId functionId) => Calls().Where(call => call.CallerFunctionId == functionId).OrderBy(call => call.CallId.ToString(), StringComparer.Ordinal).ToArray();
    public IReadOnlyList<MissingArgumentObligation> GetOpenObligations(StableId targetId) => snapshot.Obligations.Where(obligation => obligation.Status == ObligationStatus.Open && (obligation.Id == targetId || obligation.CallId == targetId || obligation.ParameterId == targetId)).OrderBy(obligation => obligation.Id.ToString(), StringComparer.Ordinal).ToArray();

    public FunctionFocusPayload? GetFunctionFocus(StableId functionId)
    {
        var function = snapshot.Functions.SingleOrDefault(candidate => candidate.Id == functionId);
        if (function is null) return null;
        var types = snapshot.Types.ToDictionary(type => type.Id);
        var functions = snapshot.Functions.ToDictionary(candidate => candidate.Id);
        FocusEntityRef Ref(StableId id, string name) => new(id, name);
        var inputs = function.Parameters.OrderBy(parameter => parameter.Id.ToString(), StringComparer.Ordinal).Select(parameter => new FocusParameter(Ref(parameter.Id, parameter.DisplayName), Ref(parameter.TypeId, types[parameter.TypeId].DisplayName)));
        var callers = GetDirectCallers(functionId).Select(call => new FocusCall(call.CallId, Ref(call.CallerFunctionId, functions[call.CallerFunctionId].DisplayName)));
        var callees = GetDirectCallees(functionId).Select(call => new FocusCall(call.CallId, Ref(call.CalleeFunctionId, functions[call.CalleeFunctionId].DisplayName)));
        var effects = function.DeclaredEffectIds.OrderBy(id => id.ToString(), StringComparer.Ordinal).Select(id => snapshot.Effects.Single(effect => effect.Id == id)).Select(effect => Ref(effect.Id, effect.DisplayName));
        var relatedCallIds = function.Calls.Select(call => call.Id).Concat(GetDirectCallers(functionId).Select(call => call.CallId)).ToHashSet();
        var callsById = snapshot.Functions.SelectMany(owner => owner.Calls).ToDictionary(call => call.Id);
        var obligations = snapshot.Obligations.Where(obligation => obligation.Status == ObligationStatus.Open && relatedCallIds.Contains(obligation.CallId)).OrderBy(obligation => obligation.Id.ToString(), StringComparer.Ordinal).Select(obligation => { var callee = functions[callsById[obligation.CallId].CalleeFunctionId]; var parameter = callee.Parameters.Single(candidate => candidate.Id == obligation.ParameterId); return new FocusOpenMissingArgument(obligation.Id, obligation.CallId, Ref(parameter.Id, parameter.DisplayName), obligation.OriginatingTransactionId); });
        return new(snapshot.Id, Ref(function.Id, function.DisplayName), Snapshot.Freeze(inputs), Ref(function.ResultTypeId, types[function.ResultTypeId].DisplayName), Snapshot.Freeze(callers), Snapshot.Freeze(callees), Snapshot.Freeze(effects), Snapshot.Freeze(obligations));
    }

    public static ChangeReviewPayload SummarizeChange(TransactionRequest request, SimulationResult result)
    {
        if (result is SimulationRejected rejected)
        {
            return new(request.Id, rejected.BaseSnapshotId, null, ChangeReviewStatus.Rejected, null, [], [], Snapshot.Freeze(rejected.Diagnostics));
        }

        var simulation = ((SimulationProposed)result).Simulation;
        if (simulation.TransactionId != request.Id || simulation.BaseSnapshotId != request.BaseSnapshotId) throw new ArgumentException("The simulation does not belong to the request.", nameof(result));
        var diff = simulation.Diff;
        var affected = diff.Renames.Select(change => change.TargetId)
            .Concat(diff.ParametersAdded.Select(change => change.FunctionId)).Concat(diff.ParametersAdded.Select(change => change.ParameterId))
            .Concat(diff.ArgumentsAttached.Select(change => change.CallId)).Concat(diff.ObligationsCreated.Select(change => change.ObligationId)).Concat(diff.ObligationsResolved.Select(change => change.ObligationId))
            .Distinct().OrderBy(id => id.ToString(), StringComparer.Ordinal);
        var open = simulation.ProposedSnapshot.Obligations.Where(obligation => obligation.Status == ObligationStatus.Open).OrderBy(obligation => obligation.Id.ToString(), StringComparer.Ordinal);
        return new(request.Id, request.BaseSnapshotId, simulation.ProposedSnapshot.Id, open.Any() ? ChangeReviewStatus.Incomplete : ChangeReviewStatus.StructurallyComplete, diff, Snapshot.Freeze(affected), Snapshot.Freeze(open), []);
    }

    private IEnumerable<DirectCall> Calls()
    {
        var bodyCalls = BodyCallIndex.Build(snapshot);
        return bodyCalls.Count > 0
            ? bodyCalls.Select(call => new DirectCall(call.CallId, call.CallerFunctionId, call.CalleeFunctionId))
            : snapshot.Functions.SelectMany(function => function.Calls.Select(call => new DirectCall(call.Id, function.Id, call.CalleeFunctionId)));
    }
}
