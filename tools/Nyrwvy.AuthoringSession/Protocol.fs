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

    let private pureScalar (item: JsonElement): PureRunScalar =
        ensureFields "Scalar run value" [ "kind"; "value" ] [ "kind"; "value" ] item
        match requiredString "kind" item with
        | "string" -> PureRunScalar.String(requiredString "value" item)
        | "bool" -> PureRunScalar.Bool(requiredBool "value" item)
        | "int64" -> PureRunScalar.Int64(requiredInt64 "value" item)
        | _ -> fail "Scalar run values must be string, bool or int64."

    let rec private pureValueAt (remaining: int ref) depth (item: JsonElement): PureRunValue =
        if item.ValueKind <> JsonValueKind.Object then fail "Run values must be non-null typed objects."
        let kind = requiredString "kind" item
        if depth >= PureRun.maxValueLayers && kind <> "string" && kind <> "bool" && kind <> "int64" && kind <> "unit" then
            fail $"Run values may have at most {PureRun.maxValueLayers} nested composite layers."
        let children =
            match kind with
            | "list" when tryProperty "itemKind" item |> Option.isSome -> (array "items" item).Length
            | "product" | "union" -> (array "fields" item).Length
            | "result" -> 1
            | "option" when tryProperty "itemKind" item |> Option.isSome -> if requiredBool "hasValue" item then 1 else 0
            | "fakeCapability" -> array "operations" item |> List.sumBy (fun operation -> (array "responses" operation).Length)
            | _ -> 0
        remaining.Value <- remaining.Value - 1 - children
        if remaining.Value < 0 then fail "Run values may contain at most 8192 value nodes."
        match kind with
        | "list" when tryProperty "itemType" item |> Option.isSome ->
            ensureFields "Nested List run value" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] item
            let items = array "items" item
            if items.Length > 4096 then fail "Lists may contain at most 4096 items."
            PureRunValue.ListValue(typeRef (property "itemType" item), items |> List.map (pureValueAt remaining (depth + 1)))
        | "option" when tryProperty "itemType" item |> Option.isSome ->
            let hasValue = requiredBool "hasValue" item
            let fields = [ "kind"; "itemType"; "hasValue" ] @ (if hasValue then [ "value" ] else [])
            ensureFields "Nested Option run value" fields fields item
            PureRunValue.OptionValue(typeRef (property "itemType" item),
                if hasValue then Some(pureValueAt remaining (depth + 1) (property "value" item)) else None)
        | "unit" ->
            ensureFields "Unit run value" [ "kind" ] [ "kind" ] item
            PureRunValue.Unit
        | "hostCapability" ->
            ensureFields "Host capability" [ "kind"; "capabilityId"; "implementation" ] [ "kind"; "capabilityId"; "implementation" ] item
            PureRunValue.HostCapability(id<CapabilityTag> "capabilityId" item, requiredString "implementation" item)
        | "fakeCapability" ->
            ensureFields "Fake capability" [ "kind"; "capabilityId"; "operations" ] [ "kind"; "capabilityId"; "operations" ] item
            let operations = array "operations" item
            if operations.Length > 256 then fail "A fake capability may script at most 256 operations."
            PureRunValue.FakeCapability(
                id<CapabilityTag> "capabilityId" item,
                operations |> List.map (fun operation ->
                    ensureFields "Fake operation" [ "operationId"; "responses" ] [ "operationId"; "responses" ] operation
                    let responses = array "responses" operation
                    if responses.Length > 256 then fail "A fake operation may script at most 256 responses."
                    { OperationId = id<CapabilityOperationTag> "operationId" operation
                      Responses =
                        responses |> List.map (fun response ->
                            match requiredString "kind" response with
                            | "return" ->
                                ensureFields "Fake return" [ "kind"; "value" ] [ "kind"; "value" ] response
                                PureRunFakeResponse.Return(pureValueAt remaining (depth + 1) (property "value" response))
                            | "throw" ->
                                ensureFields "Fake throw" [ "kind"; "message" ] [ "kind"; "message" ] response
                                PureRunFakeResponse.Throw(requiredString "message" response)
                            | other -> fail $"Unsupported fake response kind '{other}'.") }))
        | "string" | "bool" | "int64" ->
            match pureScalar item with
            | PureRunScalar.String text -> PureRunValue.String text
            | PureRunScalar.Bool value -> PureRunValue.Bool value
            | PureRunScalar.Int64 value -> PureRunValue.Int64 value
        | "product" | "union" as kind ->
            let properties = [ "kind"; "typeId"; "fields" ] @ (if kind = "union" then [ "variantId" ] else [])
            ensureFields "Named run value" properties properties item
            let fields = array "fields" item
            if fields.Length > 256 then fail "Named run values may contain at most 256 fields."
            let fields = fields |> List.map (fun field ->
                ensureFields "Named run field" [ "fieldId"; "value" ] [ "fieldId"; "value" ] field
                id<FieldTag> "fieldId" field, pureValueAt remaining (depth + 1) (property "value" field))
            let typeId = id<TypeTag> "typeId" item
            if kind = "union" then PureRunValue.Union(typeId, id<VariantTag> "variantId" item, fields)
            else PureRunValue.Product(typeId, fields)
        | "result" when tryProperty "okType" item |> Option.isSome ->
            let fields = [ "kind"; "okType"; "errorType"; "isOk"; "value" ]
            ensureFields "Nested Result run value" fields fields item
            PureRunValue.ResultValue(
                typeRef (property "okType" item),
                typeRef (property "errorType" item),
                requiredBool "isOk" item,
                pureValueAt remaining (depth + 1) (property "value" item))
        | "result" ->
            let fields = [ "kind"; "okKind"; "errorKind"; "isOk"; "value" ]
            ensureFields "Result run value" fields fields item
            let kind name =
                match requiredString name item with
                | "string" -> PureRunScalarKind.String
                | "bool" -> PureRunScalarKind.Bool
                | "int64" -> PureRunScalarKind.Int64
                | _ -> fail "Result okKind and errorKind must be string, bool or int64."
            let okKind, errorKind = kind "okKind", kind "errorKind"
            let isOk = requiredBool "isOk" item
            let value =
                match (if isOk then okKind else errorKind) with
                | PureRunScalarKind.String -> PureRunScalar.String(requiredString "value" item)
                | PureRunScalarKind.Bool -> PureRunScalar.Bool(requiredBool "value" item)
                | PureRunScalarKind.Int64 -> PureRunScalar.Int64(requiredInt64 "value" item)
            if isOk then PureRunValue.ResultOk(errorKind, value)
            else PureRunValue.ResultErr(okKind, value)
        | "option" ->
            let hasValue = requiredBool "hasValue" item
            let fields = [ "kind"; "itemKind"; "hasValue" ] @ (if hasValue then [ "value" ] else [])
            ensureFields "Option run value" fields fields item
            match requiredString "itemKind" item with
            | "string" -> PureRunValue.StringOption(if hasValue then Some(requiredString "value" item) else None)
            | "bool" -> PureRunValue.BoolOption(if hasValue then Some(requiredBool "value" item) else None)
            | "int64" -> PureRunValue.Int64Option(if hasValue then Some(requiredInt64 "value" item) else None)
            | _ -> fail "Option itemKind must be string, bool or int64."
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

    let private pureValue item = pureValueAt (ref 8192) 0 item

    let private encodePureScalar value =
        let kind, payload =
            match value with
            | PureRunScalar.String text -> "string", box text
            | PureRunScalar.Bool value -> "bool", box value
            | PureRunScalar.Int64 number -> "int64", box (number.ToString(Globalization.CultureInfo.InvariantCulture))
        objectValue [ "kind", box kind; "value", payload ]

    let rec private encodePureValue value =
        let listValue kind items =
            objectValue [ "kind", box "list"; "itemKind", box kind; "items", arrayValue items ]
        let optionValue kind item =
            objectValue
                ([ "kind", box "option"; "itemKind", box kind; "hasValue", box (Option.isSome item) ]
                 @ (item |> Option.map (fun value -> [ "value", value ]) |> Option.defaultValue []))
        let resultValue isOk inactiveKind item =
            let kind = function
                | PureRunScalarKind.String -> "string"
                | PureRunScalarKind.Bool -> "bool"
                | PureRunScalarKind.Int64 -> "int64"
            let activeKind, payload =
                match item with
                | PureRunScalar.String text -> "string", box text
                | PureRunScalar.Bool value -> "bool", box value
                | PureRunScalar.Int64 number -> "int64", box (number.ToString(Globalization.CultureInfo.InvariantCulture))
            objectValue
                [ "kind", box "result"
                  "okKind", box (if isOk then activeKind else kind inactiveKind)
                  "errorKind", box (if isOk then kind inactiveKind else activeKind)
                  "isOk", box isOk
                  "value", payload ]
        match value with
        | PureRunValue.Unit -> objectValue [ "kind", box "unit" ]
        | PureRunValue.HostCapability(capabilityId, implementation) ->
            objectValue [ "kind", box "hostCapability"; "capabilityId", box (StableId.value capabilityId); "implementation", box implementation ]
        | PureRunValue.FakeCapability(capabilityId, operations) ->
            let response = function
                | PureRunFakeResponse.Return value -> objectValue [ "kind", box "return"; "value", encodePureValue value ]
                | PureRunFakeResponse.Throw message -> objectValue [ "kind", box "throw"; "message", box message ]
            objectValue
                [ "kind", box "fakeCapability"
                  "capabilityId", box (StableId.value capabilityId)
                  "operations", operations |> List.map (fun operation ->
                      objectValue
                          [ "operationId", box (StableId.value operation.OperationId)
                            "responses", operation.Responses |> List.map response |> arrayValue ]) |> arrayValue ]
        | PureRunValue.ListValue(itemType, items) ->
            objectValue [ "kind", box "list"; "itemType", WireEncodeCommon.typeRef itemType
                          "items", items |> List.map encodePureValue |> arrayValue ]
        | PureRunValue.OptionValue(itemType, item) ->
            objectValue
                ([ "kind", box "option"; "itemType", WireEncodeCommon.typeRef itemType; "hasValue", box (Option.isSome item) ]
                 @ (item |> Option.map (fun value -> [ "value", encodePureValue value ]) |> Option.defaultValue []))
        | PureRunValue.Union(typeId, variantId, fields) ->
            objectValue
                [ "kind", box "union"
                  "typeId", box (StableId.value typeId)
                  "variantId", box (StableId.value variantId)
                  "fields", fields |> List.map (fun (fieldId, value) ->
                      objectValue [ "fieldId", box (StableId.value fieldId); "value", encodePureValue value ]) |> arrayValue ]
        | PureRunValue.Product(typeId, fields) ->
            objectValue
                [ "kind", box "product"
                  "typeId", box (StableId.value typeId)
                  "fields", fields |> List.map (fun (fieldId, value) ->
                      objectValue [ "fieldId", box (StableId.value fieldId); "value", encodePureValue value ]) |> arrayValue ]
        | PureRunValue.ResultValue(okType, errorType, isOk, payload) ->
            objectValue
                [ "kind", box "result"
                  "okType", WireEncodeCommon.typeRef okType
                  "errorType", WireEncodeCommon.typeRef errorType
                  "isOk", box isOk
                  "value", encodePureValue payload ]
        | PureRunValue.ResultOk(errorKind, item) -> resultValue true errorKind item
        | PureRunValue.ResultErr(okKind, item) -> resultValue false okKind item
        | PureRunValue.StringOption item -> optionValue "string" (item |> Option.map box)
        | PureRunValue.BoolOption item -> optionValue "bool" (item |> Option.map box)
        | PureRunValue.Int64Option item -> optionValue "int64" (item |> Option.map (fun number -> box (number.ToString(Globalization.CultureInfo.InvariantCulture))))
        | PureRunValue.StringList items -> listValue "string" (items |> List.map box)
        | PureRunValue.BoolList items -> listValue "bool" (items |> List.map box)
        | PureRunValue.Int64List items -> listValue "int64" (items |> List.map (fun item -> box (item.ToString(Globalization.CultureInfo.InvariantCulture))))
        | PureRunValue.String value -> encodePureScalar (PureRunScalar.String value)
        | PureRunValue.Bool value -> encodePureScalar (PureRunScalar.Bool value)
        | PureRunValue.Int64 value -> encodePureScalar (PureRunScalar.Int64 value)

    let private encodePathStep step =
        match step with
        | PureRunPathStep.Field(typeId, fieldId) ->
            objectValue [ "kind", box "field"; "typeId", box (StableId.value typeId); "fieldId", box (StableId.value fieldId) ]
        | PureRunPathStep.Item index -> objectValue [ "kind", box "item"; "index", box index ]
        | PureRunPathStep.SomeValue -> objectValue [ "kind", box "some" ]
        | PureRunPathStep.OkValue -> objectValue [ "kind", box "ok" ]
        | PureRunPathStep.ErrValue -> objectValue [ "kind", box "err" ]

    let private encodeMismatch (mismatch: PureRunMismatch) =
        objectValue
            [ "path", mismatch.Path |> List.map encodePathStep |> arrayValue
              "expected", encodePureValue mismatch.Expected
              "actual", encodePureValue mismatch.Actual ]

    let private encodeCall (call: PureRunCall) =
        objectValue
            [ "parameterId", box (StableId.value call.ParameterId)
              "operationId", box (StableId.value call.OperationId)
              "arguments", call.Arguments |> List.map (fun (parameterId, value) ->
                  objectValue [ "capabilityParameterId", box (StableId.value parameterId); "value", encodePureValue value ]) |> arrayValue ]

    let private encodeFrame frame =
        match frame with
        | PureRunFrame.Function functionId -> objectValue [ "kind", box "function"; "functionId", box (StableId.value functionId) ]
        | PureRunFrame.CapabilityOperation operationId ->
            objectValue [ "kind", box "capabilityOperation"; "operationId", box (StableId.value operationId) ]

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
        use _scope = SnapshotComputationScope.Start()
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
            | "previewDependencyUpgrade" ->
                ensureFields "previewDependencyUpgrade request" [ "id"; "op"; "source"; "replaces" ] [ "id"; "op"; "source" ] root
                let replaces = tryProperty "replaces" root |> Option.map (fun _ -> requiredString "replaces" root)
                match session.PreviewDependencyUpgrade(requiredString "source" root, replaces) with
                | Error error -> sessionFailure requestId error
                | Ok preview ->
                    let changes (items: AuthoringDependencyChange list) =
                        items |> List.map (fun item -> objectValue [ "kind", box item.Kind; "id", box item.Id ]) |> arrayValue
                    success requestId
                        (objectValue
                            [ "replacedSnapshotId", box preview.ReplacedSnapshotId
                              "replacedFingerprint", box preview.ReplacedFingerprint
                              "newSnapshotId", box preview.NewSnapshotId
                              "newFingerprint", box preview.NewFingerprint
                              "compatible", box (List.isEmpty preview.Issues)
                              "added", changes preview.Added
                              "removed", changes preview.Removed
                              "changed", changes preview.Changed
                              "issues", preview.Issues |> List.map (fun issue -> objectValue [ "code", box issue.Code; "message", box issue.Message ]) |> arrayValue ])
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
                let displayName = requiredString "displayName" root
                if declarations.Functions |> List.exists (fun item -> item.DisplayName = displayName) then
                    WireInput.fail $"Function display name '{displayName}' already exists. Choose a unique name or use the exact-ID authoring surface."
                let parameters = WireSymbolic.functionParameters declarations root
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = displayName
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
            | "simulateResultTypeMigration" ->
                ensureFields
                    "simulateResultTypeMigration request"
                    [ "id"; "op"; "targetFunctionId"; "newResultType"; "targetReplacement"; "callerRewrites"; "structuredReview" ]
                    [ "id"; "op"; "targetFunctionId"; "newResultType"; "targetReplacement"; "callerRewrites" ]
                    root
                let request: AgentResultTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetFunctionId = id<FunctionTag> "targetFunctionId" root
                      NewResultType = typeRef (property "newResultType" root)
                      TargetReplacement = recipe (property "targetReplacement" root)
                      CallerRewrites = array "callerRewrites" root |> List.map parameterTypeCallerRewrite }
                match session.SimulateResultTypeMigration request with
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            | "simulateProductFieldTypeMigration" ->
                ensureFields
                    "simulateProductFieldTypeMigration request"
                    [ "id"; "op"; "fieldId"; "newFieldType"; "functionRewrites"; "structuredReview" ]
                    [ "id"; "op"; "fieldId"; "newFieldType"; "functionRewrites" ]
                    root
                let request: AgentProductFieldTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      FieldId = id<FieldTag> "fieldId" root
                      NewFieldType = typeRef (property "newFieldType" root)
                      FunctionRewrites =
                        array "functionRewrites" root
                        |> List.map (fun item ->
                            ensureFields "function rewrite" [ "functionId"; "replacement" ] [ "functionId"; "replacement" ] item
                            { FunctionId = id<FunctionTag> "functionId" item
                              Replacement = recipe (property "replacement" item) }) }
                match session.SimulateProductFieldTypeMigration request with
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
            | "simulateAddUnionVariantLike" ->
                ensureFields
                    "simulateAddUnionVariantLike request"
                    [ "id"; "op"; "typeId"; "sourceVariantId"; "displayName"; "additionalFields"; "structuredReview" ]
                    [ "id"; "op"; "typeId"; "sourceVariantId"; "displayName" ]
                    root
                let request: AgentAddFieldlessUnionVariantLikeRequest =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetTypeId = id<TypeTag> "typeId" root
                      SourceVariantId = id<VariantTag> "sourceVariantId" root
                      NewVariantDisplayName = requiredString "displayName" root }
                let additionalFields =
                    tryProperty "additionalFields" root
                    |> Option.map (fun _ -> array "additionalFields" root |> List.map typeFieldIntent)
                    |> Option.defaultValue []
                match session.SimulateAddUnionVariantLike(request, additionalFields) with
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
                    [ "id"; "op"; "functionId"; "arguments"; "expected"; "hostSources" ]
                    [ "id"; "op"; "functionId"; "arguments" ]
                    root
                let functionId = id<FunctionTag> "functionId" root
                let arguments = array "arguments" root |> List.map pureValue
                let expected = tryProperty "expected" root |> Option.map pureValue
                let hostSources =
                    tryProperty "hostSources" root
                    |> Option.map (fun _ ->
                        array "hostSources" root |> List.map (fun path ->
                            if path.ValueKind <> JsonValueKind.String then fail "hostSources must contain file paths."
                            path.GetString()))
                    |> Option.defaultValue []
                match PureRun.runWithHost session functionId arguments expected hostSources with
                | Ok result ->
                    success requestId
                        (objectValue
                            [ "sourceSnapshotId", box (StableId.value result.SnapshotId)
                              "functionId", box (StableId.value result.FunctionId)
                              "actual", encodePureValue result.Actual
                              "expected", result.Expected |> Option.map encodePureValue |> Option.defaultValue null
                              "matches", result.Matches |> Option.map box |> Option.defaultValue null
                              "mismatch", result.Mismatch |> Option.map encodeMismatch |> Option.defaultValue null
                              "calls", result.Calls |> List.map encodeCall |> arrayValue ])
                | Error(PureRunError.SessionFailure error) -> sessionFailure requestId error
                | Error(PureRunError.RuntimeFailure runtime) ->
                    failure requestId
                        (objectValue
                            [ "code", box (if runtime.BindingExhausted then "RunBindingExhausted" else "RunRuntimeFailure")
                              "message", box runtime.Message
                              "functionId", box (StableId.value runtime.FunctionId)
                              "sourceSnapshotId", box (StableId.value runtime.SnapshotId)
                              "exceptionType", box runtime.ExceptionType
                              "frames", runtime.Frames |> List.map encodeFrame |> arrayValue
                              "calls", runtime.Calls |> List.map encodeCall |> arrayValue ])
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
