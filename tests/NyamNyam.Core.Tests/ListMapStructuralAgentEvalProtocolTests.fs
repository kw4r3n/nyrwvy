namespace NyamNyam.Core.Tests

open System
open System.Collections.Generic
open System.Text.Json
open NyamNyam.Core
open NyamNyam.AgentEval.ListMapStructural
open Xunit

module ListMapStructuralAgentEvalProtocolTests =
    type private Fixture =
        { Snapshot: Snapshot
          TargetFunctionId: FunctionId
          SourceParameterId: ParameterId
          NonListParameterId: ParameterId
          ResultNodeId: GraphNodeId
          MapperFunctionId: FunctionId
          WrongInputMapperFunctionId: FunctionId
          WrongResultMapperFunctionId: FunctionId
          EffectfulMapperFunctionId: FunctionId
          ForeignParameterId: ParameterId }

    let private functionId name =
        StableId.createForTesting<FunctionTag> ("function-listmap-protocol-" + name)

    let private parameterId name =
        StableId.createForTesting<ParameterTag> ("parameter-listmap-protocol-" + name)

    let private nodeId name =
        StableId.createForTesting<GraphNodeTag> ("node-listmap-protocol-" + name)

    let private snapshotId name =
        StableId.createForTesting<SnapshotTag> ("snapshot-listmap-protocol-" + name)

    let private effectId =
        StableId.createForTesting<EffectTag> "effect-listmap-protocol"

    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])

    let private objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let private command (pairs: (string * obj) list): string =
        objectValue pairs |> JsonSerializer.Serialize

    let private unaryFunction
        (name: string)
        (parameterType: TypeRef)
        (resultType: TypeRef)
        (requiredEffects: EffectId list)
        (bodyBuilder: GraphNodeId -> SemanticNode list * GraphNodeId)
        : SemanticFunction =
        let parameter: SemanticParameter =
            { Id = parameterId (name + "-value")
              DisplayName = "value"
              Type = parameterType }
        let parameterNodeId = nodeId (name + "-parameter")
        let parameterNode =
            { Id = parameterNodeId
              Kind = SemanticNodeKind.ParameterReference parameter.Id }
        let extraNodes, resultNodeId = bodyBuilder parameterNodeId
        { Id = functionId name
          DisplayName = name
          Parameters = [ parameter ]
          ResultType = resultType
          RequiredEffects = requiredEffects
          Body =
            { Nodes = parameterNode :: extraNodes
              ResultNodeId = resultNodeId } }

    let private createFixture (): Fixture =
        let mapper =
            unaryFunction "mapper" int64Type boolType [] (fun valueNodeId ->
                let zeroId = nodeId "mapper-zero"
                let resultId = nodeId "mapper-result"
                [ { Id = zeroId; Kind = SemanticNodeKind.Int64Literal 0L }
                  { Id = resultId; Kind = SemanticNodeKind.Int64GreaterThan(valueNodeId, zeroId) } ],
                resultId)

        let wrongInput =
            unaryFunction "wrong-input" stringType boolType [] (fun _ ->
                let resultId = nodeId "wrong-input-result"
                [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

        let wrongResult =
            unaryFunction "wrong-result" int64Type int64Type [] (fun valueNodeId -> [], valueNodeId)

        let effectful =
            unaryFunction "effectful" int64Type boolType [ effectId ] (fun _ ->
                let resultId = nodeId "effectful-result"
                [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

        let sourceParameter: SemanticParameter =
            { Id = parameterId "target-values"
              DisplayName = "values"
              Type = listOf int64Type }
        let nonListParameter: SemanticParameter =
            { Id = parameterId "target-count"
              DisplayName = "count"
              Type = int64Type }
        let resultId = nodeId "target-result"
        let target: SemanticFunction =
            { Id = functionId "target"
              DisplayName = "target"
              Parameters = [ sourceParameter; nonListParameter ]
              ResultType = listOf boolType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = resultId; Kind = SemanticNodeKind.ListEmpty boolType } ]
                  ResultNodeId = resultId } }

        let foreignParameter: SemanticParameter =
            { Id = parameterId "foreign-values"
              DisplayName = "foreign_values"
              Type = listOf int64Type }
        let foreignResultId = nodeId "foreign-result"
        let foreign: SemanticFunction =
            { Id = functionId "foreign"
              DisplayName = "foreign"
              Parameters = [ foreignParameter ]
              ResultType = listOf int64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = foreignResultId
                        Kind = SemanticNodeKind.ParameterReference foreignParameter.Id } ]
                  ResultNodeId = foreignResultId } }

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = [ { Id = effectId; DisplayName = "protocol_effect" } ]
              Capabilities = []
              Functions = [ mapper; wrongInput; wrongResult; effectful; target; foreign ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)
        { Snapshot = snapshot
          TargetFunctionId = target.Id
          SourceParameterId = sourceParameter.Id
          NonListParameterId = nonListParameter.Id
          ResultNodeId = resultId
          MapperFunctionId = mapper.Id
          WrongInputMapperFunctionId = wrongInput.Id
          WrongResultMapperFunctionId = wrongResult.Id
          EffectfulMapperFunctionId = effectful.Id
          ForeignParameterId = foreignParameter.Id }

    let private createSession (snapshot: Snapshot): ListMapStructuralSession =
        match ListMapStructuralSession.create snapshot with
        | Ok session -> session
        | Error issues -> failwithf "Unexpected ListMap structural session creation failure: %A" issues

    let private parse (response: string): JsonDocument = JsonDocument.Parse(response)

    let private execute (session: ListMapStructuralSession) (payload: string): string =
        let response = session.ExecuteLine(payload)
        use document = parse response
        if not (document.RootElement.GetProperty("ok").GetBoolean()) then
            failwithf "ListMap protocol command failed: %s" response
        response

    let private resultStatus (response: string): string =
        use document = parse response
        document.RootElement.GetProperty("result").GetProperty("status").GetString()

    let private proposalHandle (response: string): string =
        use document = parse response
        document.RootElement.GetProperty("result").GetProperty("proposalHandle").GetString()

    let private firstDiagnosticCode (response: string): string =
        use document = parse response
        document.RootElement
            .GetProperty("result")
            .GetProperty("diagnostics")
            .EnumerateArray()
        |> Seq.head
        |> fun diagnostic -> diagnostic.GetProperty("code").GetString()

    let private simulateCommand
        (fixture: Fixture)
        (sourceParameterId: ParameterId)
        (mapperFunctionId: FunctionId)
        (expectedBase: SnapshotId)
        (expectedResult: GraphNodeId)
        : string =
        command
            [ "command", box "simulateListMapV1"
              "targetFunctionId", box (StableId.value fixture.TargetFunctionId)
              "expectedBaseSnapshotId", box (StableId.value expectedBase)
              "expectedCurrentResultNodeId", box (StableId.value expectedResult)
              "sourceParameterId", box (StableId.value sourceParameterId)
              "mapperFunctionId", box (StableId.value mapperFunctionId) ]

    let private proposalCommand (name: string) (handle: string): string =
        command [ "command", box name; "proposalHandle", box handle ]

    [<Fact>]
    let ``ListMap runner CLI pins its separate protocol`` () =
        match
            ListMapStructuralAgentEvalCommandLine.parse
                [ "--protocol"; ListMapStructuralProtocol.version; "--input"; "snapshot.json" ]
        with
        | Error error -> failwithf "Unexpected CLI parse failure: %s" error
        | Ok parsed ->
            Assert.Equal(ListMapStructuralProtocol.version, parsed.ProtocolVersion)
            Assert.Equal("snapshot.json", parsed.InputPath)

        match
            ListMapStructuralAgentEvalCommandLine.parse
                [ "--protocol"; "m2-structural-v1"; "--input"; "snapshot.json" ]
        with
        | Ok _ -> failwith "Wrong protocol version must be rejected."
        | Error error -> Assert.Contains(ListMapStructuralProtocol.version, error, StringComparison.Ordinal)

        match
            ListMapStructuralAgentEvalCommandLine.parse
                [ "--protocol"; ListMapStructuralProtocol.version; "--input"; "snapshot.json"; "--extra"; "x" ]
        with
        | Ok _ -> failwith "Unknown CLI option must be rejected."
        | Error error -> Assert.Contains("Unsupported option", error, StringComparison.Ordinal)

    [<Fact>]
    let ``ListMap describe exposes only shallow authority`` () =
        let fixture = createFixture ()
        let session = createSession fixture.Snapshot
        let response = command [ "command", box "describeV1" ] |> execute session
        use document = parse response
        let result = document.RootElement.GetProperty("result")
        Assert.Equal(ListMapStructuralProtocol.version, result.GetProperty("protocolVersion").GetString())

        let commands =
            result.GetProperty("commands").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("command").GetString())
            |> Seq.toList
        let expected =
            [ "describeV1"
              "stateV1"
              "focusFunctionV1"
              "simulateListMapV1"
              "commitListMapV1"
              "rejectListMapV1"
              "metricsV1" ]
        if commands <> expected then
            failwithf "Unexpected ListMap command surface: %A" commands

        let authority = result.GetProperty("authority")
        for field in
            [ "rawSnapshotPayload"
              "rawValueGraphPayload"
              "rawSemanticNodePayload"
              "genericExpressionRecipe"
              "lambdaClosureConstruction"
              "effectfulCallbackAuthority" ] do
            Assert.False(authority.GetProperty(field).GetBoolean(), field)

        let roles =
            result.GetProperty("operation").GetProperty("materializedRoles").EnumerateArray()
            |> Seq.map (fun item -> item.GetString())
            |> Seq.toList
        if roles <> [ "ParameterReference"; "ListMap" ] then
            failwithf "Unexpected materialized ListMap roles: %A" roles

    [<Fact>]
    let ``ListMap focus proposal review and commit preserve requested versus materialized IDs`` () =
        let fixture = createFixture ()
        let session = createSession fixture.Snapshot

        let focusResponse =
            command
                [ "command", box "focusFunctionV1"
                  "functionId", box (StableId.value fixture.TargetFunctionId) ]
            |> execute session
        use focusDocument = parse focusResponse
        let focus = focusDocument.RootElement.GetProperty("result").GetProperty("value")
        Assert.Equal(StableId.value fixture.Snapshot.Id, focus.GetProperty("snapshotId").GetString())

        let proposalResponse =
            simulateCommand
                fixture
                fixture.SourceParameterId
                fixture.MapperFunctionId
                fixture.Snapshot.Id
                fixture.ResultNodeId
            |> execute session
        Assert.Equal("proposed", resultStatus proposalResponse)
        Assert.Equal("listmap-proposal-1", proposalHandle proposalResponse)

        use proposalDocument = parse proposalResponse
        let review = proposalDocument.RootElement.GetProperty("result").GetProperty("review")
        let requested = review.GetProperty("requestedChange")
        Assert.Equal(StableId.value fixture.SourceParameterId, requested.GetProperty("sourceParameterId").GetString())
        Assert.Equal(StableId.value fixture.MapperFunctionId, requested.GetProperty("mapperFunctionId").GetString())

        let added = review.GetProperty("resultingChange").GetProperty("addedNodes")
        Assert.Equal(2, added.GetArrayLength())
        Assert.Equal("ParameterReference", added[0].GetProperty("role").GetString())
        Assert.Equal("ListMap", added[1].GetProperty("role").GetString())
        Assert.Equal(StableId.value fixture.SourceParameterId, added[0].GetProperty("parameterId").GetString())
        Assert.Equal(StableId.value fixture.MapperFunctionId, added[1].GetProperty("functionId").GetString())

        let commitResponse = proposalCommand "commitListMapV1" "listmap-proposal-1" |> execute session
        Assert.Equal("committed", resultStatus commitResponse)

        let stateResponse = command [ "command", box "stateV1" ] |> execute session
        use stateDocument = parse stateResponse
        let state = stateDocument.RootElement.GetProperty("result")
        if state.GetProperty("snapshotId").GetString() = StableId.value fixture.Snapshot.Id then
            failwith "ListMap commit did not advance the session snapshot."
        Assert.Equal(0, state.GetProperty("proposalHandles").GetArrayLength())
        let metrics = state.GetProperty("metrics")
        Assert.Equal(1, metrics.GetProperty("focusQueries").GetInt32())
        Assert.Equal(1, metrics.GetProperty("simulations").GetInt32())
        Assert.Equal(1, metrics.GetProperty("commitAttempts").GetInt32())
        Assert.Equal(1, metrics.GetProperty("commits").GetInt32())

    [<Fact>]
    let ``ListMap protocol preserves structured preflight diagnostics`` () =
        let fixture = createFixture ()
        let cases: (string * ParameterId * FunctionId * SnapshotId * GraphNodeId) list =
            [ "StructuralExpectedBaseMismatch",
              fixture.SourceParameterId,
              fixture.MapperFunctionId,
              snapshotId "wrong-base",
              fixture.ResultNodeId
              "StructuralExpectedResultMismatch",
              fixture.SourceParameterId,
              fixture.MapperFunctionId,
              fixture.Snapshot.Id,
              nodeId "wrong-result"
              "StructuralListSourceParameterOutOfScope",
              fixture.ForeignParameterId,
              fixture.MapperFunctionId,
              fixture.Snapshot.Id,
              fixture.ResultNodeId
              "StructuralListSourceTypeMismatch",
              fixture.NonListParameterId,
              fixture.MapperFunctionId,
              fixture.Snapshot.Id,
              fixture.ResultNodeId
              "StructuralListMapperNotFound",
              fixture.SourceParameterId,
              functionId "missing-mapper",
              fixture.Snapshot.Id,
              fixture.ResultNodeId
              "StructuralListMapperInputTypeMismatch",
              fixture.SourceParameterId,
              fixture.WrongInputMapperFunctionId,
              fixture.Snapshot.Id,
              fixture.ResultNodeId
              "StructuralListMapperResultTypeMismatch",
              fixture.SourceParameterId,
              fixture.WrongResultMapperFunctionId,
              fixture.Snapshot.Id,
              fixture.ResultNodeId
              "StructuralListMapperEffectsNotAllowed",
              fixture.SourceParameterId,
              fixture.EffectfulMapperFunctionId,
              fixture.Snapshot.Id,
              fixture.ResultNodeId ]

        for expectedCode, sourceId, mapperId, baseId, resultId in cases do
            let session = createSession fixture.Snapshot
            let response = simulateCommand fixture sourceId mapperId baseId resultId |> execute session
            Assert.Equal("rejected", resultStatus response)
            Assert.Equal(expectedCode, firstDiagnosticCode response)

    [<Fact>]
    let ``ListMap stale proposals remain opaque and explicitly rejectable`` () =
        let fixture = createFixture ()
        let session = createSession fixture.Snapshot
        let simulate =
            simulateCommand
                fixture
                fixture.SourceParameterId
                fixture.MapperFunctionId
                fixture.Snapshot.Id
                fixture.ResultNodeId

        let firstResponse = simulate |> execute session
        let secondResponse = simulate |> execute session
        let firstHandle = proposalHandle firstResponse
        let secondHandle = proposalHandle secondResponse
        Assert.Equal("listmap-proposal-1", firstHandle)
        Assert.Equal("listmap-proposal-2", secondHandle)

        let commitResponse = proposalCommand "commitListMapV1" firstHandle |> execute session
        Assert.Equal("committed", resultStatus commitResponse)

        let staleResponse = proposalCommand "commitListMapV1" secondHandle |> execute session
        Assert.Equal("rejected", resultStatus staleResponse)
        Assert.Equal("StaleBaseSnapshot", firstDiagnosticCode staleResponse)

        let rejectResponse = proposalCommand "rejectListMapV1" secondHandle |> execute session
        Assert.Equal("rejectedByAgent", resultStatus rejectResponse)

        let stateResponse = command [ "command", box "stateV1" ] |> execute session
        use stateDocument = parse stateResponse
        let state = stateDocument.RootElement.GetProperty("result")
        Assert.Equal(0, state.GetProperty("proposalHandles").GetArrayLength())
        let metrics = state.GetProperty("metrics")
        Assert.Equal(2, metrics.GetProperty("simulations").GetInt32())
        Assert.Equal(2, metrics.GetProperty("commitAttempts").GetInt32())
        Assert.Equal(1, metrics.GetProperty("commits").GetInt32())
        Assert.Equal(1, metrics.GetProperty("rejects").GetInt32())
        Assert.Equal(0, metrics.GetProperty("pendingProposalCount").GetInt32())

    [<Fact>]
    let ``ListMap protocol rejects malformed widened and unknown command payloads`` () =
        let fixture = createFixture ()
        let session = createSession fixture.Snapshot
        let malformed =
            [ "[]"
              ""
              command [ "command", box "describeV1"; "extra", box true ]
              command [ "command", box "unsupportedV1" ]
              command
                  [ "command", box "simulateListMapV1"
                    "targetFunctionId", box (StableId.value fixture.TargetFunctionId)
                    "expectedBaseSnapshotId", box (StableId.value fixture.Snapshot.Id)
                    "expectedCurrentResultNodeId", box (StableId.value fixture.ResultNodeId)
                    "sourceParameterId", box (StableId.value fixture.SourceParameterId)
                    "mapperFunctionId", box (StableId.value fixture.MapperFunctionId)
                    "replacement", objectValue [] ] ]

        for payload in malformed do
            let response = session.ExecuteLine(payload)
            use document = parse response
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), response)
            Assert.Equal("ProtocolError", document.RootElement.GetProperty("error").GetProperty("code").GetString())

        let metricsResponse = command [ "command", box "metricsV1" ] |> execute session
        use metricsDocument = parse metricsResponse
        let metrics = metricsDocument.RootElement.GetProperty("result").GetProperty("metrics")
        Assert.Equal(malformed.Length, metrics.GetProperty("protocolErrors").GetInt32())
