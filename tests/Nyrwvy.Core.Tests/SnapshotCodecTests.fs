namespace Nyrwvy.Core.Tests

open System.Text.Json.Nodes
open Nyrwvy.Core
open Xunit

module SnapshotCodecTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected codec error: %A" error

    [<Fact>]
    let ``canonical snapshot serialization round-trips the authoritative graph`` () =
        let fixture = TestGraph.create ()
        let original =
            { fixture.Snapshot with
                Types = List.rev fixture.Snapshot.Types
                Effects = List.rev fixture.Snapshot.Effects
                Capabilities = List.rev fixture.Snapshot.Capabilities
                Functions =
                    fixture.Snapshot.Functions
                    |> List.rev
                    |> List.map (fun functionEntity ->
                        { functionEntity with
                            Body = { functionEntity.Body with Nodes = List.rev functionEntity.Body.Nodes } }) }

        let canonical = SnapshotCodec.serialize original |> unwrap
        let restored = SnapshotCodec.deserialize canonical |> unwrap
        let reserialized = SnapshotCodec.serialize restored |> unwrap

        Assert.Equal(canonical, reserialized)
        Assert.Equal(original.Id, restored.Id)
        Assert.Equal(original.ParentId, restored.ParentId)

        if original.Obligations <> restored.Obligations then
            failwith "Obligations did not round-trip exactly."

        let restoredRun = restored.Functions |> List.find (fun item -> item.Id = fixture.RunFunctionId)
        Assert.Equal(fixture.RunCallNodeId, restoredRun.Body.ResultNodeId)
        Assert.Equal(3, restoredRun.Body.Nodes.Length)

    /// One function whose body nests `depth` conditionals (about three JSON levels each).
    let private deepSnapshot depth =
        let nodeId suffix = StableId.createForTesting<GraphNodeTag> suffix
        let literal suffix value: ValueGraph =
            let id = nodeId suffix
            { Nodes = [ { Id = id; Kind = SemanticNodeKind.BoolLiteral value } ]
              ResultNodeId = id }
        let rec body level =
            if level = 0 then literal "node-deep-leaf" true
            else
                let conditionId = nodeId (sprintf "node-deep-condition-%03d" level)
                let resultId = nodeId (sprintf "node-deep-result-%03d" level)
                { Nodes =
                    [ { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral true }
                      { Id = resultId
                        Kind =
                            SemanticNodeKind.Conditional(
                                conditionId,
                                body (level - 1),
                                literal (sprintf "node-deep-false-%03d" level) false
                            ) } ]
                  ResultNodeId = resultId }
        { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-deep") with
            Functions =
                [ { MustNotWait = false; Exported = true; Id = StableId.createForTesting<FunctionTag> "function-deep"
                    TypeParameters = []
                    DisplayName = "deep"
                    Parameters = []
                    ResultType = TypeRef.Primitive PrimitiveType.Bool
                    RequiredEffects = []
                    Body = body depth } ] }

    [<Fact>]
    let ``a resolved read's merged model is not validated again for its workspace`` () =
        let dependency = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-dependency")
        let resolved = SnapshotCodec.serializeResolved dependency (deepSnapshot 20) |> unwrap
        use scope = SnapshotComputationScope.Start(measure = true)
        let local = SnapshotCodec.deserializeResolved dependency resolved |> unwrap
        let merged = SnapshotCodec.resolveDependency dependency local
        Assert.Empty(SnapshotValidator.validate merged)
        Assert.Equal(1, scope.Measurements |> List.sumBy (fun item -> if item.Name = "validation" then item.Computations else 0))
        // Another dependency object is another merge.
        let other = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-dependency")
        Assert.NotSame(merged, SnapshotCodec.resolveDependency other local)

    [<Theory>]
    [<InlineData(20)>]
    [<InlineData(331)>]
    let ``deep conditional snapshots roundtrip standalone and dependency resolved codecs`` depth =
        let snapshot = deepSnapshot depth
        let dependency = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-dependency")
        let encoded = SnapshotCodec.serialize snapshot |> unwrap
        let restored = SnapshotCodec.deserialize encoded |> unwrap
        Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
        let resolved = SnapshotCodec.serializeResolved dependency snapshot |> unwrap
        let restoredResolved = SnapshotCodec.deserializeResolved dependency resolved |> unwrap
        Assert.Equal(resolved, SnapshotCodec.serializeResolved dependency restoredResolved |> unwrap)

    [<Fact>]
    let ``a graph nested beyond the JSON depth limit is a codec error, not an exception`` () =
        let snapshot = deepSnapshot 400
        let dependency = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-dependency")
        let expected: Result<string, SnapshotCodecError> = Error(SnapshotCodecError.MaximumDepthExceeded SnapshotCodec.maxJsonDepth)
        Assert.Equal(expected, SnapshotCodec.serialize snapshot)
        Assert.Equal(expected, SnapshotCodec.serializeResolved dependency snapshot)
        Assert.Equal(Error(SnapshotCodecError.MaximumDepthExceeded SnapshotCodec.maxJsonDepth), SnapshotFingerprint.compute snapshot |> Result.map SnapshotFingerprint.value)
        Assert.Equal(Error(SnapshotCodecError.MaximumDepthExceeded SnapshotCodec.maxJsonDepth), SemanticFingerprint.compute snapshot |> Result.map ignore)
        match SnapshotCodec.serializeResolved snapshot (Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-local")) with
        | Error(SnapshotCodecError.MaximumDepthExceeded _) -> ()
        | result -> failwithf "Expected an over-deep dependency digest to be a codec error, got %A" result

    [<Fact>]
    let ``snapshot codec retains a finite JSON depth limit`` () =
        let json = String.replicate 1001 "[" + "0" + String.replicate 1001 "]"
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.InvalidJson _) -> ()
        | result -> failwithf "Expected excessive JSON depth to be rejected, got %A" result

    [<Fact>]
    let ``snapshot codec preserves open missing-argument obligations`` () =
        let fixture = TestGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-with-obligation"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
                      CallSitePlans = [ LeaveMissing(fixture.RunCallNodeId, obligationId) ] } ] }

        let proposed =
            match Transaction.simulate fixture.Snapshot request with
            | Proposed simulation -> simulation.ProposedSnapshot
            | Rejected diagnostics -> failwithf "Unexpected rejection: %A" diagnostics

        let canonical = SnapshotCodec.serialize proposed |> unwrap
        let restored = SnapshotCodec.deserialize canonical |> unwrap
        let obligation = List.exactlyOne restored.Obligations

        Assert.Equal(obligationId, obligation.Id)
        Assert.Equal(fixture.RunCallNodeId, obligation.CallNodeId)

        match obligation.Status with
        | ObligationStatus.Open -> ()
        | ObligationStatus.Resolved _ -> failwith "Open obligation was restored as resolved."

    [<Fact>]
    let ``snapshot codec rejects duplicate property names`` () =
        let json = """{"schemaVersion":24,"snapshotId":"snapshot-a","snapshotId":"snapshot-b","types":[],"effects":[],"capabilities":[],"functions":[],"obligations":[]}"""
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("duplicate", message)
        | result -> failwithf "Expected duplicate-property InvalidShape, got %A" result

    [<Theory>]
    [<InlineData("\"snapshotId\"", "Snapshot contains duplicate property names.")>]
    [<InlineData("\"snapshot\\u0049d\"", "Snapshot contains duplicate property names.")>]
    let ``snapshot codec rejects duplicate decoded names before missing or unsupported properties`` duplicateName expected =
        let json = "{\"snapshotId\":\"snapshot-a\"," + duplicateName + ":\"snapshot-b\",\"unexpected\":0}"
        Assert.Equal(Error(SnapshotCodecError.InvalidShape expected), SnapshotCodec.deserialize json)

    [<Theory>]
    [<InlineData("{\"schemaVersion\":24,\"z\":0,\"a\":0}", "Snapshot is missing required properties: capabilities, effects, functions, obligations, snapshotId, types.")>]
    [<InlineData("{\"schemaVersion\":24,\"snapshotId\":\"snapshot\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[],\"obligations\":[],\"z\":0,\"a\":0}", "Snapshot contains unsupported properties: a, z.")>]
    let ``snapshot property diagnostics retain sorted names and missing property precedence`` json expected =
        Assert.Equal(Error(SnapshotCodecError.InvalidShape expected), SnapshotCodec.deserialize json)

    [<Fact>]
    let ``malformed property names take precedence even after a duplicate`` () =
        let malformed = "{\"snapshotId\":\"a\",\"snapshotId\":\"b\",\"\\uD800\":0}"
        match SnapshotCodec.deserialize malformed with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.StartsWith("Invalid JSON property name encoding:", message)
        | result -> failwithf "Expected malformed-name InvalidShape, got %A" result

    [<Theory>]
    [<InlineData("valueNodeId")>]
    [<InlineData(@"valueNode\u0049d")>]
    let ``shared unary property sets reject nested duplicate decoded names`` duplicateName =
        let literalId = StableId.createForTesting<GraphNodeTag> "unary-literal"
        let resultId = StableId.createForTesting<GraphNodeTag> "unary-result"
        let fn: SemanticFunction =
            { MustNotWait = false; Id = StableId.createForTesting<FunctionTag> "unary-function"
              DisplayName = "isEmpty"; Exported = true; TypeParameters = []; Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.Bool; RequiredEffects = []
              Body =
                { Nodes = [ { Id = literalId; Kind = SemanticNodeKind.StringLiteral "" }
                            { Id = resultId; Kind = SemanticNodeKind.StringIsEmpty literalId } ]
                  ResultNodeId = resultId } }
        let snapshot = { Snapshot.empty (StableId.createForTesting<SnapshotTag> "unary-snapshot") with Functions = [ fn ] }
        let json = SnapshotCodec.serialize snapshot |> unwrap
        let duplicate = json.Replace("\"kind\":\"stringIsEmpty\"", "\"kind\":\"stringIsEmpty\",\"" + duplicateName + "\":\"extra\"")
        Assert.NotEqual<string>(json, duplicate)
        Assert.Equal(Error(SnapshotCodecError.InvalidShape "StringIsEmpty node contains duplicate property names."), SnapshotCodec.deserialize duplicate)

    [<Fact>]
    let ``empty snapshots retain exact frozen and current JSON`` () =
        let snapshot = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-empty")
        let expected version =
            "{\"schemaVersion\":" + string version + ",\"snapshotId\":\"snapshot-empty\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[],\"obligations\":[]}"
        Assert.Equal(expected 9, SnapshotCodec.serializeCanonical snapshot |> unwrap)
        Assert.Equal(expected SnapshotSchema.current.Json, SnapshotCodec.serialize snapshot |> unwrap)

    [<Theory>]
    [<InlineData("null")>]
    [<InlineData("[]")>]
    [<InlineData("true")>]
    [<InlineData("42")>]
    [<InlineData("\"text\"")>]
    let ``snapshot codec rejects non-object tagged records without throwing`` (invalidValue: string) =
        let fixture = TestGraph.create ()
        let encoded = SnapshotCodec.serialize fixture.Snapshot |> unwrap
        let replacements: (JsonNode -> unit) list =
            [ (fun root -> root.["types"].[0] <- JsonNode.Parse invalidValue)
              (fun root -> root.["functions"].[0].["resultType"] <- JsonNode.Parse invalidValue)
              (fun root -> root.["functions"].[0].["body"].["nodes"].[0] <- JsonNode.Parse invalidValue)
              (fun root -> root.["obligations"].AsArray().Add(JsonNode.Parse invalidValue)) ]

        for replace in replacements do
            let root = JsonNode.Parse encoded
            replace root
            match SnapshotCodec.deserialize (root.ToJsonString()) with
            | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("object", message)
            | result -> failwithf "Expected non-object InvalidShape, got %A" result

    [<Theory>]
    [<InlineData(@"\uD800")>]
    [<InlineData(@"\uDC00")>]
    [<InlineData(@"\uD800A")>]
    [<InlineData(@"\uD800\uD800")>]
    [<InlineData(@"\uDC00\uD800")>]
    let ``snapshot codec rejects malformed surrogate escapes in names strings and ID arrays`` (invalidEscape: string) =
        let fixture = TestGraph.create ()
        let encoded = SnapshotCodec.serialize fixture.Snapshot |> unwrap
        let malformedStrings =
            [ encoded.Replace(StableId.value fixture.Snapshot.Id, invalidEscape)
              encoded.Replace("\"requiredEffects\":[]", "\"requiredEffects\":[\"" + invalidEscape + "\"]")
              encoded.Replace("\"schemaVersion\":", "\"" + invalidEscape + "\":")
              encoded.Replace("\"displayName\":", "\"" + invalidEscape + "\":") ]

        for json in malformedStrings do
            match SnapshotCodec.deserialize json with
            | Error(SnapshotCodecError.InvalidShape _) -> ()
            | result -> failwithf "Expected malformed-string InvalidShape, got %A" result

    let private loneSurrogateLiteralSnapshot (text: string) =
        let nodeId = StableId.createForTesting<GraphNodeTag> "node-lone-surrogate"
        let literal: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = StableId.createForTesting<FunctionTag> "function-lone-surrogate"
              TypeParameters = []
              DisplayName = "lone"
              Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.String
              RequiredEffects = []
              Body = { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral text } ]; ResultNodeId = nodeId } }
        { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-lone-surrogate") with Functions = [ literal ] }

    [<Fact>]
    let ``snapshot codec rejects a String literal with a lone surrogate`` () =
        // A String holds no unpaired surrogate, so the codec refuses to write one rather than
        // replacing it with U+FFFD.
        let snapshot = loneSurrogateLiteralSnapshot (utf16 "{D800}")
        match SnapshotCodec.serialize snapshot with
        | Error(SnapshotCodecError.InvalidSnapshot issues) ->
            Assert.Equal<string list>([ "StringLiteralUnpairedSurrogate" ], issues |> List.map _.Code)
        | result -> failwithf "Expected InvalidSnapshot, got %A" result

    [<Fact>]
    let ``snapshot codec preserves valid surrogate pairs`` () =
        let snapshot = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-\U0001F600")
        let encoded = SnapshotCodec.serialize snapshot |> unwrap
        let decoded = SnapshotCodec.deserialize encoded |> unwrap

        Assert.Equal(snapshot, decoded)

    [<Fact>]
    let ``snapshot codec rejects obsolete schema versions`` () =
        let json = """{"schemaVersion":7,"snapshotId":"snapshot","types":[],"effects":[],"capabilities":[],"functions":[],"obligations":[]}"""
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.UnsupportedSchemaVersion 7) -> ()
        | result -> failwithf "Expected UnsupportedSchemaVersion, got %A" result
        // Legacy versions below the first record version are no longer read, as in the Zig reader.
        Assert.Equal(Error(SnapshotCodecError.UnsupportedSchemaVersion 20), SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":7", "\"schemaVersion\":20")))
