namespace Nyrwvy.AgentEval

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core

type StalePerturbation =
    { TriggerFunctionId: FunctionId
      TriggerNewDisplayName: string
      AdvanceTypeId: TypeId
      AdvanceNewDisplayName: string }

type SessionRunMetadata =
    { Model: string option
      ProviderRuntime: string option
      ReasoningConfig: string option
      OpenCodeVersion: string option
      PromptRevision: string option
      ToolRevision: string option }

[<RequireQualifiedAccess>]
module SessionRunMetadata =
    let defaultValue =
        { Model = None
          ProviderRuntime = None
          ReasoningConfig = None
          OpenCodeVersion = None
          PromptRevision = None
          ToolRevision = None }

type HarnessReceipt =
    { Kind: string
      TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      BaseSnapshotFingerprint: SnapshotFingerprint
      PublishedSnapshotFingerprint: SnapshotFingerprint
      BaseSemanticFingerprint: SemanticFingerprint
      PublishedSemanticFingerprint: SemanticFingerprint
      Readiness: TransactionReadiness
      Diff: SemanticDiff }

type SessionArtifactMetadata =
    { StartingSnapshotId: SnapshotId
      StartingSnapshotFingerprint: SnapshotFingerprint
      StartingSemanticFingerprint: SemanticFingerprint
      Model: string
      ProviderRuntime: string
      ReasoningConfig: string
      OpenCodeVersion: string
      PromptRevision: string
      ToolRevision: string }

type SessionConfig =
    { StalePerturbation: StalePerturbation option
      VerificationFixture: VerificationFixture option
      RunMetadata: SessionRunMetadata }

[<RequireQualifiedAccess>]
module SessionConfig =
    let defaultValue =
        { StalePerturbation = None
          VerificationFixture = None
          RunMetadata = SessionRunMetadata.defaultValue }

type SessionEvent =
    { Sequence: int
      Request: string
      Response: string
      RequestBytes: int64
      ResponseBytes: int64
      ContextBytes: int64
      FocusContextBytes: int64
      FocusEntityCount: int
      HarnessReceipts: HarnessReceipt list }

type SessionArtifactFailure =
    { Artifact: string
      Message: string }

type SessionArtifactStatus =
    { Enabled: bool
      Invalidated: bool
      Durable: bool
      Terminal: bool
      FinalizationAttempted: bool
      FinalizationDurable: bool
      Failures: SessionArtifactFailure list }

type SessionUsage =
    { ToolCalls: int
      QueryCount: int
      FocusQueryCount: int
      SimulationCount: int
      CommitAttemptCount: int
      CommitCount: int
      RejectedSimulationCount: int
      RejectedCommitCount: int
      AgentRejectCount: int
      RejectCount: int
      ProtocolErrorCount: int
      EventCount: int
      RequestBytes: int64
      ResponseBytes: int64
      ContextBytes: int64
      QueryContextBytes: int64
      FocusContextBytes: int64
      FocusEntityCount: int
      Events: SessionEvent list
      HarnessEvents: string list
      HarnessReceipts: HarnessReceipt list }

    member value.Queries = value.QueryCount + value.FocusQueryCount
    member value.Simulations = value.SimulationCount
    member value.CommitAttempts = value.CommitAttemptCount
    member value.RejectActions = value.AgentRejectCount

type SessionArtifactPaths =
    { DirectoryPath: string
      EventLogPath: string
      MetricsPath: string
      RunMetadataPath: string
      RunStatePath: string }

type SessionArtifactWriter =
    { Paths: SessionArtifactPaths
      WriteRunMetadata: string -> unit
      WriteEvent: string -> unit
      WriteMetrics: string -> unit
      WriteRunState: string -> unit }

[<RequireQualifiedAccess>]
type SessionCreationError =
    | WorkspaceValidation of ValidationIssue list
    | ArtifactInitializationFailed of string
    | InvalidConfiguration of string

[<RequireQualifiedAccess>]
module SessionArtifacts =
    let eventFileName = "events.jsonl"
    let metricsFileName = "metrics.json"
    let runMetadataFileName = "run-metadata.json"
    let runStateFileName = "run-state.json"

