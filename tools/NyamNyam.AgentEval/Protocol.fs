namespace NyamNyam.AgentEval

open System
open System.Collections.Generic
open System.Text.Json
open NyamNyam.Core

type StalePerturbation =
    { TypeId: TypeId
      NewDisplayName: string }

type SessionConfig =
    { StalePerturbation: StalePerturbation option }

[<RequireQualifiedAccess>]
module SessionConfig =
    let defaultValue = { StalePerturbation = None }

module private JsonOutput =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arrayValue (items: obj list): obj =
        items |> List.toArray |> box

    let strings (items: string list): obj =
        items |> List.map box |> arrayValue

    let serialize (value: obj): string =
        JsonSerializer.Serialize(value)

module private JsonInput =
    exception ProtocolInputException of string

    let private fail message = raise (ProtocolInputException message)

    let private property (name: string) (element: JsonElement): JsonElement =
        if element.ValueKind <> JsonValueKind.Object then
            fail "Expected a JSON object."

        element.EnumerateObject()
        |> Seq.tryFind (fun candidate -> String.Equals(candidate.Name, name, StringComparison.Ordinal))
        |> Option.map (fun candidate -> candidate.Value)
        |> Option.defaultWith (fun () -> fail $"Missing required property '{name}'.")

    let requiredString name element =
        let value = property name element
        if value.ValueKind <> JsonValueKind.String then
            fail $"Property '{name}' must be a JSON string."
        value.GetString()

    let requiredArray name element =
        let value = property name element
        if value.ValueKind <> JsonValueKind.Array then
            fail $"Property '{name}' must be a JSON array."
        value.EnumerateArray() |> Seq.toList

    let stableId<'Kind> label (text: string): StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> fail $"{label} is not a valid stable ID: {error}."

    let private primitiveType (name: string) =
        match name with
        | "String" -> PrimitiveType.String
        | "Bool" -> PrimitiveType.Bool
        | "Unit" -> PrimitiveType.Unit
        | "Int64" -> PrimitiveType.Int64
        | "Float64" -> PrimitiveType.Float64
        | _ -> fail $"Unsupported primitive type '{name}'."

    let rec typeRef (element: JsonElement): TypeRef =
        match requiredString "kind" element with
        | "primitive" -> TypeRef.Primitive(primitiveType (requiredString "name" element))
        | "named" -> TypeRef.Named(stableId<TypeTag> "type.id" (requiredString "id" element))
        | "capability" ->
            TypeRef.Capability(stableId<CapabilityTag> "capability.id" (requiredString "id" element))
        | "list" -> TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ typeRef (property "item" element) ])
        | "option" -> TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ typeRef (property "item" element) ])
        | "result" ->
            TypeRef.BuiltinGeneric(
                BuiltinGenericType.Result,
                [ typeRef (property "ok" element); typeRef (property "error" element) ]
            )
        | kind -> fail $"Unsupported type-ref kind '{kind}'."

    let private callPlan (element: JsonElement): AgentCallPlan =
        let callNodeId = stableId<GraphNodeTag> "callSitePlan.callNodeId" (requiredString "callNodeId" element)
        match requiredString "kind" element with
        | "bindExistingValue" ->
            AgentCallPlan.BindExistingValue(
                callNodeId,
                stableId<GraphNodeTag> "callSitePlan.valueNodeId" (requiredString "valueNodeId" element)
            )
        | "leaveMissing" -> AgentCallPlan.LeaveMissing callNodeId
        | kind -> fail $"Unsupported call-site plan kind '{kind}'."

    let operation (element: JsonElement): AgentOperationIntent =
        match requiredString "kind" element with
        | "renameFunction" ->
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Function(
                    stableId<FunctionTag> "operation.functionId" (requiredString "functionId" element)
                ),
                requiredString "newDisplayName" element
            )
        | "renameType" ->
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Type(stableId<TypeTag> "operation.typeId" (requiredString "typeId" element)),
                requiredString "newDisplayName" element
            )
        | "addFunctionParameter" ->
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId =
                    stableId<FunctionTag> "operation.functionId" (requiredString "functionId" element)
                  NewParameterDisplayName = requiredString "displayName" element
                  NewParameterType = typeRef (property "type" element)
                  CallSitePlans = requiredArray "callSitePlans" element |> List.map callPlan }
        | "resolveMissingArgument" ->
            AgentOperationIntent.ResolveMissingArgument
                { TargetObligationId =
                    stableId<ObligationTag> "operation.obligationId" (requiredString "obligationId" element)
                  ResolutionValueNodeId =
                    stableId<GraphNodeTag> "operation.valueNodeId" (requiredString "valueNodeId" element) }
        | kind -> fail $"Unsupported operation kind '{kind}'."

    let parseDocument (line: string): JsonDocument =
        try
            JsonDocument.Parse(line)
        with
        | :? JsonException as error -> fail $"Invalid JSON: {error.Message}"

