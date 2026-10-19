namespace Nyrwvy.Core.Tests

open System
open Nyrwvy.Core
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

    let private renderFocusAndReview (model: ViewerFocusModel) =
        let review: ViewerChangeReviewModel =
            { TransactionId = StableId.createForTesting<TransactionTag> "transaction-viewer-html"
              BaseSnapshotId = model.SnapshotId
              ProposedSnapshotId = model.SnapshotId
              Diff =
                { Renames = []
                  VisibilityChanges = []
                  ParametersAdded = []
                  ProductFieldsAdded = []
                  ProductFieldsBound = []
                  ProductFieldsRemoved = []
                  FunctionsRemoved = []
                  TypesRemoved = []
                  EffectsRemoved = []
                  CapabilitiesRemoved = []
                  CapabilityOperationsRemoved = []
                  CapabilityOperationsAdded = []
                  CapabilityImplementationsBound = []
                  ArgumentsAttached = []
                  ObligationsCreated = []
                  ObligationsResolved = []
                  FunctionRequiredEffectsChanged = []
                  ParametersRemoved = []
                  UnionVariantsRemoved = []
                  UnionVariantsAdded = []
                  UnionMatchArmsAdded = [] }
              Readiness = ChangeReviewReadiness.Complete
              AffectedFunctionIds = [ model.Function.FunctionId ]
              AffectedTypeIds = []
              FunctionFocus = [ model ] }
        [ ViewerHtml.renderFocusDocument model; ViewerHtml.renderChangeReviewDocument review ]

    let private assertDependencyList (items: string list) (document: string) =
        let expected =
            [ "<ul aria-label=\"value dependencies\">"
              yield! items |> List.map (fun item -> "<li>" + item + "</li>")
              "</ul>" ]
            |> String.concat "\n"
        let lines = document.Split('\n') |> Array.map (fun line -> line.Trim()) |> String.concat "\n"
        Assert.Contains(expected, lines)

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

        if not (List.contains (ViewerNodeRole.Operation "Conditional") roles) then
            failwith "Validation focus omitted conditional nodes."
        if not (List.contains (ViewerNodeRole.Operation "ResultOk") roles) then
            failwith "Validation focus omitted Result.Ok construction."
        if not (List.contains (ViewerNodeRole.Operation "ResultErr") roles) then
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
    let ``review projections preserve dependency roles order and multiplicity`` () =
        let floatType = TypeRef.Primitive PrimitiveType.Float64
        let functionId = StableId.createForTesting<FunctionTag> "function-viewer-dependency-roles"
        let snapshotId = StableId.createForTesting<SnapshotTag> "snapshot-viewer-dependency-roles"
        let leftId = StableId.createForTesting<GraphNodeTag> "node-viewer-dependency-left"
        let rightId = StableId.createForTesting<GraphNodeTag> "node-viewer-dependency-right"
        let subtractLeftRightId = StableId.createForTesting<GraphNodeTag> "node-viewer-subtract-left-right"
        let subtractRightLeftId = StableId.createForTesting<GraphNodeTag> "node-viewer-subtract-right-left"
        let listId = StableId.createForTesting<GraphNodeTag> "node-viewer-list-duplicate"

        let leftNode: SemanticNode =
            { Id = leftId
              Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 10.0) }
        let rightNode: SemanticNode =
            { Id = rightId
              Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 3.0) }
        let subtractLeftRight: SemanticNode =
            { Id = subtractLeftRightId
              Kind = SemanticNodeKind.Float64Subtract(leftId, rightId) }
        let subtractRightLeft: SemanticNode =
            { Id = subtractRightLeftId
              Kind = SemanticNodeKind.Float64Subtract(rightId, leftId) }
        let listNode: SemanticNode =
            { Id = listId
              Kind = SemanticNodeKind.ListConstruct(floatType, [ leftId; leftId ]) }

        let functionEntity: SemanticFunction =
            { Exported = true; Id = functionId
              TypeParameters = []
              DisplayName = "dependency_roles"
              Parameters = []
              ResultType = floatType
              RequiredEffects = []
              Body =
                { Nodes = [ leftNode; rightNode; subtractLeftRight; subtractRightLeft; listNode ]
                  ResultNodeId = subtractLeftRightId } }

        let snapshot: Snapshot =
            { Id = snapshotId
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ functionEntity ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        let model =
            SemanticQuery.focusFunction snapshot functionId
            |> unwrap
            |> ViewerProjection.focus

        let projected nodeId = model.Graph.Nodes |> List.find (fun node -> node.Id = nodeId)
        let expectedLeftRight =
            [ { Role = ValueDependencyRole.LeftOperand; NodeId = leftId }
              { Role = ValueDependencyRole.RightOperand; NodeId = rightId } ]
        let expectedRightLeft =
            [ { Role = ValueDependencyRole.LeftOperand; NodeId = rightId }
              { Role = ValueDependencyRole.RightOperand; NodeId = leftId } ]
        let expectedRepeatedList =
            [ { Role = ValueDependencyRole.ListItem 0; NodeId = leftId }
              { Role = ValueDependencyRole.ListItem 1; NodeId = leftId } ]

        if (projected subtractLeftRightId).ValueDependencies <> expectedLeftRight then
            failwithf "Viewer lost left/right dependency roles: %A" (projected subtractLeftRightId).ValueDependencies
        if (projected subtractRightLeftId).ValueDependencies <> expectedRightLeft then
            failwithf "Viewer lost swapped left/right dependency roles: %A" (projected subtractRightLeftId).ValueDependencies
        if (projected listId).ValueDependencies <> expectedRepeatedList then
            failwithf "Viewer lost repeated List dependency occurrences: %A" (projected listId).ValueDependencies

        let listDependencyRelations =
            (projected listId).Relations
            |> List.filter (fun relation -> relation.Kind = ViewerRelationKind.DependsOn)
        Assert.Equal(2, listDependencyRelations.Length)

        let summaries =
            StructuralTransactionInternals.summarizeNodes
                snapshot
                functionEntity
                [ subtractLeftRight; subtractRightLeft; listNode ]
        let summary nodeId = summaries |> List.find (fun node -> node.NodeId = nodeId)

        if (summary subtractLeftRightId).ValueDependencies <> expectedLeftRight then
            failwithf "Structural review lost left/right dependency roles: %A" (summary subtractLeftRightId).ValueDependencies
        if (summary subtractRightLeftId).ValueDependencies <> expectedRightLeft then
            failwithf "Structural review lost swapped left/right dependency roles: %A" (summary subtractRightLeftId).ValueDependencies
        if (summary listId).ValueDependencies <> expectedRepeatedList then
            failwithf "Structural review lost repeated List dependency occurrences: %A" (summary listId).ValueDependencies

        if (summary subtractLeftRightId).Dependencies <> [ leftId; rightId ] then
            failwithf "Structural dependency order changed: %A" (summary subtractLeftRightId).Dependencies
        if (summary subtractRightLeftId).Dependencies <> [ rightId; leftId ] then
            failwithf "Structural swapped dependency order changed: %A" (summary subtractRightLeftId).Dependencies
        if (summary listId).Dependencies <> [ leftId; leftId ] then
            failwithf "Structural dependency multiplicity was lost: %A" (summary listId).Dependencies

        let item label nodeId = label + ": <code>node:" + StableId.value nodeId + "</code>"
        for document in renderFocusAndReview model do
            assertDependencyList [ item "left operand" leftId; item "right operand" rightId ] document
            assertDependencyList [ item "left operand" rightId; item "right operand" leftId ] document
            assertDependencyList [ item "list item [0]" leftId; item "list item [1]" leftId ] document

        let swapped =
            { model with
                Graph =
                    { model.Graph with
                        Nodes =
                            model.Graph.Nodes
                            |> List.map (fun node ->
                                if node.Id = subtractLeftRightId then
                                    { node with ValueDependencies = expectedRightLeft }
                                else node) } }
        for original, reversed in List.zip (renderFocusAndReview model) (renderFocusAndReview swapped) do
            Assert.NotEqual<string>(original, reversed)

    [<Fact>]
    let ``focus and review HTML preserve keyed dependencies and escape their identifiers`` () =
        let fixture = MilestoneZeroGraph.create ()
        let model = focus fixture fixture.ValidateFunctionId
        let suffix = "<script>&\"'"
        let escapedSuffix = "&lt;script&gt;&amp;&quot;&#39;"
        let nodeId = StableId.createForTesting<GraphNodeTag> ("node-" + suffix)
        let parameterId = StableId.createForTesting<ParameterTag> ("parameter-" + suffix)
        let capabilityParameterId = StableId.createForTesting<CapabilityParameterTag> ("capability-parameter-" + suffix)
        let fieldId = StableId.createForTesting<FieldTag> ("field-" + suffix)
        let cases =
            [ ViewerNodeRole.Operation "DirectCall",
              [ ValueDependencyRole.CallArgument parameterId, "call argument parameter:parameter-" + escapedSuffix ]
              ViewerNodeRole.Operation "CapabilityInvoke",
              [ ValueDependencyRole.CapabilityValue, "capability value"
                ValueDependencyRole.CapabilityArgument capabilityParameterId,
                "capability argument capability-parameter:capability-parameter-" + escapedSuffix ]
              ViewerNodeRole.Operation "ProductConstruct",
              [ ValueDependencyRole.FieldValue fieldId, "field value field:field-" + escapedSuffix ]
              ViewerNodeRole.Operation "UnionConstruct",
              [ ValueDependencyRole.FieldValue fieldId, "field value field:field-" + escapedSuffix ] ]

        for role, dependencies in cases do
            let node: ViewerNode =
                { Id = StableId.createForTesting<GraphNodeTag> "node-viewer-keyed-dependencies"
                  Type = None
                  Role = role
                  Relations = []
                  ValueDependencies = dependencies |> List.map (fun (role, _) -> { Role = role; NodeId = nodeId })
                  Branches = [] }
            let keyedModel = { model with Graph = { Nodes = [ node ]; ResultNodeId = node.Id } }
            let documents = renderFocusAndReview keyedModel
            Assert.Equal<string list>(documents, renderFocusAndReview keyedModel)
            for document in documents do
                let expected =
                    dependencies
                    |> List.map (fun (_, label) -> label + ": <code>node:node-" + escapedSuffix + "</code>")
                assertDependencyList expected document
                Assert.DoesNotContain(suffix, document)
                Assert.DoesNotContain("<script", document)

    [<Fact>]
    let ``focus and review HTML are deterministic under nonsemantic root-node reordering`` () =
        let fixture = MilestoneZeroGraph.create ()
        let source = SemanticQuery.focusFunction fixture.Snapshot fixture.ValidateFunctionId |> unwrap
        let original = source |> ViewerProjection.focus |> renderFocusAndReview

        let reorderedFunction =
            { source.Function with
                Body = { source.Function.Body with Nodes = List.rev source.Function.Body.Nodes } }
        let reorderedSource = { source with Function = reorderedFunction }
        let reordered = reorderedSource |> ViewerProjection.focus |> renderFocusAndReview

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
    let ``focus HTML distinguishes required effects from referenced declarations`` () =
        let fixture = MilestoneZeroGraph.create ()
        let pureDocument = focus fixture fixture.ValidateFunctionId |> ViewerHtml.renderFocusDocument
        let effectfulDocument = focus fixture fixture.MainFunctionId |> ViewerHtml.renderFocusDocument
        let effectText = "<p>Declared required effects: <code>effect:" + StableId.value fixture.ConsoleEffectId + "</code></p>"

        Assert.Contains("<p>Declared required effects: None.</p>", pureDocument)
        Assert.Contains(effectText, effectfulDocument)
        let relatedSignatures =
            effectfulDocument.Substring(effectfulDocument.IndexOf("<details><summary>Related function signatures", StringComparison.Ordinal))
        Assert.Contains(effectText, relatedSignatures)

    [<Fact>]
    let ``change review HTML includes required effect additions and removals`` () =
        let fixture = MilestoneZeroGraph.create ()
        let effectRequest: FunctionRequiredEffectRequest =
            { TargetFunctionId = fixture.ValidateFunctionId
              EffectId = fixture.ConsoleEffectId }
        let simulate suffix (snapshot: Snapshot) operation =
            let request: TransactionRequest =
                { Id = StableId.createForTesting<TransactionTag> ("transaction-viewer-effect-" + suffix)
                  BaseSnapshotId = snapshot.Id
                  ProposedSnapshotId = StableId.createForTesting<SnapshotTag> ("snapshot-viewer-effect-" + suffix)
                  Operations = [ operation ] }
            match Transaction.simulate snapshot request with
            | Proposed value -> value
            | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

        let added = simulate "add" fixture.Snapshot (SemanticOperation.AddFunctionRequiredEffect effectRequest)
        let removed = simulate "remove" added.ProposedSnapshot (SemanticOperation.RemoveFunctionRequiredEffect effectRequest)
        for verb, simulation in [ "added", added; "removed", removed ] do
            Assert.Single(simulation.Diff.FunctionRequiredEffectsChanged) |> ignore
            let document =
                ChangeReviewQuery.project simulation
                |> unwrap
                |> ViewerProjection.changeReview
                |> ViewerHtml.renderChangeReviewDocument
            let expected =
                "<li><code>function:" + StableId.value fixture.ValidateFunctionId
                + "</code> " + verb + " required effect <code>effect:"
                + StableId.value fixture.ConsoleEffectId + "</code></li>"
            Assert.Contains("<h3>Required effects changed</h3>", document)
            Assert.Contains(expected, document)

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
                      NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
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

    [<Fact>]
    let ``every node kind is labelled from its name, established wordings kept`` () =
        let fixture = MilestoneZeroGraph.create ()
        let model = focus fixture fixture.ValidateFunctionId
        let render name =
            let node: ViewerNode =
                { Id = StableId.createForTesting<GraphNodeTag> "node-label-check"
                  Type = None
                  Role = ViewerNodeRole.Operation name
                  Relations = []
                  ValueDependencies = []
                  Branches = [] }
            ViewerHtml.renderFocusDocument { model with Graph = { Nodes = [ node ]; ResultNodeId = node.Id } }
        // A builtin added to SemanticNodeKind needs no viewer entry: its label derives from the kind name.
        for case in Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<SemanticNodeKind>) do
            let label = Text.RegularExpressions.Regex.Match(render case.Name, "<h4>([^<]*)</h4>").Groups[1].Value
            Assert.False(String.IsNullOrEmpty label, case.Name + " has no viewer label.")
            if label = case.Name && case.Name |> Seq.skip 1 |> Seq.exists Char.IsUpper then
                failwithf "%s is shown as its raw kind name." case.Name
        for name, label in
            [ "StringIsEmpty", "string is empty"
              "Int64WrappingAdd", "Int64 wrapping add"
              "Float64IsNaN", "Float64 is NaN"
              "ListMinBy", "List min by"
              "StringEncodeUtf8", "UTF-8 encode"
              "OptionNone", "Option.None"
              "ListFooBarBaz", "List foo bar baz" ] do
            Assert.Contains("<h4>" + label + "</h4>", render name)
