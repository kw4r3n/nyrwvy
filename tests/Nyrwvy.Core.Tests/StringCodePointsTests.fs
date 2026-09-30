namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringCodePoints: each code point of a String as a one-code-point String, in order.
module StringCodePointsTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-code-points-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``code points type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "points"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped StringCodePoints was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request [ intType ] (listOf stringType) (ExpressionRecipe.StringCodePoints(slot 0))))
        // The result is List<String>, not String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringCodePoints(slot 0))))
        let proposal =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
                (request [ stringType ] (listOf stringType) (ExpressionRecipe.StringCodePoints(slot 0))) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringCodePoints\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        let nyason = NyasonCodec.serialize decoded |> unwrap "nyason"
        Assert.Contains("string-code-points", nyason)
        let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringCodePoints\"", TypedSemanticIrCodec.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringCodePoints\"", ExecutionIrCodec.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains("NyList<string>.FromOwnedArray(codePoints.ToArray())", artifact.Source)
        // A persisted node over a non-String value is a validation error.
        let parameterId = committed.Functions.Head.Parameters.Head.Id
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference reference when reference = parameterId ->
                                        { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored code points split on code points and fold long text linearly`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-code-points-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let listType = sprintf """{"kind":"builtinGeneric","generic":"list","arguments":[%s]}""" (primitive "string")
            let line =
                sprintf """{"id":"points","op":"simulateCreateFunctionByName","displayName":"points","parameters":[{"displayName":"text","type":%s}],"resultType":%s,"requiredEffects":[],"body":{"kind":"stringCodePoints","value":{"kind":"parameter","name":"text"}}}""" (primitive "string") listType
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
            Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let create name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      DisplayName = name
                      Parameters = parameters |> List.map (fun (displayName, parameterType) -> { DisplayName = displayName; Type = parameterType })
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            let slot = ExpressionRecipe.ParameterSlot
            create "measure" [ "text", stringType ] intType (ExpressionRecipe.StringLength(slot 0))
            create "pointsAll" [ "texts", listOf stringType ] (listOf (listOf stringType))
                (ExpressionRecipe.ListMap(slot 0, functionId "points", []))
            // An unpaired surrogate cannot cross the runner boundary, so split it from a literal and
            // report each item's code-point length: a pair stays one item, a lone surrogate is its own.
            create "loneWidths" [] (listOf intType)
                (ExpressionRecipe.ListMap(ExpressionRecipe.StringCodePoints(ExpressionRecipe.StringLiteral (utf16 "a{D800}b{DC00}😀")), functionId "measure", []))
            create "addLength" [ "total", intType; "item", stringType ] intType
                (ExpressionRecipe.Int64WrappingAdd(slot 0, ExpressionRecipe.StringLength(slot 1)))
            create "foldCount" [ "text", stringType ] intType
                (ExpressionRecipe.ListFold(ExpressionRecipe.StringCodePoints(slot 0), ExpressionRecipe.Int64Literal 0L, functionId "addLength", []))
            let texts = [ ""; "hello"; "a😀b"; "日本語"; "é"; "😀😀" ]
            let expected =
                PureRunValue.ListValue(
                    listOf stringType,
                    [ PureRunValue.StringList []
                      PureRunValue.StringList [ "h"; "e"; "l"; "l"; "o" ]
                      PureRunValue.StringList [ "a"; "😀"; "b" ]
                      PureRunValue.StringList [ "日"; "本"; "語" ]
                      PureRunValue.StringList [ "e"; "́" ]
                      PureRunValue.StringList [ "😀"; "😀" ] ])
            let split = PureRun.run session (functionId "pointsAll") [ PureRunValue.StringList texts ] (Some expected) |> unwrap "split"
            Assert.True((split.Matches = Some true), sprintf "%A" split.Mismatch)
            let lone = PureRun.run session (functionId "loneWidths") [] None |> unwrap "lone"
            Assert.Equal(PureRunValue.Int64List [ 1L; 1L; 1L; 1L; 1L ], lone.Actual)
            // Far past the ~7.7K characters where per-character self-recursion overflowed the stack.
            let long = String.replicate 15000 "ab😀"
            let counted = PureRun.run session (functionId "foldCount") [ PureRunValue.String long ] None |> unwrap "fold"
            Assert.Equal(PureRunValue.Int64 45000L, counted.Actual)
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
