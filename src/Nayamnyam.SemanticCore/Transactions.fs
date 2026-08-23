namespace Nayamnyam.SemanticCore

open System

[<RequireQualifiedAccess>]
type OperationError =
    | StaleBaseSnapshot of requested: SnapshotReference * actual: SnapshotReference
    | FunctionNotFound of functionId: FunctionId
    | BlankDisplayName
    | ReusedSnapshotId of snapshotId: SnapshotId
    | BlankParameterDisplayName of parameterId: ParameterId
    | ParameterAlreadyExists of parameterId: ParameterId
    | ObligationIdAlreadyExists of obligationId: ObligationId
    | RevisionLimitReached of revision: int

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
      BaseSnapshot: SnapshotReference
      ProposedSnapshotId: SnapshotId
      FunctionId: FunctionId
      NewDisplayName: string }

type AddParameterRequest =
    { TransactionId: TransactionId
      BaseSnapshot: SnapshotReference
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

    let private formatIdComponent (stableId: StableId<'Tag>) =
        let value = StableId.value stableId

        // Length prefixes preserve opaque ID text without delimiter collisions.
        $"{value.Length}:{value}"

    let private missingArgumentObligationId
        (callId: CallId)
        (parameterId: ParameterId)
        : ObligationId =
        let callComponent = formatIdComponent callId
        let parameterComponent = formatIdComponent parameterId

        requireId<ObligationIdTag>
            $"obligation:missing-argument:{callComponent}{parameterComponent}"

    let private validateBaseSnapshot
        (requested: SnapshotReference)
        (snapshot: Snapshot)
        : OperationError option =
        let actual = Snapshot.reference snapshot

        if requested = actual then
            None
        else
            Some(OperationError.StaleBaseSnapshot(requested, actual))

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

        match validateBaseSnapshot request.BaseSnapshot snapshot with
        | Some error ->
            TransactionResult.Rejected error
        | None ->
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
                elif Snapshot.revision snapshot = Int32.MaxValue then
                    TransactionResult.Rejected(
                        OperationError.RevisionLimitReached(Snapshot.revision snapshot)
                    )
                else
                    match
                        Snapshot.renameFunction
                            request.ProposedSnapshotId
                            request.TransactionId
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

        match validateBaseSnapshot request.BaseSnapshot snapshot with
        | Some error ->
            TransactionResult.Rejected error
        | None ->
            if String.IsNullOrWhiteSpace request.Parameter.DisplayName then
                TransactionResult.Rejected(
                    OperationError.BlankParameterDisplayName request.Parameter.Id
                )
            elif request.ProposedSnapshotId = currentSnapshotId then
                TransactionResult.Rejected(
                    OperationError.ReusedSnapshotId request.ProposedSnapshotId
                )
            elif Snapshot.revision snapshot = Int32.MaxValue then
                TransactionResult.Rejected(
                    OperationError.RevisionLimitReached(Snapshot.revision snapshot)
                )
            else
                match Snapshot.tryFindFunction request.FunctionId snapshot with
                | None ->
                    TransactionResult.Rejected(OperationError.FunctionNotFound request.FunctionId)
                | Some _ ->
                    let parameterAlreadyExists =
                        Snapshot.functions snapshot
                        |> List.collect (fun functionEntity -> functionEntity.Parameters)
                        |> List.exists (fun existingParameter ->
                            existingParameter.Id = request.Parameter.Id)

                    if parameterAlreadyExists then
                        TransactionResult.Rejected(
                            OperationError.ParameterAlreadyExists request.Parameter.Id
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
                                { Id =
                                    missingArgumentObligationId
                                        call.Id
                                        request.Parameter.Id
                                  Kind = ObligationKind.MissingArgument
                                  CallId = call.Id
                                  ParameterId = request.Parameter.Id
                                  OriginTransactionId = request.TransactionId
                                  Status = ObligationStatus.Open })
                            |> List.sortWith (fun left right ->
                                StringComparer.Ordinal.Compare(
                                    StableId.value left.Id,
                                    StableId.value right.Id
                                ))

                        match
                            Snapshot.addParameter
                                request.ProposedSnapshotId
                                request.TransactionId
                                request.FunctionId
                                request.Parameter
                                snapshot
                        with
                        | None ->
                            TransactionResult.Rejected(
                                OperationError.FunctionNotFound request.FunctionId
                            )
                        | Some snapshotWithParameter ->
                            match Snapshot.addObligations newObligations snapshotWithParameter with
                            | Error duplicateObligationId ->
                                TransactionResult.Rejected(
                                    OperationError.ObligationIdAlreadyExists duplicateObligationId
                                )
                            | Ok committedSnapshot ->
                                let semanticDiff =
                                    SemanticChange.ParameterAdded(
                                        request.FunctionId,
                                        request.Parameter.Id
                                    )
                                    :: (newObligations
                                        |> List.map (fun obligation ->
                                            SemanticChange.ObligationCreated obligation.Id))

                                classify committedSnapshot semanticDiff
