namespace NyamNyam.Core.Tests

open Microsoft.FSharp.Reflection
open NyamNyam.Core
open Xunit

module AgentFunctionCreationTests =
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit

    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-function-create-" + name)
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-create-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-create-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-function-create-" + name)
    let private effectId name = StableId.createForTesting<EffectTag> ("effect-function-create-" + name)

    let private workspace (snapshot: Snapshot) =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private parameter displayName typeRef: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
        { DisplayName = displayName
          Type = typeRef }

    let private request (snapshot: Snapshot) displayName parameters resultType requiredEffects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = snapshot.Id
          DisplayName = displayName
          Parameters = parameters
          ResultType = resultType
          RequiredEffects = requiredEffects
          Body = body }

    let private simulate ws creationRequest =
        match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws creationRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected function-creation simulation failure: %A" error

    let private assertRejectedCode expected result =
        match result with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``atomic creation resolves ordered parameter slots and publishes system-owned identities`` () =
        let snapshot = Snapshot.empty (snapshotId "sum")
        let ws = workspace snapshot
        let creationRequest =
            request
                snapshot
                "sum"
                [ parameter "left" int64Type
                  parameter "right" int64Type ]
                int64Type
                []
                (ExpressionRecipe.Int64WrappingAdd(
                    ExpressionRecipe.ParameterSlot 0,
                    ExpressionRecipe.ParameterSlot 1
                ))
        let proposal = simulate ws creationRequest
        let review = AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review proposal
        let created = review.CreatedFunction

        Assert.Equal("sum", created.DisplayName)
        Assert.Equal(2, created.Parameters.Length)
        Assert.NotEqual(created.Parameters[0].Id, created.Parameters[1].Id)
        Assert.Equal("left", created.Parameters[0].DisplayName)
        Assert.Equal("right", created.Parameters[1].DisplayName)
        Assert.Equal(int64Type, created.ResultType)
        Assert.Empty(created.RequiredEffects)
        Assert.Equal(3, review.MaterializedBody.Length)
        Assert.Equal(created.Id, review.ProposedFocus.Function.Id)

        match created.Body.Nodes with
        | [ leftReference; rightReference; sum ] ->
            match leftReference.Kind, rightReference.Kind, sum.Kind with
            | SemanticNodeKind.ParameterReference leftId,
              SemanticNodeKind.ParameterReference rightId,
              SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
                Assert.Equal(created.Parameters[0].Id, leftId)
                Assert.Equal(created.Parameters[1].Id, rightId)
                Assert.Equal(leftReference.Id, leftNodeId)
                Assert.Equal(rightReference.Id, rightNodeId)
                Assert.Equal(sum.Id, created.Body.ResultNodeId)
            | other -> failwithf "Unexpected materialized body: %A" other
        | other -> failwithf "Unexpected materialized node count: %A" other

        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics ->
            failwithf "Function creation commit failed: %A" diagnostics
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, publication) ->
            Assert.Equal(created.Id, publication.CreatedFunctionId)
            let published = AgentWorkspace.snapshotForStructuralEditing next
            Assert.Empty(SnapshotValidator.validate published)
            Assert.Contains(published.Functions, fun functionEntity -> functionEntity.Id = created.Id)

    [<Fact>]
    let ``atomic creation reuses nested bind recipe materialization`` () =
        let snapshot = Snapshot.empty (snapshotId "bind")
        let ws = workspace snapshot
        let creationRequest =
            request
                snapshot
                "is_empty_bound"
                [ parameter "value" stringType ]
                boolType
                []
                (ExpressionRecipe.Bind(
                    ExpressionRecipe.ParameterSlot 0,
                    ExpressionRecipe.StringIsEmpty(
                        ExpressionRecipe.PatternReference
                            { ScopeDepth = 0
                              Slot = RecipePatternSlot.Value }
                    )
                ))
        let proposal = simulate ws creationRequest
        let created =
            proposal
            |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
            |> fun review -> review.CreatedFunction

        match created.Body.Nodes with
        | [ inputReference; bindNode ] ->
            match inputReference.Kind, bindNode.Kind with
            | SemanticNodeKind.ParameterReference parameterReferenceId,
              SemanticNodeKind.Bind(inputNodeId, arm) ->
                Assert.Equal(created.Parameters.Head.Id, parameterReferenceId)
                Assert.Equal(inputReference.Id, inputNodeId)
                Assert.Equal(stringType, arm.ValueBinding.Type)
                match arm.Body.Nodes with
                | [ patternReference; isEmpty ] ->
                    match patternReference.Kind, isEmpty.Kind with
                    | SemanticNodeKind.PatternReference bindingId,
                      SemanticNodeKind.StringIsEmpty sourceNodeId ->
                        Assert.Equal(arm.ValueBinding.Id, bindingId)
                        Assert.Equal(patternReference.Id, sourceNodeId)
                        Assert.Equal(isEmpty.Id, arm.Body.ResultNodeId)
                    | other -> failwithf "Unexpected nested bind body: %A" other
                | other -> failwithf "Unexpected nested bind node count: %A" other
            | other -> failwithf "Unexpected bind materialization: %A" other
        | other -> failwithf "Unexpected top-level bind node count: %A" other

    [<Fact>]
    let ``creation-only parameter slots and declaration typing reject invalid uses`` () =
        let existingParameterId = parameterId "existing"
        let existingResultId = nodeId "existing-result"
        let existingFunctionId = functionId "existing"
        let existingFunction: SemanticFunction =
            { Id = existingFunctionId
              DisplayName = "existing"
              Parameters =
                [ { Id = existingParameterId
                    DisplayName = "value"
                    Type = int64Type } ]
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = existingResultId
                        Kind = SemanticNodeKind.ParameterReference existingParameterId } ]
                  ResultNodeId = existingResultId } }
        let snapshot =
            { Snapshot.empty (snapshotId "validation") with
                Functions = [ existingFunction ] }
        let ws = workspace snapshot

        request
            snapshot
            "bad_slot"
            [ parameter "value" int64Type ]
            int64Type
            []
            (ExpressionRecipe.ParameterSlot 1)
        |> AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
        |> assertRejectedCode "PureExpressionParameterSlotOutOfRange"

        request
            snapshot
            "bad_parameter_id"
            [ parameter "value" int64Type ]
            int64Type
            []
            (ExpressionRecipe.Parameter existingParameterId)
        |> AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
        |> assertRejectedCode "PureExpressionParameterOutOfScope"

        request
            snapshot
            "bad_result"
            [ parameter "value" int64Type ]
            boolType
            []
            (ExpressionRecipe.ParameterSlot 0)
        |> AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
        |> assertRejectedCode "FunctionCreationResultTypeMismatch"

        let bodyReplacement: AgentReplaceFunctionBodyWithExpressionRecipe =
            { TargetFunctionId = existingFunctionId
              ExpectedBaseSnapshotId = snapshot.Id
              ExpectedCurrentResultNodeId = existingResultId
              Replacement = ExpressionRecipe.ParameterSlot 0 }
        AgentExpressionStructuralWorkflow.simulate ws bodyReplacement
        |> assertRejectedCode "PureExpressionParameterSlotUnavailable"

        let targetedReplacement: AgentExpressionStructuralWorkflow.Targeted.Request =
            { TargetFunctionId = existingFunctionId
              ExpectedBaseSnapshotId = snapshot.Id
              TargetNodeId = existingResultId
              Replacement = ExpressionRecipe.ParameterSlot 0 }
        AgentExpressionStructuralWorkflow.Targeted.simulate ws targetedReplacement
        |> assertRejectedCode "PureExpressionParameterSlotUnavailable"

    [<Fact>]
    let ``atomic creation validates required effects before materialization`` () =
        let effect =
            { Id = effectId "io"
              DisplayName = "IO" }
        let unknownEffectId = effectId "unknown"
        let calleeResultId = nodeId "effect-callee-result"
        let calleeId = functionId "effect-callee"
        let callee: SemanticFunction =
            { Id = calleeId
              DisplayName = "effectful"
              Parameters = []
              ResultType = unitType
              RequiredEffects = [ effect.Id ]
              Body =
                { Nodes = [ { Id = calleeResultId; Kind = SemanticNodeKind.UnitLiteral } ]
                  ResultNodeId = calleeResultId } }
        let snapshot =
            { Snapshot.empty (snapshotId "effects") with
                Effects = [ effect ]
                Functions = [ callee ] }
        let ws = workspace snapshot
        let callBody = ExpressionRecipe.DirectCall(calleeId, [])

        request snapshot "missing_effect" [] unitType [] callBody
        |> AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
        |> assertRejectedCode "ExpressionCallMissingEffectRequirement"

        request snapshot "duplicate_effect" [] unitType [ effect.Id; effect.Id ] callBody
        |> AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
        |> assertRejectedCode "FunctionCreationDuplicateRequiredEffect"

        request snapshot "unknown_effect" [] unitType [ unknownEffectId ] callBody
        |> AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
        |> assertRejectedCode "FunctionCreationRequiredEffectNotFound"

        request snapshot "with_effect" [] unitType [ effect.Id ] callBody
        |> simulate ws
        |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
        |> fun review -> Assert.Equal<EffectId list>([ effect.Id ], review.CreatedFunction.RequiredEffects)

    [<Fact>]
    let ``atomic creation review rejects stale publication`` () =
        let existingResultId = nodeId "stale-existing-result"
        let existingFunctionId = functionId "stale-existing"
        let existingFunction: SemanticFunction =
            { Id = existingFunctionId
              DisplayName = "existing"
              Parameters = []
              ResultType = unitType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = existingResultId; Kind = SemanticNodeKind.UnitLiteral } ]
                  ResultNodeId = existingResultId } }
        let snapshot =
            { Snapshot.empty (snapshotId "stale") with
                Functions = [ existingFunction ] }
        let ws = workspace snapshot
        let proposal =
            request snapshot "created" [] unitType [] ExpressionRecipe.UnitLiteral
            |> simulate ws
        let advance =
            AgentWorkflow.simulate
                ws
                [ AgentOperationIntent.RenameDisplayName(
                    AgentRenameTarget.Function existingFunctionId,
                    "existing_advanced"
                  ) ]
            |> function
                | Ok value -> value
                | Error error -> failwithf "Advance simulation failed: %A" error
        let advanced =
            match AgentWorkflow.commit ws advance with
            | AgentCommitResult.Committed(next, _) -> next
            | AgentCommitResult.Rejected diagnostics -> failwithf "Advance commit failed: %A" diagnostics

        match AgentExpressionStructuralWorkflow.FunctionCreation.commit advanced proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected [ diagnostic ]
            when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | other -> failwithf "Expected stale creation rejection, got %A" other

    [<Fact>]
    let ``atomic creation request exposes intent rather than raw semantic authority`` () =
        let fields =
            FSharpType.GetRecordFields(typeof<AgentExpressionStructuralWorkflow.FunctionCreation.Request>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList
        let expected =
            [ "ExpectedBaseSnapshotId", typeof<SnapshotId>
              "DisplayName", typeof<string>
              "Parameters", typeof<AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent list>
              "ResultType", typeof<TypeRef>
              "RequiredEffects", typeof<EffectId list>
              "Body", typeof<ExpressionRecipe> ]
        Assert.Equal<(string * System.Type) list>(expected, fields)
