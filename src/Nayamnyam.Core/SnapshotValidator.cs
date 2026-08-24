namespace Nayamnyam.Core;

public sealed record ValidationIssue(string Code, string Message);

public static class SnapshotValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(Snapshot snapshot)
    {
        var issues = new List<ValidationIssue>();
        var ids = new HashSet<StableId>();

        Add(snapshot.Id, IdDomain.Snapshot, "snapshot", ids, issues);
        if (snapshot.ParentId is { } parentId)
        {
            Add(parentId, IdDomain.Snapshot, "parent snapshot", ids, issues);
        }

        foreach (var type in snapshot.Types)
        {
            Add(type.Id, IdDomain.Type, "type", ids, issues);
            if (!Enum.IsDefined(type.Kind) ||
                (type.Kind == TypeKind.Builtin && (type.Fields.Count != 0 || type.Variants.Count != 0)) ||
                (type.Kind == TypeKind.Product && type.Variants.Count != 0) ||
                (type.Kind == TypeKind.Union && type.Fields.Count != 0))
            {
                issues.Add(new("InvalidTypeShape", $"Type '{type.Id}' has an invalid {type.Kind} shape."));
            }
            foreach (var field in type.Fields)
            {
                Add(field.Id, IdDomain.Field, "field", ids, issues);
            }

            foreach (var variant in type.Variants)
            {
                Add(variant.Id, IdDomain.Variant, "variant", ids, issues);
            }
        }

        foreach (var function in snapshot.Functions)
        {
            Add(function.Id, IdDomain.Function, "function", ids, issues);
            foreach (var parameter in function.Parameters)
            {
                Add(parameter.Id, IdDomain.Parameter, "parameter", ids, issues);
            }
            foreach (var call in function.Calls)
            {
                Add(call.Id, IdDomain.GraphNode, "call", ids, issues);
            }
        }

        foreach (var effect in snapshot.Effects) Add(effect.Id, IdDomain.Effect, "effect", ids, issues);
        foreach (var capability in snapshot.Capabilities) Add(capability.Id, IdDomain.Capability, "capability", ids, issues);
        foreach (var obligation in snapshot.Obligations)
        {
            Add(obligation.Id, IdDomain.Obligation, "obligation", ids, issues);
            CheckDomain(obligation.OriginatingTransactionId, IdDomain.Transaction, "originating transaction", issues);
            if (obligation.ResolutionTransactionId is { } resolutionTransactionId) CheckDomain(resolutionTransactionId, IdDomain.Transaction, "resolution transaction", issues);
        }

        var typeIds = snapshot.Types.Select(type => type.Id).ToHashSet();
        var effectIds = snapshot.Effects.Select(effect => effect.Id).ToHashSet();
        foreach (var type in snapshot.Types)
        {
            foreach (var field in type.Fields.Where(field => !typeIds.Contains(field.TypeId)))
            {
                issues.Add(new("UnknownFieldType", $"Field '{field.Id}' references unknown type '{field.TypeId}'."));
            }
        }

        foreach (var function in snapshot.Functions)
        {
            if (!typeIds.Contains(function.ResultTypeId))
            {
                issues.Add(new("UnknownResultType", $"Function '{function.Id}' references unknown result type '{function.ResultTypeId}'."));
            }

            foreach (var parameter in function.Parameters.Where(parameter => !typeIds.Contains(parameter.TypeId)))
            {
                issues.Add(new("UnknownParameterType", $"Parameter '{parameter.Id}' references unknown type '{parameter.TypeId}'."));
            }

            foreach (var effectId in function.DeclaredEffectIds.Where(effectId => !effectIds.Contains(effectId)))
            {
                issues.Add(new("UnknownFunctionEffect", $"Function '{function.Id}' references unknown effect '{effectId}'."));
            }
        }

        foreach (var capability in snapshot.Capabilities.Where(capability => !effectIds.Contains(capability.EffectId)))
        {
            issues.Add(new("UnknownCapabilityEffect", $"Capability '{capability.Id}' references unknown effect '{capability.EffectId}'."));
        }

        var functionsById = snapshot.Functions.ToDictionary(function => function.Id);
        foreach (var caller in snapshot.Functions)
        {
            foreach (var call in caller.Calls)
            {
                if (!functionsById.TryGetValue(call.CalleeFunctionId, out var callee))
                {
                    issues.Add(new("UnknownCallee", $"Call '{call.Id}' references unknown function '{call.CalleeFunctionId}'."));
                    continue;
                }

                foreach (var argument in call.Arguments)
                {
                    var target = callee.Parameters.SingleOrDefault(parameter => parameter.Id == argument.TargetParameterId);
                    var source = caller.Parameters.SingleOrDefault(parameter => parameter.Id == argument.SourceParameterId);
                    if (target is null || source is null || target.TypeId != source.TypeId)
                    {
                        issues.Add(new("InvalidArgumentBinding", $"Call '{call.Id}' has an invalid argument binding."));
                    }
                }

                if (call.Arguments.GroupBy(argument => argument.TargetParameterId).Any(group => group.Count() > 1))
                {
                    issues.Add(new("DuplicateArgumentTarget", $"Call '{call.Id}' binds a parameter more than once."));
                }

                foreach (var parameter in callee.Parameters.Where(parameter => !call.Arguments.Any(argument => argument.TargetParameterId == parameter.Id)))
                {
                    if (!snapshot.Obligations.Any(obligation => obligation.Status == ObligationStatus.Open && obligation.CallId == call.Id && obligation.ParameterId == parameter.Id))
                    {
                        issues.Add(new("MissingArgumentObligation", $"Call '{call.Id}' has no argument or open obligation for parameter '{parameter.Id}'."));
                    }
                }
            }
        }

        foreach (var obligation in snapshot.Obligations)
        {
            if (!Enum.IsDefined(obligation.Status)) issues.Add(new("InvalidObligationStatus", $"Obligation '{obligation.Id}' has an invalid status."));
            var owner = snapshot.Functions.FirstOrDefault(function => function.Calls.Any(call => call.Id == obligation.CallId));
            var obligatedCall = owner?.Calls.Single(call => call.Id == obligation.CallId);
            var obligatedCallee = obligatedCall is null ? null : functionsById.GetValueOrDefault(obligatedCall.CalleeFunctionId);
            var parameter = obligatedCallee?.Parameters.SingleOrDefault(candidate => candidate.Id == obligation.ParameterId);
            if (obligatedCall is null || parameter is null)
            {
                issues.Add(new("InvalidObligationTarget", $"Obligation '{obligation.Id}' does not target an existing call parameter."));
                continue;
            }

            var binding = obligatedCall.Arguments.SingleOrDefault(argument => argument.TargetParameterId == obligation.ParameterId);
            if (obligation.Status == ObligationStatus.Open && binding is not null) issues.Add(new("SatisfiedOpenObligation", $"Open obligation '{obligation.Id}' already has an argument."));
            if (obligation.Status == ObligationStatus.Resolved && (binding is null || obligation.ResolutionTransactionId is null || obligation.ResolutionSourceParameterId != binding.SourceParameterId)) issues.Add(new("InvalidResolution", $"Resolved obligation '{obligation.Id}' lacks matching provenance and binding."));
        }

        return issues.OrderBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Message, StringComparer.Ordinal).ToArray();
    }

    private static void Add(StableId id, IdDomain expected, string kind, ISet<StableId> ids, ICollection<ValidationIssue> issues)
    {
        CheckDomain(id, expected, kind, issues);

        if (!ids.Add(id))
        {
            issues.Add(new("DuplicateId", $"Stable ID '{id}' occurs more than once."));
        }
    }

    private static void CheckDomain(StableId id, IdDomain expected, string kind, ICollection<ValidationIssue> issues)
    {
        if (id.Domain != expected) issues.Add(new("WrongIdDomain", $"{kind} ID '{id}' must use the {expected} domain."));
    }
}
