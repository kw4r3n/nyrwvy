namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringSpan and StringSpanBack: the code-point positions where a run of set members ends,
/// scanning forward from `from` or backward from `end`; pure, linear and never failing.
module StringSpanTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-span-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``StringSpan and StringSpanBack type check, persist, and lower through every layer`` () =
        let parameterId index = id<ParameterTag> $"input{index}"
        let slot index = GraphBuilder.param (parameterId index)
        let text value = GraphBuilder.leaf (SemanticNodeKind.StringLiteral value)
        let int value = GraphBuilder.leaf (SemanticNodeKind.Int64Literal value)

        let snapshotWith (parameterTypes: TypeRef list) resultType body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "span"
                  DisplayName = "span"
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
        let ternary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1], ids[2]))
        let span = ternary SemanticNodeKind.StringSpan
        let spanBack = ternary SemanticNodeKind.StringSpanBack

        intrinsic [ intType; stringType ] intType (span [ slot 0; slot 1; int 0L ])
        intrinsic [ stringType; intType ] intType (span [ slot 0; slot 1; int 0L ])
        intrinsic [ stringType; stringType ] intType (span [ slot 0; slot 1; slot 1 ])
        intrinsic [ intType; stringType ] intType (spanBack [ slot 0; slot 1; int 0L ])
        intrinsic [ stringType; intType ] intType (spanBack [ slot 0; slot 1; int 0L ])
        intrinsic [ stringType; stringType ] intType (spanBack [ slot 0; slot 1; slot 0 ])
        // Result type: Int64, not Option<Int64> or String.
        Assert.Contains(
            issues [ stringType ] stringType (span [ slot 0; text " "; int 0L ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        Assert.Contains(
            issues [ stringType ] stringType (spanBack [ slot 0; text " "; int 0L ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let cases =
            [ "stringSpan", span [ slot 0; slot 1; int 2L ]
              "stringSpanBack", spanBack [ slot 0; slot 1; int 2L ] ]
        for kind, body in cases do
            let committed = snapshotWith [ stringType; stringType ] intType body
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
            // A persisted node over a non-String set is a validation error.
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
