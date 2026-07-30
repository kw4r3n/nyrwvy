using System.Diagnostics;
using System.Text.Json;

using CallId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.CallTag>;
using FunctionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.FunctionTag>;
using ObligationId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ObligationTag>;
using ParameterId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ParameterTag>;
using SnapshotId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.SnapshotTag>;
using TypeId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TypeTag>;

namespace Nayamnyam.Spike;

public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
    };

    public static Snapshot ReadSnapshot(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireProperties(
            root,
            "snapshot",
            "schema_version",
            "id",
            "types",
            "functions",
            "calls",
            "obligations");

        var schemaVersion = ReadInt32(root, "schema_version");
        if (schemaVersion != 1)
        {
            throw new JsonException(
                $"Schema version {schemaVersion} is not supported.");
        }

        var snapshot = new Snapshot(
            schemaVersion,
            new SnapshotId(ReadString(root, "id")),
            ReadArray(root, "types", ReadType),
            ReadArray(root, "functions", ReadFunction),
            ReadArray(root, "calls", ReadCall),
            ReadArray(root, "obligations", ReadObligation));
        var diagnostics = Validation.SnapshotDiagnostics(snapshot);
        if (diagnostics.Count != 0)
        {
            throw new JsonException(string.Join(
                Environment.NewLine,
                diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }

        return snapshot;
    }

    public static byte[] WriteSnapshot(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var diagnostics = Validation.SnapshotDiagnostics(snapshot);
        if (diagnostics.Count != 0)
        {
            throw new ArgumentException(
                string.Join(
                    Environment.NewLine,
                    diagnostics.Select(item => $"{item.Code}: {item.Message}")),
                nameof(snapshot));
        }

        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", snapshot.SchemaVersion);
            writer.WriteString("id", snapshot.Id.Value);
            writer.WriteStartArray("types");
            foreach (var type in snapshot.Types.OrderBy(
                item => item.Id.Value,
                StringComparer.Ordinal))
            {
                WriteType(writer, type);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("functions");
            foreach (var function in snapshot.Functions.OrderBy(
                item => item.Id.Value,
                StringComparer.Ordinal))
            {
                WriteFunction(writer, function);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("calls");
            foreach (var call in snapshot.Calls.OrderBy(
                item => item.Id.Value,
                StringComparer.Ordinal))
            {
                WriteCall(writer, call);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("obligations");
            foreach (var obligation in snapshot.Obligations.OrderBy(
                item => item.Id.Value,
                StringComparer.Ordinal))
            {
                WriteObligation(writer, obligation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static byte[] WriteSummary(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", snapshot.SchemaVersion);
            writer.WriteString("snapshot_id", snapshot.Id.Value);
            writer.WriteNumber("type_count", snapshot.Types.Count);
            writer.WriteNumber("function_count", snapshot.Functions.Count);
            writer.WriteNumber("call_count", snapshot.Calls.Count);
            writer.WriteNumber("obligation_count", snapshot.Obligations.Count);
            writer.WriteEndObject();
        });
    }

    private static TypeEntity ReadType(JsonElement element)
    {
        RequireProperties(element, "type", "id", "name");
        return new TypeEntity(
            new TypeId(ReadString(element, "id")),
            ReadString(element, "name"));
    }

    private static FunctionEntity ReadFunction(JsonElement element)
    {
        RequireProperties(element, "function", "id", "name", "parameters");
        return new FunctionEntity(
            new FunctionId(ReadString(element, "id")),
            ReadString(element, "name"),
            ReadArray(element, "parameters", ReadParameter));
    }

    private static Parameter ReadParameter(JsonElement element)
    {
        RequireProperties(element, "parameter", "id", "name", "type_id");
        return new Parameter(
            new ParameterId(ReadString(element, "id")),
            ReadString(element, "name"),
            new TypeId(ReadString(element, "type_id")));
    }

    private static CallEntity ReadCall(JsonElement element)
    {
        RequireProperties(element, "call", "id", "callee_id", "arguments");
        return new CallEntity(
            new CallId(ReadString(element, "id")),
            new FunctionId(ReadString(element, "callee_id")),
            ReadArray(element, "arguments", ReadArgument));
    }

    private static ArgumentBinding ReadArgument(JsonElement element)
    {
        RequireProperties(
            element,
            "argument binding",
            "parameter_id",
            "value_ref");
        return new ArgumentBinding(
            new ParameterId(ReadString(element, "parameter_id")),
            ReadString(element, "value_ref"));
    }

    private static Obligation ReadObligation(JsonElement element)
    {
        RequireProperties(element, "obligation", "id", "kind", "status");
        var statusText = ReadString(element, "status");
        if (!string.Equals(statusText, "open", StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Unknown obligation status '{statusText}'.");
        }

        return new Obligation(
            new ObligationId(ReadString(element, "id")),
            ReadObligationKind(GetRequiredProperty(element, "kind")),
            new ObligationStatus.Open());
    }

    private static ObligationKind ReadObligationKind(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Obligation kind must be an object.");
        }

        var kind = ReadString(element, "kind");
        if (string.Equals(kind, "missing_argument", StringComparison.Ordinal))
        {
            RequireProperties(
                element,
                "missing-argument obligation kind",
                "kind",
                "call_id",
                "parameter_id");
            return new ObligationKind.MissingArgument(
                new CallId(ReadString(element, "call_id")),
                new ParameterId(ReadString(element, "parameter_id")));
        }

        throw new JsonException($"Unknown obligation kind '{kind}'.");
    }

    private static void WriteType(Utf8JsonWriter writer, TypeEntity type)
    {
        writer.WriteStartObject();
        writer.WriteString("id", type.Id.Value);
        writer.WriteString("name", type.Name);
        writer.WriteEndObject();
    }

    private static void WriteFunction(
        Utf8JsonWriter writer,
        FunctionEntity function)
    {
        writer.WriteStartObject();
        writer.WriteString("id", function.Id.Value);
        writer.WriteString("name", function.Name);
        writer.WriteStartArray("parameters");
        foreach (var parameter in function.Parameters)
        {
            writer.WriteStartObject();
            writer.WriteString("id", parameter.Id.Value);
            writer.WriteString("name", parameter.Name);
            writer.WriteString("type_id", parameter.TypeId.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCall(Utf8JsonWriter writer, CallEntity call)
    {
        writer.WriteStartObject();
        writer.WriteString("id", call.Id.Value);
        writer.WriteString("callee_id", call.CalleeId.Value);
        writer.WriteStartArray("arguments");
        foreach (var argument in call.Arguments)
        {
            writer.WriteStartObject();
            writer.WriteString("parameter_id", argument.ParameterId.Value);
            writer.WriteString("value_ref", argument.ValueRef);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteObligation(
        Utf8JsonWriter writer,
        Obligation obligation)
    {
        writer.WriteStartObject();
        writer.WriteString("id", obligation.Id.Value);
        writer.WritePropertyName("kind");

        if (obligation.Kind is ObligationKind.MissingArgument missingArgument)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "missing_argument");
            writer.WriteString("call_id", missingArgument.CallId.Value);
            writer.WriteString("parameter_id", missingArgument.ParameterId.Value);
            writer.WriteEndObject();
        }
        else
        {
            throw new UnreachableException(
                "The closed obligation-kind hierarchy was not exhaustively handled.");
        }

        if (obligation.Status is ObligationStatus.Open)
        {
            writer.WriteString("status", "open");
        }
        else
        {
            throw new UnreachableException(
                "The closed obligation-status hierarchy was not exhaustively handled.");
        }

        writer.WriteEndObject();
    }

    private static IReadOnlyList<T> ReadArray<T>(
        JsonElement parent,
        string propertyName,
        Func<JsonElement, T> readItem)
    {
        var property = GetRequiredProperty(parent, propertyName);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(
                $"Property '{propertyName}' must be an array.");
        }

        return Array.AsReadOnly(property.EnumerateArray().Select(readItem).ToArray());
    }

    private static string ReadString(JsonElement parent, string propertyName)
    {
        var property = GetRequiredProperty(parent, propertyName);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                $"Property '{propertyName}' must be a string.");
        }

        return property.GetString()
            ?? throw new JsonException($"Property '{propertyName}' must not be null.");
    }

    private static int ReadInt32(JsonElement parent, string propertyName)
    {
        var property = GetRequiredProperty(parent, propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value))
        {
            throw new JsonException(
                $"Property '{propertyName}' must be a 32-bit integer.");
        }

        return value;
    }

    private static JsonElement GetRequiredProperty(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            throw new JsonException(
                $"Required property '{propertyName}' is missing.");
        }

        return property;
    }

    private static void RequireProperties(
        JsonElement element,
        string objectName,
        params string[] expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{objectName} must be an object.");
        }

        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new JsonException(
                    $"Unknown property '{property.Name}' in {objectName}.");
            }

            if (!encountered.Add(property.Name))
            {
                throw new JsonException(
                    $"Duplicate property '{property.Name}' in {objectName}.");
            }
        }

        foreach (var expectedName in expectedNames)
        {
            if (!encountered.Contains(expectedName))
            {
                throw new JsonException(
                    $"Required property '{expectedName}' is missing from "
                        + $"{objectName}.");
            }
        }
    }

    private static byte[] WriteCanonical(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }
}
