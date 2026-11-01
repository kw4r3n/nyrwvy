namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringLength and StringSlice: code-point length and clamped code-point slicing.
module StringSliceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-slice-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private caseTypeId = id<TypeTag> "case"
    let private textFieldId = id<FieldTag> "text"
    let private startFieldId = id<FieldTag> "start"
    let private endFieldId = id<FieldTag> "end"
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private caseType: SemanticType =
        { Exported = true; TypeParameters = []; Id = caseTypeId
          DisplayName = "SliceCase"
          Kind =
            SemanticTypeKind.Product
                [ { Id = textFieldId; DisplayName = "text"; Type = stringType }
                  { Id = startFieldId; DisplayName = "start"; Type = intType }
                  { Id = endFieldId; DisplayName = "end"; Type = intType } ] }

    [<Fact>]
    let ``length and slice reject mistyped operands in recipes and persisted graphs`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "measure"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string operation was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request [ intType ] intType (ExpressionRecipe.StringLength(slot 0))))
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request [ stringType; stringType ] stringType (ExpressionRecipe.StringSlice(slot 0, slot 1, ExpressionRecipe.Int64Literal 0L))))
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request [ stringType ] stringType (ExpressionRecipe.StringSlice(slot 0, ExpressionRecipe.Int64Literal 0L, slot 0))))
        // The length is Int64, not String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringLength(slot 0))))
        let proposal =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
                (request [ stringType; intType ] stringType (ExpressionRecipe.StringSlice(slot 0, slot 1, slot 1))) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        let intParameterId = committed.Functions.Head.Parameters[1].Id
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference parameterId when parameterId = intParameterId ->
                                        { node with Kind = SemanticNodeKind.StringLiteral "1" }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
