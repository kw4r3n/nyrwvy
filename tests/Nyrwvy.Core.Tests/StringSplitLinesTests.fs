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
    let private text value = ExpressionRecipe.StringLiteral value

    [<Fact>]
    let ``StringSplitLines type checks, persists, and lowers through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "lines"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped StringSplitLines was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ intType; stringType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1))))
        Assert.Equal(mismatch, rejects (request [ stringType; intType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1))))
        Assert.Equal(mismatch, rejects (request [ stringType; listOf stringType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1))))
        // Result type: List<String>, not String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringSplitLines(slot 0, text "\n"))))
        let proposal =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ stringType; stringType ] (listOf stringType) (ExpressionRecipe.StringSplitLines(slot 0, slot 1)))
            |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
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
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains("NyText.SplitLines(", artifact.Source)
        // The breaks operand is a character set, like StringSpan's set.
        let splitNode =
            committed.Functions.Head.Body.Nodes
            |> List.find (fun node -> match node.Kind with SemanticNodeKind.StringSplitLines _ -> true | _ -> false)
        Assert.Contains(ValueDependency.ofNode splitNode, fun dependency -> dependency.Role = ValueDependencyRole.CharacterSet)
        // A persisted node over non-String breaks is a validation error.
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
