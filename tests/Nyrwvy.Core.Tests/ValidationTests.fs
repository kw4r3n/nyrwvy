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

    let private withResolvedObligation (fixture: GraphFixture) =
        let transactionId = StableId.createForTesting<TransactionTag> "transaction-owner-check"
        { fixture.Snapshot with
            Obligations =
                [ { Id = StableId.createForTesting<ObligationTag> "obligation-owner-check"
                    OriginTransactionId = transactionId
                    CallNodeId = fixture.RunCallNodeId
                    ParameterId = fixture.ValidateInputParameterId
                    Status =
                        ObligationStatus.Resolved
                            { TransactionId = transactionId
                              ValueNodeId = fixture.RunInputNodeId } } ] }

    [<Fact>]
    let ``resolved obligation accepts a value owned by its caller`` () =
        TestGraph.create () |> withResolvedObligation |> SnapshotValidator.validate |> Assert.Empty

    [<Theory>]
    [<InlineData("call", false)>]
    [<InlineData("call", true)>]
    [<InlineData("value", false)>]
    [<InlineData("value", true)>]
    let ``duplicate node ownership keeps last owner diagnostics without leaking function types`` duplicateKind duplicateLast =
        let fixture = TestGraph.create ()
        let snapshot = withResolvedObligation fixture
        let duplicateId = if duplicateKind = "call" then fixture.RunCallNodeId else fixture.RunInputNodeId
        let duplicate: SemanticFunction =
            { MustNotWait = false; Exported = true
              Id = StableId.createForTesting<FunctionTag> "function-duplicate-owner"
              DisplayName = "duplicate_owner"
              TypeParameters = []
              Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.Int64
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = duplicateId; Kind = SemanticNodeKind.Int64Literal 1L } ]
                  ResultNodeId = duplicateId } }
        let functions =
            if duplicateLast then snapshot.Functions @ [ duplicate ]
            else duplicate :: snapshot.Functions
        let obligation = List.exactlyOne snapshot.Obligations
        let expected: ValidationIssue list =
            [ { Code = "DuplicateGraphNodeId"
                Message = $"Graph node ID '{StableId.value duplicateId}' occurs more than once." }
              if duplicateLast then
                  if duplicateKind = "call" then
                      { Code = "InvalidObligationTarget"
                        Message = $"Obligation '{StableId.value obligation.Id}' does not target a direct-call node." }
                  else
                      { Code = "InvalidObligationResolutionValue"
                        Message = $"Resolved obligation '{StableId.value obligation.Id}' references a value node outside the call's owning function." } ]
        Assert.Equal<ValidationIssue list>(expected, SnapshotValidator.validate { snapshot with Functions = functions })

    [<Theory>]
    [<InlineData("call")>]
    [<InlineData("value")>]
    let ``obligation ownership checks preserve unknown node diagnostics`` missingKind =
        let snapshot = TestGraph.create () |> withResolvedObligation
        let obligation = List.exactlyOne snapshot.Obligations
        let missingId = StableId.createForTesting<GraphNodeTag> "node-owner-missing"
        let changed, expected: MissingArgumentObligation * ValidationIssue list =
            if missingKind = "call" then
                { obligation with CallNodeId = missingId },
                [ { Code = "InvalidObligationTarget"
                    Message = $"Obligation '{StableId.value obligation.Id}' targets unknown graph node '{StableId.value missingId}'." } ]
            else
                let resolution =
                    { TransactionId = obligation.OriginTransactionId
                      ValueNodeId = missingId }
                { obligation with Status = ObligationStatus.Resolved resolution },
                [ { Code = "InvalidObligationResolution"
                    Message = $"Resolved obligation '{StableId.value obligation.Id}' does not match the call's argument binding." }
                  { Code = "InvalidObligationResolutionValue"
                    Message = $"Resolved obligation '{StableId.value obligation.Id}' references a value node outside the call's owning function." } ]
        Assert.Equal<ValidationIssue list>(expected, SnapshotValidator.validate { snapshot with Obligations = [ changed ] })

    [<Fact>]
    let ``null string literals are rejected`` () =
        let fixture = TestGraph.create ()
        let functionId = StableId.createForTesting<FunctionTag> "function-null-string-literal"
        let nodeId = StableId.createForTesting<GraphNodeTag> "node-null-string-literal"
        let functionEntity: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId
              TypeParameters = []
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
    let ``string literal with an unpaired surrogate is rejected`` () =
        let fixture = TestGraph.create ()
        let functionId = StableId.createForTesting<FunctionTag> "function-lone-surrogate-literal"
        let nodeId = StableId.createForTesting<GraphNodeTag> "node-lone-surrogate-literal"
        let functionEntity: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId
              TypeParameters = []
              DisplayName = "lone_surrogate_literal"
              Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.String
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral(utf16 "a{D800}b") } ]
                  ResultNodeId = nodeId } }

        let invalid =
            { fixture.Snapshot with
                Functions = functionEntity :: fixture.Snapshot.Functions }

        SnapshotValidator.validate invalid |> requireIssue "StringLiteralUnpairedSurrogate"

    [<Fact>]
    let ``missing call argument requires an explicit open obligation`` () =
        let fixture = TestGraph.create ()
        let policyParameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let policyParameter: SemanticParameter =
            { Id = policyParameterId
              DisplayName = "policy"
              Type = TypeRef.Named(fixture.PolicyTypeId, []) }

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
            { MustNotWait = false; Exported = true; Id = StableId.createForTesting<FunctionTag> "function-dependencies"
              TypeParameters = []
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

    [<Theory>]
    [<InlineData("conditional")>]
    [<InlineData("bool-and")>]
    [<InlineData("bind")>]
    let ``a branch node referencing its enclosing control node is a cycle even off the branch result`` (kind: string) =
        // The enclosing node is visible inside its own branches, so a branch node may name it
        // without leaving lexical scope. Lowering treats every free reference of a branch as a
        // dependency of the enclosing node, so this must be a cycle here, not a late lowering error.
        let sharedId = StableId.createForTesting<GraphNodeTag> "node-self-shared"
        let conditionId = StableId.createForTesting<GraphNodeTag> "node-self-condition"
        let enclosingId = StableId.createForTesting<GraphNodeTag> "node-self-enclosing"
        let deadId = StableId.createForTesting<GraphNodeTag> "node-self-dead"
        let resultId = StableId.createForTesting<GraphNodeTag> "node-self-result"
        let falseId = StableId.createForTesting<GraphNodeTag> "node-self-false"
        let bindingId = StableId.createForTesting<PatternBindingTag> "binding-self"
        let branch: ValueGraph =
            { Nodes =
                [ { Id = deadId; Kind = SemanticNodeKind.Int64WrappingAdd(enclosingId, sharedId) }
                  { Id = resultId; Kind = SemanticNodeKind.Int64Literal 2L } ]
              ResultNodeId = resultId }
        let boolBranch: ValueGraph =
            { Nodes =
                [ { Id = deadId; Kind = SemanticNodeKind.BoolNot enclosingId }
                  { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ]
              ResultNodeId = resultId }
        let whenFalse: ValueGraph =
            { Nodes = [ { Id = falseId; Kind = SemanticNodeKind.Int64WrappingAdd(sharedId, sharedId) } ]
              ResultNodeId = falseId }
        let enclosingKind, resultType =
            match kind with
            | "conditional" -> SemanticNodeKind.Conditional(conditionId, branch, whenFalse), TypeRef.Primitive PrimitiveType.Int64
            | "bool-and" -> SemanticNodeKind.BoolAnd(conditionId, boolBranch), TypeRef.Primitive PrimitiveType.Bool
            | "bind" ->
                SemanticNodeKind.Bind(
                    sharedId,
                    { ValueBinding = { Id = bindingId; DisplayName = Some "value"; Type = TypeRef.Primitive PrimitiveType.Int64 }
                      Body = branch }),
                TypeRef.Primitive PrimitiveType.Int64
            | other -> failwithf "Unknown fixture case: %s" other
        let graph: ValueGraph =
            { Nodes =
                [ { Id = sharedId; Kind = SemanticNodeKind.Int64Literal 1L }
                  { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral false }
                  { Id = enclosingId; Kind = enclosingKind } ]
              ResultNodeId = enclosingId }
        let snapshot = dependencySnapshot graph
        let snapshot =
            { snapshot with Functions = snapshot.Functions |> List.map (fun fn -> { fn with ResultType = resultType }) }

        Assert.Equal<string list>([ "GraphNodeDependencyCycle" ], SnapshotValidator.validate snapshot |> List.map (fun issue -> issue.Code))

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

    [<Fact>]
    let ``callback diagnostics of Map, Set and Task nodes are not named after lists`` () =
        // The callback codes are shared by every higher-order node, so a client filtering on them
        // must not read a Map or Task problem as a List problem.
        let id (name: string) = StableId.createForTesting<GraphNodeTag> $"node-callback-{name}"
        let mapParameterId = StableId.createForTesting<ParameterTag> "parameter-callback-map"
        let textParameterId = StableId.createForTesting<ParameterTag> "parameter-callback-text"
        let unknownFunctionId = StableId.createForTesting<FunctionTag> "function-callback-unknown"
        let unaryFunctionId = StableId.createForTesting<FunctionTag> "function-callback-unary"
        let unaryParameterId = StableId.createForTesting<ParameterTag> "parameter-callback-unary"
        let int64Type = TypeRef.Primitive PrimitiveType.Int64
        let stringType = TypeRef.Primitive PrimitiveType.String
        let unary: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = unaryFunctionId
              TypeParameters = []
              DisplayName = "unary"
              Parameters = [ { Id = unaryParameterId; DisplayName = "value"; Type = int64Type } ]
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = id "unary-value"; Kind = SemanticNodeKind.ParameterReference unaryParameterId } ]
                  ResultNodeId = id "unary-value" } }
        let probe: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = StableId.createForTesting<FunctionTag> "function-callback-probe"
              TypeParameters = []
              DisplayName = "probe"
              Parameters =
                [ { Id = mapParameterId; DisplayName = "counts"; Type = TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ stringType; int64Type ]) }
                  { Id = textParameterId; DisplayName = "text"; Type = stringType } ]
              ResultType = TypeRef.Primitive PrimitiveType.Unit
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = id "map"; Kind = SemanticNodeKind.ParameterReference mapParameterId }
                      { Id = id "text"; Kind = SemanticNodeKind.ParameterReference textParameterId }
                      { Id = id "seed"; Kind = SemanticNodeKind.Int64Literal 0L }
                      { Id = id "map-filter"; Kind = SemanticNodeKind.MapFilter(id "map", unknownFunctionId, []) }
                      { Id = id "map-fold"; Kind = SemanticNodeKind.MapFold(id "map", id "seed", unaryFunctionId, []) }
                      { Id = id "set-from-list"; Kind = SemanticNodeKind.SetFromList(id "text") }
                      { Id = id "task-start"; Kind = SemanticNodeKind.TaskStart(id "text", unknownFunctionId, [], id "seed") }
                      { Id = id "parallel-all"; Kind = SemanticNodeKind.ParallelAll(id "text", unknownFunctionId, []) }
                      { Id = id "result"; Kind = SemanticNodeKind.UnitLiteral } ]
                  ResultNodeId = id "result" } }
        let snapshot =
            { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-callback-codes") with Functions = [ unary; probe ] }
        let codes = SnapshotValidator.validate snapshot |> List.map (fun issue -> issue.Code)

        for expected in [ "UnknownCallbackFunction"; "CallbackArityMismatch"; "HigherOrderSourceTypeMismatch" ] do
            Assert.Contains(expected, codes)
        Assert.Empty(codes |> List.filter (fun code -> code.StartsWith "List"))
