namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module PureRunProductTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("run-product-" + name)
    let private productId = id<TypeTag> "record"
    let private otherId = id<TypeTag> "other"
    let private emptyId = id<TypeTag> "empty"
    let private nestedId = id<TypeTag> "nested"
    let private textId = id<FieldTag> "z-text"
    let private numberId = id<FieldTag> "a-number"
    let private flagId = id<FieldTag> "m-flag"
    let private nestedFieldId = id<FieldTag> "nested-field"
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private withSession run =
        let types: SemanticType list =
            [ { Id = productId; DisplayName = "Record"
                Kind = SemanticTypeKind.Product
                    [ { Id = textId; DisplayName = "value"; Type = stringType }
                      { Id = flagId; DisplayName = "value"; Type = boolType }
                      { Id = numberId; DisplayName = "value"; Type = intType } ] }
              { Id = otherId; DisplayName = "Record"
                Kind = SemanticTypeKind.Product
                    [ { Id = id<FieldTag> "other-left"; DisplayName = "text"; Type = stringType }
                      { Id = id<FieldTag> "other-right"; DisplayName = "text"; Type = stringType } ] }
              { Id = emptyId; DisplayName = "Empty"; Kind = SemanticTypeKind.Product [] }
              { Id = nestedId; DisplayName = "Nested"
                Kind = SemanticTypeKind.Product [ { Id = nestedFieldId; DisplayName = "inner"; Type = TypeRef.Named emptyId } ] } ]
        let initial = { Snapshot.empty (id<SnapshotTag> "initial") with Types = types }
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
        let ws = AgentWorkspace.create initial |> unwrap
        let ws = create ws "identity" [ TypeRef.Named productId ] (TypeRef.Named productId) (ExpressionRecipe.ParameterSlot 0)
        let field fieldId = ExpressionRecipe.FieldAccess(ExpressionRecipe.ParameterSlot 0, fieldId)
        let ws = create ws "update" [ TypeRef.Named productId ] (TypeRef.Named productId)
                    (ExpressionRecipe.ProductConstruct(productId,
                        [ textId, ExpressionRecipe.StringConcat(field textId, ExpressionRecipe.StringLiteral "!")
                          numberId, ExpressionRecipe.Int64WrappingAdd(field numberId, ExpressionRecipe.Int64Literal 1L)
                          flagId, field flagId ]))
        let ws = create ws "extract" [ TypeRef.Named productId ] stringType (field textId)
        let ws = create ws "empty" [ TypeRef.Named emptyId ] (TypeRef.Named emptyId) (ExpressionRecipe.ParameterSlot 0)
        let ws = create ws "textPair" [ TypeRef.Named otherId ] (TypeRef.Named otherId) (ExpressionRecipe.ParameterSlot 0)
        let ws = create ws "nested" [] (TypeRef.Named nestedId)
                    (ExpressionRecipe.ProductConstruct(nestedId, [ nestedFieldId, ExpressionRecipe.ProductConstruct(emptyId, []) ]))
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-product-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap)
            use session = AuthoringSession.openExisting path |> unwrap
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            run session path functionId
        finally Directory.Delete(root, true)

    let private fields =
        [ textId, PureRunValue.String "雪\n\"\\"
          numberId, PureRunValue.Int64 Int64.MaxValue
          flagId, PureRunValue.Bool false ]

    [<Fact>]
    let ``runPure rejects invalid Product shapes identities and nested results before execution`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject value expectedCode =
                match PureRun.run session (functionId "identity") [ value ] None with
                | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal(expectedCode, code)
                | other -> failwithf "Invalid Product accepted: %A" other
            reject (PureRunValue.Product(otherId, [])) "RunArgumentTypeMismatch"
            reject (PureRunValue.Product(productId, List.tail fields)) "RunProductShapeMismatch"
            reject (PureRunValue.Product(productId, fields @ [ List.head fields ])) "RunProductShapeMismatch"
            reject (PureRunValue.Product(productId, [ fields[0]; fields[0]; fields[2] ])) "RunProductShapeMismatch"
            reject (PureRunValue.Product(productId, fields |> List.map (fun (fieldId, value) ->
                (if fieldId = textId then id<FieldTag> "foreign" else fieldId), value))) "RunProductShapeMismatch"
            reject (PureRunValue.Product(productId, fields |> List.map (fun (fieldId, value) ->
                fieldId, (if fieldId = numberId then PureRunValue.Bool true else value)))) "RunProductShapeMismatch"
            for text, expected in [ String('x', 65537), "RunArgumentTooLarge"; String([| char 0xD800 |]), "RunInvalidString" ] do
                reject (PureRunValue.Product(productId, fields |> List.map (fun (fieldId, value) ->
                    fieldId, (if fieldId = textId then PureRunValue.String text else value)))) expected
            let value = PureRunValue.Product(productId, fields)
            match PureRun.run session (functionId "identity") [ value ] (Some(PureRunValue.Product(productId, []))) with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunProductShapeMismatch", code)
            | other -> failwithf "Invalid expectation accepted: %A" other
            match PureRun.run session (functionId "nested") [] (Some(PureRunValue.Product(nestedId, [ nestedFieldId, PureRunValue.Int64 1L ]))) with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunProductShapeMismatch", code)
            | other -> failwithf "Mistyped nested field expectation accepted: %A" other
            match PureRun.run session (functionId "nested") [] (Some(PureRunValue.Product(nestedId, [ nestedFieldId, PureRunValue.Product(emptyId, [ numberId, PureRunValue.Int64 1L ]) ]))) with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunProductShapeMismatch", code)
            | other -> failwithf "Invalid inner Product expectation accepted: %A" other
            let large = PureRunValue.Product(otherId,
                [ id<FieldTag> "other-left", PureRunValue.String(String('x', 32768))
                  id<FieldTag> "other-right", PureRunValue.String(String('y', 32769)) ])
            match PureRun.run session (functionId "textPair") [ large ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunArgumentTooLarge", code)
            | other -> failwithf "Aggregate String limit ignored: %A" other
            Assert.Equal(before, File.ReadAllText path))

    [<Fact>]
    let ``Product wire values require exact field envelopes and scalar payloads`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject value =
                let request = $"""{{"id":"invalid","op":"runPure","functionId":"{StableId.value (functionId "identity")}","arguments":[{value}]}}"""
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Equal("ProtocolError", response.RootElement.GetProperty("error").GetProperty("code").GetString())
            reject """{"kind":"product","fields":[]}"""
            reject ($"""{{"kind":"product","typeId":"{StableId.value productId}"}}""")
            let product field = $"""{{"kind":"product","typeId":"{StableId.value productId}","fields":[{field}]}}"""
            for value in
                [ """{"kind":"string","value":null}"""
                  """{"kind":"int64","value":"9223372036854775808"}"""
                  """{"kind":"bool","value":"false"}"""
                  """{"kind":"string","value":"x","extra":true}""" ] do
                reject (product ($"""{{"fieldId":"{StableId.value textId}","value":{value}}}"""))
            reject (product """{"value":{"kind":"string","value":"x"}}""")
            let tooMany = List.replicate 257 ($"""{{"fieldId":"{StableId.value textId}","value":{{"kind":"string","value":""}}}}""") |> String.concat ","
            reject (product tooMany)
            Assert.Equal(before, File.ReadAllText path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure executes Product construction access and canonical identity based comparisons`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let value = PureRunValue.Product(productId, fields)
            let reversed = PureRunValue.Product(productId, List.rev fields)
            let identity = PureRun.run session (functionId "identity") [ value ] (Some reversed) |> unwrap
            Assert.Equal(Some true, identity.Matches)
            let canonical = PureRunValue.Product(productId, fields |> List.sortBy (fst >> StableId.value))
            Assert.Equal(canonical, identity.Actual)
            Assert.Equal(Some canonical, identity.Expected)
            let expected = PureRunValue.Product(productId,
                [ flagId, PureRunValue.Bool false
                  numberId, PureRunValue.Int64 Int64.MinValue
                  textId, PureRunValue.String "雪\n\"\\!" ])
            let updated = PureRun.run session (functionId "update") [ value ] (Some expected) |> unwrap
            Assert.Equal(Some true, updated.Matches)
            let mismatch = PureRun.run session (functionId "identity") [ value ] (Some expected) |> unwrap
            Assert.Equal(Some false, mismatch.Matches)
            let extracted = PureRun.run session (functionId "extract") [ value ] None |> unwrap
            Assert.Equal(PureRunValue.String "雪\n\"\\", extracted.Actual)
            let empty = PureRunValue.Product(emptyId, [])
            let emptyResult = PureRun.run session (functionId "empty") [ empty ] (Some empty) |> unwrap
            Assert.Equal(Some true, emptyResult.Matches)
            let wire =
                $"""{{"kind":"product","typeId":"{StableId.value productId}","fields":[{{"fieldId":"{StableId.value textId}","value":{{"kind":"string","value":"wire"}}}},{{"fieldId":"{StableId.value flagId}","value":{{"kind":"bool","value":false}}}},{{"fieldId":"{StableId.value numberId}","value":{{"kind":"int64","value":"9223372036854775807"}}}}]}}"""
            let request = $"""{{"id":"product","op":"runPure","functionId":"{StableId.value (functionId "identity")}","arguments":[{wire}],"expected":{wire}}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            Assert.True(result.GetProperty("matches").GetBoolean())
            let actual = result.GetProperty("actual")
            Assert.Equal(StableId.value productId, actual.GetProperty("typeId").GetString())
            let actualFields = actual.GetProperty("fields").EnumerateArray() |> Seq.toList
            Assert.Equal<string list>([ numberId; flagId; textId ] |> List.map StableId.value,
                                      actualFields |> List.map (fun field -> field.GetProperty("fieldId").GetString()))
            Assert.Equal("9223372036854775807", actualFields.Head.GetProperty("value").GetProperty("value").GetString())
            Assert.Equal(before, File.ReadAllText path))
