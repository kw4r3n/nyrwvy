namespace Nyrwvy.AgentEval.ListMapStructural

open System
open System.Collections.Generic
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.AgentEval

/// Frozen evaluation protocol `m2-listmap-structural-v1` (freeze tag `eval/m2-listmap-condition-v1-20260831`, docs/operations/evaluation-freeze-tags.md).
/// Its review encoding intentionally duplicates the maintained wire encoders to preserve historical
/// replay; only extend it for exhaustiveness over new semantic cases, never refactor it onto shared code.
[<RequireQualifiedAccess>]
module ListMapStructuralProtocol =
    let version = "m2-listmap-structural-v1"

module private Input =
    exception ProtocolInputException of string

    let fail (message: string) = raise (ProtocolInputException message)

    let parseDocument (line: string): JsonDocument =
        try JsonDocument.Parse(line)
        with :? JsonException as error -> fail $"Invalid JSON: {error.Message}"

    let private propertyNames (element: JsonElement): Set<string> =
        if element.ValueKind <> JsonValueKind.Object then fail "Expected a JSON object."
        element.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq

    let ensureFields
        (context: string)
        (allowed: string list)
        (required: string list)
        (element: JsonElement) =
        let actual = propertyNames element
        let unknown = Set.difference actual (Set.ofList allowed) |> Set.toList |> List.sort
        let missing = Set.difference (Set.ofList required) actual |> Set.toList |> List.sort
        if not (List.isEmpty unknown) then
            let rendered = String.concat ", " unknown
            fail $"{context} contains unsupported properties: {rendered}."
        if not (List.isEmpty missing) then
            let rendered = String.concat ", " missing
            fail $"{context} is missing required properties: {rendered}."

    let property (name: string) (element: JsonElement): JsonElement =
        if element.ValueKind <> JsonValueKind.Object then fail "Expected a JSON object."
        match element.TryGetProperty(name) with
        | true, value -> value
        | false, _ -> fail $"Missing required property '{name}'."

    let requiredString (name: string) (element: JsonElement): string =
        let value = property name element
        if value.ValueKind <> JsonValueKind.String then fail $"Property '{name}' must be a JSON string."
        value.GetString()

    let stableId<'Kind> (label: string) (text: string): StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> fail $"{label} is not a valid stable ID: {error}."

    let listMapRequest (root: JsonElement): AgentReplaceFunctionBodyWithListMap =
        let fields =
            [ "command"
              "targetFunctionId"
              "expectedBaseSnapshotId"
              "expectedCurrentResultNodeId"
              "sourceParameterId"
              "mapperFunctionId" ]
        ensureFields "simulateListMapV1 command" fields fields root
        { TargetFunctionId = stableId<FunctionTag> "targetFunctionId" (requiredString "targetFunctionId" root)
          ExpectedBaseSnapshotId = stableId<SnapshotTag> "expectedBaseSnapshotId" (requiredString "expectedBaseSnapshotId" root)
          ExpectedCurrentResultNodeId = stableId<GraphNodeTag> "expectedCurrentResultNodeId" (requiredString "expectedCurrentResultNodeId" root)
          SourceParameterId = stableId<ParameterTag> "sourceParameterId" (requiredString "sourceParameterId" root)
          MapperFunctionId = stableId<FunctionTag> "mapperFunctionId" (requiredString "mapperFunctionId" root) }

