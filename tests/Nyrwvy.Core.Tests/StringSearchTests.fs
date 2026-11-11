namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringIndexOf, StringLastIndexOf, StringStartsWith, StringEndsWith and StringSplit: ordinal
/// search in code-point positions that never fails and never splits a surrogate pair.
module StringSearchTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-search-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``string search builtins type check, persist, and lower through every layer`` () =
        let parameterId index = id<ParameterTag> $"input{index}"
        let slot index = GraphBuilder.param (parameterId index)
        let text value = GraphBuilder.leaf (SemanticNodeKind.StringLiteral value)
        let int value = GraphBuilder.leaf (SemanticNodeKind.Int64Literal value)

        let snapshotWith (parameterTypes: TypeRef list) resultType body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "search"
                  DisplayName = "search"
                  Exported = true
                  TypeParameters = []
                  Parameters = parameterTypes |> List.mapi (fun index t -> { Id = parameterId index; DisplayName = $"input{index}"; Type = t })
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body = GraphBuilder.build (id<GraphNodeTag>) body }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        let issues parameterTypes resultType body = SnapshotValidator.validate (snapshotWith parameterTypes resultType body)
        let intrinsic parameterTypes resultType body =
            Assert.Contains(issues parameterTypes resultType body, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
        let binary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1]))
        let ternary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1], ids[2]))

        intrinsic [ stringType; intType ] (optionOf intType) (ternary SemanticNodeKind.StringIndexOf [ slot 0; slot 1; int 0L ])
        intrinsic [ stringType; stringType ] (optionOf intType) (ternary SemanticNodeKind.StringIndexOf [ slot 0; slot 1; slot 1 ])
        intrinsic [ intType ] (optionOf intType) (binary SemanticNodeKind.StringLastIndexOf [ slot 0; text "a" ])
        intrinsic [ stringType; intType ] boolType (binary SemanticNodeKind.StringStartsWith [ slot 0; slot 1 ])
        intrinsic [ intType ] boolType (binary SemanticNodeKind.StringEndsWith [ slot 0; text "a" ])
        intrinsic [ stringType; intType ] (listOf stringType) (binary SemanticNodeKind.StringSplit [ slot 0; slot 1 ])
        // Result types: Option<Int64>, Bool and List<String>, not String.
        Assert.Contains(
            issues [ stringType ] intType (ternary SemanticNodeKind.StringIndexOf [ slot 0; text "a"; int 0L ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        Assert.Contains(
            issues [ stringType ] stringType (binary SemanticNodeKind.StringSplit [ slot 0; text "," ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let cases =
            [ "stringIndexOf", optionOf intType, ternary SemanticNodeKind.StringIndexOf [ slot 0; slot 1; int 2L ]
              "stringLastIndexOf", optionOf intType, binary SemanticNodeKind.StringLastIndexOf [ slot 0; slot 1 ]
              "stringStartsWith", boolType, binary SemanticNodeKind.StringStartsWith [ slot 0; slot 1 ]
              "stringEndsWith", boolType, binary SemanticNodeKind.StringEndsWith [ slot 0; slot 1 ]
              "stringSplit", listOf stringType, binary SemanticNodeKind.StringSplit [ slot 0; slot 1 ] ]
        for kind, resultType, body in cases do
            let committed = snapshotWith [ stringType; stringType ] resultType body
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
            // A persisted node over a non-String operand is a validation error.
            let broken =
                { committed with
                    Functions = committed.Functions |> List.map (fun fn ->
                        { fn with
                            Body =
                                { fn.Body with
                                    Nodes = fn.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference reference when reference = parameterId 1 ->
                                            { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
