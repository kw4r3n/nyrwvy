namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringSpan and StringSpanBack: the code-point positions where a run of set members ends,
/// scanning forward from `from` or backward from `end`; pure, linear and never failing.
module StringSpanTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-span-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value
    let private int value = ExpressionRecipe.Int64Literal value

    [<Fact>]
    let ``StringSpan and StringSpanBack type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "span"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string span was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ intType; stringType ] intType (ExpressionRecipe.StringSpan(slot 0, slot 1, int 0L))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] intType (ExpressionRecipe.StringSpan(slot 0, slot 1, int 0L))))
        Assert.Equal(mismatch, rejects (request [ stringType; stringType ] intType (ExpressionRecipe.StringSpan(slot 0, slot 1, slot 1))))
        Assert.Equal(mismatch, rejects (request [ intType; stringType ] intType (ExpressionRecipe.StringSpanBack(slot 0, slot 1, int 0L))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] intType (ExpressionRecipe.StringSpanBack(slot 0, slot 1, int 0L))))
        Assert.Equal(mismatch, rejects (request [ stringType; stringType ] intType (ExpressionRecipe.StringSpanBack(slot 0, slot 1, slot 0))))
        // Result type: Int64, not Option<Int64> or String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringSpan(slot 0, text " ", int 0L))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringSpanBack(slot 0, text " ", int 0L))))
        let cases =
            [ "stringSpan", "NyText.Span(", ExpressionRecipe.StringSpan(slot 0, slot 1, int 2L)
              "stringSpanBack", "NyText.SpanBack(", ExpressionRecipe.StringSpanBack(slot 0, slot 1, int 2L) ]
        for kind, emitted, body in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ stringType; stringType ] intType body)
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
            let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
            let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            Assert.Contains($"\"{kind}\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains($"\"{kind}\"", ExecutionIrSerializer.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            Assert.Contains(emitted, artifact.Source)
            // A persisted node over a non-String set is a validation error.
            let parameterId = committed.Functions.Head.Parameters.[1].Id
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
    let ``JSONL-authored StringSpan and StringSpanBack match Python lstrip and rstrip and stay linear`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-span-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let parameter name = sprintf """{"kind":"parameter","name":"%s"}""" name
            let author name (parameters: (string * string) list) resultType body =
                let parameterJson =
                    parameters |> List.map (fun (displayName, typeJson) -> sprintf """{"displayName":"%s","type":%s}""" displayName typeJson) |> String.concat ","
                let line =
                    sprintf """{"id":"%s","op":"simulateCreateFunctionByName","exported":true,"displayName":"%s","parameters":[%s],"resultType":%s,"requiredEffects":[],"body":%s}"""
                        name name parameterJson resultType body
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            let str = primitive "string"
            let i64 = primitive "int64"
            author "span" [ "text", str; "set", str; "from", i64 ] i64
                (sprintf """{"kind":"stringSpan","value":%s,"set":%s,"from":%s}""" (parameter "text") (parameter "set") (parameter "from"))
            author "spanBack" [ "text", str; "set", str; "end", i64 ] i64
                (sprintf """{"kind":"stringSpanBack","value":%s,"set":%s,"end":%s}""" (parameter "text") (parameter "set") (parameter "end"))
            // Python strip(chars) as one expression over both positions.
            author "strip" [ "text", str; "set", str ] str
                (sprintf
                    """{"kind":"stringSlice","value":%s,"start":{"kind":"stringSpan","value":%s,"set":%s,"from":{"kind":"int64Literal","value":0}},"end":{"kind":"stringSpanBack","value":%s,"set":%s,"end":{"kind":"stringLength","value":%s}}}"""
                    (parameter "text") (parameter "text") (parameter "set") (parameter "text") (parameter "set") (parameter "text"))
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            Assert.Equal(PureRunValue.Int64 3L, run "span" [ PureRunValue.String "  😀x"; PureRunValue.String " 😀"; PureRunValue.Int64 0L ])
            Assert.Equal(PureRunValue.Int64 1L, run "spanBack" [ PureRunValue.String "x😀 "; PureRunValue.String " 😀"; PureRunValue.Int64 3L ])
            Assert.Equal(PureRunValue.String "a b", run "strip" [ PureRunValue.String " \t a b\n　"; PureRunValue.String " \t\n　" ])
            let create name resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = []
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            // Python parity, generated with python3 from
            //   span:     start = clamp(from, 0, len); start + len(v[start:]) - len(v[start:].lstrip(set))
            //   spanBack: e = clamp(end, 0, len); len(v[:e].rstrip(set))
            // Python counts a non-BMP character as one code point, as StringLength does. Lone
            // surrogates are built with utf16, since F# literals turn them into U+FFFD.
            let cases =
                [ ("", " ", 0L, 0L), (0L, 0L)
                  ("", "", 3L, 3L), (0L, 0L)
                  ("abc", "", 1L, 2L), (1L, 2L)
                  ("abc", "", -5L, 9L), (0L, 3L)
                  ("  hi  ", " ", 0L, 6L), (2L, 4L)
                  ("  hi  ", " ", -3L, 99L), (2L, 4L)
                  ("  hi  ", " ", 3L, 3L), (3L, 3L)
                  ("aaaa", "a", 0L, 4L), (4L, 0L)
                  ("aaaa", "a", 2L, 2L), (4L, 0L)
                  ("abcd", "xyz", 0L, 4L), (0L, 4L)
                  ("abcd", "xyz", 9L, -1L), (4L, 0L)
                  ("\t\n x y \n", " \t\n\u3000", 0L, 9L), (3L, 6L)
                  ("\u3000\u3000全角\u3000", " \t\n\u3000", 0L, 5L), (2L, 4L)
                  ("123abc456", "0123456789", 0L, 9L), (3L, 6L)
                  ("123abc456", "0123456789", 3L, 6L), (3L, 6L)
                  ("😀😀x😀", "😀", 0L, 4L), (2L, 3L)
                  ("x😀😀", "😀", 0L, 3L), (0L, 1L)
                  ("😀a", utf16 "{D83D}", 0L, 2L), (0L, 2L)
                  (utf16 "a{D800}{D800}b", utf16 "{D800}", 1L, 3L), (3L, 1L)
                  (utf16 "{D800}a{D800}", utf16 "a{D800}", 0L, 3L), (3L, 0L)
                  ("abcabc", "cba", 0L, 6L), (6L, 0L)
                  ("abc", "abc", 1L, 2L), (3L, 0L) ]
            create "spanTable" (listOf intType)
                (ExpressionRecipe.ListConstruct(intType, cases |> List.map (fun ((value, set, from, _), _) -> ExpressionRecipe.StringSpan(text value, text set, int from))))
            create "spanBackTable" (listOf intType)
                (ExpressionRecipe.ListConstruct(intType, cases |> List.map (fun ((value, set, _, stop), _) -> ExpressionRecipe.StringSpanBack(text value, text set, int stop))))
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            check "spanTable" (PureRunValue.Int64List(cases |> List.map (snd >> fst)))
            check "spanBackTable" (PureRunValue.Int64List(cases |> List.map (snd >> snd)))
            // Long text: one linear pass per call, where the probe-5 self-recursive skipIn needed a frame per character.
            let long = String.replicate 50000 " " + "x"
            Assert.Equal(PureRunValue.Int64 50000L, run "span" [ PureRunValue.String long; PureRunValue.String " "; PureRunValue.Int64 0L ])
            Assert.Equal(PureRunValue.Int64 0L, run "spanBack" [ PureRunValue.String long; PureRunValue.String " "; PureRunValue.Int64 50000L ])
            // The runner caps a String argument at 65,536 UTF-16 code units.
            Assert.Equal(PureRunValue.String "x", run "strip" [ PureRunValue.String (long + String.replicate 15000 " "); PureRunValue.String " " ])
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