module private JsonOutput =
    let strings (items: string list): obj =
        items |> List.map box |> ArtifactJson.arrayValue

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
        | "Bits32" -> PrimitiveType.Bits32
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
    open ArtifactJson
    open JsonOutput

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
        | BuiltinGenericType.OneShot -> "OneShot"
        | BuiltinGenericType.Task -> "Task"
        | BuiltinGenericType.TaskScope -> "TaskScope"

    let rec typeRef = function
        | TypeRef.Primitive primitive ->
            objectValue [ "kind", box "primitive"; "name", box (primitiveName primitive) ]
        | TypeRef.Named typeId ->
            objectValue [ "kind", box "named"; "id", box (StableId.value typeId) ]
        | TypeRef.Capability capabilityId ->
            objectValue [ "kind", box "capability"; "id", box (StableId.value capabilityId) ]
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

    // Count the primary function plus every entity/relation entry in the safe
    // focus projection; missing IDs are entries too, but never expose the raw snapshot.
    let focusEntityCount (value: FunctionFocusView) =
        1
        + value.NodeTypes.Length
        + value.Callers.Length
        + value.Callees.Length
        + value.RelatedFunctionSignatures.Length
        + value.OpenObligations.Length
        + value.ReferencedTypes.Length
        + value.ReferencedCapabilities.Length
        + value.ReferencedEffects.Length
        + value.MissingFunctionIds.Length
        + value.MissingTypeIds.Length
        + value.MissingCapabilityIds.Length
        + value.MissingEffectIds.Length

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

    let harnessReceipt (value: HarnessReceipt) =
        objectValue
            [ "kind", box value.Kind
              "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "publishedSnapshotId", box (StableId.value value.PublishedSnapshotId)
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.BaseSnapshotFingerprint)
              "publishedSnapshotFingerprint", box (SnapshotFingerprint.value value.PublishedSnapshotFingerprint)
              "baseSemanticFingerprint", box (SemanticFingerprint.value value.BaseSemanticFingerprint)
              "publishedSemanticFingerprint", box (SemanticFingerprint.value value.PublishedSemanticFingerprint)
              "semanticFingerprint", box (SemanticFingerprint.value value.PublishedSemanticFingerprint)
              "readiness", readiness value.Readiness
              "semanticDiff", diff value.Diff ]

    let artifactFailure (value: SessionArtifactFailure) =
        objectValue [ "artifact", box value.Artifact; "message", box value.Message ]

    let artifactStatus (value: SessionArtifactStatus) =
        let status =
            if not value.Enabled then
                "notConfigured"
            elif value.Invalidated then
                "invalidated"
            elif value.Terminal then
                "finalized"
            else
                "inProgress"

        objectValue
            [ "status", box status
              "enabled", box value.Enabled
              "runInvalidated", box value.Invalidated
              "durable", box value.Durable
              "terminal", box value.Terminal
              "finalizationAttempted", box value.FinalizationAttempted
              "finalizationDurable", box value.FinalizationDurable
              "failures", value.Failures |> List.map artifactFailure |> arrayValue ]

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
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.Receipt.BaseSnapshotFingerprint)
              "publishedSnapshotFingerprint", box (SnapshotFingerprint.value value.Receipt.ProposedSnapshotFingerprint)
              "publishedSemanticFingerprint", box (SemanticFingerprint.value value.Receipt.ProposedSemanticFingerprint)
              "receipt",
                objectValue
                    [ "transactionId", box (StableId.value value.Receipt.TransactionId)
                      "baseSnapshotId", box (StableId.value value.Receipt.BaseSnapshotId)
                      "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.Receipt.BaseSnapshotFingerprint)
                      "proposedSnapshotId", box (StableId.value value.Receipt.ProposedSnapshotId)
                      "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.Receipt.ProposedSnapshotFingerprint)
                      "proposedSemanticFingerprint", box (SemanticFingerprint.value value.Receipt.ProposedSemanticFingerprint) ]
              "readiness", readiness value.Readiness
              "diff", diff value.Diff ]

    let verification (value: VerificationReport) =
        let optionalSnapshotId snapshotId =
            match snapshotId with
            | Some item -> box (StableId.value item)
            | None -> null

        let optionalSnapshotFingerprint fingerprint =
            match fingerprint with
            | Some item -> box (SnapshotFingerprint.value item)
            | None -> null

        let optionalSemanticFingerprint fingerprint =
            match fingerprint with
            | Some item -> box (SemanticFingerprint.value item)
            | None -> null

        objectValue
            [ "state", box (if value.EvidenceDurable then "terminal" else "inProgress")
              "fixture", box (VerificationFixture.name value.Fixture)
              "status", box (VerificationStatus.name value.Status)
              "evidenceDurable", box value.EvidenceDurable
              "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "publishedSnapshotId", box (StableId.value value.PublishedSnapshotId)
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.BaseSnapshotFingerprint)
              "publishedSnapshotFingerprint", box (SnapshotFingerprint.value value.ProposedSnapshotFingerprint)
              "publishedSemanticFingerprint", box (SemanticFingerprint.value value.ProposedSemanticFingerprint)
              "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.ProposedSnapshotFingerprint)
              "proposedSemanticFingerprint", box (SemanticFingerprint.value value.ProposedSemanticFingerprint)
              "receipt",
                objectValue
                    [ "transactionId", box (StableId.value value.TransactionId)
                      "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
                      "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.BaseSnapshotFingerprint)
                      "proposedSnapshotId", box (StableId.value value.PublishedSnapshotId)
                      "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.ProposedSnapshotFingerprint)
                      "proposedSemanticFingerprint", box (SemanticFingerprint.value value.ProposedSemanticFingerprint) ]
              "currentSnapshotFingerprint", optionalSnapshotFingerprint value.CurrentSnapshotFingerprint
              "currentSemanticFingerprint", optionalSemanticFingerprint value.CurrentSemanticFingerprint
              "typedSemanticSourceSnapshotId", optionalSnapshotId value.TypedSemanticSourceSnapshotId
              "executionSourceSnapshotId", optionalSnapshotId value.ExecutionSourceSnapshotId
              "error",
                value.Error
                |> Option.map (fun _ -> box "Verification failed; inspect harness evidence.")
                |> Option.defaultValue null ]

    let runMetadata (value: SessionArtifactMetadata) =
        objectValue
            [ "schemaVersion", box 1
              "startingSnapshotId", box (StableId.value value.StartingSnapshotId)
              "startingSnapshotFingerprint", box (SnapshotFingerprint.value value.StartingSnapshotFingerprint)
              "startingSemanticFingerprint", box (SemanticFingerprint.value value.StartingSemanticFingerprint)
              "model", box value.Model
              "providerRuntime", box value.ProviderRuntime
              "reasoningConfig", box value.ReasoningConfig
              "openCodeVersion", box value.OpenCodeVersion
              "promptRevision", box value.PromptRevision
              "toolRevision", box value.ToolRevision ]

