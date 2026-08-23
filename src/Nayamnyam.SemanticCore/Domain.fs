namespace Nayamnyam.SemanticCore

[<RequireQualifiedAccess>]
type SnapshotError =
    | NegativeRevision of revision: int
    | DuplicateFunctionId of functionId: FunctionId
    | DuplicateParameterId of functionId: FunctionId * parameterId: ParameterId
    | DuplicateCallId of callId: CallId

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

type Snapshot =
    private
        { Id: SnapshotId
          Revision: int
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
        |> List.tryPick (fun functionEntity ->
            functionEntity.Parameters
            |> List.countBy (fun parameter -> parameter.Id)
            |> List.tryFind (fun (_, count) -> count > 1)
            |> Option.map (fun (parameterId, _) -> functionEntity.Id, parameterId))

    let private findDuplicateCallId (functions: FunctionEntity list) =
        functions
        |> List.collect (fun functionEntity -> functionEntity.Calls)
        |> List.countBy (fun call -> call.Id)
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
                | Some(duplicateFunctionId, duplicateParameterId) ->
                    Error(
                        SnapshotError.DuplicateParameterId(
                            duplicateFunctionId,
                            duplicateParameterId
                        )
                    )
                | None ->
                    match findDuplicateCallId functions with
                    | Some duplicateCallId ->
                        Error(SnapshotError.DuplicateCallId duplicateCallId)
                    | None ->
                        let functionsById =
                            functions
                            |> List.map (fun functionEntity -> functionEntity.Id, functionEntity)
                            |> Map.ofList

                        Ok
                            { Id = id
                              Revision = revision
                              Functions = functionsById
                              Obligations = Map.empty }

    let id (snapshot: Snapshot) = snapshot.Id

    let revision (snapshot: Snapshot) = snapshot.Revision

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
                  Functions = renamedFunctions
                  Obligations = snapshot.Obligations }

    let internal addParameter
        (proposedSnapshotId: SnapshotId)
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
                  Functions = updatedFunctions
                  Obligations = snapshot.Obligations }

    let internal addObligations
        (newObligations: Obligation list)
        (snapshot: Snapshot)
        : Snapshot =
        let updatedObligations =
            newObligations
            |> List.fold
                (fun obligations obligation ->
                    Map.add obligation.Id obligation obligations)
                snapshot.Obligations

        { snapshot with
            Obligations = updatedObligations }
