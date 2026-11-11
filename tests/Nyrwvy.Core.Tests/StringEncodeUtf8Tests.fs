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
        let parameterId = id<ParameterTag> "text"

        let snapshotWith parameterType resultType =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "encode"
                  DisplayName = "encode"
                  Exported = true
                  TypeParameters = []
                  Parameters = [ { Id = parameterId; DisplayName = "text"; Type = parameterType } ]
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body =
                    GraphBuilder.build
                        (id<GraphNodeTag>)
                        (GraphBuilder.node
                            (fun ids -> SemanticNodeKind.StringEncodeUtf8 ids[0])
                            [ GraphBuilder.param parameterId ]) }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        let issues parameterType resultType =
            SnapshotValidator.validate (snapshotWith parameterType resultType)

        Assert.Contains(issues intType stringType, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
        Assert.Contains(issues stringType intType, fun issue -> issue.Code = "FunctionResultTypeMismatch")
        let committed = snapshotWith stringType stringType
        Assert.Empty(SnapshotValidator.validate committed)
        Assert.Equal<Set<SnapshotSchemaFeature>>(Set.singleton SnapshotSchemaFeature.StringEncodeUtf8, SnapshotSchema.usedFeatures committed)
        let json = SnapshotCodec.serializeCanonical committed |> unwrap "serialize"
        Assert.Contains("\"stringEncodeUtf8\"", json)
        Assert.Contains("\"schemaVersion\":19", json)
        let decoded = SnapshotCodec.deserialize (SnapshotCodec.serialize committed |> unwrap "serialize") |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringEncodeUtf8\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringEncodeUtf8\"", ExecutionIrSerializer.serialize execution)
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
