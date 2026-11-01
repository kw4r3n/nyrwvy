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
    let private text value = ExpressionRecipe.StringLiteral value
    let private int value = ExpressionRecipe.Int64Literal value

    [<Fact>]
    let ``string search builtins type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "search"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string search was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] (optionOf intType) (ExpressionRecipe.StringIndexOf(slot 0, slot 1, int 0L))))
        Assert.Equal(mismatch, rejects (request [ stringType; stringType ] (optionOf intType) (ExpressionRecipe.StringIndexOf(slot 0, slot 1, slot 1))))
        Assert.Equal(mismatch, rejects (request [ intType ] (optionOf intType) (ExpressionRecipe.StringLastIndexOf(slot 0, text "a"))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] boolType (ExpressionRecipe.StringStartsWith(slot 0, slot 1))))
        Assert.Equal(mismatch, rejects (request [ intType ] boolType (ExpressionRecipe.StringEndsWith(slot 0, text "a"))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] (listOf stringType) (ExpressionRecipe.StringSplit(slot 0, slot 1))))
        // Result types: Option<Int64>, Bool and List<String>, not String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringIndexOf(slot 0, text "a", int 0L))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringSplit(slot 0, text ","))))
        let cases =
            [ "stringIndexOf", "NyText.IndexOf(", optionOf intType, ExpressionRecipe.StringIndexOf(slot 0, slot 1, int 2L)
              "stringLastIndexOf", "NyText.LastIndexOf(", optionOf intType, ExpressionRecipe.StringLastIndexOf(slot 0, slot 1)
              "stringStartsWith", "NyText.StartsWith(", boolType, ExpressionRecipe.StringStartsWith(slot 0, slot 1)
              "stringEndsWith", "NyText.EndsWith(", boolType, ExpressionRecipe.StringEndsWith(slot 0, slot 1)
              "stringSplit", "NyText.Split(", listOf stringType, ExpressionRecipe.StringSplit(slot 0, slot 1) ]
        for kind, emitted, resultType, body in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ stringType; stringType ] resultType body)
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
            // A persisted node over a non-String operand is a validation error.
            let parameterId = committed.Functions.Head.Parameters.[1].Id
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
