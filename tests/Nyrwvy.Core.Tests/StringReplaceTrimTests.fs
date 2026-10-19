namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringReplace (Python str.replace, ordinal in code points, empty pattern is a no-op) and
/// StringTrim, StringTrimStart and StringTrimEnd (Unicode White_Space, as .NET char.IsWhiteSpace).
module StringReplaceTrimTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-replace-trim-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    /// The 25 White_Space code points, all in the BMP.
    let private whiteSpace =
        [ yield! [ 0x09 .. 0x0D ]
          0x20
          0x85
          0xA0
          0x1680
          yield! [ 0x2000 .. 0x200A ]
          0x2028
          0x2029
          0x202F
          0x205F
          0x3000 ]
        |> List.map (char >> string)

    [<Fact>]
    let ``StringReplace and the trims type check, persist, and lower through every layer`` () =
        Assert.Equal(25, whiteSpace.Length)
        Assert.All(whiteSpace, fun space -> Assert.True(Char.IsWhiteSpace(space, 0)))
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "edit"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string edit was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringReplace(slot 0, text "a", text "b"))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringReplace(text "a", slot 0, text "b"))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringReplace(text "a", text "b", slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringTrim(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringTrimStart(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringTrimEnd(slot 0))))
        // The result is a String, not an Int64.
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringTrim(slot 0))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringReplace(slot 0, text "a", text "b"))))
        let cases =
            [ "stringReplace", "string-replace", "NyText.Replace(", [ stringType; stringType; stringType ], ExpressionRecipe.StringReplace(slot 0, slot 1, slot 2)
              "stringTrim", "string-trim", "NyText.Trim(", [ stringType ], ExpressionRecipe.StringTrim(slot 0)
              "stringTrimStart", "string-trim-start", "NyText.TrimStart(", [ stringType ], ExpressionRecipe.StringTrimStart(slot 0)
              "stringTrimEnd", "string-trim-end", "NyText.TrimEnd(", [ stringType ], ExpressionRecipe.StringTrimEnd(slot 0) ]
        for kind, nyasonTag, emitted, parameters, body in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request parameters stringType body)
                |> unwrap ("simulate " + kind)
            let committed =
                match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
                | other -> failwithf "Commit failed: %A" other
            Assert.Empty(SnapshotValidator.validate committed)
            Assert.Equal(SnapshotSchema.minimumVersion SnapshotSchemaFeature.StringReplaceAndTrim, SnapshotSchema.required committed)
            let json = SnapshotCodec.serialize committed |> unwrap "serialize"
            Assert.Contains($"\"{kind}\"", json)
            let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
            let nyason = NyasonCodec.serialize decoded |> unwrap "nyason"
            // The exact tag, so string-trim is not found inside string-trim-start.
            Assert.Matches($"(^|[^-a-z]){nyasonTag}([^-a-z]|$)", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            Assert.Contains($"\"{kind}\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains($"\"{kind}\"", ExecutionIrSerializer.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            Assert.Contains(emitted, artifact.Source)
            // A persisted node over a non-String operand is a validation error.
            let broken =
                { committed with
                    Functions = committed.Functions |> List.map (fun fn ->
                        { fn with
                            Body =
                                { fn.Body with
                                    Nodes = fn.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference _ -> { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored StringReplace and trims follow the stated semantics under runPure`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-replace-trim-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let str = """{"kind":"primitive","primitive":"string"}"""
            let parameter name = sprintf """{"kind":"parameter","name":"%s"}""" name
            let author name (parameters: string list) body =
                let parameterJson = parameters |> List.map (fun displayName -> sprintf """{"displayName":"%s","type":%s}""" displayName str) |> String.concat ","
                let line =
                    sprintf """{"id":"%s","op":"simulateCreateFunctionByName","displayName":"%s","parameters":[%s],"resultType":%s,"requiredEffects":[],"body":%s}"""
                        name name parameterJson str body
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            author "replace" [ "text"; "pattern"; "replacement" ]
                (sprintf """{"kind":"stringReplace","value":%s,"pattern":%s,"replacement":%s}""" (parameter "text") (parameter "pattern") (parameter "replacement"))
            for kind in [ "stringTrim"; "stringTrimStart"; "stringTrimEnd" ] do
                author kind [ "text" ] (sprintf """{"kind":"%s","value":%s}""" kind (parameter "text"))
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) (arguments |> List.map PureRunValue.String) None |> unwrap name).Actual
            Assert.Equal(PureRunValue.String "a-b-c", run "replace" [ "a😀b😀c"; "😀"; "-" ])
            Assert.Equal(PureRunValue.String "x", run "stringTrim" [ "　 x\u0085\t" ])
            Assert.Equal(PureRunValue.String "x\u0085\t", run "stringTrimStart" [ "　 x\u0085\t" ])
            Assert.Equal(PureRunValue.String "　 x", run "stringTrimEnd" [ "　 x\u0085\t" ])
            let create name resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = []
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            // Python parity for str.replace(old, new) except an empty pattern. Lone surrogates are
            // built with utf16, since F# literals turn them into U+FFFD.
            let replaceCases =
                [ ("aaa", "aa", "b"), "ba"
                  ("aaaa", "aa", "b"), "bb"
                  ("abab", "ab", "abab"), "abababab"
                  ("a,b,,c", ",", ""), "abc"
                  ("x", "x", ""), ""
                  ("hello", "xyz", "!"), "hello"
                  ("", "a", "b"), ""
                  ("ABC", "b", "x"), "ABC"
                  ("日本語日本", "日本", "に"), "に語に"
                  ("a😀b😀c", "😀", "-"), "a-b-c"
                  ("a😀b", "b", "😀"), "a😀😀"
                  // A pattern that is half of a surrogate pair never matches inside the pair.
                  ("😀", utf16 "{DE00}", "x"), "😀"
                  ("😀", utf16 "{D83D}", "x"), "😀"
                  (utf16 "😀{DE00}", utf16 "{DE00}", "x"), "😀x"
                  (utf16 "a{D800}b", utf16 "{D800}", "-"), "a-b"
                  // An empty pattern returns the value unchanged; Python inserts the replacement
                  // between every code point ("xaxbxcx", and "x" for "").
                  ("abc", "", "x"), "abc"
                  ("", "", "x"), "" ]
            create "replaceTable" (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]))
                (ExpressionRecipe.ListConstruct(
                    stringType,
                    replaceCases |> List.map (fun ((value, pattern, replacement), _) -> ExpressionRecipe.StringReplace(text value, text pattern, text replacement))
                ))
            // Each White_Space code point is removed from its side; U+001C-U+001F (Python
            // str.isspace), U+200B and U+FEFF are not White_Space and are kept.
            let trimCases =
                [ for space in whiteSpace do
                      yield (space + "a" + space), ("a", "a" + space, space + "a")
                  for kept in [ "\u001C"; "\u001F"; "​"; "﻿" ] do
                      yield (kept + "a" + kept), (kept + "a" + kept, kept + "a" + kept, kept + "a" + kept)
                  yield "", ("", "", "")
                  yield " \t　\n\u0085 ", ("", "", "")
                  yield "  a b  ", ("a b", "a b  ", "  a b")
                  yield " 😀　😀 ", ("😀　😀", "😀　😀 ", " 😀　😀")
                  yield "abc", ("abc", "abc", "abc") ]
            let trimTable name (build: ExpressionRecipe -> ExpressionRecipe) =
                create name (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]))
                    (ExpressionRecipe.ListConstruct(stringType, trimCases |> List.map (fst >> text >> build)))
            trimTable "trimTable" ExpressionRecipe.StringTrim
            trimTable "trimStartTable" ExpressionRecipe.StringTrimStart
            trimTable "trimEndTable" ExpressionRecipe.StringTrimEnd
            // An unpaired surrogate is not white space and stays: one code point remains.
            create "loneSurrogate" intType (ExpressionRecipe.StringLength(ExpressionRecipe.StringTrim(text (utf16 " {D800} "))))
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            check "replaceTable" (PureRunValue.StringList(replaceCases |> List.map snd))
            check "trimTable" (PureRunValue.StringList(trimCases |> List.map (fun (_, (both, _, _)) -> both)))
            check "trimStartTable" (PureRunValue.StringList(trimCases |> List.map (fun (_, (_, start, _)) -> start)))
            check "trimEndTable" (PureRunValue.StringList(trimCases |> List.map (fun (_, (_, _, finish)) -> finish)))
            check "loneSurrogate" (PureRunValue.Int64 1L)
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
