namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

/// By-name requests resolve exactly one declaration of a kind, so creation and rename
/// refuse a display name that another declaration of the same kind already holds.
module DisplayNameUniquenessTests =
    let private workspace () =
        let fixture = MilestoneZeroGraph.create ()
        match AgentWorkspace.create fixture.Snapshot with
        | Ok workspace -> fixture, workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private codes (diagnostics: TransactionDiagnostic list) = diagnostics |> List.map (fun item -> item.Code)

    [<Fact>]
    let ``creating a declaration with a taken display name is rejected per kind`` () =
        let fixture, workspace = workspace ()
        let baseId = AgentWorkspace.currentSnapshotId workspace
        match AgentTypeDeclarationWorkflow.simulate workspace { TypeParameters = []; ExpectedBaseSnapshotId = baseId; DisplayName = "ValidUser"; Kind = AgentTypeDeclarationKindIntent.Product [] } with
        | Error(AgentTypeDeclarationWorkflowError.SimulationRejected diagnostics) ->
            Assert.Equal<string list>([ "TypeDeclarationDuplicateDisplayName" ], codes diagnostics)
        | other -> failwithf "Duplicate type name was accepted: %A" other
        match AgentEffectDeclarationWorkflow.simulate workspace { ExpectedBaseSnapshotId = baseId; DisplayName = "ConsoleOutput" } with
        | Error(AgentEffectDeclarationWorkflowError.SimulationRejected diagnostics) ->
            Assert.Equal<string list>([ "EffectDeclarationDuplicateDisplayName" ], codes diagnostics)
        | other -> failwithf "Duplicate effect name was accepted: %A" other
        match AgentCapabilityDeclarationWorkflow.simulate workspace { ExpectedBaseSnapshotId = baseId; DisplayName = "Console"; Operations = [] } with
        | Error(AgentCapabilityDeclarationWorkflowError.SimulationRejected diagnostics) ->
            Assert.Equal<string list>([ "CapabilityDeclarationDuplicateDisplayName" ], codes diagnostics)
        | other -> failwithf "Duplicate capability name was accepted: %A" other
        let functionRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = baseId
              TypeParameters = None
              DisplayName = "main"
              Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.Bool
              RequiredEffects = []
              Body = ExpressionRecipe.BoolLiteral true }
        match AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace functionRequest with
        | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics) ->
            Assert.Equal<string list>([ "FunctionCreationDuplicateDisplayName" ], codes diagnostics)
        | other -> failwithf "Duplicate function name was accepted: %A" other
        // Names are unique per kind: a function may share a type's name, and a fresh name is accepted.
        for name in [ "ValidUser"; "main_2" ] do
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace { functionRequest with DisplayName = name } with
            | Ok _ -> ()
            | Error error -> failwithf "Unique function name '%s' was rejected: %A" name error
        ignore fixture

    [<Fact>]
    let ``a rename may not take another declaration's display name but may swap names`` () =
        let fixture, workspace = workspace ()
        let rename id name = AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function id, name)
        match AgentWorkflow.simulate workspace [ rename fixture.ValidateFunctionId "main" ] with
        | Error(AgentWorkflowError.SimulationRejected diagnostics) ->
            Assert.Equal<string list>([ "DuplicateDisplayName" ], codes diagnostics)
        | other -> failwithf "Rename onto a taken name was accepted: %A" other
        match AgentWorkflow.simulate workspace [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Type fixture.PolicyTypeId, "ValidUser") ] with
        | Error(AgentWorkflowError.SimulationRejected diagnostics) ->
            Assert.Equal<string list>([ "DuplicateDisplayName" ], codes diagnostics)
        | other -> failwithf "Type rename onto a taken name was accepted: %A" other
        // Only the final names matter, so an exchange within one transaction is allowed.
        match AgentWorkflow.simulate workspace [ rename fixture.ValidateFunctionId "main"; rename fixture.MainFunctionId "validate_user" ] with
        | Ok _ -> ()
        | Error error -> failwithf "Name swap was rejected: %A" error
