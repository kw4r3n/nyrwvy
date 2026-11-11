namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringReplace (Python str.replace, ordinal in code points, empty pattern is a no-op) and
/// StringTrim, StringTrimStart and StringTrimEnd (Unicode White_Space, as .NET char.IsWhiteSpace).
module StringReplaceTrimTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-replace-trim-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    /// The 25 White_Space code points, all in the BMP.
    let private whiteSpace =
        [ yield! [ 0x09 .. 0x0D ]
          0x20
          0x85
          0xA0
          0x1680
          yield! [ 0x2000 .. 0x200A ]
          0x2028
          0x2029
          0x202F
          0x205F
          0x3000 ]
        |> List.map (char >> string)

    [<Fact>]
    let ``StringReplace and the trims type check, persist, and lower through every layer`` () =
        Assert.Equal(25, whiteSpace.Length)
        Assert.All(whiteSpace, fun space -> Assert.True(Char.IsWhiteSpace(space, 0)))
        let parameterId index = id<ParameterTag> $"input{index}"
        let slot index = GraphBuilder.param (parameterId index)
        let text value = GraphBuilder.leaf (SemanticNodeKind.StringLiteral value)

        let snapshotWith (parameterTypes: TypeRef list) resultType body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "edit"
                  DisplayName = "edit"
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
        let unary kind = GraphBuilder.node (fun ids -> kind ids[0])
        let ternary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1], ids[2]))

        intrinsic [ intType ] stringType (ternary SemanticNodeKind.StringReplace [ slot 0; text "a"; text "b" ])
        intrinsic [ intType ] stringType (ternary SemanticNodeKind.StringReplace [ text "a"; slot 0; text "b" ])
        intrinsic [ intType ] stringType (ternary SemanticNodeKind.StringReplace [ text "a"; text "b"; slot 0 ])
        intrinsic [ intType ] stringType (unary SemanticNodeKind.StringTrim [ slot 0 ])
        intrinsic [ intType ] stringType (unary SemanticNodeKind.StringTrimStart [ slot 0 ])
        intrinsic [ intType ] stringType (unary SemanticNodeKind.StringTrimEnd [ slot 0 ])
        // The result is a String, not an Int64.
        Assert.Contains(
            issues [ stringType ] intType (unary SemanticNodeKind.StringTrim [ slot 0 ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        Assert.Contains(
            issues [ stringType ] intType (ternary SemanticNodeKind.StringReplace [ slot 0; text "a"; text "b" ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let cases =
            [ "stringReplace", [ stringType; stringType; stringType ], ternary SemanticNodeKind.StringReplace [ slot 0; slot 1; slot 2 ]
              "stringTrim", [ stringType ], unary SemanticNodeKind.StringTrim [ slot 0 ]
              "stringTrimStart", [ stringType ], unary SemanticNodeKind.StringTrimStart [ slot 0 ]
              "stringTrimEnd", [ stringType ], unary SemanticNodeKind.StringTrimEnd [ slot 0 ] ]
        for kind, parameters, body in cases do
            let committed = snapshotWith parameters stringType body
            Assert.Empty(SnapshotValidator.validate committed)
            Assert.Equal(SnapshotSchema.minimumVersion SnapshotSchemaFeature.StringReplaceAndTrim, SnapshotSchema.required committed)
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
                                        | SemanticNodeKind.ParameterReference _ -> { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
