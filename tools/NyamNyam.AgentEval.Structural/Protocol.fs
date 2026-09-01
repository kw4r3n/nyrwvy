namespace NyamNyam.AgentEval.Structural

open System
open System.Collections.Generic
open System.Text.Json
open NyamNyam.Core

[<RequireQualifiedAccess>]
module StructuralProtocol =
    let version = "m2-structural-v1"

module private Json =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arrayValue (items: obj list): obj = items |> List.toArray |> box
    let serialize (value: obj): string = JsonSerializer.Serialize(value)

module private Input =
    exception ProtocolInputException of string

    let fail message = raise (ProtocolInputException message)

    let parseDocument (line: string): JsonDocument =
        try JsonDocument.Parse(line)
        with :? JsonException as error -> fail $"Invalid JSON: {error.Message}"

    let private propertyNames (element: JsonElement): Set<string> =
        if element.ValueKind <> JsonValueKind.Object then fail "Expected a JSON object."
        element.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq

    let ensureFields (context: string) (allowed: string list) (required: string list) (element: JsonElement) =
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

    let rec recipe (element: JsonElement): Float64ExpressionRecipe =
        let binary context constructor =
            ensureFields context [ "kind"; "left"; "right" ] [ "kind"; "left"; "right" ] element
            constructor (recipe (property "left" element), recipe (property "right" element))

        let kind = requiredString "kind" element
        match kind with
        | "parameter" ->
            ensureFields "parameter recipe" [ "kind"; "parameterId" ] [ "kind"; "parameterId" ] element
            Float64ExpressionRecipe.Parameter(
                stableId<ParameterTag> "replacement.parameterId" (requiredString "parameterId" element)
            )
        | "literal" ->
            ensureFields "literal recipe" [ "kind"; "bits" ] [ "kind"; "bits" ] element
            match Float64Bits.tryParseHex (requiredString "bits" element) with
            | Ok bits -> Float64ExpressionRecipe.Literal bits
            | Error error -> fail $"replacement.bits is invalid: {error}"
        | "add" -> binary "add recipe" Float64ExpressionRecipe.Add
        | "subtract" -> binary "subtract recipe" Float64ExpressionRecipe.Subtract
        | "multiply" -> binary "multiply recipe" Float64ExpressionRecipe.Multiply
        | "divide" -> binary "divide recipe" Float64ExpressionRecipe.Divide
        | other -> fail $"Unsupported Float64 recipe kind '{other}'."

    let structuralRequest (root: JsonElement): AgentReplaceFunctionBodyWithFloat64Recipe =
        ensureFields
            "simulateStructuralV1 command"
            [ "command"; "targetFunctionId"; "expectedBaseSnapshotId"; "expectedCurrentResultNodeId"; "replacement" ]
            [ "command"; "targetFunctionId"; "expectedBaseSnapshotId"; "expectedCurrentResultNodeId"; "replacement" ]
            root
        { TargetFunctionId = stableId<FunctionTag> "targetFunctionId" (requiredString "targetFunctionId" root)
          ExpectedBaseSnapshotId = stableId<SnapshotTag> "expectedBaseSnapshotId" (requiredString "expectedBaseSnapshotId" root)
          ExpectedCurrentResultNodeId = stableId<GraphNodeTag> "expectedCurrentResultNodeId" (requiredString "expectedCurrentResultNodeId" root)
          Replacement = recipe (property "replacement" root) }

