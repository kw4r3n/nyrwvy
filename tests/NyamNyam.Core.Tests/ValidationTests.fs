namespace NyamNyam.Core.Tests

open NyamNyam.Core
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
