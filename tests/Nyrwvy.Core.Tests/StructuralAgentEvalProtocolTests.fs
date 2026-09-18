namespace Nyrwvy.Core.Tests

open System
open System.Collections.Generic
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.AgentEval.Structural
open Xunit

module StructuralAgentEvalProtocolTests =
    type private Fixture =
        { Snapshot: Snapshot
          TargetFunctionId: FunctionId
          TargetParameterId: ParameterId
          ResultNodeId: GraphNodeId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-structural-protocol-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-structural-protocol-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-structural-protocol-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-structural-protocol-" + name)

    let private objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let private arrayValue (items: obj list): obj = items |> List.toArray |> box
    let private command (pairs: (string * obj) list): string = objectValue pairs |> JsonSerializer.Serialize

    let private createFixture (): Fixture =
        let float64Type = TypeRef.Primitive PrimitiveType.Float64
        let parameter: SemanticParameter =
            { Id = parameterId "value"
              DisplayName = "value"
              Type = float64Type }
        let resultId = nodeId "result"
        let target: SemanticFunction =
            { Id = functionId "target"
              DisplayName = "target"
              Parameters = [ parameter ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = resultId; Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0) } ]
                  ResultNodeId = resultId } }
        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ target ]
              Obligations = [] }
        Assert.Empty(SnapshotValidator.validate snapshot)
        { Snapshot = snapshot
          TargetFunctionId = target.Id
          TargetParameterId = parameter.Id
          ResultNodeId = resultId }

    let private createSession (snapshot: Snapshot): StructuralSession =
        match StructuralSession.create snapshot with
        | Ok session -> session
        | Error issues -> failwithf "Unexpected structural session creation failure: %A" issues

    let private parse (response: string): JsonDocument = JsonDocument.Parse(response)

    let private execute (session: StructuralSession) (payload: string): string =
        let response = session.ExecuteLine(payload)
        use document = parse response
        if not (document.RootElement.GetProperty("ok").GetBoolean()) then
            failwithf "Protocol command failed: %s" response
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

    let private simulateCommand (fixture: Fixture) (replacement: obj): string =
        command
            [ "command", box "simulateStructuralV1"
              "targetFunctionId", box (StableId.value fixture.TargetFunctionId)
              "expectedBaseSnapshotId", box (StableId.value fixture.Snapshot.Id)
              "expectedCurrentResultNodeId", box (StableId.value fixture.ResultNodeId)
              "replacement", replacement ]

    let private literal (bits: string): obj = objectValue [ "kind", box "literal"; "bits", box bits ]
    let private parameter (parameterId: ParameterId): obj =
        objectValue [ "kind", box "parameter"; "parameterId", box (StableId.value parameterId) ]
    let private binary kind (left: obj) (right: obj): obj =
        objectValue [ "kind", box kind; "left", left; "right", right ]
    let private add left right = binary "add" left right

    let private proposalCommand (name: string) (handle: string): string =
        command [ "command", box name; "proposalHandle", box handle ]

    [<Fact>]
    let ``structural runner CLI pins the separate M2 protocol condition`` () =
        match
            StructuralAgentEvalCommandLine.parse
                [ "--protocol"; StructuralProtocol.version; "--input"; "snapshot.json" ]
        with
        | Error error -> failwithf "Unexpected CLI parse failure: %s" error
        | Ok parsed ->
            Assert.Equal(StructuralProtocol.version, parsed.ProtocolVersion)
            Assert.Equal("snapshot.json", parsed.InputPath)

        match StructuralAgentEvalCommandLine.parse [ "--protocol"; "m1"; "--input"; "snapshot.json" ] with
        | Ok _ -> failwith "Wrong protocol version must be rejected."
        | Error error -> Assert.Contains(StructuralProtocol.version, error, StringComparison.Ordinal)

    [<Fact>]
    let ``describe exposes exact versioned command and recipe schema`` () =
        let fixture = createFixture ()
        let session = createSession fixture.Snapshot
        let response = command [ "command", box "describeV1" ] |> execute session
        use document = parse response
        let result = document.RootElement.GetProperty("result")
        Assert.Equal(StructuralProtocol.version, result.GetProperty("protocolVersion").GetString())

        let commands =
            result.GetProperty("commands").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("command").GetString())
            |> Set.ofSeq
        Assert.Contains("simulateStructuralV1", commands)
        Assert.Contains("commitStructuralV1", commands)
        Assert.Contains("rejectStructuralV1", commands)

        let recipeCases =
            result.GetProperty("recipeCases").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("kind").GetString())
            |> Seq.toList
        let expected = [ "parameter"; "literal"; "add"; "subtract"; "multiply"; "divide" ]
        if recipeCases <> expected then
            failwithf "Unexpected recipe schema: %A" recipeCases

        let authority = result.GetProperty("authority")
        Assert.Equal("system-owned", authority.GetProperty("newGraphNodeIds").GetString())
        Assert.False(authority.GetProperty("rawSnapshotPayload").GetBoolean())

    [<Fact>]
    let ``focus and structural proposal expose stable IDs requested recipe and materialized review`` () =
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
        let parameters = focus.GetProperty("function").GetProperty("parameters")
        Assert.Equal(StableId.value fixture.TargetParameterId, parameters[0].GetProperty("id").GetString())

        let replacement = add (parameter fixture.TargetParameterId) (literal "7ff0000000000001")
        let proposalResponse = simulateCommand fixture replacement |> execute session
        Assert.Equal("proposed", resultStatus proposalResponse)

        use proposalDocument = parse proposalResponse
        let review = proposalDocument.RootElement.GetProperty("result").GetProperty("review")
        let requested = review.GetProperty("requestedChange").GetProperty("replacement")
        Assert.Equal("add", requested.GetProperty("kind").GetString())
        Assert.Equal("7ff8000000000000", requested.GetProperty("right").GetProperty("bits").GetString())

        let added = review.GetProperty("resultingChange").GetProperty("addedNodes")
        Assert.Equal(3, added.GetArrayLength())
        Assert.Equal("ParameterReference", added[0].GetProperty("role").GetString())
        Assert.Equal("Float64Literal", added[1].GetProperty("role").GetString())
        Assert.Equal("7ff8000000000000", added[1].GetProperty("float64Bits").GetString())
        Assert.Equal("Float64Add", added[2].GetProperty("role").GetString())

    [<Fact>]
    let ``structural arithmetic recipe cases materialize their semantic roles`` () =
        let fixture = createFixture ()
        let cases =
            [ "subtract", "Float64Subtract"
              "multiply", "Float64Multiply"
              "divide", "Float64Divide" ]

        for kind, expectedRole in cases do
            let session = createSession fixture.Snapshot
            let replacement = binary kind (parameter fixture.TargetParameterId) (literal "4000000000000000")
            let response = simulateCommand fixture replacement |> execute session
            Assert.Equal("proposed", resultStatus response)
            use document = parse response
            let review = document.RootElement.GetProperty("result").GetProperty("review")
            Assert.Equal(kind, review.GetProperty("requestedChange").GetProperty("replacement").GetProperty("kind").GetString())
            let added = review.GetProperty("resultingChange").GetProperty("addedNodes")
            Assert.Equal(expectedRole, added[2].GetProperty("role").GetString())

    [<Fact>]
    let ``structural protocol retires stale proposals without counting agent rejection`` () =
        let fixture = createFixture ()
        let session = createSession fixture.Snapshot
        let firstResponse = simulateCommand fixture (literal "4000000000000000") |> execute session
        let secondResponse = simulateCommand fixture (literal "4008000000000000") |> execute session
        let firstHandle = proposalHandle firstResponse
        let secondHandle = proposalHandle secondResponse

        let commitResponse = proposalCommand "commitStructuralV1" firstHandle |> execute session
        Assert.Equal("committed", resultStatus commitResponse)

        let staleResponse = proposalCommand "commitStructuralV1" secondHandle |> execute session
        Assert.Equal("rejected", resultStatus staleResponse)
        Assert.Equal("StaleBaseSnapshot", firstDiagnosticCode staleResponse)

        let freshState = command [ "command", box "stateV1" ] |> execute session
        use stateDocument = parse freshState
        let state = stateDocument.RootElement.GetProperty("result")
        let currentSnapshotId = state.GetProperty("snapshotId").GetString()
        if currentSnapshotId = StableId.value fixture.Snapshot.Id then
            failwith "Structural commit did not advance the session snapshot."
        Assert.Equal(0, state.GetProperty("proposalHandles").GetArrayLength())
        let metrics = state.GetProperty("metrics")
        Assert.Equal(0, metrics.GetProperty("rejects").GetInt32())
        Assert.Equal(0, metrics.GetProperty("pendingProposalCount").GetInt32())

    [<Fact>]
    let ``structural diagnostics remain structured and raw node-ID payloads are rejected`` () =
        let fixture = createFixture ()
        let session = createSession fixture.Snapshot
        let foreign = StableId.createForTesting<ParameterTag> "parameter-structural-protocol-foreign"

        let rejected = simulateCommand fixture (parameter foreign) |> execute session
        Assert.Equal("rejected", resultStatus rejected)
        Assert.Equal("StructuralParameterOutOfScope", firstDiagnosticCode rejected)

        let rawPayload =
            objectValue
                [ "kind", box "literal"
                  "bits", box "3ff0000000000000"
                  "newGraphNodeId", box "agent-supplied-node" ]
        let response = session.ExecuteLine(simulateCommand fixture rawPayload)
        use document = parse response
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean())
        let error = document.RootElement.GetProperty("error")
        Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
        Assert.Contains("unsupported properties", error.GetProperty("message").GetString(), StringComparison.Ordinal)
