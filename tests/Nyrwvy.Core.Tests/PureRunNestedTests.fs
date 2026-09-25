namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module PureRunNestedTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("run-nested-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listType item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionType item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private resultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ intType; stringType ])
    let private productId = id<TypeTag> "product"
    let private unionId = id<TypeTag> "union"
    let private variantId = id<VariantTag> "empty"
    let private textId = id<FieldTag> "z-text"
    let private numberId = id<FieldTag> "a-number"
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private withSession run =
        let snapshot =
            { Snapshot.empty (id<SnapshotTag> "initial") with
                Types =
                    [ { Id = productId; DisplayName = "Entry"
                        Kind = SemanticTypeKind.Product
                            [ { Id = textId; DisplayName = "text"; Type = stringType }
                              { Id = numberId; DisplayName = "number"; Type = intType } ] }
                      { Id = unionId; DisplayName = "Choice"
                        Kind = SemanticTypeKind.Union [ { Id = variantId; DisplayName = "Empty"; Fields = [] } ] } ] }
        let create ws name itemType resultType body =
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  DisplayName = name; Parameters = [ { DisplayName = "input"; Type = itemType } ]
                  ResultType = resultType; RequiredEffects = []; Body = body }
            let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let ws = AgentWorkspace.create snapshot |> unwrap
        let ws =
            [ "listOptions", listType (optionType intType)
              "optionLists", optionType (listType (optionType stringType))
              "products", listType (TypeRef.Named productId)
              "union", optionType (TypeRef.Named unionId)
              "results", listType resultType
              "matrix", listType (listType intType)
              "strings", listType (listType stringType)
              "deepEight", ([ 1 .. 8 ] |> List.fold (fun item _ -> optionType item) intType)
              "deepNine", ([ 1 .. 9 ] |> List.fold (fun item _ -> optionType item) intType) ]
            |> List.fold (fun ws (name, itemType) -> create ws name itemType itemType (ExpressionRecipe.ParameterSlot 0)) ws
        let matrixType = listType (listType intType)
        let ws = create ws "double" matrixType matrixType
                    (ExpressionRecipe.ListConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 0))
        let ws = create ws "count" matrixType intType (ExpressionRecipe.ListLength(ExpressionRecipe.ParameterSlot 0))
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-nested-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap)
            use session = AuthoringSession.openExisting path |> unwrap
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            run session path functionId
        finally Directory.Delete(root, true)

    [<Fact>]
    let ``nested runner values check every item type shape depth and aggregate budget`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject name value expected =
                match PureRun.run session (functionId name) [ value ] None with
                | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal(expected, code)
                | other -> failwithf "Invalid nested value accepted: %A" other
            reject "listOptions" (PureRunValue.ListValue(optionType intType, [ PureRunValue.StringOption None ])) "RunContainerTypeMismatch"
            reject "products" (PureRunValue.ListValue(TypeRef.Named productId, [ PureRunValue.Product(productId, []) ])) "RunProductShapeMismatch"
            reject "union" (PureRunValue.OptionValue(TypeRef.Named unionId, Some(PureRunValue.Union(unionId, id<VariantTag> "foreign", [])))) "RunUnionShapeMismatch"
            reject "matrix" (PureRunValue.ListValue(listType intType, List.replicate 4097 (PureRunValue.Int64List []))) "RunArgumentTooLarge"
            reject "matrix" (PureRunValue.ListValue(listType intType, List.replicate 4096 (PureRunValue.Int64List [ 1L ]))) "RunArgumentTooLarge"
            reject "strings" (PureRunValue.ListValue(listType stringType,
                [ PureRunValue.StringList [ String('x', 32769) ]; PureRunValue.StringList [ String('y', 32769) ] ])) "RunArgumentTooLarge"
            reject "strings" (PureRunValue.ListValue(listType stringType, [ PureRunValue.StringList [ String([| char 0xD800 |]) ] ])) "RunInvalidString"
            let deep = [ 1 .. 10 ] |> List.fold (fun value _ -> PureRunValue.OptionValue(optionType intType, Some value)) (PureRunValue.Int64Option None)
            reject "matrix" deep "RunArgumentTooLarge"
            let eightOptions = [ 1 .. 8 ] |> List.fold (fun item _ -> optionType item) intType
            reject "deepNine" (PureRunValue.OptionValue(eightOptions, None)) "RunContainerTypeMismatch"
            let valid = PureRunValue.ListValue(optionType intType, [])
            let invalidExpected = PureRunValue.ListValue(optionType intType, [ PureRunValue.BoolOption None ])
            match PureRun.run session (functionId "listOptions") [ valid ] (Some invalidExpected) with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunContainerTypeMismatch", code)
            | other -> failwithf "Invalid nested expectation accepted: %A" other
            Assert.Equal(before, File.ReadAllText path))

    [<Fact>]
    let ``nested wire rejects mixed representations absent metadata and invalid payloads`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject value code =
                let request = $"""{{"id":"nested-invalid","op":"runPure","functionId":"{StableId.value (functionId "listOptions")}","arguments":[{value}]}}"""
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Equal(code, response.RootElement.GetProperty("error").GetProperty("code").GetString())
            reject """{"kind":"list","items":[]}""" "ProtocolError"
            reject """{"kind":"list","itemKind":"int64","itemType":{"kind":"primitive","primitive":"int64"},"items":[]}""" "ProtocolError"
            reject """{"kind":"list","itemType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]},"items":[null]}""" "ProtocolError"
            reject """{"kind":"option","itemType":{"kind":"primitive","primitive":"int64"},"hasValue":false,"value":null}""" "ProtocolError"
            let mutable deep = """{"kind":"option","itemKind":"int64","hasValue":false}"""
            for _ in 1 .. 10 do
                deep <- """{"kind":"option","itemType":{"kind":"primitive","primitive":"int64"},"hasValue":true,"value":""" + deep + "}"
            reject deep "ProtocolError"
            Assert.Equal(before, File.ReadAllText path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure executes nested Lists Options and named scalar field payloads`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let roundtrip name value =
                let result = PureRun.run session (functionId name) [ value ] (Some value) |> unwrap
                Assert.Equal(Some true, result.Matches)
                result
            roundtrip "listOptions" (PureRunValue.ListValue(optionType intType,
                [ PureRunValue.Int64Option None; PureRunValue.Int64Option(Some Int64.MinValue); PureRunValue.Int64Option(Some 0L) ])) |> ignore
            let _, eight = [ 1 .. 7 ] |> List.fold (fun (itemType, value) _ ->
                optionType itemType, PureRunValue.OptionValue(itemType, Some value)) (optionType intType, PureRunValue.Int64Option(Some 0L))
            roundtrip "deepEight" eight |> ignore
            let optional = PureRunValue.OptionValue(listType (optionType stringType),
                Some(PureRunValue.ListValue(optionType stringType, [ PureRunValue.StringOption(Some "雪\n"); PureRunValue.StringOption None ])))
            roundtrip "optionLists" optional |> ignore
            roundtrip "optionLists" (PureRunValue.OptionValue(listType (optionType stringType), None)) |> ignore
            let products = PureRunValue.ListValue(TypeRef.Named productId,
                [ PureRunValue.Product(productId, [ textId, PureRunScalar.String "entry"; numberId, PureRunScalar.Int64 Int64.MaxValue ]) ])
            let actual = roundtrip "products" products
            Assert.Equal(PureRunValue.ListValue(TypeRef.Named productId,
                [ PureRunValue.Product(productId, [ numberId, PureRunScalar.Int64 Int64.MaxValue; textId, PureRunScalar.String "entry" ]) ]), actual.Actual)
            roundtrip "union" (PureRunValue.OptionValue(TypeRef.Named unionId, Some(PureRunValue.Union(unionId, variantId, [])))) |> ignore
            roundtrip "results" (PureRunValue.ListValue(resultType,
                [ PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 7L)
                  PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String "error") ])) |> ignore
            let oneEmpty = PureRunValue.ListValue(listType intType, [ PureRunValue.Int64List [] ])
            let empty = PureRunValue.ListValue(listType intType, [])
            let mismatch = PureRun.run session (functionId "matrix") [ oneEmpty ] (Some empty) |> unwrap
            Assert.Equal(Some false, mismatch.Matches)
            let count = PureRun.run session (functionId "count") [ oneEmpty ] (Some(PureRunValue.Int64 1L)) |> unwrap
            Assert.Equal(Some true, count.Matches)
            let excessive = PureRunValue.ListValue(listType intType, List.replicate 1500 (PureRunValue.Int64List [ 1L; 2L ]))
            match PureRun.run session (functionId "double") [ excessive ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunProcessFailure", code)
            | other -> failwithf "Oversized nested output accepted: %A" other
            let wire = """{"kind":"list","itemType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]},"items":[{"kind":"option","itemKind":"int64","hasValue":true,"value":"9223372036854775807"},{"kind":"option","itemKind":"int64","hasValue":false}]}"""
            let request = $"""{{"id":"nested","op":"runPure","functionId":"{StableId.value (functionId "listOptions")}","arguments":[{wire}],"expected":{wire}}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            Assert.True(result.GetProperty("matches").GetBoolean())
            use expected = JsonDocument.Parse wire
            Assert.True(JsonElement.DeepEquals(expected.RootElement, result.GetProperty("actual")), result.ToString())
            Assert.Equal(before, File.ReadAllText path))
