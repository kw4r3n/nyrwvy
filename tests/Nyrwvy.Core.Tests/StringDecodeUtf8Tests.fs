namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
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

    [<Fact>]
    let ``StringDecodeUtf8Lossy type checks, persists, and lowers through every layer`` () =
        let parameterId = id<ParameterTag> "bytes"

        let snapshotWith parameterType resultType =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "decode"
                  DisplayName = "decode"
                  Exported = true
                  TypeParameters = []
                  Parameters = [ { Id = parameterId; DisplayName = "bytes"; Type = parameterType } ]
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body =
                    GraphBuilder.build
                        (id<GraphNodeTag>)
                        (GraphBuilder.node (fun ids -> SemanticNodeKind.StringDecodeUtf8Lossy ids[0]) [ GraphBuilder.param parameterId ]) }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        Assert.Contains(
            SnapshotValidator.validate (snapshotWith intType stringType),
            fun issue -> issue.Code = "StringIntrinsicTypeMismatch"
        )
        Assert.Contains(
            SnapshotValidator.validate (snapshotWith stringType intType),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let committed = snapshotWith stringType stringType
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", ExecutionIrSerializer.serialize execution)
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
