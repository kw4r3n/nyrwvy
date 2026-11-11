namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringCodePoints: each code point of a String as a one-code-point String, in order.
module StringCodePointsTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-code-points-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``code points type check, persist, and lower through every layer`` () =
        let parameterId = id<ParameterTag> "input0"

        let snapshotWith parameterType resultType =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "points"
                  DisplayName = "points"
                  Exported = true
                  TypeParameters = []
                  Parameters = [ { Id = parameterId; DisplayName = "input0"; Type = parameterType } ]
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body =
                    GraphBuilder.build
                        (id<GraphNodeTag>)
                        (GraphBuilder.node (fun ids -> SemanticNodeKind.StringCodePoints ids[0]) [ GraphBuilder.param parameterId ]) }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        Assert.Contains(
            SnapshotValidator.validate (snapshotWith intType (listOf stringType)),
            fun issue -> issue.Code = "StringIntrinsicTypeMismatch"
        )
        // The result is List<String>, not String.
        Assert.Contains(
            SnapshotValidator.validate (snapshotWith stringType stringType),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let committed = snapshotWith stringType (listOf stringType)
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringCodePoints\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringCodePoints\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringCodePoints\"", ExecutionIrSerializer.serialize execution)
        // A persisted node over a non-String value is a validation error.
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
