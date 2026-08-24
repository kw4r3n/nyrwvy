namespace Nayamnyam.Core

open System

[<Struct>]
type ValidationIssue =
    { Code: string
      Message: string }

[<RequireQualifiedAccess>]
module SnapshotValidator =
    let private nodeDependencies (node: SemanticNode) =
        match node.Kind with
        | SemanticNodeKind.ParameterReference _ -> []
        | SemanticNodeKind.DirectCall(_, arguments) ->
            arguments |> List.map (fun argument -> argument.ValueNodeId)

    let private hasDependencyCycle (nodesById: Map<GraphNodeId, SemanticNode>) =
        let rec visit path nodeId =
            if Set.contains nodeId path then
                true
            else
                match Map.tryFind nodeId nodesById with
                | None -> false
                | Some node ->
                    let nextPath = Set.add nodeId path
                    nodeDependencies node |> List.exists (visit nextPath)

        nodesById |> Map.exists (fun nodeId _ -> visit Set.empty nodeId)

    let validate (snapshot: Snapshot): ValidationIssue list =
        let issues = ResizeArray<ValidationIssue>()
        let add code message = issues.Add({ Code = code; Message = message })

        if snapshot.ParentId = Some snapshot.Id then
            add "SnapshotParentSelfReference" "A snapshot cannot name itself as its parent."

        snapshot.Types
        |> List.countBy (fun item -> item.Id)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.iter (fun (id, _) ->
            add "DuplicateTypeId" $"Type ID '{StableId.value id}' occurs more than once.")

        snapshot.Functions
        |> List.countBy (fun item -> item.Id)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.iter (fun (id, _) ->
            add "DuplicateFunctionId" $"Function ID '{StableId.value id}' occurs more than once.")

        let parameterEntries =
            snapshot.Functions
            |> List.collect (fun owner -> owner.Parameters |> List.map (fun parameter -> owner.Id, parameter))

        parameterEntries
        |> List.countBy (fun (_, parameter) -> parameter.Id)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.iter (fun (id, _) ->
            add "DuplicateParameterId" $"Parameter ID '{StableId.value id}' occurs more than once.")

        let nodeEntries =
            snapshot.Functions
            |> List.collect (fun owner -> owner.Nodes |> List.map (fun node -> owner.Id, node))

        nodeEntries
        |> List.countBy (fun (_, node) -> node.Id)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.iter (fun (id, _) ->
            add "DuplicateGraphNodeId" $"Graph node ID '{StableId.value id}' occurs more than once.")

        snapshot.Obligations
        |> List.countBy (fun obligation -> obligation.Id)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.iter (fun (id, _) ->
            add "DuplicateObligationId" $"Obligation ID '{StableId.value id}' occurs more than once.")

        snapshot.Obligations
        |> List.filter (fun obligation -> obligation.Status = ObligationStatus.Open)
        |> List.countBy (fun obligation -> obligation.CallNodeId, obligation.ParameterId)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.iter (fun ((callNodeId, parameterId), _) ->
            add
                "DuplicateOpenMissingArgumentObligation"
                $"Call '{StableId.value callNodeId}' has more than one open obligation for parameter '{StableId.value parameterId}'.")

        snapshot.Types
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item ->
            add "BlankTypeDisplayName" $"Type '{StableId.value item.Id}' has a blank display name.")

        snapshot.Functions
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item ->
            add "BlankFunctionDisplayName" $"Function '{StableId.value item.Id}' has a blank display name.")

        parameterEntries
        |> List.map snd
        |> List.filter (fun parameter -> String.IsNullOrWhiteSpace parameter.DisplayName)
        |> List.iter (fun parameter ->
            add "BlankParameterDisplayName" $"Parameter '{StableId.value parameter.Id}' has a blank display name.")

        let typeIds = snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList
        let functionsById = snapshot.Functions |> List.map (fun item -> item.Id, item) |> Map.ofList
        let nodeOwners = nodeEntries |> List.map (fun (ownerId, node) -> node.Id, (ownerId, node)) |> Map.ofList

        let openObligationPairs =
            snapshot.Obligations
            |> List.choose (fun obligation ->
                match obligation.Status with
                | ObligationStatus.Open -> Some(obligation.CallNodeId, obligation.ParameterId)
                | ObligationStatus.Resolved _ -> None)
            |> Set.ofList

        let tryNodeType (owner: SemanticFunction) nodeId =
            match owner.Nodes |> List.tryFind (fun node -> node.Id = nodeId) with
            | None -> None
            | Some node ->
                match node.Kind with
                | SemanticNodeKind.ParameterReference parameterId ->
                    owner.Parameters
                    |> List.tryFind (fun parameter -> parameter.Id = parameterId)
                    |> Option.map (fun parameter -> parameter.TypeId)
                | SemanticNodeKind.DirectCall(targetFunctionId, _) ->
                    Map.tryFind targetFunctionId functionsById
                    |> Option.map (fun target -> target.ResultTypeId)

        for functionEntity in snapshot.Functions do
            if not (Set.contains functionEntity.ResultTypeId typeIds) then
                add
                    "UnknownFunctionResultType"
                    $"Function '{StableId.value functionEntity.Id}' references unknown result type '{StableId.value functionEntity.ResultTypeId}'."

            for parameter in functionEntity.Parameters do
                if not (Set.contains parameter.TypeId typeIds) then
                    add
                        "UnknownParameterType"
                        $"Parameter '{StableId.value parameter.Id}' references unknown type '{StableId.value parameter.TypeId}'."

            let ownNodes = functionEntity.Nodes |> List.map (fun node -> node.Id, node) |> Map.ofList

            match Map.tryFind functionEntity.ResultNodeId ownNodes with
            | None ->
                add
                    "UnknownFunctionResultNode"
                    $"Function '{StableId.value functionEntity.Id}' result node '{StableId.value functionEntity.ResultNodeId}' is not owned by the function."
            | Some _ ->
                match tryNodeType functionEntity functionEntity.ResultNodeId with
                | Some resultTypeId when resultTypeId <> functionEntity.ResultTypeId ->
                    add
                        "FunctionResultTypeMismatch"
                        $"Function '{StableId.value functionEntity.Id}' result node type does not match its declared result type."
                | _ -> ()

            if hasDependencyCycle ownNodes then
                add
                    "GraphNodeDependencyCycle"
                    $"Function '{StableId.value functionEntity.Id}' contains a cycle between value-producing graph nodes."

            for node in functionEntity.Nodes do
                match node.Kind with
                | SemanticNodeKind.ParameterReference parameterId ->
                    if not (functionEntity.Parameters |> List.exists (fun parameter -> parameter.Id = parameterId)) then
                        add
                            "UnknownParameterReference"
                            $"Graph node '{StableId.value node.Id}' references parameter '{StableId.value parameterId}' outside its owning function."
                | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
                    match Map.tryFind targetFunctionId functionsById with
                    | None ->
                        add
                            "UnknownCallTarget"
                            $"Call node '{StableId.value node.Id}' references unknown function '{StableId.value targetFunctionId}'."
                    | Some targetFunction ->
                        arguments
                        |> List.countBy (fun argument -> argument.TargetParameterId)
                        |> List.filter (fun (_, count) -> count > 1)
                        |> List.iter (fun (parameterId, _) ->
                            add
                                "DuplicateArgumentTarget"
                                $"Call node '{StableId.value node.Id}' binds parameter '{StableId.value parameterId}' more than once.")

                        for argument in arguments do
                            let targetParameter =
                                targetFunction.Parameters
                                |> List.tryFind (fun parameter -> parameter.Id = argument.TargetParameterId)

                            if Option.isNone targetParameter then
                                add
                                    "UnknownArgumentTargetParameter"
                                    $"Call node '{StableId.value node.Id}' targets parameter '{StableId.value argument.TargetParameterId}' not owned by its callee."

                            if not (Map.containsKey argument.ValueNodeId ownNodes) then
                                add
                                    "UnknownArgumentValueNode"
                                    $"Call node '{StableId.value node.Id}' uses value node '{StableId.value argument.ValueNodeId}' outside its owning function."

                            match targetParameter, tryNodeType functionEntity argument.ValueNodeId with
                            | Some parameter, Some valueTypeId when parameter.TypeId <> valueTypeId ->
                                add
                                    "ArgumentTypeMismatch"
                                    $"Call node '{StableId.value node.Id}' binds a value with the wrong type to parameter '{StableId.value parameter.Id}'."
                            | _ -> ()

                        for parameter in targetFunction.Parameters do
                            let hasBinding =
                                arguments
                                |> List.exists (fun argument -> argument.TargetParameterId = parameter.Id)

                            let hasOpenObligation =
                                Set.contains (node.Id, parameter.Id) openObligationPairs

                            if not hasBinding && not hasOpenObligation then
                                add
                                    "MissingArgument"
                                    $"Call node '{StableId.value node.Id}' has no binding or open obligation for parameter '{StableId.value parameter.Id}'."

        for obligation in snapshot.Obligations do
            match Map.tryFind obligation.CallNodeId nodeOwners with
            | None ->
                add
                    "InvalidObligationTarget"
                    $"Obligation '{StableId.value obligation.Id}' targets unknown graph node '{StableId.value obligation.CallNodeId}'."
            | Some(ownerFunctionId, callNode) ->
                match callNode.Kind with
                | SemanticNodeKind.ParameterReference _ ->
                    add
                        "InvalidObligationTarget"
                        $"Obligation '{StableId.value obligation.Id}' does not target a direct-call node."
                | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
                    let targetParameter =
                        Map.tryFind targetFunctionId functionsById
                        |> Option.bind (fun target ->
                            target.Parameters
                            |> List.tryFind (fun parameter -> parameter.Id = obligation.ParameterId))

                    if Option.isNone targetParameter then
                        add
                            "InvalidObligationParameter"
                            $"Obligation '{StableId.value obligation.Id}' targets a parameter not owned by the call's callee."

                    let matchingBindings =
                        arguments
                        |> List.filter (fun argument -> argument.TargetParameterId = obligation.ParameterId)

                    match obligation.Status with
                    | ObligationStatus.Open ->
                        if not (List.isEmpty matchingBindings) then
                            add
                                "SatisfiedOpenObligation"
                                $"Open obligation '{StableId.value obligation.Id}' already has a matching argument binding."
                    | ObligationStatus.Resolved resolution ->
                        let exactBinding =
                            matchingBindings
                            |> List.exists (fun argument -> argument.ValueNodeId = resolution.ValueNodeId)

                        if not exactBinding then
                            add
                                "InvalidObligationResolution"
                                $"Resolved obligation '{StableId.value obligation.Id}' does not match the call's argument binding."

                        match Map.tryFind resolution.ValueNodeId nodeOwners with
                        | Some(valueOwnerId, _) when valueOwnerId = ownerFunctionId -> ()
                        | _ ->
                            add
                                "InvalidObligationResolutionValue"
                                $"Resolved obligation '{StableId.value obligation.Id}' references a value node outside the call's owning function."

        issues
        |> Seq.sortBy (fun issue -> issue.Code, issue.Message)
        |> List.ofSeq
