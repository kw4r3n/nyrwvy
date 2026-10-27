namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open System.Text.Json.Nodes
open Xunit

module AgentExpressionBindTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private targetId = StableId.createForTesting<FunctionTag> "function-bind-target"
    let private consoleParameterId = StableId.createForTesting<ParameterTag> "parameter-bind-console"
    let private oldResultId = StableId.createForTesting<GraphNodeTag> "node-bind-old-result"
    let private providerId = StableId.createForTesting<FunctionTag> "function-bind-provider"
    let private providerParameterId = StableId.createForTesting<ParameterTag> "parameter-bind-provider"
    let private messageId = StableId.createForTesting<FunctionTag> "function-bind-message"
    let private messageParameterId = StableId.createForTesting<ParameterTag> "parameter-bind-message"

    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private createSnapshot () =
        let fixture = MilestoneZeroGraph.create ()
        let target: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = targetId
              TypeParameters = []
              DisplayName = "bound"
              Parameters =
                [ { Id = consoleParameterId
                    DisplayName = "console"
                    Type = TypeRef.Capability fixture.ConsoleCapabilityId } ]
              ResultType = stringType
              RequiredEffects = [ fixture.ConsoleEffectId ]
              Body =
                { Nodes = [ { Id = oldResultId; Kind = SemanticNodeKind.StringLiteral "old" } ]
                  ResultNodeId = oldResultId } }
        let provider: SemanticFunction =
            { target with
                Id = providerId
                Parameters = [ { List.head target.Parameters with Id = providerParameterId } ]
                ResultType = TypeRef.Capability fixture.ConsoleCapabilityId
                Body =
                    let id = StableId.createForTesting<GraphNodeTag> "node-bind-provider-old"
                    { Nodes = [ { Id = id; Kind = SemanticNodeKind.ParameterReference providerParameterId } ]
                      ResultNodeId = id } }
        let message: SemanticFunction =
            { target with
                Id = messageId
                Parameters = [ { List.head target.Parameters with Id = messageParameterId } ]
                Body =
                    let id = StableId.createForTesting<GraphNodeTag> "node-bind-message-old"
                    { Nodes = [ { Id = id; Kind = SemanticNodeKind.StringLiteral "payload" } ]
                      ResultNodeId = id } }
        { fixture.Snapshot with Functions = [ target; provider; message ]; Obligations = [] }

    let private target (snapshot: Snapshot) = snapshot.Functions |> List.find (fun fn -> fn.Id = targetId)

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private request (snapshot: Snapshot) recipe: AgentReplaceFunctionBodyWithExpressionRecipe =
        { TargetFunctionId = targetId
          ExpectedBaseSnapshotId = snapshot.Id
          ExpectedCurrentResultNodeId = (target snapshot).Body.ResultNodeId
          Replacement = recipe }

    let private publishFunction snapshot functionId recipe =
        let ws = workspace snapshot
        let functionEntity = snapshot.Functions |> List.find (fun fn -> fn.Id = functionId)
        let request: AgentReplaceFunctionBodyWithExpressionRecipe =
            { TargetFunctionId = functionId
              ExpectedBaseSnapshotId = snapshot.Id
              ExpectedCurrentResultNodeId = functionEntity.Body.ResultNodeId
              Replacement = recipe }
        let proposal =
            match AgentExpressionStructuralWorkflow.simulate ws request with
            | Ok value -> value
            | Error error -> failwithf "Simulation failed: %A" error
        let review = AgentExpressionStructuralChangeProposal.review proposal
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        match AgentExpressionStructuralWorkflow.commit ws proposal with
        | AgentStructuralCommitResult.Committed(next, _) ->
            review, AgentWorkspace.snapshotForStructuralEditing next
        | other -> failwithf "Commit failed: %A" other

    let private publish snapshot recipe = publishFunction snapshot targetId recipe

    let private lower snapshot = snapshot |> TypedSemanticIr.project |> unwrap |> ExecutionIr.lower

    let private write (snapshot: Snapshot) receiver message =
        let operation = snapshot.Capabilities |> List.exactlyOne |> fun cap -> List.exactlyOne cap.Operations
        let parameterId = (List.exactlyOne operation.Parameters).Id
        ExpressionRecipe.CapabilityInvoke(receiver, operation.Id, [ parameterId, message ])

    let private console = ExpressionRecipe.Parameter consoleParameterId
    let private acquire = ExpressionRecipe.DirectCall(providerId, [ providerParameterId, console ])
    let private message = ExpressionRecipe.DirectCall(messageId, [ messageParameterId, console ])

    let private value depth =
        ExpressionRecipe.PatternReference { ScopeDepth = depth; Slot = RecipePatternSlot.Value }

    [<Fact>]
    let ``Bind reuses an immutable value across nested scopes and empty match frames`` () =
        let recipe =
            ExpressionRecipe.Bind(
                ExpressionRecipe.StringLiteral "once",
                ExpressionRecipe.Bind(
                    ExpressionRecipe.UnitLiteral,
                    ExpressionRecipe.MatchOption(
                        ExpressionRecipe.OptionNone stringType,
                        value 2,
                        value 2
                    )
                )
            )
        let review, snapshot = publish (createSnapshot ()) recipe
        Assert.Equal<string list>(
            [ "StringLiteral"; "Bind"; "UnitLiteral"; "Bind"; "OptionNone"; "MatchOption"; "PatternReference"; "PatternReference" ],
            review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role)
        )
        Assert.Empty(SnapshotValidator.validate snapshot)
        lower snapshot |> unwrap |> ignore
        let _, outerInput = publish (createSnapshot ()) (ExpressionRecipe.Bind(ExpressionRecipe.StringLiteral "outer", ExpressionRecipe.Bind(value 0, value 0)))
        lower outerInput |> unwrap |> ignore

    [<Fact>]
    let ``Bind rejects a reference to its own initializer binding before publication`` () =
        let snapshot = createSnapshot ()
        let ws = workspace snapshot
        let recipe = ExpressionRecipe.Bind(value 0, ExpressionRecipe.StringLiteral "body")
        match AgentExpressionStructuralWorkflow.simulate ws (request snapshot recipe) with
        | Error(AgentStructuralWorkflowError.SimulationRejected _) ->
            Assert.Equal<Snapshot>(snapshot, AgentWorkspace.snapshotForStructuralEditing ws)
        | other -> failwithf "Expected scope rejection, got %A" other

    [<Fact>]
    let ``Bind orders independent receiver and argument effects but preserves inner and sibling ambiguity`` () =
        let snapshot = createSnapshot ()
        let doneValue = ExpressionRecipe.StringLiteral "done"
        let unorderedCall = write snapshot acquire message
        let ambiguous =
            [ ExpressionRecipe.Bind(unorderedCall, doneValue)
              ExpressionRecipe.Bind(ExpressionRecipe.UnitLiteral, ExpressionRecipe.Bind(unorderedCall, doneValue))
              ExpressionRecipe.StringContains(
                  ExpressionRecipe.Bind(write snapshot console (ExpressionRecipe.StringLiteral "first"), doneValue),
                  message
              ) |> fun condition -> ExpressionRecipe.Conditional(condition, doneValue, doneValue) ]
        for recipe in ambiguous do
            let ws = workspace snapshot
            match AgentExpressionStructuralWorkflow.simulate ws (request snapshot recipe) with
            | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics) ->
                let text = sprintf "%A" diagnostics
                Assert.Contains("AmbiguousEffectOrder", text)
                Assert.Contains(StableId.value targetId, text)
                Assert.Equal<Snapshot>(snapshot, AgentWorkspace.snapshotForStructuralEditing ws)
            | other -> failwithf "Expected ambiguous effects to be refused before publication, got %A" other

        let ordered = ExpressionRecipe.Bind(acquire, ExpressionRecipe.Bind(write snapshot (value 0) message, doneValue))
        let _, published = publish snapshot ordered
        let execution = lower published |> unwrap
        let fn = execution.Functions |> List.find (fun fn -> fn.SemanticFunctionId = targetId)
        let nodes = (target published).Body |> ValueGraph.collectNodes
        let bindings =
            nodes |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.Bind(_, body) -> Some(node.Id, body.ValueBinding.Id)
                | _ -> None)
        for nodeId, bindingId in bindings do
            Assert.Contains(fn.Blocks |> List.collect (fun block -> block.Parameters), fun p -> p.Origin = ExecutionOrigin.PatternBinding(nodeId, bindingId))
            Assert.Contains(fn.Blocks |> List.collect (fun block -> block.Parameters), fun p -> p.Origin = ExecutionOrigin.SemanticNode nodeId)
        // Reordering stored nodes cannot change the executable block graph.
        let reordered =
            { published with
                Functions =
                    published.Functions |> List.map (fun fn ->
                        let rec reverse (graph: ValueGraph): ValueGraph =
                            { graph with
                                Nodes = graph.Nodes |> List.rev |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.Bind(input, body) ->
                                        { node with Kind = SemanticNodeKind.Bind(input, { body with Body = reverse body.Body }) }
                                    | _ -> node) }
                        { fn with Body = reverse fn.Body }) }
        Assert.Equal<ExecutionProgram>(execution, lower reordered |> unwrap)

    [<Fact>]
    let ``Bind roundtrips scope identity and exposes input body and references in review`` () =
        let original = createSnapshot ()
        let recipe = ExpressionRecipe.Bind(ExpressionRecipe.StringLiteral "kept", value 0)
        let review, published = publish original recipe
        let bindNode, input, body =
            (target published).Body.Nodes |> List.pick (fun node ->
                match node.Kind with
                | SemanticNodeKind.Bind(input, body) -> Some(node, input, body)
                | _ -> None)
        let json = SnapshotCodec.serialize published |> unwrap
        for restored in [ SnapshotCodec.deserialize json |> unwrap ] do
            Assert.Equal((target published).Body.ResultNodeId, (target restored).Body.ResultNodeId)
            Assert.Equal(json, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(review.Receipt.ProposedSemanticFingerprint, SemanticFingerprint.compute restored |> unwrap)
            Assert.Equal(review.Receipt.ProposedSnapshotFingerprint, SnapshotFingerprint.compute restored |> unwrap)
            Assert.Equal(TypedSemanticIr.project published |> unwrap, TypedSemanticIr.project restored |> unwrap)

        let typed = TypedSemanticIr.project published |> unwrap |> TypedSemanticIrSerializer.serialize
        Assert.Contains("\"kind\":\"bind\"", typed)
        Assert.Contains(StableId.value body.ValueBinding.Id, typed)
        let summary = review.ResultingChange.AddedNodes |> List.find (fun node -> node.NodeId = bindNode.Id)
        Assert.Equal<ValueDependency list>([ { Role = ValueDependencyRole.BindInput; NodeId = input } ], summary.ValueDependencies)
        Assert.Equal((target original).Body, review.BaseFocus.Function.Body)
        Assert.Equal((target published).Body, review.ProposedFocus.Function.Body)
        let viewer = review.ProposedFocus |> ViewerProjection.focus
        let projectedBind = viewer.Graph.Nodes |> List.find (fun node -> node.Id = bindNode.Id)
        let branch = List.exactlyOne projectedBind.Branches
        Assert.Equal(ViewerNodeRole.Operation "Bind", projectedBind.Role)
        Assert.Equal(ViewerBranchRole.BindBody, branch.Role)
        Assert.Equal(body.ValueBinding.Id, (List.exactlyOne branch.Bindings).Id)
        Assert.Equal(stringType, (List.exactlyOne branch.Bindings).Type)
        Assert.Contains((List.exactlyOne branch.Graph.Nodes).Relations, fun relation -> relation.Target = ViewerTarget.PatternBinding body.ValueBinding.Id)
        Assert.Equal<SemanticType list>(original.Types, published.Types)
        Assert.Equal<SemanticEffect list>(original.Effects, published.Effects)
        Assert.Equal<SemanticCapability list>(original.Capabilities, published.Capabilities)
        Assert.Equal<SemanticFunction list>(original.Functions |> List.filter (fun fn -> fn.Id <> targetId), published.Functions |> List.filter (fun fn -> fn.Id <> targetId))
        Assert.Equal<EffectId list>((target original).RequiredEffects, (target published).RequiredEffects)

    [<Fact>]
    let ``Bind codecs reject obsolete versions unknown fields and unknown kinds`` () =
        let _, published = publish (createSnapshot ()) (ExpressionRecipe.Bind(ExpressionRecipe.StringLiteral "value", value 0))
        let json = SnapshotCodec.serialize published |> unwrap
        let obsolete = JsonNode.Parse(json)
        obsolete["schemaVersion"] <- JsonValue.Create(8)
        Assert.Equal(Error(SnapshotCodecError.UnsupportedSchemaVersion 8), SnapshotCodec.deserialize (obsolete.ToJsonString()))
        for field, content in [ "kind", "futureBind"; "implicitOrder", "first" ] do
            let document = JsonNode.Parse(json)
            let fn = document["functions"].AsArray() |> Seq.find (fun fn -> fn["id"].GetValue<string>() = StableId.value targetId)
            let graph = fn["body"]
            let node = graph["nodes"].AsArray() |> Seq.find (fun node -> node["kind"].GetValue<string>() = "bind")
            node[field] <- JsonValue.Create(content)
            match SnapshotCodec.deserialize (document.ToJsonString()) with
            | Error(SnapshotCodecError.InvalidShape _) -> ()
            | other -> failwithf "Expected closed node schema, got %A" other

    [<Fact>]
    let ``Bind validation rejects forged scope type identity and cyclic dependencies`` () =
        let _, published = publish (createSnapshot ()) (ExpressionRecipe.Bind(ExpressionRecipe.StringLiteral "value", value 0))
        let fn = target published
        let bindNode, input, body = fn.Body.Nodes |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.Bind(input, body) -> Some(node, input, body)
            | _ -> None)
        let replace nodes =
            { published with Functions = published.Functions |> List.map (fun f -> if f.Id = targetId then { f with Body = { f.Body with Nodes = nodes } } else f) }
        let requireIssue code snapshot =
            Assert.Contains(SnapshotValidator.validate snapshot, fun issue -> issue.Code = code)
        let wrongType = { body with ValueBinding = { body.ValueBinding with Type = TypeRef.Primitive PrimitiveType.Bool } }
        fn.Body.Nodes
        |> List.map (fun node -> if node.Id = bindNode.Id then { node with Kind = SemanticNodeKind.Bind(input, wrongType) } else node)
        |> replace |> requireIssue "PatternBindingTypeMismatch"
        fn.Body.Nodes
        |> List.map (fun node -> if node.Id = input then { node with Kind = SemanticNodeKind.PatternReference body.ValueBinding.Id } else node)
        |> replace |> requireIssue "UnknownPatternReference"
        let escaped = { Id = StableId.createForTesting<GraphNodeTag> "node-bind-escaped"; Kind = SemanticNodeKind.PatternReference body.ValueBinding.Id }
        escaped :: fn.Body.Nodes |> replace |> requireIssue "UnknownPatternReference"
        let duplicate = { bindNode with Id = StableId.createForTesting<GraphNodeTag> "node-bind-duplicate" }
        duplicate :: fn.Body.Nodes |> replace |> requireIssue "DuplicatePatternBindingId"
        fn.Body.Nodes
        |> List.map (fun node -> if node.Id = bindNode.Id then { node with Kind = SemanticNodeKind.Bind(bindNode.Id, body) } else node)
        |> replace |> requireIssue "GraphNodeDependencyCycle"

    [<Fact>]
    let ``Bind preflight preserves lexical slots result effects and recipe bounds`` () =
        let snapshot = createSnapshot ()
        let assertRejected code snapshot recipe =
            let ws = workspace snapshot
            match AgentExpressionStructuralWorkflow.simulate ws (request snapshot recipe) with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
                Assert.Equal(code, diagnostic.Code)
                Assert.Equal<Snapshot>(snapshot, AgentWorkspace.snapshotForStructuralEditing ws)
            | other -> failwithf "Expected %s, got %A" code other
        let bind body = ExpressionRecipe.Bind(ExpressionRecipe.StringLiteral "input", body)
        assertRejected "PureExpressionPatternScopeDepthInvalid" snapshot (bind (value -1))
        assertRejected "PureExpressionPatternScopeNotFound" snapshot (bind (value 1))
        for slot in [ RecipePatternSlot.Error; RecipePatternSlot.Field(StableId.createForTesting<FieldTag> "field-bind-foreign") ] do
            assertRejected "PureExpressionPatternSlotNotFound" snapshot (bind (ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = slot }))
        assertRejected "PureExpressionResultTypeMismatch" snapshot (bind ExpressionRecipe.UnitLiteral)
        let withoutEffects = { snapshot with Functions = snapshot.Functions |> List.map (fun fn -> if fn.Id = targetId then { fn with RequiredEffects = [] } else fn) }
        assertRejected "ExpressionCallMissingEffectRequirement" withoutEffects (ExpressionRecipe.Bind(message, ExpressionRecipe.StringLiteral "done"))
        let effectfulBody = ExpressionRecipe.Bind(write snapshot console (ExpressionRecipe.StringLiteral "message"), ExpressionRecipe.StringLiteral "done")
        assertRejected "ExpressionCapabilityMissingEffectRequirement" withoutEffects (bind effectfulBody)
        let rec nested count =
            if count = 0 then ExpressionRecipe.StringLiteral "done"
            else ExpressionRecipe.Bind(ExpressionRecipe.UnitLiteral, nested (count - 1))
        publish snapshot (nested 31) |> ignore
        assertRejected "StructuralRecipeDepthLimitExceeded" snapshot (nested 32)
        let wide count = ExpressionRecipe.Bind(ExpressionRecipe.ListConstruct(stringType, List.replicate count (ExpressionRecipe.StringLiteral "item")), ExpressionRecipe.StringLiteral "done")
        publish snapshot (wide 253) |> ignore
        assertRejected "StructuralRecipeNodeLimitExceeded" snapshot (wide 254)

    [<Fact>]
    let ``Bind guarded publication rejects stale proposals and reallocates local identities on retry`` () =
        let snapshot = createSnapshot ()
        let ws = workspace snapshot
        let recipe = ExpressionRecipe.Bind(ExpressionRecipe.StringLiteral "input", value 0)
        let proposal = AgentExpressionStructuralWorkflow.simulate ws (request snapshot recipe) |> unwrap
        let rename = AgentWorkflow.simulate ws [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function providerId, "renamed") ] |> unwrap
        let advanced =
            match AgentWorkflow.commit ws rename with
            | AgentCommitResult.Committed(next, _) -> next
            | other -> failwithf "Advance failed: %A" other
        match AgentExpressionStructuralWorkflow.commit advanced proposal with
        | AgentStructuralCommitResult.Rejected [ diagnostic ] -> Assert.Equal("StaleBaseSnapshot", diagnostic.Code)
        | other -> failwithf "Expected stale rejection, got %A" other
        let review, committed = publish (AgentWorkspace.snapshotForStructuralEditing advanced) recipe
        let oldReview = AgentExpressionStructuralChangeProposal.review proposal
        let oldIds = oldReview.ResultingChange.AddedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        let newIds = review.ResultingChange.AddedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        Assert.Empty(Set.intersect oldIds newIds)
        let oldBindings = SemanticFacts.collectPatternBindings oldReview.ProposedFocus.Function.Body |> List.map (fun binding -> binding.Id) |> Set.ofList
        let newBindings = SemanticFacts.collectPatternBindings (target committed).Body |> List.map (fun binding -> binding.Id) |> Set.ofList
        Assert.Empty(Set.intersect oldBindings newBindings)
        Assert.Equal(review.Receipt.ProposedSnapshotId, committed.Id)
        Assert.Equal(review.Receipt.ProposedSnapshotFingerprint, SnapshotFingerprint.compute committed |> unwrap)

    [<Fact>]
    let ``Bind fingerprints distinguish reversed scope order while ignoring binding display names`` () =
        let initial = createSnapshot ()
        let recipe =
            ExpressionRecipe.Bind(
                write initial console (ExpressionRecipe.StringLiteral "first"),
                ExpressionRecipe.Bind(write initial console (ExpressionRecipe.StringLiteral "second"), ExpressionRecipe.StringLiteral "done")
            )
        let _, snapshot = publish initial recipe
        let fn = target snapshot
        let outerNode, outerInput, outerBody = fn.Body.Nodes |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.Bind(input, body) -> Some(node, input, body)
            | _ -> None)
        let innerNode, innerInput, innerBody = outerBody.Body.Nodes |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.Bind(input, body) -> Some(node, input, body)
            | _ -> None)
        let reversedInner = { innerNode with Kind = SemanticNodeKind.Bind(outerInput, innerBody) }
        let reversedBody = { outerBody.Body with Nodes = (fn.Body.Nodes |> List.filter (fun node -> node.Id <> outerNode.Id)) @ [ reversedInner ] }
        let reversedOuter = { outerNode with Kind = SemanticNodeKind.Bind(innerInput, { outerBody with Body = reversedBody }) }
        let reversedGraph = { fn.Body with Nodes = (outerBody.Body.Nodes |> List.filter (fun node -> node.Id <> innerNode.Id)) @ [ reversedOuter ] }
        let reversed = { snapshot with Functions = snapshot.Functions |> List.map (fun f -> if f.Id = targetId then { f with Body = reversedGraph } else f) }
        Assert.Empty(SnapshotValidator.validate reversed)
        lower reversed |> unwrap |> ignore
        Assert.NotEqual(SemanticFingerprint.compute snapshot |> unwrap, SemanticFingerprint.compute reversed |> unwrap)
        let renameBinding (node: SemanticNode) =
            match node.Kind with
            | SemanticNodeKind.Bind(input, body) ->
                let renamedBody = { body with ValueBinding = { body.ValueBinding with DisplayName = Some "local" } }
                { node with Kind = SemanticNodeKind.Bind(input, renamedBody) }
            | _ -> node
        let renamed =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn -> { fn with Body = ValueGraph.mapNodes renameBinding fn.Body }) }
        Assert.Equal(SemanticFingerprint.compute snapshot |> unwrap, SemanticFingerprint.compute renamed |> unwrap)
        Assert.NotEqual(SnapshotFingerprint.compute snapshot |> unwrap, SnapshotFingerprint.compute renamed |> unwrap)

    // Shared with the backend execution test so its process harness stays in
    // CSharpBackendTests and these authoring fixtures have one owner.
    let internal executionFixture () =
        let reverseId = StableId.createForTesting<ParameterTag> "parameter-bind-reverse"
        let executeId = StableId.createForTesting<ParameterTag> "parameter-bind-execute"
        let initial = createSnapshot ()
        let capability = List.exactlyOne initial.Capabilities
        let operation = List.exactlyOne capability.Operations
        let initializeTarget (fn: SemanticFunction) =
            if fn.Id <> targetId then
                fn
            else
                { fn with
                    Parameters =
                        fn.Parameters
                        @ [ { Id = reverseId; DisplayName = "reverse"; Type = TypeRef.Primitive PrimitiveType.Bool }
                            { Id = executeId; DisplayName = "execute"; Type = TypeRef.Primitive PrimitiveType.Bool } ]
                    ResultType = TypeRef.Capability capability.Id
                    Body =
                        { Nodes = [ { Id = oldResultId; Kind = SemanticNodeKind.ParameterReference consoleParameterId } ]
                          ResultNodeId = oldResultId } }
        let initial = { initial with Functions = initial.Functions |> List.map initializeTarget }
        let providerConsole = ExpressionRecipe.Parameter providerParameterId
        let providerBody = ExpressionRecipe.Bind(write initial providerConsole (ExpressionRecipe.StringLiteral "acquire"), providerConsole)
        let _, withProvider = publishFunction initial providerId providerBody
        let messageConsole = ExpressionRecipe.Parameter messageParameterId
        let messageBody = ExpressionRecipe.Bind(write withProvider messageConsole (ExpressionRecipe.StringLiteral "message"), ExpressionRecipe.StringLiteral "payload")
        let _, withMessage = publishFunction withProvider messageId messageBody
        let ordered first second =
            ExpressionRecipe.Bind(write withMessage (value 0) first, ExpressionRecipe.Bind(write withMessage (value 1) second, value 2))
        let recipe =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.Parameter executeId,
                ExpressionRecipe.Bind(
                    acquire,
                    ExpressionRecipe.Conditional(
                        ExpressionRecipe.Parameter reverseId,
                        ordered (ExpressionRecipe.StringLiteral "second") message,
                        ordered message (ExpressionRecipe.StringLiteral "second")
                    )
                ),
                console
            )
        let _, published = publish withMessage recipe
        let persisted = published |> SnapshotCodec.serialize |> unwrap |> SnapshotCodec.deserialize |> unwrap
        let execution = lower persisted |> unwrap
        let functionName = CSharpBackendNames.functionName targetId
        let capabilityName = CSharpBackendNames.capabilityName capability.Id
        let operationName = CSharpBackendNames.capabilityOperationName operation.Id
        let harness =
            [ "using System;"
              "using System.Collections.Generic;"
              "using Nyrwvy.Generated;"
              "var console = new RecordingConsole();"
              $"var first = NyFunctions.{functionName}(console, false, true);"
              "if (!ReferenceEquals(console, first)) throw new Exception(\"lost bound capability\");"
              "Console.WriteLine(string.Join(\",\", console.Events));"
              "console.Events.Clear();"
              $"var reversed = NyFunctions.{functionName}(console, true, true);"
              "if (!ReferenceEquals(console, reversed)) throw new Exception(\"lost reversed capability\");"
              "Console.WriteLine(string.Join(\",\", console.Events));"
              "console.Events.Clear();"
              $"var skipped = NyFunctions.{functionName}(console, false, false);"
              "if (!ReferenceEquals(console, skipped) || console.Events.Count != 0) throw new Exception(\"unselected body executed\");"
              "Console.WriteLine(\"skipped\");"
              $"sealed class RecordingConsole : {capabilityName}"
              "{"
              "    public List<string> Events { get; } = new();"
              $"    public NyUnit {operationName}(NyStr message)"
              "    {"
              "        Events.Add(message);"
              "        return NyUnit.Value;"
              "    }"
              "}" ] |> String.concat "\n"
        execution, CSharpBackendProfile.systemConsoleWriteLine operation.Id, harness
