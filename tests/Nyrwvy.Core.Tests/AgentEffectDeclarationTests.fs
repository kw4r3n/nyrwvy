namespace Nyrwvy.Core.Tests

open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

module AgentEffectDeclarationTests =
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-effect-declaration-" + name)
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-effect-declaration-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-effect-declaration-" + name)
    let private unitType = TypeRef.Primitive PrimitiveType.Unit

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private simulate ws request =
        match AgentEffectDeclarationWorkflow.simulate ws request with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected effect-declaration simulation failure: %A" error

    let private commit ws proposal =
        match AgentEffectDeclarationWorkflow.commit ws proposal with
        | AgentEffectDeclarationCommitResult.Rejected diagnostics ->
            failwithf "Unexpected effect-declaration commit failure: %A" diagnostics
        | AgentEffectDeclarationCommitResult.Committed(next, publication) -> next, publication

    let private untouchedFunction () =
        let resultId = nodeId "untouched-result"
        { Id = functionId "untouched"
          DisplayName = "untouched"
          Parameters = []
          ResultType = unitType
          RequiredEffects = []
          Body =
            { Nodes = [ { Id = resultId; Kind = SemanticNodeKind.UnitLiteral } ]
              ResultNodeId = resultId } }

    [<Fact>]
    let ``effect creation allocates identity publishes and composes with function creation`` () =
        let untouched = untouchedFunction ()
        let snapshot =
            { Snapshot.empty (snapshotId "composition") with
                Functions = [ untouched ] }
        let ws0 = workspace snapshot
        let request: AgentCreateEffectDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "io" }

        let proposal = simulate ws0 request
        let review = AgentEffectDeclarationProposal.review proposal
        Assert.Equal(request, review.RequestedChange)
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(AgentEffectDeclarationProposal.transactionId proposal, review.Receipt.TransactionId)
        Assert.Equal(AgentEffectDeclarationProposal.proposedSnapshotId proposal, review.Receipt.ProposedSnapshotId)
        Assert.Equal("io", review.CreatedEffect.DisplayName)

        let ws1, publication = commit ws0 proposal
        Assert.Equal(review.CreatedEffect.Id, publication.CreatedEffectId)
        Assert.Contains(AgentWorkspace.listEffects ws1, fun effect -> effect = review.CreatedEffect)
        Assert.Contains(AgentWorkspace.listFunctions ws1, fun functionEntity -> functionEntity = untouched)

        let functionRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws1
              DisplayName = "uses_io"
              Parameters = []
              ResultType = unitType
              RequiredEffects = [ review.CreatedEffect.Id ]
              Body = ExpressionRecipe.UnitLiteral }

        let functionProposal =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws1 functionRequest with
            | Ok value -> value
            | Error error -> failwithf "Function composition failed: %A" error

        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws1 functionProposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics ->
            failwithf "Function composition commit failed: %A" diagnostics
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(ws2, publication) ->
            let created =
                AgentWorkspace.listFunctions ws2
                |> List.find (fun functionEntity -> functionEntity.Id = publication.CreatedFunctionId)
            Assert.Equal<EffectId list>([ review.CreatedEffect.Id ], created.RequiredEffects)
            Assert.Contains(AgentWorkspace.listFunctions ws2, fun functionEntity -> functionEntity = untouched)

    [<Fact>]
    let ``invalid effect intent rejects without publication`` () =
        let snapshot = Snapshot.empty (snapshotId "invalid")
        let ws = workspace snapshot
        let request: AgentCreateEffectDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = " " }

        match AgentEffectDeclarationWorkflow.simulate ws request with
        | Error(AgentEffectDeclarationWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("EffectDeclarationDisplayNameBlank", diagnostic.Code)
        | result -> failwithf "Expected blank effect rejection, got %A" result

        Assert.Equal(snapshot.Id, AgentWorkspace.currentSnapshotId ws)
        Assert.Empty(AgentWorkspace.listEffects ws)

    [<Fact>]
    let ``effect declaration review binds exact result and stale proposal cannot publish`` () =
        let snapshot = Snapshot.empty (snapshotId "stale")
        let ws = workspace snapshot
        let request name: AgentCreateEffectDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = name }

        let first = simulate ws (request "first")
        let second = simulate ws (request "second")
        let firstReview = AgentEffectDeclarationProposal.review first
        Assert.Equal(firstReview.Receipt.TransactionId, AgentEffectDeclarationProposal.transactionId first)
        Assert.Equal(firstReview.Receipt.ProposedSnapshotId, AgentEffectDeclarationProposal.proposedSnapshotId first)

        let advanced, _ = commit ws first
        match AgentEffectDeclarationWorkflow.commit advanced second with
        | AgentEffectDeclarationCommitResult.Rejected [ diagnostic ] ->
            Assert.Equal("StaleBaseSnapshot", diagnostic.Code)
        | result -> failwithf "Expected stale effect proposal rejection, got %A" result

        let names = AgentWorkspace.listEffects advanced |> List.map (fun effect -> effect.DisplayName)
        Assert.Equal<string list>([ "first" ], names)

    [<Fact>]
    let ``effect declaration request exposes intent without production identity authority`` () =
        let requestFields =
            FSharpType.GetRecordFields(typeof<AgentCreateEffectDeclarationRequest>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList

        Assert.Equal<(string * System.Type) list>(
            [ "ExpectedBaseSnapshotId", typeof<SnapshotId>
              "DisplayName", typeof<string> ],
            requestFields
        )

        let fieldTypes = requestFields |> List.map snd
        Assert.DoesNotContain(fieldTypes, fun fieldType ->
            [ typeof<EffectId>; typeof<SemanticEffect>; typeof<Snapshot> ]
            |> List.exists ((=) fieldType))
