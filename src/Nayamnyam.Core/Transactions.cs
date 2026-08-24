namespace Nayamnyam.Core;

public sealed record TransactionRequest(StableId Id, StableId BaseSnapshotId, IReadOnlyList<SemanticOperation> Operations);
public abstract record SemanticOperation;
public sealed record RenameSymbol(StableId TargetId, string NewDisplayName) : SemanticOperation;
public abstract record CallPlan(StableId CallId);
public sealed record BindExistingArgument(StableId CallId, StableId SourceParameterId) : CallPlan(CallId);
public sealed record LeaveArgumentMissing(StableId CallId, StableId ObligationId) : CallPlan(CallId);
public sealed record AddFunctionParameter(StableId FunctionId, StableId ParameterId, string DisplayName, StableId TypeId, IReadOnlyList<CallPlan> CallPlans) : SemanticOperation;
public sealed record ResolveMissingArgument(StableId ObligationId, StableId SourceParameterId) : SemanticOperation;
public sealed record TransactionPolicy(bool AllowBlockingObligations = false);
public sealed record Diagnostic(string Code, string Message);
public sealed record SymbolRenamed(StableId TargetId, string OldDisplayName, string NewDisplayName);
public sealed record ParameterAdded(StableId FunctionId, StableId ParameterId, string DisplayName, StableId TypeId);
public sealed record CallArgumentAttached(StableId CallId, StableId TargetParameterId, StableId SourceParameterId);
public sealed record MissingArgumentCreated(StableId ObligationId, StableId CallId, StableId ParameterId);
public sealed record MissingArgumentResolved(StableId ObligationId, StableId CallId, StableId ParameterId, StableId SourceParameterId);
public sealed record SemanticDiff(
    IReadOnlyList<SymbolRenamed> Renames,
    IReadOnlyList<ParameterAdded> ParametersAdded,
    IReadOnlyList<CallArgumentAttached> ArgumentsAttached,
    IReadOnlyList<MissingArgumentCreated> ObligationsCreated,
    IReadOnlyList<MissingArgumentResolved> ObligationsResolved);

public abstract record SimulationResult;
public sealed record SimulationRejected(StableId BaseSnapshotId, IReadOnlyList<Diagnostic> Diagnostics) : SimulationResult;
public sealed record SimulationProposed(TransactionSimulation Simulation) : SimulationResult;
public abstract record CommitResult;
public sealed record CommitRejected(StableId BaseSnapshotId, IReadOnlyList<Diagnostic> Diagnostics) : CommitResult;
public sealed record CommitSucceeded(Snapshot Snapshot, SemanticDiff Diff) : CommitResult;
public sealed record RejectionSucceeded(StableId TransactionId, StableId CurrentSnapshotId);

public sealed class TransactionSimulation
{
    internal TransactionSimulation(StableId transactionId, StableId baseSnapshotId, Snapshot proposedSnapshot, SemanticDiff diff)
    {
        TransactionId = transactionId;
        BaseSnapshotId = baseSnapshotId;
        ProposedSnapshot = proposedSnapshot;
        Diff = diff;
    }

    public StableId TransactionId { get; }
    public StableId BaseSnapshotId { get; }
    public Snapshot ProposedSnapshot { get; }
    public SemanticDiff Diff { get; }
    public bool HasBlockingObligations => ProposedSnapshot.Obligations.Any(obligation => obligation.Status == ObligationStatus.Open);
}

public sealed class TransactionEngine
{
    private readonly InMemorySnapshotStore store;

    public TransactionEngine(Snapshot initialSnapshot) : this(new InMemorySnapshotStore(initialSnapshot)) { }
    public TransactionEngine(InMemorySnapshotStore store) => this.store = store;

    public Snapshot CurrentSnapshot => store.Current;

