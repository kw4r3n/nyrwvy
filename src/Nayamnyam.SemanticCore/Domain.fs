namespace Nayamnyam.SemanticCore

[<RequireQualifiedAccess>]
type SnapshotError =
    | NegativeRevision of revision: int
    | DuplicateValueId of valueId: ValueId
    | DuplicateFunctionId of functionId: FunctionId
    | DuplicateParameterId of parameterId: ParameterId
    | DuplicateCallId of callId: CallId
    | UnknownTargetFunction of callId: CallId * targetFunctionId: FunctionId
    | ArgumentBindingKeyMismatch of
        callId: CallId *
        bindingKey: ParameterId *
        bindingParameterId: ParameterId
    | UnknownArgumentParameter of callId: CallId * parameterId: ParameterId
    | UnknownArgumentValue of callId: CallId * valueId: ValueId
    | ArgumentParameterTargetsWrongFunction of
        callId: CallId *
        targetFunctionId: FunctionId *
        parameterId: ParameterId
    | MissingArgument of callId: CallId * parameterId: ParameterId

type Parameter =
    { Id: ParameterId
      DisplayName: string }

type ValueEntity =
    { Id: ValueId }

type ArgumentBinding =
    { ParameterId: ParameterId
      ValueId: ValueId }

type Call =
    { Id: CallId
      TargetFunctionId: FunctionId
      ArgumentBindings: Map<ParameterId, ArgumentBinding> }

type FunctionEntity =
    { Id: FunctionId
      DisplayName: string
      Parameters: Parameter list
      Calls: Call list }

[<RequireQualifiedAccess>]
type ObligationKind =
    | MissingArgument

type ObligationResolution =
    { TransactionId: TransactionId
      ValueId: ValueId }

[<RequireQualifiedAccess>]
type ObligationStatus =
    | Open
    | Resolved of resolution: ObligationResolution

type Obligation =
    { Id: ObligationId
      Kind: ObligationKind
      CallId: CallId
      ParameterId: ParameterId
      OriginTransactionId: TransactionId
      Status: ObligationStatus }

type SnapshotReference =
    { Id: SnapshotId
      Revision: int
      CommitTransactionId: TransactionId option }

type Snapshot =
    private
        { Id: SnapshotId
          Revision: int
          CommitTransactionId: TransactionId option
          Values: Map<ValueId, ValueEntity>
          Functions: Map<FunctionId, FunctionEntity>
          Obligations: Map<ObligationId, Obligation> }

