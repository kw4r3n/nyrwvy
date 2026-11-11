namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Standard operations whose meaning is fixed with no choice to make (post-M2 orders 35 and 36):
/// ListReverse, and the IEEE 754 Float64 -> Float64 operations Sqrt, Abs, Negate, Floor, Ceiling and
/// Truncate. None of them fails.
module StandardOperationTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("standard-operations-" + name)
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private kindOf mode : GraphNodeId -> SemanticNodeKind =
        match mode with
        | "sqrt" -> SemanticNodeKind.Float64Sqrt
        | "abs" -> SemanticNodeKind.Float64Abs
        | "negate" -> SemanticNodeKind.Float64Negate
        | "floor" -> SemanticNodeKind.Float64Floor
        | "ceiling" -> SemanticNodeKind.Float64Ceiling
        | "truncate" -> SemanticNodeKind.Float64Truncate
        | "reverse" -> SemanticNodeKind.ListReverse
        | "dropLast" -> SemanticNodeKind.ListDropLast
        | "last" -> SemanticNodeKind.ListLast
        | other -> failwithf "Unknown mode %s" other

    [<Theory>]
    [<InlineData("sqrt", "float64Sqrt")>]
    [<InlineData("abs", "float64Abs")>]
    [<InlineData("negate", "float64Negate")>]
    [<InlineData("floor", "float64Floor")>]
    [<InlineData("ceiling", "float64Ceiling")>]
    [<InlineData("truncate", "float64Truncate")>]
    [<InlineData("reverse", "listReverse")>]
    [<InlineData("dropLast", "listDropLast")>]
    [<InlineData("last", "listLast")>]
    let ``Standard operations type check, persist, and lower through every layer``
        (mode: string, wireKind: string) =
        // ListReverse keeps its List<T> type, so a List<String> stands in for every item type.
        let valueType, wrongType, issueCode =
            if mode = "reverse" then listOf textType, textType, "ListReverseInputTypeMismatch"
            elif mode = "dropLast" then listOf textType, textType, "ListDropLastInputTypeMismatch"
            elif mode = "last" then listOf textType, textType, "ListLastInputTypeMismatch"
            else floatType, intType, "Float64OperandTypeMismatch"
        // ListLast gives Option<T>; every other operation keeps its operand type.
        let resultType = if mode = "last" then TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ textType ]) else valueType
        let parameterId = id<ParameterTag> ("value-" + mode)

        let snapshotWith parameterType declaredResultType =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> ("fn-" + mode)
                  DisplayName = mode
                  Exported = true
                  TypeParameters = []
                  Parameters = [ { Id = parameterId; DisplayName = "value"; Type = parameterType } ]
                  ResultType = declaredResultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body =
                    GraphBuilder.build
                        (id<GraphNodeTag>)
                        (GraphBuilder.node (fun ids -> kindOf mode ids[0]) [ GraphBuilder.param parameterId ]) }

            { Snapshot.empty (id<SnapshotTag> ("base-" + mode)) with Functions = [ fn ] }

        Assert.Contains(SnapshotValidator.validate (snapshotWith wrongType resultType), fun issue -> issue.Code = issueCode)
        Assert.Contains(
            SnapshotValidator.validate (snapshotWith valueType wrongType),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let committed = snapshotWith valueType resultType
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"" + wireKind + "\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"" + wireKind + "\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"" + wireKind + "\"", ExecutionIrSerializer.serialize execution)
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
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = issueCode)

    let private commit (call: string -> JsonElement) (proposal: JsonElement) =
        call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposal.GetProperty("proposalId").GetString() + "\"}") |> ignore
