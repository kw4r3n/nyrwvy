namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringJoin (Python `sep.join(list)`) and ListSlice (generic List<T> slice clamped like
/// StringSlice), both pure and never failing.
module StringJoinListSliceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("join-slice-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``StringJoin and ListSlice type check, persist, and lower through every layer`` () =
        let parameterId index = id<ParameterTag> $"input{index}"
        let slot index = GraphBuilder.param (parameterId index)
        let int value = GraphBuilder.leaf (SemanticNodeKind.Int64Literal value)

        let snapshotWith (parameterTypes: TypeRef list) resultType body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "joinSlice"
                  DisplayName = "joinSlice"
                  Exported = true
                  TypeParameters = []
                  Parameters = parameterTypes |> List.mapi (fun index t -> { Id = parameterId index; DisplayName = $"input{index}"; Type = t })
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body = GraphBuilder.build (id<GraphNodeTag>) body }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        let issues parameterTypes resultType body = SnapshotValidator.validate (snapshotWith parameterTypes resultType body)
        let binary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1]))
        let ternary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1], ids[2]))
        let join = binary SemanticNodeKind.StringJoin [ slot 0; slot 1 ]
        // A mistyped operand in any position is a validation error (the code depends on the position).
        Assert.NotEmpty(issues [ listOf intType; stringType ] stringType join)
        Assert.NotEmpty(issues [ stringType; stringType ] stringType join)
        Assert.NotEmpty(issues [ listOf stringType; intType ] stringType join)
        Assert.NotEmpty(issues [ listOf (optionOf stringType); stringType ] stringType join)
        // The result is a String, not a List<String>.
        Assert.Contains(
            issues [ listOf stringType; stringType ] (listOf stringType) join,
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let slice = ternary SemanticNodeKind.ListSlice [ slot 0; slot 1; slot 2 ]
        Assert.NotEmpty(issues [ stringType; intType; intType ] stringType slice)
        Assert.NotEmpty(issues [ listOf intType; stringType; intType ] (listOf intType) slice)
        Assert.NotEmpty(issues [ listOf intType; intType; stringType ] (listOf intType) slice)
        // The result keeps the input's item type; it is neither Option<T> nor another List type.
        Assert.Contains(
            issues [ listOf intType; intType; intType ] (optionOf intType) slice,
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        Assert.Contains(
            issues [ listOf intType; intType; intType ] (listOf stringType) slice,
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        Assert.Empty(issues [ listOf (optionOf intType); intType; intType ] (listOf (optionOf intType)) slice)
        Assert.Empty(
            issues
                []
                (listOf stringType)
                (ternary SemanticNodeKind.ListSlice [ GraphBuilder.leaf (SemanticNodeKind.ListEmpty stringType); int 0L; int 1L ])
        )
        let cases =
            [ "stringJoin", [ listOf stringType; stringType ], stringType, join, "StringIntrinsicTypeMismatch"
              "listSlice", [ listOf (listOf stringType); intType; intType ], listOf (listOf stringType), slice, "ListSliceListTypeMismatch" ]
        for kind, parameters, resultType, body, brokenCode in cases do
            let committed = snapshotWith parameters resultType body
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
            // A persisted node whose list input is not the required list is a validation error.
            let broken =
                { committed with
                    Functions = committed.Functions |> List.map (fun fn ->
                        { fn with
                            Body =
                                { fn.Body with
                                    Nodes = fn.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference reference when reference = parameterId 0 ->
                                            { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = brokenCode)
