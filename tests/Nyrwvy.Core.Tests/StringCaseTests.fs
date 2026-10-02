namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringToLower and StringUtf8ByteCount: culture-invariant simple lowercase per code point and
/// the UTF-8 byte length, both pure and never failing.
module StringCaseTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-case-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    [<Fact>]
    let ``StringToLower, StringToUpper and StringUtf8ByteCount type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "case"
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
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringToLower(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] intType (ExpressionRecipe.StringUtf8ByteCount(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringToUpper(slot 0))))
        // Result types: String and Int64, not the other way round.
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringToLower(slot 0))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringUtf8ByteCount(slot 0))))
        let cases =
            [ "stringToLower", "string-to-lower", "NyText.ToLower(", stringType, ExpressionRecipe.StringToLower(slot 0)
              "stringToUpper", "string-to-upper", "NyText.ToUpper(", stringType, ExpressionRecipe.StringToUpper(slot 0)
              "stringUtf8ByteCount", "string-utf8-byte-count", "NyText.Utf8ByteCount(", intType, ExpressionRecipe.StringUtf8ByteCount(slot 0) ]
        for kind, nyasonTag, emitted, resultType, body in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ stringType ] resultType body)
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
    let ``JSONL-authored StringToLower, StringToUpper and StringUtf8ByteCount per code point`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-case-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let author name resultType kind =
                let line =
                    sprintf
                        """{"id":"%s","op":"simulateCreateFunctionByName","displayName":"%s","parameters":[{"displayName":"text","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"primitive","primitive":"%s"},"requiredEffects":[],"body":{"kind":"%s","value":{"kind":"parameter","name":"text"}}}"""
                        name name resultType kind
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            author "lower" "string" "stringToLower"
            author "utf8" "int64" "stringUtf8ByteCount"
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            Assert.Equal(PureRunValue.String "hello, world!", run "lower" [ PureRunValue.String "Hello, World!" ])
            Assert.Equal(PureRunValue.Int64 4L, run "utf8" [ PureRunValue.String "😀" ])
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
            // Python parity: str.lower(), except where Python applies a full or context-sensitive
            // mapping (marked). Lone surrogates are built with utf16, since
            // F# literals turn them into U+FFFD; Python keeps them as they are too.
            let lowerCases =
                [ "Hello, World!", "hello, world!"
                  "", ""
                  "ΑΒΓΔ Ωμέγα", "αβγδ ωμέγα"
                  "ПРИВЕТ, Мир", "привет, мир"
                  "I", "i" // never the Turkish dotless ı: culture invariant
                  "K", "k" // KELVIN SIGN
                  "Ω", "ω" // OHM SIGN
                  "ẞ", "ß" // CAPITAL SHARP S
                  "ǅ", "ǆ" // titlecase Dž
                  "ÀÉÎ", "àéî"
                  "ＡＢ", "ａｂ"
                  "😀ABC", "😀abc"
                  "\U00010400", "\U00010428" // DESERET: a supplementary-plane mapping
                  "日本語ABC", "日本語abc"
                  utf16 "A{D800}B{DC00}C", utf16 "a{D800}b{DC00}c"
                  // Python: "i̇stanbul" (i + U+0307, the full mapping); here the simple mapping U+0069.
                  "İstanbul", "istanbul"
                  // Python: "οδος" (Final_Sigma in context); here every Σ maps to σ.
                  "ΟΔΟΣ", "οδοσ" ]
            create "lowerTable" (listOf stringType)
                (ExpressionRecipe.ListConstruct(stringType, lowerCases |> List.map (fst >> text >> ExpressionRecipe.StringToLower)))
            // Simple uppercase per code point, so the code-point length never changes: ß becomes
            // CAPITAL SHARP S (the inverse of StringToLower), where Python's full mapping gives "SS".
            let upperCases =
                [ "Hello, World!", "HELLO, WORLD!"
                  "", ""
                  "αβγδ ωμέγα", "ΑΒΓΔ ΩΜΈΓΑ"
                  "привет, Мир", "ПРИВЕТ, МИР"
                  "i", "I" // never the Turkish dotted İ: culture invariant
                  "ı", "I" // DOTLESS I: UnicodeData maps it to I, which .NET's invariant casing does not
                  "ß", "ẞ" // Python: "SS"
                  "straße", "STRAẞE"
                  "ſ", "S" // LONG S
                  "ǆ", "Ǆ" // Dž lowercase to uppercase, not titlecase
                  "ᾳ", "ᾼ" // simple mapping; Python: "ΑΙ"
                  "ŉ", "ŉ" // no simple mapping; Python: "ʼN"
                  "àéî", "ÀÉÎ"
                  "ａｂ", "ＡＢ"
                  "😀abc", "😀ABC"
                  "\U00010428", "\U00010400" // DESERET
                  "日本語abc", "日本語ABC"
                  utf16 "a{D800}b{DC00}c", utf16 "A{D800}B{DC00}C"
                  "οδος", "ΟΔΟΣ" ]
            create "upperTable" (listOf stringType)
                (ExpressionRecipe.ListConstruct(stringType, upperCases |> List.map (fst >> text >> ExpressionRecipe.StringToUpper)))
            // Python parity: len(text.encode("utf-8")); a lone surrogate, which strict Python cannot
            // encode, counts 3 bytes as with "surrogatepass" (WTF-8) and as its U+FFFD replacement.
            let utf8Cases =
                [ "", 0L
                  "abc", 3L
                  "é", 2L
                  "İ", 2L
                  "ΑΒΓ", 6L
                  "日本語", 9L
                  "😀", 4L
                  "a😀b", 6L
                  utf16 "a{D800}b", 5L
                  utf16 "{DE00}😀", 7L ]
            create "utf8Table" (listOf intType)
                (ExpressionRecipe.ListConstruct(intType, utf8Cases |> List.map (fst >> text >> ExpressionRecipe.StringUtf8ByteCount)))
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            check "lowerTable" (PureRunValue.StringList(lowerCases |> List.map snd))
            check "upperTable" (PureRunValue.StringList(upperCases |> List.map snd))
            check "utf8Table" (PureRunValue.Int64List(utf8Cases |> List.map snd))
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