module private Encode =
    open JsonOutput

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
        | TypeRef.Primitive primitive ->
            objectValue [ "kind", box "primitive"; "name", box (primitiveName primitive) ]
        | TypeRef.Named typeId ->
            objectValue [ "kind", box "named"; "id", box (StableId.value typeId) ]
        | TypeRef.Capability capabilityId ->
            objectValue [ "kind", box "capability"; "id", box (StableId.value capabilityId) ]
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

    let semanticType (value: SemanticType) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "kind", box (sprintf "%A" value.Kind) ]

    let callSite (value: QueryCallSite) =
        objectValue
            [ "callerFunctionId", box (StableId.value value.CallerFunctionId)
              "callNodeId", box (StableId.value value.CallNodeId)
              "calleeFunctionId", box (StableId.value value.CalleeFunctionId) ]

    let obligation (value: QueryOpenObligation) =
        objectValue
            [ "obligationId", box (StableId.value value.ObligationId)
              "originTransactionId", box (StableId.value value.OriginTransactionId)
              "callNodeId", box (StableId.value value.CallNodeId)
              "parameterId", box (StableId.value value.ParameterId) ]

    let signature (value: QueryFunctionSignature) =
        objectValue
            [ "functionId", box (StableId.value value.FunctionId)
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map parameter |> arrayValue
              "resultType", typeRef value.ResultType
              "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arrayValue ]

    let focus (value: FunctionFocusView) =
        objectValue
            [ "snapshotId", box (StableId.value value.SnapshotId)
              "function", functionEntity value.Function
              "nodeTypes",
                value.NodeTypes
                |> List.map (fun nodeType ->
                    objectValue
                        [ "nodeId", box (StableId.value nodeType.NodeId)
                          "type",
                            match nodeType.Type with
                            | Some item -> typeRef item
                            | None -> null ])
                |> arrayValue
              "callers", value.Callers |> List.map callSite |> arrayValue
              "callees", value.Callees |> List.map callSite |> arrayValue
              "relatedFunctionSignatures", value.RelatedFunctionSignatures |> List.map signature |> arrayValue
              "openObligations", value.OpenObligations |> List.map obligation |> arrayValue
              "referencedTypes", value.ReferencedTypes |> List.map semanticType |> arrayValue
              "referencedCapabilities", value.ReferencedCapabilities |> List.map (sprintf "%A" >> box) |> arrayValue
              "referencedEffects", value.ReferencedEffects |> List.map (sprintf "%A" >> box) |> arrayValue
              "missingFunctionIds", value.MissingFunctionIds |> List.map (StableId.value >> box) |> arrayValue
              "missingTypeIds", value.MissingTypeIds |> List.map (StableId.value >> box) |> arrayValue
              "missingCapabilityIds", value.MissingCapabilityIds |> List.map (StableId.value >> box) |> arrayValue
              "missingEffectIds", value.MissingEffectIds |> List.map (StableId.value >> box) |> arrayValue ]

    let diagnostics (values: TransactionDiagnostic list) =
        values
        |> List.map (fun diagnostic ->
            objectValue [ "code", box diagnostic.Code; "message", box diagnostic.Message ])
        |> arrayValue

    let readiness = function
        | TransactionReadiness.SemanticallyReady ->
            objectValue [ "kind", box "semanticallyReady" ]
        | TransactionReadiness.Incomplete obligationIds ->
            objectValue
                [ "kind", box "incomplete"
                  "obligationIds", obligationIds |> List.map (StableId.value >> box) |> arrayValue ]

    let diff (value: SemanticDiff) =
        let renames =
            value.Renames
            |> List.map (function
                | TypeRenamed(typeId, oldName, newName) ->
                    objectValue
                        [ "kind", box "type"
                          "id", box (StableId.value typeId)
                          "oldDisplayName", box oldName
                          "newDisplayName", box newName ]
                | FunctionRenamed(functionId, oldName, newName) ->
                    objectValue
                        [ "kind", box "function"
                          "id", box (StableId.value functionId)
                          "oldDisplayName", box oldName
                          "newDisplayName", box newName ])

        let parametersAdded =
            value.ParametersAdded
            |> List.map (fun parameterAdded ->
                objectValue
                    [ "ownerFunctionId", box (StableId.value parameterAdded.OwnerFunctionId)
                      "parameterId", box (StableId.value parameterAdded.AddedParameterId)
                      "displayName", box parameterAdded.AddedDisplayName
                      "type", typeRef parameterAdded.AddedType ])

        let argumentsAttached =
            value.ArgumentsAttached
            |> List.map (fun argument ->
                objectValue
                    [ "callNodeId", box (StableId.value argument.CallNodeId)
                      "parameterId", box (StableId.value argument.TargetParameterId)
                      "valueNodeId", box (StableId.value argument.ValueNodeId) ])

        let obligationsCreated =
            value.ObligationsCreated
            |> List.map (fun obligation ->
                objectValue
                    [ "obligationId", box (StableId.value obligation.ObligationId)
                      "callNodeId", box (StableId.value obligation.CallNodeId)
                      "parameterId", box (StableId.value obligation.ParameterId) ])

        let obligationsResolved =
            value.ObligationsResolved
            |> List.map (fun obligation ->
                objectValue
                    [ "obligationId", box (StableId.value obligation.ObligationId)
                      "callNodeId", box (StableId.value obligation.CallNodeId)
                      "parameterId", box (StableId.value obligation.ParameterId)
                      "valueNodeId", box (StableId.value obligation.ValueNodeId) ])

        objectValue
            [ "renames", arrayValue renames
              "parametersAdded", arrayValue parametersAdded
              "argumentsAttached", arrayValue argumentsAttached
              "obligationsCreated", arrayValue obligationsCreated
              "obligationsResolved", arrayValue obligationsResolved ]

    let review (value: AgentChangeReview) =
        objectValue
            [ "requestedOperations", value.RequestedOperations |> List.map (sprintf "%A" >> box) |> arrayValue
              "receipt",
                objectValue
                    [ "transactionId", box (StableId.value value.Receipt.TransactionId)
                      "baseSnapshotId", box (StableId.value value.Receipt.BaseSnapshotId)
                      "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.Receipt.BaseSnapshotFingerprint)
                      "proposedSnapshotId", box (StableId.value value.Receipt.ProposedSnapshotId)
                      "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.Receipt.ProposedSnapshotFingerprint)
                      "proposedSemanticFingerprint", box (SemanticFingerprint.value value.Receipt.ProposedSemanticFingerprint) ]
              "readiness", readiness value.Readiness
              "diff", diff value.ResultingChange.Diff
              "affectedFunctionIds",
                value.ResultingChange.AffectedFunctionIds |> List.map (StableId.value >> box) |> arrayValue
              "affectedTypeIds",
                value.ResultingChange.AffectedTypeIds |> List.map (StableId.value >> box) |> arrayValue
              "affectedFunctionFocus", value.ResultingChange.FunctionFocus |> List.map focus |> arrayValue ]

    let publication (value: PublishedTransaction) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "publishedSnapshotId", box (StableId.value value.PublishedSnapshotId)
              "readiness", readiness value.Readiness
              "diff", diff value.Diff ]

