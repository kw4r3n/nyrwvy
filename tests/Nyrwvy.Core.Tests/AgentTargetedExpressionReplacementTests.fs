namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module AgentTargetedExpressionReplacementTests =
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-targeted-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-targeted-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-targeted-" + name)
    let private bindingId name = StableId.createForTesting<PatternBindingTag> ("binding-targeted-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-targeted-" + name)

    let private workspace (snapshot: Snapshot) =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private request (snapshot: Snapshot) functionId targetNodeId recipe: AgentExpressionStructuralWorkflow.Targeted.Request =
        { TargetFunctionId = functionId
          ExpectedBaseSnapshotId = snapshot.Id
          TargetNodeId = targetNodeId
          Replacement = recipe }

    let private simulate workspace targetedRequest =
        match AgentExpressionStructuralWorkflow.Targeted.simulate workspace targetedRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected targeted simulation failure: %A" error

    let private assertRejectedCode expected result =
        match result with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    type private SharedFixture =
        { Snapshot: Snapshot
          FunctionId: FunctionId
          TargetId: GraphNodeId
          ExclusiveDependencyId: GraphNodeId
          SharedDependencyId: GraphNodeId
          FirstConsumerId: GraphNodeId
          SecondConsumerId: GraphNodeId }

    let private sharedFixture () =
        let functionId = functionId "shared"
        let exclusiveId = nodeId "exclusive"
        let sharedId = nodeId "shared"
        let sharedConstId = nodeId "shared-const"
        let sharedConsumerId = nodeId "shared-consumer"
        let targetId = nodeId "target"
        let firstConstId = nodeId "first-const"
        let firstConsumerId = nodeId "first-consumer"
        let secondConstId = nodeId "second-const"
        let secondConsumerId = nodeId "second-consumer"
        let target: SemanticFunction =
            { Id = functionId
              DisplayName = "shared"
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = exclusiveId; Kind = SemanticNodeKind.Int64Literal 1L }
                      { Id = sharedId; Kind = SemanticNodeKind.Int64Literal 2L }
                      { Id = sharedConstId; Kind = SemanticNodeKind.Int64Literal 5L }
                      { Id = sharedConsumerId
                        Kind = SemanticNodeKind.Int64WrappingAdd(sharedId, sharedConstId) }
                      { Id = targetId
                        Kind = SemanticNodeKind.Int64WrappingAdd(exclusiveId, sharedId) }
                      { Id = firstConstId; Kind = SemanticNodeKind.Int64Literal 3L }
                      { Id = firstConsumerId
                        Kind = SemanticNodeKind.Int64Equal(targetId, firstConstId) }
                      { Id = secondConstId; Kind = SemanticNodeKind.Int64Literal 4L }
                      { Id = secondConsumerId
                        Kind = SemanticNodeKind.Int64LessThan(targetId, secondConstId) } ]
                  ResultNodeId = firstConsumerId } }
        let snapshot =
            { Snapshot.empty (snapshotId "shared") with
                Functions = [ target ] }
        Assert.Empty(SnapshotValidator.validate snapshot)
        { Snapshot = snapshot
          FunctionId = functionId
          TargetId = targetId
          ExclusiveDependencyId = exclusiveId
          SharedDependencyId = sharedId
          FirstConsumerId = firstConsumerId
          SecondConsumerId = secondConsumerId }

    [<Fact>]
    let ``targeted replacement retains identity updates all consumers and prunes only obsolete dependencies`` () =
        let fixture = sharedFixture ()
        let ws = workspace fixture.Snapshot
        let recipe =
            ExpressionRecipe.Int64WrappingAdd(
                ExpressionRecipe.Int64Literal 10L,
                ExpressionRecipe.Int64Literal 20L
            )
        let proposal =
            request fixture.Snapshot fixture.FunctionId fixture.TargetId recipe
            |> simulate ws
        let review = AgentExpressionStructuralWorkflow.Targeted.Proposal.review proposal

        Assert.Equal(fixture.TargetId, review.ResultingChange.TargetNodeId)
        Assert.Equal("Int64WrappingAdd", review.ResultingChange.PreviousTarget.Role)
        Assert.Equal(Some int64Type, review.ResultingChange.PreviousTarget.Type)
        Assert.Contains(review.ResultingChange.RemovedNodes, fun node -> node.NodeId = fixture.ExclusiveDependencyId)
        Assert.DoesNotContain(review.ResultingChange.RemovedNodes, fun node -> node.NodeId = fixture.SharedDependencyId)
        Assert.Contains(review.ResultingChange.RetainedSharedDependencies, fun node -> node.NodeId = fixture.SharedDependencyId)
        Assert.Equal(2, review.ResultingChange.AddedNodes.Length)
        Assert.Contains(
            AgentExpressionStructuralWorkflow.Targeted.Use.NodeDependency(
                fixture.FirstConsumerId,
                ValueDependencyRole.LeftOperand
            ),
            review.ResultingChange.AffectedUses
        )
        Assert.Contains(
            AgentExpressionStructuralWorkflow.Targeted.Use.NodeDependency(
                fixture.SecondConsumerId,
                ValueDependencyRole.LeftOperand
            ),
            review.ResultingChange.AffectedUses
        )

        match AgentExpressionStructuralWorkflow.Targeted.commit ws proposal with
        | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Rejected diagnostics ->
            failwithf "Targeted commit failed: %A" diagnostics
        | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Committed(next, publication) ->
            Assert.Equal(fixture.TargetId, publication.Diff.TargetNodeId)
            let published = AgentWorkspace.snapshotForStructuralEditing next
            Assert.Empty(SnapshotValidator.validate published)
            let target = published.Functions |> List.exactlyOne
            let nodes = ValueGraph.collectNodes target.Body
            Assert.DoesNotContain(nodes, fun node -> node.Id = fixture.ExclusiveDependencyId)
            Assert.Contains(nodes, fun node -> node.Id = fixture.SharedDependencyId)
            let replacement = nodes |> List.find (fun node -> node.Id = fixture.TargetId)
            match replacement.Kind with
            | SemanticNodeKind.Int64WrappingAdd(left, right) ->
                Assert.Contains(review.ResultingChange.AddedNodes, fun node -> node.NodeId = left)
                Assert.Contains(review.ResultingChange.AddedNodes, fun node -> node.NodeId = right)
            | other -> failwithf "Unexpected replacement: %A" other
            for consumerId in [ fixture.FirstConsumerId; fixture.SecondConsumerId ] do
                match (nodes |> List.find (fun node -> node.Id = consumerId)).Kind with
                | SemanticNodeKind.Int64Equal(left, _)
                | SemanticNodeKind.Int64LessThan(left, _) -> Assert.Equal(fixture.TargetId, left)
                | other -> failwithf "Unexpected consumer: %A" other

    type private NestedFixture =
        { Snapshot: Snapshot
          FunctionId: FunctionId
          BindId: GraphNodeId
          TargetId: GraphNodeId
          OuterBindingId: PatternBindingId
          OldReferenceId: GraphNodeId }

    let private nestedFixture () =
        let functionId = functionId "nested"
        let inputId = nodeId "nested-input"
        let bindId = nodeId "nested-bind"
        let oldReferenceId = nodeId "nested-reference"
        let targetId = nodeId "nested-target"
        let binding: PatternBinding =
            { Id = bindingId "outer"
              DisplayName = None
              Type = stringType }
        let body: ValueGraph =
            { Nodes =
                [ { Id = oldReferenceId
                    Kind = SemanticNodeKind.PatternReference binding.Id }
                  { Id = targetId
                    Kind = SemanticNodeKind.StringIsEmpty oldReferenceId } ]
              ResultNodeId = targetId }
        let target: SemanticFunction =
            { Id = functionId
              DisplayName = "nested"
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = inputId; Kind = SemanticNodeKind.StringLiteral "outer" }
                      { Id = bindId
                        Kind =
                            SemanticNodeKind.Bind(
                                inputId,
                                { ValueBinding = binding
                                  Body = body }
                            ) } ]
                  ResultNodeId = bindId } }
        let snapshot =
            { Snapshot.empty (snapshotId "nested") with
                Functions = [ target ] }
        Assert.Empty(SnapshotValidator.validate snapshot)
        { Snapshot = snapshot
          FunctionId = functionId
          BindId = bindId
          TargetId = targetId
          OuterBindingId = binding.Id
          OldReferenceId = oldReferenceId }

    let private value depth =
        ExpressionRecipe.PatternReference
            { ScopeDepth = depth
              Slot = RecipePatternSlot.Value }

    [<Fact>]
    let ``targeted replacement reconstructs nested lexical frames and rejects invalid scope or type`` () =
        let fixture = nestedFixture ()
        let ws = workspace fixture.Snapshot
        let recipe =
            ExpressionRecipe.Bind(
                ExpressionRecipe.StringLiteral "inner",
                ExpressionRecipe.StringContains(value 1, value 0)
            )
        let proposal =
            request fixture.Snapshot fixture.FunctionId fixture.TargetId recipe
            |> simulate ws
        let review = AgentExpressionStructuralWorkflow.Targeted.Proposal.review proposal
        Assert.Single(review.ResultingChange.AddedPatternBindings) |> ignore
        Assert.Contains(review.ResultingChange.RemovedNodes, fun node -> node.NodeId = fixture.OldReferenceId)
        Assert.Contains(
            AgentExpressionStructuralWorkflow.Targeted.Use.GraphResult(Some fixture.BindId),
            review.ResultingChange.AffectedUses
        )

        match AgentExpressionStructuralWorkflow.Targeted.commit ws proposal with
        | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Rejected diagnostics ->
            failwithf "Targeted commit failed: %A" diagnostics
        | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Committed(next, _) ->
            let published = AgentWorkspace.snapshotForStructuralEditing next
            let target = published.Functions |> List.exactlyOne
            let nodes = ValueGraph.collectNodes target.Body
            let replacement = nodes |> List.find (fun node -> node.Id = fixture.TargetId)
            match replacement.Kind with
            | SemanticNodeKind.Bind(_, innerBody) ->
                let references =
                    ValueGraph.collectNodes innerBody.Body
                    |> List.choose (fun node ->
                        match node.Kind with
                        | SemanticNodeKind.PatternReference id -> Some id
                        | _ -> None)
                Assert.Contains(fixture.OuterBindingId, references)
                Assert.Contains(innerBody.ValueBinding.Id, references)
            | other -> failwithf "Unexpected replacement: %A" other

        request fixture.Snapshot fixture.FunctionId fixture.TargetId (value 1)
        |> AgentExpressionStructuralWorkflow.Targeted.simulate ws
        |> assertRejectedCode "PureExpressionPatternScopeNotFound"

        request fixture.Snapshot fixture.FunctionId fixture.TargetId (ExpressionRecipe.StringLiteral "wrong")
        |> AgentExpressionStructuralWorkflow.Targeted.simulate ws
        |> assertRejectedCode "TargetedExpressionTypeMismatch"

    let private obligationFixture resolved =
        let calleeId = functionId "obligation-callee"
        let calleeParameterId = parameterId "obligation-callee-value"
        let calleeResultId = nodeId "obligation-callee-result"
        let callee: SemanticFunction =
            { Id = calleeId
              DisplayName = "callee"
              Parameters =
                [ { Id = calleeParameterId
                    DisplayName = "value"
                    Type = float64Type } ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = calleeResultId
                        Kind = SemanticNodeKind.ParameterReference calleeParameterId } ]
                  ResultNodeId = calleeResultId } }
        let targetId = functionId "obligation-target"
        let callId = nodeId "obligation-call"
        let valueId = nodeId "obligation-value"
        let arguments: ArgumentBinding list =
            if resolved then
                [ { TargetParameterId = calleeParameterId
                    ValueNodeId = valueId } ]
            else
                []
        let bodyNodes: SemanticNode list =
            (if resolved then
                [ { Id = valueId
                    Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 4.0) } ]
             else
                [])
            @ [ { Id = callId
                  Kind = SemanticNodeKind.DirectCall(calleeId, arguments) } ]
        let target: SemanticFunction =
            { Id = targetId
              DisplayName = "target"
              Parameters = []
              ResultType = float64Type
              RequiredEffects = []
              Body = { Nodes = bodyNodes; ResultNodeId = callId } }
        let origin = StableId.createForTesting<TransactionTag> "transaction-targeted-obligation"
        let status =
            if resolved then
                ObligationStatus.Resolved
                    { TransactionId = origin
                      ValueNodeId = valueId }
            else
                ObligationStatus.Open
        let obligation: MissingArgumentObligation =
            { Id = StableId.createForTesting<ObligationTag>
                       (if resolved then "obligation-targeted-resolved" else "obligation-targeted-open")
              OriginTransactionId = origin
              CallNodeId = callId
              ParameterId = calleeParameterId
              Status = status }
        let snapshot =
            { Snapshot.empty (snapshotId (if resolved then "obligation-resolved" else "obligation-open")) with
                Functions = [ target; callee ]
                Obligations = [ obligation ] }
        Assert.Empty(SnapshotValidator.validate snapshot)
        snapshot, targetId, callId

    [<Fact>]
    let ``targeted replacement protects open and resolved obligations on the retained target identity`` () =
        for resolved in [ false; true ] do
            let snapshot, targetFunctionId, targetNodeId = obligationFixture resolved
            let ws = workspace snapshot
            request
                snapshot
                targetFunctionId
                targetNodeId
                (ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 5.0))
            |> AgentExpressionStructuralWorkflow.Targeted.simulate ws
            |> assertRejectedCode "TargetedExpressionReferencedByObligation"

    [<Fact>]
    let ``targeted proposal rejects stale publication after workspace advance`` () =
        let fixture = sharedFixture ()
        let ws = workspace fixture.Snapshot
        let proposal =
            request
                fixture.Snapshot
                fixture.FunctionId
                fixture.TargetId
                (ExpressionRecipe.Int64Literal 9L)
            |> simulate ws
        let advance =
            AgentWorkflow.simulate
                ws
                [ AgentOperationIntent.RenameDisplayName(
                    AgentRenameTarget.Function fixture.FunctionId,
                    "shared_advanced"
                  ) ]
            |> function
                | Ok value -> value
                | Error error -> failwithf "Advance simulation failed: %A" error
        let advanced =
            match AgentWorkflow.commit ws advance with
            | AgentCommitResult.Committed(next, _) -> next
            | AgentCommitResult.Rejected diagnostics -> failwithf "Advance commit failed: %A" diagnostics

        match AgentExpressionStructuralWorkflow.Targeted.commit advanced proposal with
        | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Rejected [ diagnostic ]
            when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | other -> failwithf "Expected stale targeted rejection, got %A" other
