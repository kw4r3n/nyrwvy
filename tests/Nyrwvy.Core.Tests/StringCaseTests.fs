namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringToLower and StringUtf8ByteCount: culture-invariant simple lowercase per code point and
/// the UTF-8 byte length, both pure and never failing.
module StringCaseTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-case-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``StringToLower, StringToUpper and StringUtf8ByteCount type check, persist, and lower through every layer`` () =
        let parameterId = id<ParameterTag> "input0"
        let slot0 = GraphBuilder.param parameterId

        let snapshotWith parameterType resultType body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "case"
                  DisplayName = "case"
                  Exported = true
                  TypeParameters = []
                  Parameters = [ { Id = parameterId; DisplayName = "input0"; Type = parameterType } ]
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body = GraphBuilder.build (id<GraphNodeTag>) body }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        let unary kind = GraphBuilder.node (fun ids -> kind ids[0]) [ slot0 ]
        let issues parameterType resultType body = SnapshotValidator.validate (snapshotWith parameterType resultType body)
        let intrinsic parameterType resultType body =
            Assert.Contains(issues parameterType resultType body, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

        intrinsic intType stringType (unary SemanticNodeKind.StringToLower)
        intrinsic intType intType (unary SemanticNodeKind.StringUtf8ByteCount)
        intrinsic intType stringType (unary SemanticNodeKind.StringToUpper)
        // Result types: String and Int64, not the other way round.
        Assert.Contains(
            issues stringType intType (unary SemanticNodeKind.StringToLower),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        Assert.Contains(
            issues stringType stringType (unary SemanticNodeKind.StringUtf8ByteCount),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let cases =
            [ "stringToLower", stringType, unary SemanticNodeKind.StringToLower
              "stringToUpper", stringType, unary SemanticNodeKind.StringToUpper
              "stringUtf8ByteCount", intType, unary SemanticNodeKind.StringUtf8ByteCount ]
        for kind, resultType, body in cases do
            let committed = snapshotWith stringType resultType body
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
                                        | SemanticNodeKind.ParameterReference reference when reference = parameterId ->
                                            { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
