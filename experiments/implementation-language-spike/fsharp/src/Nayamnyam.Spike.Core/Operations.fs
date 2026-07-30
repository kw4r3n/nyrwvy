namespace Nayamnyam.Spike

open System

module Operations =
    let private diagnostic code message =
        {
            Code = code
            Message = message
        }

    let private transactionTail (transactionId: TransactionId) =
        let text = StableId.value transactionId
        text.Substring("transaction:".Length)

    let private snapshotId transactionId =
        SnapshotId.create $"snapshot:{transactionTail transactionId}"

    let private obligationId transactionId (callId: CallId) (parameterId: ParameterId) =
        let callText = StableId.value callId
        let parameterText = StableId.value parameterId
        let callTail = callText.Substring("call:".Length)
        let parameterTail = parameterText.Substring("parameter:".Length)

        ObligationId.create
            $"obligation:{transactionTail transactionId}:{callTail}:{parameterTail}"

    let private blockingIds (snapshot: Snapshot) =
        snapshot.Obligations
        |> List.choose (fun obligation ->
            match obligation.Status with
            | Open -> Some obligation.Id)
        |> List.sortWith (fun left right ->
            StringComparer.Ordinal.Compare(
                StableId.value left,
                StableId.value right
            ))

    let private complete candidate diff =
        let blocking = blockingIds candidate

        if List.isEmpty blocking then
            Executable(candidate, diff)
        else
            Incomplete(candidate, diff, blocking)

    let private validateBase snapshot baseSnapshotId =
        if baseSnapshotId = snapshot.Id then
            []
        else
            [
                diagnostic
                    "stale_base_snapshot"
                    $"Base snapshot '{StableId.value baseSnapshotId}' does not match current snapshot '{StableId.value snapshot.Id}'."
            ]

    let renameFunction
        (snapshot: Snapshot)
        (baseSnapshotId: SnapshotId)
        (transactionId: TransactionId)
        (functionId: FunctionId)
        (newName: string)
        =
        let staleDiagnostics = validateBase snapshot baseSnapshotId

        if not (List.isEmpty staleDiagnostics) then
            Rejected(baseSnapshotId, staleDiagnostics)
        else
            let snapshotDiagnostics = Validation.validateSnapshot snapshot

            if not (List.isEmpty snapshotDiagnostics) then
                Rejected(baseSnapshotId, snapshotDiagnostics)
            else
                match
                    snapshot.Functions
                    |> List.tryFind (fun functionEntity ->
                        functionEntity.Id = functionId)
                with
                | None ->
                    Rejected(
                        baseSnapshotId,
                        [
                            diagnostic
                                "function_not_found"
                                $"Function '{StableId.value functionId}' was not found."
                        ]
                    )
                | Some target ->
                    let renamedFunctions =
                        snapshot.Functions
                        |> List.map (fun functionEntity ->
                            if functionEntity.Id = functionId then
                                {
                                    functionEntity with
                                        Name = newName
                                }
                            else
                                functionEntity)

                    let candidate =
                        {
                            snapshot with
                                Id = snapshotId transactionId
                                Functions = renamedFunctions
                        }

                    complete
                        candidate
                        [ FunctionRenamed(functionId, target.Name, newName) ]

    let addParameter
        (snapshot: Snapshot)
        (baseSnapshotId: SnapshotId)
        (transactionId: TransactionId)
        (functionId: FunctionId)
        (parameter: Parameter)
        =
        let staleDiagnostics = validateBase snapshot baseSnapshotId

        if not (List.isEmpty staleDiagnostics) then
            Rejected(baseSnapshotId, staleDiagnostics)
        else
            let snapshotDiagnostics = Validation.validateSnapshot snapshot

            if not (List.isEmpty snapshotDiagnostics) then
                Rejected(baseSnapshotId, snapshotDiagnostics)
            else
                match
                    snapshot.Functions
                    |> List.tryFind (fun functionEntity ->
                        functionEntity.Id = functionId)
                with
                | None ->
                    Rejected(
                        baseSnapshotId,
                        [
                            diagnostic
                                "function_not_found"
                                $"Function '{StableId.value functionId}' was not found."
                        ]
                    )
                | Some target ->
                    let duplicateParameter =
                        snapshot.Functions
                        |> List.collect (fun functionEntity ->
                            functionEntity.Parameters)
                        |> List.exists (fun existing ->
                            existing.Id = parameter.Id)

                    let knownType =
                        snapshot.Types
                        |> List.exists (fun typeEntity ->
                            typeEntity.Id = parameter.TypeId)

                    if duplicateParameter then
                        Rejected(
                            baseSnapshotId,
                            [
                                diagnostic
                                    "duplicate_parameter_id"
                                    $"Parameter ID '{StableId.value parameter.Id}' already exists."
                            ]
                        )
                    elif not knownType then
                        Rejected(
                            baseSnapshotId,
                            [
                                diagnostic
                                    "unknown_parameter_type"
                                    $"Parameter '{StableId.value parameter.Id}' references unknown type '{StableId.value parameter.TypeId}'."
                            ]
                        )
                    else
                        let changedFunctions =
                            snapshot.Functions
                            |> List.map (fun functionEntity ->
                                if functionEntity.Id = target.Id then
                                    {
                                        functionEntity with
                                            Parameters =
                                                functionEntity.Parameters
                                                @ [ parameter ]
                                    }
                                else
                                    functionEntity)

                        let createdObligations =
                            snapshot.Calls
                            |> List.filter (fun call ->
                                call.CalleeId = functionId
                                && not (
                                    call.Arguments
                                    |> List.exists (fun argument ->
                                        argument.ParameterId = parameter.Id)
                                ))
                            |> List.map (fun call ->
                                {
                                    Id =
                                        obligationId
                                            transactionId
                                            call.Id
                                            parameter.Id
                                    Kind =
                                        MissingArgument(
                                            call.Id,
                                            parameter.Id
                                        )
                                    Status = Open
                                })
                            |> List.sortWith (fun left right ->
                                StringComparer.Ordinal.Compare(
                                    StableId.value left.Id,
                                    StableId.value right.Id
                                ))

                        let collidingObligation =
                            createdObligations
                            |> List.tryFind (fun created ->
                                snapshot.Obligations
                                |> List.exists (fun existing ->
                                    existing.Id = created.Id))

                        match collidingObligation with
                        | Some collision ->
                            Rejected(
                                baseSnapshotId,
                                [
                                    diagnostic
                                        "duplicate_obligation_id"
                                        $"Obligation ID '{StableId.value collision.Id}' already exists."
                                ]
                            )
                        | None ->
                            let allObligations =
                                snapshot.Obligations
                                @ createdObligations
                                |> List.sortWith (fun left right ->
                                    StringComparer.Ordinal.Compare(
                                        StableId.value left.Id,
                                        StableId.value right.Id
                                    ))

                            let candidate =
                                {
                                    snapshot with
                                        Id = snapshotId transactionId
                                        Functions = changedFunctions
                                        Obligations = allObligations
                                }

                            let obligationChanges =
                                createdObligations
                                |> List.map (fun obligation ->
                                    ObligationCreated obligation.Id)

                            complete
                                candidate
                                (ParameterAdded(functionId, parameter.Id)
                                 :: obligationChanges)
