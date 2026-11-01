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
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value
    let private int value = ExpressionRecipe.Int64Literal value
    let private someValue = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
    /// The String inside StringFromCodePoint(code), or "" for None.
    let private fromOrEmpty code =
        ExpressionRecipe.MatchOption(ExpressionRecipe.StringFromCodePoint code, someValue, text "")

    [<Fact>]
    let ``StringFromCodePoint and StringToCodePoint type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "codePoint"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string builtin was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ stringType ] (optionOf stringType) (ExpressionRecipe.StringFromCodePoint(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] (optionOf intType) (ExpressionRecipe.StringToCodePoint(slot 0))))
        // Result types: Option<String> and Option<Int64>, not the bare String or Int64.
        Assert.NotEqual<string>("", rejects (request [ intType ] stringType (ExpressionRecipe.StringFromCodePoint(slot 0))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringToCodePoint(slot 0))))
        let cases =
            [ "stringFromCodePoint", "NyText.FromCodePoint(", intType, optionOf stringType, ExpressionRecipe.StringFromCodePoint(slot 0), SemanticNodeKind.StringLiteral "x"
              "stringToCodePoint", "NyText.ToCodePoint(", stringType, optionOf intType, ExpressionRecipe.StringToCodePoint(slot 0), SemanticNodeKind.Int64Literal 1L ]
        for kind, emitted, parameterType, resultType, body, wrongOperand in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ parameterType ] resultType body)
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
            // A persisted node over an operand of the other type is a validation error.
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
                                            { node with Kind = wrongOperand }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