[<RequireQualifiedAccess>]
module private ArtifactWriter =
    let private utf8 = UTF8Encoding(false)

    let eventJson (event: SessionEvent) =
        let value =
            ArtifactJson.objectValue
                [ "sequence", box event.Sequence
                  "request", box event.Request
                  "response", box event.Response
                  "requestBytes", box event.RequestBytes
                  "responseBytes", box event.ResponseBytes
                  "contextBytes", box event.ContextBytes
                  "focusContextBytes", box event.FocusContextBytes
                  "focusEntityCount", box event.FocusEntityCount
                  "harnessReceipts", event.HarnessReceipts |> List.map Encode.harnessReceipt |> ArtifactJson.arrayValue ]

        ArtifactJson.serialize value + "\n"

    let create
        (directory: string)
        (runMetadata: SessionArtifactMetadata)
        : Result<SessionArtifactWriter, string> =
        try
            if String.IsNullOrWhiteSpace directory then
                Error "Artifact directory must not be blank."
            else
                let fullDirectory = Path.GetFullPath(directory)
                ArtifactPermissions.ensureDirectory fullDirectory
                let sessionDirectory =
                    Path.Combine(fullDirectory, "session-" + Guid.NewGuid().ToString("N"))

                ArtifactPermissions.ensureDirectory sessionDirectory

                let paths =
                    { DirectoryPath = sessionDirectory
                      EventLogPath = Path.Combine(sessionDirectory, SessionArtifacts.eventFileName)
                      MetricsPath = Path.Combine(sessionDirectory, SessionArtifacts.metricsFileName)
                      RunMetadataPath = Path.Combine(sessionDirectory, SessionArtifacts.runMetadataFileName)
                      RunStatePath = Path.Combine(sessionDirectory, SessionArtifacts.runStateFileName) }

                ArtifactFiles.writeText paths.EventLogPath FileMode.Create String.Empty

                let writer =
                    { Paths = paths
                      WriteRunMetadata = ArtifactFiles.writeAtomically paths.RunMetadataPath
                      WriteEvent = fun value -> ArtifactFiles.writeText paths.EventLogPath FileMode.Append value
                      WriteMetrics = ArtifactFiles.writeAtomically paths.MetricsPath
                      WriteRunState = ArtifactFiles.writeAtomically paths.RunStatePath }

                ArtifactJson.serialize (Encode.runMetadata runMetadata)
                |> writer.WriteRunMetadata

                Ok writer
        with
        | _ -> Error "Artifact initialization failed."