type Session internal (initialWorkspace: AgentWorkspace, config: SessionConfig) =
    let mutable workspace = initialWorkspace
    let mutable proposals: Map<string, AgentChangeProposal> = Map.empty
    let mutable nextProposalNumber = 1
    let mutable toolCalls = 0
    let mutable queries = 0
    let mutable simulations = 0
    let mutable commitAttempts = 0
    let mutable rejectActions = 0
    let mutable perturbationApplied = false
    let mutable harnessEvents: string list = []

    let proposalHandle () =
        let handle = $"proposal-{nextProposalNumber}"
        nextProposalNumber <- nextProposalNumber + 1
        handle

    let queryResult value =
        JsonOutput.objectValue [ "status", box "ok"; "value", value ]

    let queryError error =
        JsonOutput.objectValue
            [ "status", box "error"
              "errorKind", box "query"
              "detail", box (sprintf "%A" error) ]

    let applyHarnessPerturbation () =
        match config.StalePerturbation, perturbationApplied with
        | None, _
        | _, true -> ()
        | Some perturbation, false ->
            let intent =
                AgentOperationIntent.RenameDisplayName(
                    AgentRenameTarget.Type perturbation.TypeId,
                    perturbation.NewDisplayName
                )

            match AgentWorkflow.simulate workspace [ intent ] with
            | Error error ->
                perturbationApplied <- true
                harnessEvents <-
                    harnessEvents
                    @ [ $"stale-perturbation simulation failed: {error}" ]
            | Ok proposal ->
                match AgentWorkflow.commit workspace proposal with
                | AgentCommitResult.Rejected diagnostics ->
                    perturbationApplied <- true
                    harnessEvents <-
                        harnessEvents
                        @ [ $"stale-perturbation commit failed: {diagnostics}" ]
                | AgentCommitResult.Committed(nextWorkspace, publication) ->
                    workspace <- nextWorkspace
                    perturbationApplied <- true
                    harnessEvents <-
                        harnessEvents
                        @ [ $"stale-perturbation committed transaction {StableId.value publication.TransactionId} snapshot {StableId.value publication.PublishedSnapshotId}" ]

    let metrics () =
        JsonOutput.objectValue
            [ "toolCalls", box toolCalls
              "queries", box queries
              "simulations", box simulations
              "commitAttempts", box commitAttempts
              "rejectActions", box rejectActions
              "pendingProposalCount", box proposals.Count
              "harnessEvents", harnessEvents |> List.map box |> JsonOutput.arrayValue ]

    let stateResult () =
        let snapshotFingerprint =
            match AgentWorkspace.currentSnapshotFingerprint workspace with
            | Ok fingerprint -> box (SnapshotFingerprint.value fingerprint)
            | Error error -> box $"error:{error}"

        let semanticFingerprint =
            match AgentWorkspace.currentSemanticFingerprint workspace with
            | Ok fingerprint -> box (SemanticFingerprint.value fingerprint)
            | Error error -> box $"error:{error}"

        JsonOutput.objectValue
            [ "status", box "ok"
              "snapshotId", box (StableId.value (AgentWorkspace.currentSnapshotId workspace))
              "snapshotFingerprint", snapshotFingerprint
              "semanticFingerprint", semanticFingerprint
              "proposalHandles", proposals |> Map.toList |> List.map (fst >> box) |> JsonOutput.arrayValue
              "metrics", metrics () ]

    let requireProposal handle =
        match Map.tryFind handle proposals with
        | Some proposal -> proposal
        | None -> JsonInput.ProtocolInputException $"Unknown proposal handle '{handle}'." |> raise

    let executeCommand (root: JsonElement) =
        let command = JsonInput.requiredString "command" root
        match command with
        | "state" -> stateResult ()
        | "getFunction" ->
            queries <- queries + 1
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getFunction workspace functionId with
            | Ok value -> queryResult (Encode.functionEntity value)
            | Error error -> queryError error
        | "getType" ->
            queries <- queries + 1
            let typeId = JsonInput.stableId<TypeTag> "typeId" (JsonInput.requiredString "typeId" root)
            match AgentWorkspace.getType workspace typeId with
            | Ok value -> queryResult (Encode.semanticType value)
            | Error error -> queryError error
        | "getCallers" ->
            queries <- queries + 1
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getCallers workspace functionId with
            | Ok values -> queryResult (values |> List.map Encode.callSite |> JsonOutput.arrayValue)
            | Error error -> queryError error
        | "getCallees" ->
            queries <- queries + 1
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getCallees workspace functionId with
            | Ok values -> queryResult (values |> List.map Encode.callSite |> JsonOutput.arrayValue)
            | Error error -> queryError error
        | "getOpenObligations" ->
            queries <- queries + 1
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getOpenObligations workspace functionId with
            | Ok values -> queryResult (values |> List.map Encode.obligation |> JsonOutput.arrayValue)
            | Error error -> queryError error
        | "getDependencies" ->
            queries <- queries + 1
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            let nodeId = JsonInput.stableId<GraphNodeTag> "nodeId" (JsonInput.requiredString "nodeId" root)
            match AgentWorkspace.getDependencies workspace functionId nodeId with
            | Ok values -> queryResult (values |> List.map (StableId.value >> box) |> JsonOutput.arrayValue)
            | Error error -> queryError error
        | "getUses" ->
            queries <- queries + 1
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            let nodeId = JsonInput.stableId<GraphNodeTag> "nodeId" (JsonInput.requiredString "nodeId" root)
            match AgentWorkspace.getUses workspace functionId nodeId with
            | Ok values -> queryResult (values |> List.map (StableId.value >> box) |> JsonOutput.arrayValue)
            | Error error -> queryError error
        | "focusFunction" ->
            queries <- queries + 1
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.focusFunction workspace functionId with
            | Ok value -> queryResult (Encode.focus value)
            | Error error -> queryError error
        | "simulate" ->
            simulations <- simulations + 1
            let operations = JsonInput.requiredArray "operations" root |> List.map JsonInput.operation
            match AgentWorkflow.simulate workspace operations with
            | Error(AgentWorkflowError.SimulationRejected diagnostics) ->
                JsonOutput.objectValue
                    [ "status", box "rejected"
                      "diagnostics", Encode.diagnostics diagnostics ]
            | Error error ->
                JsonOutput.objectValue
                    [ "status", box "error"
                      "errorKind", box "workflow"
                      "detail", box (sprintf "%A" error) ]
            | Ok proposal ->
                let handle = proposalHandle ()
                proposals <- Map.add handle proposal proposals
                let review = AgentChangeProposal.review proposal
                let result =
                    JsonOutput.objectValue
                        [ "status", box "proposed"
                          "proposalHandle", box handle
                          "transactionId", box (StableId.value (AgentChangeProposal.transactionId proposal))
                          "proposedSnapshotId", box (StableId.value (AgentChangeProposal.proposedSnapshotId proposal))
                          "review", Encode.review review ]
                applyHarnessPerturbation ()
                result
        | "commit" ->
            commitAttempts <- commitAttempts + 1
            let handle = JsonInput.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            match AgentWorkflow.commit workspace proposal with
            | AgentCommitResult.Rejected diagnostics ->
                JsonOutput.objectValue
                    [ "status", box "rejected"
                      "proposalHandle", box handle
                      "diagnostics", Encode.diagnostics diagnostics ]
            | AgentCommitResult.Committed(nextWorkspace, publication) ->
                workspace <- nextWorkspace
                proposals <- Map.remove handle proposals
                JsonOutput.objectValue
                    [ "status", box "committed"
                      "proposalHandle", box handle
                      "publication", Encode.publication publication ]
        | "commitAllowIncomplete" ->
            commitAttempts <- commitAttempts + 1
            let handle = JsonInput.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            match AgentWorkflow.commitWithPolicy workspace proposal CommitPolicy.AllowBlockingObligations with
            | AgentCommitResult.Rejected diagnostics ->
                JsonOutput.objectValue
                    [ "status", box "rejected"
                      "proposalHandle", box handle
                      "diagnostics", Encode.diagnostics diagnostics ]
            | AgentCommitResult.Committed(nextWorkspace, publication) ->
                workspace <- nextWorkspace
                proposals <- Map.remove handle proposals
                JsonOutput.objectValue
                    [ "status", box "committed"
                      "proposalHandle", box handle
                      "publication", Encode.publication publication ]
        | "reject" ->
            rejectActions <- rejectActions + 1
            let handle = JsonInput.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            let nextWorkspace, receipt = AgentWorkflow.reject workspace proposal
            workspace <- nextWorkspace
            proposals <- Map.remove handle proposals
            JsonOutput.objectValue
                [ "status", box "rejectedByAgent"
                  "proposalHandle", box handle
                  "transactionId", box (StableId.value receipt.TransactionId)
                  "retainedSnapshotId", box (StableId.value receipt.RetainedSnapshotId) ]
        | "metrics" ->
            JsonOutput.objectValue [ "status", box "ok"; "metrics", metrics () ]
        | value -> raise (JsonInput.ProtocolInputException $"Unsupported command '{value}'.")

    member _.ExecuteLine(line: string): string =
        toolCalls <- toolCalls + 1
        try
            if String.IsNullOrWhiteSpace line then
                raise (JsonInput.ProtocolInputException "Input line must contain one JSON command.")

            use document = JsonInput.parseDocument line
            let result = executeCommand document.RootElement
            JsonOutput.objectValue
                [ "ok", box true
                  "sequence", box toolCalls
                  "result", result
                  "metrics", metrics () ]
            |> JsonOutput.serialize
        with
        | JsonInput.ProtocolInputException message ->
            JsonOutput.objectValue
                [ "ok", box false
                  "sequence", box toolCalls
                  "error",
                    JsonOutput.objectValue
                        [ "code", box "ProtocolError"
                          "message", box message ]
                  "metrics", metrics () ]
            |> JsonOutput.serialize

[<RequireQualifiedAccess>]
module Session =
    let create (snapshot: Snapshot) (config: SessionConfig): Result<Session, ValidationIssue list> =
        AgentWorkspace.create snapshot
        |> Result.map (fun workspace -> Session(workspace, config))
