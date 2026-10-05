namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module ParameterAdditionNonCallUseTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("parameter-addition-non-call-" + name)
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private listType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ int64Type ])
    let private targetId = id<FunctionTag> "double"
    let private effectId = id<EffectTag> "effect"
    let private capabilityId = id<CapabilityTag> "capability"
    let private operationId = id<CapabilityOperationTag> "apply"

    let private target: SemanticFunction =
        let parameterId = id<ParameterTag> "double-x"
        let resultId = id<GraphNodeTag> "double-result"
        { Id = targetId
          DisplayName = "double"
          Parameters = [ { Id = parameterId; DisplayName = "x"; Type = int64Type } ]
          ResultType = int64Type
          RequiredEffects = []
          Body = { Nodes = [ { Id = resultId; Kind = SemanticNodeKind.ParameterReference parameterId } ]; ResultNodeId = resultId } }

    let private user name parameters resultType (nodes: (string * SemanticNodeKind) list) : SemanticFunction =
        let nodes = nodes |> List.map (fun (key, kind) -> { Id = id<GraphNodeTag> (name + "-" + key); Kind = kind })
        { Id = id<FunctionTag> name
          DisplayName = name
          Parameters = parameters
          ResultType = resultType
          RequiredEffects = []
          Body = { Nodes = nodes; ResultNodeId = (List.last nodes).Id } }

    let private callbackUser =
        let itemsId = id<ParameterTag> "mapAll-items"
        user "mapAll" [ { Id = itemsId; DisplayName = "items"; Type = listType } ] listType
            [ "items", SemanticNodeKind.ParameterReference itemsId
              "map", SemanticNodeKind.ListMap(id<GraphNodeTag> "mapAll-items", targetId, []) ]

    let private functionValueUser =
        user "asValue" [] (TypeRef.Function([ int64Type ], int64Type)) [ "value", SemanticNodeKind.FunctionReference targetId ]

    let private capabilityUser =
        user "doubler" [] (TypeRef.Capability capabilityId)
            [ "construct", SemanticNodeKind.CapabilityConstruct(capabilityId, [ operationId, targetId ]) ]

    let private directCaller =
        let valueId = id<GraphNodeTag> "callDouble-value"
        user "callDouble" [] int64Type
            [ "value", SemanticNodeKind.Int64Literal 2L
              "call", SemanticNodeKind.DirectCall(targetId, [ { TargetParameterId = target.Parameters.Head.Id; ValueNodeId = valueId } ]) ]

    let private snapshot (users: SemanticFunction list) =
        let snapshot =
            { Snapshot.empty (id<SnapshotTag> "initial") with
                Effects = [ { Id = effectId; DisplayName = "Effect" } ]
                Capabilities =
                    [ { Id = capabilityId
                        DisplayName = "Doubler"
                        Operations =
                          [ { Id = operationId
                              DisplayName = "apply"
                              Parameters = [ { Id = id<CapabilityParameterTag> "apply-x"; DisplayName = "x"; Type = int64Type } ]
                              ResultType = int64Type
                              FailureType = None
                              EffectId = effectId } ] } ]
                Functions = target :: users }
        Assert.Empty(SnapshotValidator.validate snapshot)
        snapshot

    let private addParameter (users: SemanticFunction list) plans =
        let workspace =
            match AgentWorkspace.create (snapshot users) with
            | Ok value -> value
            | Error errors -> failwithf "Invalid fixture: %A" errors
        AgentWorkflow.simulate
            workspace
            [ AgentOperationIntent.AddFunctionParameter
                  { TargetFunctionId = targetId
                    NewParameterDisplayName = "factor"
                    NewParameterType = int64Type
                    CallSitePlans = plans } ]

    let private expectRejected (users: SemanticFunction list) plans (expectedUses: string list) =
        match addParameter users plans with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("ParameterAdditionNonCallUse", diagnostic.Code)
            Assert.Contains($"'double' ({StableId.value targetId})", diagnostic.Message)
            for expected in expectedUses do
                Assert.Contains(expected, diagnostic.Message)
            diagnostic.Message
        | other -> failwithf "Expected ParameterAdditionNonCallUse, got %A" other

    let private named (fn: SemanticFunction) kind = $"'{fn.DisplayName}' ({StableId.value fn.Id}) ({kind})"

    [<Fact>]
    let ``a function used as a list callback rejects a new parameter with the using function named`` () =
        expectRejected [ callbackUser ] [] [ named callbackUser "callback" ] |> ignore

    [<Fact>]
    let ``a function used as a function value rejects a new parameter with the using function named`` () =
        expectRejected [ functionValueUser ] [] [ named functionValueUser "function value" ] |> ignore

    [<Fact>]
    let ``a capability implementation rejects a new parameter with the using function named`` () =
        expectRejected [ capabilityUser ] [] [ named capabilityUser "capability implementation" ] |> ignore

    [<Fact>]
    let ``non-call uses are reported before call-site plans and list every using function`` () =
        let callNodeId = id<GraphNodeTag> "callDouble-call"
        let message =
            expectRejected
                [ directCaller; capabilityUser; callbackUser; functionValueUser ]
                [ AgentCallPlan.LeaveMissing callNodeId ]
                [ named callbackUser "callback"
                  named functionValueUser "function value"
                  named capabilityUser "capability implementation" ]
        Assert.DoesNotContain("'callDouble'", message)

    [<Fact>]
    let ``direct calls alone still plan a new parameter`` () =
        let callNodeId = id<GraphNodeTag> "callDouble-call"
        match addParameter [ directCaller ] [ AgentCallPlan.LeaveMissing callNodeId ] with
        | Ok proposal ->
            let diff = (AgentChangeProposal.review proposal).ResultingChange.Diff
            Assert.Equal(targetId, (List.exactlyOne diff.ParametersAdded).OwnerFunctionId)
            Assert.Equal(callNodeId, (List.exactlyOne diff.ObligationsCreated).CallNodeId)
        | Error error -> failwithf "Direct-call-only addition was rejected: %A" error
