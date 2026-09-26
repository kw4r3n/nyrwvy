namespace Nyrwvy.Core

/// Structural call reachability, including callbacks and cleanup bodies; branches are conservative.
[<RequireQualifiedAccess>]
module ExecutionCallGraph =
    let instructions (fn: ExecutionFunction) = fn.Blocks |> List.collect (fun block -> block.Instructions)

    let target (instruction: ExecutionInstruction) =
        match instruction.Kind with
        | ExecutionInstructionKind.DirectCall(functionId, _)
        | ExecutionInstructionKind.ListFold(_, _, functionId, _)
        | ExecutionInstructionKind.ListEffectFold(_, _, functionId, _)
        | ExecutionInstructionKind.UseResource(_, _, _, functionId, _)
        | ExecutionInstructionKind.ParallelAll(_, functionId, _)
        | ExecutionInstructionKind.ListMap(_, functionId, _)
        | ExecutionInstructionKind.ListFilter(_, functionId, _)
        | ExecutionInstructionKind.ListAny(_, functionId, _)
        | ExecutionInstructionKind.ListAll(_, functionId, _) -> Some functionId
        | _ -> None

    let reachable (program: ExecutionProgram) roots =
        let functions = program.Functions |> List.map (fun fn -> fn.SemanticFunctionId, fn) |> Map.ofList
        let rec visit seen pending =
            match pending with
            | [] -> seen
            | id :: rest when Set.contains id seen -> visit seen rest
            | id :: rest ->
                let targets =
                    Map.tryFind id functions
                    |> Option.map (instructions >> List.choose target)
                    |> Option.defaultValue []
                visit (Set.add id seen) (targets @ rest)
        visit Set.empty roots

    /// Keep declaration/type identities intact while emitting only the selected entry's functions.
    let forEntry (program: ExecutionProgram) entry =
        let ids = reachable program [ entry ]
        { program with Functions = program.Functions |> List.filter (fun fn -> Set.contains fn.SemanticFunctionId ids) }
