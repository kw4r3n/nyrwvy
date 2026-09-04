namespace NyamNyam.AgentEval.PureExpression

open System
open System.Collections.Generic
open System.Text.Json
open NyamNyam.Core

[<RequireQualifiedAccess>]
module PureExpressionProtocol =
    let version = "pure-expression-control-flow-v1"

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
            let names = String.concat ", " unknown
            fail $"{context} contains unsupported properties: {names}."
        if not (List.isEmpty missing) then
            let names = String.concat ", " missing
            fail $"{context} is missing required properties: {names}."

    let property (name: string) (element: JsonElement): JsonElement =
        match element.TryGetProperty(name) with
        | true, value -> value
        | false, _ -> fail $"Missing required property '{name}'."

    let requiredString (name: string) (element: JsonElement): string =
        let value = property name element
        if value.ValueKind <> JsonValueKind.String then fail $"Property '{name}' must be a JSON string."
        value.GetString()

    let requiredBool (name: string) (element: JsonElement): bool =
        let value = property name element
        if value.ValueKind <> JsonValueKind.True && value.ValueKind <> JsonValueKind.False then
            fail $"Property '{name}' must be a JSON boolean."
        value.GetBoolean()

    let requiredInt32 (name: string) (element: JsonElement): int =
        let value = property name element
        match value.TryGetInt32() with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 32-bit JSON integer."

    let requiredInt64 (name: string) (element: JsonElement): int64 =
        let value = property name element
        match value.TryGetInt64() with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 64-bit JSON integer."

    let stableId<'Kind> (label: string) (text: string): StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> fail $"{label} is not a valid stable ID: {error}."

    let private arrayItems (name: string) (element: JsonElement) =
        let value = property name element
        if value.ValueKind <> JsonValueKind.Array then fail $"Property '{name}' must be a JSON array."
        value.EnumerateArray() |> Seq.toList

    let primitiveTypeRef (element: JsonElement): TypeRef =
        ensureFields "primitive type" [ "kind"; "name" ] [ "kind"; "name" ] element
        if requiredString "kind" element <> "primitive" then fail "Only primitive item types are exposed by this evaluation adapter."
        match requiredString "name" element with
        | "String" -> TypeRef.Primitive PrimitiveType.String
        | "Bool" -> TypeRef.Primitive PrimitiveType.Bool
        | "Unit" -> TypeRef.Primitive PrimitiveType.Unit
        | "Int64" -> TypeRef.Primitive PrimitiveType.Int64
        | "Float64" -> TypeRef.Primitive PrimitiveType.Float64
        | other -> fail $"Unsupported primitive type '{other}'."

    let patternSlot (element: JsonElement): RecipePatternSlot =
        let kind = requiredString "kind" element
        match kind with
        | "value" ->
            ensureFields "value pattern slot" [ "kind" ] [ "kind" ] element
            RecipePatternSlot.Value
        | "field" ->
            ensureFields "field pattern slot" [ "kind"; "fieldId" ] [ "kind"; "fieldId" ] element
            RecipePatternSlot.Field(stableId<FieldTag> "slot.fieldId" (requiredString "fieldId" element))
        | other -> fail $"Unsupported pattern slot kind '{other}'."

    let rec recipe (element: JsonElement): PureExpressionRecipe =
        match requiredString "kind" element with
        | "parameter" ->
            ensureFields "parameter recipe" [ "kind"; "parameterId" ] [ "kind"; "parameterId" ] element
            PureExpressionRecipe.Parameter(stableId<ParameterTag> "parameterId" (requiredString "parameterId" element))
        | "patternReference" ->
            ensureFields
                "patternReference recipe"
                [ "kind"; "scopeDepth"; "slot" ]
                [ "kind"; "scopeDepth"; "slot" ]
                element
            PureExpressionRecipe.PatternReference
                { ScopeDepth = requiredInt32 "scopeDepth" element
                  Slot = patternSlot (property "slot" element) }
        | "boolLiteral" ->
            ensureFields "boolLiteral recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            PureExpressionRecipe.BoolLiteral(requiredBool "value" element)
        | "int64Literal" ->
            ensureFields "int64Literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            PureExpressionRecipe.Int64Literal(requiredInt64 "value" element)
        | "optionNone" ->
            ensureFields "optionNone recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            PureExpressionRecipe.OptionNone(primitiveTypeRef (property "itemType" element))
        | "conditional" ->
            ensureFields
                "conditional recipe"
                [ "kind"; "condition"; "whenTrue"; "whenFalse" ]
                [ "kind"; "condition"; "whenTrue"; "whenFalse" ]
                element
            PureExpressionRecipe.Conditional(
                recipe (property "condition" element),
                recipe (property "whenTrue" element),
                recipe (property "whenFalse" element)
            )
        | "matchOption" ->
            ensureFields
                "matchOption recipe"
                [ "kind"; "input"; "someBody"; "noneBody" ]
                [ "kind"; "input"; "someBody"; "noneBody" ]
                element
            PureExpressionRecipe.MatchOption(
                recipe (property "input" element),
                recipe (property "someBody" element),
                recipe (property "noneBody" element)
            )
        | "matchUnion" ->
            ensureFields "matchUnion recipe" [ "kind"; "input"; "arms" ] [ "kind"; "input"; "arms" ] element
            let arms =
                arrayItems "arms" element
                |> List.map (fun arm ->
                    ensureFields "matchUnion arm" [ "variantId"; "body" ] [ "variantId"; "body" ] arm
                    let variantId = stableId<VariantTag> "arm.variantId" (requiredString "variantId" arm)
                    variantId, recipe (property "body" arm))
            PureExpressionRecipe.MatchUnion(recipe (property "input" element), arms)
        | other -> fail $"Unsupported pure-expression recipe kind '{other}'."

    let request (root: JsonElement): AgentReplaceFunctionBodyWithPureExpressionRecipe =
        ensureFields
            "simulatePureExpressionV1 command"
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

    let nodeSummary (value: StructuralNodeSummary) =
        objectValue
            [ "nodeId", box (StableId.value value.NodeId)
              "role", box value.Role
              "type", optional value.Type (sprintf "%A" >> box)
              "dependencies", value.Dependencies |> List.map (StableId.value >> box) |> arrayValue
              "parameterId", optional value.ParameterId (StableId.value >> box)
              "functionId", optional value.FunctionId (StableId.value >> box) ]

    let diff (value: FunctionBodyReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "oldResultNodeId", box (StableId.value value.OldResultNodeId)
              "newResultNodeId", box (StableId.value value.NewResultNodeId)
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arrayValue
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arrayValue ]

    let readiness = function
        | TransactionReadiness.SemanticallyReady -> box "semanticallyReady"
        | TransactionReadiness.Incomplete ids ->
            objectValue
                [ "kind", box "incomplete"
                  "obligationIds", ids |> List.map (StableId.value >> box) |> arrayValue ]

    let diagnostics values =
        values
        |> List.map (fun (diagnostic: TransactionDiagnostic) ->
            objectValue [ "code", box diagnostic.Code; "message", box diagnostic.Message ])
        |> arrayValue

    let fixture (value: PureExpressionEvalFixture) =
        objectValue
            [ "targetFunctionId", box (StableId.value value.TargetFunctionId)
              "initialSnapshotId", box (StableId.value value.Snapshot.Id)
              "initialResultNodeId", box (StableId.value value.InitialResultNodeId)
              "targetSignature", box "target(option: Option<Int64>, union: choice) -> Int64"
              "unionShape", box "choice.first(value: Int64) | choice.second"
              "optionParameterId", box (StableId.value value.OptionParameterId)
              "unionParameterId", box (StableId.value value.UnionParameterId)
              "firstVariantId", box (StableId.value value.FirstVariantId)
              "secondVariantId", box (StableId.value value.SecondVariantId)
              "firstFieldId", box (StableId.value value.FirstFieldId) ]

    let description fixtureValue =
        objectValue
            [ "status", box "ok"
              "protocolVersion", box PureExpressionProtocol.version
              "fixture", fixture fixtureValue
              "commands",
                [ objectValue [ "command", box "describeV1" ]
                  objectValue
                      [ "command", box "simulatePureExpressionV1"
                        "fields",
                            arrayValue
                                [ box "targetFunctionId"
                                  box "expectedBaseSnapshotId"
                                  box "expectedCurrentResultNodeId"
                                  box "replacement" ] ]
                  objectValue [ "command", box "commitPureExpressionV1"; "fields", arrayValue [ box "proposalHandle" ] ]
                  objectValue [ "command", box "rejectPureExpressionV1"; "fields", arrayValue [ box "proposalHandle" ] ] ]
                |> arrayValue
              "recipeCases",
                [ objectValue [ "kind", box "parameter"; "fields", arrayValue [ box "parameterId" ] ]
                  objectValue [ "kind", box "patternReference"; "fields", arrayValue [ box "scopeDepth"; box "slot" ] ]
                  objectValue [ "kind", box "boolLiteral"; "fields", arrayValue [ box "value" ] ]
                  objectValue [ "kind", box "int64Literal"; "fields", arrayValue [ box "value" ] ]
                  objectValue [ "kind", box "optionNone"; "fields", arrayValue [ box "itemType" ] ]
                  objectValue
                      [ "kind", box "conditional"
                        "fields", arrayValue [ box "condition"; box "whenTrue"; box "whenFalse" ] ]
                  objectValue
                      [ "kind", box "matchOption"
                        "fields", arrayValue [ box "input"; box "someBody"; box "noneBody" ] ]
                  objectValue [ "kind", box "matchUnion"; "fields", arrayValue [ box "input"; box "arms" ] ] ]
                |> arrayValue
              "typeSyntax", box "optionNone.itemType uses {\"kind\":\"primitive\",\"name\":\"Int64\"} (or String/Bool/Unit/Float64)"
              "patternSlots",
                [ objectValue [ "kind", box "value" ]
                  objectValue [ "kind", box "field"; "fields", arrayValue [ box "fieldId" ] ] ]
                |> arrayValue
              "lexicalScope",
                objectValue
                    [ "scopeDepth", box "0 is the current enclosing match-arm frame; 1 is the next outer frame, and so on"
                      "emptyFramesConsumeDepth", box true
                      "optionSomeSlot", box "value"
                      "optionNoneSlots", arrayValue []
                      "unionArmSlots", box "field(fieldId) for fields declared by that variant" ]
              "authority",
                objectValue
                    [ "newGraphNodeIds", box "system-owned"
                      "newPatternBindingIds", box "system-owned"
                      "rawGraphPayloads", box false ] ]

