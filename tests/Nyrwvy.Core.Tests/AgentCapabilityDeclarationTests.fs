namespace Nyrwvy.Core.Tests

open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

module AgentCapabilityDeclarationTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private unitType = TypeRef.Primitive PrimitiveType.Unit

    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-capability-declaration-" + name)
    let private effectId name = StableId.createForTesting<EffectTag> ("effect-capability-declaration-" + name)
    let private typeId name = StableId.createForTesting<TypeTag> ("type-capability-declaration-" + name)

    let private parameter displayName typeRef: AgentCapabilityParameterIntent =
        { DisplayName = displayName
          Type = typeRef }

    let private operation displayName parameters resultType failureType effectId: AgentCapabilityOperationIntent =
        { DisplayName = displayName
          Parameters = parameters
          ResultType = resultType
          FailureType = failureType
          EffectId = effectId }

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private simulate ws request =
        match AgentCapabilityDeclarationWorkflow.simulate ws request with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected capability-declaration simulation failure: %A" error

    let private commit ws proposal =
        match AgentCapabilityDeclarationWorkflow.commit ws proposal with
        | AgentCapabilityDeclarationCommitResult.Rejected diagnostics ->
            failwithf "Unexpected capability-declaration commit failure: %A" diagnostics
        | AgentCapabilityDeclarationCommitResult.Committed(next, publication) -> next, publication

    let private publishEffect ws displayName =
        let request: AgentCreateEffectDeclarationRequest =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
              DisplayName = displayName }
        let proposal =
            match AgentEffectDeclarationWorkflow.simulate ws request with
            | Ok value -> value
            | Error error -> failwithf "Effect prerequisite simulation failed: %A" error
        let review = AgentEffectDeclarationProposal.review proposal
        match AgentEffectDeclarationWorkflow.commit ws proposal with
        | AgentEffectDeclarationCommitResult.Rejected diagnostics ->
            failwithf "Effect prerequisite commit failed: %A" diagnostics
        | AgentEffectDeclarationCommitResult.Committed(next, _) -> next, review.CreatedEffect

    [<Fact>]
    let ``capability creation allocates identities publishes and composes with CapabilityInvoke`` () =
        let ws0 = Snapshot.empty (snapshotId "composition") |> workspace
        let ws1, ioEffect = publishEffect ws0 "io"

        let request: AgentCreateCapabilityDeclarationRequest =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws1
              DisplayName = "Console"
              Operations =
                [ operation
                    "write"
                    [ parameter "text" stringType ]
                    unitType
                    None
                    ioEffect.Id
                  operation
                    "combine"
                    [ parameter "left" stringType
                      parameter "right" stringType ]
                    stringType
                    (Some int64Type)
                    ioEffect.Id ] }

        let proposal = simulate ws1 request
        let review = AgentCapabilityDeclarationProposal.review proposal
        Assert.Equal(request, review.RequestedChange)
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(AgentCapabilityDeclarationProposal.transactionId proposal, review.Receipt.TransactionId)
        Assert.Equal(AgentCapabilityDeclarationProposal.proposedSnapshotId proposal, review.Receipt.ProposedSnapshotId)

        let created = review.CreatedCapability
        Assert.Equal("Console", created.DisplayName)
        Assert.Equal(2, created.Operations.Length)
        Assert.NotEqual(created.Operations[0].Id, created.Operations[1].Id)
        let parameterIds =
            created.Operations
            |> List.collect (fun item -> item.Parameters)
            |> List.map (fun item -> item.Id)
        Assert.Equal(parameterIds.Length, parameterIds |> List.distinct |> List.length)

        let ws2, publication = commit ws1 proposal
        Assert.Equal(created.Id, publication.CreatedCapabilityId)
        Assert.Contains(AgentWorkspace.listCapabilities ws2, fun item -> item = created)
        Assert.Contains(AgentWorkspace.listEffects ws2, fun item -> item = ioEffect)

        let writeOperation = created.Operations |> List.find (fun item -> item.DisplayName = "write")
        let writeParameter = List.exactlyOne writeOperation.Parameters
        let functionRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws2
              DisplayName = "write_message"
              Parameters =
                [ { DisplayName = "console"
                    Type = TypeRef.Capability created.Id } ]
              ResultType = unitType
              RequiredEffects = [ ioEffect.Id ]
              Body =
                ExpressionRecipe.CapabilityInvoke(
                    ExpressionRecipe.ParameterSlot 0,
                    writeOperation.Id,
                    [ writeParameter.Id, ExpressionRecipe.StringLiteral "hello" ]
                ) }

        let functionProposal =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws2 functionRequest with
            | Ok value -> value
            | Error error -> failwithf "CapabilityInvoke composition failed: %A" error

        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws2 functionProposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics ->
            failwithf "CapabilityInvoke composition commit failed: %A" diagnostics
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(ws3, publication) ->
            let authored =
                AgentWorkspace.listFunctions ws3
                |> List.find (fun item -> item.Id = publication.CreatedFunctionId)
            Assert.Equal<EffectId list>([ ioEffect.Id ], authored.RequiredEffects)
            Assert.Contains(ValueGraph.collectNodes authored.Body, fun node ->
                match node.Kind with
                | SemanticNodeKind.CapabilityInvoke(_, operationId, _) -> operationId = writeOperation.Id
                | _ -> false)
            Assert.Contains(AgentWorkspace.listCapabilities ws3, fun item -> item = created)

    [<Fact>]
    let ``invalid capability declaration intent rejects without publication`` () =
        let knownEffect: SemanticEffect =
            { Id = effectId "known"
              DisplayName = "known" }
        let snapshot =
            { Snapshot.empty (snapshotId "invalid") with
                Effects = [ knownEffect ] }
        let ws = workspace snapshot
        let unknownEffect = effectId "unknown"
        let unknownType = typeId "unknown"

        let assertRejected expected request =
            match AgentCapabilityDeclarationWorkflow.simulate ws request with
            | Error(AgentCapabilityDeclarationWorkflowError.SimulationRejected [ diagnostic ]) ->
                Assert.Equal(expected, diagnostic.Code)
            | result -> failwithf "Expected %s rejection, got %A" expected result

        { ExpectedBaseSnapshotId = snapshot.Id
          DisplayName = " "
          Operations = [] }
        |> assertRejected "CapabilityDeclarationDisplayNameBlank"

        { ExpectedBaseSnapshotId = snapshot.Id
          DisplayName = "Cap"
          Operations = [ operation " " [] unitType None knownEffect.Id ] }
        |> assertRejected "CapabilityDeclarationOperationDisplayNameBlank"

        { ExpectedBaseSnapshotId = snapshot.Id
          DisplayName = "Cap"
          Operations = [ operation "op" [ parameter " " stringType ] unitType None knownEffect.Id ] }
        |> assertRejected "CapabilityDeclarationParameterDisplayNameBlank"

        { ExpectedBaseSnapshotId = snapshot.Id
          DisplayName = "Cap"
          Operations = [ operation "op" [] unitType None unknownEffect ] }
        |> assertRejected "CapabilityDeclarationUnknownEffect"

        { ExpectedBaseSnapshotId = snapshot.Id
          DisplayName = "Cap"
          Operations = [ operation "op" [ parameter "value" (TypeRef.Named unknownType) ] unitType None knownEffect.Id ] }
        |> assertRejected "CapabilityDeclarationUnknownNamedType"

        Assert.Equal(snapshot.Id, AgentWorkspace.currentSnapshotId ws)
        Assert.Empty(AgentWorkspace.listCapabilities ws)

    [<Fact>]
    let ``capability declaration review binds exact result and stale proposal cannot publish`` () =
        let effect: SemanticEffect =
            { Id = effectId "stale"
              DisplayName = "effect" }
        let snapshot =
            { Snapshot.empty (snapshotId "stale") with
                Effects = [ effect ] }
        let ws = workspace snapshot
        let request name: AgentCreateCapabilityDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = name
              Operations = [] }

        let first = simulate ws (request "First")
        let second = simulate ws (request "Second")
        let firstReview = AgentCapabilityDeclarationProposal.review first
        Assert.Equal(firstReview.Receipt.TransactionId, AgentCapabilityDeclarationProposal.transactionId first)
        Assert.Equal(firstReview.Receipt.ProposedSnapshotId, AgentCapabilityDeclarationProposal.proposedSnapshotId first)

        let advanced, _ = commit ws first
        match AgentCapabilityDeclarationWorkflow.commit advanced second with
        | AgentCapabilityDeclarationCommitResult.Rejected [ diagnostic ] ->
            Assert.Equal("StaleBaseSnapshot", diagnostic.Code)
        | result -> failwithf "Expected stale capability proposal rejection, got %A" result

        let names = AgentWorkspace.listCapabilities advanced |> List.map (fun item -> item.DisplayName)
        Assert.Equal<string list>([ "First" ], names)
        Assert.Contains(AgentWorkspace.listEffects advanced, fun item -> item = effect)

    [<Fact>]
    let ``capability declaration request exposes intent without production identity authority`` () =
        let requestFields =
            FSharpType.GetRecordFields(typeof<AgentCreateCapabilityDeclarationRequest>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList

        Assert.Equal<(string * System.Type) list>(
            [ "ExpectedBaseSnapshotId", typeof<SnapshotId>
              "DisplayName", typeof<string>
              "Operations", typeof<AgentCapabilityOperationIntent list> ],
            requestFields
        )

        let fieldTypes =
            [ typeof<AgentCreateCapabilityDeclarationRequest>
              typeof<AgentCapabilityOperationIntent>
              typeof<AgentCapabilityParameterIntent> ]
            |> List.collect (fun recordType ->
                FSharpType.GetRecordFields(recordType)
                |> Array.map (fun field -> field.PropertyType)
                |> Array.toList)

        Assert.DoesNotContain(fieldTypes, fun fieldType ->
            [ typeof<CapabilityId>
              typeof<CapabilityOperationId>
              typeof<CapabilityParameterId>
              typeof<SemanticCapability>
              typeof<SemanticCapabilityOperation>
              typeof<SemanticCapabilityParameter>
              typeof<Snapshot> ]
            |> List.exists ((=) fieldType))
