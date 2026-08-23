namespace Nayamnyam.SemanticCore

[<RequireQualifiedAccess>]
type SnapshotError =
    | NegativeRevision of revision: int
    | DuplicateFunctionId of functionId: FunctionId
    | DuplicateParameterId of parameterId: ParameterId
    | DuplicateCallId of callId: CallId
    | UnknownTargetFunction of callId: CallId * targetFunctionId: FunctionId
    | ArgumentBindingKeyMismatch of
        callId: CallId *
        bindingKey: ParameterId *
        bindingParameterId: ParameterId
    | UnknownArgumentParameter of callId: CallId * parameterId: ParameterId
    | ArgumentParameterTargetsWrongFunction of
        callId: CallId *
        targetFunctionId: FunctionId *
        parameterId: ParameterId
    | MissingArgument of callId: CallId * parameterId: ParameterId

type Parameter =
    { Id: ParameterId
      DisplayName: string }

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

[<RequireQualifiedAccess>]
type ObligationStatus =
    | Open
    | Resolved

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
          Functions: Map<FunctionId, FunctionEntity>
          Obligations: Map<ObligationId, Obligation> }

[<RequireQualifiedAccess>]
module Snapshot =
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

    let private findInvalidCall (functions: FunctionEntity list) =
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
                                if ownerFunctionId = call.TargetFunctionId then
                                    None
                                else
                                    Some(
                                        SnapshotError.ArgumentParameterTargetsWrongFunction(
                                            call.Id,
                                            call.TargetFunctionId,
                                            binding.ParameterId
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
        (functions: FunctionEntity list)
        : Result<Snapshot, SnapshotError> =
        if revision < 0 then
            Error(SnapshotError.NegativeRevision revision)
        else
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
                        match findInvalidCall functions with
                        | Some error ->
                            Error error
                        | None ->
                            let functionsById =
                                functions
                                |> List.map (fun functionEntity -> functionEntity.Id, functionEntity)
                                |> Map.ofList

                            Ok
                                { Id = id
                                  Revision = revision
                                  CommitTransactionId = None
                                  Functions = functionsById
                                  Obligations = Map.empty }

    let id (snapshot: Snapshot) = snapshot.Id

    let revision (snapshot: Snapshot) = snapshot.Revision

    let reference (snapshot: Snapshot) =
        { Id = snapshot.Id
          Revision = snapshot.Revision
          CommitTransactionId = snapshot.CommitTransactionId }

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
            | ObligationStatus.Resolved ->
                None)

    let tryFindFunction (functionId: FunctionId) (snapshot: Snapshot) =
        Map.tryFind functionId snapshot.Functions

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
                  Functions = updatedFunctions
                  Obligations = snapshot.Obligations }

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
