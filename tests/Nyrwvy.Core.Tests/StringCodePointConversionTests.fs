namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringFromCodePoint and StringToCodePoint: Python chr and ord as Options, pure and never failing,
/// with surrogate codes (U+D800..U+DFFF) giving None, since a String holds only scalar values.
module StringCodePointConversionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-code-point-" + name)
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
    let private someValue = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
    /// The String inside StringFromCodePoint(code), or "" for None.
    let private fromOrEmpty code =
        ExpressionRecipe.MatchOption(ExpressionRecipe.StringFromCodePoint code, someValue, text "")

    [<Fact>]
    let ``StringFromCodePoint and StringToCodePoint type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "codePoint"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string builtin was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ stringType ] (optionOf stringType) (ExpressionRecipe.StringFromCodePoint(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] (optionOf intType) (ExpressionRecipe.StringToCodePoint(slot 0))))
        // Result types: Option<String> and Option<Int64>, not the bare String or Int64.
        Assert.NotEqual<string>("", rejects (request [ intType ] stringType (ExpressionRecipe.StringFromCodePoint(slot 0))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringToCodePoint(slot 0))))
        let cases =
            [ "stringFromCodePoint", "NyText.FromCodePoint(", intType, optionOf stringType, ExpressionRecipe.StringFromCodePoint(slot 0), SemanticNodeKind.StringLiteral "x"
              "stringToCodePoint", "NyText.ToCodePoint(", stringType, optionOf intType, ExpressionRecipe.StringToCodePoint(slot 0), SemanticNodeKind.Int64Literal 1L ]
        for kind, emitted, parameterType, resultType, body, wrongOperand in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ parameterType ] resultType body)
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
            // A persisted node over an operand of the other type is a validation error.
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
                                            { node with Kind = wrongOperand }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored StringFromCodePoint and StringToCodePoint match Python chr and ord`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-code-point-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let author name parameterType resultType kind =
                let line =
                    sprintf
                        """{"id":"%s","op":"simulateCreateFunctionByName","exported":true,"displayName":"%s","parameters":[{"displayName":"input","type":{"kind":"primitive","primitive":"%s"}}],"resultType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"%s"}]},"requiredEffects":[],"body":{"kind":"%s","value":{"kind":"parameter","name":"input"}}}"""
                        name name parameterType resultType kind
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            author "chr" "int64" "string" "stringFromCodePoint"
            author "ord" "string" "int64" "stringToCodePoint"
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            Assert.Equal(PureRunValue.StringOption(Some "A"), run "chr" [ PureRunValue.Int64 65L ])
            Assert.Equal(PureRunValue.StringOption(Some "😀"), run "chr" [ PureRunValue.Int64 0x1F600L ])
            Assert.Equal(PureRunValue.StringOption None, run "chr" [ PureRunValue.Int64 -1L ])
            Assert.Equal(PureRunValue.Int64Option(Some 0x1F600L), run "ord" [ PureRunValue.String "😀" ])
            Assert.Equal(PureRunValue.Int64Option None, run "ord" [ PureRunValue.String "" ])
            let create name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = parameters
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            // Python parity: chr(code) as an Option, None where Python raises ValueError, and None for
            // a surrogate code, where Python returns a lone surrogate.
            let fromCases =
                [ 0L, Some "\u0000"
                  65L, Some "A"
                  0x7FL, Some "\u007F"
                  0xE9L, Some "é"
                  0x20ACL, Some "€"
                  0xD7FFL, Some "\uD7FF"
                  0xD800L, None
                  0xDBFFL, None
                  0xDC00L, None
                  0xDFFFL, None
                  0xE000L, Some "\uE000"
                  0xFFFDL, Some "\uFFFD"
                  0x10000L, Some "\U00010000"
                  0x1F600L, Some "😀"
                  0x10FFFFL, Some "\U0010FFFF"
                  -1L, None
                  0x110000L, None
                  0x100000041L, None // not truncated to 32 bits
                  Int64.MinValue, None
                  Int64.MaxValue, None ]
            create "fromTable" [] (listOf (optionOf stringType))
                (ExpressionRecipe.ListConstruct(optionOf stringType, fromCases |> List.map (fst >> int >> ExpressionRecipe.StringFromCodePoint)))
            // Python parity: ord(text) as an Option, None where Python raises TypeError.
            let toCases =
                [ text "A", Some 65L
                  text "\u0000", Some 0L
                  text "é", Some 0xE9L
                  text "€", Some 0x20ACL
                  text "\uFFFF", Some 0xFFFFL
                  text "😀", Some 0x1F600L
                  text "\U0010FFFF", Some 0x10FFFFL
                  text "", None
                  text "ab", None
                  text "e\u0301", None // two code points, not normalized
                  text "😀a", None ]
            create "toTable" [] (listOf (optionOf intType))
                (ExpressionRecipe.ListConstruct(optionOf intType, toCases |> List.map (fst >> ExpressionRecipe.StringToCodePoint)))
            // Round trip StringToCodePoint(StringFromCodePoint(n)) = Some n for every scalar value at
            // the UTF-8 and UTF-16 boundaries; surrogate codes give None (fromTable).
            let roundTripCodes =
                [ 0L; 0x7FL; 0x80L; 0x7FFL; 0x800L; 0xD7FFL; 0xE000L; 0xFFFFL; 0x10000L; 0x10FFFFL ]
            create "roundTripTable" [] (listOf (optionOf intType))
                (ExpressionRecipe.ListConstruct(optionOf intType, roundTripCodes |> List.map (int >> fromOrEmpty >> ExpressionRecipe.StringToCodePoint)))
            // Every item c of StringCodePoints(t) comes back from StringFromCodePoint(StringToCodePoint(c)).
            let orEmptyString input = ExpressionRecipe.MatchOption(input, someValue, text "")
            create "roundTripItem" [ { DisplayName = "c"; Type = stringType } ] stringType
                (ExpressionRecipe.MatchOption(
                    ExpressionRecipe.StringToCodePoint(ExpressionRecipe.ParameterSlot 0),
                    orEmptyString (ExpressionRecipe.StringFromCodePoint someValue),
                    text "?"))
            // Five code points, two of them astral (😀 and U+10FFFF), joined by a concatenation.
            let mixed () = ExpressionRecipe.StringConcat(text "a😀b", text "é\U0010FFFF")
            create "codePointsRoundTrip" [] boolType
                (ExpressionRecipe.BoolAnd(
                    ExpressionRecipe.Int64Equal(ExpressionRecipe.StringLength(mixed ()), int 5L),
                    ExpressionRecipe.StringEqual(
                        ExpressionRecipe.StringJoin(ExpressionRecipe.ListMap(ExpressionRecipe.StringCodePoints(mixed ()), functionId "roundTripItem", []), text ""),
                        mixed ())))
            let expectList itemType items = PureRunValue.ListValue(itemType, items)
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            check "fromTable" (expectList (optionOf stringType) (fromCases |> List.map (snd >> PureRunValue.StringOption)))
            check "toTable" (expectList (optionOf intType) (toCases |> List.map (snd >> PureRunValue.Int64Option)))
            check "roundTripTable" (expectList (optionOf intType) (roundTripCodes |> List.map (Some >> PureRunValue.Int64Option)))
            check "codePointsRoundTrip" (PureRunValue.Bool true)
            Assert.Equal(PureRunValue.String "😀", run "roundTripItem" [ PureRunValue.String "😀" ])
            Assert.Equal(PureRunValue.String "?", run "roundTripItem" [ PureRunValue.String "ab" ])
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
