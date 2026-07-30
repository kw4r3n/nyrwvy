using System.Diagnostics;

using FunctionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.FunctionTag>;
using ParameterId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ParameterTag>;

namespace Nayamnyam.Spike;

public static class Validation
{
    public static IReadOnlyList<Diagnostic> SnapshotDiagnostics(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var diagnostics = new List<Diagnostic>();

        if (snapshot.SchemaVersion != 1)
        {
            diagnostics.Add(new Diagnostic(
                "unsupported_schema_version",
                $"Schema version {snapshot.SchemaVersion} is not supported."));
        }

        AddDuplicateDiagnostics(
            snapshot.Types.Select(item => item.Id.Value),
            "duplicate_type_id",
            diagnostics);
        AddDuplicateDiagnostics(
            snapshot.Functions.Select(item => item.Id.Value),
            "duplicate_function_id",
            diagnostics);
        AddDuplicateDiagnostics(
            snapshot.Calls.Select(item => item.Id.Value),
            "duplicate_call_id",
            diagnostics);
        AddDuplicateDiagnostics(
            snapshot.Obligations.Select(item => item.Id.Value),
            "duplicate_obligation_id",
            diagnostics);

        var types = snapshot.Types
            .GroupBy(item => item.Id)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var functions = snapshot.Functions
            .GroupBy(item => item.Id)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var parameters = new Dictionary<ParameterId, FunctionId>();

        foreach (var function in snapshot.Functions)
        {
            var localParameterIds = new HashSet<ParameterId>();
            foreach (var parameter in function.Parameters)
            {
                if (!localParameterIds.Add(parameter.Id)
                    || !parameters.TryAdd(parameter.Id, function.Id))
                {
                    diagnostics.Add(new Diagnostic(
                        "duplicate_parameter_id",
                        $"Parameter ID '{parameter.Id}' is not unique."));
                }

                if (!types.ContainsKey(parameter.TypeId))
                {
                    diagnostics.Add(new Diagnostic(
                        "unknown_parameter_type",
                        $"Parameter '{parameter.Id}' references unknown type "
                            + $"'{parameter.TypeId}'."));
                }
            }
        }

        foreach (var call in snapshot.Calls)
        {
            if (!functions.TryGetValue(call.CalleeId, out var callee))
            {
                diagnostics.Add(new Diagnostic(
                    "unknown_callee",
                    $"Call '{call.Id}' references unknown function '{call.CalleeId}'."));
                continue;
            }

            var declaredIds = callee.Parameters.Select(item => item.Id).ToHashSet();
            var boundIds = new HashSet<ParameterId>();
            foreach (var argument in call.Arguments)
            {
                if (!boundIds.Add(argument.ParameterId))
                {
                    diagnostics.Add(new Diagnostic(
                        "duplicate_argument_binding",
                        $"Call '{call.Id}' binds parameter "
                            + $"'{argument.ParameterId}' more than once."));
                }

                if (!declaredIds.Contains(argument.ParameterId))
                {
                    diagnostics.Add(new Diagnostic(
                        "invalid_argument_parameter_ownership",
                        $"Call '{call.Id}' binds parameter "
                            + $"'{argument.ParameterId}' not owned by callee "
                            + $"'{call.CalleeId}'."));
                }
            }
        }

        var calls = snapshot.Calls.Select(item => item.Id).ToHashSet();
        foreach (var obligation in snapshot.Obligations)
        {
            if (obligation.Status is not ObligationStatus.Open)
            {
                throw new UnreachableException(
                    "The closed obligation-status hierarchy was not exhaustively handled.");
            }

            if (obligation.Kind is ObligationKind.MissingArgument missingArgument)
            {
                if (!calls.Contains(missingArgument.CallId))
                {
                    diagnostics.Add(new Diagnostic(
                        "unknown_obligation_call",
                        $"Obligation '{obligation.Id}' references unknown call "
                            + $"'{missingArgument.CallId}'."));
                }

                if (!parameters.ContainsKey(missingArgument.ParameterId))
                {
                    diagnostics.Add(new Diagnostic(
                        "unknown_obligation_parameter",
                        $"Obligation '{obligation.Id}' references unknown parameter "
                            + $"'{missingArgument.ParameterId}'."));
                }

                continue;
            }

            throw new UnreachableException(
                "The closed obligation-kind hierarchy was not exhaustively handled.");
        }

        return Array.AsReadOnly(diagnostics.ToArray());
    }

    private static void AddDuplicateDiagnostics(
        IEnumerable<string> values,
        string code,
        ICollection<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                diagnostics.Add(new Diagnostic(
                    code,
                    $"Stable ID '{value}' is not unique."));
            }
        }
    }
}
