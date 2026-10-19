namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module AgentExpressionCapabilityTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private ioEffectId = StableId.createForTesting<EffectTag> "effect-expression-io"
    let private remoteEffectId = StableId.createForTesting<EffectTag> "effect-expression-remote"
    let private capabilityId = StableId.createForTesting<CapabilityTag> "capability-expression-output"
    let private otherCapabilityId = StableId.createForTesting<CapabilityTag> "capability-expression-other"
    let private operationId = StableId.createForTesting<CapabilityOperationTag> "operation-expression-format"
    // Declaration order deliberately differs from stable-ID sort order.
    let private textId = StableId.createForTesting<CapabilityParameterTag> "capability-parameter-expression-z-text"
    let private countId = StableId.createForTesting<CapabilityParameterTag> "capability-parameter-expression-a-count"
    let private callerId = StableId.createForTesting<FunctionTag> "function-expression-caller"
    let private providerId = StableId.createForTesting<FunctionTag> "function-expression-provider"
    let private argumentId = StableId.createForTesting<FunctionTag> "function-expression-argument"
    let private receiverId = StableId.createForTesting<ParameterTag> "parameter-expression-receiver"
    let private otherReceiverId = StableId.createForTesting<ParameterTag> "parameter-expression-other-receiver"
    let private providerParameterId = StableId.createForTesting<ParameterTag> "parameter-expression-provider"
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-expression-" + name)

    let private createSnapshot callerEffects =
        let operation: SemanticCapabilityOperation =
            { Id = operationId
              DisplayName = "format"
              Parameters =
                [ { Id = textId; DisplayName = "text"; Type = stringType }
                  { Id = countId; DisplayName = "count"; Type = int64Type } ]
              ResultType = stringType
              FailureType = None
              EffectId = ioEffectId }
        let functionWithBody id name parameters resultType effects kind: SemanticFunction =
            let resultNodeId = nodeId name
            { Exported = true; Id = id
              DisplayName = name
              Parameters = parameters
              ResultType = resultType
              RequiredEffects = effects
              Body =
                { Nodes = [ { Id = resultNodeId; Kind = kind } ]
                  ResultNodeId = resultNodeId } }
        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-expression-capability"
              ParentId = None
              Types = []
              Effects =
                [ { Exported = true; Id = ioEffectId; DisplayName = "IO" }
                  { Exported = true; Id = remoteEffectId; DisplayName = "Remote" } ]
              Capabilities =
                [ { Exported = true; Id = capabilityId; DisplayName = "Output"; Operations = [ operation ] }
                  { Exported = true; Id = otherCapabilityId; DisplayName = "Other"; Operations = [] } ]
              Functions =
                [ functionWithBody callerId "caller"
                      [ { Id = receiverId; DisplayName = "receiver"; Type = TypeRef.Capability capabilityId }
                        { Id = otherReceiverId; DisplayName = "other"; Type = TypeRef.Capability otherCapabilityId } ]
                      stringType callerEffects (SemanticNodeKind.StringLiteral "old")
                  functionWithBody providerId "provider"
                      [ { Id = providerParameterId; DisplayName = "value"; Type = TypeRef.Capability capabilityId } ]
                      (TypeRef.Capability capabilityId) [ remoteEffectId ]
                      (SemanticNodeKind.ParameterReference providerParameterId)
                  functionWithBody argumentId "argument" [] stringType [ remoteEffectId ]
                      (SemanticNodeKind.StringLiteral "message") ]
              Obligations = [] }
        Assert.Empty(SnapshotValidator.validate snapshot)
        snapshot

    let private caller (snapshot: Snapshot) =
        snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = callerId)

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Unexpected fixture failure: %A" issues

    let private request (snapshot: Snapshot) recipe: AgentReplaceFunctionBodyWithExpressionRecipe =
        { TargetFunctionId = callerId
          ExpectedBaseSnapshotId = snapshot.Id
          ExpectedCurrentResultNodeId = (caller snapshot).Body.ResultNodeId
          Replacement = recipe }

    let private publish snapshot recipe =
        let workspace = workspace snapshot
        let proposal =
            match AgentExpressionStructuralWorkflow.simulate workspace (request snapshot recipe) with
            | Ok proposal -> proposal
            | Error error -> failwithf "Unexpected simulation failure: %A" error
        let review = AgentExpressionStructuralChangeProposal.review proposal
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        match AgentExpressionStructuralWorkflow.commit workspace proposal with
        | AgentStructuralCommitResult.Committed(next, _) -> review, AgentWorkspace.snapshotForStructuralEditing next
        | other -> failwithf "Unexpected publication failure: %A" other

    let private lower (snapshot: Snapshot) =
        match TypedSemanticIr.project snapshot with
        | Ok program -> ExecutionIr.lower program
        | Error error -> failwithf "Unexpected typed projection failure: %A" error

    let private arguments =
        [ countId, ExpressionRecipe.Int64Literal 2L
          textId, ExpressionRecipe.StringLiteral "message" ]

    let private invoke receiver bindings = ExpressionRecipe.CapabilityInvoke(receiver, operationId, bindings)
    let private receiver = ExpressionRecipe.Parameter receiverId
    let private suppliedReceiver =
        ExpressionRecipe.DirectCall(providerId, [ providerParameterId, receiver ])

    let private assertRejected snapshot expected recipe =
        let workspace = workspace snapshot
        match AgentExpressionStructuralWorkflow.simulate workspace (request snapshot recipe) with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected ->
            Assert.Equal<Snapshot>(snapshot, AgentWorkspace.snapshotForStructuralEditing workspace)
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``CapabilityInvoke materialization preserves declaration order and existing execution-readiness boundaries`` () =
        let snapshot = createSnapshot [ ioEffectId; remoteEffectId ]
        let recipe =
            invoke suppliedReceiver
                [ countId, ExpressionRecipe.Int64Literal 2L
                  textId, ExpressionRecipe.DirectCall(argumentId, []) ]
        // Construction order is not execution order: the provider and argument calls are
        // independent effects, so the proposal is refused before publication.
        let rejectedWorkspace = workspace snapshot
        match AgentExpressionStructuralWorkflow.simulate rejectedWorkspace (request snapshot recipe) with
        | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics) ->
            let text = sprintf "%A" diagnostics
            Assert.Contains("AmbiguousEffectOrder", text)
            Assert.Contains(StableId.value callerId, text)
        | other -> failwithf "Expected the ambiguous-effect boundary at proposal time, got %A" other
        Assert.Equal<Snapshot>(snapshot, AgentWorkspace.snapshotForStructuralEditing rejectedWorkspace)

        // Materialization keeps the receiver first and the operation's declaration order.
        let review, published =
            publish snapshot (invoke receiver [ countId, ExpressionRecipe.Int64Literal 2L; textId, ExpressionRecipe.DirectCall(argumentId, []) ])
        Assert.Equal<string list>(
            [ "ParameterReference"; "DirectCall"; "Int64Literal"; "CapabilityInvoke" ],
            review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role)
        )
        let target = caller published
        match target.Body.Nodes with
        | [ parameter; argument; count; invocation ] ->
            Assert.Equal(SemanticNodeKind.ParameterReference receiverId, parameter.Kind)
            Assert.Equal(SemanticNodeKind.DirectCall(argumentId, []), argument.Kind)
            Assert.Equal(SemanticNodeKind.Int64Literal 2L, count.Kind)
            Assert.Equal(
                SemanticNodeKind.CapabilityInvoke(
                    parameter.Id,
                    operationId,
                    [ { TargetCapabilityParameterId = textId; ValueNodeId = argument.Id }
                      { TargetCapabilityParameterId = countId; ValueNodeId = count.Id } ]
                ),
                invocation.Kind
            )
        | other -> failwithf "Unexpected materialized body: %A" other
        Assert.Equal<SemanticFunction>(caller snapshot, { target with Body = (caller snapshot).Body })
        Assert.Equal<SemanticFunction list>(
            snapshot.Functions |> List.filter (fun functionEntity -> functionEntity.Id <> callerId),
            published.Functions |> List.filter (fun functionEntity -> functionEntity.Id <> callerId)
        )
        Assert.Equal<SemanticType list>(snapshot.Types, published.Types)
        Assert.Equal<SemanticEffect list>(snapshot.Effects, published.Effects)
        Assert.Equal<SemanticCapability list>(snapshot.Capabilities, published.Capabilities)
        Assert.Equal<MissingArgumentObligation list>(snapshot.Obligations, published.Obligations)

        // One effectful receiver or argument is ordered before the invocation by dependency.
        let orderedRecipes =
            [ invoke suppliedReceiver arguments
              invoke receiver [ countId, ExpressionRecipe.Int64Literal 2L; textId, ExpressionRecipe.DirectCall(argumentId, []) ] ]
        for orderedRecipe in orderedRecipes do
            let _, orderedSnapshot = publish snapshot orderedRecipe
            match lower orderedSnapshot with
            | Ok _ -> ()
            | Error error -> failwithf "Dependency-ordered invocation did not lower: %A" error

    [<Fact>]
    let ``CapabilityInvoke uses a lexical capability value and stays inside its match arm`` () =
        let snapshot = createSnapshot [ ioEffectId ]
        let recipe =
            ExpressionRecipe.MatchOption(
                ExpressionRecipe.OptionSome receiver,
                invoke (ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }) arguments,
                ExpressionRecipe.StringLiteral "none"
            )
        let review, published = publish snapshot recipe
        Assert.Contains("CapabilityInvoke", review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role))
        match (caller published).Body.Nodes with
        | [ _; _; { Kind = SemanticNodeKind.MatchOption(_, someArm, _) } ] ->
            let patternNode = List.head someArm.Body.Nodes
            Assert.Equal(SemanticNodeKind.PatternReference someArm.ValueBinding.Id, patternNode.Kind)
            match (List.last someArm.Body.Nodes).Kind with
            | SemanticNodeKind.CapabilityInvoke(valueNodeId, actualOperationId, _) ->
                Assert.Equal(patternNode.Id, valueNodeId)
                Assert.Equal(operationId, actualOperationId)
            | other -> failwithf "Expected branch-local invocation, got %A" other
        | other -> failwithf "Invocation escaped its match arm: %A" other
        match lower published with
        | Ok _ -> ()
        | Error error -> failwithf "Branch-local invocation did not lower: %A" error

    [<Fact>]
    let ``CapabilityInvoke preflight requires an owned receiver and exact typed argument bindings`` () =
        let snapshot = createSnapshot [ ioEffectId ]
        let missingOperation = StableId.createForTesting<CapabilityOperationTag> "operation-expression-missing"
        let foreignParameter = StableId.createForTesting<CapabilityParameterTag> "capability-parameter-expression-foreign"
        let cases =
            [ ExpressionRecipe.CapabilityInvoke(receiver, missingOperation, arguments), "ExpressionCapabilityOperationNotFound"
              invoke (ExpressionRecipe.StringLiteral "not a capability") arguments, "ExpressionCapabilityValueTypeMismatch"
              invoke (ExpressionRecipe.Parameter otherReceiverId) arguments, "ExpressionCapabilityValueTypeMismatch"
              invoke receiver ((textId, ExpressionRecipe.StringLiteral "duplicate") :: arguments), "ExpressionDuplicateCapabilityArgument"
              invoke receiver [ textId, ExpressionRecipe.StringLiteral "message" ], "ExpressionCapabilityArgumentSetMismatch"
              invoke receiver ((foreignParameter, ExpressionRecipe.Int64Literal 1L) :: arguments), "ExpressionCapabilityArgumentSetMismatch"
              invoke receiver [ textId, ExpressionRecipe.Int64Literal 1L; countId, ExpressionRecipe.Int64Literal 2L ], "ExpressionCapabilityArgumentTypeMismatch" ]
        for recipe, code in cases do
            assertRejected snapshot code recipe

    [<Fact>]
    let ``effect preflight includes untaken branches receiver expressions and argument expressions`` () =
        let noEffects = createSnapshot []
        let onlyIo = createSnapshot [ ioEffectId ]
        let invocation = invoke receiver arguments
        assertRejected noEffects "ExpressionCapabilityMissingEffectRequirement" invocation
        // The diagnostic names the operation and effect by display name next to their IDs.
        match AgentExpressionStructuralWorkflow.simulate (workspace noEffects) (request noEffects invocation) with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Contains($"operation 'format' ({StableId.value operationId}) requires effect 'IO' ({StableId.value ioEffectId})", diagnostic.Message)
        | other -> failwithf "Expected a rejection, got %A" other
        assertRejected noEffects "ExpressionCapabilityMissingEffectRequirement"
            (ExpressionRecipe.Conditional(ExpressionRecipe.BoolLiteral false, invocation, ExpressionRecipe.StringLiteral "none"))
        assertRejected onlyIo "ExpressionCallMissingEffectRequirement" (invoke suppliedReceiver arguments)
        assertRejected onlyIo "ExpressionCallMissingEffectRequirement"
            (invoke receiver [ countId, ExpressionRecipe.Int64Literal 2L; textId, ExpressionRecipe.DirectCall(argumentId, []) ])
