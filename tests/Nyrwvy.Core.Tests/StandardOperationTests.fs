namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Standard operations whose meaning is fixed with no choice to make (post-M2 orders 35 and 36):
/// ListReverse, and the IEEE 754 Float64 -> Float64 operations Sqrt, Abs, Negate, Floor, Ceiling and
/// Truncate. None of them fails.
module StandardOperationTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("standard-operations-" + name)
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private recipeOf mode value =
        match mode with
        | "sqrt" -> ExpressionRecipe.Float64Sqrt value
        | "abs" -> ExpressionRecipe.Float64Abs value
        | "negate" -> ExpressionRecipe.Float64Negate value
        | "floor" -> ExpressionRecipe.Float64Floor value
        | "ceiling" -> ExpressionRecipe.Float64Ceiling value
        | "truncate" -> ExpressionRecipe.Float64Truncate value
        | "reverse" -> ExpressionRecipe.ListReverse value
        | "dropLast" -> ExpressionRecipe.ListDropLast value
        | "last" -> ExpressionRecipe.ListLast value
        | other -> failwithf "Unknown mode %s" other

    [<Theory>]
    [<InlineData("sqrt", "float64Sqrt", "System.Math.Sqrt(")>]
    [<InlineData("abs", "float64Abs", "System.Math.Abs(")>]
    [<InlineData("negate", "float64Negate", " = -")>]
    [<InlineData("floor", "float64Floor", "System.Math.Floor(")>]
    [<InlineData("ceiling", "float64Ceiling", "System.Math.Ceiling(")>]
    [<InlineData("truncate", "float64Truncate", "System.Math.Truncate(")>]
    [<InlineData("reverse", "listReverse", ".Reverse()")>]
    [<InlineData("dropLast", "listDropLast", ".DropLast()")>]
    [<InlineData("last", "listLast", ".Last()")>]
    let ``Standard operations type check, persist, and lower through every layer``
        (mode: string, wireKind: string, csharpCall: string) =
        // ListReverse keeps its List<T> type, so a List<String> stands in for every item type.
        let valueType, wrongType, issueCode =
            if mode = "reverse" then listOf textType, textType, "ListReverseInputTypeMismatch"
            elif mode = "dropLast" then listOf textType, textType, "ListDropLastInputTypeMismatch"
            elif mode = "last" then listOf textType, textType, "ListLastInputTypeMismatch"
            else floatType, intType, "Float64OperandTypeMismatch"
        // ListLast gives Option<T>; every other operation keeps its operand type.
        let resultType = if mode = "last" then TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ textType ]) else valueType
        let snapshot = Snapshot.empty (id<SnapshotTag> ("base-" + mode))
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = mode
              Parameters = [ { DisplayName = "value"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped operation was accepted: %A" other
        let body = recipeOf mode (ExpressionRecipe.ParameterSlot 0)
        Assert.NotEqual<string>("", rejects (request wrongType resultType body))
        Assert.NotEqual<string>("", rejects (request valueType wrongType body))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request valueType resultType body) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"" + wireKind + "\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"" + wireKind + "\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"" + wireKind + "\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains(csharpCall, artifact.Source)
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
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = issueCode)

    let private commit (call: string -> JsonElement) (proposal: JsonElement) =
        call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposal.GetProperty("proposalId").GetString() + "\"}") |> ignore
