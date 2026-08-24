using Nayamnyam.Core;

namespace Nayamnyam.Core.Tests;

public sealed class BackendIrTests
{
    [Fact]
    public void LowersAndEmitsStableStringCall()
    {
        var stringType = Id(IdDomain.Type, 1); var identity = Id(IdDomain.Function, 1); var run = Id(IdDomain.Function, 2); var parameter = Id(IdDomain.Parameter, 1);
        var typed = new TypedSemanticProgram([new TypedSemanticFunction(identity, [parameter], stringType, new TypedParameterReference(parameter, stringType)), new TypedSemanticFunction(run, [], stringType, new TypedDirectCall(Id(IdDomain.GraphNode, 1), identity, [(parameter, new TypedStringLiteral("nyam", stringType))], stringType))], stringType);
        var source = CSharpEmitter.Emit(ExecutionLowerer.Lower(typed));
        Assert.Contains("return p0;", source, StringComparison.Ordinal);
        Assert.Contains("(\"nyam\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LowersExpressionBodiesFromSemanticGraph()
    {
        var stringType = Id(IdDomain.Type, 1); var identity = Id(IdDomain.Function, 1); var run = Id(IdDomain.Function, 2); var parameter = Id(IdDomain.Parameter, 1);
        var graph = new Snapshot(Id(IdDomain.Snapshot, 1), null, [new SemanticType(stringType, "String", TypeKind.Builtin, [], [])], [new SemanticFunction(identity, "identity", [new Parameter(parameter, "value", stringType)], stringType, [], [], new ParameterReferenceBody(parameter)), new SemanticFunction(run, "run", [], stringType, [], [], new DirectCallBody(Id(IdDomain.GraphNode, 1), identity, [new BodyArgument(parameter, new StringLiteralBody("nyam"))]))], [], [], []);
        var source = CSharpEmitter.Emit(ExecutionLowerer.Lower(GraphToTypedLowerer.Lower(graph, stringType)));
        Assert.Contains("F_00000000000000000000000000000001(\"nyam\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsConditionalExpression()
    {
        var stringType = Id(IdDomain.Type, 1); var boolType = Id(IdDomain.Type, 2); var function = Id(IdDomain.Function, 1);
        var typed = new TypedSemanticProgram([new TypedSemanticFunction(function, [], stringType, new TypedConditional(new TypedBoolLiteral(false, boolType), new TypedStringLiteral("yes", stringType), new TypedStringLiteral("no", stringType), stringType))], stringType);
        var source = CSharpEmitter.Emit(ExecutionLowerer.Lower(typed));
        Assert.Contains("return (false ? \"yes\" : \"no\");", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LowersBoolParameterConditionalFromSnapshotBuiltins()
    {
        var stringType = Id(IdDomain.Type, 1); var boolType = Id(IdDomain.Type, 2); var function = Id(IdDomain.Function, 1); var flag = Id(IdDomain.Parameter, 1);
        var snapshot = new Snapshot(Id(IdDomain.Snapshot, 1), null, [new SemanticType(stringType, "String", TypeKind.Builtin, [], []), new SemanticType(boolType, "Bool", TypeKind.Builtin, [], [])], [new SemanticFunction(function, "choose", [new Parameter(flag, "flag", boolType)], stringType, [], [], new ConditionalBody(new ParameterReferenceBody(flag), new StringLiteralBody("yes"), new StringLiteralBody("no")))], [], [], [], new BuiltinTypes(stringType, boolType));
        var source = CSharpEmitter.Emit(ExecutionLowerer.Lower(GraphToTypedLowerer.Lower(snapshot)));
        Assert.Contains("(bool p0)", source, StringComparison.Ordinal);
        Assert.Contains("return (p0 ? \"yes\" : \"no\");", source, StringComparison.Ordinal);
    }
    private static StableId Id(IdDomain domain, int value) => new(domain, new Guid($"00000000-0000-0000-0000-{value:D12}"));
}
