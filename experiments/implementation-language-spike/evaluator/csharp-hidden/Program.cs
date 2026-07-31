using System.Text;
using System.Text.Json;
using Nayamnyam.Spike;

using CallId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.CallTag>;
using FunctionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.FunctionTag>;
using ObligationId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ObligationTag>;
using ParameterId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.ParameterTag>;
using TransactionId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TransactionTag>;
using TypeId = Nayamnyam.Spike.StableId<Nayamnyam.Spike.TypeTag>;

namespace Nayamnyam.Spike.Hidden;

internal static class Program
{
    private static readonly string FixturePath = FindFixture(
        new DirectoryInfo(AppContext.BaseDirectory));

    private static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            (
                "duplicate-name parameter obligations",
                DuplicateNameParameterObligations),
            (
                "same-name function and type renames are no change",
                SameNameRenameIsNoChange),
            (
                "type rename preserves identity and references",
                TypeRenamePreservesIdentityAndReferences),
            (
                "schema v2 migration and canonical stability",
                SchemaV2MigrationAndCanonicalStability),
        ];

        foreach (var (name, body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }

        return 0;
    }

    private static void DuplicateNameParameterObligations()
    {
        var snapshot = LoadInitial();
        var functionId = new FunctionId("function:validate-user");
        var existingParameterId = new ParameterId("parameter:input");
        var addedParameterId = new ParameterId("parameter:duplicate-input");
        var expectedConflictId = new ObligationId(
            "obligation:duplicate-name:validate-user:input:duplicate-input");
        var expectedMissingId = new ObligationId(
            "obligation:duplicate-name:main-validate:duplicate-input");
        ObligationId[] expectedObligationIds =
        [
            expectedMissingId,
            expectedConflictId,
        ];
        var parameter = new Parameter(
            addedParameterId,
            "input",
            new TypeId("type:string"));
        var result = Operations.AddParameter(
            snapshot,
            snapshot.Id,
            new TransactionId("transaction:duplicate-name"),
            functionId,
            parameter);
        var incomplete = RequireType<TransactionResult.Incomplete>(
            result,
            "Duplicate-name add must return Incomplete.");
        var originalFunction = snapshot.Functions.Single(
            function => function.Id == functionId);
        var changedFunction = incomplete.Snapshot.Functions.Single(
            function => function.Id == functionId);
        SequenceEqual(
            originalFunction.Parameters.Append(parameter),
            changedFunction.Parameters,
            "Duplicate-name add did not append the requested parameter.");

        var conflicts = incomplete.Snapshot.Obligations
            .Where(obligation =>
                obligation.Kind is ObligationKind.ParameterNameConflict)
            .ToArray();
        var missingArguments = incomplete.Snapshot.Obligations
            .Where(obligation =>
                obligation.Kind is ObligationKind.MissingArgument)
            .ToArray();
        Equal(1, conflicts.Length, "Expected one conflict obligation.");
        Equal(1, missingArguments.Length, "Expected one missing-argument obligation.");

        var conflict = RequireType<ObligationKind.ParameterNameConflict>(
            conflicts.Single().Kind,
            "Conflict obligation has the wrong kind.");
        Equal(functionId, conflict.FunctionId, "Conflict has the wrong function ID.");
        Equal(
            existingParameterId,
            conflict.ExistingParameterId,
            "Conflict has the wrong existing parameter ID.");
        Equal(
            addedParameterId,
            conflict.AddedParameterId,
            "Conflict has the wrong added parameter ID.");
        Equal(
            expectedConflictId,
            conflicts.Single().Id,
            "Conflict obligation has the wrong derived ID.");

        var missing = RequireType<ObligationKind.MissingArgument>(
            missingArguments.Single().Kind,
            "Missing-argument obligation has the wrong kind.");
        Equal(
            new CallId("call:main-validate"),
            missing.CallId,
            "Missing argument has the wrong call ID.");
        Equal(
            addedParameterId,
            missing.ParameterId,
            "Missing argument has the wrong parameter ID.");
        Equal(
            expectedMissingId,
            missingArguments.Single().Id,
            "Missing-argument obligation has the wrong derived ID.");
        SequenceEqual(
            expectedObligationIds,
            incomplete.Snapshot.Obligations.Select(obligation => obligation.Id),
            "Candidate obligations are not exactly the derived obligations in "
                + "ordinal order.");
        SequenceEqual(
            expectedObligationIds,
            incomplete.Blocking,
            "Blocking obligation IDs are not exact and ordinally sorted.");
        SemanticChange[] expectedDiff =
        [
            new SemanticChange.ParameterAdded(functionId, addedParameterId),
            new SemanticChange.ObligationCreated(expectedMissingId),
            new SemanticChange.ObligationCreated(expectedConflictId),
        ];
        SequenceEqual(
            expectedDiff,
            incomplete.Diff,
            "Duplicate-name semantic diff is not ParameterAdded followed by "
                + "ordinal ObligationCreated changes.");
    }

    private static void SameNameRenameIsNoChange()
    {
        var snapshot = LoadInitial();
        AssertSameNameNoChange(
            snapshot,
            new TransactionId("transaction:unused-function-no-change"),
            new RenameTarget.Function(
                new FunctionId("function:validate-user")),
            "validate_user",
            "Function");
        AssertSameNameNoChange(
            snapshot,
            new TransactionId("transaction:unused-type-no-change"),
            new RenameTarget.Type(
                new TypeId("type:raw-user-input")),
            "raw_user_input",
            "Type");
    }

    private static void AssertSameNameNoChange(
        Snapshot snapshot,
        TransactionId transactionId,
        RenameTarget target,
        string currentName,
        string targetLabel)
    {
        var result = Operations.RenameSymbol(
            snapshot,
            snapshot.Id,
            transactionId,
            target,
            currentName);
        var noChange = RequireType<TransactionResult.NoChange>(
            result,
            $"{targetLabel} same-name rename must return NoChange.");

        Equal(
            snapshot.Id,
            noChange.BaseSnapshotId,
            $"{targetLabel} NoChange did not preserve the base snapshot ID.");
        Equal(
            0,
            noChange.Diff.Count,
            $"{targetLabel} NoChange semantic diff must be empty.");
    }

    private static void TypeRenamePreservesIdentityAndReferences()
    {
        var snapshot = LoadInitial();
        var originalJson = CanonicalJson.WriteSnapshot(snapshot);
        var typeId = new TypeId("type:raw-user-input");
        var originalReferences = snapshot.Functions
            .SelectMany(function => function.Parameters)
            .Select(parameter => (parameter.Id, parameter.TypeId))
            .ToArray();
        var expectedTypes = snapshot.Types
            .Select(type => (
                type.Id,
                type.Id == typeId ? "raw_input" : type.Name))
            .ToArray();

        var result = Operations.RenameSymbol(
            snapshot,
            snapshot.Id,
            new TransactionId("transaction:rename-type"),
            new RenameTarget.Type(typeId),
            "raw_input");
        var executable = RequireType<TransactionResult.Executable>(
            result,
            "Type rename on the obligation-free fixture must be Executable.");
        var renamed = executable.Snapshot.Types.Single(type => type.Id == typeId);

        Equal(typeId, renamed.Id, "TypeId changed during type rename.");
        Equal("raw_input", renamed.Name, "Type display name did not change.");
        SequenceEqual(
            expectedTypes,
            executable.Snapshot.Types.Select(type => (type.Id, type.Name)),
            "Type rename changed more than the target display name.");
        SequenceEqual(
            originalReferences,
            executable.Snapshot.Functions
                .SelectMany(function => function.Parameters)
                .Select(parameter => (parameter.Id, parameter.TypeId)),
            "Type rename changed a parameter type reference.");
        var expectedCandidateJson = Encoding.UTF8.GetString(originalJson)
            .Replace(
                "\"id\": \"snapshot:initial\"",
                "\"id\": \"snapshot:rename-type\"",
                StringComparison.Ordinal)
            .Replace(
                "\"name\": \"raw_user_input\"",
                "\"name\": \"raw_input\"",
                StringComparison.Ordinal);
        SequenceEqual(
            Encoding.UTF8.GetBytes(expectedCandidateJson),
            CanonicalJson.WriteSnapshot(executable.Snapshot),
            "Type rename changed snapshot content beyond ID and display name.");
        Equal(1, executable.Diff.Count, "Type rename diff must have one change.");

        var change = RequireType<SemanticChange.TypeRenamed>(
            executable.Diff.Single(),
            "Type rename must emit TypeRenamed.");
        Equal(typeId, change.TypeId, "TypeRenamed has the wrong TypeId.");
        Equal(
            "raw_user_input",
            change.OldName,
            "TypeRenamed has the wrong old name.");
        Equal("raw_input", change.NewName, "TypeRenamed has the wrong new name.");
        SequenceEqual(
            originalJson,
            CanonicalJson.WriteSnapshot(snapshot),
            "Type rename mutated the input snapshot.");
    }

    private static void SchemaV2MigrationAndCanonicalStability()
    {
        var migrated = CanonicalJson.WriteSnapshot(
            CanonicalJson.ReadSnapshot(Encoding.UTF8.GetBytes(MigrationFixture)));
        using (var document = JsonDocument.Parse(migrated))
        {
            Equal(
                2,
                document.RootElement.GetProperty("schema_version").GetInt32(),
                "Migrated writer output is not schema v2.");
        }

        SequenceEqual(
            ["transaction:migrated"],
            ObligationOrigins(migrated),
            "Migrated obligation has the wrong origin transaction.");
        SequenceEqual(
            ["id,kind,status,origin_transaction_id"],
            ObligationPropertyOrders(migrated),
            "Migrated obligation properties are not in canonical order.");
        var reloaded = CanonicalJson.WriteSnapshot(
            CanonicalJson.ReadSnapshot(migrated));
        SequenceEqual(
            migrated,
            reloaded,
            "Canonical v2 output is not stable across two loads.");

        var snapshot = LoadInitial();
        var nativeResult = Operations.AddParameter(
            snapshot,
            snapshot.Id,
            new TransactionId("transaction:native"),
            new FunctionId("function:validate-user"),
            new Parameter(
                new ParameterId("parameter:native"),
                "native",
                new TypeId("type:string")));
        var incomplete = RequireType<TransactionResult.Incomplete>(
            nativeResult,
            "Native obligation setup must return Incomplete.");
        var nativeJson = CanonicalJson.WriteSnapshot(incomplete.Snapshot);
        SequenceEqual(
            ["transaction:native"],
            ObligationOrigins(nativeJson),
            "Native v2 obligation lost its creating transaction.");
        SequenceEqual(
            ["id,kind,status,origin_transaction_id"],
            ObligationPropertyOrders(nativeJson),
            "Native obligation properties are not in canonical order.");
    }

    private static Snapshot LoadInitial()
    {
        return CanonicalJson.ReadSnapshot(File.ReadAllBytes(FixturePath));
    }

    private static IReadOnlyList<string> ObligationOrigins(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("obligations")
            .EnumerateArray()
            .Select(obligation =>
                obligation.GetProperty("origin_transaction_id").GetString()
                ?? throw new InvalidOperationException(
                    "origin_transaction_id must not be null."))
            .ToArray();
    }

    private static IReadOnlyList<string> ObligationPropertyOrders(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("obligations")
            .EnumerateArray()
            .Select(obligation => string.Join(
                ",",
                obligation.EnumerateObject().Select(property => property.Name)))
            .ToArray();
    }

    private static string FindFixture(DirectoryInfo directory)
    {
        var candidate = Path.Combine(
            directory.FullName,
            "shared",
            "public",
            "fixtures",
            "initial-snapshot-v1.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return directory.Parent is null
            ? throw new InvalidOperationException(
                "Could not locate the public v1 fixture.")
            : FindFixture(directory.Parent);
    }

    private static T RequireType<T>(object value, string message)
        where T : class
    {
        return value as T ?? throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected: {expected}. Actual: {actual}.");
        }
    }

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static readonly string MigrationFixture = string.Join(
        "\n",
        [
            "{",
            "  \"schema_version\": 1,",
            "  \"id\": \"snapshot:migration\",",
            "  \"types\": [",
            "    {",
            "      \"id\": \"type:string\",",
            "      \"name\": \"string\"",
            "    }",
            "  ],",
            "  \"functions\": [",
            "    {",
            "      \"id\": \"function:validate-user\",",
            "      \"name\": \"validate_user\",",
            "      \"parameters\": [",
            "        {",
            "          \"id\": \"parameter:input\",",
            "          \"name\": \"input\",",
            "          \"type_id\": \"type:string\"",
            "        }",
            "      ]",
            "    }",
            "  ],",
            "  \"calls\": [",
            "    {",
            "      \"id\": \"call:main-validate\",",
            "      \"callee_id\": \"function:validate-user\",",
            "      \"arguments\": []",
            "    }",
            "  ],",
            "  \"obligations\": [",
            "    {",
            "      \"id\": \"obligation:migrate:main-validate:input\",",
            "      \"kind\": {",
            "        \"kind\": \"missing_argument\",",
            "        \"call_id\": \"call:main-validate\",",
            "        \"parameter_id\": \"parameter:input\"",
            "      },",
            "      \"status\": \"open\"",
            "    }",
            "  ]",
            "}",
            "",
        ]);
}
