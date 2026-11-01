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
    let private text value = ExpressionRecipe.StringLiteral value
    let private int value = ExpressionRecipe.Int64Literal value
    let private strings items = ExpressionRecipe.ListConstruct(stringType, items |> List.map text)

    [<Fact>]
    let ``StringJoin and ListSlice type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "joinSlice"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped builtin was accepted: %A" other
        let accepts request =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap "accepted" |> ignore
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        let join = ExpressionRecipe.StringJoin(slot 0, slot 1)
        Assert.Equal(mismatch, rejects (request [ listOf intType; stringType ] stringType join))
        Assert.Equal(mismatch, rejects (request [ stringType; stringType ] stringType join))
        Assert.Equal(mismatch, rejects (request [ listOf stringType; intType ] stringType join))
        Assert.Equal(mismatch, rejects (request [ listOf (optionOf stringType); stringType ] stringType join))
        // The result is a String, not a List<String>.
        Assert.NotEqual<string>("", rejects (request [ listOf stringType; stringType ] (listOf stringType) join))
        let slice = ExpressionRecipe.ListSlice(slot 0, slot 1, slot 2)
        Assert.Equal(mismatch, rejects (request [ stringType; intType; intType ] stringType slice))
        Assert.Equal(mismatch, rejects (request [ listOf intType; stringType; intType ] (listOf intType) slice))
        Assert.Equal(mismatch, rejects (request [ listOf intType; intType; stringType ] (listOf intType) slice))
        // The result keeps the input's item type; it is neither Option<T> nor another List type.
        Assert.NotEqual<string>("", rejects (request [ listOf intType; intType; intType ] (optionOf intType) slice))
        Assert.NotEqual<string>("", rejects (request [ listOf intType; intType; intType ] (listOf stringType) slice))
        accepts (request [ listOf (optionOf intType); intType; intType ] (listOf (optionOf intType)) slice)
        accepts (request [] (listOf stringType) (ExpressionRecipe.ListSlice(ExpressionRecipe.ListEmpty stringType, int 0L, int 1L)))
        let cases =
            [ "stringJoin", "NyText.Join(", [ listOf stringType; stringType ], stringType, join, "StringIntrinsicTypeMismatch"
              "listSlice", ".Slice(", [ listOf (listOf stringType); intType; intType ], listOf (listOf stringType), slice, "ListSliceListTypeMismatch" ]
        for kind, emitted, parameters, resultType, body, brokenCode in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request parameters resultType body)
                |> unwrap ("simulate " + kind)
            let committed =
                match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
                | other -> failwithf "Commit failed: %A" other
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
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            Assert.Contains(emitted, artifact.Source)
            // A persisted node whose list input is not the required list is a validation error.
            let parameterId = committed.Functions.Head.Parameters.Head.Id
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
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = brokenCode)
