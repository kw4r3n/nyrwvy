namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringJoin (Python `sep.join(list)`) and ListSlice (generic List<T> slice clamped like
/// StringSlice), both pure and never failing.
module StringJoinListSliceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("join-slice-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value
    let private int value = ExpressionRecipe.Int64Literal value
    let private strings items = ExpressionRecipe.ListConstruct(stringType, items |> List.map text)

    [<Fact>]
    let ``StringJoin and ListSlice type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "joinSlice"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped builtin was accepted: %A" other
        let accepts request =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap "accepted" |> ignore
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        let join = ExpressionRecipe.StringJoin(slot 0, slot 1)
        Assert.Equal(mismatch, rejects (request [ listOf intType; stringType ] stringType join))
        Assert.Equal(mismatch, rejects (request [ stringType; stringType ] stringType join))
        Assert.Equal(mismatch, rejects (request [ listOf stringType; intType ] stringType join))
        Assert.Equal(mismatch, rejects (request [ listOf (optionOf stringType); stringType ] stringType join))
        // The result is a String, not a List<String>.
        Assert.NotEqual<string>("", rejects (request [ listOf stringType; stringType ] (listOf stringType) join))
        let slice = ExpressionRecipe.ListSlice(slot 0, slot 1, slot 2)
        Assert.Equal(mismatch, rejects (request [ stringType; intType; intType ] stringType slice))
        Assert.Equal(mismatch, rejects (request [ listOf intType; stringType; intType ] (listOf intType) slice))
        Assert.Equal(mismatch, rejects (request [ listOf intType; intType; stringType ] (listOf intType) slice))
        // The result keeps the input's item type; it is neither Option<T> nor another List type.
        Assert.NotEqual<string>("", rejects (request [ listOf intType; intType; intType ] (optionOf intType) slice))
        Assert.NotEqual<string>("", rejects (request [ listOf intType; intType; intType ] (listOf stringType) slice))
        accepts (request [ listOf (optionOf intType); intType; intType ] (listOf (optionOf intType)) slice)
        accepts (request [] (listOf stringType) (ExpressionRecipe.ListSlice(ExpressionRecipe.ListEmpty stringType, int 0L, int 1L)))
        let cases =
            [ "stringJoin", "string-join", "NyText.Join(", [ listOf stringType; stringType ], stringType, join, "StringIntrinsicTypeMismatch"
              "listSlice", "list-slice", ".Slice(", [ listOf (listOf stringType); intType; intType ], listOf (listOf stringType), slice, "ListSliceListTypeMismatch" ]
        for kind, nyasonTag, emitted, parameters, resultType, body, brokenCode in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request parameters resultType body)
                |> unwrap ("simulate " + kind)
            let committed =
                match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
                | other -> failwithf "Commit failed: %A" other
            Assert.Empty(SnapshotValidator.validate committed)
            let json = SnapshotCodec.serialize committed |> unwrap "serialize"
            Assert.Contains($"\"{kind}\"", json)
            let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
            let nyason = NyasonCodec.serialize decoded |> unwrap "nyason"
            Assert.Contains(nyasonTag, nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            Assert.Contains($"\"{kind}\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains($"\"{kind}\"", ExecutionIrSerializer.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            Assert.Contains(emitted, artifact.Source)
            // A persisted node whose list input is not the required list is a validation error.
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
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = brokenCode)

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored StringJoin and ListSlice match Python join and clamped slicing`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-join-slice-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let author line =
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            let S = """{"kind":"primitive","primitive":"string"}"""
            let I = """{"kind":"primitive","primitive":"int64"}"""
            let listWire item = """{"kind":"builtinGeneric","generic":"list","arguments":[""" + item + "]}"
            author (
                """{"id":"join","op":"simulateCreateFunctionByName","displayName":"join","parameters":[{"displayName":"xs","type":"""
                + listWire S + """},{"displayName":"sep","type":""" + S + """}],"resultType":""" + S
                + ""","requiredEffects":[],"body":{"kind":"stringJoin","list":{"kind":"parameter","name":"xs"},"separator":{"kind":"parameter","name":"sep"}}}"""
            )
            author (
                """{"id":"slice","op":"simulateCreateFunctionByName","displayName":"slice","parameters":[{"displayName":"rows","type":"""
                + listWire (listWire S) + """},{"displayName":"start","type":""" + I + """},{"displayName":"end","type":""" + I
                + """}],"resultType":""" + listWire (listWire S)
                + ""","requiredEffects":[],"body":{"kind":"listSlice","list":{"kind":"parameter","name":"rows"},"start":{"kind":"parameter","name":"start"},"end":{"kind":"parameter","name":"end"}}}"""
            )
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            Assert.Equal(PureRunValue.String "a, b, c", run "join" [ PureRunValue.StringList [ "a"; "b"; "c" ]; PureRunValue.String ", " ])
            Assert.Equal(PureRunValue.String "", run "join" [ PureRunValue.StringList []; PureRunValue.String "," ])
            // Nested item type: slicing List<List<String>> keeps each inner list whole.
            let rows =
                PureRunValue.ListValue(
                    listOf stringType,
                    [ PureRunValue.StringList [ "a"; "b" ]; PureRunValue.StringList []; PureRunValue.StringList [ "c" ] ]
                )
            Assert.Equal(
                PureRunValue.ListValue(listOf stringType, [ PureRunValue.StringList []; PureRunValue.StringList [ "c" ] ]),
                run "slice" [ rows; PureRunValue.Int64 1L; PureRunValue.Int64 3L ]
            )
            Assert.Equal(PureRunValue.ListValue(listOf stringType, []), run "slice" [ rows; PureRunValue.Int64 2L; PureRunValue.Int64 1L ])
            Assert.Equal(rows, run "slice" [ rows; PureRunValue.Int64 -9L; PureRunValue.Int64 9L ])
            let create name resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      DisplayName = name
                      Parameters = []
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            // Python parity: separator.join(items).
            let joinCases =
                [ ExpressionRecipe.StringJoin(strings [], text ","), ""
                  ExpressionRecipe.StringJoin(strings [ "a" ], text ","), "a"
                  ExpressionRecipe.StringJoin(strings [ "a"; "b"; "c" ], text ", "), "a, b, c"
                  ExpressionRecipe.StringJoin(strings [ "" ], text ","), ""
                  ExpressionRecipe.StringJoin(strings [ ""; "" ], text ","), ","
                  ExpressionRecipe.StringJoin(strings [ "a"; ""; "b" ], text ","), "a,,b"
                  ExpressionRecipe.StringJoin(strings [ "a"; "b" ], text ""), "ab"
                  ExpressionRecipe.StringJoin(strings [ "日本"; "語" ], text "・"), "日本・語"
                  ExpressionRecipe.StringJoin(strings [ "😀"; "😀" ], text "‍"), "😀‍😀"
                  ExpressionRecipe.StringJoin(strings [ "x"; "y"; "z" ], text "→😀←"), "x→😀←y→😀←z"
                  ExpressionRecipe.StringJoin(strings [ "Ω"; "ω" ], text "\n"), "Ω\nω"
                  // ",".join("a,,b,".split(",")) == "a,,b,": StringJoin inverts StringSplit.
                  ExpressionRecipe.StringJoin(ExpressionRecipe.StringSplit(text "a,,b,", text ","), text ","), "a,,b,"
                  ExpressionRecipe.StringJoin(ExpressionRecipe.StringCodePoints(text "a😀b"), text "-"), "a-😀-b" ]
            create "joinTable" (listOf stringType) (ExpressionRecipe.ListConstruct(stringType, joinCases |> List.map fst))
            // Python items[start:end] for 0 <= start, end; negative bounds clamp to 0 instead of
            // counting from the end (marked).
            let source = strings [ "a"; "b"; "c"; "d"; "e" ]
            let sliceCases =
                [ 0L, 5L, "a|b|c|d|e"
                  1L, 3L, "b|c"
                  4L, 5L, "e"
                  2L, 2L, ""
                  3L, 1L, ""
                  0L, 100L, "a|b|c|d|e"
                  5L, 9L, ""
                  100L, 200L, ""
                  -2L, 2L, "a|b" // Python items[-2:2] == []
                  -5L, -1L, "" // Python items[-5:-1] == a..d
                  -1L, 100L, "a|b|c|d|e" // Python items[-1:100] == [e]
                  Int64.MinValue, Int64.MaxValue, "a|b|c|d|e" ]
            create "sliceTable" (listOf stringType)
                (ExpressionRecipe.ListConstruct(
                    stringType,
                    sliceCases |> List.map (fun (start, stop, _) -> ExpressionRecipe.StringJoin(ExpressionRecipe.ListSlice(source, int start, int stop), text "|"))
                ))
            let lengthCases =
                [ ExpressionRecipe.ListSlice(ExpressionRecipe.ListEmpty stringType, int 0L, int 1L), 0L
                  ExpressionRecipe.ListSlice(source, int 1L, int 4L), 3L
                  ExpressionRecipe.ListSlice(strings [ ""; "" ], int 0L, int 2L), 2L ]
            create "sliceLengths" (listOf intType)
                (ExpressionRecipe.ListConstruct(intType, lengthCases |> List.map (fst >> ExpressionRecipe.ListLength)))
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            check "joinTable" (PureRunValue.StringList(joinCases |> List.map snd))
            check "sliceTable" (PureRunValue.StringList(sliceCases |> List.map (fun (_, _, expected) -> expected)))
            check "sliceLengths" (PureRunValue.Int64List(lengthCases |> List.map snd))
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
