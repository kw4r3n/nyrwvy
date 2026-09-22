namespace Nyrwvy.Core.Tests

open System
open System.Globalization
open System.IO
open System.Text
open Nyrwvy.Core
open Xunit

module NyasonTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected Nyason codec error: %A" error

    [<Fact>]
    let ``Nyason round-trips the representative Milestone 0 semantic graph canonically`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let decoded = NyasonCodec.deserialize encoded |> unwrap
        let reencoded = NyasonCodec.serialize decoded |> unwrap

        Assert.Equal(encoded, reencoded)
        Assert.Equal(fixture.Snapshot.Id, decoded.Id)
        Assert.Equal(fixture.Snapshot.Functions.Length, decoded.Functions.Length)
        Assert.Contains("direct-call {", encoded)
        Assert.Contains("result-type {", encoded)
        Assert.Contains("capability-invoke {", encoded)

    [<Fact>]
    let ``Nyason canonical output ignores nonsemantic top-level collection order`` () =
        let fixture = MilestoneZeroGraph.create ()
        let reordered =
            { fixture.Snapshot with
                Types = List.rev fixture.Snapshot.Types
                Effects = List.rev fixture.Snapshot.Effects
                Capabilities = List.rev fixture.Snapshot.Capabilities
                Functions = List.rev fixture.Snapshot.Functions
                Obligations = List.rev fixture.Snapshot.Obligations }

        let original = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let changedOrder = NyasonCodec.serialize reordered |> unwrap
        Assert.Equal(original, changedOrder)

    [<Fact>]
    let ``Nyason rejects an unsupported schema identity`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let changed = encoded.Replace(NyasonCodec.schemaId, "nayamnyam.snapshot.future", StringComparison.Ordinal)

        match NyasonCodec.deserialize changed with
        | Error(NyasonCodecError.UnsupportedSchema "nayamnyam.snapshot.future") -> ()
        | result -> failwithf "Expected UnsupportedSchema, got %A" result

    [<Fact>]
    let ``Nyason rejects unknown record fields`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let marker = "  snapshot = snapshot {"
        let changed = encoded.Replace(marker, "  unexpected = true\n" + marker, StringComparison.Ordinal)

        match NyasonCodec.deserialize changed with
        | Error(NyasonCodecError.InvalidShape message) -> Assert.Contains("unknown fields", message)
        | result -> failwithf "Expected InvalidShape, got %A" result

    [<Fact>]
    let ``Nyason rejects duplicate record fields during syntax parsing`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let schemaLine = $"  schema = \"{NyasonCodec.schemaId}\""
        let changed = encoded.Replace(schemaLine, schemaLine + "\n" + schemaLine, StringComparison.Ordinal)

        match NyasonCodec.deserialize changed with
        | Error(NyasonCodecError.InvalidSyntax error) -> Assert.Contains("Duplicate field", error.Message)
        | result -> failwithf "Expected InvalidSyntax, got %A" result

    [<Fact>]
    let ``Nyason typed stable IDs reject the wrong semantic domain before graph validation`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let changed = encoded.Replace("function:function-main", "node:function-main", StringComparison.Ordinal)

        match NyasonCodec.deserialize changed with
        | Error(NyasonCodecError.InvalidShape message) -> Assert.Contains("Expected function stable ID", message)
        | result -> failwithf "Expected typed-ID InvalidShape, got %A" result

    [<Fact>]
    let ``Nyason rejects malformed syntax without best-effort recovery`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let trimmed = encoded.TrimEnd()
        let changed = trimmed.Substring(0, trimmed.Length - 1)

        match NyasonCodec.deserialize changed with
        | Error(NyasonCodecError.InvalidSyntax _) -> ()
        | result -> failwithf "Expected InvalidSyntax, got %A" result

    [<Theory>]
    [<InlineData(" 041")>]
    [<InlineData("004 ")>]
    [<InlineData("\t041")>]
    [<InlineData("041\t")>]
    [<InlineData("\n041")>]
    [<InlineData("041\r")>]
    [<InlineData("004\u0000")>]
    [<InlineData("0G41")>]
    [<InlineData("041")>]
    [<InlineData("")>]
    let ``Nyason Unicode escapes require four hexadecimal digits`` (digits: string) =
        let source = "\"\\u" + digits + "\""
        match NyasonSyntax.parse source with
        | Error error ->
            Assert.Equal(3, error.Position)
            Assert.Contains("Unicode escape", error.Message)
        | result -> failwithf "Expected invalid Unicode escape, got %A" result

        let snapshot = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-unicode-escape")
        let encoded = NyasonCodec.serialize snapshot |> unwrap
        let malformed = encoded.Replace("\"" + NyasonCodec.schemaId + "\"", source, StringComparison.Ordinal)
        match NyasonCodec.deserialize malformed with
        | Error(NyasonCodecError.InvalidSyntax _) -> ()
        | result -> failwithf "Expected InvalidSyntax for Unicode escape, got %A" result

    [<Theory>]
    [<InlineData("0041", "A")>]
    [<InlineData("00e9", "\u00E9")>]
    [<InlineData("00E9", "\u00E9")>]
    [<InlineData("0000", "\u0000")>]
    [<InlineData(@"D83D\uDE00", "\U0001F600")>]
    [<InlineData("0041F", "AF")>]
    let ``Nyason decodes four hex digits and preserves canonical string roundtrip`` (digits: string) expected =
        let decoded = NyasonSyntax.parse ("\"\\u" + digits + "\"") |> unwrap
        Assert.Equal<NyasonValue>(NyasonValue.Text expected, decoded)
        Assert.Equal<NyasonValue>(decoded, NyasonSyntax.render decoded |> NyasonSyntax.parse |> unwrap)

    [<Fact>]
    let ``Nyason percent-escapes opaque stable ID payloads and restores them exactly`` () =
        let snapshotId = StableId.createForTesting<SnapshotTag> "snapshot with / slash and % sign"
        let snapshot = Snapshot.empty snapshotId
        let encoded = NyasonCodec.serialize snapshot |> unwrap
        let decoded = NyasonCodec.deserialize encoded |> unwrap

        Assert.Equal(snapshotId, decoded.Id)
        Assert.Contains("snapshot:snapshot%20with%20%2F%20slash%20and%20%25%20sign", encoded)

    [<Fact>]
    let ``encoding comparison writes reproducible transport-size metrics`` () =
        let fixture = MilestoneZeroGraph.create ()
        let nyason = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let json =
            match SnapshotCodec.serialize fixture.Snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected JSON codec error: %A" error

        let jsonBytes = Encoding.UTF8.GetByteCount json
        let nyasonBytes = Encoding.UTF8.GetByteCount nyason
        let ratio = float nyasonBytes / float jsonBytes
        let ratioText = ratio.ToString("F3", CultureInfo.InvariantCulture)
        let metrics =
            String.concat
                Environment.NewLine
                [ $"json-bytes={jsonBytes}"
                  $"nyason-bytes={nyasonBytes}"
                  $"nyason-to-json-byte-ratio={ratioText}"
                  $"json-chars={json.Length}"
                  $"nyason-chars={nyason.Length}" ]

        let path = Path.Combine(Path.GetTempPath(), "nyrwvy-encoding-comparison.txt")
        File.WriteAllText(path, metrics + Environment.NewLine)
        Assert.True(jsonBytes > 0)
        Assert.True(nyasonBytes > 0)