    public SimulationResult Simulate(TransactionRequest request)
    {
        var baseSnapshot = CurrentSnapshot;
        if (request.BaseSnapshotId != baseSnapshot.Id)
        {
            return Reject(request.BaseSnapshotId, "StaleBaseSnapshot", "The transaction base snapshot is not current.");
        }

        if (request.Id.Domain != IdDomain.Transaction)
        {
            return Reject(request.BaseSnapshotId, "WrongTransactionIdDomain", "Transaction IDs must use the transaction domain.");
        }

        var functions = baseSnapshot.Functions.ToList();
        var types = baseSnapshot.Types.ToArray();
        var obligations = baseSnapshot.Obligations.ToList();
        var renames = new List<SymbolRenamed>();
        var parametersAdded = new List<ParameterAdded>();
        var argumentsAttached = new List<CallArgumentAttached>();
        var obligationsCreated = new List<MissingArgumentCreated>();
        var obligationsResolved = new List<MissingArgumentResolved>();
        foreach (var operation in request.Operations)
        {
            if (operation is RenameSymbol rename)
            {
                if (string.IsNullOrWhiteSpace(rename.NewDisplayName)) return Reject(request.BaseSnapshotId, "InvalidDisplayName", "A display name cannot be empty.");
                if (rename.TargetId.Domain == IdDomain.Function)
                {
                    var index = functions.FindIndex(function => function.Id == rename.TargetId);
                    if (index < 0) return Reject(request.BaseSnapshotId, "TargetNotFound", $"Function '{rename.TargetId}' was not found.");
                    var target = functions[index];
                    if (target.DisplayName == rename.NewDisplayName) continue;
                    functions[index] = new SemanticFunction(target.Id, rename.NewDisplayName, target.Parameters, target.ResultTypeId, target.DeclaredEffectIds, target.Calls, target.Body);
                    renames.Add(new(rename.TargetId, target.DisplayName, rename.NewDisplayName));
                    continue;
                }

                if (rename.TargetId.Domain == IdDomain.Type)
                {
                    var index = Array.FindIndex(types, type => type.Id == rename.TargetId);
                if (index < 0) return Reject(request.BaseSnapshotId, "TargetNotFound", $"Type '{rename.TargetId}' was not found.");
                var target = types[index];
                    if (target.DisplayName == rename.NewDisplayName) continue;
                types[index] = new SemanticType(target.Id, rename.NewDisplayName, target.Kind, target.Fields, target.Variants);
                renames.Add(new(rename.TargetId, target.DisplayName, rename.NewDisplayName));
                continue;
                }

                return Reject(request.BaseSnapshotId, "WrongTargetIdDomain", "Only functions and types can be renamed.");
            }

            if (operation is AddFunctionParameter add)
            {
                var index = functions.FindIndex(function => function.Id == add.FunctionId);
                if (add.FunctionId.Domain != IdDomain.Function || index < 0) return Reject(request.BaseSnapshotId, "TargetNotFound", "The target function was not found.");
                if (add.ParameterId.Domain != IdDomain.Parameter || add.TypeId.Domain != IdDomain.Type || !types.Any(type => type.Id == add.TypeId)) return Reject(request.BaseSnapshotId, "InvalidParameter", "The new parameter ID or type is invalid.");
                if (functions.SelectMany(function => function.Parameters).Any(parameter => parameter.Id == add.ParameterId)) return Reject(request.BaseSnapshotId, "DuplicateId", "The parameter ID already exists.");
                var affected = functions.SelectMany((caller, callerIndex) => caller.Calls.Where(call => call.CalleeFunctionId == add.FunctionId).Select(call => (callerIndex, call))).ToArray();
                if (affected.Length != add.CallPlans.Count || add.CallPlans.Select(plan => plan.CallId).Distinct().Count() != affected.Length || affected.Any(item => !add.CallPlans.Any(plan => plan.CallId == item.call.Id))) return Reject(request.BaseSnapshotId, "CallPlanMismatch", "Every affected call requires exactly one plan.");
                var target = functions[index]; functions[index] = new SemanticFunction(target.Id, target.DisplayName, target.Parameters.Append(new Parameter(add.ParameterId, add.DisplayName, add.TypeId)), target.ResultTypeId, target.DeclaredEffectIds, target.Calls);
                foreach (var (callerIndex, call) in affected)
                {
                    var plan = add.CallPlans.Single(item => item.CallId == call.Id);
                    if (plan is BindExistingArgument binding)
                    {
                        var source = functions[callerIndex].Parameters.SingleOrDefault(parameter => parameter.Id == binding.SourceParameterId);
                        if (source is null || source.TypeId != add.TypeId) return Reject(request.BaseSnapshotId, "InvalidArgumentSource", "The argument source must be a same-typed caller parameter.");
                        ReplaceCall(functions, callerIndex, call, call.Arguments.Append(new ArgumentBinding(add.ParameterId, source.Id)));
                        argumentsAttached.Add(new(call.Id, add.ParameterId, source.Id));
                    }
                    else if (plan is LeaveArgumentMissing missing && missing.ObligationId.Domain == IdDomain.Obligation && !obligations.Any(obligation => obligation.Id == missing.ObligationId)) { obligations.Add(new MissingArgumentObligation(missing.ObligationId, ObligationStatus.Open, request.Id, call.Id, add.ParameterId, null, null)); obligationsCreated.Add(new(missing.ObligationId, call.Id, add.ParameterId)); }
                    else return Reject(request.BaseSnapshotId, "InvalidCallPlan", "The call plan is invalid.");
                }
                parametersAdded.Add(new(add.FunctionId, add.ParameterId, add.DisplayName, add.TypeId));
                continue;
            }

            if (operation is ResolveMissingArgument resolve)
            {
                var obligationIndex = obligations.FindIndex(obligation => obligation.Id == resolve.ObligationId);
                if (obligationIndex < 0 || obligations[obligationIndex].Status != ObligationStatus.Open) return Reject(request.BaseSnapshotId, "ObligationNotOpen", "The missing-argument obligation is not open.");
                var obligation = obligations[obligationIndex];
                var callerIndex = functions.FindIndex(function => function.Calls.Any(call => call.Id == obligation.CallId));
                if (callerIndex < 0) return Reject(request.BaseSnapshotId, "ObligationTargetMismatch", "The obligated call no longer exists.");
                var caller = functions[callerIndex]; var call = caller.Calls.Single(item => item.Id == obligation.CallId);
                var callee = functions.SingleOrDefault(function => function.Id == call.CalleeFunctionId);
                var target = callee?.Parameters.SingleOrDefault(parameter => parameter.Id == obligation.ParameterId);
                var source = caller.Parameters.SingleOrDefault(parameter => parameter.Id == resolve.SourceParameterId);
                if (target is null || source is null || target.TypeId != source.TypeId || call.Arguments.Any(argument => argument.TargetParameterId == target.Id)) return Reject(request.BaseSnapshotId, "InvalidArgumentSource", "The resolution source does not satisfy the obligated parameter.");
                ReplaceCall(functions, callerIndex, call, call.Arguments.Append(new ArgumentBinding(target.Id, source.Id)));
                obligations[obligationIndex] = obligation with { Status = ObligationStatus.Resolved, ResolutionTransactionId = request.Id, ResolutionSourceParameterId = source.Id };
                argumentsAttached.Add(new(call.Id, target.Id, source.Id));
                obligationsResolved.Add(new(obligation.Id, call.Id, target.Id, source.Id));
                continue;
            }

            return Reject(request.BaseSnapshotId, "UnsupportedOperation", "The operation is not supported by this transaction engine.");
        }

        if (request.Operations.Count == 0) return Reject(request.BaseSnapshotId, "NoOperations", "A transaction must contain at least one operation.");
        var proposedId = new StableId(IdDomain.Snapshot, request.Id.Value);
        if (store.Contains(proposedId)) return Reject(request.BaseSnapshotId, "SnapshotIdCollision", "The proposed snapshot ID already exists.");
        var proposed = new Snapshot(proposedId, baseSnapshot.Id, types, functions, baseSnapshot.Effects, baseSnapshot.Capabilities, obligations);
        var issues = SnapshotValidator.Validate(proposed);
        if (issues.Count > 0) return Reject(request.BaseSnapshotId, "InvalidProposedSnapshot", issues[0].Message);
        var netRenames = NetRenames(baseSnapshot, proposed);
        if (netRenames.Count == 0 && parametersAdded.Count == 0 && argumentsAttached.Count == 0 && obligationsCreated.Count == 0 && obligationsResolved.Count == 0) return Reject(request.BaseSnapshotId, "NoChange", "The transaction has no net semantic change.");
        return new SimulationProposed(new(request.Id, request.BaseSnapshotId, proposed, new(
            Snapshot.Freeze(netRenames),
            Snapshot.Freeze(parametersAdded.OrderBy(change => change.FunctionId.ToString(), StringComparer.Ordinal).ThenBy(change => change.ParameterId.ToString(), StringComparer.Ordinal)),
            Snapshot.Freeze(argumentsAttached.OrderBy(change => change.CallId.ToString(), StringComparer.Ordinal).ThenBy(change => change.TargetParameterId.ToString(), StringComparer.Ordinal).ThenBy(change => change.SourceParameterId.ToString(), StringComparer.Ordinal)),
            Snapshot.Freeze(obligationsCreated.OrderBy(change => change.ObligationId.ToString(), StringComparer.Ordinal)),
            Snapshot.Freeze(obligationsResolved.OrderBy(change => change.ObligationId.ToString(), StringComparer.Ordinal)))));
    }