[<RequireQualifiedAccess>]
module Snapshot =
    let private findDuplicateValueId (values: ValueEntity list) =
        values
        |> List.countBy (fun value -> value.Id)
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map fst

    let private findDuplicateFunctionId (functions: FunctionEntity list) =
        functions
        |> List.countBy (fun functionEntity -> functionEntity.Id)
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map fst

    let private findDuplicateParameterId (functions: FunctionEntity list) =
        functions
        |> List.collect (fun functionEntity -> functionEntity.Parameters)
        |> List.countBy (fun parameter -> parameter.Id)
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map fst

    let private findDuplicateCallId (functions: FunctionEntity list) =
        functions
        |> List.collect (fun functionEntity -> functionEntity.Calls)
        |> List.countBy (fun call -> call.Id)
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map fst

    let private findInvalidCall
        (values: ValueEntity list)
        (functions: FunctionEntity list)
        =
        let functionsById =
            functions
            |> List.map (fun functionEntity -> functionEntity.Id, functionEntity)
            |> Map.ofList

        let parameterOwners =
            functions
            |> List.collect (fun functionEntity ->
                functionEntity.Parameters
                |> List.map (fun parameter -> parameter.Id, functionEntity.Id))
            |> Map.ofList

        let valueIds =
            values
            |> List.map (fun value -> value.Id)
            |> Set.ofList

        functions
        |> List.collect (fun functionEntity -> functionEntity.Calls)
        |> List.tryPick (fun call ->
            match Map.tryFind call.TargetFunctionId functionsById with
            | None ->
                Some(SnapshotError.UnknownTargetFunction(call.Id, call.TargetFunctionId))
            | Some targetFunction ->
                let invalidBinding =
                    call.ArgumentBindings
                    |> Map.toList
                    |> List.tryPick (fun (bindingKey, binding) ->
                        if bindingKey <> binding.ParameterId then
                            Some(
                                SnapshotError.ArgumentBindingKeyMismatch(
                                    call.Id,
                                    bindingKey,
                                    binding.ParameterId
                                )
                            )
                        else
                            match Map.tryFind binding.ParameterId parameterOwners with
                            | None ->
                                Some(
                                    SnapshotError.UnknownArgumentParameter(
                                        call.Id,
                                        binding.ParameterId
                                    )
                                )
                            | Some ownerFunctionId ->
                                if ownerFunctionId <> call.TargetFunctionId then
                                    Some(
                                        SnapshotError.ArgumentParameterTargetsWrongFunction(
                                            call.Id,
                                            call.TargetFunctionId,
                                            binding.ParameterId
                                        )
                                    )
                                elif Set.contains binding.ValueId valueIds then
                                    None
                                else
                                    Some(
                                        SnapshotError.UnknownArgumentValue(
                                            call.Id,
                                            binding.ValueId
                                        )
                                    ))

                match invalidBinding with
                | Some error ->
                    Some error
                | None ->
                    targetFunction.Parameters
                    |> List.tryPick (fun parameter ->
                        if Map.containsKey parameter.Id call.ArgumentBindings then
                            None
                        else
                            Some(SnapshotError.MissingArgument(call.Id, parameter.Id))))

    let private findDuplicateObligationId (obligations: Obligation list) =
        obligations
        |> List.countBy (fun obligation -> obligation.Id)
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map fst

    let create
        (id: SnapshotId)
        (revision: int)
        (values: ValueEntity list)
        (functions: FunctionEntity list)
        : Result<Snapshot, SnapshotError> =
        if revision < 0 then
            Error(SnapshotError.NegativeRevision revision)
        else
            match findDuplicateValueId values with
            | Some duplicateValueId ->
                Error(SnapshotError.DuplicateValueId duplicateValueId)
            | None ->
                match findDuplicateFunctionId functions with
                | Some duplicateFunctionId ->
                    Error(SnapshotError.DuplicateFunctionId duplicateFunctionId)
                | None ->
                    match findDuplicateParameterId functions with
                    | Some duplicateParameterId ->
                        Error(SnapshotError.DuplicateParameterId duplicateParameterId)
                    | None ->
                        match findDuplicateCallId functions with
                        | Some duplicateCallId ->
                            Error(SnapshotError.DuplicateCallId duplicateCallId)
                        | None ->
                            match findInvalidCall values functions with
                            | Some error ->
                                Error error
                            | None ->
                                let valuesById =
                                    values
                                    |> List.map (fun value -> value.Id, value)
                                    |> Map.ofList

                                let functionsById =
                                    functions
                                    |> List.map (fun functionEntity -> functionEntity.Id, functionEntity)
                                    |> Map.ofList

                                Ok
                                    { Id = id
                                      Revision = revision
                                      CommitTransactionId = None
                                      Values = valuesById
                                      Functions = functionsById
                                      Obligations = Map.empty }

    let id (snapshot: Snapshot) = snapshot.Id

    let revision (snapshot: Snapshot) = snapshot.Revision

    let reference (snapshot: Snapshot) =
        { Id = snapshot.Id
          Revision = snapshot.Revision
          CommitTransactionId = snapshot.CommitTransactionId }

    let values (snapshot: Snapshot) =
        snapshot.Values
        |> Map.toList
        |> List.map snd

    let functions (snapshot: Snapshot) =
        snapshot.Functions
        |> Map.toList
        |> List.map snd

    let obligations (snapshot: Snapshot) =
        snapshot.Obligations
        |> Map.toList
        |> List.map snd

    let openObligations (snapshot: Snapshot) =
        snapshot.Obligations
        |> Map.toList
        |> List.choose (fun (_, obligation) ->
            match obligation.Status with
            | ObligationStatus.Open ->
                Some obligation
            | ObligationStatus.Resolved _ ->
                None)

    let tryFindFunction (functionId: FunctionId) (snapshot: Snapshot) =
        Map.tryFind functionId snapshot.Functions

    let tryFindValue (valueId: ValueId) (snapshot: Snapshot) =
        Map.tryFind valueId snapshot.Values

    let tryFindObligation (obligationId: ObligationId) (snapshot: Snapshot) =
        Map.tryFind obligationId snapshot.Obligations

    let allCalls (snapshot: Snapshot) =
        snapshot.Functions
        |> Map.toList
        |> List.collect (fun (callerFunctionId, functionEntity) ->
            functionEntity.Calls
            |> List.map (fun call -> callerFunctionId, call))

    let internal renameFunction
        (proposedSnapshotId: SnapshotId)
        (commitTransactionId: TransactionId)
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
                  CommitTransactionId = Some commitTransactionId
                  Values = snapshot.Values
                  Functions = renamedFunctions
                  Obligations = snapshot.Obligations }

    let internal addParameter
        (proposedSnapshotId: SnapshotId)
        (commitTransactionId: TransactionId)
        (functionId: FunctionId)
        (parameter: Parameter)
        (snapshot: Snapshot)
        : Snapshot option =
        match Map.tryFind functionId snapshot.Functions with
        | None ->
            None
        | Some currentFunction ->
            let updatedFunction =
                { currentFunction with
                    Parameters = currentFunction.Parameters @ [ parameter ] }

            let updatedFunctions =
                Map.add functionId updatedFunction snapshot.Functions

            Some
                { Id = proposedSnapshotId
                  Revision = snapshot.Revision + 1
                  CommitTransactionId = Some commitTransactionId
                  Values = snapshot.Values
                  Functions = updatedFunctions
                  Obligations = snapshot.Obligations }

    let internal resolveMissingArgument
        (proposedSnapshotId: SnapshotId)
        (commitTransactionId: TransactionId)
        (callerFunctionId: FunctionId)
        (callId: CallId)
        (obligationId: ObligationId)
        (valueId: ValueId)
        (snapshot: Snapshot)
        : Snapshot option =
        match
            Map.tryFind callerFunctionId snapshot.Functions,
            Map.tryFind obligationId snapshot.Obligations
        with
        | Some callerFunction, Some obligation ->
            match callerFunction.Calls |> List.tryFind (fun call -> call.Id = callId) with
            | None ->
                None
            | Some _ ->
                let argumentBinding =
                    { ParameterId = obligation.ParameterId
                      ValueId = valueId }

                let updatedCalls =
                    callerFunction.Calls
                    |> List.map (fun call ->
                        if call.Id = callId then
                            { call with
                                ArgumentBindings =
                                    Map.add
                                        obligation.ParameterId
                                        argumentBinding
                                        call.ArgumentBindings }
                        else
                            call)

                let updatedFunction =
                    { callerFunction with
                        Calls = updatedCalls }

                let updatedObligation =
                    { obligation with
                        Status =
                            ObligationStatus.Resolved(
                                { TransactionId = commitTransactionId
                                  ValueId = valueId }
                            ) }

                Some
                    { Id = proposedSnapshotId
                      Revision = snapshot.Revision + 1
                      CommitTransactionId = Some commitTransactionId
                      Values = snapshot.Values
                      Functions = Map.add callerFunctionId updatedFunction snapshot.Functions
                      Obligations =
                        Map.add obligationId updatedObligation snapshot.Obligations }
        | None, _ ->
            None
        | _, None ->
            None

    let internal addObligations
        (newObligations: Obligation list)
        (snapshot: Snapshot)
        : Result<Snapshot, ObligationId> =
        let existingObligations =
            snapshot.Obligations
            |> Map.toList
            |> List.map snd

        match findDuplicateObligationId (existingObligations @ newObligations) with
        | Some duplicateObligationId ->
            Error duplicateObligationId
        | None ->
            let updatedObligations =
                newObligations
                |> List.fold
                    (fun obligations obligation ->
                        Map.add obligation.Id obligation obligations)
                    snapshot.Obligations

            Ok
                { snapshot with
                    Obligations = updatedObligations }