module private Encode =
    open ArtifactJson

    let private optional (value: 'a option) (encode: 'a -> obj): obj =
        match value with
        | Some item -> encode item
        | None -> null

    let private primitiveName = function
        | PrimitiveType.String -> "String"
        | PrimitiveType.Bool -> "Bool"
        | PrimitiveType.Unit -> "Unit"
        | PrimitiveType.Int64 -> "Int64"
        | PrimitiveType.Float64 -> "Float64"
        | PrimitiveType.Bits32 -> "Bits32"

    let private genericName = function
        | BuiltinGenericType.Option -> "Option"
        | BuiltinGenericType.Result -> "Result"
        | BuiltinGenericType.Map -> "Map"
        | BuiltinGenericType.List -> "List"
        | BuiltinGenericType.Set -> "Set"
        | BuiltinGenericType.Task -> "Task"
        | BuiltinGenericType.TaskScope -> "TaskScope"

    let rec typeRef = function
        | TypeRef.Primitive primitive ->
            objectValue [ "kind", box "primitive"; "name", box (primitiveName primitive) ]
        | TypeRef.Named id ->
            objectValue [ "kind", box "named"; "id", box (StableId.value id) ]
        | TypeRef.Capability id ->
            objectValue [ "kind", box "capability"; "id", box (StableId.value id) ]
        | TypeRef.TypeParameter(id, name) ->
            objectValue [ "kind", box "typeParameter"; "id", box (StableId.value id); "name", box name ]
        | TypeRef.Function(parameters, resultType) ->
            objectValue
                [ "kind", box "function"
                  "parameters", parameters |> List.map typeRef |> arrayValue
                  "resultType", typeRef resultType ]
        | TypeRef.BuiltinGeneric(kind, arguments) ->
            objectValue
                [ "kind", box "builtinGeneric"
                  "name", box (genericName kind)
                  "arguments", arguments |> List.map typeRef |> arrayValue ]

    let parameter (value: SemanticParameter) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "type", typeRef value.Type ]

    let functionEntity (value: SemanticFunction) =
        let nodes =
            ValueGraph.collectNodes value.Body
            |> List.map (fun node ->
                objectValue
                    [ "id", box (StableId.value node.Id)
                      "kind", box (sprintf "%A" node.Kind) ])
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map parameter |> arrayValue
              "resultType", typeRef value.ResultType
              "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arrayValue
              "body",
                objectValue
                    [ "nodes", arrayValue nodes
                      "resultNodeId", box (StableId.value value.Body.ResultNodeId) ] ]

    let private callSite (value: QueryCallSite) =
        objectValue
            [ "callerFunctionId", box (StableId.value value.CallerFunctionId)
              "callNodeId", box (StableId.value value.CallNodeId)
              "calleeFunctionId", box (StableId.value value.CalleeFunctionId) ]

    let focus (value: FunctionFocusView) =
        objectValue
            [ "snapshotId", box (StableId.value value.SnapshotId)
              "function", functionEntity value.Function
              "nodeTypes",
                value.NodeTypes
                |> List.map (fun node ->
                    objectValue
                        [ "nodeId", box (StableId.value node.NodeId)
                          "type", optional node.Type typeRef ])
                |> arrayValue
              "callers", value.Callers |> List.map callSite |> arrayValue
              "callees", value.Callees |> List.map callSite |> arrayValue
              "openObligations",
                value.OpenObligations
                |> List.map (fun obligation ->
                    objectValue
                        [ "obligationId", box (StableId.value obligation.ObligationId)
                          "callNodeId", box (StableId.value obligation.CallNodeId)
                          "parameterId", box (StableId.value obligation.ParameterId) ])
                |> arrayValue ]

    let readiness = function
        | TransactionReadiness.SemanticallyReady -> objectValue [ "kind", box "semanticallyReady" ]
        | TransactionReadiness.Incomplete ids ->
            objectValue
                [ "kind", box "incomplete"
                  "obligationIds", ids |> List.map (StableId.value >> box) |> arrayValue ]

    let request (value: AgentReplaceFunctionBodyWithListMap) =
        objectValue
            [ "targetFunctionId", box (StableId.value value.TargetFunctionId)
              "expectedBaseSnapshotId", box (StableId.value value.ExpectedBaseSnapshotId)
              "expectedCurrentResultNodeId", box (StableId.value value.ExpectedCurrentResultNodeId)
              "sourceParameterId", box (StableId.value value.SourceParameterId)
              "mapperFunctionId", box (StableId.value value.MapperFunctionId) ]

    let nodeSummary (value: StructuralNodeSummary) =
        objectValue
            [ "nodeId", box (StableId.value value.NodeId)
              "role", box value.Role
              "type", optional value.Type typeRef
              "dependencies", value.Dependencies |> List.map (StableId.value >> box) |> arrayValue
              "parameterId", optional value.ParameterId (StableId.value >> box)
              "functionId", optional value.FunctionId (StableId.value >> box)
              "float64Bits", optional value.Float64BitsHex box ]

    let diff (value: FunctionBodyReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "oldResultNodeId", box (StableId.value value.OldResultNodeId)
              "newResultNodeId", box (StableId.value value.NewResultNodeId)
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arrayValue
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arrayValue ]

    let receipt (value: TransactionPreviewReceipt) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.BaseSnapshotFingerprint)
              "proposedSnapshotId", box (StableId.value value.ProposedSnapshotId)
              "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.ProposedSnapshotFingerprint)
              "proposedSemanticFingerprint", box (SemanticFingerprint.value value.ProposedSemanticFingerprint) ]

    let review (value: AgentListMapStructuralChangeReview) =
        objectValue
            [ "requestedChange", request value.RequestedChange
              "receipt", receipt value.Receipt
              "readiness", readiness value.Readiness
              "resultingChange", diff value.ResultingChange
              "baseFocus", focus value.BaseFocus
              "proposedFocus", focus value.ProposedFocus ]

    let publication (value: PublishedStructuralTransaction) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "publishedSnapshotId", box (StableId.value value.PublishedSnapshotId)
              "diff", diff value.Diff
              "readiness", readiness value.Readiness
              "receipt", receipt value.Receipt ]

    let diagnostics values =
        values
        |> List.map (fun (diagnostic: TransactionDiagnostic) ->
            objectValue [ "code", box diagnostic.Code; "message", box diagnostic.Message ])
        |> arrayValue

    let description () =
        objectValue
            [ "status", box "ok"
              "protocolVersion", box ListMapStructuralProtocol.version
              "commands",
                [ objectValue [ "command", box "describeV1" ]
                  objectValue [ "command", box "stateV1" ]
                  objectValue
                      [ "command", box "focusFunctionV1"
                        "fields", arrayValue [ box "functionId" ] ]
                  objectValue
                      [ "command", box "simulateListMapV1"
                        "fields",
                            arrayValue
                                [ box "targetFunctionId"
                                  box "expectedBaseSnapshotId"
                                  box "expectedCurrentResultNodeId"
                                  box "sourceParameterId"
                                  box "mapperFunctionId" ] ]
                  objectValue
                      [ "command", box "commitListMapV1"
                        "fields", arrayValue [ box "proposalHandle" ] ]
                  objectValue
                      [ "command", box "rejectListMapV1"
                        "fields", arrayValue [ box "proposalHandle" ] ]
                  objectValue [ "command", box "metricsV1" ] ]
                |> arrayValue
              "operation",
                objectValue
                    [ "kind", box "completeFunctionBodyListMap"
                      "materializedRoles", arrayValue [ box "ParameterReference"; box "ListMap" ]
                      "source", box "target-owned List<T> parameter"
                      "mapper", box "existing pure unary FunctionId compatible with T -> R" ]
              "authority",
                objectValue
                    [ "newGraphNodeIds", box "system-owned"
                      "rawSnapshotPayload", box false
                      "rawValueGraphPayload", box false
                      "rawSemanticNodePayload", box false
                      "genericExpressionRecipe", box false
                      "lambdaClosureConstruction", box false
                      "effectfulCallbackAuthority", box false ] ]

