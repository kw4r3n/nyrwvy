namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module CapabilityTests =
    type private ConsoleFixture =
        { Snapshot: Snapshot
          EffectId: EffectId
          CapabilityId: CapabilityId
          OperationId: CapabilityOperationId
          OperationParameterId: CapabilityParameterId
          FunctionId: FunctionId
          ConsoleParameterId: ParameterId
          MessageParameterId: ParameterId
          ConsoleNodeId: GraphNodeId
          MessageNodeId: GraphNodeId
          InvokeNodeId: GraphNodeId }

    let private requireIssue code issues =
        if not (issues |> List.exists (fun issue -> issue.Code = code)) then
            failwithf "Expected validation issue %s, got %A" code issues

    let private createConsoleFixture (): ConsoleFixture =
        let stringType = TypeRef.Primitive PrimitiveType.String
        let unitType = TypeRef.Primitive PrimitiveType.Unit
        let effectId = StableId.createForTesting<EffectTag> "effect-console-output"
        let capabilityId = StableId.createForTesting<CapabilityTag> "capability-console"
        let operationId = StableId.createForTesting<CapabilityOperationTag> "capability-operation-write-line"
        let operationParameterId = StableId.createForTesting<CapabilityParameterTag> "capability-parameter-text"
        let functionId = StableId.createForTesting<FunctionTag> "function-report"
        let consoleParameterId = StableId.createForTesting<ParameterTag> "parameter-console"
        let messageParameterId = StableId.createForTesting<ParameterTag> "parameter-message"
        let consoleNodeId = StableId.createForTesting<GraphNodeTag> "node-console"
        let messageNodeId = StableId.createForTesting<GraphNodeTag> "node-message"
        let invokeNodeId = StableId.createForTesting<GraphNodeTag> "node-write-line"

        let effect: SemanticEffect =
            { Id = effectId
              DisplayName = "ConsoleOutput" }

        let operationParameter: SemanticCapabilityParameter =
            { Id = operationParameterId
              DisplayName = "text"
              Type = stringType }

        let operation: SemanticCapabilityOperation =
            { Id = operationId
              DisplayName = "write_line"
              Parameters = [ operationParameter ]
              ResultType = unitType
              FailureType = None
              EffectId = effectId }

        let capability: SemanticCapability =
            { Id = capabilityId
              DisplayName = "Console"
              Operations = [ operation ] }

        let consoleParameter: SemanticParameter =
            { Id = consoleParameterId
              DisplayName = "console"
              Type = TypeRef.Capability capabilityId }

        let messageParameter: SemanticParameter =
            { Id = messageParameterId
              DisplayName = "message"
              Type = stringType }

        let consoleNode: SemanticNode =
            { Id = consoleNodeId
              Kind = SemanticNodeKind.ParameterReference consoleParameterId }

        let messageNode: SemanticNode =
            { Id = messageNodeId
              Kind = SemanticNodeKind.ParameterReference messageParameterId }

        let argument: CapabilityArgumentBinding =
            { TargetCapabilityParameterId = operationParameterId
              ValueNodeId = messageNodeId }

        let invokeNode: SemanticNode =
            { Id = invokeNodeId
              Kind = SemanticNodeKind.CapabilityInvoke(consoleNodeId, operationId, [ argument ]) }

        let functionEntity: SemanticFunction =
            { Id = functionId
              DisplayName = "report"
              Parameters = [ messageParameter; consoleParameter ]
              ResultType = unitType
              RequiredEffects = [ effectId ]
              Body =
                { Nodes = [ messageNode; consoleNode; invokeNode ]
                  ResultNodeId = invokeNodeId } }

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-console"
              ParentId = None
              Types = []
              Effects = [ effect ]
              Capabilities = [ capability ]
              Functions = [ functionEntity ]
              Obligations = [] }

        { Snapshot = snapshot
          EffectId = effectId
          CapabilityId = capabilityId
          OperationId = operationId
          OperationParameterId = operationParameterId
          FunctionId = functionId
          ConsoleParameterId = consoleParameterId
          MessageParameterId = messageParameterId
          ConsoleNodeId = consoleNodeId
          MessageNodeId = messageNodeId
          InvokeNodeId = invokeNodeId }

    [<Fact>]
    let ``typed Console invocation with declared effect is valid`` () =
        let fixture = createConsoleFixture ()
        Assert.Empty(SnapshotValidator.validate fixture.Snapshot)

    [<Fact>]
    let ``capability invocation requires its effect in the function contract`` () =
        let fixture = createConsoleFixture ()
        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity -> { functionEntity with RequiredEffects = [] })

        SnapshotValidator.validate { fixture.Snapshot with Functions = functions }
        |> requireIssue "MissingEffectRequirement"

    [<Fact>]
    let ``capability operation rejects a value of another capability`` () =
        let fixture = createConsoleFixture ()
        let otherCapabilityId = StableId.createForTesting<CapabilityTag> "capability-other"
        let otherCapability: SemanticCapability =
            { Id = otherCapabilityId
              DisplayName = "OtherCapability"
              Operations = [] }

        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                let parameters =
                    functionEntity.Parameters
                    |> List.map (fun parameter ->
                        if parameter.Id = fixture.ConsoleParameterId then
                            { parameter with Type = TypeRef.Capability otherCapabilityId }
                        else
                            parameter)
                { functionEntity with Parameters = parameters })

        let invalid =
            { fixture.Snapshot with
                Capabilities = fixture.Snapshot.Capabilities @ [ otherCapability ]
                Functions = functions }

        SnapshotValidator.validate invalid |> requireIssue "CapabilityValueTypeMismatch"

    [<Fact>]
    let ``capability arguments are checked by capability parameter identity and type`` () =
        let fixture = createConsoleFixture ()
        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                let parameters =
                    functionEntity.Parameters
                    |> List.map (fun parameter ->
                        if parameter.Id = fixture.MessageParameterId then
                            { parameter with Type = TypeRef.Primitive PrimitiveType.Bool }
                        else
                            parameter)
                { functionEntity with Parameters = parameters })

        SnapshotValidator.validate { fixture.Snapshot with Functions = functions }
        |> requireIssue "CapabilityArgumentTypeMismatch"

    [<Fact>]
    let ``capability invocation must bind the exact operation parameter set`` () =
        let fixture = createConsoleFixture ()
        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                let updateNode (node: SemanticNode) =
                    if node.Id <> fixture.InvokeNodeId then
                        node
                    else
                        match node.Kind with
                        | SemanticNodeKind.CapabilityInvoke(capabilityNodeId, operationId, _) ->
                            { node with Kind = SemanticNodeKind.CapabilityInvoke(capabilityNodeId, operationId, []) }
                        | _ -> node
                { functionEntity with Body = ValueGraph.mapNodes updateNode functionEntity.Body })

        SnapshotValidator.validate { fixture.Snapshot with Functions = functions }
        |> requireIssue "CapabilityArgumentSetMismatch"

    [<Fact>]
    let ``direct caller explicitly carries callee effects`` () =
        let fixture = createConsoleFixture ()
        let unitType = TypeRef.Primitive PrimitiveType.Unit
        let callee = List.exactlyOne fixture.Snapshot.Functions
        let callerId = StableId.createForTesting<FunctionTag> "function-caller"
        let callerNodeId = StableId.createForTesting<GraphNodeTag> "node-caller-call"

        let callerNode: SemanticNode =
            { Id = callerNodeId
              Kind = SemanticNodeKind.DirectCall(callee.Id, []) }

        let caller: SemanticFunction =
            { Id = callerId
              DisplayName = "caller"
              Parameters = []
              ResultType = unitType
              RequiredEffects = []
              Body =
                { Nodes = [ callerNode ]
                  ResultNodeId = callerNodeId } }

        let invalid = { fixture.Snapshot with Functions = fixture.Snapshot.Functions @ [ caller ] }
        let issues = SnapshotValidator.validate invalid
        requireIssue "MissingArgument" issues
        requireIssue "MissingEffectRequirement" issues

    [<Fact>]
    let ``string intrinsics have fixed explicit signatures`` () =
        let stringType = TypeRef.Primitive PrimitiveType.String
        let boolType = TypeRef.Primitive PrimitiveType.Bool
        let functionId = StableId.createForTesting<FunctionTag> "function-string-check"
        let valueParameterId = StableId.createForTesting<ParameterTag> "parameter-string-value"
        let substringParameterId = StableId.createForTesting<ParameterTag> "parameter-substring"
        let valueNodeId = StableId.createForTesting<GraphNodeTag> "node-string-value"
        let substringNodeId = StableId.createForTesting<GraphNodeTag> "node-substring"
        let containsNodeId = StableId.createForTesting<GraphNodeTag> "node-contains"

        let valueNode: SemanticNode =
            { Id = valueNodeId
              Kind = SemanticNodeKind.ParameterReference valueParameterId }
        let substringNode: SemanticNode =
            { Id = substringNodeId
              Kind = SemanticNodeKind.ParameterReference substringParameterId }
        let containsNode: SemanticNode =
            { Id = containsNodeId
              Kind = SemanticNodeKind.StringContains(valueNodeId, substringNodeId) }

        let functionEntity: SemanticFunction =
            { Id = functionId
              DisplayName = "contains"
              Parameters =
                [ { Id = valueParameterId; DisplayName = "value"; Type = stringType }
                  { Id = substringParameterId; DisplayName = "substring"; Type = stringType } ]
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes = [ valueNode; substringNode; containsNode ]
                  ResultNodeId = containsNodeId } }

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-string-check"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ functionEntity ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        let invalidFunctions =
            snapshot.Functions
            |> List.map (fun item ->
                let parameters =
                    item.Parameters
                    |> List.map (fun parameter ->
                        if parameter.Id = substringParameterId then
                            { parameter with Type = boolType }
                        else
                            parameter)
                { item with Parameters = parameters })

        SnapshotValidator.validate { snapshot with Functions = invalidFunctions }
        |> requireIssue "StringIntrinsicTypeMismatch"

    [<Fact>]
    let ``reference JSON codec round-trips capability semantics`` () =
        let fixture = createConsoleFixture ()
        let encoded =
            match SnapshotCodec.serialize fixture.Snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected serialization failure: %A" error
        let restored =
            match SnapshotCodec.deserialize encoded with
            | Ok value -> value
            | Error error -> failwithf "Unexpected deserialization failure: %A" error
        let reencoded =
            match SnapshotCodec.serialize restored with
            | Ok value -> value
            | Error error -> failwithf "Unexpected reserialization failure: %A" error

        Assert.Equal(encoded, reencoded)
        let capability = List.exactlyOne restored.Capabilities
        let operation = List.exactlyOne capability.Operations
        Assert.Equal(fixture.CapabilityId, capability.Id)
        Assert.Equal(fixture.OperationId, operation.Id)
        Assert.Equal(fixture.EffectId, operation.EffectId)
