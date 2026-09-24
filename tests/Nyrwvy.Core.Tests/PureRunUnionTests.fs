namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module PureRunUnionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("run-union-" + name)
    let private unionId = id<TypeTag> "union"
    let private foreignId = id<TypeTag> "foreign"
    let private nestedId = id<TypeTag> "nested"
    let private payloadId = id<VariantTag> "payload"
    let private emptyId = id<VariantTag> "empty"
    let private otherEmptyId = id<VariantTag> "other-empty"
    let private foreignVariantId = id<VariantTag> "foreign-variant"
    let private textId = id<FieldTag> "z-text"
    let private extraId = id<FieldTag> "y-extra"
    let private numberId = id<FieldTag> "a-number"
    let private flagId = id<FieldTag> "m-flag"
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error
    let private fields =
        [ textId, PureRunScalar.String "雪\n\"\\"
          extraId, PureRunScalar.String ""
          numberId, PureRunScalar.Int64 Int64.MinValue
          flagId, PureRunScalar.Bool false ]

    let private withSession run =
        let types: SemanticType list =
            [ { Id = unionId; DisplayName = "Choice"
                Kind = SemanticTypeKind.Union
                    [ { Id = payloadId; DisplayName = "Choice"
                        Fields =
                            [ { Id = textId; DisplayName = "value"; Type = TypeRef.Primitive PrimitiveType.String }
                              { Id = extraId; DisplayName = "value"; Type = TypeRef.Primitive PrimitiveType.String }
                              { Id = flagId; DisplayName = "value"; Type = TypeRef.Primitive PrimitiveType.Bool }
                              { Id = numberId; DisplayName = "value"; Type = TypeRef.Primitive PrimitiveType.Int64 } ] }
                      { Id = emptyId; DisplayName = "Choice"; Fields = [] }
                      { Id = otherEmptyId; DisplayName = "Choice"; Fields = [] } ] }
              { Id = foreignId; DisplayName = "Choice"
                Kind = SemanticTypeKind.Union [ { Id = foreignVariantId; DisplayName = "Choice"; Fields = [] } ] }
              { Id = nestedId; DisplayName = "Nested"
                Kind = SemanticTypeKind.Union
                    [ { Id = id<VariantTag> "nested-empty"; DisplayName = "Empty"; Fields = [] }
                      { Id = id<VariantTag> "nested-data"; DisplayName = "Data"
                        Fields = [ { Id = id<FieldTag> "nested-data"; DisplayName = "inner"; Type = TypeRef.Named unionId } ] } ] } ]
        let create ws name parameters resultType body =
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  DisplayName = name
                  Parameters = parameters |> List.map (fun itemType -> { DisplayName = "input"; Type = itemType })
                  ResultType = resultType; RequiredEffects = []; Body = body }
            let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let ws = AgentWorkspace.create { Snapshot.empty (id<SnapshotTag> "initial") with Types = types } |> unwrap
        let ws = create ws "identity" [ TypeRef.Named unionId ] (TypeRef.Named unionId) (ExpressionRecipe.ParameterSlot 0)
        let ws = create ws "make" [ TypeRef.Primitive PrimitiveType.String ] (TypeRef.Named unionId)
                    (ExpressionRecipe.UnionConstruct(payloadId,
                        [ textId, ExpressionRecipe.ParameterSlot 0
                          extraId, ExpressionRecipe.StringLiteral ""
                          numberId, ExpressionRecipe.Int64Literal Int64.MinValue
                          flagId, ExpressionRecipe.BoolLiteral false ]))
        let ws = create ws "classify" [ TypeRef.Named unionId ] (TypeRef.Primitive PrimitiveType.Bool)
                    (ExpressionRecipe.MatchUnion(ExpressionRecipe.ParameterSlot 0,
                        [ payloadId, ExpressionRecipe.BoolLiteral true
                          emptyId, ExpressionRecipe.BoolLiteral false
                          otherEmptyId, ExpressionRecipe.BoolLiteral false ]))
        let ws = create ws "nested" [] (TypeRef.Named nestedId) (ExpressionRecipe.UnionConstruct(id<VariantTag> "nested-empty", []))
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-union-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap)
            use session = AuthoringSession.openExisting path |> unwrap
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            run session path functionId
        finally Directory.Delete(root, true)

    [<Fact>]
    let ``runPure refuses foreign Union variants invalid fields and non scalar declarations`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject value expected =
                match PureRun.run session (functionId "identity") [ value ] None with
                | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal(expected, code)
                | other -> failwithf "Invalid Union accepted: %A" other
            reject (PureRunValue.Union(foreignId, foreignVariantId, [])) "RunArgumentTypeMismatch"
            reject (PureRunValue.Product(unionId, [])) "RunProductShapeMismatch"
            for variant, values in
                [ foreignVariantId, []
                  id<VariantTag> "unknown", []
                  payloadId, List.tail fields
                  payloadId, fields @ [ List.head fields ]
                  payloadId, [ fields[0]; fields[0]; fields[2]; fields[3] ]
                  emptyId, [ List.head fields ]
                  payloadId, fields |> List.map (fun (fieldId, value) -> (if fieldId = textId then id<FieldTag> "foreign" else fieldId), value)
                  payloadId, fields |> List.map (fun (fieldId, value) -> fieldId, (if fieldId = flagId then PureRunScalar.Int64 0L else value)) ] do
                reject (PureRunValue.Union(unionId, variant, values)) "RunUnionShapeMismatch"
            let valid = PureRunValue.Union(unionId, payloadId, fields)
            match PureRun.run session (functionId "identity") [ valid ] (Some(PureRunValue.Union(unionId, foreignVariantId, []))) with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunUnionShapeMismatch", code)
            | other -> failwithf "Foreign expectation accepted: %A" other
            for text, expected in [ String('x', 65537), "RunArgumentTooLarge"; String([| char 0xD800 |]), "RunInvalidString" ] do
                let values = fields |> List.map (fun (fieldId, value) -> fieldId, (if fieldId = textId then PureRunScalar.String text else value))
                reject (PureRunValue.Union(unionId, payloadId, values)) expected
            let large = fields |> List.map (fun (fieldId, value) ->
                fieldId, (if fieldId = textId || fieldId = extraId then PureRunScalar.String(String('x', 32769)) else value))
            reject (PureRunValue.Union(unionId, payloadId, large)) "RunArgumentTooLarge"
            match PureRun.run session (functionId "nested") [] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunResultTypeUnsupported", code)
            | other -> failwithf "Union with nested field accepted: %A" other
            Assert.Equal(before, File.ReadAllText path))

    [<Fact>]
    let ``Union wire values require type variant and exact scalar field envelopes`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject value expected =
                let request = $"""{{"id":"bad-union","op":"runPure","functionId":"{StableId.value (functionId "identity")}","arguments":[{value}]}}"""
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Equal(expected, response.RootElement.GetProperty("error").GetProperty("code").GetString())
            let wrap values = $"""{{"kind":"union","typeId":"{StableId.value unionId}","variantId":"{StableId.value payloadId}","fields":[{values}]}}"""
            reject ($"""{{"kind":"union","typeId":"{StableId.value unionId}","fields":[]}}""") "ProtocolError"
            reject ($"""{{"kind":"union","variantId":"{StableId.value emptyId}","fields":[]}}""") "ProtocolError"
            reject ($"""{{"kind":"union","typeId":"{StableId.value unionId}","variantId":"{StableId.value emptyId}"}}""") "ProtocolError"
            reject (wrap "") "RunUnionShapeMismatch"
            for value in [ """{"kind":"string","value":null}"""; """{"kind":"int64","value":"9223372036854775808"}"""; """{"kind":"product","fields":[]}""" ] do
                reject (wrap ($"""{{"fieldId":"{StableId.value textId}","value":{value}}}""")) "ProtocolError"
            let tooMany = List.replicate 257 ($"""{{"fieldId":"{StableId.value textId}","value":{{"kind":"string","value":""}}}}""") |> String.concat ","
            reject (wrap tooMany) "ProtocolError"
            Assert.Equal(before, File.ReadAllText path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure roundtrips Union payloads constructs variants and compares tags`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let value = PureRunValue.Union(unionId, payloadId, fields)
            let reversed = PureRunValue.Union(unionId, payloadId, List.rev fields)
            let identity = PureRun.run session (functionId "identity") [ value ] (Some reversed) |> unwrap
            Assert.Equal(Some true, identity.Matches)
            Assert.Equal(PureRunValue.Union(unionId, payloadId, fields |> List.sortBy (fst >> StableId.value)), identity.Actual)
            let constructed = PureRun.run session (functionId "make") [ PureRunValue.String "雪\n\"\\" ] (Some value) |> unwrap
            Assert.Equal(Some true, constructed.Matches)
            let empty = PureRunValue.Union(unionId, emptyId, [])
            let other = PureRunValue.Union(unionId, otherEmptyId, [])
            let mismatch = PureRun.run session (functionId "identity") [ empty ] (Some other) |> unwrap
            Assert.Equal(Some false, mismatch.Matches)
            for input, expected in [ value, true; empty, false; other, false ] do
                let result = PureRun.run session (functionId "classify") [ input ] (Some(PureRunValue.Bool expected)) |> unwrap
                Assert.Equal(Some true, result.Matches)
            let wire = $"""{{"kind":"union","typeId":"{StableId.value unionId}","variantId":"{StableId.value payloadId}","fields":[{{"fieldId":"{StableId.value textId}","value":{{"kind":"string","value":"wire"}}}},{{"fieldId":"{StableId.value extraId}","value":{{"kind":"string","value":""}}}},{{"fieldId":"{StableId.value flagId}","value":{{"kind":"bool","value":false}}}},{{"fieldId":"{StableId.value numberId}","value":{{"kind":"int64","value":"9223372036854775807"}}}}]}}"""
            let request = $"""{{"id":"union","op":"runPure","functionId":"{StableId.value (functionId "identity")}","arguments":[{wire}],"expected":{wire}}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            Assert.True(result.GetProperty("matches").GetBoolean())
            let actual = result.GetProperty("actual")
            Assert.Equal("union", actual.GetProperty("kind").GetString())
            Assert.Equal(StableId.value unionId, actual.GetProperty("typeId").GetString())
            Assert.Equal(StableId.value payloadId, actual.GetProperty("variantId").GetString())
            let output = actual.GetProperty("fields").EnumerateArray() |> Seq.toList
            Assert.Equal<string list>([ numberId; flagId; extraId; textId ] |> List.map StableId.value,
                                      output |> List.map (fun field -> field.GetProperty("fieldId").GetString()))
            Assert.Equal("9223372036854775807", output.Head.GetProperty("value").GetProperty("value").GetString())
            Assert.Equal(before, File.ReadAllText path))
