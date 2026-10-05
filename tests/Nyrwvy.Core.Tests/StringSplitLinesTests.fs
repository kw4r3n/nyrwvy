namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringSplitLines: the lines of a String split at any code point of a breaks set, with "\r\n" as
/// one break when both halves are members; Python splitlines for the matching set, pure and linear.
module StringSplitLinesTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-split-lines-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    [<Fact>]
    let ``StringSplitLines type checks, persists, and lowers through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "lines"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped StringSplitLines was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ intType; stringType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1))))
        Assert.Equal(mismatch, rejects (request [ stringType; listOf stringType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1))))
        // Result type: List<String>, not String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringSplitLines(slot 0, text "\n"))))
        let proposal =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ stringType; stringType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1)))
            |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringSplitLines\"", json)
        Assert.Contains("\"breaksNodeId\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let nyason = NyasonCodec.serialize decoded |> unwrap "nyason"
        Assert.Contains("string-split-lines", nyason)
        let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringSplitLines\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringSplitLines\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains("NyText.SplitLines(", artifact.Source)
        // The breaks operand is a character set, like StringSpan's set.
        let splitNode =
            committed.Functions.Head.Body.Nodes
            |> List.find (fun node -> match node.Kind with SemanticNodeKind.StringSplitLines _ -> true | _ -> false)
        Assert.Contains(ValueDependency.ofNode splitNode, fun dependency -> dependency.Role = ValueDependencyRole.CharacterSet)
        // A persisted node over non-String breaks is a validation error.
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
    let ``JSONL-authored StringSplitLines matches Python splitlines and stays linear`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-split-lines-" + Guid.NewGuid().ToString("N"))
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
                    sprintf """{"id":"%s","op":"simulateCreateFunctionByName","displayName":"%s","parameters":[%s],"resultType":%s,"requiredEffects":[],"body":%s}"""
                        name name parameterJson resultType body
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            let str = primitive "string"
            author "lines" [ "text", str; "breaks", str ] (sprintf """{"kind":"builtinGeneric","generic":"list","arguments":[%s]}""" str)
                (sprintf """{"kind":"stringSplitLines","value":%s,"breaks":%s}""" (parameter "text") (parameter "breaks"))
            // Line normalization in one expression: split at \n, \r and \r\n, rejoin with \n.
            author "normalize" [ "text", str ] str
                (sprintf """{"kind":"stringJoin","list":{"kind":"stringSplitLines","value":%s,"breaks":{"kind":"stringLiteral","value":"\n\r"}},"separator":{"kind":"stringLiteral","value":"\n"}}""" (parameter "text"))
            author "lineCount" [ "text", str ] (primitive "int64")
                (sprintf """{"kind":"listLength","value":{"kind":"stringSplitLines","value":%s,"breaks":{"kind":"stringLiteral","value":"\n\r"}}}""" (parameter "text"))
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            Assert.Equal(PureRunValue.StringList [ "a"; ""; "b😀" ], run "lines" [ PureRunValue.String "a\r\n\nb😀\n"; PureRunValue.String "\n\r" ])
            Assert.Equal(PureRunValue.StringList [], run "lines" [ PureRunValue.String ""; PureRunValue.String "\n" ])
            Assert.Equal(PureRunValue.String "a\n\nb", run "normalize" [ PureRunValue.String "a\r\r\nb\r" ])
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
            // Python parity, generated with python3: with pyBreaks the expected value is
            // value.splitlines(); with byteBreaks it is [b.decode() for b in value.encode().splitlines()];
            // other sets use the per-code-point oracle of docs/decisions/string-split-lines.md. Python
            // counts a non-BMP character as one code point, as the break set does. Lone surrogates are
            // built with utf16, since F# literals turn them into U+FFFD.
            let pyBreaks = "\n\r\u000B\u000C\u001C\u001D\u001E\u0085  "
            let byteBreaks = "\n\r"
            let cases =
                [ ("", pyBreaks), []
                  ("", ""), []
                  ("abc", ""), [ "abc" ]
                  ("\n", ""), [ "\n" ]
                  ("abc", pyBreaks), [ "abc" ]
                  ("a\n", pyBreaks), [ "a" ]
                  ("a\n\nb", pyBreaks), [ "a"; ""; "b" ]
                  ("\n", pyBreaks), [ "" ]
                  ("\n\n", pyBreaks), [ ""; "" ]
                  ("a\n\n\nb\n\n", pyBreaks), [ "a"; ""; ""; "b"; "" ]
                  ("a\rb", pyBreaks), [ "a"; "b" ]
                  ("a\r\nb", pyBreaks), [ "a"; "b" ]
                  ("a\n\rb", pyBreaks), [ "a"; ""; "b" ]
                  ("\r\n", pyBreaks), [ "" ]
                  ("\r\r\n\n", pyBreaks), [ ""; ""; "" ]
                  ("a\r\n\r\nb\r\n", byteBreaks), [ "a"; ""; "b" ]
                  ("a\r\r\nb", byteBreaks), [ "a"; ""; "b" ]
                  ("\r", byteBreaks), [ "" ]
                  ("a\n\rb\r", byteBreaks), [ "a"; ""; "b" ]
                  ("a\r\nb", "\r"), [ "a"; "\nb" ]
                  ("a\r\nb\r", "\r"), [ "a"; "\nb" ]
                  ("a\r\nb", "\n"), [ "a\r"; "b" ]
                  ("\r\n\r\n", "\r"), [ ""; "\n"; "\n" ]
                  ("0\n1\r2\u000B3\u000C4\u001C5\u001D6\u001E7\u00858\u20289\u2029z", pyBreaks), [ "0"; "1"; "2"; "3"; "4"; "5"; "6"; "7"; "8"; "9"; "z" ]
                  ("0\n1\r2\u000B3\u000C4\u001C5\u001D6\u001E7\u00858\u20289\u2029z", byteBreaks), [ "0"; "1"; "2\u000B3\u000C4\u001C5\u001D6\u001E7\u00858\u20289\u2029z" ]
                  ("\n\r\u000B\u000C\u001C\u001D\u001E\u0085\u2028\u2029", pyBreaks), [ ""; ""; ""; ""; ""; ""; ""; ""; ""; "" ]
                  ("a\u2028b\u0085c", byteBreaks), [ "a\u2028b\u0085c" ]
                  ("a\u2028b\u0085c\u2029", pyBreaks), [ "a"; "b"; "c" ]
                  ("a\u2028b", "\u2028"), [ "a"; "b" ]
                  ("a😀b😀", "😀"), [ "a"; "b" ]
                  ("😀\n😀", pyBreaks), [ "😀"; "😀" ]
                  ("a😀b", utf16 "{D83D}"), [ "a😀b" ]
                  (utf16 "x{D800}y{D800}", utf16 "{D800}"), [ "x"; "y" ]
                  (utf16 "{D800}\n{DC00}", pyBreaks), [ utf16 "{D800}"; utf16 "{DC00}" ]
                  ("a,b;;c", ",;"), [ "a"; "b"; ""; "c" ]
                  (" a b ", " "), [ ""; "a"; "b" ] ]
            create "linesTable" (listOf (listOf stringType))
                (ExpressionRecipe.ListConstruct(listOf stringType, cases |> List.map (fun ((value, breaks), _) -> ExpressionRecipe.StringSplitLines(text value, text breaks))))
            let result = PureRun.run session (functionId "linesTable") [] (Some(PureRunValue.ListValue(listOf stringType, cases |> List.map (snd >> PureRunValue.StringList)))) |> unwrap "linesTable"
            Assert.True((result.Matches = Some true), sprintf "linesTable: %A" result.Actual)
            // Long text near the runner's 65,536 UTF-16 unit argument cap: 20,000 lines in one linear
            // pass, where the probe-5 fold carried an accumulator through every character.
            let long =
                String.Join("", Seq.init 20000 (fun k -> match k % 3 with | 0 -> "x\r\n" | 1 -> "y\n" | _ -> "z\r"))
            Assert.True(long.Length > 45000 && long.Length <= 65536)
            Assert.Equal(PureRunValue.Int64 20000L, run "lineCount" [ PureRunValue.String long ])
            let expected = String.Join("\n", Seq.init 20000 (fun k -> match k % 3 with | 0 -> "x" | 1 -> "y" | _ -> "z"))
            Assert.Equal(PureRunValue.String expected, run "normalize" [ PureRunValue.String long ])
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
