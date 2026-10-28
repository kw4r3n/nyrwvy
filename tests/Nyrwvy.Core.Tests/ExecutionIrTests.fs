namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module ExecutionIrTests =
    let private typed (fixture: MilestoneZeroFixture) =
        match TypedSemanticIr.project fixture.Snapshot with
        | Ok program -> program
        | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error

    let private lower (fixture: MilestoneZeroFixture) =
        match fixture |> typed |> ExecutionIr.lower with
        | Ok program -> program
        | Error error -> failwithf "Unexpected Execution IR error: %A" error

    let private functionById id (program: ExecutionProgram) =
        program.Functions |> List.find (fun functionEntity -> functionEntity.SemanticFunctionId = id)

    [<Fact>]
    let ``representative M0 program lowers to terminated execution blocks`` () =
        let fixture = MilestoneZeroGraph.create ()
        let execution = lower fixture

        Assert.Equal(fixture.Snapshot.Id, execution.SourceSnapshotId)
        Assert.Equal(fixture.Snapshot.Functions.Length, execution.Functions.Length)

        for functionEntity in execution.Functions do
            Assert.NotEmpty(functionEntity.Blocks)
            Assert.Contains(functionEntity.Blocks, fun block -> block.Id = functionEntity.EntryBlockId)

    [<Fact>]
    let ``main makes direct-call evaluation order explicit`` () =
        let fixture = MilestoneZeroGraph.create ()
        let execution = lower fixture
        let main = functionById fixture.MainFunctionId execution
        let entry = main.Blocks |> List.find (fun block -> block.Id = main.EntryBlockId)

        let calls =
            entry.Instructions
            |> List.choose (fun instruction ->
                match instruction.Kind with
                | ExecutionInstructionKind.DirectCall(functionId, _) -> Some functionId
                | _ -> None)

        Assert.Equal<FunctionId list>([ fixture.ValidateFunctionId; fixture.ReportFunctionId ], calls)

    [<Fact>]
    let ``structured conditionals lower to explicit branch and merge blocks`` () =
        let fixture = MilestoneZeroGraph.create ()
        let execution = lower fixture
        let validate = functionById fixture.ValidateFunctionId execution

        Assert.Contains(
            validate.Blocks,
            fun block ->
                match block.Terminator with
                | ExecutionTerminator.Branch _ -> true
                | _ -> false
        )

        let mergeValues =
            validate.Blocks
            |> List.collect (fun block -> block.Parameters)
            |> List.filter (fun value ->
                match value.Origin with
                | ExecutionOrigin.SemanticNode _ -> true
                | _ -> false)

        Assert.NotEmpty(mergeValues)
        Assert.Contains(
            validate.Blocks,
            fun block ->
                match block.Terminator with
                | ExecutionTerminator.Return _ -> true
                | _ -> false
        )

    [<Fact>]
    let ``result and union matches lower payload handling to explicit extraction instructions`` () =
        let fixture = MilestoneZeroGraph.create ()
        let execution = lower fixture
        let report = functionById fixture.ReportFunctionId execution

        Assert.Contains(
            report.Blocks,
            fun block ->
                match block.Terminator with
                | ExecutionTerminator.MatchResult _ -> true
                | _ -> false
        )

        Assert.Contains(
            report.Blocks |> List.collect (fun block -> block.Instructions),
            fun instruction ->
                match instruction.Kind with
                | ExecutionInstructionKind.ExtractResultOk _
                | ExecutionInstructionKind.ExtractResultErr _ -> true
                | _ -> false
        )

        Assert.Contains(
            report.Blocks,
            fun block ->
                match block.Terminator with
                | ExecutionTerminator.MatchUnion _ -> true
                | _ -> false
        )

    [<Fact>]
    let ``execution values keep semantic node or pattern provenance`` () =
        let fixture = MilestoneZeroGraph.create ()
        let execution = lower fixture

        let values =
            execution.Functions
            |> List.collect (fun functionEntity ->
                let parameterValues =
                    functionEntity.Parameters
                    |> List.map (fun parameter -> parameter.Value)
                let blockValues =
                    functionEntity.Blocks
                    |> List.collect (fun block ->
                        let instructionValues =
                            block.Instructions
                            |> List.map (fun instruction -> instruction.Result)
                        block.Parameters @ instructionValues)
                parameterValues @ blockValues)

        Assert.Contains(values, fun value -> value.Origin = ExecutionOrigin.SemanticNode fixture.MainValidateCallNodeId)
        Assert.Contains(
            values,
            fun value ->
                match value.Origin with
                | ExecutionOrigin.PatternBinding _ -> true
                | _ -> false
        )

    [<Theory>]
    [<InlineData(1, false)>]
    [<InlineData(36, false)>]
    [<InlineData(36, true)>]
    let ``validation and lowering promptly distinguish independent and ordered effects through a shared DAG`` sharedNodeCount ordered =
        let fixture = MilestoneZeroGraph.create ()
        let operation =
            fixture.Snapshot.Capabilities
            |> List.find (fun capability -> capability.Id = fixture.ConsoleCapabilityId)
            |> fun capability ->
                capability.Operations
                |> List.find (fun operation -> operation.Id = fixture.ConsoleWriteLineOperationId)
        let operationParameter = List.exactlyOne operation.Parameters
        let extraTextNodeId index =
            StableId.createForTesting<GraphNodeTag> (sprintf "node-main-extra-text-%02d" index)
        let extraInvokeNodeId = StableId.createForTesting<GraphNodeTag> "node-main-extra-console"
        let orderListNodeId = StableId.createForTesting<GraphNodeTag> "node-main-order-list"
        let orderLengthNodeId = StableId.createForTesting<GraphNodeTag> "node-main-order-length"

        let main =
            fixture.Snapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.MainFunctionId)
        let consoleParameter =
            main.Parameters
            |> List.find (fun parameter -> parameter.Type = TypeRef.Capability fixture.ConsoleCapabilityId)
        let consoleNode =
            main.Body.Nodes
            |> List.find (fun node ->
                node.Kind = SemanticNodeKind.ParameterReference consoleParameter.Id)

        let orderNodes: SemanticNode list =
            if ordered then
                [ { Id = orderListNodeId
                    Kind =
                        SemanticNodeKind.ListConstruct(
                            TypeRef.Primitive PrimitiveType.Unit,
                            [ main.Body.ResultNodeId ]
                        ) }
                  { Id = orderLengthNodeId
                    Kind = SemanticNodeKind.ListLength orderListNodeId } ]
            else
                []

        // Each concat shares both preceding subgraphs: only 36 nodes, but millions of paths.
        let extraTextNodes: SemanticNode list =
            [ for index in 0 .. sharedNodeCount - 1 ->
                { Id = extraTextNodeId index
                  Kind =
                    if index = 0 && ordered then
                        SemanticNodeKind.Int64ToString orderLengthNodeId
                    elif index < 2 then
                        SemanticNodeKind.StringLiteral "extra output"
                    else
                        SemanticNodeKind.StringConcat(extraTextNodeId (index - 1), extraTextNodeId (index - 2)) } ]
        let extraInvokeNode: SemanticNode =
            { Id = extraInvokeNodeId
              Kind =
                SemanticNodeKind.CapabilityInvoke(
                    consoleNode.Id,
                    fixture.ConsoleWriteLineOperationId,
                    [ { TargetCapabilityParameterId = operationParameter.Id
                        ValueNodeId = extraTextNodeId (sharedNodeCount - 1) } ]
                ) }

        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.MainFunctionId then
                    functionEntity
                else
                    { functionEntity with
                        Body =
                            { functionEntity.Body with
                                Nodes = functionEntity.Body.Nodes @ orderNodes @ extraTextNodes @ [ extraInvokeNode ] } })

        let snapshot = { fixture.Snapshot with Functions = functions }
        // Validation applies the rule lowering enforces, so the unordered pair is refused there.
        let stopwatch = System.Diagnostics.Stopwatch.StartNew()
        let issues = SnapshotValidator.validate snapshot
        stopwatch.Stop()
        Assert.True(stopwatch.Elapsed < System.TimeSpan.FromSeconds 5.0, sprintf "Validation took %O" stopwatch.Elapsed)
        if not ordered then
            let issue = Assert.Single(issues)
            Assert.Equal("AmbiguousEffectOrder", issue.Code)
            Assert.Contains($"'{StableId.value main.Body.ResultNodeId}'", issue.Message)
            Assert.Contains($"'{StableId.value extraInvokeNodeId}'", issue.Message)
        else
            Assert.Empty(issues)
            let typedProgram =
                match TypedSemanticIr.project snapshot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let result = ExecutionIr.lower typedProgram
            stopwatch.Stop()
            Assert.True(stopwatch.Elapsed < System.TimeSpan.FromSeconds 5.0, sprintf "Lowering took %O" stopwatch.Elapsed)
            match result with
            | Ok execution ->
                let loweredMain = functionById fixture.MainFunctionId execution
                let entry = loweredMain.Blocks |> List.find (fun block -> block.Id = loweredMain.EntryBlockId)
                let effects =
                    entry.Instructions
                    |> List.choose (fun instruction ->
                        match instruction.Kind with
                        | ExecutionInstructionKind.DirectCall(functionId, _) when functionId = fixture.ReportFunctionId ->
                            Some instruction.OriginNodeId
                        | ExecutionInstructionKind.CapabilityInvoke _ -> Some instruction.OriginNodeId
                        | _ -> None)
                Assert.Equal<GraphNodeId list>([ main.Body.ResultNodeId; extraInvokeNodeId ], effects)
            | Error error -> failwithf "Unexpected lowering error: %A" error

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``lowering cyclic typed graphs preserves effect ordering error precedence`` ordered =
        let fixture = MilestoneZeroGraph.create ()
        let program = typed fixture
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-cycle-unit"
        let valueNodeId = StableId.createForTesting<GraphNodeTag> "node-cycle-unit"
        let firstNodeId = StableId.createForTesting<GraphNodeTag> "node-cycle-a"
        let secondNodeId = StableId.createForTesting<GraphNodeTag> "node-cycle-b"
        let unitType = TypeRef.Primitive PrimitiveType.Unit
        let call nodeId argumentId: TypedSemanticNode =
            { OriginNodeId = nodeId
              Completion = GraphCompletion.Normal unitType
              Kind =
                TypedSemanticNodeKind.DirectCall(
                    fixture.MainFunctionId,
                    [ { TargetParameterId = parameterId; ValueNodeId = argumentId } ]
                ) }
        let functionEntity: TypedSemanticFunction =
            { Id = fixture.MainFunctionId
              TypeParameters = []
              Parameters = [ { Id = parameterId; Type = unitType } ]
              ResultType = unitType
              RequiredEffects = [ fixture.ConsoleEffectId ]
              Body =
                { Nodes =
                    [ { OriginNodeId = valueNodeId; Completion = GraphCompletion.Normal unitType; Kind = TypedSemanticNodeKind.UnitLiteral }
                      call firstNodeId firstNodeId
                      call secondNodeId (if ordered then firstNodeId else valueNodeId) ]
                  ResultNodeId = secondNodeId } }

        // lower accepts typed programs directly, without snapshot cycle validation.
        match ordered, ExecutionIr.lower { program with Functions = [ functionEntity ] } with
        | false, Error(ExecutionIrError.AmbiguousEffectOrder(functionId, first, second)) ->
            Assert.Equal(fixture.MainFunctionId, functionId)
            Assert.Equal(firstNodeId, first)
            Assert.Equal(secondNodeId, second)
        | true, Error(ExecutionIrError.UnresolvedDependencies(functionId, nodes)) ->
            Assert.Equal(fixture.MainFunctionId, functionId)
            Assert.Equal<GraphNodeId list>([ firstNodeId; secondNodeId ], nodes)
        | ordered, result -> failwithf "Unexpected cyclic lowering result (ordered = %b): %A" ordered result
