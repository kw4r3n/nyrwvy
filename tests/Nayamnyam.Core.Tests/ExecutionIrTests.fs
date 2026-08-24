namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module ExecutionIrTests =
    let private typed fixture =
        match TypedSemanticIr.project fixture.Snapshot with
        | Ok program -> program
        | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error

    let private lower fixture =
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
                functionEntity.Parameters |> List.map (fun parameter -> parameter.Value)
                @ (functionEntity.Blocks
                   |> List.collect (fun block ->
                       block.Parameters
                       @ (block.Instructions |> List.map (fun instruction -> instruction.Result)))) )

        Assert.Contains(values, fun value -> value.Origin = ExecutionOrigin.SemanticNode fixture.MainValidateCallNodeId)
        Assert.Contains(
            values,
            fun value ->
                match value.Origin with
                | ExecutionOrigin.PatternBinding _ -> true
                | _ -> false
        )

    [<Fact>]
    let ``lowering rejects independent effectful nodes instead of inventing an order`` () =
        let fixture = MilestoneZeroGraph.create ()
        let operation =
            fixture.Snapshot.Capabilities
            |> List.find (fun capability -> capability.Id = fixture.ConsoleCapabilityId)
            |> fun capability ->
                capability.Operations
                |> List.find (fun operation -> operation.Id = fixture.ConsoleWriteLineOperationId)
        let operationParameter = List.exactlyOne operation.Parameters
        let extraTextNodeId = StableId.createForTesting<GraphNodeTag> "node-main-extra-text"
        let extraInvokeNodeId = StableId.createForTesting<GraphNodeTag> "node-main-extra-console"

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

        let extraTextNode: SemanticNode =
            { Id = extraTextNodeId
              Kind = SemanticNodeKind.StringLiteral "extra output" }
        let extraInvokeNode: SemanticNode =
            { Id = extraInvokeNodeId
              Kind =
                SemanticNodeKind.CapabilityInvoke(
                    consoleNode.Id,
                    fixture.ConsoleWriteLineOperationId,
                    [ { TargetCapabilityParameterId = operationParameter.Id
                        ValueNodeId = extraTextNodeId } ]
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
                                Nodes = functionEntity.Body.Nodes @ [ extraTextNode; extraInvokeNode ] } })

        let snapshot = { fixture.Snapshot with Functions = functions }
        Assert.Empty(SnapshotValidator.validate snapshot)

        let typedProgram =
            match TypedSemanticIr.project snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error

        match ExecutionIr.lower typedProgram with
        | Error(ExecutionIrError.AmbiguousEffectOrder(functionId, first, second)) ->
            Assert.Equal(fixture.MainFunctionId, functionId)
            let pair = Set.ofList [ first; second ]
            let expected = Set.ofList [ fixture.MainValidateCallNodeId; extraInvokeNodeId ]
            if pair = expected then
                failwith "The pure validate call was incorrectly classified as effectful."
            Assert.Contains(extraInvokeNodeId, pair)
        | result -> failwithf "Expected AmbiguousEffectOrder, got %A" result
