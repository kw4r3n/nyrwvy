namespace Nayamnyam.Core;

public static class GraphToTypedLowerer
{
    public static TypedSemanticProgram Lower(Snapshot snapshot, StableId stringTypeId)
    {
        var functions = snapshot.Functions.ToDictionary(function => function.Id);
        return new TypedSemanticProgram(snapshot.Functions.Select(function => new TypedSemanticFunction(function.Id, function.Parameters.Select(parameter => parameter.Id).ToArray(), function.ResultTypeId, LowerBody(function, function.Body ?? throw new InvalidOperationException($"Function '{function.Id}' has no body."), functions, stringTypeId, stringTypeId))).ToArray(), stringTypeId);
    }

    public static TypedSemanticProgram Lower(Snapshot snapshot)
    {
        var builtins = snapshot.BuiltinTypes ?? throw new InvalidOperationException("Executable lowering requires builtin type IDs.");
        return Lower(snapshot, builtins.StringTypeId, builtins.BoolTypeId);
    }

    private static TypedSemanticProgram Lower(Snapshot snapshot, StableId stringTypeId, StableId boolTypeId)
    {
        var functions = snapshot.Functions.ToDictionary(function => function.Id);
        return new TypedSemanticProgram(snapshot.Functions.Select(function => new TypedSemanticFunction(function.Id, function.Parameters.Select(parameter => parameter.Id).ToArray(), function.ResultTypeId, LowerBody(function, function.Body ?? throw new InvalidOperationException($"Function '{function.Id}' has no body."), functions, stringTypeId, boolTypeId), function.Parameters.ToDictionary(parameter => parameter.Id, parameter => parameter.TypeId))).ToArray(), stringTypeId, boolTypeId);
    }

    private static TypedExpression LowerBody(SemanticFunction owner, FunctionBody body, IReadOnlyDictionary<StableId, SemanticFunction> functions, StableId stringTypeId, StableId boolTypeId) => body switch
    {
        StringLiteralBody literal => new TypedStringLiteral(literal.Value, stringTypeId),
        BoolLiteralBody literal => new TypedBoolLiteral(literal.Value, boolTypeId),
        ParameterReferenceBody parameter => new TypedParameterReference(Owned(owner, parameter.ParameterId).Id, Owned(owner, parameter.ParameterId).TypeId),
        DirectCallBody call => new TypedDirectCall(call.Id, functions[call.CalleeFunctionId].Id, call.Arguments.Select(argument => (argument.TargetParameterId, LowerBody(owner, argument.Value, functions, stringTypeId, boolTypeId))).ToArray(), functions[call.CalleeFunctionId].ResultTypeId),
        ConditionalBody conditional => new TypedConditional(LowerBody(owner, conditional.Condition, functions, stringTypeId, boolTypeId), LowerBody(owner, conditional.WhenTrue, functions, stringTypeId, boolTypeId), LowerBody(owner, conditional.WhenFalse, functions, stringTypeId, boolTypeId), stringTypeId),
        _ => throw new NotSupportedException(),
    };

    private static Parameter Owned(SemanticFunction function, StableId parameterId) => function.Parameters.Single(parameter => parameter.Id == parameterId);
}
