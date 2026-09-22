namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module ValidationTests =
    let private requireIssue code issues =
        if not (issues |> List.exists (fun issue -> issue.Code = code)) then
            failwithf "Expected validation issue %s, got %A" code issues

    [<Fact>]
    let ``baseline graph is structurally and semantically valid`` () =
        let fixture = TestGraph.create ()
        let issues = SnapshotValidator.validate fixture.Snapshot
        Assert.Empty(issues)

    [<Fact>]
    let ``null string literals are rejected`` () =
        let fixture = TestGraph.create ()
        let functionId = StableId.createForTesting<FunctionTag> "function-null-string-literal"
        let nodeId = StableId.createForTesting<GraphNodeTag> "node-null-string-literal"
        let functionEntity: SemanticFunction =
            { Id = functionId
              DisplayName = "null_string_literal"
              Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.String
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral null } ]
                  ResultNodeId = nodeId } }

        let invalid =
            { fixture.Snapshot with
                Functions = functionEntity :: fixture.Snapshot.Functions }

        SnapshotValidator.validate invalid |> requireIssue "NullStringLiteral"

    [<Fact>]
    let ``missing call argument requires an explicit open obligation`` () =
        let fixture = TestGraph.create ()
        let policyParameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let policyParameter: SemanticParameter =
            { Id = policyParameterId
              DisplayName = "policy"
              Type = TypeRef.Named fixture.PolicyTypeId }

        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id = fixture.ValidateFunctionId then
                    { functionEntity with Parameters = functionEntity.Parameters @ [ policyParameter ] }
                else
                    functionEntity)

        let invalid = { fixture.Snapshot with Functions = functions }
        SnapshotValidator.validate invalid |> requireIssue "MissingArgument"

    [<Fact>]
    let ``argument value must belong to the caller lexical graph`` () =
        let fixture = TestGraph.create ()
        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.RunFunctionId then
                    functionEntity
                else
                    let updateNode (node: SemanticNode) =
                        if node.Id <> fixture.RunCallNodeId then
                            node
                        else
                            match node.Kind with
                            | SemanticNodeKind.DirectCall(target, arguments) ->
                                let changed =
                                    arguments
                                    |> List.map (fun argument ->
                                        { argument with ValueNodeId = fixture.ValidateInputNodeId })
                                { node with Kind = SemanticNodeKind.DirectCall(target, changed) }
                            | _ -> node
                    { functionEntity with Body = ValueGraph.mapNodes updateNode functionEntity.Body })

        SnapshotValidator.validate { fixture.Snapshot with Functions = functions }
        |> requireIssue "ValueNodeOutOfScope"

    let private dependencySnapshot (graph: ValueGraph) =
        let functionEntity: SemanticFunction =
            { Id = StableId.createForTesting<FunctionTag> "function-dependencies"
              DisplayName = "dependencies"
              Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.Int64
              RequiredEffects = []
              Body = graph }
        { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-dependencies") with
            Functions = [ functionEntity ] }

    let private sharedDependencyGraph depth: ValueGraph =
        let nodeId (index: int) = StableId.createForTesting<GraphNodeTag> $"node-shared-{index:D2}"
        { Nodes =
            [ for index in 0 .. depth ->
                { Id = nodeId index
                  Kind =
                    if index = 0 then SemanticNodeKind.Int64Literal 1L
                    else SemanticNodeKind.Int64WrappingAdd(nodeId (index - 1), nodeId (index - 1)) } ]
          ResultNodeId = nodeId depth }

    [<Fact>]
    let ``shared value dependencies form a valid DAG independent of node order`` () =
        let graph = sharedDependencyGraph 32
        Assert.Empty(SnapshotValidator.validate (dependencySnapshot graph))
        Assert.Empty(SnapshotValidator.validate (dependencySnapshot { graph with Nodes = List.rev graph.Nodes }))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``shared dependencies and unknown IDs do not hide disconnected cycles`` includeUnknownDependency =
        let graph = sharedDependencyGraph 8
        let firstId = StableId.createForTesting<GraphNodeTag> "node-z-cycle-first"
        let secondId = StableId.createForTesting<GraphNodeTag> "node-z-cycle-second"
        let dependencyId =
            if includeUnknownDependency then StableId.createForTesting<GraphNodeTag> "node-missing"
            else graph.ResultNodeId
        let cycleNodes =
            [ { Id = firstId; Kind = SemanticNodeKind.Int64WrappingAdd(dependencyId, secondId) }
              { Id = secondId; Kind = SemanticNodeKind.Int64WrappingAdd(firstId, graph.ResultNodeId) } ]
        let issues =
            dependencySnapshot { graph with Nodes = graph.Nodes @ cycleNodes }
            |> SnapshotValidator.validate
        let expectedCodes =
            if includeUnknownDependency then [ "GraphNodeDependencyCycle"; "ValueNodeOutOfScope" ]
            else [ "GraphNodeDependencyCycle" ]

        Assert.Equal<string list>(expectedCodes, issues |> List.map (fun issue -> issue.Code))

    [<Theory>]
    [<InlineData("shared", "")>]
    [<InlineData("cycle", "GraphNodeDependencyCycle")>]
    [<InlineData("sibling", "ValueNodeOutOfScope")>]
    [<InlineData("unknown", "ValueNodeOutOfScope")>]
    [<InlineData("foreign-result", "UnknownGraphResultNode")>]
    let ``branch dependency checks preserve lexical scope and cycle diagnostics`` referenceKind expectedCode =
        let sharedId = StableId.createForTesting<GraphNodeTag> "node-branch-shared"
        let conditionId = StableId.createForTesting<GraphNodeTag> "node-branch-condition"
        let conditionalId = StableId.createForTesting<GraphNodeTag> "node-branch-conditional"
        let trueId = StableId.createForTesting<GraphNodeTag> "node-branch-true"
        let falseId = StableId.createForTesting<GraphNodeTag> "node-branch-false"
        let dependencyId, resultId =
            match referenceKind with
            | "shared" -> sharedId, trueId
            | "cycle" -> conditionalId, trueId
            | "sibling" -> falseId, trueId
            | "unknown" -> StableId.createForTesting<GraphNodeTag> "node-branch-missing", trueId
            | "foreign-result" -> sharedId, sharedId
            | other -> failwithf "Unknown fixture case: %s" other
        let whenTrue: ValueGraph =
            { Nodes = [ { Id = trueId; Kind = SemanticNodeKind.Int64WrappingAdd(dependencyId, sharedId) } ]
              ResultNodeId = resultId }
        let whenFalse: ValueGraph =
            { Nodes = [ { Id = falseId; Kind = SemanticNodeKind.Int64WrappingAdd(sharedId, sharedId) } ]
              ResultNodeId = falseId }
        let graph: ValueGraph =
            { Nodes =
                [ { Id = sharedId; Kind = SemanticNodeKind.Int64Literal 1L }
                  { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral false }
                  { Id = conditionalId; Kind = SemanticNodeKind.Conditional(conditionId, whenTrue, whenFalse) } ]
              ResultNodeId = conditionalId }
        let issues = dependencySnapshot graph |> SnapshotValidator.validate
        let expectedCodes = if expectedCode = "" then [] else [ expectedCode ]

        Assert.Equal<string list>(expectedCodes, issues |> List.map (fun issue -> issue.Code))

    [<Fact>]
    let ``value dependency cycles are rejected`` () =
        let fixture = TestGraph.create ()
        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.RunFunctionId then
                    functionEntity
                else
                    let updateNode (node: SemanticNode) =
                        if node.Id <> fixture.RunCallNodeId then
                            node
                        else
                            match node.Kind with
                            | SemanticNodeKind.DirectCall(target, arguments) ->
                                let changed =
                                    arguments
                                    |> List.map (fun argument ->
                                        { argument with ValueNodeId = fixture.RunCallNodeId })
                                { node with Kind = SemanticNodeKind.DirectCall(target, changed) }
                            | _ -> node
                    { functionEntity with Body = ValueGraph.mapNodes updateNode functionEntity.Body })

        SnapshotValidator.validate { fixture.Snapshot with Functions = functions }
        |> requireIssue "GraphNodeDependencyCycle"