type PureExpressionSession internal (fixture: PureExpressionEvalFixture, initialWorkspace: AgentWorkspace) =
    let mutable workspace = initialWorkspace
    let mutable proposals: Map<string, AgentPureExpressionStructuralChangeProposal> = Map.empty
    let mutable nextProposalNumber = 1

    let proposalHandle () =
        let handle = $"pure-proposal-{nextProposalNumber}"
        nextProposalNumber <- nextProposalNumber + 1
        handle

    let requireProposal handle =
        match Map.tryFind handle proposals with
        | Some proposal -> proposal
        | None -> Input.fail $"Unknown pure-expression proposal handle '{handle}'."

    let execute (root: JsonElement) =
        match Input.requiredString "command" root with
        | "describeV1" ->
            Input.ensureFields "describeV1 command" [ "command" ] [ "command" ] root
            Encode.description fixture
        | "simulatePureExpressionV1" ->
            let request = Input.request root
            match AgentPureExpressionStructuralWorkflow.simulate workspace request with
            | Ok proposal ->
                let handle = proposalHandle ()
                proposals <- Map.add handle proposal proposals
                let review = AgentPureExpressionStructuralChangeProposal.review proposal
                Json.objectValue
                    [ "status", box "proposed"
                      "protocolVersion", box PureExpressionProtocol.version
                      "proposalHandle", box handle
                      "transactionId", box (StableId.value (AgentPureExpressionStructuralChangeProposal.transactionId proposal))
                      "proposedSnapshotId", box (StableId.value (AgentPureExpressionStructuralChangeProposal.proposedSnapshotId proposal))
                      "readiness", Encode.readiness review.Readiness
                      "resultingChange", Encode.diff review.ResultingChange ]
            | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics) ->
                Json.objectValue
                    [ "status", box "rejected"
                      "protocolVersion", box PureExpressionProtocol.version
                      "diagnostics", Encode.diagnostics diagnostics ]
            | Error error ->
                Json.objectValue
                    [ "status", box "error"
                      "protocolVersion", box PureExpressionProtocol.version
                      "detail", box (sprintf "%A" error) ]
        | "commitPureExpressionV1" ->
            Input.ensureFields
                "commitPureExpressionV1 command"
                [ "command"; "proposalHandle" ]
                [ "command"; "proposalHandle" ]
                root
            let handle = Input.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            match AgentPureExpressionStructuralWorkflow.commit workspace proposal with
            | AgentStructuralCommitResult.Rejected diagnostics ->
                Json.objectValue
                    [ "status", box "rejected"
                      "protocolVersion", box PureExpressionProtocol.version
                      "proposalHandle", box handle
                      "diagnostics", Encode.diagnostics diagnostics ]
            | AgentStructuralCommitResult.Committed(nextWorkspace, publication) ->
                workspace <- nextWorkspace
                proposals <- Map.remove handle proposals
                Json.objectValue
                    [ "status", box "committed"
                      "protocolVersion", box PureExpressionProtocol.version
                      "proposalHandle", box handle
                      "publishedSnapshotId", box (StableId.value publication.PublishedSnapshotId)
                      "resultingChange", Encode.diff publication.Diff ]
        | "rejectPureExpressionV1" ->
            Input.ensureFields
                "rejectPureExpressionV1 command"
                [ "command"; "proposalHandle" ]
                [ "command"; "proposalHandle" ]
                root
            let handle = Input.requiredString "proposalHandle" root
            let proposal = requireProposal handle
            let nextWorkspace, receipt = AgentPureExpressionStructuralWorkflow.reject workspace proposal
            workspace <- nextWorkspace
            proposals <- Map.remove handle proposals
            Json.objectValue
                [ "status", box "rejectedByAgent"
                  "protocolVersion", box PureExpressionProtocol.version
                  "proposalHandle", box handle
                  "transactionId", box (StableId.value receipt.TransactionId) ]
        | other -> Input.fail $"Unsupported command '{other}' for protocol {PureExpressionProtocol.version}."

    member _.ExecuteLine(line: string): string =
        try
            if String.IsNullOrWhiteSpace line then Input.fail "Input line must contain one JSON command."
            use document = Input.parseDocument line
            Json.objectValue [ "ok", box true; "result", execute document.RootElement ] |> Json.serialize
        with Input.ProtocolInputException message ->
            Json.objectValue
                [ "ok", box false
                  "error", Json.objectValue [ "code", box "ProtocolError"; "message", box message ] ]
            |> Json.serialize

[<RequireQualifiedAccess>]
module PureExpressionSession =
    let create (fixture: PureExpressionEvalFixture): Result<PureExpressionSession, ValidationIssue list> =
        match AgentWorkspace.create fixture.Snapshot with
        | Ok workspace -> Ok(PureExpressionSession(fixture, workspace))
        | Error issues -> Error issues