    public CommitResult Commit(TransactionSimulation simulation, TransactionPolicy policy)
    {
        if (simulation.HasBlockingObligations && !policy.AllowBlockingObligations) return new CommitRejected(simulation.BaseSnapshotId, [new("BlockingObligations", "The proposed snapshot has blocking obligations.")]);
        var result = store.TryPublish(simulation.BaseSnapshotId, simulation.ProposedSnapshot);
        if (result == SnapshotPublishResult.StaleBase)
        {
            return new CommitRejected(simulation.BaseSnapshotId, [new("StaleBaseSnapshot", "The transaction base snapshot is not current.")]);
        }
        if (result == SnapshotPublishResult.SnapshotIdCollision) return new CommitRejected(simulation.BaseSnapshotId, [new("SnapshotIdCollision", "The proposed snapshot ID already exists.")]);
        if (result == SnapshotPublishResult.InvalidParent) return new CommitRejected(simulation.BaseSnapshotId, [new("InvalidParentSnapshot", "The proposed snapshot does not reference its base snapshot.")]);
        return new CommitSucceeded(simulation.ProposedSnapshot, simulation.Diff);
    }

    public RejectionSucceeded Reject(TransactionSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        return new RejectionSucceeded(simulation.TransactionId, CurrentSnapshot.Id);
    }

