using Nayamnyam.Core;

namespace Nayamnyam.Core.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void SerializeIsCanonicalAndRoundTrips()
    {
        var snapshot = CreateSnapshot(typesInReverseOrder: true);

        var json = SnapshotSerializer.Serialize(snapshot);
        var reserialized = SnapshotSerializer.Serialize(SnapshotSerializer.Deserialize(json));

        Assert.Equal(json, reserialized);
        Assert.True(json.IndexOf("String", StringComparison.Ordinal) < json.IndexOf("Bool", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"declaredEffectIds\"", StringComparison.Ordinal) < json.IndexOf("\"displayName\":\"validate_user\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsDuplicateStableIds()
    {
        var snapshot = CreateSnapshot(typesInReverseOrder: false);
        var duplicate = new SemanticType(snapshot.Types[1].Id, snapshot.Types[0].DisplayName, snapshot.Types[0].Kind, snapshot.Types[0].Fields, snapshot.Types[0].Variants);
        snapshot = new Snapshot(snapshot.Id, snapshot.ParentId, [duplicate, snapshot.Types[1]], snapshot.Functions, snapshot.Effects, snapshot.Capabilities, snapshot.Obligations);

        var issue = Assert.Single(SnapshotValidator.Validate(snapshot), issue => issue.Code == "DuplicateId");

        Assert.Contains(snapshot.Types[1].Id.ToString(), issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotDefensivelyCopiesCallerCollections()
    {
        var type = new SemanticType(Id(IdDomain.Type, 1), "String", TypeKind.Builtin, [], []);
        var types = new List<SemanticType> { type };
        var snapshot = new Snapshot(Id(IdDomain.Snapshot, 1), null, types, [], [], [], []);

        types.Clear();

        Assert.Single(snapshot.Types);
    }

    [Fact]
    public void ValidatorRejectsInvalidTypeShape()
    {
        var variant = new SemanticVariant(Id(IdDomain.Variant, 1), "Some");
        var type = new SemanticType(Id(IdDomain.Type, 1), "Option", TypeKind.Product, [], [variant]);
        var snapshot = new Snapshot(Id(IdDomain.Snapshot, 1), null, [type], [], [], [], []);

        Assert.Contains(SnapshotValidator.Validate(snapshot), issue => issue.Code == "InvalidTypeShape");
    }

    [Fact]
    public void StableIdRejectsNonSchemaDomainPrefix()
    {
        Assert.Throws<FormatException>(() => StableId.Parse("1:00000000-0000-0000-0000-000000000001"));
    }

    [Fact]
    public void DeserializeRejectsUnsupportedFunctionPayload()
    {
        var json = SnapshotSerializer.Serialize(CreateSnapshot(typesInReverseOrder: false));
        json = json.Replace("\"displayName\":\"validate_user\"", "\"body\":{},\"displayName\":\"validate_user\"", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => SnapshotSerializer.Deserialize(json));
    }

    [Fact]
    public void SerializeRoundTripsFunctionBody()
    {
        var snapshot = CreateSnapshot(typesInReverseOrder: false);
        var function = snapshot.Functions[0];
        var body = new StringLiteralBody("nyam\n\"quoted\"");
        snapshot = new Snapshot(snapshot.Id, snapshot.ParentId, snapshot.Types, [new SemanticFunction(function.Id, function.DisplayName, function.Parameters, function.ResultTypeId, function.DeclaredEffectIds, function.Calls, body)], snapshot.Effects, snapshot.Capabilities, snapshot.Obligations);

        var restored = SnapshotSerializer.Deserialize(SnapshotSerializer.Serialize(snapshot));

        Assert.Equal(body, Assert.IsType<StringLiteralBody>(restored.Functions[0].Body));
    }

    [Fact]
    public void SerializeRoundTripsConditionalBody()
    {
        var snapshot = CreateSnapshot(typesInReverseOrder: false);
        var function = snapshot.Functions[0];
        var body = new ConditionalBody(new BoolLiteralBody(true), new StringLiteralBody("yes"), new StringLiteralBody("no"));
        snapshot = new Snapshot(snapshot.Id, snapshot.ParentId, snapshot.Types, [new SemanticFunction(function.Id, function.DisplayName, function.Parameters, function.ResultTypeId, function.DeclaredEffectIds, function.Calls, body)], snapshot.Effects, snapshot.Capabilities, snapshot.Obligations);

        var restored = SnapshotSerializer.Deserialize(SnapshotSerializer.Serialize(snapshot));

        Assert.Equal(body, restored.Functions[0].Body);
    }

    private static Snapshot CreateSnapshot(bool typesInReverseOrder)
    {
        var stringType = new SemanticType(Id(IdDomain.Type, 1), "String", TypeKind.Builtin, [], []);
        var boolType = new SemanticType(Id(IdDomain.Type, 2), "Bool", TypeKind.Builtin, [], []);
        var effect = new Effect(Id(IdDomain.Effect, 1), "ConsoleOutput");
        var function = new SemanticFunction(Id(IdDomain.Function, 1), "validate_user", [new Parameter(Id(IdDomain.Parameter, 1), "name", stringType.Id)], boolType.Id, [effect.Id]);
        return new Snapshot(Id(IdDomain.Snapshot, 1), null, typesInReverseOrder ? [stringType, boolType] : [boolType, stringType], [function], [effect], [], []);
    }

    private static StableId Id(IdDomain domain, int value) => new(domain, new Guid($"00000000-0000-0000-0000-{value:D12}"));
}
