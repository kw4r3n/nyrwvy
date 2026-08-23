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
    | ObligationNotFound of obligationId: ObligationId
    | ObligationAlreadyResolved of obligationId: ObligationId
    | CallNotFound of callId: CallId
    | ParameterNotOwnedByCallTarget of callId: CallId * parameterId: ParameterId
    | ArgumentAlreadyBound of callId: CallId * parameterId: ParameterId
    | ValueNotFound of valueId: ValueId
    | RevisionLimitReached of revision: int

[<RequireQualifiedAccess>]
type ExecutionReadiness =
    | Executable
    | Incomplete

[<RequireQualifiedAccess>]
type SemanticChange =
    | FunctionRenamed of
        functionId: FunctionId *
        previousDisplayName: string *
        nextDisplayName: string
    | ParameterAdded of functionId: FunctionId * parameterId: ParameterId
    | ObligationCreated of obligationId: ObligationId
    | ArgumentBound of callId: CallId * parameterId: ParameterId * valueId: ValueId
    | ObligationResolved of obligationId: ObligationId
    | ExecutionReadinessChanged of
        previous: ExecutionReadiness *
        next: ExecutionReadiness

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

type ResolveMissingArgumentRequest =
    { TransactionId: TransactionId
      BaseSnapshot: SnapshotReference
      ProposedSnapshotId: SnapshotId
      ObligationId: ObligationId
      ValueId: ValueId }

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

    let private readiness (snapshot: Snapshot) =
        match Snapshot.openObligations snapshot with
        | [] ->
            ExecutionReadiness.Executable
        | _ ->
            ExecutionReadiness.Incomplete

    let private appendReadinessChange
        (previousReadiness: ExecutionReadiness)
        (snapshot: Snapshot)
        (semanticDiff: SemanticChange list)
        =
        let nextReadiness = readiness snapshot

        if previousReadiness = nextReadiness then
            semanticDiff
        else
            semanticDiff
            @ [ SemanticChange.ExecutionReadinessChanged(previousReadiness, nextReadiness) ]

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
        let previousReadiness = readiness snapshot

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

                                let semanticDiff =
                                    appendReadinessChange
                                        previousReadiness
                                        committedSnapshot
                                        semanticDiff

                                classify committedSnapshot semanticDiff

    let resolveMissingArgument
        (request: ResolveMissingArgumentRequest)
        (snapshot: Snapshot)
        : TransactionResult =
        let currentSnapshotId = Snapshot.id snapshot
        let previousReadiness = readiness snapshot

        match validateBaseSnapshot request.BaseSnapshot snapshot with
        | Some error ->
            TransactionResult.Rejected error
        | None ->
            match Snapshot.tryFindObligation request.ObligationId snapshot with
            | None ->
                TransactionResult.Rejected(OperationError.ObligationNotFound request.ObligationId)
            | Some obligation ->
                match obligation.Kind with
                | ObligationKind.MissingArgument ->
                    match obligation.Status with
                    | ObligationStatus.Resolved _ ->
                        TransactionResult.Rejected(
                            OperationError.ObligationAlreadyResolved request.ObligationId
                        )
                    | ObligationStatus.Open ->
                        if request.ProposedSnapshotId = currentSnapshotId then
                            TransactionResult.Rejected(
                                OperationError.ReusedSnapshotId request.ProposedSnapshotId
                            )
                        elif Snapshot.revision snapshot = Int32.MaxValue then
                            TransactionResult.Rejected(
                                OperationError.RevisionLimitReached(Snapshot.revision snapshot)
                            )
                        else
                            match
                                Snapshot.allCalls snapshot
                                |> List.tryFind (fun (_, call) -> call.Id = obligation.CallId)
                            with
                            | None ->
                                TransactionResult.Rejected(
                                    OperationError.CallNotFound obligation.CallId
                                )
                            | Some(callerFunctionId, call) ->
                                match Snapshot.tryFindFunction call.TargetFunctionId snapshot with
                                | None ->
                                    TransactionResult.Rejected(
                                        OperationError.FunctionNotFound call.TargetFunctionId
                                    )
                                | Some targetFunction ->
                                    let parameterBelongsToTarget =
                                        targetFunction.Parameters
                                        |> List.exists (fun parameter ->
                                            parameter.Id = obligation.ParameterId)

                                    if not parameterBelongsToTarget then
                                        TransactionResult.Rejected(
                                            OperationError.ParameterNotOwnedByCallTarget(
                                                call.Id,
                                                obligation.ParameterId
                                            )
                                        )
                                    elif Map.containsKey obligation.ParameterId call.ArgumentBindings then
                                        TransactionResult.Rejected(
                                            OperationError.ArgumentAlreadyBound(
                                                call.Id,
                                                obligation.ParameterId
                                            )
                                        )
                                    else
                                        match Snapshot.tryFindValue request.ValueId snapshot with
                                        | None ->
                                            TransactionResult.Rejected(
                                                OperationError.ValueNotFound request.ValueId
                                            )
                                        | Some _ ->
                                            match
                                                Snapshot.resolveMissingArgument
                                                    request.ProposedSnapshotId
                                                    request.TransactionId
                                                    callerFunctionId
                                                    call.Id
                                                    request.ObligationId
                                                    request.ValueId
                                                    snapshot
                                            with
                                            | None ->
                                                TransactionResult.Rejected(
                                                    OperationError.ObligationNotFound request.ObligationId
                                                )
                                            | Some committedSnapshot ->
                                                let semanticDiff =
                                                    [ SemanticChange.ArgumentBound(
                                                          call.Id,
                                                          obligation.ParameterId,
                                                          request.ValueId
                                                      )
                                                      SemanticChange.ObligationResolved request.ObligationId ]

                                                let semanticDiff =
                                                    appendReadinessChange
                                                        previousReadiness
                                                        committedSnapshot
                                                        semanticDiff

                                                classify committedSnapshot semanticDiff
