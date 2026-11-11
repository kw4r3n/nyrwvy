namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringFromCodePoint and StringToCodePoint: Python chr and ord as Options, pure and never failing,
/// with surrogate codes (U+D800..U+DFFF) giving None, since a String holds only scalar values.
module StringCodePointConversionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-code-point-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``StringFromCodePoint and StringToCodePoint type check, persist, and lower through every layer`` () =
        let parameterId = id<ParameterTag> "input0"

        let snapshotWith parameterType resultType kind =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "codePoint"
                  DisplayName = "codePoint"
                  Exported = true
                  TypeParameters = []
                  Parameters = [ { Id = parameterId; DisplayName = "input0"; Type = parameterType } ]
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body =
                    GraphBuilder.build
                        (id<GraphNodeTag>)
                        (GraphBuilder.node (fun ids -> kind ids[0]) [ GraphBuilder.param parameterId ]) }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        let issues parameterType resultType kind = SnapshotValidator.validate (snapshotWith parameterType resultType kind)
        Assert.Contains(
            issues stringType (optionOf stringType) SemanticNodeKind.StringFromCodePoint,
            fun issue -> issue.Code = "StringIntrinsicTypeMismatch"
        )
        Assert.Contains(
            issues intType (optionOf intType) SemanticNodeKind.StringToCodePoint,
            fun issue -> issue.Code = "StringIntrinsicTypeMismatch"
        )
        // Result types: Option<String> and Option<Int64>, not the bare String or Int64.
        Assert.Contains(
            issues intType stringType SemanticNodeKind.StringFromCodePoint,
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        Assert.Contains(
            issues stringType intType SemanticNodeKind.StringToCodePoint,
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let cases =
            [ "stringFromCodePoint", intType, optionOf stringType, SemanticNodeKind.StringFromCodePoint, SemanticNodeKind.StringLiteral "x"
              "stringToCodePoint", stringType, optionOf intType, SemanticNodeKind.StringToCodePoint, SemanticNodeKind.Int64Literal 1L ]
        for kind, parameterType, resultType, nodeKind, wrongOperand in cases do
            let committed = snapshotWith parameterType resultType nodeKind
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
            // A persisted node over an operand of the other type is a validation error.
            let broken =
                { committed with
                    Functions = committed.Functions |> List.map (fun fn ->
                        { fn with
                            Body =
                                { fn.Body with
                                    Nodes = fn.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference reference when reference = parameterId ->
                                            { node with Kind = wrongOperand }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