module private Encode =
    open Json

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

    let private genericName = function
        | BuiltinGenericType.Option -> "Option"
        | BuiltinGenericType.Result -> "Result"
        | BuiltinGenericType.List -> "List"

    let rec typeRef = function
        | TypeRef.Primitive primitive -> objectValue [ "kind", box "primitive"; "name", box (primitiveName primitive) ]
        | TypeRef.Named id -> objectValue [ "kind", box "named"; "id", box (StableId.value id) ]
        | TypeRef.Capability id -> objectValue [ "kind", box "capability"; "id", box (StableId.value id) ]
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
              "body", objectValue [ "nodes", arrayValue nodes; "resultNodeId", box (StableId.value value.Body.ResultNodeId) ] ]

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

    let rec recipe = function
        | Float64ExpressionRecipe.Parameter parameterId ->
            objectValue [ "kind", box "parameter"; "parameterId", box (StableId.value parameterId) ]
        | Float64ExpressionRecipe.Literal bits ->
            objectValue [ "kind", box "literal"; "bits", box (Float64Bits.toHex bits) ]
        | Float64ExpressionRecipe.Add(left, right) ->
            objectValue [ "kind", box "add"; "left", recipe left; "right", recipe right ]
        | Float64ExpressionRecipe.Subtract(left, right) ->
            objectValue [ "kind", box "subtract"; "left", recipe left; "right", recipe right ]
        | Float64ExpressionRecipe.Multiply(left, right) ->
            objectValue [ "kind", box "multiply"; "left", recipe left; "right", recipe right ]
        | Float64ExpressionRecipe.Divide(left, right) ->
            objectValue [ "kind", box "divide"; "left", recipe left; "right", recipe right ]

    let request (value: AgentReplaceFunctionBodyWithFloat64Recipe) =
        objectValue
            [ "targetFunctionId", box (StableId.value value.TargetFunctionId)
              "expectedBaseSnapshotId", box (StableId.value value.ExpectedBaseSnapshotId)
              "expectedCurrentResultNodeId", box (StableId.value value.ExpectedCurrentResultNodeId)
              "replacement", recipe value.Replacement ]

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

    let review (value: AgentStructuralChangeReview) =
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

    let diagnostics (values: TransactionDiagnostic list) =
        values
        |> List.map (fun diagnostic -> objectValue [ "code", box diagnostic.Code; "message", box diagnostic.Message ])
        |> arrayValue

    let description () =
        objectValue
            [ "status", box "ok"
              "protocolVersion", box StructuralProtocol.version
              "commands",
                [ objectValue [ "command", box "describeV1" ]
                  objectValue [ "command", box "stateV1" ]
                  objectValue [ "command", box "focusFunctionV1"; "fields", arrayValue [ box "functionId" ] ]
                  objectValue
                      [ "command", box "simulateStructuralV1"
                        "fields",
                            arrayValue
                                [ box "targetFunctionId"
                                  box "expectedBaseSnapshotId"
                                  box "expectedCurrentResultNodeId"
                                  box "replacement" ] ]
                  objectValue [ "command", box "commitStructuralV1"; "fields", arrayValue [ box "proposalHandle" ] ]
                  objectValue [ "command", box "commitStructuralAllowIncompleteV1"; "fields", arrayValue [ box "proposalHandle" ] ]
                  objectValue [ "command", box "rejectStructuralV1"; "fields", arrayValue [ box "proposalHandle" ] ]
                  objectValue [ "command", box "metricsV1" ] ]
                |> arrayValue
              "recipeCases",
                [ objectValue [ "kind", box "parameter"; "fields", arrayValue [ box "parameterId" ] ]
                  objectValue
                      [ "kind", box "literal"
                        "fields", arrayValue [ box "bits" ]
                        "bitsFormat", box "16 hexadecimal digits; NaN canonicalizes semantically" ]
                  objectValue [ "kind", box "add"; "fields", arrayValue [ box "left"; box "right" ] ]
                  objectValue [ "kind", box "subtract"; "fields", arrayValue [ box "left"; box "right" ] ]
                  objectValue [ "kind", box "multiply"; "fields", arrayValue [ box "left"; box "right" ] ]
                  objectValue [ "kind", box "divide"; "fields", arrayValue [ box "left"; box "right" ] ] ]
                |> arrayValue
              "authority",
                objectValue
                    [ "newGraphNodeIds", box "system-owned"
                      "rawSnapshotPayload", box false
                      "rawValueGraphPayload", box false
                      "rawSemanticNodePayload", box false ] ]

type StructuralSessionUsage =
    { ToolCalls: int
      FocusQueries: int
      Simulations: int
      CommitAttempts: int
      Commits: int
      Rejects: int
      ProtocolErrors: int }

