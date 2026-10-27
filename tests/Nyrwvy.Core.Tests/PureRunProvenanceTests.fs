namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// runPure diagnostics that lead back to semantic identities: mismatch paths and runtime frames.
module PureRunProvenanceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("run-provenance-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])
    let private itemId = id<TypeTag> "item"
    let private shapeId = id<TypeTag> "shape"
    let private labelId = id<FieldTag> "label"
    let private tagsId = id<FieldTag> "tags"
    let private sizeId = id<FieldTag> "size"
    let private itemsId = id<FieldTag> "items"
    let private dotId = id<VariantTag> "dot"
    let private groupId = id<VariantTag> "group"
    let private itemType = TypeRef.Named(itemId, [])
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private item label tags =
        PureRunValue.Product(itemId, [], [ labelId, PureRunValue.String label; tagsId, PureRunValue.StringList tags ])

    let private group items = PureRunValue.Union(shapeId, [], groupId, [ itemsId, PureRunValue.ListValue(itemType, items) ])

    [<Fact>]
    let ``mismatch paths name fields items and payloads by semantic identity`` () =
        let expected = group [ item "a" [ "x" ]; item "b" [ "y"; "z" ] ]
        let actual = group [ item "a" [ "x" ]; item "b" [ "y"; "Z" ] ]
        let found = PureRun.mismatch expected actual |> Option.get
        Assert.Equal<PureRunPathStep list>(
            [ PureRunPathStep.Field(shapeId, itemsId); PureRunPathStep.Item 1; PureRunPathStep.Field(itemId, tagsId); PureRunPathStep.Item 1 ],
            found.Path)
        Assert.Equal(PureRunValue.String "z", found.Expected)
        Assert.Equal(PureRunValue.String "Z", found.Actual)
        Assert.Equal(None, PureRun.mismatch expected expected)
        // A shared prefix with a different length stops at the List itself.
        let shorter = PureRun.mismatch (PureRunValue.StringList [ "a" ]) (PureRunValue.StringList [ "a"; "b" ]) |> Option.get
        Assert.Empty(shorter.Path)
        // Different variants or tags stop at the Union or Result.
        let dot = PureRunValue.Union(shapeId, [], dotId, [ sizeId, PureRunValue.Int64 1L ])
        Assert.Empty((PureRun.mismatch dot (group []) |> Option.get).Path)
        let ok = PureRunValue.ResultValue(itemType, stringType, true, item "a" [])
        let err = PureRunValue.ResultValue(itemType, stringType, false, PureRunValue.String "a")
        Assert.Empty((PureRun.mismatch ok err |> Option.get).Path)
        let okOther = PureRunValue.ResultValue(itemType, stringType, true, item "b" [])
        Assert.Equal<PureRunPathStep list>([ PureRunPathStep.OkValue; PureRunPathStep.Field(itemId, labelId) ], (PureRun.mismatch ok okOther |> Option.get).Path)
        let some = PureRun.mismatch (PureRunValue.Int64Option(Some 1L)) (PureRunValue.Int64Option(Some 2L)) |> Option.get
        Assert.Equal<PureRunPathStep list>([ PureRunPathStep.SomeValue ], some.Path)
        Assert.Empty((PureRun.mismatch (PureRunValue.Int64Option None) (PureRunValue.Int64Option(Some 2L)) |> Option.get).Path)
        let flat = PureRun.mismatch (PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String "a"))
                                    (PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String "b")) |> Option.get
        Assert.Equal<PureRunPathStep list>([ PureRunPathStep.ErrValue ], flat.Path)
        Assert.Equal(PureRunValue.String "b", flat.Actual)

    let private withSession run =
        let types: SemanticType list =
            [ { Exported = true; TypeParameters = []; Id = itemId; DisplayName = "Item"
                Kind = SemanticTypeKind.Product
                    [ { Id = labelId; DisplayName = "label"; Type = stringType }
                      { Id = tagsId; DisplayName = "tags"; Type = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]) } ] }
              { Exported = true; TypeParameters = []; Id = shapeId; DisplayName = "Shape"
                Kind = SemanticTypeKind.Union
                    [ { Id = dotId; DisplayName = "Dot"; Fields = [ { Id = sizeId; DisplayName = "size"; Type = intType } ] }
                      { Id = groupId; DisplayName = "Group"; Fields = [ { Id = itemsId; DisplayName = "items"; Type = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) } ] } ] } ]
        let create ws name parameterType resultType body =
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  TypeParameters = None
                  DisplayName = name
                  Parameters = [ { DisplayName = "input"; Type = parameterType } ]
                  ResultType = resultType; RequiredEffects = []; Body = body }
            let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let find ws name =
            AgentWorkspace.snapshotForStructuralEditing ws |> fun snapshot -> snapshot.Functions |> List.find (fun fn -> fn.DisplayName = name)
        let replace ws name body =
            let target = find ws name
            let body = body (ExpressionRecipe.Parameter (List.exactlyOne target.Parameters).Id)
            let request: AgentReplaceFunctionBodyWithExpressionRecipe =
                { TargetFunctionId = target.Id
                  ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  ExpectedCurrentResultNodeId = target.Body.ResultNodeId
                  Replacement = body }
            let proposal = AgentExpressionStructuralWorkflow.simulate ws request |> unwrap
            match AgentExpressionStructuralWorkflow.commit ws proposal with
            | AgentStructuralCommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let call ws name argument =
            let target = find ws name
            ExpressionRecipe.DirectCall(target.Id, [ (List.exactlyOne target.Parameters).Id, argument ])
        let input = ExpressionRecipe.ParameterSlot 0
        let next value = ExpressionRecipe.Int64WrappingAdd(value, ExpressionRecipe.Int64Literal 1L)
        let ws = AgentWorkspace.create { Snapshot.empty (id<SnapshotTag> "initial") with Types = types } |> unwrap
        let ws = create ws "shape" (TypeRef.Named(shapeId, [])) (TypeRef.Named(shapeId, [])) input
        let ws = create ws "check" intType (resultType itemType stringType)
                    (ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.ProductConstruct(itemId, [], [ labelId, ExpressionRecipe.StringLiteral "ok"; tagsId, ExpressionRecipe.ListEmpty stringType ])))
        // Unbounded self and mutual recursion are valid programs that fail only at run time. The self
        // call is an operand (loop(x + 1) + 1), not a tail call, which would run as a loop instead.
        let ws = create ws "loop" intType intType input
        let ws = replace ws "loop" (fun value -> next (call ws "loop" (next value)))
        let ws = create ws "ping" intType intType input
        let ws = create ws "pong" intType intType (call ws "ping" (next input))
        let ws = replace ws "ping" (fun value -> call ws "pong" (next value))
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-provenance-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap)
            // A snapshot written directly declares its backend as `create` would.
            File.WriteAllText(path + ".backends.json", """{"targets": ["csharp"]}""")
            use session = AuthoringSession.openExisting path |> unwrap
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            run session path functionId
        finally Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure reports mismatch paths and maps runtime stacks to function identities`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let value = group [ item "a" [ "x" ]; item "b" [ "y" ] ]
            let result = PureRun.run session (functionId "shape") [ value ] (Some(group [ item "a" [ "x" ]; item "c" [ "y" ] ])) |> unwrap
            Assert.Equal(Some false, result.Matches)
            let found = Option.get result.Mismatch
            Assert.Equal<PureRunPathStep list>([ PureRunPathStep.Field(shapeId, itemsId); PureRunPathStep.Item 1; PureRunPathStep.Field(itemId, labelId) ], found.Path)
            Assert.Equal(PureRunValue.String "b", found.Actual)
            let matched = PureRun.run session (functionId "shape") [ value ] (Some value) |> unwrap
            Assert.Equal(None, matched.Mismatch)
            let request = $"""{{"id":"mismatch","op":"runPure","functionId":"{StableId.value (functionId "check")}","arguments":[{{"kind":"int64","value":"1"}}],"expected":{{"kind":"result","okType":{{"kind":"named","typeId":"{StableId.value itemId}"}},"errorType":{{"kind":"primitive","primitive":"string"}},"isOk":false,"value":{{"kind":"string","value":"ok"}}}}}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let mismatch = response.RootElement.GetProperty("result").GetProperty("mismatch")
            Assert.Equal(0, mismatch.GetProperty("path").GetArrayLength())
            Assert.False(mismatch.GetProperty("expected").GetProperty("isOk").GetBoolean())
            Assert.True(mismatch.GetProperty("actual").GetProperty("isOk").GetBoolean())
            match PureRun.run session (functionId "loop") [ PureRunValue.Int64 0L ] None with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("System.StackOverflowException", failure.ExceptionType)
                Assert.Contains("recursion was too deep", failure.Message)
                Assert.Equal(functionId "loop", failure.FunctionId)
                Assert.NotEmpty(failure.Frames)
                Assert.All(failure.Frames, fun frame -> Assert.Equal(PureRunFrame.Function(functionId "loop"), frame))
            | other -> failwithf "Unbounded recursion was not a runtime failure: %A" other
            let request = $"""{{"id":"overflow","op":"runPure","functionId":"{StableId.value (functionId "ping")}","arguments":[{{"kind":"int64","value":"0"}}]}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            let root = response.RootElement
            Assert.False(root.GetProperty("ok").GetBoolean())
            let error = root.GetProperty("error")
            Assert.Equal("RunRuntimeFailure", error.GetProperty("code").GetString())
            Assert.Equal(StableId.value (functionId "ping"), error.GetProperty("functionId").GetString())
            let frames = error.GetProperty("frames").EnumerateArray() |> Seq.map (fun frame -> frame.GetProperty("functionId").GetString()) |> Set.ofSeq
            Assert.Equal<Set<string>>(Set.ofList [ StableId.value (functionId "ping"); StableId.value (functionId "pong") ], frames)
            Assert.Equal(before, File.ReadAllText path))
