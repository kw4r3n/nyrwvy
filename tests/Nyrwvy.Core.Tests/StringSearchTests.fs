namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringIndexOf, StringLastIndexOf, StringStartsWith, StringEndsWith and StringSplit: ordinal
/// search in code-point positions that never fails and never splits a surrogate pair.
module StringSearchTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-search-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value
    let private int value = ExpressionRecipe.Int64Literal value

    [<Fact>]
    let ``string search builtins type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "search"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string search was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] (optionOf intType) (ExpressionRecipe.StringIndexOf(slot 0, slot 1, int 0L))))
        Assert.Equal(mismatch, rejects (request [ stringType; stringType ] (optionOf intType) (ExpressionRecipe.StringIndexOf(slot 0, slot 1, slot 1))))
        Assert.Equal(mismatch, rejects (request [ intType ] (optionOf intType) (ExpressionRecipe.StringLastIndexOf(slot 0, text "a"))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] boolType (ExpressionRecipe.StringStartsWith(slot 0, slot 1))))
        Assert.Equal(mismatch, rejects (request [ intType ] boolType (ExpressionRecipe.StringEndsWith(slot 0, text "a"))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] (listOf stringType) (ExpressionRecipe.StringSplit(slot 0, slot 1))))
        // Result types: Option<Int64>, Bool and List<String>, not String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringIndexOf(slot 0, text "a", int 0L))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringSplit(slot 0, text ","))))
        let cases =
            [ "stringIndexOf", "string-index-of", "NyText.IndexOf(", optionOf intType, ExpressionRecipe.StringIndexOf(slot 0, slot 1, int 2L)
              "stringLastIndexOf", "string-last-index-of", "NyText.LastIndexOf(", optionOf intType, ExpressionRecipe.StringLastIndexOf(slot 0, slot 1)
              "stringStartsWith", "string-starts-with", "NyText.StartsWith(", boolType, ExpressionRecipe.StringStartsWith(slot 0, slot 1)
              "stringEndsWith", "string-ends-with", "NyText.EndsWith(", boolType, ExpressionRecipe.StringEndsWith(slot 0, slot 1)
              "stringSplit", "string-split", "NyText.Split(", listOf stringType, ExpressionRecipe.StringSplit(slot 0, slot 1) ]
        for kind, nyasonTag, emitted, resultType, body in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ stringType; stringType ] resultType body)
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
            Assert.Contains($"\"{kind}\"", TypedSemanticIrCodec.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains($"\"{kind}\"", ExecutionIrCodec.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            Assert.Contains(emitted, artifact.Source)
            // A persisted node over a non-String operand is a validation error.
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
    let ``JSONL-authored string search matches Python in code points and stays linear`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-search-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let generic name argument = sprintf """{"kind":"builtinGeneric","generic":"%s","arguments":[%s]}""" name argument
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
            author "indexOf" [ "text", str; "sub", str; "from", primitive "int64" ] (generic "option" (primitive "int64"))
                (sprintf """{"kind":"stringIndexOf","value":%s,"substring":%s,"from":%s}""" (parameter "text") (parameter "sub") (parameter "from"))
            author "lastIndexOf" [ "text", str; "sub", str ] (generic "option" (primitive "int64"))
                (sprintf """{"kind":"stringLastIndexOf","value":%s,"substring":%s}""" (parameter "text") (parameter "sub"))
            author "startsWith" [ "text", str; "prefix", str ] (primitive "bool")
                (sprintf """{"kind":"stringStartsWith","value":%s,"prefix":%s}""" (parameter "text") (parameter "prefix"))
            author "endsWith" [ "text", str; "suffix", str ] (primitive "bool")
                (sprintf """{"kind":"stringEndsWith","value":%s,"suffix":%s}""" (parameter "text") (parameter "suffix"))
            author "split" [ "text", str; "separator", str ] (generic "list" str)
                (sprintf """{"kind":"stringSplit","value":%s,"separator":%s}""" (parameter "text") (parameter "separator"))
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            Assert.Equal(PureRunValue.Int64Option(Some 3L), run "indexOf" [ PureRunValue.String "a😀b😀c"; PureRunValue.String "😀"; PureRunValue.Int64 2L ])
            Assert.Equal(PureRunValue.Int64Option(Some 3L), run "lastIndexOf" [ PureRunValue.String "a😀b😀c"; PureRunValue.String "😀" ])
            Assert.Equal(PureRunValue.Bool true, run "startsWith" [ PureRunValue.String "😀x"; PureRunValue.String "😀" ])
            Assert.Equal(PureRunValue.Bool false, run "endsWith" [ PureRunValue.String "hello"; PureRunValue.String "LO" ])
            Assert.Equal(PureRunValue.StringList [ "a"; "b"; ""; "c" ], run "split" [ PureRunValue.String "a,b,,c"; PureRunValue.String "," ])
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
            let found = PureRunValue.Int64Option
            // Python parity: str.find(sub, start) for 0 <= start <= len, str.rfind, startswith,
            // endswith and split(sep), with -1 read as None. Lone surrogates are built with utf16,
            // since F# literals turn them into U+FFFD.
            let indexCases =
                [ ("hello world", "o", 0L), Some 4L
                  ("hello world", "o", 5L), Some 7L
                  ("hello world", "o", 8L), None
                  ("hello", "", 2L), Some 2L
                  ("hello", "", 5L), Some 5L
                  ("", "", 0L), Some 0L
                  ("", "a", 0L), None
                  ("a😀b😀c", "😀", 0L), Some 1L
                  ("a😀b😀c", "😀", 2L), Some 3L
                  ("a😀b😀c", "b😀", 0L), Some 2L
                  ("日本語日本", "本", 2L), Some 4L
                  ("aaaa", "aa", 1L), Some 1L
                  ("abc", "abcd", 0L), None
                  ("ABC", "b", 0L), None
                  ("é", "́", 0L), Some 1L
                  // Lone surrogates: a match never starts or ends inside a surrogate pair.
                  (utf16 "😀{DE00}", utf16 "{DE00}", 0L), Some 1L
                  ("😀", utf16 "{D83D}", 0L), None
                  (utf16 "a{D800}b", "b", 0L), Some 2L
                  // Clamped `from`, where Python differs (negative counts from the end; past the end is -1).
                  ("hello", "h", -2L), Some 0L
                  ("hello", "", 9L), Some 5L ]
            create "indexTable" (listOf (optionOf intType))
                (ExpressionRecipe.ListConstruct(optionOf intType, indexCases |> List.map (fun ((value, sub, from), _) -> ExpressionRecipe.StringIndexOf(text value, text sub, int from))))
            let lastCases =
                [ ("hello world", "o"), Some 7L
                  ("hello", ""), Some 5L
                  ("", ""), Some 0L
                  ("a😀b😀c", "😀"), Some 3L
                  ("aaaa", "aa"), Some 2L
                  ("abc", "x"), None
                  ("😀😀", "😀😀"), Some 0L
                  (utf16 "{DE00}😀", utf16 "{DE00}"), Some 0L
                  ("😀", utf16 "{DE00}"), None ]
            create "lastTable" (listOf (optionOf intType))
                (ExpressionRecipe.ListConstruct(optionOf intType, lastCases |> List.map (fun ((value, sub), _) -> ExpressionRecipe.StringLastIndexOf(text value, text sub))))
            let affixCases =
                [ ExpressionRecipe.StringStartsWith(text "hello", text "he"), true
                  ExpressionRecipe.StringStartsWith(text "hello", text ""), true
                  ExpressionRecipe.StringStartsWith(text "", text ""), true
                  ExpressionRecipe.StringStartsWith(text "", text "a"), false
                  ExpressionRecipe.StringStartsWith(text "😀x", text "😀"), true
                  ExpressionRecipe.StringStartsWith(text "hello", text "Hello"), false
                  ExpressionRecipe.StringStartsWith(text "he", text "hello"), false
                  ExpressionRecipe.StringStartsWith(text "😀", text (utf16 "{D83D}")), false
                  ExpressionRecipe.StringStartsWith(text (utf16 "{D800}x"), text (utf16 "{D800}")), true
                  ExpressionRecipe.StringEndsWith(text "hello", text "lo"), true
                  ExpressionRecipe.StringEndsWith(text "hello", text ""), true
                  ExpressionRecipe.StringEndsWith(text "", text ""), true
                  ExpressionRecipe.StringEndsWith(text "", text "a"), false
                  ExpressionRecipe.StringEndsWith(text "x😀", text "😀"), true
                  ExpressionRecipe.StringEndsWith(text "hello", text "LO"), false
                  ExpressionRecipe.StringEndsWith(text "lo", text "hello"), false
                  ExpressionRecipe.StringEndsWith(text "😀", text (utf16 "{DE00}")), false ]
            create "affixTable" (listOf boolType) (ExpressionRecipe.ListConstruct(boolType, affixCases |> List.map fst))
            let splitCases =
                [ ("a,b,,c", ","), [ "a"; "b"; ""; "c" ]
                  ("", ","), [ "" ]
                  (",", ","), [ ""; "" ]
                  ("abc", ","), [ "abc" ]
                  ("a::b::", "::"), [ "a"; "b"; "" ]
                  ("aaa", "aa"), [ ""; "a" ]
                  ("😀x😀", "x"), [ "😀"; "😀" ]
                  ("a😀b😀c", "😀"), [ "a"; "b"; "c" ]
                  ("a😀b", utf16 "{DE00}"), [ "a😀b" ]
                  (utf16 "x{D800}y", utf16 "{D800}"), [ "x"; "y" ]
                  // An empty separator, where Python raises ValueError, is the whole text.
                  ("abc", ""), [ "abc" ] ]
            create "splitTable" (listOf (listOf stringType))
                (ExpressionRecipe.ListConstruct(listOf stringType, splitCases |> List.map (fun ((value, separator), _) -> ExpressionRecipe.StringSplit(text value, text separator))))
            let expectList itemType items = PureRunValue.ListValue(itemType, items)
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            check "indexTable" (expectList (optionOf intType) (indexCases |> List.map (snd >> found)))
            check "lastTable" (expectList (optionOf intType) (lastCases |> List.map (snd >> found)))
            check "affixTable" (PureRunValue.BoolList(affixCases |> List.map snd))
            check "splitTable" (expectList (listOf stringType) (splitCases |> List.map (snd >> PureRunValue.StringList)))
            // Long text: one linear pass per call, far past the ~7.7K characters of probe 4.
            let slot = ExpressionRecipe.ParameterSlot
            let createOne name resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      DisplayName = name
                      Parameters = [ { DisplayName = "text"; Type = stringType } ]
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            createOne "pieces" intType (ExpressionRecipe.ListLength(ExpressionRecipe.StringSplit(slot 0, text "😀")))
            let long = String.replicate 15000 "ab😀" + "z"
            Assert.Equal(PureRunValue.Int64 15001L, run "pieces" [ PureRunValue.String long ])
            Assert.Equal(PureRunValue.Int64Option(Some 45000L), run "indexOf" [ PureRunValue.String long; PureRunValue.String "z"; PureRunValue.Int64 0L ])
            Assert.Equal(PureRunValue.Int64Option(Some 44999L), run "lastIndexOf" [ PureRunValue.String long; PureRunValue.String "😀z" ])
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
