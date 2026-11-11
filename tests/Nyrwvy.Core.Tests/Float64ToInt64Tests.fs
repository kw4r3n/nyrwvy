namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Float64RoundToInt64 and Float64FloorToInt64 (post-M2 order 34; rounding half away from zero since
/// order 43) and floor as Option<Int64>, None for NaN, the infinities and results outside Int64. Never fail.
module Float64ToInt64Tests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("float64-to-int64-" + name)
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Theory>]
    [<InlineData("round", "float64RoundToInt64")>]
    [<InlineData("floor", "float64FloorToInt64")>]
    let ``Float64 to Int64 conversions type check, persist, and lower through every layer``
        (mode: string, wireKind: string) =
        let kindOf (ids: GraphNodeId list) =
            if mode = "round" then SemanticNodeKind.Float64RoundToInt64 ids[0] else SemanticNodeKind.Float64FloorToInt64 ids[0]
        let buildSnapshot parameterType resultType =
            let functionId = id<FunctionTag> ("convert-" + mode)
            let parameterId = id<ParameterTag> ("value-" + mode)
            let body =
                GraphBuilder.build (id<GraphNodeTag>)
                    (GraphBuilder.node kindOf [ GraphBuilder.param parameterId ])
            let fn: SemanticFunction =
                { Id = functionId
                  DisplayName = mode
                  Exported = true
                  MustNotWait = false
                  TypeParameters = []
                  Parameters = [ { Id = parameterId; DisplayName = "value"; Type = parameterType } ]
                  ResultType = resultType
                  RequiredEffects = []
                  Body = body }
            { Snapshot.empty (id<SnapshotTag> ("base-" + mode)) with Functions = [ fn ] }
        Assert.NotEmpty(SnapshotValidator.validate (buildSnapshot intType (optionOf intType)))
        Assert.Contains(SnapshotValidator.validate (buildSnapshot floatType intType), fun issue -> issue.Code = "FunctionResultTypeMismatch")
        let snapshot = buildSnapshot floatType (optionOf intType)
        Assert.Empty(SnapshotValidator.validate snapshot)
        let json = SnapshotCodec.serialize snapshot |> unwrap "serialize"
        Assert.Contains("\"" + wireKind + "\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"" + wireKind + "\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"" + wireKind + "\"", ExecutionIrSerializer.serialize execution)
        let parameterId = snapshot.Functions.Head.Parameters.Head.Id
        let broken =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference reference when reference = parameterId ->
                                        { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "Float64OperandTypeMismatch")
