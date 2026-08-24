namespace Nayamnyam.Core

open System

[<RequireQualifiedAccess>]
type RenameTarget =
    | TypeTarget of TypeId
    | FunctionTarget of FunctionId

type SemanticOperation =
    | RenameDisplayName of RenameTarget * string

type TransactionRequest =
    { Id: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      Operations: SemanticOperation list }

type SymbolRename =
    | TypeRenamed of TypeId * oldDisplayName: string * newDisplayName: string
    | FunctionRenamed of FunctionId * oldDisplayName: string * newDisplayName: string

type SemanticDiff =
    { Renames: SymbolRename list }

[<Struct>]
type TransactionDiagnostic =
    { Code: string
      Message: string }

type TransactionSimulation =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshot: Snapshot
      Diff: SemanticDiff }

type SimulationResult =
    | Rejected of TransactionDiagnostic list
    | Proposed of TransactionSimulation

[<RequireQualifiedAccess>]
module Transaction =
    let private reject code message =
        Rejected [ { Code = code; Message = message } ]

    let private renameType id newDisplayName snapshot =
        match snapshot.Types |> List.tryFind (fun item -> item.Id = id) with
        | None -> Error("TargetNotFound", $"Type '{StableId.value id}' was not found.")
        | Some _ ->
            let types =
                snapshot.Types
                |> List.map (fun item ->
                    if item.Id = id then
                        { item with DisplayName = newDisplayName }
                    else
                        item)

            Ok { snapshot with Types = types }

    let private renameFunction id newDisplayName snapshot =
        match snapshot.Functions |> List.tryFind (fun item -> item.Id = id) with
        | None -> Error("TargetNotFound", $"Function '{StableId.value id}' was not found.")
        | Some _ ->
            let functions =
                snapshot.Functions
                |> List.map (fun item ->
                    if item.Id = id then
                        { item with DisplayName = newDisplayName }
                    else
                        item)

            Ok { snapshot with Functions = functions }

    let private applyOperation snapshot operation =
        match operation with
        | RenameDisplayName(_, newDisplayName) when String.IsNullOrWhiteSpace newDisplayName ->
            Error("InvalidDisplayName", "A display name cannot be blank.")
        | RenameDisplayName(RenameTarget.TypeTarget id, newDisplayName) ->
            renameType id newDisplayName snapshot
        | RenameDisplayName(RenameTarget.FunctionTarget id, newDisplayName) ->
            renameFunction id newDisplayName snapshot

    let private netRenames baseSnapshot proposedSnapshot =
        let typeNames =
            baseSnapshot.Types
            |> List.map (fun item -> item.Id, item.DisplayName)
            |> Map.ofList

        let functionNames =
            baseSnapshot.Functions
            |> List.map (fun item -> item.Id, item.DisplayName)
            |> Map.ofList

        let typeRenames =
            proposedSnapshot.Types
            |> List.choose (fun item ->
                match Map.tryFind item.Id typeNames with
                | Some oldName when oldName <> item.DisplayName ->
                    Some(TypeRenamed(item.Id, oldName, item.DisplayName))
                | _ -> None)

        let functionRenames =
            proposedSnapshot.Functions
            |> List.choose (fun item ->
                match Map.tryFind item.Id functionNames with
                | Some oldName when oldName <> item.DisplayName ->
                    Some(FunctionRenamed(item.Id, oldName, item.DisplayName))
                | _ -> None)

        typeRenames @ functionRenames
        |> List.sortBy (function
            | TypeRenamed(id, _, _) -> "type:" + StableId.value id
            | FunctionRenamed(id, _, _) -> "function:" + StableId.value id)

    let simulate (baseSnapshot: Snapshot) (request: TransactionRequest): SimulationResult =
        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ ->
            reject "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] when request.BaseSnapshotId <> baseSnapshot.Id ->
            reject "StaleBaseSnapshot" "The transaction base snapshot does not match the supplied snapshot."
        | [] when request.ProposedSnapshotId = baseSnapshot.Id ->
            reject "InvalidProposedSnapshotId" "The proposed snapshot ID must differ from the base snapshot ID."
        | [] when List.isEmpty request.Operations ->
            reject "NoOperations" "A transaction must contain at least one semantic operation."
        | [] ->
            let applied =
                request.Operations
                |> List.fold
                    (fun state operation ->
                        match state with
                        | Error error -> Error error
                        | Ok snapshot -> applyOperation snapshot operation)
                    (Ok baseSnapshot)

            match applied with
            | Error(code, message) -> reject code message
            | Ok changed ->
                let proposed =
                    { changed with
                        Id = request.ProposedSnapshotId
                        ParentId = Some baseSnapshot.Id }

                match SnapshotValidator.validate proposed with
                | issue :: _ ->
                    reject "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [] ->
                    let renames = netRenames baseSnapshot proposed

                    if List.isEmpty renames then
                        reject "NoChange" "The transaction has no net semantic change."
                    else
                        Proposed
                            { TransactionId = request.Id
                              BaseSnapshotId = request.BaseSnapshotId
                              ProposedSnapshot = proposed
                              Diff = { Renames = renames } }
