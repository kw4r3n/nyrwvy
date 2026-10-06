namespace Nyrwvy.Core

/// Structural call reachability, including callbacks and cleanup bodies; branches are conservative.
[<RequireQualifiedAccess>]
module ExecutionCallGraph =
    /// Select before projection/lowering: unrelated incomplete or unordered functions must
    /// not prevent an entry's execution. Follow semantic references, including function values
    /// and every capability implementation, conservatively through all nested branch graphs.
    /// Declarations remain complete so the backend's capability/type boundary is unchanged.
    let forSnapshotEntry (snapshot: Snapshot) entry =
        let functions = snapshot.Functions |> List.map (fun fn -> fn.Id, fn) |> Map.ofList
        let rec visit seen pending =
            match pending with
            | [] -> seen
            | id :: rest when Set.contains id seen -> visit seen rest
            | id :: rest ->
                let targets =
                    Map.tryFind id functions
                    |> Option.map (fun fn ->
                        ValueGraph.collectNodes fn.Body
                        |> List.collect SemanticNodeShape.functionReferences
                        |> List.map (fun reference -> reference.FunctionId))
                    |> Option.defaultValue []
                visit (Set.add id seen) (targets @ rest)
        let ids = visit Set.empty [ entry ]
        let selected = snapshot.Functions |> List.filter (fun fn -> Set.contains fn.Id ids)
        let nodes = selected |> List.collect (fun fn -> ValueGraph.collectNodes fn.Body) |> List.map (fun node -> node.Id) |> Set.ofList
        { snapshot with
            Functions = selected
            Obligations = snapshot.Obligations |> List.filter (fun obligation -> Set.contains obligation.CallNodeId nodes) }

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