type Session internal
    (initialWorkspace: AgentWorkspace,
     config: SessionConfig,
     runMetadata: SessionArtifactMetadata,
     artifactWriter: SessionArtifactWriter option) =
    let mutable workspace = initialWorkspace
    let mutable proposals: Map<string, AgentChangeProposal> = Map.empty
    let mutable nextProposalNumber = 1
    let mutable usage =
        { ToolCalls = 0
          QueryCount = 0
          FocusQueryCount = 0
          SimulationCount = 0
          CommitAttemptCount = 0
          CommitCount = 0
          RejectedSimulationCount = 0
          RejectedCommitCount = 0
          AgentRejectCount = 0
          RejectCount = 0
          ProtocolErrorCount = 0
          EventCount = 0
          RequestBytes = 0L
          ResponseBytes = 0L
          ContextBytes = 0L
          QueryContextBytes = 0L
          FocusContextBytes = 0L
          FocusEntityCount = 0
          Events = []
          HarnessEvents = []
          HarnessReceipts = [] }
    let mutable artifactFailures: SessionArtifactFailure list = []
    let mutable finalizationAttempted = false
    let mutable finalizationDurable = false
    let mutable perturbationApplied = false
    let mutable harnessFailure = false

    let usageSnapshot () =
        { usage with
            Events = List.rev usage.Events
            HarnessEvents = List.rev usage.HarnessEvents
            HarnessReceipts = List.rev usage.HarnessReceipts }

    let artifactStatus () =
        { Enabled = artifactWriter.IsSome
          Invalidated = harnessFailure || not (List.isEmpty artifactFailures)
          Durable = artifactWriter.IsSome && not harnessFailure && List.isEmpty artifactFailures
          Terminal = artifactWriter.IsSome && finalizationDurable
          FinalizationAttempted = finalizationAttempted
          FinalizationDurable = artifactWriter.IsSome && finalizationDurable
          Failures = List.rev artifactFailures }

    let runStateValue (state: string) =
        let status = artifactStatus ()
        let terminal = state = "terminal"
        let terminalStatus =
            { status with
                Terminal = terminal
                FinalizationDurable = terminal }

        ArtifactJson.objectValue
            [ "schemaVersion", box 1
              "state", box state
              "artifactPersistence", Encode.artifactStatus terminalStatus ]

    let metricsFor includeHarnessData (responseBytesOverride: int64 option) =
        let value = usage
        let responseBytes = responseBytesOverride |> Option.defaultValue value.ResponseBytes

        let harnessData =
            if includeHarnessData then
                [ "harnessEvents", List.rev value.HarnessEvents |> List.map box |> ArtifactJson.arrayValue
                  "harnessReceipts",
                  List.rev value.HarnessReceipts |> List.map Encode.harnessReceipt |> ArtifactJson.arrayValue ]
            else
                []

        ArtifactJson.objectValue
            ([ "toolCalls", box value.ToolCalls
               "queries", box value.Queries
               "queryCount", box value.QueryCount
               "focusQueries", box value.FocusQueryCount
               "focusQueryCount", box value.FocusQueryCount
               "focusCount", box value.FocusQueryCount
               "simulations", box value.SimulationCount
               "simulationCount", box value.SimulationCount
               "commitAttempts", box value.CommitAttemptCount
               "commitAttemptCount", box value.CommitAttemptCount
               "commits", box value.CommitCount
               "commitCount", box value.CommitCount
               "rejectedSimulations", box value.RejectedSimulationCount
               "rejectedCommits", box value.RejectedCommitCount
               "rejectActions", box value.AgentRejectCount
               "rejects", box value.RejectCount
               "rejectCount", box value.RejectCount
               "protocolErrors", box value.ProtocolErrorCount
               "eventCount", box value.EventCount
               "requestBytes", box value.RequestBytes
               "responseBytes", box responseBytes
               "contextBytes", box value.ContextBytes
               "queryContextBytes", box value.QueryContextBytes
               "focusContextBytes", box value.FocusContextBytes
               "focusEntityCount", box value.FocusEntityCount
               "pendingProposalCount", box proposals.Count ]
             @ harnessData
             @ [ "artifactPersistence", Encode.artifactStatus (artifactStatus ()) ])

    let agentMetrics () = metricsFor false None
    let artifactMetrics () = metricsFor true None

    let recordHarnessEvent message =
        usage <-
            { usage with
                HarnessEvents = message :: usage.HarnessEvents }

    let harnessFailureResult () =
        ArtifactJson.objectValue
            [ "status", box "harnessFailure"
              "errorKind", box "harness"
              "detail", box "The harness stale-workspace advance failed; the run is invalidated." ]

    let invalidateHarnessRun detail =
        harnessFailure <- true
        recordHarnessEvent ("stale-perturbation-advance-failed:" + detail)

    let recordHarnessReceipt (receipt: HarnessReceipt) =
        usage <-
            { usage with
                HarnessReceipts = receipt :: usage.HarnessReceipts }

    let recordArtifactFailure name =
        let failure =
            { Artifact = name
              Message = "Artifact persistence failed." }

        if artifactFailures |> List.exists (fun item -> item.Artifact = name) |> not then
            artifactFailures <- failure :: artifactFailures

        recordHarnessEvent ("artifact-write-failed:" + name)

    let tryArtifactWrite name (action: unit -> unit) =
        try
            action ()
            true
        with
        | _ ->
            recordArtifactFailure name
            false

    let tryWriteRunState (state: string) failureName =
        match artifactWriter with
        | None -> false
        | Some writer ->
            try
                ArtifactJson.serialize (runStateValue state)
                |> writer.WriteRunState
                true
            with
            | _ ->
                recordArtifactFailure failureName
                false

    let recordQuery () =
        usage <-
            { usage with
                QueryCount = usage.QueryCount + 1 }

    let recordFocusQuery () =
        usage <-
            { usage with
                FocusQueryCount = usage.FocusQueryCount + 1 }

    let recordSimulation () =
        usage <-
            { usage with
                SimulationCount = usage.SimulationCount + 1 }

    let recordRejection update =
        usage <-
            { usage with
                RejectCount = usage.RejectCount + 1 }
        update ()

    let proposalHandle () =
        let handle = $"proposal-{nextProposalNumber}"
        nextProposalNumber <- nextProposalNumber + 1
        handle

    let queryResult (focusEntityCount: int option) value =
        let isFocus = focusEntityCount.IsSome
        let contextBytes = ArtifactJson.serialize value |> ArtifactJson.utf8ByteCount
        usage <-
            { usage with
                ContextBytes = usage.ContextBytes + contextBytes
                QueryContextBytes =
                    if isFocus then usage.QueryContextBytes else usage.QueryContextBytes + contextBytes
                FocusContextBytes =
                    if isFocus then usage.FocusContextBytes + contextBytes else usage.FocusContextBytes }

        match focusEntityCount with
        | Some count ->
            usage <-
                { usage with
                    FocusEntityCount = usage.FocusEntityCount + count }
        | None -> ()

        ArtifactJson.objectValue [ "status", box "ok"; "value", value ]

    let queryError error =
        ArtifactJson.objectValue
            [ "status", box "error"
              "errorKind", box "query"
              "detail", box (sprintf "%A" error) ]

    let applyHarnessPerturbation shouldApply =
        match shouldApply, config.StalePerturbation, perturbationApplied with
        | false, _, _
        | _, None, _
        | _, _, true -> true
        | true, Some perturbation, false ->
            match
                AgentWorkspace.currentSnapshotFingerprint workspace,
                AgentWorkspace.currentSemanticFingerprint workspace
            with
            | Error error, _
            | _, Error error ->
                invalidateHarnessRun $"base identity capture failed: {error}"
                false
            | Ok baseSnapshotFingerprint, Ok baseSemanticFingerprint ->
                let intent =
                    AgentOperationIntent.RenameDisplayName(
                        AgentRenameTarget.Type perturbation.AdvanceTypeId,
                        perturbation.AdvanceNewDisplayName
                    )

                match AgentWorkflow.simulate workspace [ intent ] with
                | Error error ->
                    invalidateHarnessRun $"simulation failed: {error}"
                    false
                | Ok proposal ->
                    match AgentWorkflow.commit workspace proposal with
                    | AgentCommitResult.Rejected diagnostics ->
                        invalidateHarnessRun $"commit failed: {diagnostics}"
                        false
                    | AgentCommitResult.Committed(nextWorkspace, publication) ->
                        workspace <- nextWorkspace
                        perturbationApplied <- true
                        recordHarnessReceipt
                            { Kind = "stalePerturbation"
                              TransactionId = publication.TransactionId
                              BaseSnapshotId = publication.BaseSnapshotId
                              PublishedSnapshotId = publication.PublishedSnapshotId
                              BaseSnapshotFingerprint = baseSnapshotFingerprint
                              PublishedSnapshotFingerprint = publication.Receipt.ProposedSnapshotFingerprint
                              BaseSemanticFingerprint = baseSemanticFingerprint
                              PublishedSemanticFingerprint = publication.Receipt.ProposedSemanticFingerprint
                              Readiness = publication.Readiness
                              Diff = publication.Diff }
                        recordHarnessEvent "stale-perturbation-committed"
                        true

    let responseWithMetrics body =
        let rec build responseBytes =
            let response =
                ArtifactJson.objectValue
                    (body
                     @ [ "artifactPersistence", Encode.artifactStatus (artifactStatus ());
                           "metrics", metricsFor false (Some responseBytes) ])
                |> ArtifactJson.serialize

            let nextResponseBytes = usage.ResponseBytes + ArtifactJson.utf8ByteCount response

            if nextResponseBytes = responseBytes then
                response
            else
                build nextResponseBytes

        build usage.ResponseBytes

    let recordEvent request body contextBytes focusContextBytes focusEntityCount harnessReceiptCountBefore =
        let initialResponse = responseWithMetrics body
        let receiptCount = usage.HarnessReceipts.Length - harnessReceiptCountBefore
        let harnessReceipts =
            usage.HarnessReceipts
            |> List.take (max 0 receiptCount)
            |> List.rev
        let initialEvent =
            { Sequence = usage.EventCount
              Request = request
              Response = initialResponse
              RequestBytes = ArtifactJson.utf8ByteCount request
              ResponseBytes = ArtifactJson.utf8ByteCount initialResponse
              ContextBytes = contextBytes
              FocusContextBytes = focusContextBytes
              FocusEntityCount = focusEntityCount
              HarnessReceipts = harnessReceipts }

        usage <-
            { usage with
                ResponseBytes = usage.ResponseBytes + initialEvent.ResponseBytes
                Events = initialEvent :: usage.Events }

        let failuresBefore = artifactFailures.Length

        let refreshCurrentEventResponse () =
            let currentEvent = List.head usage.Events
            usage <-
                { usage with
                    ResponseBytes = usage.ResponseBytes - currentEvent.ResponseBytes }

            let response = responseWithMetrics body
            let responseBytes = ArtifactJson.utf8ByteCount response
            let updatedEvent =
                { currentEvent with
                    Response = response
                    ResponseBytes = responseBytes }

            usage <-
                { usage with
                    ResponseBytes = usage.ResponseBytes + responseBytes
                    Events = updatedEvent :: List.tail usage.Events }

            response

        match artifactWriter with
        | None -> initialResponse
        | Some writer ->
            let metricsWritten =
                tryArtifactWrite "metrics" (fun () ->
                    ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)

            if not metricsWritten then
                tryWriteRunState "inProgress" "run-state" |> ignore

            if artifactFailures.Length <> failuresBefore then
                if not (tryArtifactWrite "metrics" (fun () ->
                    ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)) then
                    tryWriteRunState "inProgress" "run-state" |> ignore

            let response =
                if artifactFailures.Length = failuresBefore then
                    initialResponse
                else
                    refreshCurrentEventResponse ()

            let event = List.head usage.Events
            let eventWritten =
                tryArtifactWrite "event-log" (fun () ->
                    ArtifactWriter.eventJson event |> writer.WriteEvent)

            if eventWritten then
                response
            else
                tryWriteRunState "inProgress" "run-state" |> ignore

                let finalResponse = refreshCurrentEventResponse ()
                let failuresBeforeMetricsRetry = artifactFailures.Length

                if not (tryArtifactWrite "metrics" (fun () ->
                    ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)) then
                    tryWriteRunState "inProgress" "run-state" |> ignore

                if artifactFailures.Length = failuresBeforeMetricsRetry then
                    finalResponse
                else
                    refreshCurrentEventResponse ()

    let stateResult () =
        let snapshotFingerprint =
            match AgentWorkspace.currentSnapshotFingerprint workspace with
            | Ok fingerprint -> box (SnapshotFingerprint.value fingerprint)
            | Error error -> box $"error:{error}"

        let semanticFingerprint =
            match AgentWorkspace.currentSemanticFingerprint workspace with
            | Ok fingerprint -> box (SemanticFingerprint.value fingerprint)
            | Error error -> box $"error:{error}"

        ArtifactJson.objectValue
            [ "status", box "ok"
              "snapshotId", box (StableId.value (AgentWorkspace.currentSnapshotId workspace))
              "snapshotFingerprint", snapshotFingerprint
              "semanticFingerprint", semanticFingerprint
              "proposalHandles", proposals |> Map.toList |> List.map (fst >> box) |> ArtifactJson.arrayValue
              "metrics", agentMetrics () ]

    let requireProposal handle =
        match Map.tryFind handle proposals with
        | Some proposal -> proposal
        | None -> JsonInput.ProtocolInputException $"Unknown proposal handle '{handle}'." |> raise

    let verifyCommitted
        (fixture: VerificationFixture option)
        (nextWorkspace: AgentWorkspace)
        (publication: PublishedTransaction)
        : VerificationReport option =
        match fixture, publication.Readiness, artifactWriter with
        | Some fixture, TransactionReadiness.SemanticallyReady, Some writer ->
            Some(Verification.run fixture nextWorkspace publication writer.Paths.DirectoryPath)
        | _ -> None

    let mutable verificationReports: VerificationReport list = []

    let addVerification
        (nextWorkspace: AgentWorkspace)
        (publication: PublishedTransaction)
        (body: (string * obj) list)
        : (string * obj) list =
        match verifyCommitted config.VerificationFixture nextWorkspace publication with
        | Some report ->
            verificationReports <- report :: verificationReports

            if not report.EvidenceDurable then
                recordArtifactFailure "verification-evidence"
                tryWriteRunState "inProgress" "run-state" |> ignore

            body @ [ "verification", Encode.verification report ]
        | None -> body

    let commitWith
        (commit: AgentWorkspace -> AgentChangeProposal -> AgentCommitResult)
        (root: JsonElement)
        =
        usage <-
            { usage with
                CommitAttemptCount = usage.CommitAttemptCount + 1 }
        let handle = JsonInput.requiredString "proposalHandle" root
        let proposal = requireProposal handle
        match commit workspace proposal with
        | AgentCommitResult.Rejected diagnostics ->
            recordRejection (fun () ->
                usage <-
                    { usage with
                        RejectedCommitCount = usage.RejectedCommitCount + 1 })
            ArtifactJson.objectValue
                [ "status", box "rejected"
                  "proposalHandle", box handle
                  "diagnostics", Encode.diagnostics diagnostics ]
        | AgentCommitResult.Committed(nextWorkspace, publication) ->
            workspace <- nextWorkspace
            proposals <- Map.remove handle proposals
            usage <-
                { usage with
                    CommitCount = usage.CommitCount + 1 }
            [ "status", box "committed"
              "proposalHandle", box handle
              "publication", Encode.publication publication ]
            |> addVerification nextWorkspace publication
            |> ArtifactJson.objectValue

    let executeCommand (root: JsonElement) =
        let command = JsonInput.requiredString "command" root
        match command with
        | _ when harnessFailure -> harnessFailureResult ()
        | "state" -> stateResult ()
        | "getFunction" ->
            recordQuery ()
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getFunction workspace functionId with
            | Ok value -> queryResult None (Encode.functionEntity value)
            | Error error -> queryError error
        | "getType" ->
            recordQuery ()
            let typeId = JsonInput.stableId<TypeTag> "typeId" (JsonInput.requiredString "typeId" root)
            match AgentWorkspace.getType workspace typeId with
            | Ok value -> queryResult None (Encode.semanticType value)
            | Error error -> queryError error
        | "getCallers" ->
            recordQuery ()
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getCallers workspace functionId with
            | Ok values -> queryResult None (values |> List.map Encode.callSite |> ArtifactJson.arrayValue)
            | Error error -> queryError error
        | "getCallees" ->
            recordQuery ()
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getCallees workspace functionId with
            | Ok values -> queryResult None (values |> List.map Encode.callSite |> ArtifactJson.arrayValue)
            | Error error -> queryError error
        | "getOpenObligations" ->
            recordQuery ()
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.getOpenObligations workspace functionId with
            | Ok values -> queryResult None (values |> List.map Encode.obligation |> ArtifactJson.arrayValue)
            | Error error -> queryError error
        | "getDependencies" ->
            recordQuery ()
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            let nodeId = JsonInput.stableId<GraphNodeTag> "nodeId" (JsonInput.requiredString "nodeId" root)
            match AgentWorkspace.getDependencies workspace functionId nodeId with
            | Ok values -> queryResult None (values |> List.map (StableId.value >> box) |> ArtifactJson.arrayValue)
            | Error error -> queryError error
        | "getUses" ->
            recordQuery ()
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            let nodeId = JsonInput.stableId<GraphNodeTag> "nodeId" (JsonInput.requiredString "nodeId" root)
            match AgentWorkspace.getUses workspace functionId nodeId with
            | Ok values -> queryResult None (values |> List.map (StableId.value >> box) |> ArtifactJson.arrayValue)
            | Error error -> queryError error
        | "focusFunction" ->
            recordFocusQuery ()
            let functionId =
                JsonInput.stableId<FunctionTag> "functionId" (JsonInput.requiredString "functionId" root)
            match AgentWorkspace.focusFunction workspace functionId with
            | Ok value -> queryResult (Some(Encode.focusEntityCount value)) (Encode.focus value)
            | Error error -> queryError error
        | "simulate" ->
            recordSimulation ()
            let operations = JsonInput.requiredArray "operations" root |> List.map JsonInput.operation
            match AgentWorkflow.simulate workspace operations with
            | Error(AgentWorkflowError.SimulationRejected diagnostics) ->
                recordRejection (fun () ->
                    usage <-
                        { usage with
                            RejectedSimulationCount = usage.RejectedSimulationCount + 1 })
                ArtifactJson.objectValue
                    [ "status", box "rejected"
                      "diagnostics", Encode.diagnostics diagnostics ]
            | Error error ->
                recordRejection (fun () ->
                    usage <-
                        { usage with
                            RejectedSimulationCount = usage.RejectedSimulationCount + 1 })
                ArtifactJson.objectValue
                    [ "status", box "error"
                      "errorKind", box "workflow"
                      "detail", box (sprintf "%A" error) ]
            | Ok proposal ->
                let handle = proposalHandle ()
                let review = AgentChangeProposal.review proposal
                let result =
                    ArtifactJson.objectValue
                        [ "status", box "proposed"
                          "proposalHandle", box handle
                          "transactionId", box (StableId.value (AgentChangeProposal.transactionId proposal))
                          "proposedSnapshotId", box (StableId.value (AgentChangeProposal.proposedSnapshotId proposal))
                          "review", Encode.review review ]
                let isTriggerProposal =
                    match config.StalePerturbation, review.ResultingChange.Diff with
                    | Some perturbation,
                      { Renames = [ FunctionRenamed(functionId, _, newDisplayName) ]
                        ParametersAdded = []
                        ArgumentsAttached = []
                        ObligationsCreated = []
                        ObligationsResolved = [] } ->
                        functionId = perturbation.TriggerFunctionId
                        && newDisplayName = perturbation.TriggerNewDisplayName
                    | _ -> false

                if applyHarnessPerturbation isTriggerProposal then
                    proposals <- Map.add handle proposal proposals
                    result
                else
                    tryWriteRunState "inProgress" "run-state" |> ignore
                    harnessFailureResult ()
        | "commit" -> commitWith AgentWorkflow.commit root
        | "commitAllowIncomplete" ->
            commitWith
                (fun current proposal ->
                    AgentWorkflow.commitWithPolicy current proposal CommitPolicy.AllowBlockingObligations)
                root
        | "reject" ->
            recordRejection (fun () ->
                usage <-
                    { usage with
                        AgentRejectCount = usage.AgentRejectCount + 1 })
            let handle = JsonInput.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            let nextWorkspace, receipt = AgentWorkflow.reject workspace proposal
            workspace <- nextWorkspace
            proposals <- Map.remove handle proposals
            ArtifactJson.objectValue
                [ "status", box "rejectedByAgent"
                  "proposalHandle", box handle
                  "transactionId", box (StableId.value receipt.TransactionId)
                  "retainedSnapshotId", box (StableId.value receipt.RetainedSnapshotId) ]
        | "metrics" ->
            ArtifactJson.objectValue [ "status", box "ok"; "metrics", agentMetrics () ]
        | value -> raise (JsonInput.ProtocolInputException $"Unsupported command '{value}'.")

    do
        match artifactWriter with
        | None -> ()
        | Some writer ->
            let metricsWritten =
                tryArtifactWrite "metrics" (fun () ->
                    ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)

            if not metricsWritten then
                tryWriteRunState "inProgress" "run-state" |> ignore

            let stateWritten = tryWriteRunState "inProgress" "run-state"

            if not stateWritten then
                tryArtifactWrite "metrics" (fun () ->
                    ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)
                |> ignore

    member _.Usage: SessionUsage = usageSnapshot ()

    member _.ArtifactStatus: SessionArtifactStatus = artifactStatus ()

    member _.ArtifactPaths: SessionArtifactPaths option =
        artifactWriter |> Option.map (fun writer -> writer.Paths)

    member _.RunMetadata: SessionArtifactMetadata = runMetadata

    member _.VerificationReports: VerificationReport list =
        List.rev verificationReports

    member _.ExecuteLine(line: string): string =
        if finalizationAttempted then
            ArtifactJson.objectValue
                [ "ok", box false
                  "error",
                    ArtifactJson.objectValue
                        [ "code", box "ProtocolError"
                          "message", box "Session has already been finalized." ] ]
            |> ArtifactJson.serialize
        else
            let request = if isNull line then String.Empty else line
            let requestBytes = ArtifactJson.utf8ByteCount request
            usage <-
                { usage with
                    ToolCalls = usage.ToolCalls + 1
                    EventCount = usage.EventCount + 1
                    RequestBytes = usage.RequestBytes + requestBytes }

            let contextBytesBefore = usage.ContextBytes
            let focusContextBytesBefore = usage.FocusContextBytes
            let focusEntityCountBefore = usage.FocusEntityCount
            let harnessReceiptCountBefore = usage.HarnessReceipts.Length

            let body =
                try
                    if String.IsNullOrWhiteSpace line then
                        raise (JsonInput.ProtocolInputException "Input line must contain one JSON command.")

                    use document = JsonInput.parseDocument line
                    let result = executeCommand document.RootElement
                    [ "ok", box true
                      "sequence", box usage.EventCount
                      "result", result ]
                with
                | JsonInput.ProtocolInputException message ->
                    usage <-
                        { usage with
                            ProtocolErrorCount = usage.ProtocolErrorCount + 1 }
                    [ "ok", box false
                      "sequence", box usage.EventCount
                      "error",
                        ArtifactJson.objectValue
                            [ "code", box "ProtocolError"
                              "message", box message ] ]

            recordEvent
                request
                body
                (usage.ContextBytes - contextBytesBefore)
                (usage.FocusContextBytes - focusContextBytesBefore)
                (usage.FocusEntityCount - focusEntityCountBefore)
                harnessReceiptCountBefore

    member _.Finalize(): SessionArtifactStatus =
        if not finalizationAttempted then
            finalizationAttempted <- true

            match artifactWriter with
            | None -> ()
            | Some writer ->
                let pendingWritten = tryWriteRunState "terminalPending" "finalization"

                if not pendingWritten then
                    tryArtifactWrite "metrics" (fun () ->
                        ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)
                    |> ignore

                    let invalidatedTerminalWritten = tryWriteRunState "terminal" "run-state"
                    finalizationDurable <- invalidatedTerminalWritten
                else
                    let metricsWritten =
                        tryArtifactWrite "metrics" (fun () ->
                            ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)

                    if metricsWritten then
                        let terminalWritten = tryWriteRunState "terminal" "finalization"
                        finalizationDurable <- terminalWritten

                        if not terminalWritten then
                            tryArtifactWrite "metrics" (fun () ->
                                ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)
                            |> ignore

                            let invalidatedTerminalWritten = tryWriteRunState "terminal" "run-state"
                            finalizationDurable <- invalidatedTerminalWritten
                    else
                        finalizationDurable <- false
                        let invalidatedTerminalWritten = tryWriteRunState "terminal" "run-state"
                        finalizationDurable <- invalidatedTerminalWritten

                if finalizationDurable then
                    let metricsWritten =
                        tryArtifactWrite "metrics" (fun () ->
                            ArtifactJson.serialize (artifactMetrics ()) |> writer.WriteMetrics)

                    if not metricsWritten then
                        tryWriteRunState "terminal" "run-state" |> ignore

        artifactStatus ()

