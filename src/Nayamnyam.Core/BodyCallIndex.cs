namespace Nayamnyam.Core;

public sealed record BodyCall(StableId CallId, StableId CallerFunctionId, StableId CalleeFunctionId);

public static class BodyCallIndex
{
    public static IReadOnlyList<BodyCall> Build(Snapshot snapshot) => snapshot.Functions.SelectMany(function => Walk(function.Id, function.Body)).OrderBy(call => call.CallId.ToString(), StringComparer.Ordinal).ToArray();

    private static IEnumerable<BodyCall> Walk(StableId callerId, FunctionBody? body)
    {
        switch (body)
        {
            case DirectCallBody call:
                yield return new BodyCall(call.Id, callerId, call.CalleeFunctionId);
                foreach (var argument in call.Arguments) foreach (var nested in Walk(callerId, argument.Value)) yield return nested;
                break;
            case ConditionalBody conditional:
                foreach (var nested in Walk(callerId, conditional.Condition)) yield return nested;
                foreach (var nested in Walk(callerId, conditional.WhenTrue)) yield return nested;
                foreach (var nested in Walk(callerId, conditional.WhenFalse)) yield return nested;
                break;
        }
    }
}
