namespace Nyrwvy.Core

/// Structural call reachability, including callbacks and cleanup bodies; branches are conservative.
[<RequireQualifiedAccess>]
module ExecutionCallGraph =
    let instructions (fn: ExecutionFunction) = fn.Blocks |> List.collect (fun block -> block.Instructions)

    let targets (instruction: ExecutionInstruction) =
        ExecutionInstructionKind.callbackTargets instruction.Kind |> List.map snd

    let reachable (program: ExecutionProgram) roots =
        let functions = program.Functions |> List.map (fun fn -> fn.SemanticFunctionId, fn) |> Map.ofList
        let rec visit seen pending =
            match pending with
            | [] -> seen
            | id :: rest when Set.contains id seen -> visit seen rest
            | id :: rest ->
                let targets =
                    Map.tryFind id functions
                    |> Option.map (instructions >> List.collect targets)
                    |> Option.defaultValue []
                visit (Set.add id seen) (targets @ rest)
        visit Set.empty roots

    /// Keep declaration/type identities intact while emitting only the selected entry's functions.
    let forEntry (program: ExecutionProgram) entry =
        let ids = reachable program [ entry ]
        { program with Functions = program.Functions |> List.filter (fun fn -> Set.contains fn.SemanticFunctionId ids) }
