namespace Nayamnyam.SemanticCore

open System

[<RequireQualifiedAccess>]
type OperationError =
    | StaleBaseSnapshot of requested: SnapshotId * actual: SnapshotId
    | FunctionNotFound of functionId: FunctionId
    | BlankDisplayName
    | ReusedSnapshotId of snapshotId: SnapshotId
    | BlankParameterDisplayName of parameterId: ParameterId
    | ParameterAlreadyExists of functionId: FunctionId * parameterId: ParameterId

[<RequireQualifiedAccess>]
type SemanticChange =
    | FunctionRenamed of
        functionId: FunctionId *
        previousDisplayName: string *
        nextDisplayName: string
    | ParameterAdded of functionId: FunctionId * parameterId: ParameterId
    | ObligationCreated of obligationId: ObligationId

[<RequireQualifiedAccess>]
type TransactionResult =
    | Executable of committedSnapshot: Snapshot * semanticDiff: SemanticChange list
    | Incomplete of
        committedSnapshot: Snapshot *
        semanticDiff: SemanticChange list *
        blockingObligations: Obligation list
    | NoChange of baseSnapshot: Snapshot
    | Rejected of error: OperationError

type RenameFunctionRequest =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      FunctionId: FunctionId
      NewDisplayName: string }

type AddParameterRequest =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      FunctionId: FunctionId
      Parameter: Parameter }

[<RequireQualifiedAccess>]
module Transactions =
    let private requireId<'Tag> (value: string) : StableId<'Tag> =
        match StableId.create<'Tag> value with
        | Ok stableId ->
            stableId
        | Error StableIdError.BlankValue ->
            invalidOp "An internally generated stable ID was blank."

    let private classify
        (snapshot: Snapshot)
        (semanticDiff: SemanticChange list)
        : TransactionResult =
        match Snapshot.openObligations snapshot with
        | [] ->
            TransactionResult.Executable(snapshot, semanticDiff)
        | blockingObligations ->
            TransactionResult.Incomplete(snapshot, semanticDiff, blockingObligations)

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

                        classify committedSnapshot semanticDiff

    let addParameter
        (request: AddParameterRequest)
        (snapshot: Snapshot)
        : TransactionResult =
        let currentSnapshotId = Snapshot.id snapshot

        if request.BaseSnapshotId <> currentSnapshotId then
            TransactionResult.Rejected(
                OperationError.StaleBaseSnapshot(request.BaseSnapshotId, currentSnapshotId)
            )
        elif String.IsNullOrWhiteSpace request.Parameter.DisplayName then
            TransactionResult.Rejected(
                OperationError.BlankParameterDisplayName request.Parameter.Id
            )
        elif request.ProposedSnapshotId = currentSnapshotId then
            TransactionResult.Rejected(
                OperationError.ReusedSnapshotId request.ProposedSnapshotId
            )
        else
            match Snapshot.tryFindFunction request.FunctionId snapshot with
            | None ->
                TransactionResult.Rejected(OperationError.FunctionNotFound request.FunctionId)
            | Some currentFunction ->
                let parameterAlreadyExists =
                    currentFunction.Parameters
                    |> List.exists (fun existingParameter ->
                        existingParameter.Id = request.Parameter.Id)

                if parameterAlreadyExists then
                    TransactionResult.Rejected(
                        OperationError.ParameterAlreadyExists(
                            request.FunctionId,
                            request.Parameter.Id
                        )
                    )
                else
                    let affectedCalls =
                        Snapshot.allCalls snapshot
                        |> List.map snd
                        |> List.filter (fun call ->
                            call.TargetFunctionId = request.FunctionId
                            && not (
                                Map.containsKey
                                    request.Parameter.Id
                                    call.ArgumentBindings
                            ))

                    let newObligations =
                        affectedCalls
                        |> List.map (fun call ->
                            let obligationId =
                                requireId<ObligationIdTag>
                                    $"obligation:missing-argument:{StableId.value call.Id}:{StableId.value request.Parameter.Id}"

                            { Id = obligationId
                              Kind = ObligationKind.MissingArgument
                              CallId = call.Id
                              ParameterId = request.Parameter.Id
                              OriginTransactionId = request.TransactionId
                              Status = ObligationStatus.Open })

                    match
                        Snapshot.addParameter
                            request.ProposedSnapshotId
                            request.FunctionId
                            request.Parameter
                            snapshot
                    with
                    | None ->
                        TransactionResult.Rejected(
                            OperationError.FunctionNotFound request.FunctionId
                        )
                    | Some snapshotWithParameter ->
                        let committedSnapshot =
                            Snapshot.addObligations newObligations snapshotWithParameter

                        let semanticDiff =
                            SemanticChange.ParameterAdded(
                                request.FunctionId,
                                request.Parameter.Id
                            )
                            :: (newObligations
                                |> List.map (fun obligation ->
                                    SemanticChange.ObligationCreated obligation.Id))

                        classify committedSnapshot semanticDiff
