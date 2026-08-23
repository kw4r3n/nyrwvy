namespace Nayamnyam.SemanticCore

[<RequireQualifiedAccess>]
type SnapshotError =
    | NegativeRevision of revision: int
    | DuplicateFunctionId of functionId: FunctionId

type FunctionEntity =
    { Id: FunctionId
      DisplayName: string }

type Snapshot =
    private
        { Id: SnapshotId
          Revision: int
          Functions: Map<FunctionId, FunctionEntity> }

[<RequireQualifiedAccess>]
module Snapshot =
    let private findDuplicateFunctionId (functions: FunctionEntity list) =
        functions
        |> List.countBy (fun functionEntity -> functionEntity.Id)
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map fst

    let create
        (id: SnapshotId)
        (revision: int)
        (functions: FunctionEntity list)
        : Result<Snapshot, SnapshotError> =
        if revision < 0 then
            Error(SnapshotError.NegativeRevision revision)
        else
            match findDuplicateFunctionId functions with
            | Some duplicateFunctionId ->
                Error(SnapshotError.DuplicateFunctionId duplicateFunctionId)
            | None ->
                let functionsById =
                    functions
                    |> List.map (fun functionEntity -> functionEntity.Id, functionEntity)
                    |> Map.ofList

                Ok
                    { Id = id
                      Revision = revision
                      Functions = functionsById }

    let id (snapshot: Snapshot) = snapshot.Id

    let revision (snapshot: Snapshot) = snapshot.Revision

    let functions (snapshot: Snapshot) =
        snapshot.Functions
        |> Map.toList
        |> List.map snd

    let tryFindFunction (functionId: FunctionId) (snapshot: Snapshot) =
        Map.tryFind functionId snapshot.Functions

    let renameFunction
        (proposedSnapshotId: SnapshotId)
        (functionId: FunctionId)
        (newDisplayName: string)
        (snapshot: Snapshot)
        : Snapshot option =
        match Map.tryFind functionId snapshot.Functions with
        | None ->
            None
        | Some currentFunction ->
            let renamedFunction =
                { currentFunction with
                    DisplayName = newDisplayName }

            let renamedFunctions =
                Map.add functionId renamedFunction snapshot.Functions

            Some
                { Id = proposedSnapshotId
                  Revision = snapshot.Revision + 1
                  Functions = renamedFunctions }
