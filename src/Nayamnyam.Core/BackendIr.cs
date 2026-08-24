using System.Text;
using System.Text.Json;

namespace Nayamnyam.Core;

public abstract record TypedExpression(StableId ResultTypeId);
public sealed record TypedStringLiteral(string Value, StableId TypeId) : TypedExpression(TypeId);
public sealed record TypedBoolLiteral(bool Value, StableId TypeId) : TypedExpression(TypeId);
public sealed record TypedConditional(TypedExpression Condition, TypedExpression WhenTrue, TypedExpression WhenFalse, StableId TypeId) : TypedExpression(TypeId);
public sealed record TypedParameterReference(StableId ParameterId, StableId TypeId) : TypedExpression(TypeId);
public sealed record TypedDirectCall(StableId CallId, StableId FunctionId, IReadOnlyList<(StableId ParameterId, TypedExpression Value)> Arguments, StableId TypeId) : TypedExpression(TypeId);
public sealed record TypedSemanticFunction(StableId FunctionId, IReadOnlyList<StableId> ParameterIds, StableId ResultTypeId, TypedExpression Body, IReadOnlyDictionary<StableId, StableId>? ParameterTypes = null);
public sealed record TypedSemanticProgram(IReadOnlyList<TypedSemanticFunction> Functions, StableId StringTypeId, StableId? BoolTypeId = null);

public abstract record ExecutionExpression;
public sealed record ExecutionStringConstant(string Value) : ExecutionExpression;
public sealed record ExecutionBoolConstant(bool Value) : ExecutionExpression;
public sealed record ExecutionConditional(ExecutionExpression Condition, ExecutionExpression WhenTrue, ExecutionExpression WhenFalse) : ExecutionExpression;
public sealed record ExecutionLoadArgument(int Index) : ExecutionExpression;
public sealed record ExecutionInvoke(string Symbol, IReadOnlyList<ExecutionExpression> Arguments) : ExecutionExpression;
public sealed record ExecutionFunction(string Symbol, IReadOnlyList<string> Parameters, ExecutionExpression Body, StableId ProvenanceFunctionId);
public sealed record ExecutionModule(IReadOnlyList<ExecutionFunction> Functions);

public static class ExecutionLowerer
{
    public static ExecutionModule Lower(TypedSemanticProgram program)
    {
        var functions = program.Functions.ToDictionary(function => function.FunctionId);
        var symbols = functions.Keys.ToDictionary(id => id, Symbol);
        return new ExecutionModule(program.Functions.OrderBy(function => function.FunctionId.ToString(), StringComparer.Ordinal).Select(function => new ExecutionFunction(Symbol(function.FunctionId), function.ParameterIds.Select((id, index) => $"{TypeName(function.ParameterTypes?.GetValueOrDefault(id), program.StringTypeId, program.BoolTypeId)} p{index}").ToArray(), Lower(function.Body, function.ParameterIds, symbols, functions), function.FunctionId)).ToArray());
    }

    private static ExecutionExpression Lower(TypedExpression expression, IReadOnlyList<StableId> parameters, IReadOnlyDictionary<StableId, string> symbols, IReadOnlyDictionary<StableId, TypedSemanticFunction> functions) => expression switch
    {
        TypedStringLiteral literal => new ExecutionStringConstant(literal.Value),
        TypedBoolLiteral literal => new ExecutionBoolConstant(literal.Value),
        TypedConditional conditional => new ExecutionConditional(Lower(conditional.Condition, parameters, symbols, functions), Lower(conditional.WhenTrue, parameters, symbols, functions), Lower(conditional.WhenFalse, parameters, symbols, functions)),
        TypedParameterReference parameter => new ExecutionLoadArgument(Index(parameters, parameter.ParameterId)),
        TypedDirectCall call => new ExecutionInvoke(symbols[call.FunctionId], call.Arguments.OrderBy(argument => Index(functions[call.FunctionId].ParameterIds, argument.ParameterId)).Select(argument => Lower(argument.Value, parameters, symbols, functions)).ToArray()),
        _ => throw new NotSupportedException($"Unsupported typed expression '{expression.GetType().Name}'."),
    };

    private static int Index(IReadOnlyList<StableId> parameters, StableId parameterId) { for (var index = 0; index < parameters.Count; index++) if (parameters[index] == parameterId) return index; throw new InvalidOperationException("Parameter reference is not owned by its function."); }
    private static string Symbol(StableId id) => $"F_{id.Value:N}";
    private static string TypeName(StableId? typeId, StableId stringTypeId, StableId? boolTypeId) => typeId is { } resolvedTypeId && boolTypeId is { } boolId && resolvedTypeId == boolId ? "bool" : typeId is null || typeId == stringTypeId ? "string" : throw new NotSupportedException("Unsupported execution type.");
}

public static class CSharpEmitter
{
    public static string Emit(ExecutionModule module)
    {
        var source = new StringBuilder("#nullable enable\n\nnamespace Nayamnyam.Generated;\n\npublic static class Program\n{\n");
        foreach (var function in module.Functions) { source.Append("    public static string ").Append(function.Symbol).Append('(').Append(string.Join(", ", function.Parameters)).Append(")\n    {\n        return ").Append(Expression(function.Body)).Append(";\n    }\n\n"); }
        return source.Append("}\n").ToString();
    }

    private static string Expression(ExecutionExpression expression) => expression switch
    {
        ExecutionStringConstant constant => JsonSerializer.Serialize(constant.Value),
        ExecutionBoolConstant constant => constant.Value ? "true" : "false",
        ExecutionConditional conditional => $"({Expression(conditional.Condition)} ? {Expression(conditional.WhenTrue)} : {Expression(conditional.WhenFalse)})",
        ExecutionLoadArgument argument => $"p{argument.Index}",
        ExecutionInvoke call => $"{call.Symbol}({string.Join(", ", call.Arguments.Select(Expression))})",
        _ => throw new NotSupportedException(),
    };
}
