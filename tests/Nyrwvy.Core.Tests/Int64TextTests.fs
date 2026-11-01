namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Int64ToString and StringParseInt64: canonical decimal conversion between Int64 and String.
module Int64TextTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("int64-text-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionInt = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ intType ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``conversions reject mistyped operands in recipes and persisted graphs`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "convert"
              Parameters = [ { DisplayName = "input"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped conversion was accepted: %A" other
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request stringType stringType (ExpressionRecipe.Int64ToString(ExpressionRecipe.ParameterSlot 0))))
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request intType optionInt (ExpressionRecipe.StringParseInt64(ExpressionRecipe.ParameterSlot 0))))
        // The parse result is Option<Int64>, not Int64.
        Assert.NotEqual<string>("", rejects (request stringType intType (ExpressionRecipe.StringParseInt64(ExpressionRecipe.ParameterSlot 0))))
        let proposal =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
                (request intType stringType (ExpressionRecipe.Int64ToString(ExpressionRecipe.ParameterSlot 0))) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference _ -> { node with Kind = SemanticNodeKind.StringLiteral "1" }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "Int64IntrinsicTypeMismatch")
