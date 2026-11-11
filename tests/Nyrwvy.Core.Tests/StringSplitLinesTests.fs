namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringSplitLines: the lines of a String split at any code point of a breaks set, with "\r\n" as
/// one break when both halves are members; Python splitlines for the matching set, pure and linear.
module StringSplitLinesTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-split-lines-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``StringSplitLines type checks, persists, and lowers through every layer`` () =
        let parameterId index = id<ParameterTag> $"input{index}"
        let slot index = GraphBuilder.param (parameterId index)
        let text value = GraphBuilder.leaf (SemanticNodeKind.StringLiteral value)

        let snapshotWith (parameterTypes: TypeRef list) resultType body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "lines"
                  DisplayName = "lines"
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
        let splitLines = GraphBuilder.node (fun ids -> SemanticNodeKind.StringSplitLines(ids[0], ids[1])) [ slot 0; slot 1 ]

        intrinsic [ intType; stringType ] (listOf stringType) splitLines
        intrinsic [ stringType; intType ] (listOf stringType) splitLines
        intrinsic [ stringType; listOf stringType ] (listOf stringType) splitLines
        // Result type: List<String>, not String.
        Assert.Contains(
            issues [ stringType ] stringType (GraphBuilder.node (fun ids -> SemanticNodeKind.StringSplitLines(ids[0], ids[1])) [ slot 0; text "\n" ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let committed = snapshotWith [ stringType; stringType ] (listOf stringType) splitLines
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringSplitLines\"", json)
        Assert.Contains("\"breaksNodeId\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringSplitLines\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringSplitLines\"", ExecutionIrSerializer.serialize execution)
        // The breaks operand is a character set, like StringSpan's set.
        let splitNode =
            committed.Functions.Head.Body.Nodes
            |> List.find (fun node -> match node.Kind with SemanticNodeKind.StringSplitLines _ -> true | _ -> false)
        Assert.Contains(ValueDependency.ofNode splitNode, fun dependency -> dependency.Role = ValueDependencyRole.CharacterSet)
        // A persisted node over non-String breaks is a validation error.
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
