namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Xunit

module StringConcatTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("concat-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private parameter name: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
        { DisplayName = name; Type = stringType }

    [<Fact>]
    let ``StringConcat rejects mistyped operands in recipes and persisted graphs`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> function
            | Ok value -> value
            | Error errors -> failwithf "Invalid fixture: %A" errors
        let request body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "join"
              Parameters = [ parameter "left"; parameter "right" ]
              ResultType = stringType
              RequiredEffects = []
              Body = body }
        let invalid =
            request (ExpressionRecipe.StringConcat(ExpressionRecipe.BoolLiteral true, ExpressionRecipe.ParameterSlot 1))
        match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws invalid with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("PureExpressionOperandTypeMismatch", diagnostic.Code)
        | other -> failwithf "Mistyped concatenation was accepted: %A" other
        let valid = request (ExpressionRecipe.StringConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws valid |> function
            | Ok result -> result
            | Error error -> failwithf "Creation failed: %A" error
        let committed = AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal |> function
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let created = AgentWorkspace.snapshotForStructuralEditing committed
        let broken =
            { created with
                Functions = created.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference parameterId when parameterId = fn.Parameters.Head.Id ->
                                        { node with Kind = SemanticNodeKind.BoolLiteral true }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
