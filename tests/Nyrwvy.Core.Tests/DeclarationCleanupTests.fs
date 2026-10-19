namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module DeclarationCleanupTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("declaration-cleanup-" + name)
    let private effectId = id<EffectTag> "effect"
    let private capabilityId = id<CapabilityTag> "capability"
    let private operationId = id<CapabilityOperationTag> "operation"
    let private operationParameterId = id<CapabilityParameterTag> "argument"
    let private functionId = id<FunctionTag> "function"
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit

    let private fixture () =
        let effect: SemanticEffect = { Id = effectId; DisplayName = "Notify" }
        let capability: SemanticCapability =
            { Id = capabilityId
              DisplayName = "Notifier"
              Operations =
                [ { Id = operationId
                    DisplayName = "send"
                    Parameters = [ { Id = operationParameterId; DisplayName = "text"; Type = stringType } ]
                    ResultType = unitType
                    FailureType = None
                    EffectId = effectId } ] }
        let targetParameter: SemanticParameter =
            { Id = id<ParameterTag> "capability-input"
              DisplayName = "notifier"
              Type = TypeRef.Capability capabilityId }
        let textParameter: SemanticParameter =
            { Id = id<ParameterTag> "text-input"; DisplayName = "text"; Type = stringType }
        let capabilityNodeId = id<GraphNodeTag> "capability-reference"
        let textNodeId = id<GraphNodeTag> "text-reference"
        let invocationId = id<GraphNodeTag> "invocation"
        let fn: SemanticFunction =
            { Id = functionId
              TypeParameters = []
              DisplayName = "useNotifier"
              Parameters = [ targetParameter; textParameter ]
              ResultType = unitType
              RequiredEffects = [ effectId ]
              Body =
                { Nodes =
                    [ { Id = capabilityNodeId; Kind = SemanticNodeKind.ParameterReference targetParameter.Id }
                      { Id = textNodeId; Kind = SemanticNodeKind.ParameterReference textParameter.Id }
                      { Id = invocationId
                        Kind = SemanticNodeKind.CapabilityInvoke(
                            capabilityNodeId,
                            operationId,
                            [ { TargetCapabilityParameterId = operationParameterId; ValueNodeId = textNodeId } ]) } ]
                  ResultNodeId = invocationId } }
        { Snapshot.empty (id<SnapshotTag> "initial") with
            Effects = [ effect ]
            Capabilities = [ capability ]
            Functions = [ fn ] }

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error errors -> failwithf "Invalid fixture: %A" errors

    let private cleanup =
        [ AgentOperationIntent.DeleteFunction functionId
          AgentOperationIntent.DeleteCapability capabilityId
          AgentOperationIntent.DeleteEffect effectId ]

    [<Fact>]
    let ``capability and effect cleanup preserves exact reviewed identities and can be composed`` () =
        let initial = fixture ()
        let ws = workspace initial
        let proposal = AgentWorkflow.simulate ws cleanup |> function
            | Ok value -> value
            | Error error -> failwithf "Simulation failed: %A" error
        let review = AgentChangeProposal.review proposal
        Assert.Equal(effectId, (List.exactlyOne review.ResultingChange.Diff.EffectsRemoved).EffectId)
        let removed = List.exactlyOne review.ResultingChange.Diff.CapabilitiesRemoved
        Assert.Equal(capabilityId, removed.CapabilityId)
        Assert.Equal<CapabilityOperationId list>([ operationId ], removed.RemovedOperationIds)
        Assert.Equal<CapabilityParameterId list>([ operationParameterId ], removed.RemovedParameterIds)
        Assert.Empty(review.ResultingChange.FunctionFocus)
        let next = AgentWorkflow.commit ws proposal |> function
            | AgentCommitResult.Committed(value, _) -> value
            | other -> failwithf "Commit failed: %A" other
        let snapshot = AgentWorkspace.snapshotForStructuralEditing next
        Assert.Empty(SnapshotValidator.validate snapshot)
        Assert.Empty(snapshot.Functions)
        Assert.Empty(snapshot.Capabilities)
        Assert.Empty(snapshot.Effects)
        let json = AgentWorkspace.serializeCurrentSnapshot next |> function
            | Ok text -> text
            | Error error -> failwithf "Serialize failed: %A" error
        match SnapshotCodec.deserialize json with
        | Ok reopened ->
            Assert.Empty(reopened.Functions)
            Assert.Empty(reopened.Capabilities)
            Assert.Empty(reopened.Effects)
        | Error error -> failwithf "Reopen failed: %A" error

    [<Fact>]
    let ``deleting declarations refuses live capability and effect references`` () =
        let initial = fixture ()
        let reject snapshot operations expected =
            match AgentWorkflow.simulate (workspace snapshot) operations with
            | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal(expected, diagnostic.Code)
            | other -> failwithf "Expected %s, got %A" expected other
        reject initial [ AgentOperationIntent.DeleteCapability capabilityId ] "CapabilityStillReferenced"
        reject initial [ AgentOperationIntent.DeleteEffect effectId ] "EffectStillReferenced"
        reject initial
            [ AgentOperationIntent.DeleteFunction functionId; AgentOperationIntent.DeleteEffect effectId ]
            "EffectStillReferenced"
        let typeId = id<TypeTag> "holder"
        let withType =
            { initial with
                Functions = []
                Types =
                    [ { TypeParameters = []; Id = typeId
                        DisplayName = "Holder"
                        Kind = SemanticTypeKind.Product
                            [ { Id = id<FieldTag> "nested-capability"
                                DisplayName = "notifier"
                                Type = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Capability capabilityId ]) } ] } ] }
        reject withType [ AgentOperationIntent.DeleteCapability capabilityId ] "CapabilityStillReferenced"

    [<Fact>]
    let ``operation deletion preserves sibling operations and capability value references`` () =
        let initial = fixture ()
        let owner = List.exactlyOne initial.Capabilities
        let unusedOperationId = id<CapabilityOperationTag> "unused-operation"
        let unusedParameterId = id<CapabilityParameterTag> "unused-parameter"
        let unused =
            { List.exactlyOne owner.Operations with
                Id = unusedOperationId; DisplayName = "unused <operation>"
                Parameters = [ { Id = unusedParameterId; DisplayName = "unused"; Type = stringType } ] }
        let initial = { initial with Capabilities = [ { owner with Operations = owner.Operations @ [ unused ] } ] }
        let ws = workspace initial
        let proposal = AgentWorkflow.simulate ws [ AgentOperationIntent.DeleteCapabilityOperation unusedOperationId ] |> function
            | Ok value -> value
            | Error error -> failwithf "Simulation failed: %A" error
        let review = AgentChangeProposal.review proposal
        let removed = List.exactlyOne review.ResultingChange.Diff.CapabilityOperationsRemoved
        Assert.Equal(capabilityId, removed.OwnerCapabilityId)
        Assert.Equal(unusedOperationId, removed.OperationId)
        Assert.Equal<CapabilityParameterId list>([ unusedParameterId ], removed.RemovedParameterIds)
        Assert.Empty(review.ResultingChange.Diff.CapabilitiesRemoved)
        let document = review.ResultingChange |> ViewerProjection.changeReview |> ViewerHtml.renderChangeReviewDocument
        Assert.Contains("unused &lt;operation&gt;", document)
        Assert.Contains(StableId.value unusedParameterId, document)
        let next = AgentWorkflow.commit ws proposal |> function
            | AgentCommitResult.Committed(value, _) -> value
            | other -> failwithf "Commit failed: %A" other
        let snapshot = AgentWorkspace.snapshotForStructuralEditing next
        Assert.Equal<SemanticCapability list>([ owner ], snapshot.Capabilities)
        Assert.Equal<SemanticFunction list>(initial.Functions, snapshot.Functions)
        Assert.Empty(SnapshotValidator.validate snapshot)

    [<Fact>]
    let ``operation deletion refuses live invocations missing targets and invalid batch order`` () =
        let initial = fixture ()
        let reject operations expected =
            match AgentWorkflow.simulate (workspace initial) operations with
            | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal(expected, diagnostic.Code)
            | other -> failwithf "Expected %s, got %A" expected other
        reject [ AgentOperationIntent.DeleteCapabilityOperation operationId ] "CapabilityOperationStillReferenced"
        reject [ AgentOperationIntent.DeleteCapabilityOperation operationId; AgentOperationIntent.DeleteFunction functionId ]
            "CapabilityOperationStillReferenced"
        reject [ AgentOperationIntent.DeleteCapabilityOperation(id<CapabilityOperationTag> "missing") ] "TargetNotFound"
        reject [ AgentOperationIntent.DeleteFunction functionId
                 AgentOperationIntent.DeleteCapabilityOperation operationId
                 AgentOperationIntent.DeleteCapabilityOperation operationId ] "TargetNotFound"
        Assert.Equal<SemanticCapability list>((fixture ()).Capabilities, initial.Capabilities)

    [<Fact>]
    let ``operation deletion finds invocations inside nested branches`` () =
        let initial = fixture ()
        let fn = List.exactlyOne initial.Functions
        let conditionId = id<GraphNodeTag> "condition"
        let branchId = id<GraphNodeTag> "branch"
        let unitId = id<GraphNodeTag> "else-unit"
        let body: ValueGraph =
            { Nodes =
                [ { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral true }
                  { Id = branchId
                    Kind = SemanticNodeKind.Conditional(conditionId, fn.Body,
                        { Nodes = [ { Id = unitId; Kind = SemanticNodeKind.UnitLiteral } ]; ResultNodeId = unitId }) } ]
              ResultNodeId = branchId }
        let nested = { initial with Functions = [ { fn with Body = body } ] }
        match AgentWorkflow.simulate (workspace nested) [ AgentOperationIntent.DeleteCapabilityOperation operationId ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("CapabilityOperationStillReferenced", diagnostic.Code)
        | other -> failwithf "Nested invocation was not protected: %A" other

    let private unusedTypeId = id<TypeTag> "unused-type"

    let private withUnusedType (snapshot: Snapshot) =
        { snapshot with
            Types =
                [ { TypeParameters = []; Id = unusedTypeId
                    DisplayName = "Unused"
                    Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "unused-field"; DisplayName = "text"; Type = stringType } ] } ] }

    let private dropRequiredEffect =
        AgentOperationIntent.RemoveFunctionRequiredEffect { TargetFunctionId = functionId; EffectId = effectId }

    [<Fact>]
    let ``a safe deletion is not blamed for an issue an earlier operation in the batch left`` () =
        let initial = fixture () |> withUnusedType
        let preExisting =
            SnapshotValidator.validate
                { initial with
                    Functions = initial.Functions |> List.map (fun fn -> { fn with RequiredEffects = [] }) }
            |> List.head
        match AgentWorkflow.simulate (workspace initial) [ dropRequiredEffect; AgentOperationIntent.DeleteType unusedTypeId ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("InvalidProposedSnapshot", diagnostic.Code)
            Assert.Equal($"{preExisting.Code}: {preExisting.Message}", diagnostic.Message)
        | other -> failwithf "Expected the earlier operation's issue, got %A" other
        // A later operation may repair what an earlier one left; the deletion in between does not block it.
        let repaired =
            [ dropRequiredEffect
              AgentOperationIntent.DeleteType unusedTypeId
              AgentOperationIntent.AddFunctionRequiredEffect { TargetFunctionId = functionId; EffectId = effectId } ]
        match AgentWorkflow.simulate (workspace initial) repaired with
        | Ok proposal ->
            let diff = (AgentChangeProposal.review proposal).ResultingChange.Diff
            Assert.Equal(unusedTypeId, (List.exactlyOne diff.TypesRemoved).TypeId)
            Assert.Empty(diff.FunctionRequiredEffectsChanged)
        | Error error -> failwithf "Repaired batch was rejected: %A" error

    [<Fact>]
    let ``a deletion is blamed only for the issues it introduces`` () =
        let initial = fixture ()
        let preExisting =
            SnapshotValidator.validate
                { initial with
                    Functions = initial.Functions |> List.map (fun fn -> { fn with RequiredEffects = [] }) }
            |> List.head
        match AgentWorkflow.simulate (workspace initial) [ dropRequiredEffect; AgentOperationIntent.DeleteCapability capabilityId ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("CapabilityStillReferenced", diagnostic.Code)
            Assert.DoesNotContain(preExisting.Message, diagnostic.Message)
        | other -> failwithf "Expected CapabilityStillReferenced, got %A" other

    [<Fact>]
    let ``JSONL operation deletion retains an empty capability and allows effect cleanup`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-operation-cleanup-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (fixture ()) |> function
                | Ok text -> text
                | Error error -> failwithf "Fixture encoding failed: %A" error)
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let command =
                $"""{{"id":"cleanup","op":"simulateOperations","operations":[{{"kind":"deleteFunction","functionId":"{StableId.value functionId}"}},{{"kind":"deleteCapabilityOperation","operationId":"{StableId.value operationId}"}},{{"kind":"deleteEffect","effectId":"{StableId.value effectId}"}}]}}"""
            use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            let diff = result.GetProperty("review").GetProperty("diff")
            let removed = diff.GetProperty("capabilityOperationsRemoved").EnumerateArray() |> Seq.exactlyOne
            Assert.Equal(StableId.value operationId, removed.GetProperty("operationId").GetString())
            Assert.Equal(StableId.value capabilityId, removed.GetProperty("ownerCapabilityId").GetString())
            Assert.Equal(StableId.value operationParameterId, (removed.GetProperty("removedParameterIds")[0]).GetString())
            let proposalId = result.GetProperty("proposalId").GetString()
            use commit =
                AuthoringProtocol.executeLine session ($"""{{"id":"commit","op":"commit","proposalId":"{proposalId}"}}""")
                |> JsonDocument.Parse
            Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            session.Close()
            use reopened = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Reopen failed: %A" error
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> function
                | Ok value -> value
                | Error error -> failwithf "Stored graph invalid: %A" error
            Assert.Empty(snapshot.Functions)
            Assert.Empty(snapshot.Effects)
            Assert.Equal(capabilityId, (List.exactlyOne snapshot.Capabilities).Id)
            Assert.Empty((List.exactlyOne snapshot.Capabilities).Operations)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``JSONL commits full declaration cleanup and resumes an empty workspace`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-declaration-cleanup-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let initial = fixture ()
            File.WriteAllText(path, SnapshotCodec.serialize initial |> function
                | Ok text -> text
                | Error error -> failwithf "Fixture encoding failed: %A" error)
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let command =
                $"""{{"id":"cleanup","op":"simulateOperations","operations":[{{"kind":"deleteFunction","functionId":"{StableId.value functionId}"}},{{"kind":"deleteCapability","capabilityId":"{StableId.value capabilityId}"}},{{"kind":"deleteEffect","effectId":"{StableId.value effectId}"}}]}}"""
            use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            let diff = result.GetProperty("review").GetProperty("diff")
            Assert.Equal(1, diff.GetProperty("effectsRemoved").GetArrayLength())
            Assert.Equal(1, diff.GetProperty("capabilitiesRemoved").GetArrayLength())
            let proposalId = result.GetProperty("proposalId").GetString()
            use commit =
                AuthoringProtocol.executeLine session ($"""{{"id":"commit","op":"commit","proposalId":"{proposalId}"}}""")
                |> JsonDocument.Parse
            Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            session.Close()
            use reopened = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Reopen failed: %A" error
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> function
                | Ok value -> value
                | Error error -> failwithf "Stored graph invalid: %A" error
            Assert.Empty(snapshot.Functions)
            Assert.Empty(snapshot.Effects)
            Assert.Empty(snapshot.Capabilities)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