type ListMapStructuralSessionUsage =
    { ToolCalls: int
      FocusQueries: int
      Simulations: int
      CommitAttempts: int
      Commits: int
      Rejects: int
      ProtocolErrors: int }

type ListMapStructuralSession internal (initialWorkspace: AgentWorkspace) =
    let mutable workspace = initialWorkspace
    let mutable proposals: Map<string, AgentListMapStructuralChangeProposal> = Map.empty
    let mutable lastPublication: PublishedStructuralTransaction option = None
    let mutable nextProposalNumber = 1
    let mutable usage =
        { ToolCalls = 0
          FocusQueries = 0
          Simulations = 0
          CommitAttempts = 0
          Commits = 0
          Rejects = 0
          ProtocolErrors = 0 }

    let proposalHandle () =
        let handle = $"listmap-proposal-{nextProposalNumber}"
        nextProposalNumber <- nextProposalNumber + 1
        handle

    let requireProposal handle =
        match Map.tryFind handle proposals with
        | Some proposal -> proposal
        | None -> Input.fail $"Unknown ListMap proposal handle '{handle}'."

    let isTerminalCommitRejection (diagnostics: TransactionDiagnostic list) =
        diagnostics
        |> List.exists (fun diagnostic ->
            match diagnostic.Code with
            | "StaleBaseSnapshot"
            | "PreviewReceiptBaseMismatch"
            | "PreviewReceiptResultMismatch" -> true
            | _ -> false)

    let metrics () =
        ArtifactJson.objectValue
            [ "toolCalls", box usage.ToolCalls
              "focusQueries", box usage.FocusQueries
              "simulations", box usage.Simulations
              "commitAttempts", box usage.CommitAttempts
              "commits", box usage.Commits
              "rejects", box usage.Rejects
              "protocolErrors", box usage.ProtocolErrors
              "pendingProposalCount", box proposals.Count ]

    let state () =
        let snapshotFingerprint =
            AgentWorkspace.currentSnapshotFingerprint workspace
            |> function
                | Ok value -> box (SnapshotFingerprint.value value)
                | Error error -> box $"error:{error}"
        let semanticFingerprint =
            AgentWorkspace.currentSemanticFingerprint workspace
            |> function
                | Ok value -> box (SemanticFingerprint.value value)
                | Error error -> box $"error:{error}"
        ArtifactJson.objectValue
            [ "status", box "ok"
              "protocolVersion", box ListMapStructuralProtocol.version
              "snapshotId", box (StableId.value (AgentWorkspace.currentSnapshotId workspace))
              "snapshotFingerprint", snapshotFingerprint
              "semanticFingerprint", semanticFingerprint
              "proposalHandles", proposals |> Map.toList |> List.map (fst >> box) |> ArtifactJson.arrayValue
              "metrics", metrics () ]

    let execute (root: JsonElement) =
        let command = Input.requiredString "command" root
        match command with
        | "describeV1" ->
            Input.ensureFields "describeV1 command" [ "command" ] [ "command" ] root
            Encode.description ()
        | "stateV1" ->
            Input.ensureFields "stateV1 command" [ "command" ] [ "command" ] root
            state ()
        | "focusFunctionV1" ->
            Input.ensureFields
                "focusFunctionV1 command"
                [ "command"; "functionId" ]
                [ "command"; "functionId" ]
                root
            usage <- { usage with FocusQueries = usage.FocusQueries + 1 }
            let functionId =
                Input.stableId<FunctionTag> "functionId" (Input.requiredString "functionId" root)
            match AgentWorkspace.focusFunction workspace functionId with
            | Ok focus ->
                ArtifactJson.objectValue
                    [ "status", box "ok"
                      "protocolVersion", box ListMapStructuralProtocol.version
                      "value", Encode.focus focus ]
            | Error error ->
                ArtifactJson.objectValue
                    [ "status", box "error"
                      "protocolVersion", box ListMapStructuralProtocol.version
                      "errorKind", box "query"
                      "detail", box (sprintf "%A" error) ]
        | "simulateListMapV1" ->
            usage <- { usage with Simulations = usage.Simulations + 1 }
            let request = Input.listMapRequest root
            match AgentListMapStructuralWorkflow.simulate workspace request with
            | Ok proposal ->
                let handle = proposalHandle ()
                proposals <- Map.add handle proposal proposals
                ArtifactJson.objectValue
                    [ "status", box "proposed"
                      "protocolVersion", box ListMapStructuralProtocol.version
                      "proposalHandle", box handle
                      "transactionId", box (StableId.value (AgentListMapStructuralChangeProposal.transactionId proposal))
                      "proposedSnapshotId", box (StableId.value (AgentListMapStructuralChangeProposal.proposedSnapshotId proposal))
                      "review", Encode.review (AgentListMapStructuralChangeProposal.review proposal) ]
            | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics) ->
                ArtifactJson.objectValue
                    [ "status", box "rejected"
                      "protocolVersion", box ListMapStructuralProtocol.version
                      "diagnostics", Encode.diagnostics diagnostics ]
            | Error error ->
                ArtifactJson.objectValue
                    [ "status", box "error"
                      "protocolVersion", box ListMapStructuralProtocol.version
                      "errorKind", box "workflow"
                      "detail", box (sprintf "%A" error) ]
        | "commitListMapV1" ->
            Input.ensureFields
                "commitListMapV1 command"
                [ "command"; "proposalHandle" ]
                [ "command"; "proposalHandle" ]
                root
            usage <- { usage with CommitAttempts = usage.CommitAttempts + 1 }
            let handle = Input.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            match AgentListMapStructuralWorkflow.commit workspace proposal with
            | AgentStructuralCommitResult.Rejected diagnostics ->
                if isTerminalCommitRejection diagnostics then
                    proposals <- Map.remove handle proposals
                ArtifactJson.objectValue
                    [ "status", box "rejected"
                      "protocolVersion", box ListMapStructuralProtocol.version
                      "proposalHandle", box handle
                      "diagnostics", Encode.diagnostics diagnostics ]
            | AgentStructuralCommitResult.Committed(nextWorkspace, publication) ->
                workspace <- nextWorkspace
                lastPublication <- Some publication
                proposals <- Map.remove handle proposals
                usage <- { usage with Commits = usage.Commits + 1 }
                ArtifactJson.objectValue
                    [ "status", box "committed"
                      "protocolVersion", box ListMapStructuralProtocol.version
                      "proposalHandle", box handle
                      "publication", Encode.publication publication ]
        | "rejectListMapV1" ->
            Input.ensureFields
                "rejectListMapV1 command"
                [ "command"; "proposalHandle" ]
                [ "command"; "proposalHandle" ]
                root
            let handle = Input.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            let nextWorkspace, receipt = AgentListMapStructuralWorkflow.reject workspace proposal
            workspace <- nextWorkspace
            proposals <- Map.remove handle proposals
            usage <- { usage with Rejects = usage.Rejects + 1 }
            ArtifactJson.objectValue
                [ "status", box "rejectedByAgent"
                  "protocolVersion", box ListMapStructuralProtocol.version
                  "proposalHandle", box handle
                  "transactionId", box (StableId.value receipt.TransactionId)
                  "retainedSnapshotId", box (StableId.value receipt.RetainedSnapshotId) ]
        | "metricsV1" ->
            Input.ensureFields "metricsV1 command" [ "command" ] [ "command" ] root
            ArtifactJson.objectValue
                [ "status", box "ok"
                  "protocolVersion", box ListMapStructuralProtocol.version
                  "metrics", metrics () ]
        | other ->
            Input.fail $"Unsupported command '{other}' for protocol {ListMapStructuralProtocol.version}."

    member _.Usage = usage
    member internal _.CurrentWorkspace = workspace
    member internal _.ReplaceWorkspace(nextWorkspace: AgentWorkspace) = workspace <- nextWorkspace
    member internal _.LastPublication = lastPublication
    member internal _.PendingProposalCount = proposals.Count

    member _.ExecuteLine(line: string): string =
        lastPublication <- None
        usage <- { usage with ToolCalls = usage.ToolCalls + 1 }
        try
            if String.IsNullOrWhiteSpace line then Input.fail "Input line must contain one JSON command."
            use document = Input.parseDocument line
            ArtifactJson.objectValue [ "ok", box true; "result", execute document.RootElement ]
            |> ArtifactJson.serialize
        with Input.ProtocolInputException message ->
            usage <- { usage with ProtocolErrors = usage.ProtocolErrors + 1 }
            ArtifactJson.objectValue
                [ "ok", box false
                  "error",
                    ArtifactJson.objectValue
                        [ "code", box "ProtocolError"
                          "message", box message ] ]
            |> ArtifactJson.serialize

[<RequireQualifiedAccess>]
module ListMapStructuralSession =
    let create (snapshot: Snapshot): Result<ListMapStructuralSession, ValidationIssue list> =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> Ok(ListMapStructuralSession workspace)
        | Error issues -> Error issues