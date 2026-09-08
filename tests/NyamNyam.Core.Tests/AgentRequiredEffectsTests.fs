namespace NyamNyam.Core.Tests

open NyamNyam.Core
open Xunit

module AgentRequiredEffectsTests =
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private effectId = StableId.createForTesting<EffectTag> "effect-required-edit"
    let private calleeId = StableId.createForTesting<FunctionTag> "function-required-edit-callee"
    let private callerId = StableId.createForTesting<FunctionTag> "function-required-edit-caller"
    let private calleeResultId = StableId.createForTesting<GraphNodeTag> "node-required-edit-callee-result"
    let private callerResultId = StableId.createForTesting<GraphNodeTag> "node-required-edit-caller-result"

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private getFunction workspace functionId =
        match AgentWorkspace.getFunction workspace functionId with
        | Ok value -> value
        | Error error -> failwithf "Function query failed: %A" error

    let private effectIntent functionId: AgentFunctionRequiredEffectIntent =
        { TargetFunctionId = functionId
          EffectId = effectId }

    let private createCallSnapshot () =
        let effect: SemanticEffect =
            { Id = effectId
              DisplayName = "EditedEffect" }
        let callee: SemanticFunction =
            { Id = calleeId
              DisplayName = "callee"
              Parameters = []
              ResultType = unitType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = calleeResultId; Kind = SemanticNodeKind.UnitLiteral } ]
                  ResultNodeId = calleeResultId } }
        let caller: SemanticFunction =
            { Id = callerId
              DisplayName = "caller"
              Parameters = []
              ResultType = unitType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = callerResultId; Kind = SemanticNodeKind.DirectCall(calleeId, []) } ]
                  ResultNodeId = callerResultId } }
        { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-required-edit") with
            Effects = [ effect ]
            Functions = [ callee; caller ] }

    let private publish workspace operations =
        let proposal =
            match AgentWorkflow.simulate workspace operations with
            | Ok value -> value
            | Error error -> failwithf "Simulation failed: %A" error
        let review = AgentChangeProposal.review proposal
        match AgentWorkflow.commit workspace proposal with
        | AgentCommitResult.Committed(next, _) -> review, next
        | other -> failwithf "Commit failed: %A" other

    [<Fact>]
    let ``required effects can be added and removed explicitly across a caller chain`` () =
        let initial = workspace (createCallSnapshot ())
        let additions =
            [ AgentOperationIntent.AddFunctionRequiredEffect(effectIntent calleeId)
              AgentOperationIntent.AddFunctionRequiredEffect(effectIntent callerId) ]
        let addReview, added = publish initial additions

        Assert.Equal(2, addReview.ResultingChange.Diff.FunctionRequiredEffectsChanged.Length)
        Assert.Equal<FunctionId list>(
            [ calleeId; callerId ] |> List.sortBy StableId.value,
            addReview.ResultingChange.AffectedFunctionIds
        )
        Assert.Contains(
            FunctionRequiredEffectChange.Added(calleeId, effectId),
            addReview.ResultingChange.Diff.FunctionRequiredEffectsChanged
        )
        Assert.Contains(
            FunctionRequiredEffectChange.Added(callerId, effectId),
            addReview.ResultingChange.Diff.FunctionRequiredEffectsChanged
        )

        Assert.Equal<EffectId list>([ effectId ], (getFunction added calleeId).RequiredEffects)
        Assert.Equal<EffectId list>([ effectId ], (getFunction added callerId).RequiredEffects)

        let removals =
            [ AgentOperationIntent.RemoveFunctionRequiredEffect(effectIntent callerId)
              AgentOperationIntent.RemoveFunctionRequiredEffect(effectIntent calleeId) ]
        let removeReview, removed = publish added removals
        Assert.Contains(
            FunctionRequiredEffectChange.Removed(calleeId, effectId),
            removeReview.ResultingChange.Diff.FunctionRequiredEffectsChanged
        )
        Assert.Contains(
            FunctionRequiredEffectChange.Removed(callerId, effectId),
            removeReview.ResultingChange.Diff.FunctionRequiredEffectsChanged
        )
        Assert.Empty((getFunction removed calleeId).RequiredEffects)
        Assert.Empty((getFunction removed callerId).RequiredEffects)

    [<Fact>]
    let ``final validation rejects an unpropagated callee effect requirement`` () =
        let ws = workspace (createCallSnapshot ())
        match
            AgentWorkflow.simulate
                ws
                [ AgentOperationIntent.AddFunctionRequiredEffect(effectIntent calleeId) ]
        with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("InvalidProposedSnapshot", diagnostic.Code)
            Assert.Contains("MissingEffectRequirement", diagnostic.Message)
        | other -> failwithf "Expected full-validation rejection, got %A" other

    [<Fact>]
    let ``required effect edits reject invalid direct requests and body-incompatible removal`` () =
        let snapshot = createCallSnapshot ()
        let pureWorkspace = workspace snapshot
        let unknownEffect = StableId.createForTesting<EffectTag> "effect-required-edit-unknown"
        let unknownFunction = StableId.createForTesting<FunctionTag> "function-required-edit-unknown"

        let assertRejected expectedCode workspace operation =
            match AgentWorkflow.simulate workspace [ operation ] with
            | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal(expectedCode, diagnostic.Code)
            | other -> failwithf "Expected %s, got %A" expectedCode other

        assertRejected
            "FunctionRequiredEffectNotPresent"
            pureWorkspace
            (AgentOperationIntent.RemoveFunctionRequiredEffect(effectIntent calleeId))
        assertRejected
            "EffectNotFound"
            pureWorkspace
            (AgentOperationIntent.AddFunctionRequiredEffect
                { TargetFunctionId = calleeId
                  EffectId = unknownEffect })
        assertRejected
            "TargetNotFound"
            pureWorkspace
            (AgentOperationIntent.AddFunctionRequiredEffect
                { TargetFunctionId = unknownFunction
                  EffectId = effectId })

        let _, withEffect =
            publish
                pureWorkspace
                [ AgentOperationIntent.AddFunctionRequiredEffect(effectIntent calleeId)
                  AgentOperationIntent.AddFunctionRequiredEffect(effectIntent callerId) ]
        assertRejected
            "FunctionRequiredEffectAlreadyPresent"
            withEffect
            (AgentOperationIntent.AddFunctionRequiredEffect(effectIntent calleeId))

        let fixture = MilestoneZeroGraph.create ()
        let fixtureWorkspace = workspace fixture.Snapshot
        match
            AgentWorkflow.simulate
                fixtureWorkspace
                [ AgentOperationIntent.RemoveFunctionRequiredEffect
                    { TargetFunctionId = fixture.ReportFunctionId
                      EffectId = fixture.ConsoleEffectId } ]
        with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("InvalidProposedSnapshot", diagnostic.Code)
            Assert.Contains("MissingEffectRequirement", diagnostic.Message)
        | other -> failwithf "Expected body-effect rejection, got %A" other