type StructuralSession internal (initialWorkspace: AgentWorkspace) =
    let mutable workspace = initialWorkspace
    let mutable proposals: Map<string, AgentStructuralChangeProposal> = Map.empty
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
        let handle = $"structural-proposal-{nextProposalNumber}"
        nextProposalNumber <- nextProposalNumber + 1
        handle

    let requireProposal handle =
        match Map.tryFind handle proposals with
        | Some proposal -> proposal
        | None -> Input.fail $"Unknown structural proposal handle '{handle}'."

    let isTerminalCommitRejection (diagnostics: TransactionDiagnostic list) =
        diagnostics
        |> List.exists (fun diagnostic ->
            match diagnostic.Code with
            | "StaleBaseSnapshot"
            | "PreviewReceiptBaseMismatch"
            | "PreviewReceiptResultMismatch" -> true
            | _ -> false)

    let metrics () =
        Json.objectValue
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
        Json.objectValue
            [ "status", box "ok"
              "protocolVersion", box StructuralProtocol.version
              "snapshotId", box (StableId.value (AgentWorkspace.currentSnapshotId workspace))
              "snapshotFingerprint", snapshotFingerprint
              "semanticFingerprint", semanticFingerprint
              "proposalHandles", proposals |> Map.toList |> List.map (fst >> box) |> Json.arrayValue
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
            Input.ensureFields "focusFunctionV1 command" [ "command"; "functionId" ] [ "command"; "functionId" ] root
            usage <- { usage with FocusQueries = usage.FocusQueries + 1 }
            let functionId = Input.stableId<FunctionTag> "functionId" (Input.requiredString "functionId" root)
            match AgentWorkspace.focusFunction workspace functionId with
            | Ok focus ->
                Json.objectValue
                    [ "status", box "ok"
                      "protocolVersion", box StructuralProtocol.version
                      "value", Encode.focus focus ]
            | Error error ->
                Json.objectValue
                    [ "status", box "error"
                      "protocolVersion", box StructuralProtocol.version
                      "errorKind", box "query"
                      "detail", box (sprintf "%A" error) ]
        | "simulateStructuralV1" ->
            usage <- { usage with Simulations = usage.Simulations + 1 }
            let request = Input.structuralRequest root
            match AgentStructuralWorkflow.simulate workspace request with
            | Ok proposal ->
                let handle = proposalHandle ()
                proposals <- Map.add handle proposal proposals
                Json.objectValue
                    [ "status", box "proposed"
                      "protocolVersion", box StructuralProtocol.version
                      "proposalHandle", box handle
                      "transactionId", box (StableId.value (AgentStructuralChangeProposal.transactionId proposal))
                      "proposedSnapshotId", box (StableId.value (AgentStructuralChangeProposal.proposedSnapshotId proposal))
                      "review", Encode.review (AgentStructuralChangeProposal.review proposal) ]
            | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics) ->
                Json.objectValue
                    [ "status", box "rejected"
                      "protocolVersion", box StructuralProtocol.version
                      "diagnostics", Encode.diagnostics diagnostics ]
            | Error error ->
                Json.objectValue
                    [ "status", box "error"
                      "protocolVersion", box StructuralProtocol.version
                      "errorKind", box "workflow"
                      "detail", box (sprintf "%A" error) ]
        | ("commitStructuralV1" | "commitStructuralAllowIncompleteV1") as commitCommand ->
            Input.ensureFields (commitCommand + " command") [ "command"; "proposalHandle" ] [ "command"; "proposalHandle" ] root
            usage <- { usage with CommitAttempts = usage.CommitAttempts + 1 }
            let handle = Input.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            let result =
                if commitCommand = "commitStructuralAllowIncompleteV1" then
                    AgentStructuralWorkflow.commitWithPolicy workspace proposal CommitPolicy.AllowBlockingObligations
                else
                    AgentStructuralWorkflow.commit workspace proposal
            match result with
            | AgentStructuralCommitResult.Rejected diagnostics ->
                if isTerminalCommitRejection diagnostics then
                    proposals <- Map.remove handle proposals
                Json.objectValue
                    [ "status", box "rejected"
                      "protocolVersion", box StructuralProtocol.version
                      "proposalHandle", box handle
                      "diagnostics", Encode.diagnostics diagnostics ]
            | AgentStructuralCommitResult.Committed(nextWorkspace, publication) ->
                workspace <- nextWorkspace
                lastPublication <- Some publication
                proposals <- Map.remove handle proposals
                usage <- { usage with Commits = usage.Commits + 1 }
                Json.objectValue
                    [ "status", box "committed"
                      "protocolVersion", box StructuralProtocol.version
                      "proposalHandle", box handle
                      "publication", Encode.publication publication ]
        | "rejectStructuralV1" ->
            Input.ensureFields "rejectStructuralV1 command" [ "command"; "proposalHandle" ] [ "command"; "proposalHandle" ] root
            let handle = Input.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            let nextWorkspace, receipt = AgentStructuralWorkflow.reject workspace proposal
            workspace <- nextWorkspace
            proposals <- Map.remove handle proposals
            usage <- { usage with Rejects = usage.Rejects + 1 }
            Json.objectValue
                [ "status", box "rejectedByAgent"
                  "protocolVersion", box StructuralProtocol.version
                  "proposalHandle", box handle
                  "transactionId", box (StableId.value receipt.TransactionId)
                  "retainedSnapshotId", box (StableId.value receipt.RetainedSnapshotId) ]
        | "metricsV1" ->
            Input.ensureFields "metricsV1 command" [ "command" ] [ "command" ] root
            Json.objectValue
                [ "status", box "ok"
                  "protocolVersion", box StructuralProtocol.version
                  "metrics", metrics () ]
        | other -> Input.fail $"Unsupported command '{other}' for protocol {StructuralProtocol.version}."

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
            Json.objectValue [ "ok", box true; "result", execute document.RootElement ] |> Json.serialize
        with Input.ProtocolInputException message ->
            usage <- { usage with ProtocolErrors = usage.ProtocolErrors + 1 }
            Json.objectValue
                [ "ok", box false
                  "error", Json.objectValue [ "code", box "ProtocolError"; "message", box message ] ]
            |> Json.serialize

[<RequireQualifiedAccess>]
module StructuralSession =
    let create (snapshot: Snapshot): Result<StructuralSession, ValidationIssue list> =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> Ok(StructuralSession workspace)
        | Error issues -> Error issues
