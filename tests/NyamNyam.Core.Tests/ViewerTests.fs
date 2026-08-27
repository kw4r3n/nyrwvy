namespace NyamNyam.Core.Tests

open System
open NyamNyam.Core
open Xunit

module ViewerTests =
    let private unwrap result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "Unexpected query error: %A" error

    let private focus (fixture: MilestoneZeroFixture) functionId =
        SemanticQuery.focusFunction fixture.Snapshot functionId
        |> unwrap
        |> ViewerProjection.focus

    let rec private collectNodes (graph: ViewerGraph): ViewerNode list =
        graph.Nodes
        |> List.collect (fun node ->
            node
            :: (node.Branches
                |> List.collect (fun branch -> collectNodes branch.Graph)))

    let rec private collectBranchRoles (graph: ViewerGraph): ViewerBranchRole list =
        graph.Nodes
        |> List.collect (fun node ->
            node.Branches
            |> List.collect (fun branch ->
                branch.Role :: collectBranchRoles branch.Graph))

    [<Fact>]
    let ``focus projection preserves structured conditional and Result paths`` () =
        let fixture = MilestoneZeroGraph.create ()
        let model = focus fixture fixture.ValidateFunctionId
        let nodes = collectNodes model.Graph
        let roles = nodes |> List.map (fun node -> node.Role)
        let branches = collectBranchRoles model.Graph

        if not (List.contains ViewerNodeRole.Conditional roles) then
            failwith "Validation focus omitted conditional nodes."
        if not (List.contains ViewerNodeRole.ResultOk roles) then
            failwith "Validation focus omitted Result.Ok construction."
        if not (List.contains ViewerNodeRole.ResultErr roles) then
            failwith "Validation focus omitted Result.Err construction."
        if not (List.contains ViewerBranchRole.WhenTrue branches) then
            failwith "Validation focus omitted the true branch label."
        if not (List.contains ViewerBranchRole.WhenFalse branches) then
            failwith "Validation focus omitted the false branch label."

    [<Fact>]
    let ``report focus exposes capability invocation and exhaustive match paths`` () =
        let fixture = MilestoneZeroGraph.create ()
        let model = focus fixture fixture.ReportFunctionId
        let nodes = collectNodes model.Graph
        let branches = collectBranchRoles model.Graph

        let invokesConsole =
            nodes
            |> List.collect (fun node -> node.Relations)
            |> List.exists (fun relation ->
                relation.Kind = ViewerRelationKind.InvokesCapabilityOperation
                && relation.Target = ViewerTarget.CapabilityOperation fixture.ConsoleWriteLineOperationId)

        if not invokesConsole then
            failwith "Report focus omitted the typed Console capability operation path."
        if not (List.contains ViewerBranchRole.ResultOk branches) then
            failwith "Report focus omitted the Result.Ok match path."
        if not (List.contains ViewerBranchRole.ResultErr branches) then
            failwith "Report focus omitted the Result.Err match path."

        let unionBranches =
            branches
            |> List.choose (function
                | ViewerBranchRole.UnionVariant variantId -> Some variantId
                | _ -> None)
        if unionBranches.Length <> 2 then
            failwithf "Expected two explicit ValidationError variant branches, got %A" unionBranches

    [<Fact>]
    let ``focus HTML is deterministic under nonsemantic root-node reordering`` () =
        let fixture = MilestoneZeroGraph.create ()
        let source = SemanticQuery.focusFunction fixture.Snapshot fixture.ValidateFunctionId |> unwrap
        let original = source |> ViewerProjection.focus |> ViewerHtml.renderFocusDocument

        let reorderedFunction =
            { source.Function with
                Body = { source.Function.Body with Nodes = List.rev source.Function.Body.Nodes } }
        let reorderedSource = { source with Function = reorderedFunction }
        let reordered = reorderedSource |> ViewerProjection.focus |> ViewerHtml.renderFocusDocument

        if original <> reordered then
            failwith "Viewer output changed when only nonsemantic root node serialization order changed."

    [<Fact>]
    let ``viewer HTML escapes display metadata and emits no script execution surface`` () =
        let fixture = MilestoneZeroGraph.create ()
        let maliciousName = "<script>alert('viewer')</script>&"
        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id = fixture.ValidateFunctionId then
                    { functionEntity with DisplayName = maliciousName }
                else
                    functionEntity)
        let snapshot = { fixture.Snapshot with Functions = functions }
        let source = SemanticQuery.focusFunction snapshot fixture.ValidateFunctionId |> unwrap
        let document = source |> ViewerProjection.focus |> ViewerHtml.renderFocusDocument

        if document.Contains(maliciousName, StringComparison.Ordinal) then
            failwith "Viewer emitted unescaped display metadata."
        if not (document.Contains("&lt;script&gt;", StringComparison.Ordinal)) then
            failwith "Viewer did not preserve malicious display metadata as escaped text."
        if document.Contains("<script", StringComparison.OrdinalIgnoreCase) then
            failwith "Viewer unexpectedly emitted a script element."
        if document.Contains("innerHTML", StringComparison.OrdinalIgnoreCase) then
            failwith "Viewer unexpectedly relies on innerHTML."

    [<Fact>]
    let ``focus HTML exposes stable IDs and textual branch labels`` () =
        let fixture = MilestoneZeroGraph.create ()
        let model = focus fixture fixture.ValidateFunctionId
        let document = ViewerHtml.renderFocusDocument model

        for expected in
            [ StableId.value fixture.ValidateFunctionId
              StableId.value model.Graph.ResultNodeId
              "Branch: when true"
              "Branch: when false" ] do
            if not (document.Contains(expected, StringComparison.Ordinal)) then
                failwithf "Viewer document omitted inspectable semantic information: %s" expected

    [<Fact>]
    let ``change review HTML exposes incomplete readiness diff and blocking obligation`` () =
        let fixture = MilestoneZeroGraph.create ()
        let newParameterId = StableId.createForTesting<ParameterTag> "parameter-viewer-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-viewer-policy"
        let transactionId = StableId.createForTesting<TransactionTag> "transaction-viewer-policy"
        let proposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-viewer-policy"

        let request: TransactionRequest =
            { Id = transactionId
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = proposedSnapshotId
              Operations =
                [ SemanticOperation.AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = newParameterId
                      NewParameterDisplayName = "policy_for_viewer"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let simulation =
            match Transaction.simulate fixture.Snapshot request with
            | Proposed value -> value
            | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

        let review = ChangeReviewQuery.project simulation |> unwrap
        let document = review |> ViewerProjection.changeReview |> ViewerHtml.renderChangeReviewDocument

        for expected in
            [ "Readiness: incomplete"
              StableId.value fixture.Snapshot.Id
              StableId.value proposedSnapshotId
              StableId.value obligationId
              "Semantic change summary"
              "Blocking obligations" ] do
            if not (document.Contains(expected, StringComparison.Ordinal)) then
                failwithf "Change Review document omitted expected evidence: %s" expected
