namespace Nayamnyam.Spike

open System

module Validation =
    let private diagnostic code message =
        {
            Code = code
            Message = message
        }

    let private duplicateValues values =
        values
        |> List.countBy id
        |> List.choose (fun (value, count) ->
            if count > 1 then
                Some value
            else
                None)

    let private duplicateDiagnostics code label render values =
        values
        |> duplicateValues
        |> List.sortWith (fun left right ->
            StringComparer.Ordinal.Compare(render left, render right))
        |> List.map (fun value ->
            diagnostic code $"Duplicate {label} '{render value}'.")

    let private validateTopLevelIds (snapshot: Snapshot) =
        let typeDiagnostics =
            snapshot.Types
            |> List.map (fun entity -> entity.Id)
            |> duplicateDiagnostics "duplicate_type_id" "type ID" StableId.value

        let functionDiagnostics =
            snapshot.Functions
            |> List.map (fun entity -> entity.Id)
            |> duplicateDiagnostics "duplicate_function_id" "function ID" StableId.value

        let callDiagnostics =
            snapshot.Calls
            |> List.map (fun entity -> entity.Id)
            |> duplicateDiagnostics "duplicate_call_id" "call ID" StableId.value

        let obligationDiagnostics =
            snapshot.Obligations
            |> List.map (fun entity -> entity.Id)
            |> duplicateDiagnostics "duplicate_obligation_id" "obligation ID" StableId.value

        typeDiagnostics
        @ functionDiagnostics
        @ callDiagnostics
        @ obligationDiagnostics

    let private parametersWithOwners (snapshot: Snapshot) =
        snapshot.Functions
        |> List.collect (fun functionEntity ->
            functionEntity.Parameters
            |> List.map (fun parameter -> parameter, functionEntity.Id))

    let private validateParameterIds (snapshot: Snapshot) =
        parametersWithOwners snapshot
        |> List.map (fun (parameter, ownerId) ->
            ignore ownerId
            parameter.Id)
        |> duplicateDiagnostics "duplicate_parameter_id" "parameter ID" StableId.value

    let private createFunctionMap (snapshot: Snapshot) =
        snapshot.Functions
        |> List.fold
            (fun functions functionEntity ->
                Map.add functionEntity.Id functionEntity functions)
            Map.empty

    let private createParameterOwnerMap (snapshot: Snapshot) =
        parametersWithOwners snapshot
        |> List.fold
            (fun owners (parameter, ownerId) ->
                Map.add parameter.Id ownerId owners)
            Map.empty

    let private validateParameterTypes (snapshot: Snapshot) =
        let typeIds =
            snapshot.Types
            |> List.map (fun typeEntity -> typeEntity.Id)
            |> Set.ofList

        parametersWithOwners snapshot
        |> List.choose (fun (parameter, ownerId) ->
            if Set.contains parameter.TypeId typeIds then
                None
            else
                Some(
                    diagnostic
                        "unknown_parameter_type"
                        $"Parameter '{StableId.value parameter.Id}' on function '{StableId.value ownerId}' references unknown type '{StableId.value parameter.TypeId}'."
                ))

    let private validateCalls (snapshot: Snapshot) =
        let functions = createFunctionMap snapshot
        let parameterOwners = createParameterOwnerMap snapshot

        snapshot.Calls
        |> List.collect (fun call ->
            let calleeDiagnostics =
                match Map.tryFind call.CalleeId functions with
                | Some functionEntity ->
                    ignore functionEntity
                    []
                | None ->
                    [
                        diagnostic
                            "unknown_callee"
                            $"Call '{StableId.value call.Id}' references unknown function '{StableId.value call.CalleeId}'."
                    ]

            let duplicateArgumentDiagnostics =
                call.Arguments
                |> List.map (fun argument -> argument.ParameterId)
                |> duplicateDiagnostics
                    "duplicate_argument_binding"
                    $"argument binding on call '{StableId.value call.Id}' for parameter"
                    StableId.value

            let ownershipDiagnostics =
                call.Arguments
                |> List.choose (fun argument ->
                    match Map.tryFind argument.ParameterId parameterOwners with
                    | Some ownerId ->
                        if ownerId = call.CalleeId then
                            None
                        else
                            Some(
                                diagnostic
                                    "invalid_argument_parameter_ownership"
                                    $"Call '{StableId.value call.Id}' binds parameter '{StableId.value argument.ParameterId}' owned by function '{StableId.value ownerId}', not its callee '{StableId.value call.CalleeId}'."
                            )
                    | None ->
                        Some(
                            diagnostic
                                "unknown_argument_parameter"
                                $"Call '{StableId.value call.Id}' binds unknown parameter '{StableId.value argument.ParameterId}'."
                        ))

            calleeDiagnostics
            @ duplicateArgumentDiagnostics
            @ ownershipDiagnostics)

    let private validateObligations (snapshot: Snapshot) =
        let calls =
            snapshot.Calls
            |> List.fold (fun result call -> Map.add call.Id call result) Map.empty

        let parameterOwners = createParameterOwnerMap snapshot

        snapshot.Obligations
        |> List.collect (fun obligation ->
            let statusDiagnostics =
                match obligation.Status with
                | Open -> []

            let kindDiagnostics =
                match obligation.Kind with
                | MissingArgument (callId, parameterId) ->
                    match Map.tryFind callId calls, Map.tryFind parameterId parameterOwners with
                    | Some call, Some ownerId ->
                        let alreadyBound =
                            call.Arguments
                            |> List.exists (fun argument ->
                                argument.ParameterId = parameterId)

                        if ownerId <> call.CalleeId then
                            [
                                diagnostic
                                    "invalid_obligation_parameter_ownership"
                                    $"Obligation '{StableId.value obligation.Id}' references a parameter not owned by the call callee."
                            ]
                        elif alreadyBound then
                            [
                                diagnostic
                                    "satisfied_missing_argument_obligation"
                                    $"Obligation '{StableId.value obligation.Id}' references an argument that is already bound."
                            ]
                        else
                            []
                    | Some call, None ->
                        ignore call

                        [
                            diagnostic
                                "unknown_obligation_parameter"
                                $"Obligation '{StableId.value obligation.Id}' references unknown parameter '{StableId.value parameterId}'."
                        ]
                    | None, Some ownerId ->
                        ignore ownerId

                        [
                            diagnostic
                                "unknown_obligation_call"
                                $"Obligation '{StableId.value obligation.Id}' references unknown call '{StableId.value callId}'."
                        ]
                    | None, None ->
                        [
                            diagnostic
                                "unknown_obligation_call"
                                $"Obligation '{StableId.value obligation.Id}' references unknown call '{StableId.value callId}'."
                            diagnostic
                                "unknown_obligation_parameter"
                                $"Obligation '{StableId.value obligation.Id}' references unknown parameter '{StableId.value parameterId}'."
                        ]

            statusDiagnostics @ kindDiagnostics)

    let validateSnapshot (snapshot: Snapshot) =
        let schemaDiagnostics =
            if snapshot.SchemaVersion = 1 then
                []
            else
                [
                    diagnostic
                        "unsupported_schema_version"
                        $"Schema version {snapshot.SchemaVersion} is not supported."
                ]

        schemaDiagnostics
        @ validateTopLevelIds snapshot
        @ validateParameterIds snapshot
        @ validateParameterTypes snapshot
        @ validateCalls snapshot
        @ validateObligations snapshot
