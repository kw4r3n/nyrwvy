namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Text.Json
open Nyrwvy.Core

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

    let private pureValue (item: JsonElement): PureRunValue =
        match requiredString "kind" item with
        | "string" ->
            ensureFields "String run value" [ "kind"; "value" ] [ "kind"; "value" ] item
            PureRunValue.String(requiredString "value" item)
        | "bool" ->
            ensureFields "Bool run value" [ "kind"; "value" ] [ "kind"; "value" ] item
            PureRunValue.Bool(requiredBool "value" item)
        | "int64" ->
            ensureFields "Int64 run value" [ "kind"; "value" ] [ "kind"; "value" ] item
            PureRunValue.Int64(requiredInt64 "value" item)
        | "list" ->
            ensureFields "List run value" [ "kind"; "itemKind"; "items" ] [ "kind"; "itemKind"; "items" ] item
            let items = array "items" item
            if items.Length > 4096 then fail "List run values may contain at most 4096 items."
            match requiredString "itemKind" item with
            | "string" ->
                PureRunValue.StringList(items |> List.map (fun value ->
                    if value.ValueKind <> JsonValueKind.String then fail "String List items must be strings."
                    value.GetString()))
            | "bool" ->
                PureRunValue.BoolList(items |> List.map (fun value ->
                    if value.ValueKind <> JsonValueKind.True && value.ValueKind <> JsonValueKind.False then fail "Bool List items must be booleans."
                    value.GetBoolean()))
            | "int64" ->
                PureRunValue.Int64List(items |> List.map (fun value ->
                    let parsed =
                        match value.ValueKind with
                        | JsonValueKind.Number -> value.TryGetInt64()
                        | JsonValueKind.String -> Int64.TryParse(value.GetString(), Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture)
                        | _ -> false, 0L
                    match parsed with
                    | true, number -> number
                    | _ -> fail "Int64 List items must be exact integers or decimal strings."))
            | _ -> fail "List itemKind must be string, bool or int64."
        | value -> fail $"Unsupported pure run value kind '{value}'."

    let private encodePureValue value =
        let listValue kind items =
            objectValue [ "kind", box "list"; "itemKind", box kind; "items", arrayValue items ]
        match value with
        | PureRunValue.StringList items -> listValue "string" (items |> List.map box)
        | PureRunValue.BoolList items -> listValue "bool" (items |> List.map box)
        | PureRunValue.Int64List items -> listValue "int64" (items |> List.map (fun item -> box (item.ToString(Globalization.CultureInfo.InvariantCulture))))
        | PureRunValue.String value -> objectValue [ "kind", box "string"; "value", box value ]
        | PureRunValue.Bool value -> objectValue [ "kind", box "bool"; "value", box value ]
        | PureRunValue.Int64 value ->
            objectValue [ "kind", box "int64"; "value", box (value.ToString(Globalization.CultureInfo.InvariantCulture)) ]

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
            let encodeProposal =
                if optionalBool "structuredReview" false root then
                    WireEncode.proposalWith WireGraph.functionEntity WireGraph.focus
                else
                    WireEncode.proposal

            match operationName with
            | "status" ->
                ensureFields "status request" [ "id"; "op" ] [ "id"; "op" ] root
                match session.Status() with
                | Ok value -> success requestId (WireEncode.status value)
                | Error error -> sessionFailure requestId error
            | "dependency" ->
                ensureFields "dependency request" [ "id"; "op" ] [ "id"; "op" ] root
                match session.DependencySelection with
                | None -> success requestId (objectValue [ "selection", null ])
                | Some manifest ->
                    let selected = JsonSerializer.Deserialize<JsonElement>(manifest)
                    success requestId (objectValue [ "selection", box selected ])
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
            | "readFunction" ->
                ensureFields
                    "readFunction request"
                    [ "id"; "op"; "function"; "functionId" ]
                    [ "id"; "op" ]
                    root
                let functionId =
                    match tryProperty "function" root, tryProperty "functionId" root with
                    | Some _, None ->
                        (WireSymbolic.resolveFunction (session.Declarations()) (requiredString "function" root)).Id
                    | None, Some _ -> id<FunctionTag> "functionId" root
                    | _ -> WireInput.fail "readFunction requires exactly one of function and functionId."
                match session.FocusFunction functionId with
                | Ok focus -> success requestId (WireGraph.focus focus)
                | Error error ->
                    failure requestId (objectValue [ "code", box "SemanticQueryFailure"; "message", box (sprintf "%A" error) ])
            | "simulateOperations" ->
                ensureFields
                    "simulateOperations request"
                    [ "id"; "op"; "operations"; "structuredReview" ]
                    [ "id"; "op"; "operations" ]
                    root
                let operations = array "operations" root |> List.map operation
                match session.SimulateOperations operations with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateFunction" ->
                ensureFields
                    "simulateCreateFunction request"
                    [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body"; "structuredReview" ]
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
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateFunctionByName" ->
                ensureFields
                    "simulateCreateFunctionByName request"
                    [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body"; "structuredReview" ]
                    [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ]
                    root
                let declarations = session.Declarations()
                let parameters = WireSymbolic.functionParameters declarations root
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root
                      Parameters = parameters
                      ResultType = WireSymbolic.typeRef declarations (property "resultType" root)
                      RequiredEffects = WireSymbolic.requiredEffects declarations root
                      Body =
                        WireSymbolic.recipeForNewFunction
                            declarations
                            parameters
                            (property "body" root) }
                match session.SimulateCreateFunction request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateParameterTypeMigration" ->
                ensureFields
                    "simulateParameterTypeMigration request"
                    [ "id"; "op"; "targetFunctionId"; "targetParameterId"; "newParameterType"; "targetReplacement"; "callerRewrites"; "structuredReview" ]
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
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateType" ->
                ensureFields
                    "simulateCreateType request"
                    [ "id"; "op"; "displayName"; "typeKind"; "structuredReview" ]
                    [ "id"; "op"; "displayName"; "typeKind" ]
                    root
                let request: AgentCreateTypeDeclarationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root
                      Kind = typeKindIntent (property "typeKind" root) }
                match session.SimulateCreateType request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateTypeByName" ->
                ensureFields
                    "simulateCreateTypeByName request"
                    [ "id"; "op"; "displayName"; "typeKind"; "structuredReview" ]
                    [ "id"; "op"; "displayName"; "typeKind" ]
                    root
                let declarations = session.Declarations()
                let request: AgentCreateTypeDeclarationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root
                      Kind = WireSymbolic.typeKindIntent declarations (property "typeKind" root) }
                match session.SimulateCreateType request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateAddFieldlessUnionVariantLike" ->
                ensureFields
                    "simulateAddFieldlessUnionVariantLike request"
                    [ "id"; "op"; "typeId"; "sourceVariantId"; "displayName"; "structuredReview" ]
                    [ "id"; "op"; "typeId"; "sourceVariantId"; "displayName" ]
                    root
                let request: AgentAddFieldlessUnionVariantLikeRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetTypeId = id<TypeTag> "typeId" root
                      SourceVariantId = id<VariantTag> "sourceVariantId" root
                      NewVariantDisplayName = requiredString "displayName" root }
                match session.SimulateAddFieldlessUnionVariantLike request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateEffect" ->
                ensureFields
                    "simulateCreateEffect request"
                    [ "id"; "op"; "displayName"; "structuredReview" ]
                    [ "id"; "op"; "displayName" ]
                    root
                let request: AgentCreateEffectDeclarationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root }
                match session.SimulateCreateEffect request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateCreateCapability" ->
                ensureFields
                    "simulateCreateCapability request"
                    [ "id"; "op"; "displayName"; "operations"; "structuredReview" ]
                    [ "id"; "op"; "displayName"; "operations" ]
                    root
                let request: AgentCreateCapabilityDeclarationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = requiredString "displayName" root
                      Operations = array "operations" root |> List.map capabilityOperationIntent }
                match session.SimulateCreateCapability request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateReplaceBody" ->
                ensureFields
                    "simulateReplaceBody request"
                    [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement"; "structuredReview" ]
                    [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement" ]
                    root
                let request: AgentReplaceFunctionBodyWithExpressionRecipe =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      ExpectedCurrentResultNodeId = id<GraphNodeTag> "expectedCurrentResultNodeId" root
                      Replacement = recipe (property "replacement" root) }
                match session.SimulateReplaceBody request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateReplaceBodyByName" ->
                ensureFields
                    "simulateReplaceBodyByName request"
                    [ "id"; "op"; "function"; "replacement"; "structuredReview" ]
                    [ "id"; "op"; "function"; "replacement" ]
                    root
                let declarations = session.Declarations()
                let functionEntity =
                    WireSymbolic.resolveFunction declarations (requiredString "function" root)
                let request: AgentReplaceFunctionBodyWithExpressionRecipe =
                    { TargetFunctionId = functionEntity.Id
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      ExpectedCurrentResultNodeId = functionEntity.Body.ResultNodeId
                      Replacement =
                        WireSymbolic.recipeForExistingFunction
                            declarations
                            functionEntity
                            (property "replacement" root) }
                match session.SimulateReplaceBody request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateReplaceTarget" ->
                ensureFields
                    "simulateReplaceTarget request"
                    [ "id"; "op"; "functionId"; "targetNodeId"; "replacement"; "structuredReview" ]
                    [ "id"; "op"; "functionId"; "targetNodeId"; "replacement" ]
                    root
                let request: AgentExpressionStructuralWorkflow.Targeted.Request =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetNodeId = id<GraphNodeTag> "targetNodeId" root
                      Replacement = recipe (property "replacement" root) }
                match session.SimulateReplaceTarget request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateReplaceTargetByName" ->
                ensureFields
                    "simulateReplaceTargetByName request"
                    [ "id"; "op"; "function"; "targetNodeId"; "replacement"; "structuredReview" ]
                    [ "id"; "op"; "function"; "targetNodeId"; "replacement" ]
                    root
                let declarations = session.Declarations()
                let functionEntity = WireSymbolic.resolveFunction declarations (requiredString "function" root)
                let request: AgentExpressionStructuralWorkflow.Targeted.Request =
                    { TargetFunctionId = functionEntity.Id
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetNodeId = id<GraphNodeTag> "targetNodeId" root
                      Replacement = WireSymbolic.recipeForExistingFunction declarations functionEntity (property "replacement" root) }
                match session.SimulateReplaceTarget request with
                | Ok proposal -> success requestId (encodeProposal proposal)
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
            | "runPure" ->
                ensureFields
                    "runPure request"
                    [ "id"; "op"; "functionId"; "arguments"; "expected" ]
                    [ "id"; "op"; "functionId"; "arguments" ]
                    root
                let functionId = id<FunctionTag> "functionId" root
                let arguments = array "arguments" root |> List.map pureValue
                let expected = tryProperty "expected" root |> Option.map pureValue
                match PureRun.run session functionId arguments expected with
                | Ok result ->
                    success requestId
                        (objectValue
                            [ "sourceSnapshotId", box (StableId.value result.SnapshotId)
                              "functionId", box (StableId.value result.FunctionId)
                              "actual", encodePureValue result.Actual
                              "expected", result.Expected |> Option.map encodePureValue |> Option.defaultValue null
                              "matches", result.Matches |> Option.map box |> Option.defaultValue null ])
                | Error(PureRunError.SessionFailure error) -> sessionFailure requestId error
                | Error(PureRunError.Unsupported(code, message)) ->
                    failure requestId
                        (objectValue
                            [ "code", box code
                              "message", box message
                              "functionId", box (StableId.value functionId) ])
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
                    | "externalImplementations" ->
                        ensureFields
                            "External C# implementations profile"
                            [ "kind"; "operationIds" ]
                            [ "kind"; "operationIds" ]
                            profileValue
                        let operationIds =
                            array "operationIds" profileValue
                            |> List.map (fun element ->
                                if element.ValueKind <> JsonValueKind.String then
                                    WireInput.fail "External operation ID must be a string."
                                stableId<CapabilityOperationTag> "operationId" (element.GetString()))
                        CSharpBackendProfile.externalImplementations operationIds
                    | value -> WireInput.fail $"Unsupported C# profile '{value}'."
                match session.EmitCSharp profile with
                | Ok artifact -> success requestId (WireEncode.csharpArtifact artifact)
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
