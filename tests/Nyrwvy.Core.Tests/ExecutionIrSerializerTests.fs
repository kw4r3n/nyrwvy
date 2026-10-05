namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module ExecutionIrSerializerTests =
    let private lowerRepresentative () =
        let fixture = MilestoneZeroGraph.create ()
        let typed =
            match TypedSemanticIr.project fixture.Snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error
        let execution =
            match ExecutionIr.lower typed with
            | Ok value -> value
            | Error error -> failwithf "Unexpected Execution IR error: %A" error
        fixture, execution

    [<Fact>]
    let ``execution IR serialization is byte-for-byte deterministic`` () =
        let _, execution = lowerRepresentative ()
        let first = ExecutionIrSerializer.serialize execution
        let second = ExecutionIrSerializer.serialize execution
        Assert.Equal(first, second)
        Assert.Contains("\"schemaVersion\":6", first)

    [<Fact>]
    let ``execution IR codec canonicalizes nonsemantic collection order`` () =
        let _, execution = lowerRepresentative ()
        let reorderFunction (functionEntity: ExecutionFunction) =
            { functionEntity with Blocks = List.rev functionEntity.Blocks }
        let reordered =
            { execution with
                Types = List.rev execution.Types
                Effects = List.rev execution.Effects
                Capabilities = List.rev execution.Capabilities
                Functions = execution.Functions |> List.rev |> List.map reorderFunction }

        let expected = ExecutionIrSerializer.serialize execution
        let actual = ExecutionIrSerializer.serialize reordered
        Assert.Equal(expected, actual)

    [<Fact>]
    let ``execution IR codec preserves semantic instruction order`` () =
        let fixture, execution = lowerRepresentative ()
        let main =
            execution.Functions
            |> List.find (fun functionEntity -> functionEntity.SemanticFunctionId = fixture.MainFunctionId)
        let entry =
            main.Blocks
            |> List.find (fun block -> block.Id = main.EntryBlockId)

        if entry.Instructions.Length < 2 then
            failwith "Representative main entry block does not contain enough instructions for ordering proof."

        let changedEntry = { entry with Instructions = List.rev entry.Instructions }
        let changedMain =
            { main with
                Blocks =
                    main.Blocks
                    |> List.map (fun block -> if block.Id = entry.Id then changedEntry else block) }
        let changedProgram =
            { execution with
                Functions =
                    execution.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.SemanticFunctionId = main.SemanticFunctionId then
                            changedMain
                        else
                            functionEntity) }

        let original = ExecutionIrSerializer.serialize execution
        let changed = ExecutionIrSerializer.serialize changedProgram
        if original = changed then
            failwith "Execution IR serialization incorrectly normalized semantic instruction order."
