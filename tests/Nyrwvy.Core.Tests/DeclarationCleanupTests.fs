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
                    [ { Id = typeId
                        DisplayName = "Holder"
                        Kind = SemanticTypeKind.Product
                            [ { Id = id<FieldTag> "nested-capability"
                                DisplayName = "notifier"
                                Type = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Capability capabilityId ]) } ] } ] }
        reject withType [ AgentOperationIntent.DeleteCapability capabilityId ] "CapabilityStillReferenced"

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