    private static SimulationRejected Reject(StableId baseSnapshotId, string code, string message) => new(baseSnapshotId, [new(code, message)]);

    private static void ReplaceCall(IList<SemanticFunction> functions, int callerIndex, Call call, IEnumerable<ArgumentBinding> arguments)
    {
        var caller = functions[callerIndex];
        functions[callerIndex] = new SemanticFunction(caller.Id, caller.DisplayName, caller.Parameters, caller.ResultTypeId, caller.DeclaredEffectIds, caller.Calls.Select(existing => existing.Id == call.Id ? new Call(existing.Id, existing.CalleeFunctionId, arguments) : existing));
    }

    private static IReadOnlyList<SymbolRenamed> NetRenames(Snapshot baseSnapshot, Snapshot proposed)
    {
        var baseNames = baseSnapshot.Functions.Select(function => (function.Id, function.DisplayName)).Concat(baseSnapshot.Types.Select(type => (type.Id, type.DisplayName))).ToDictionary(item => item.Id, item => item.DisplayName);
        return proposed.Functions.Select(function => (function.Id, function.DisplayName)).Concat(proposed.Types.Select(type => (type.Id, type.DisplayName))).Where(item => baseNames.TryGetValue(item.Id, out var previous) && previous != item.DisplayName).Select(item => new SymbolRenamed(item.Id, baseNames[item.Id], item.DisplayName)).OrderBy(change => change.TargetId.ToString(), StringComparer.Ordinal).ToArray();
    }
}
