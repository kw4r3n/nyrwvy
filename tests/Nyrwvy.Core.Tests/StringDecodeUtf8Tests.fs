namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringDecodeUtf8Lossy (post-M2 order 33): bytes held one per code point decoded as UTF-8 with one
/// U+FFFD per maximal ill-formed subpart, as Python bytes.decode("utf-8", "replace"). Never fails.
module StringDecodeUtf8Tests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-decode-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    [<Fact>]
    let ``StringDecodeUtf8Lossy type checks, persists, and lowers through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "decode"
              Parameters = [ { DisplayName = "bytes"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped decode was accepted: %A" other
        let body = ExpressionRecipe.StringDecodeUtf8Lossy(ExpressionRecipe.ParameterSlot 0)
        Assert.Equal("PureExpressionOperandTypeMismatch", rejects (request intType stringType body))
        Assert.NotEqual<string>("", rejects (request stringType intType body))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request stringType stringType body) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let nyason = NyasonCodec.serialize decoded |> unwrap "nyason"
        Assert.Contains("string-decode-utf8-lossy", nyason)
        let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains("NyText.DecodeUtf8Lossy(", artifact.Source)
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
    let ``JSONL-authored StringDecodeUtf8Lossy matches Python's replace decoding`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-decode-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            use proposed =
                AuthoringProtocol.executeLine
                    session
                    """{"id":"decode","op":"simulateCreateFunctionByName","exported":true,"displayName":"decode","parameters":[{"displayName":"bytes","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"kind":"stringDecodeUtf8Lossy","value":{"kind":"parameter","name":"bytes"}}}"""
                |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            session.Commit(proposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            // Each input is a byte string written one code point per byte; each expected value is
            // Python's bytes.decode("utf-8", "replace") of those bytes.
            let cases =
                [ "", ""
                  "abc", "abc"
                  "Ã©tÃ©", "été"
                  "æ\u0097¥æ\u009C¬", "日本"
                  "ð\u009F\u0098\u0080", "\U0001F600"
                  // Overlong forms: C0 and C1 never start a sequence; E0 and F0 need A0 / 90 next.
                  "À\u0080", "��"
                  "Á¿", "��"
                  "à\u0080\u0080", "���"
                  "à \u0080", "ࠀ"
                  // Encoded surrogates: ED takes only 80..9F next.
                  "í \u0080", "���"
                  "í\u009F¿", "퟿"
                  "ð\u0080\u0080\u0080", "����"
                  "ð\u0090\u0080\u0080", "\U00010000"
                  // Above U+10FFFF: F4 takes only 80..8F next, F5..FF never start a sequence.
                  "ô\u008F¿¿", "\U0010FFFF"
                  "ô\u0090\u0080\u0080", "����"
                  "õ\u0080\u0080\u0080", "����"
                  "ÿ", "�"
                  "þÿ", "��"
                  // Lone continuation bytes, one U+FFFD each.
                  "\u0080", "�"
                  "¿\u0080a", "��a"
                  // A sequence cut short is one U+FFFD, at the end or before another byte.
                  "â\u0082", "�"
                  "â\u0082a", "�a"
                  "ð\u009F\u0098", "�"
                  "ð\u009F\u0098a\u0080", "�a�"
                  "â\u0082¬â", "€�"
                  "a\u0000b", "a\u0000b"
                  // A BOM is kept, as with Python's "utf-8" (not "utf-8-sig").
                  "ï»¿x", "﻿x"
                  // Not bytes: a code point above U+00FF, a surrogate pair included, reads as 0xFF.
                  "a€b", "a�b"
                  "â\U0001F600", "��" ]
            let decode = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "decode")
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                  TypeParameters = None
                  DisplayName = "table"
                  Parameters = []
                  ResultType = listOf stringType
                  RequiredEffects = []
                  Body =
                    ExpressionRecipe.ListConstruct(
                        stringType,
                        cases |> List.map (fun (bytes, _) -> ExpressionRecipe.DirectCall(decode.Id, [ decode.Parameters.Head.Id, text bytes ]))) }
            let table = session.SimulateCreateFunction request |> unwrap "table"
            session.Commit(table.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit table" |> ignore
            let tableId = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "table")).Id
            let result = PureRun.run session tableId [] (Some(PureRunValue.StringList(cases |> List.map snd))) |> unwrap "run"
            Assert.True((result.Matches = Some true), sprintf "%A" result.Actual)
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
