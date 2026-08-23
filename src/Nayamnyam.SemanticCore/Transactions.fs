namespace Nayamnyam.SemanticCore

open System

[<RequireQualifiedAccess>]
type OperationError =
    | StaleBaseSnapshot of requested: SnapshotId * actual: SnapshotId
    | FunctionNotFound of functionId: FunctionId
    | BlankDisplayName
    | ReusedSnapshotId of snapshotId: SnapshotId

[<RequireQualifiedAccess>]
type SemanticChange =
    | FunctionRenamed of
        functionId: FunctionId *
        previousDisplayName: string *
        nextDisplayName: string

[<RequireQualifiedAccess>]
type TransactionResult =
    | Executable of committedSnapshot: Snapshot * semanticDiff: SemanticChange list
    | NoChange of baseSnapshot: Snapshot
    | Rejected of error: OperationError

type RenameFunctionRequest =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      FunctionId: FunctionId
      NewDisplayName: string }

[<RequireQualifiedAccess>]
module Transactions =
    let renameFunction
        (request: RenameFunctionRequest)
        (snapshot: Snapshot)
        : TransactionResult =
        let currentSnapshotId = Snapshot.id snapshot

        if request.BaseSnapshotId <> currentSnapshotId then
            TransactionResult.Rejected(
                OperationError.StaleBaseSnapshot(request.BaseSnapshotId, currentSnapshotId)
            )
        else
            match Snapshot.tryFindFunction request.FunctionId snapshot with
            | None ->
                TransactionResult.Rejected(OperationError.FunctionNotFound request.FunctionId)
            | Some currentFunction ->
                if String.IsNullOrWhiteSpace request.NewDisplayName then
                    TransactionResult.Rejected OperationError.BlankDisplayName
                elif currentFunction.DisplayName = request.NewDisplayName then
                    TransactionResult.NoChange snapshot
                elif request.ProposedSnapshotId = currentSnapshotId then
                    TransactionResult.Rejected(
                        OperationError.ReusedSnapshotId request.ProposedSnapshotId
                    )
                else
                    match
                        Snapshot.renameFunction
                            request.ProposedSnapshotId
                            request.FunctionId
                            request.NewDisplayName
                            snapshot
                    with
                    | None ->
                        TransactionResult.Rejected(
                            OperationError.FunctionNotFound request.FunctionId
                        )
                    | Some committedSnapshot ->
                        let semanticDiff =
                            [ SemanticChange.FunctionRenamed(
                                request.FunctionId,
                                currentFunction.DisplayName,
                                request.NewDisplayName
                              ) ]

                        TransactionResult.Executable(committedSnapshot, semanticDiff)
