using System.Text;
using System.Text.Json;

namespace Nayamnyam.Core;

public static class SnapshotSerializer
{
    public static string Serialize(Snapshot snapshot)
    {
        var issues = SnapshotValidator.Validate(snapshot);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException($"Cannot serialize an invalid snapshot: {issues[0].Code}.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (snapshot.BuiltinTypes is { } builtins) { writer.WritePropertyName("builtinTypes"); writer.WriteStartObject(); writer.WriteString("boolTypeId", builtins.BoolTypeId.ToString()); writer.WriteString("stringTypeId", builtins.StringTypeId.ToString()); writer.WriteEndObject(); }
            WriteCapabilities(writer, snapshot.Capabilities);
            WriteEffects(writer, snapshot.Effects);
            WriteFunctions(writer, snapshot.Functions);
            WriteObligations(writer, snapshot.Obligations);
            if (snapshot.ParentId is { } parentId) writer.WriteString("parentSnapshotId", parentId.ToString());
            writer.WriteNumber("schemaVersion", Snapshot.SchemaVersion);
            writer.WriteString("snapshotId", snapshot.Id.ToString());
            WriteTypes(writer, snapshot.Types);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static Snapshot Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        EnsureProperties(root, "builtinTypes", "capabilities", "effects", "functions", "obligations", "parentSnapshotId", "schemaVersion", "snapshotId", "types");
        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != Snapshot.SchemaVersion)
        {
            throw new NotSupportedException("The snapshot schema version is not supported.");
        }

        var snapshot = new Snapshot(
            ReadId(root, "snapshotId"),
            root.TryGetProperty("parentSnapshotId", out var parent) ? StableId.Parse(parent.GetString()!) : null,
            ReadTypes(root),
            ReadFunctions(root),
            ReadEffects(root),
            ReadCapabilities(root),
            ReadObligations(root),
            root.TryGetProperty("builtinTypes", out var builtins) ? new BuiltinTypes(ReadId(builtins, "stringTypeId"), ReadId(builtins, "boolTypeId")) : null);
        var issues = SnapshotValidator.Validate(snapshot);
        if (issues.Count > 0)
        {
            throw new InvalidDataException($"Invalid snapshot: {issues[0].Code}.");
        }

        return snapshot;
    }

