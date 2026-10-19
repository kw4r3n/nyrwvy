namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// runPure values nested inside Product/Union fields and Result payloads.
module PureRunInnerNestingTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("run-inner-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listType item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionType item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])
    let private itemId = id<TypeTag> "item"
    let private shapeId = id<TypeTag> "shape"
    let private nodeId = id<TypeTag> "node"
    let private labelId = id<FieldTag> "c-label"
    let private tagsId = id<FieldTag> "b-tags"
    let private scoreId = id<FieldTag> "a-score"
    let private radiusId = id<FieldTag> "radius"
    let private membersId = id<FieldTag> "members"
    let private innerId = id<FieldTag> "inner"
    let private nextId = id<FieldTag> "next"
    let private circleId = id<VariantTag> "circle"
    let private groupId = id<VariantTag> "group"
    let private wrappedId = id<VariantTag> "wrapped"
    let private itemType = TypeRef.Named(itemId, [])
    let private shapeType = TypeRef.Named(shapeId, [])
    let private wrappedType = resultType itemType stringType
    let private levelId index = id<TypeTag> ("level-" + string index)
    let private levelFieldId index = id<FieldTag> ("level-field-" + string index)
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private levels =
        [ 1 .. 9 ] |> List.map (fun index ->
            let fieldType = if index = 9 then intType else TypeRef.Named(levelId (index + 1), [])
            { Exported = true; TypeParameters = []; Id = levelId index; DisplayName = "Level" + string index
              Kind = SemanticTypeKind.Product [ { Id = levelFieldId index; DisplayName = "value"; Type = fieldType } ] })

    let private withSession run =
        let types: SemanticType list =
            [ { Exported = true; TypeParameters = []; Id = itemId; DisplayName = "Item"
                Kind = SemanticTypeKind.Product
                    [ { Id = labelId; DisplayName = "label"; Type = stringType }
                      { Id = tagsId; DisplayName = "tags"; Type = listType stringType }
                      { Id = scoreId; DisplayName = "score"; Type = optionType intType } ] }
              { Exported = true; TypeParameters = []; Id = shapeId; DisplayName = "Shape"
                Kind = SemanticTypeKind.Union
                    [ { Id = circleId; DisplayName = "Circle"; Fields = [ { Id = radiusId; DisplayName = "radius"; Type = intType } ] }
                      { Id = groupId; DisplayName = "Group"; Fields = [ { Id = membersId; DisplayName = "members"; Type = listType itemType } ] }
                      { Id = wrappedId; DisplayName = "Wrapped"; Fields = [ { Id = innerId; DisplayName = "inner"; Type = wrappedType } ] } ] }
              { Exported = true; TypeParameters = []; Id = nodeId; DisplayName = "Node"
                Kind = SemanticTypeKind.Product [ { Id = nextId; DisplayName = "next"; Type = optionType (TypeRef.Named(nodeId, [])) } ] } ]
            @ levels
        let create ws name parameters resultType body =
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  TypeParameters = None
                  DisplayName = name
                  Parameters = parameters |> List.map (fun itemType -> { DisplayName = "input"; Type = itemType })
                  ResultType = resultType; RequiredEffects = []; Body = body }
            let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let ws = AgentWorkspace.create { Snapshot.empty (id<SnapshotTag> "initial") with Types = types } |> unwrap
        let identity ws (name, itemType) = create ws name [ itemType ] itemType (ExpressionRecipe.ParameterSlot 0)
        let ws =
            [ "item", itemType
              "shape", shapeType
              "scalarResult", resultType intType stringType
              "listOrItem", resultType (listType intType) itemType
              "optionalText", resultType (optionType stringType) stringType
              "node", TypeRef.Named(nodeId, [])
              "chainNine", TypeRef.Named(levelId 1, [])
              "chainEight", TypeRef.Named(levelId 2, []) ]
            |> List.fold identity ws
        let tags = ExpressionRecipe.FieldAccess(ExpressionRecipe.ParameterSlot 0, tagsId)
        let ws = create ws "tagCount" [ itemType ] intType (ExpressionRecipe.ListLength tags)
        let ws = create ws "wrap" [ itemType ] shapeType
                    (ExpressionRecipe.UnionConstruct(wrappedId, [], [ innerId, ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.ParameterSlot 0) ]))
        let ws = create ws "okTags" [ itemType ] (resultType (listType stringType) itemType)
                    (ExpressionRecipe.ResultOk(itemType, tags))
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-inner-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap)
            use session = AuthoringSession.openExisting path |> unwrap
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            run session path functionId
        finally Directory.Delete(root, true)

    let private item label tags score =
        PureRunValue.Product(itemId, [],
            [ labelId, PureRunValue.String label
              tagsId, PureRunValue.StringList tags
              scoreId, PureRunValue.Int64Option score ])

    let rec private chain first last =
        if first = 9 then PureRunValue.Product(levelId 9, [], [ levelFieldId 9, PureRunValue.Int64 last ])
        else PureRunValue.Product(levelId first, [], [ levelFieldId first, chain (first + 1) last ])

    [<Fact>]
    let ``inner nesting checks field payload representations and depth before execution`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject name arguments expected code =
                match PureRun.run session (functionId name) arguments expected with
                | Error(PureRunError.Unsupported(actual, _)) -> Assert.Equal(code, actual)
                | other -> failwithf "Invalid inner nesting accepted for %s: %A" name other
            let valid = item "a" [ "x" ] None
            reject "item" [ PureRunValue.Product(itemId, [], [ labelId, PureRunValue.String "a"; tagsId, PureRunValue.StringList []; scoreId, PureRunValue.Int64 1L ]) ] None "RunProductShapeMismatch"
            reject "item" [ PureRunValue.Product(itemId, [], [ labelId, PureRunValue.String "a"; tagsId, PureRunValue.ListValue(stringType, []); scoreId, PureRunValue.Int64Option None ]) ] None "RunContainerTypeMismatch"
            reject "item" [ PureRunValue.Product(itemId, [], [ labelId, PureRunValue.String "a"; tagsId, PureRunValue.StringList(List.replicate 4097 "") ; scoreId, PureRunValue.Int64Option None ]) ] None "RunArgumentTooLarge"
            reject "item" [ PureRunValue.Product(itemId, [], [ labelId, PureRunValue.String(String('x', 40000)); tagsId, PureRunValue.StringList [ String('y', 40000) ]; scoreId, PureRunValue.Int64Option None ]) ] None "RunArgumentTooLarge"
            reject "shape" [ PureRunValue.Union(shapeId, [], groupId, [ membersId, PureRunValue.ListValue(itemType, [ PureRunValue.Product(itemId, [], []) ]) ]) ] None "RunProductShapeMismatch"
            reject "shape" [ PureRunValue.Union(shapeId, [], wrappedId, [ innerId, PureRunValue.ResultValue(itemType, stringType, true, PureRunValue.String "not an item") ]) ] None "RunContainerTypeMismatch"
            reject "shape" [ PureRunValue.Union(shapeId, [], groupId, [ membersId, PureRunValue.ListValue(itemType, [ PureRunValue.Union(shapeId, [], circleId, [ radiusId, PureRunValue.Int64 1L ]) ]) ]) ] None "RunContainerTypeMismatch"
            reject "scalarResult" [ PureRunValue.ResultValue(intType, stringType, true, PureRunValue.Int64 1L) ] None "RunContainerTypeMismatch"
            reject "optionalText" [ PureRunValue.ResultValue(optionType stringType, stringType, true, PureRunValue.String "x") ] None "RunContainerTypeMismatch"
            reject "optionalText" [ PureRunValue.ResultValue(optionType stringType, stringType, false, PureRunValue.StringOption None) ] None "RunContainerTypeMismatch"
            reject "listOrItem" [ PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 1L) ] None "RunArgumentTypeMismatch"
            reject "listOrItem" [ PureRunValue.ResultValue(listType intType, itemType, false, valid) ]
                (Some(PureRunValue.ResultValue(listType intType, itemType, false, PureRunValue.Product(itemId, [], [])))) "RunProductShapeMismatch"
            reject "chainNine" [ chain 1 0L ] None "RunResultTypeUnsupported"
            Assert.Equal(before, File.ReadAllText path))

    [<Fact>]
    let ``inner nesting wire keeps flat and nested Result forms distinct`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let response name value =
                let request = $"""{{"id":"inner","op":"runPure","functionId":"{StableId.value (functionId name)}","arguments":[{value}]}}"""
                JsonDocument.Parse(AuthoringProtocol.executeLine session request)
            let reject name value code =
                use document = response name value
                Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.ToString())
                Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString())
            let optionString = """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"string"}]}"""
            let text = """{"kind":"primitive","primitive":"string"}"""
            reject "optionalText" ($"""{{"kind":"result","okType":{optionString},"errorKind":"string","isOk":true,"value":{{"kind":"option","itemKind":"string","hasValue":false}}}}""") "ProtocolError"
            reject "optionalText" ($"""{{"kind":"result","okType":{optionString},"errorType":{text},"isOk":true}}""") "ProtocolError"
            reject "optionalText" ($"""{{"kind":"result","okType":{optionString},"errorType":{text},"isOk":true,"value":null}}""") "ProtocolError"
            reject "optionalText" ($"""{{"kind":"result","okType":{optionString},"errorType":{text},"isOk":true,"value":"x"}}""") "ProtocolError"
            reject "optionalText" ($"""{{"kind":"result","okType":{optionString},"errorType":{text},"isOk":false,"value":{{"kind":"option","itemKind":"string","hasValue":false}}}}""") "RunContainerTypeMismatch"
            let field fieldId value = $"""{{"fieldId":"{StableId.value fieldId}","value":{value}}}"""
            let product tags =
                let label = field labelId """{"kind":"string","value":"a"}"""
                let score = field scoreId """{"kind":"option","itemKind":"int64","hasValue":false}"""
                $"""{{"kind":"product","typeId":"{StableId.value itemId}","fields":[{label},{field tagsId tags},{score}]}}"""
            reject "item" (product """{"kind":"option","itemKind":"string","hasValue":false}""") "RunProductShapeMismatch"
            reject "item" (product """{"kind":"list","itemKind":"string","items":[null]}""") "ProtocolError"
            let mutable deep = """{"kind":"option","itemKind":"string","hasValue":false}"""
            for _ in 1 .. PureRun.maxValueLayers do
                deep <- $"""{{"kind":"result","okType":{optionString},"errorType":{text},"isOk":true,"value":{deep}}}"""
            reject "optionalText" deep "ProtocolError"
            Assert.Equal(before, File.ReadAllText path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure executes values nested inside Product and Union fields and Result payloads`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let roundtrip name value =
                let result = PureRun.run session (functionId name) [ value ] (Some value) |> unwrap
                Assert.Equal(Some true, result.Matches)
                result.Actual
            let first = item "雪\n\"" [ "a"; ""; "b" ] (Some Int64.MinValue)
            let second = item "" [] None
            roundtrip "item" first |> ignore
            let reversed = PureRunValue.Product(itemId, [],
                                [ scoreId, PureRunValue.Int64Option None; tagsId, PureRunValue.StringList []; labelId, PureRunValue.String "" ])
            let canonical = PureRun.run session (functionId "item") [ reversed ] (Some second) |> unwrap
            Assert.Equal(Some true, canonical.Matches)
            Assert.Equal(reversed, canonical.Actual)
            let group = PureRunValue.Union(shapeId, [], groupId, [ membersId, PureRunValue.ListValue(itemType, [ first; second ]) ])
            roundtrip "shape" group |> ignore
            roundtrip "shape" (PureRunValue.Union(shapeId, [], groupId, [ membersId, PureRunValue.ListValue(itemType, []) ])) |> ignore
            roundtrip "shape" (PureRunValue.Union(shapeId, [], wrappedId, [ innerId, PureRunValue.ResultValue(itemType, stringType, false, PureRunValue.String "missing") ])) |> ignore
            roundtrip "shape" (PureRunValue.Union(shapeId, [], circleId, [ radiusId, PureRunValue.Int64 3L ])) |> ignore
            roundtrip "listOrItem" (PureRunValue.ResultValue(listType intType, itemType, true, PureRunValue.Int64List [ 1L; Int64.MaxValue ])) |> ignore
            roundtrip "listOrItem" (PureRunValue.ResultValue(listType intType, itemType, false, first)) |> ignore
            roundtrip "optionalText" (PureRunValue.ResultValue(optionType stringType, stringType, true, PureRunValue.StringOption None)) |> ignore
            roundtrip "optionalText" (PureRunValue.ResultValue(optionType stringType, stringType, true, PureRunValue.StringOption(Some ""))) |> ignore
            roundtrip "optionalText" (PureRunValue.ResultValue(optionType stringType, stringType, false, PureRunValue.String "")) |> ignore
            roundtrip "chainEight" (chain 2 Int64.MaxValue) |> ignore
            let count = PureRun.run session (functionId "tagCount") [ first ] (Some(PureRunValue.Int64 3L)) |> unwrap
            Assert.Equal(Some true, count.Matches)
            let wrapped = PureRun.run session (functionId "wrap") [ first ]
                            (Some(PureRunValue.Union(shapeId, [], wrappedId, [ innerId, PureRunValue.ResultValue(itemType, stringType, true, first) ]))) |> unwrap
            Assert.Equal(Some true, wrapped.Matches)
            let differentTag = PureRunValue.ResultValue(listType stringType, itemType, false, first)
            let okTags = PureRun.run session (functionId "okTags") [ first ] (Some differentTag) |> unwrap
            Assert.Equal(Some false, okTags.Matches)
            Assert.Equal(PureRunValue.ResultValue(listType stringType, itemType, true, PureRunValue.StringList [ "a"; ""; "b" ]), okTags.Actual)
            let deepMismatch = PureRun.run session (functionId "shape") [ group ]
                                (Some(PureRunValue.Union(shapeId, [], groupId, [ membersId, PureRunValue.ListValue(itemType, [ first; item "" [] (Some 0L) ]) ]))) |> unwrap
            Assert.Equal(Some false, deepMismatch.Matches)
            let optionString = """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"string"}]}"""
            let wire = $"""{{"kind":"result","okType":{optionString},"errorType":{{"kind":"primitive","primitive":"string"}},"isOk":true,"value":{{"kind":"option","itemKind":"string","hasValue":true,"value":"雪"}}}}"""
            let request = $"""{{"id":"inner","op":"runPure","functionId":"{StableId.value (functionId "optionalText")}","arguments":[{wire}],"expected":{wire}}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            Assert.True(result.GetProperty("matches").GetBoolean())
            use expected = JsonDocument.Parse wire
            Assert.True(JsonElement.DeepEquals(expected.RootElement, result.GetProperty("actual")), result.ToString())
            let groupWire =
                let field fieldId value = $"""{{"fieldId":"{StableId.value fieldId}","value":{value}}}"""
                let score = field scoreId """{"kind":"option","itemKind":"int64","hasValue":true,"value":"-1"}"""
                let tags = field tagsId """{"kind":"list","itemKind":"string","items":["t"]}"""
                let label = field labelId """{"kind":"string","value":"x"}"""
                let itemWire = $"""{{"kind":"product","typeId":"{StableId.value itemId}","fields":[{score},{tags},{label}]}}"""
                let itemTypeWire = $"""{{"kind":"named","typeId":"{StableId.value itemId}"}}"""
                let members = field membersId ($"""{{"kind":"list","itemType":{itemTypeWire},"items":[{itemWire}]}}""")
                $"""{{"kind":"union","typeId":"{StableId.value shapeId}","variantId":"{StableId.value groupId}","fields":[{members}]}}"""
            let request = $"""{{"id":"inner-union","op":"runPure","functionId":"{StableId.value (functionId "shape")}","arguments":[{groupWire}],"expected":{groupWire}}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            Assert.True(response.RootElement.GetProperty("result").GetProperty("matches").GetBoolean())
            Assert.Equal(before, File.ReadAllText path))