[<RequireQualifiedAccess>]
module Session =
    let private verificationArtifactIssue: ValidationIssue =
        { Code = "VerificationRequiresArtifacts"
          Message = "A verification fixture requires an artifact directory." }

    let private unavailable = "unavailable"

    let private suppliedValue value =
        match value with
        | Some item when not (isNull item) -> item
        | _ -> unavailable

    let private artifactMetadata
        (workspace: AgentWorkspace)
        (supplied: SessionRunMetadata)
        : Result<SessionArtifactMetadata, string> =
        match
            AgentWorkspace.currentSnapshotFingerprint workspace,
            AgentWorkspace.currentSemanticFingerprint workspace
        with
        | Ok startingSnapshotFingerprint, Ok startingSemanticFingerprint ->
            Ok
                { StartingSnapshotId = AgentWorkspace.currentSnapshotId workspace
                  StartingSnapshotFingerprint = startingSnapshotFingerprint
                  StartingSemanticFingerprint = startingSemanticFingerprint
                  Model = suppliedValue supplied.Model
                  ProviderRuntime = suppliedValue supplied.ProviderRuntime
                  ReasoningConfig = suppliedValue supplied.ReasoningConfig
                  OpenCodeVersion = suppliedValue supplied.OpenCodeVersion
                  PromptRevision = suppliedValue supplied.PromptRevision
                  ToolRevision = suppliedValue supplied.ToolRevision }
        | Error error, _
        | _, Error error -> Error(sprintf "%A" error)

    let create (snapshot: Snapshot) (config: SessionConfig): Result<Session, ValidationIssue list> =
        if Option.isSome config.VerificationFixture then
            Error [ verificationArtifactIssue ]
        else
            match AgentWorkspace.create snapshot with
            | Error issues -> Error issues
            | Ok workspace ->
                match artifactMetadata workspace config.RunMetadata with
                | Ok runMetadata -> Ok(Session(workspace, config, runMetadata, None))
                | Error error ->
                    Error
                        [ { Code = "SessionMetadataInitializationFailed"
                            Message = $"Session metadata initialization failed: {error}." } ]

    let createWithArtifacts
        (snapshot: Snapshot)
        (config: SessionConfig)
        (artifactDirectory: string option)
        : Result<Session, SessionCreationError> =
        match config.VerificationFixture, artifactDirectory with
        | Some _, None ->
            Error(SessionCreationError.InvalidConfiguration "A verification fixture requires an artifact directory.")
        | _ ->
            match AgentWorkspace.create snapshot with
            | Error issues -> Error(SessionCreationError.WorkspaceValidation issues)
            | Ok workspace ->
                match artifactMetadata workspace config.RunMetadata with
                | Error error -> Error(SessionCreationError.ArtifactInitializationFailed error)
                | Ok runMetadata ->
                    match artifactDirectory with
                    | None -> Ok(Session(workspace, config, runMetadata, None))
                    | Some directory ->
                        match ArtifactWriter.create directory runMetadata with
                        | Error error -> Error(SessionCreationError.ArtifactInitializationFailed error)
                         | Ok writer ->
                             try
                                 Ok(Session(workspace, config, runMetadata, Some writer))
                             with
                             | _ -> Error(SessionCreationError.ArtifactInitializationFailed "Artifact initialization failed.")

    let createWithArtifactWriter
        (snapshot: Snapshot)
        (config: SessionConfig)
        (writer: SessionArtifactWriter)
        : Result<Session, SessionCreationError> =
        match AgentWorkspace.create snapshot with
        | Error issues -> Error(SessionCreationError.WorkspaceValidation issues)
        | Ok workspace ->
            match artifactMetadata workspace config.RunMetadata with
            | Error error -> Error(SessionCreationError.ArtifactInitializationFailed error)
            | Ok runMetadata ->
                try
                    ArtifactJson.serialize (Encode.runMetadata runMetadata)
                    |> writer.WriteRunMetadata
                    Ok(Session(workspace, config, runMetadata, Some writer))
                with
                | _ -> Error(SessionCreationError.ArtifactInitializationFailed "Artifact initialization failed.")

    let createWithArtifactDirectory snapshot config directory =
        createWithArtifacts snapshot config (Some directory)

    let usage (session: Session): SessionUsage = session.Usage

    let artifactPaths (session: Session): SessionArtifactPaths option = session.ArtifactPaths

    let runMetadata (session: Session): SessionArtifactMetadata = session.RunMetadata

    let artifactStatus (session: Session): SessionArtifactStatus = session.ArtifactStatus

    let finalize (session: Session): SessionArtifactStatus = session.Finalize()
