namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Xunit

/// StringEncodeUtf8 (post-M2 order 67): the UTF-8 bytes of a String held one per code point
/// (U+0000..U+00FF), as Python text.encode("utf-8"); an unpaired surrogate becomes U+FFFD, the 3
/// bytes StringUtf8ByteCount counts for it. Never fails.
module StringEncodeUtf8Tests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-encode-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    /// Each input with Python's text.encode("utf-8") written one code point per byte; the unpaired
    /// surrogates, where Python raises, encode as U+FFFD (EF BF BD).
    let private cases =
        [ "", ""
          "abc", "abc"
          "été", "Ã©tÃ©"
          "日本", "æ\u0097¥æ\u009C¬"
          "\U0001F600", "ð\u009F\u0098\u0080"
          "\u0080", "Â\u0080"
          "ÿ", "Ã¿"
          "€", "â\u0082¬"
          "￿", "ï¿¿"
          "\u0000", "\u0000"
          "a\u0000b", "a\u0000b"
          // A BOM is encoded like any other code point, as with Python's "utf-8" (not "utf-8-sig").
          "﻿x", "ï»¿x"
          "\U00010000", "ð\u0090\u0080\u0080"
          "\U0010FFFF", "ô\u008F¿¿"
          // Unpaired surrogates: U+FFFD each, 3 bytes, as StringUtf8ByteCount counts them.
          "\uD800", "ï¿½"
          "a\uDC00b", "aï¿½b"
          "\uD83D😀", "ï¿½ð\u009F\u0098\u0080" ]

    [<Fact>]
    let ``StringEncodeUtf8 type checks, persists, and lowers through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "encode"
              Parameters = [ { DisplayName = "text"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped encode was accepted: %A" other
        let body = ExpressionRecipe.StringEncodeUtf8(ExpressionRecipe.ParameterSlot 0)
        Assert.Equal("PureExpressionOperandTypeMismatch", rejects (request intType stringType body))
        Assert.NotEqual<string>("", rejects (request stringType intType body))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request stringType stringType body) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        Assert.Equal<Set<SnapshotSchemaFeature>>(Set.singleton SnapshotSchemaFeature.StringEncodeUtf8, SnapshotSchema.usedFeatures committed)
        let json = SnapshotCodec.serializeCanonical committed |> unwrap "serialize"
        Assert.Contains("\"stringEncodeUtf8\"", json)
        Assert.Contains("\"schemaVersion\":19", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serializeCanonical decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringEncodeUtf8\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringEncodeUtf8\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains("NyText.EncodeUtf8(", artifact.Source)
        match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":19", "\"schemaVersion\":18")) with
        | Ok _ -> failwith "A schema 18 snapshot with StringEncodeUtf8 decoded."
        | Error error -> Assert.Contains("StringEncodeUtf8", sprintf "%A" error)
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