    private static void WriteCapabilities(Utf8JsonWriter writer, IReadOnlyList<Capability> capabilities)
    {
        writer.WritePropertyName("capabilities"); writer.WriteStartArray();
        foreach (var capability in capabilities.OrderBy(capability => capability.Id.ToString(), StringComparer.Ordinal))
        {
            writer.WriteStartObject(); writer.WriteString("displayName", capability.DisplayName);
            writer.WriteString("effectId", capability.EffectId.ToString()); writer.WriteString("id", capability.Id.ToString()); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteEffects(Utf8JsonWriter writer, IReadOnlyList<Effect> effects)
    {
        writer.WritePropertyName("effects"); writer.WriteStartArray();
        foreach (var effect in effects.OrderBy(effect => effect.Id.ToString(), StringComparer.Ordinal))
        {
            writer.WriteStartObject(); writer.WriteString("displayName", effect.DisplayName); writer.WriteString("id", effect.Id.ToString()); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteFunctions(Utf8JsonWriter writer, IReadOnlyList<SemanticFunction> functions)
    {
        writer.WritePropertyName("functions"); writer.WriteStartArray();
        foreach (var function in functions.OrderBy(function => function.Id.ToString(), StringComparer.Ordinal))
        {
            writer.WriteStartObject(); if (function.Body is not null) { writer.WritePropertyName("body"); WriteBody(writer, function.Body); } writer.WritePropertyName("calls"); writer.WriteStartArray();
            foreach (var call in function.Calls.OrderBy(call => call.Id.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WritePropertyName("arguments"); writer.WriteStartArray();
                foreach (var argument in call.Arguments.OrderBy(argument => argument.TargetParameterId.ToString(), StringComparer.Ordinal)) { writer.WriteStartObject(); writer.WriteString("sourceParameterId", argument.SourceParameterId.ToString()); writer.WriteString("targetParameterId", argument.TargetParameterId.ToString()); writer.WriteEndObject(); }
                writer.WriteEndArray(); writer.WriteString("calleeFunctionId", call.CalleeFunctionId.ToString()); writer.WriteString("id", call.Id.ToString()); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WritePropertyName("declaredEffectIds"); WriteIds(writer, function.DeclaredEffectIds);
            writer.WriteString("displayName", function.DisplayName);
            writer.WriteString("id", function.Id.ToString()); writer.WritePropertyName("parameters"); writer.WriteStartArray();
            foreach (var parameter in function.Parameters.OrderBy(parameter => parameter.Id.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("displayName", parameter.DisplayName); writer.WriteString("id", parameter.Id.ToString()); writer.WriteString("typeId", parameter.TypeId.ToString()); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteString("resultTypeId", function.ResultTypeId.ToString()); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteObligations(Utf8JsonWriter writer, IReadOnlyList<MissingArgumentObligation> obligations)
    {
        writer.WritePropertyName("obligations"); writer.WriteStartArray();
        foreach (var obligation in obligations.OrderBy(obligation => obligation.Id.ToString(), StringComparer.Ordinal))
        {
            writer.WriteStartObject(); writer.WriteString("callId", obligation.CallId.ToString()); writer.WriteString("id", obligation.Id.ToString()); writer.WriteString("originatingTransactionId", obligation.OriginatingTransactionId.ToString()); writer.WriteString("parameterId", obligation.ParameterId.ToString()); if (obligation.ResolutionSourceParameterId is { } source) writer.WriteString("resolutionSourceParameterId", source.ToString()); if (obligation.ResolutionTransactionId is { } transaction) writer.WriteString("resolutionTransactionId", transaction.ToString()); writer.WriteString("status", obligation.Status.ToString().ToLowerInvariant()); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteTypes(Utf8JsonWriter writer, IReadOnlyList<SemanticType> types)
    {
        writer.WritePropertyName("types"); writer.WriteStartArray();
        foreach (var type in types.OrderBy(type => type.Id.ToString(), StringComparer.Ordinal))
        {
            writer.WriteStartObject(); writer.WriteString("displayName", type.DisplayName); writer.WritePropertyName("fields"); writer.WriteStartArray();
            foreach (var field in type.Fields.OrderBy(field => field.Id.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("displayName", field.DisplayName); writer.WriteString("id", field.Id.ToString()); writer.WriteString("typeId", field.TypeId.ToString()); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteString("id", type.Id.ToString()); writer.WriteString("kind", type.Kind.ToString().ToLowerInvariant()); writer.WritePropertyName("variants"); writer.WriteStartArray();
            foreach (var variant in type.Variants.OrderBy(variant => variant.Id.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("displayName", variant.DisplayName); writer.WriteString("id", variant.Id.ToString()); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteIds(Utf8JsonWriter writer, IReadOnlyList<StableId> ids)
    {
        writer.WriteStartArray(); foreach (var id in ids.OrderBy(id => id.ToString(), StringComparer.Ordinal)) writer.WriteStringValue(id.ToString()); writer.WriteEndArray();
    }

    private static StableId ReadId(JsonElement element, string property) => StableId.Parse(element.GetProperty(property).GetString()!);
    private static IReadOnlyList<StableId> ReadIds(JsonElement element) => element.EnumerateArray().Select(value => StableId.Parse(value.GetString()!)).ToArray();
    private static IReadOnlyList<Effect> ReadEffects(JsonElement root) => root.GetProperty("effects").EnumerateArray().Select(ReadEffect).ToArray();
    private static IReadOnlyList<Capability> ReadCapabilities(JsonElement root) => root.GetProperty("capabilities").EnumerateArray().Select(ReadCapability).ToArray();
    private static IReadOnlyList<MissingArgumentObligation> ReadObligations(JsonElement root) => root.GetProperty("obligations").EnumerateArray().Select(ReadObligation).ToArray();
    private static IReadOnlyList<SemanticFunction> ReadFunctions(JsonElement root) => root.GetProperty("functions").EnumerateArray().Select(ReadFunction).ToArray();
    private static IReadOnlyList<SemanticType> ReadTypes(JsonElement root) => root.GetProperty("types").EnumerateArray().Select(ReadType).ToArray();

    private static Effect ReadEffect(JsonElement element) { EnsureProperties(element, "displayName", "id"); return new(ReadId(element, "id"), element.GetProperty("displayName").GetString()!); }
    private static Capability ReadCapability(JsonElement element) { EnsureProperties(element, "displayName", "effectId", "id"); return new(ReadId(element, "id"), element.GetProperty("displayName").GetString()!, ReadId(element, "effectId")); }
    private static MissingArgumentObligation ReadObligation(JsonElement element) { EnsureProperties(element, "callId", "id", "originatingTransactionId", "parameterId", "resolutionSourceParameterId", "resolutionTransactionId", "status"); if (!Enum.TryParse<ObligationStatus>(element.GetProperty("status").GetString(), true, out var status) || !Enum.IsDefined(status)) throw new InvalidDataException("Invalid obligation status."); return new(ReadId(element, "id"), status, ReadId(element, "originatingTransactionId"), ReadId(element, "callId"), ReadId(element, "parameterId"), element.TryGetProperty("resolutionTransactionId", out var transaction) ? StableId.Parse(transaction.GetString()!) : null, element.TryGetProperty("resolutionSourceParameterId", out var source) ? StableId.Parse(source.GetString()!) : null); }
    private static SemanticFunction ReadFunction(JsonElement element)
    {
        EnsureProperties(element, "body", "calls", "declaredEffectIds", "displayName", "id", "parameters", "resultTypeId");
        var parameters = element.GetProperty("parameters").EnumerateArray().Select(parameter => { EnsureProperties(parameter, "displayName", "id", "typeId"); return new Parameter(ReadId(parameter, "id"), parameter.GetProperty("displayName").GetString()!, ReadId(parameter, "typeId")); }).ToArray();
        var calls = element.GetProperty("calls").EnumerateArray().Select(call => { EnsureProperties(call, "arguments", "calleeFunctionId", "id"); var arguments = call.GetProperty("arguments").EnumerateArray().Select(argument => { EnsureProperties(argument, "sourceParameterId", "targetParameterId"); return new ArgumentBinding(ReadId(argument, "targetParameterId"), ReadId(argument, "sourceParameterId")); }).ToArray(); return new Call(ReadId(call, "id"), ReadId(call, "calleeFunctionId"), arguments); }).ToArray();
        return new(ReadId(element, "id"), element.GetProperty("displayName").GetString()!, parameters, ReadId(element, "resultTypeId"), ReadIds(element.GetProperty("declaredEffectIds")), calls, element.TryGetProperty("body", out var body) ? ReadBody(body) : null);
    }
    private static SemanticType ReadType(JsonElement element)
    {
        EnsureProperties(element, "displayName", "fields", "id", "kind", "variants");
        var fields = element.GetProperty("fields").EnumerateArray().Select(field => { EnsureProperties(field, "displayName", "id", "typeId"); return new SemanticField(ReadId(field, "id"), field.GetProperty("displayName").GetString()!, ReadId(field, "typeId")); }).ToArray();
        var variants = element.GetProperty("variants").EnumerateArray().Select(variant => { EnsureProperties(variant, "displayName", "id"); return new SemanticVariant(ReadId(variant, "id"), variant.GetProperty("displayName").GetString()!); }).ToArray();
        var kindText = element.GetProperty("kind").GetString();
        if (!Enum.TryParse<TypeKind>(kindText, ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) throw new InvalidDataException($"Invalid type kind '{kindText}'.");
        return new(ReadId(element, "id"), element.GetProperty("displayName").GetString()!, kind, fields, variants);
    }
    private static void EnsureProperties(JsonElement element, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject()) if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException($"Unsupported property '{property.Name}'.");
    }

    private static void WriteBody(Utf8JsonWriter writer, FunctionBody body)
    {
        writer.WriteStartObject();
        switch (body)
        {
            case StringLiteralBody literal: writer.WriteString("kind", "stringLiteral"); writer.WriteString("value", literal.Value); break;
            case BoolLiteralBody literal: writer.WriteString("kind", "boolLiteral"); writer.WriteBoolean("value", literal.Value); break;
            case ParameterReferenceBody parameter: writer.WriteString("kind", "parameterReference"); writer.WriteString("parameterId", parameter.ParameterId.ToString()); break;
            case ConditionalBody conditional: writer.WritePropertyName("condition"); WriteBody(writer, conditional.Condition); writer.WriteString("kind", "conditional"); writer.WritePropertyName("whenFalse"); WriteBody(writer, conditional.WhenFalse); writer.WritePropertyName("whenTrue"); WriteBody(writer, conditional.WhenTrue); break;
            case DirectCallBody call:
                writer.WritePropertyName("arguments"); writer.WriteStartArray(); foreach (var argument in call.Arguments.OrderBy(argument => argument.TargetParameterId.ToString(), StringComparer.Ordinal)) { writer.WriteStartObject(); writer.WriteString("targetParameterId", argument.TargetParameterId.ToString()); writer.WritePropertyName("value"); WriteBody(writer, argument.Value); writer.WriteEndObject(); } writer.WriteEndArray(); writer.WriteString("calleeFunctionId", call.CalleeFunctionId.ToString()); writer.WriteString("id", call.Id.ToString()); writer.WriteString("kind", "directCall"); break;
            default: throw new NotSupportedException();
        }
        writer.WriteEndObject();
    }

    private static FunctionBody ReadBody(JsonElement element)
    {
        if (!element.TryGetProperty("kind", out var kindProperty)) throw new InvalidDataException("Function body requires a kind.");
        var kind = kindProperty.GetString();
        if (kind == "stringLiteral") { EnsureProperties(element, "kind", "value"); return new StringLiteralBody(element.GetProperty("value").GetString()!); }
        if (kind == "boolLiteral") { EnsureProperties(element, "kind", "value"); return new BoolLiteralBody(element.GetProperty("value").GetBoolean()); }
        if (kind == "parameterReference") { EnsureProperties(element, "kind", "parameterId"); return new ParameterReferenceBody(ReadId(element, "parameterId")); }
        if (kind == "conditional") { EnsureProperties(element, "condition", "kind", "whenFalse", "whenTrue"); return new ConditionalBody(ReadBody(element.GetProperty("condition")), ReadBody(element.GetProperty("whenTrue")), ReadBody(element.GetProperty("whenFalse"))); }
        if (kind == "directCall") { EnsureProperties(element, "arguments", "calleeFunctionId", "id", "kind"); return new DirectCallBody(ReadId(element, "id"), ReadId(element, "calleeFunctionId"), element.GetProperty("arguments").EnumerateArray().Select(argument => { EnsureProperties(argument, "targetParameterId", "value"); return new BodyArgument(ReadId(argument, "targetParameterId"), ReadBody(argument.GetProperty("value"))); }).ToArray()); }
        throw new InvalidDataException($"Unsupported body kind '{kind}'.");
    }
}
