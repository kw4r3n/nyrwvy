namespace NyamNyam.Tools.AuthoringSession

open System
open System.Text.Json
open NyamNyam.Core

[<RequireQualifiedAccess>]
module AuthoringProtocol =
    open WireInput
    open WireJson

    let private success id result =
        objectValue
            [ "id", box id
              "ok", box true
              "result", result ]
        |> serialize

    let private failure id error =
        objectValue
            [ "id", if isNull id then null else box id
              "ok", box false
              "error", error ]
        |> serialize

    let private sessionFailure id error = failure id (WireEncode.error error)

    let private currentSnapshotId (session: AuthoringSession) =
        match session.Status() with
        | Ok status -> status.SnapshotId
        | Error error -> raise (InvalidOperationException(sprintf "%A" error))

    let private typeFieldIntent (item: JsonElement): AgentTypeFieldIntent =
        ensureFields
            "type field"
            [ "displayName"; "type" ]
            [ "displayName"; "type" ]
            item
        { DisplayName = requiredString "displayName" item
          Type = typeRef (property "type" item) }

    let private typeKindIntent (element: JsonElement): AgentTypeDeclarationKindIntent =
        match requiredString "kind" element with
        | "product" ->
            ensureFields
                "product type kind"
                [ "kind"; "fields" ]
                [ "kind"; "fields" ]
                element
            AgentTypeDeclarationKindIntent.Product(
                array "fields" element |> List.map typeFieldIntent
            )
        | "union" ->
            ensureFields
                "union type kind"
                [ "kind"; "variants" ]
                [ "kind"; "variants" ]
                element
            let variants =
                array "variants" element
                |> List.map (fun item ->
                    ensureFields
                        "union variant"
                        [ "displayName"; "fields" ]
                        [ "displayName"; "fields" ]
                        item
                    { DisplayName = requiredString "displayName" item
                      Fields = array "fields" item |> List.map typeFieldIntent })
            AgentTypeDeclarationKindIntent.Union variants
        | value -> WireInput.fail $"Unsupported type declaration kind '{value}'."

    let private capabilityParameterIntent (item: JsonElement): AgentCapabilityParameterIntent =
        ensureFields
            "capability parameter"
            [ "displayName"; "type" ]
            [ "displayName"; "type" ]
            item
        { DisplayName = requiredString "displayName" item
          Type = typeRef (property "type" item) }

    let private capabilityOperationIntent (item: JsonElement): AgentCapabilityOperationIntent =
        ensureFields
            "capability operation"
            [ "displayName"; "parameters"; "resultType"; "failureType"; "effectId" ]
            [ "displayName"; "parameters"; "resultType"; "effectId" ]
            item
        let failureType =
            match tryProperty "failureType" item with
            | None -> None
            | Some value when value.ValueKind = JsonValueKind.Null -> None
            | Some value -> Some(typeRef value)
        { DisplayName = requiredString "displayName" item
          Parameters = array "parameters" item |> List.map capabilityParameterIntent
          ResultType = typeRef (property "resultType" item)
          FailureType = failureType
          EffectId = id<EffectTag> "effectId" item }

    let private parameterTypeCallerRewrite (item: JsonElement): AgentParameterTypeCallerRewrite =
        ensureFields
            "parameter type caller rewrite"
            [ "callerFunctionId"; "replacement" ]
            [ "callerFunctionId"; "replacement" ]
            item
        { CallerFunctionId = id<FunctionTag> "callerFunctionId" item
          Replacement = recipe (property "replacement" item) }

    let executeLine (session: AuthoringSession) line =
        let mutable requestId: string = null
        try
            use document = parseDocument line
            let root = document.RootElement
            requestId <- requiredString "id" root
            let operationName = requiredString "op" root

            match operationName with
            | "status" ->
                ensureFields "status request" [ "id"; "op" ] [ "id"; "op" ] root
                match session.Status() with
                | Ok value -> success requestId (WireEncode.status value)
                | Error error -> sessionFailure requestId error
            | "declarations" ->
                ensureFields "declarations request" [ "id"; "op" ] [ "id"; "op" ] root
                success requestId (WireEncode.declarations (session.Declarations()))
            | "focusFunction" ->
                ensureFields
                    "focusFunction request"
                    [ "id"; "op"; "functionId" ]
                    [ "id"; "op"; "functionId" ]
                    root
                match session.FocusFunction(id<FunctionTag> "functionId" root) with
                | Ok focus -> success requestId (WireEncode.focus focus)
                | Error error ->
                    failure
                        requestId
                        (objectValue
                            [ "code", box "SemanticQueryFailure"
                              "message", box (sprintf "%A" error) ])
            | "simulateOperations" ->
                ensureFields
                    "simulateOperations request"
                    [ "id"; "op"; "operations" ]
                    [ "id"; "op"; "operations" ]
                    root
                let operations = array "operations" root |> List.map operation
                match session.SimulateOperations operations with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateFunction" ->
                ensureFields
                    "simulateCreateFunction request"
                    [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ]
                    [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ]
                    root
                let parameters =
                    array "parameters" root
                    |> List.map (fun item ->
                        ensureFields
                            "function parameter"
                            [ "displayName"; "type" ]
                            [ "displayName"; "type" ]
                            item
                        let parameter: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
                            { DisplayName = requiredString "displayName" item
                              Type = typeRef (property "type" item) }
                        parameter)
                let requiredEffects =
                    array "requiredEffects" root
                    |> List.map (fun item ->
                        if item.ValueKind <> JsonValueKind.String then
                            WireInput.fail "requiredEffects entries must be stable-ID strings."
                        stableId<EffectTag> "requiredEffects" (item.GetString()))
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root
                      Parameters = parameters
                      ResultType = typeRef (property "resultType" root)
                      RequiredEffects = requiredEffects
                      Body = recipe (property "body" root) }
                match session.SimulateCreateFunction request with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateParameterTypeMigration" ->
                ensureFields
                    "simulateParameterTypeMigration request"
                    [ "id"; "op"; "targetFunctionId"; "targetParameterId"; "newParameterType"; "targetReplacement"; "callerRewrites" ]
                    [ "id"; "op"; "targetFunctionId"; "targetParameterId"; "newParameterType"; "targetReplacement"; "callerRewrites" ]
                    root
                let request: AgentParameterTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetFunctionId = id<FunctionTag> "targetFunctionId" root
                      TargetParameterId = id<ParameterTag> "targetParameterId" root
                      NewParameterType = typeRef (property "newParameterType" root)
                      TargetReplacement = recipe (property "targetReplacement" root)
                      CallerRewrites = array "callerRewrites" root |> List.map parameterTypeCallerRewrite }
                match session.SimulateParameterTypeMigration request with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateType" ->
                ensureFields
                    "simulateCreateType request"
                    [ "id"; "op"; "displayName"; "typeKind" ]
                    [ "id"; "op"; "displayName"; "typeKind" ]
                    root
                let request: AgentCreateTypeDeclarationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root
                      Kind = typeKindIntent (property "typeKind" root) }
                match session.SimulateCreateType request with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateEffect" ->
                ensureFields
                    "simulateCreateEffect request"
                    [ "id"; "op"; "displayName" ]
                    [ "id"; "op"; "displayName" ]
                    root
                let request: AgentCreateEffectDeclarationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root }
                match session.SimulateCreateEffect request with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateCapability" ->
                ensureFields
                    "simulateCreateCapability request"
                    [ "id"; "op"; "displayName"; "operations" ]
                    [ "id"; "op"; "displayName"; "operations" ]
                    root
                let request: AgentCreateCapabilityDeclarationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root
                      Operations = array "operations" root |> List.map capabilityOperationIntent }
                match session.SimulateCreateCapability request with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateReplaceBody" ->
                ensureFields
                    "simulateReplaceBody request"
                    [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement" ]
                    [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement" ]
                    root
                let request: AgentReplaceFunctionBodyWithExpressionRecipe =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      ExpectedCurrentResultNodeId = id<GraphNodeTag> "expectedCurrentResultNodeId" root
                      Replacement = recipe (property "replacement" root) }
                match session.SimulateReplaceBody request with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateReplaceTarget" ->
                ensureFields
                    "simulateReplaceTarget request"
                    [ "id"; "op"; "functionId"; "targetNodeId"; "replacement" ]
                    [ "id"; "op"; "functionId"; "targetNodeId"; "replacement" ]
                    root
                let request: AgentExpressionStructuralWorkflow.Targeted.Request =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetNodeId = id<GraphNodeTag> "targetNodeId" root
                      Replacement = recipe (property "replacement" root) }
                match session.SimulateReplaceTarget request with
                | Ok proposal -> success requestId (WireEncode.proposal proposal)
                | Error error -> sessionFailure requestId error
            | "commit" ->
                ensureFields
                    "commit request"
                    [ "id"; "op"; "proposalId"; "allowIncomplete" ]
                    [ "id"; "op"; "proposalId" ]
                    root
                let policy =
                    if optionalBool "allowIncomplete" false root then
                        CommitPolicy.AllowBlockingObligations
                    else
                        CommitPolicy.RejectBlockingObligations
                match session.Commit(requiredString "proposalId" root, policy) with
                | Ok receipt -> success requestId (WireEncode.commitReceipt receipt)
                | Error error -> sessionFailure requestId error
            | "reject" ->
                ensureFields
                    "reject request"
                    [ "id"; "op"; "proposalId" ]
                    [ "id"; "op"; "proposalId" ]
                    root
                match session.Reject(requiredString "proposalId" root) with
                | Ok receipt -> success requestId (WireEncode.rejectionReceipt receipt)
                | Error error -> sessionFailure requestId error
            | "executionReadiness" ->
                ensureFields
                    "executionReadiness request"
                    [ "id"; "op" ]
                    [ "id"; "op" ]
                    root
                match session.ProjectExecution() with
                | Ok execution ->
                    success
                        requestId
                        (objectValue
                            [ "kind", box "ready"
                              "sourceSnapshotId", box (StableId.value execution.SourceSnapshotId)
                              "functionCount", box execution.Functions.Length ])
                | Error error -> sessionFailure requestId error
            | "emitCSharp" ->
                ensureFields
                    "emitCSharp request"
                    [ "id"; "op"; "profile" ]
                    [ "id"; "op"; "profile" ]
                    root
                let profileValue = property "profile" root
                let profile =
                    match requiredString "kind" profileValue with
                    | "empty" ->
                        ensureFields "empty C# profile" [ "kind" ] [ "kind" ] profileValue
                        CSharpBackendProfile.empty
                    | "systemConsoleWriteLine" ->
                        ensureFields
                            "System.Console.WriteLine C# profile"
                            [ "kind"; "operationId" ]
                            [ "kind"; "operationId" ]
                            profileValue
                        CSharpBackendProfile.systemConsoleWriteLine(
                            id<CapabilityOperationTag> "operationId" profileValue
                        )
                    | value -> WireInput.fail $"Unsupported C# profile '{value}'."
                match session.EmitCSharp profile with
                | Ok artifact -> success requestId (objectValue [ "source", box artifact.Source ])
                | Error error -> sessionFailure requestId error
            | value ->
                failure
                    requestId
                    (objectValue
                        [ "code", box "UnsupportedOperation"
                          "message", box $"Unsupported operation '{value}'." ])
        with
        | WireInput.ProtocolInputException message ->
            failure
                requestId
                (objectValue
                    [ "code", box "ProtocolError"
                      "message", box message ])
        | :? InvalidOperationException as error ->
            failure
                requestId
                (objectValue
                    [ "code", box "SessionError"
                      "message", box error.Message ])
