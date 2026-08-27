namespace Nayamnyam.Core

open System

[<RequireQualifiedAccess>]
type RenameTarget =
    | TypeTarget of TypeId
    | FunctionTarget of FunctionId

type CallPlan =
    | BindExistingValue of CallNodeId: GraphNodeId * ValueNodeId: GraphNodeId
    | LeaveMissing of CallNodeId: GraphNodeId * ObligationId: ObligationId

type AddFunctionParameterRequest =
    { TargetFunctionId: FunctionId
      NewParameterId: ParameterId
      NewParameterDisplayName: string
      NewParameterType: TypeRef
      CallSitePlans: CallPlan list }

type ResolveMissingArgumentRequest =
    { TargetObligationId: ObligationId
      ResolutionValueNodeId: GraphNodeId }

type SemanticOperation =
    | RenameDisplayName of RenameTarget * string
    | AddFunctionParameter of AddFunctionParameterRequest
    | ResolveMissingArgument of ResolveMissingArgumentRequest

type TransactionRequest =
    { Id: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      Operations: SemanticOperation list }

type SymbolRename =
    | TypeRenamed of TypeId * oldDisplayName: string * newDisplayName: string
    | FunctionRenamed of FunctionId * oldDisplayName: string * newDisplayName: string

type ParameterAdded =
    { OwnerFunctionId: FunctionId
      AddedParameterId: ParameterId
      AddedDisplayName: string
      AddedType: TypeRef }

type ArgumentAttached =
    { CallNodeId: GraphNodeId
      TargetParameterId: ParameterId
      ValueNodeId: GraphNodeId }

type MissingArgumentCreated =
    { ObligationId: ObligationId
      CallNodeId: GraphNodeId
      ParameterId: ParameterId }

type MissingArgumentResolved =
    { ObligationId: ObligationId
      CallNodeId: GraphNodeId
      ParameterId: ParameterId
      ValueNodeId: GraphNodeId }

type SemanticDiff =
    { Renames: SymbolRename list
      ParametersAdded: ParameterAdded list
      ArgumentsAttached: ArgumentAttached list
      ObligationsCreated: MissingArgumentCreated list
      ObligationsResolved: MissingArgumentResolved list }

[<Struct>]
type TransactionDiagnostic =
    { Code: string
      Message: string }

type TransactionSimulation =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshot: Snapshot
      Diff: SemanticDiff }

type SimulationResult =
    | Rejected of TransactionDiagnostic list
    | Proposed of TransactionSimulation

type private ChangeAccumulator =
    { ParametersAdded: ParameterAdded list
      ArgumentsAttached: ArgumentAttached list
      ObligationsCreated: MissingArgumentCreated list
      ObligationsResolved: MissingArgumentResolved list }

[<RequireQualifiedAccess>]
module Transaction =
    let private reject code message =
        Rejected [ { Code = code; Message = message } ]

    let private emptyChanges: ChangeAccumulator =
        { ParametersAdded = []
          ArgumentsAttached = []
          ObligationsCreated = []
          ObligationsResolved = [] }

    let private renameType id newDisplayName (snapshot: Snapshot) =
        match snapshot.Types |> List.tryFind (fun item -> item.Id = id) with
        | None -> Error("TargetNotFound", $"Type '{StableId.value id}' was not found.")
        | Some _ ->
            let types =
                snapshot.Types
                |> List.map (fun item ->
                    if item.Id = id then { item with DisplayName = newDisplayName } else item)
            Ok { snapshot with Types = types }

    let private renameFunction id newDisplayName (snapshot: Snapshot) =
        match snapshot.Functions |> List.tryFind (fun item -> item.Id = id) with
        | None -> Error("TargetNotFound", $"Function '{StableId.value id}' was not found.")
        | Some _ ->
            let functions =
                snapshot.Functions
                |> List.map (fun item ->
                    if item.Id = id then { item with DisplayName = newDisplayName } else item)
            Ok { snapshot with Functions = functions }

    let private callPlanNodeId = function
        | BindExistingValue(callNodeId, _) -> callNodeId
        | LeaveMissing(callNodeId, _) -> callNodeId

    let private affectedCalls targetFunctionId (snapshot: Snapshot) =
        snapshot.Functions
        |> List.collect (fun owner ->
            ValueGraph.collectNodes owner.Body
            |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.DirectCall(functionId, _) when functionId = targetFunctionId ->
                    Some(owner.Id, node.Id)
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.Int64Literal _
                | SemanticNodeKind.Float64Literal _
                | SemanticNodeKind.StringIsEmpty _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64Equal _
                | SemanticNodeKind.Int64LessThan _
                | SemanticNodeKind.Int64LessThanOrEqual _
                | SemanticNodeKind.Int64GreaterThan _
                | SemanticNodeKind.Int64GreaterThanOrEqual _
                | SemanticNodeKind.Float64Add _
                | SemanticNodeKind.Float64Subtract _
                | SemanticNodeKind.Float64Multiply _
                | SemanticNodeKind.Float64Divide _
                | SemanticNodeKind.Float64Equal _
                | SemanticNodeKind.Float64LessThan _
                | SemanticNodeKind.Float64IsNaN _
                | SemanticNodeKind.Float64IsFinite _
                | SemanticNodeKind.Float64IsInfinite _
                | SemanticNodeKind.ListEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListMap _
                | SemanticNodeKind.ListFilter _
                | SemanticNodeKind.ListAny _
                | SemanticNodeKind.ListAll _
                | SemanticNodeKind.DirectCall _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.ResultErr _
                | SemanticNodeKind.Conditional _
                | SemanticNodeKind.MatchUnion _
                | SemanticNodeKind.MatchOption _
                | SemanticNodeKind.MatchResult _ -> None))

    let private addFunctionParameter
        (transactionId: TransactionId)
        (request: AddFunctionParameterRequest)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        if String.IsNullOrWhiteSpace request.NewParameterDisplayName then
            Error("InvalidDisplayName", "A parameter display name cannot be blank.")
        elif
            snapshot.Functions
            |> List.collect (fun functionEntity -> functionEntity.Parameters)
            |> List.exists (fun parameter -> parameter.Id = request.NewParameterId)
        then
            Error("DuplicateParameterId", "The new parameter ID already exists.")
        else
            match snapshot.Functions |> List.tryFind (fun item -> item.Id = request.TargetFunctionId) with
            | None -> Error("TargetNotFound", "The target function was not found.")
            | Some targetFunction ->
                let affected = affectedCalls request.TargetFunctionId snapshot
                let affectedIds = affected |> List.map snd |> Set.ofList
                let planIds = request.CallSitePlans |> List.map callPlanNodeId
                let planIdSet = Set.ofList planIds

                if planIds.Length <> planIdSet.Count || affectedIds <> planIdSet then
                    Error("CallPlanMismatch", "Every affected direct call requires exactly one call-site plan.")
                else
                    let newObligationIds =
                        request.CallSitePlans
                        |> List.choose (function
                            | LeaveMissing(_, obligationId) -> Some obligationId
                            | BindExistingValue _ -> None)

                    let existingObligationIds = snapshot.Obligations |> List.map (fun item -> item.Id) |> Set.ofList
                    let duplicateNewObligation = newObligationIds.Length <> (newObligationIds |> Set.ofList).Count
                    let collidesWithExisting = newObligationIds |> List.exists (fun id -> Set.contains id existingObligationIds)

                    if duplicateNewObligation || collidesWithExisting then
                        Error("DuplicateObligationId", "A call-site plan uses a duplicate obligation ID.")
                    else
                        let planByCallId =
                            request.CallSitePlans |> List.map (fun plan -> callPlanNodeId plan, plan) |> Map.ofList

                        let newParameter: SemanticParameter =
                            { Id = request.NewParameterId
                              DisplayName = request.NewParameterDisplayName
                              Type = request.NewParameterType }

                        let functionsWithParameter =
                            snapshot.Functions
                            |> List.map (fun functionEntity ->
                                if functionEntity.Id = targetFunction.Id then
                                    { functionEntity with Parameters = functionEntity.Parameters @ [ newParameter ] }
                                else
                                    functionEntity)

                        let mutable attached: ArgumentAttached list = []

                        let updateNode (node: SemanticNode) =
                            match Map.tryFind node.Id planByCallId with
                            | None -> node
                            | Some plan ->
                                match plan, node.Kind with
                                | BindExistingValue(_, valueNodeId), SemanticNodeKind.DirectCall(targetId, arguments) ->
                                    let binding: ArgumentBinding =
                                        { TargetParameterId = request.NewParameterId
                                          ValueNodeId = valueNodeId }
                                    let change: ArgumentAttached =
                                        { CallNodeId = node.Id
                                          TargetParameterId = request.NewParameterId
                                          ValueNodeId = valueNodeId }
                                    attached <- change :: attached
                                    { node with Kind = SemanticNodeKind.DirectCall(targetId, arguments @ [ binding ]) }
                                | LeaveMissing _, SemanticNodeKind.DirectCall _ -> node
                                | BindExistingValue _, _
                                | LeaveMissing _, _ -> node

                        let updatedFunctions =
                            functionsWithParameter
                            |> List.map (fun owner -> { owner with Body = ValueGraph.mapNodes updateNode owner.Body })

                        let createdObligations: MissingArgumentObligation list =
                            request.CallSitePlans
                            |> List.choose (function
                                | LeaveMissing(callNodeId, obligationId) ->
                                    Some
                                        { Id = obligationId
                                          OriginTransactionId = transactionId
                                          CallNodeId = callNodeId
                                          ParameterId = request.NewParameterId
                                          Status = ObligationStatus.Open }
                                | BindExistingValue _ -> None)

                        let createdDiff: MissingArgumentCreated list =
                            createdObligations
                            |> List.map (fun obligation ->
                                { ObligationId = obligation.Id
                                  CallNodeId = obligation.CallNodeId
                                  ParameterId = obligation.ParameterId })

                        let updatedSnapshot: Snapshot =
                            { snapshot with
                                Functions = updatedFunctions
                                Obligations = snapshot.Obligations @ createdObligations }

                        let parameterChange: ParameterAdded =
                            { OwnerFunctionId = request.TargetFunctionId
                              AddedParameterId = request.NewParameterId
                              AddedDisplayName = request.NewParameterDisplayName
                              AddedType = request.NewParameterType }

                        let updatedChanges: ChangeAccumulator =
                            { changes with
                                ParametersAdded = parameterChange :: changes.ParametersAdded
                                ArgumentsAttached = attached @ changes.ArgumentsAttached
                                ObligationsCreated = createdDiff @ changes.ObligationsCreated }

                        Ok(updatedSnapshot, updatedChanges)

    let private resolveMissingArgument
        (transactionId: TransactionId)
        (request: ResolveMissingArgumentRequest)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Obligations |> List.tryFind (fun item -> item.Id = request.TargetObligationId) with
        | None -> Error("ObligationNotOpen", "The missing-argument obligation does not exist.")
        | Some obligation ->
            match obligation.Status with
            | ObligationStatus.Resolved _ ->
                Error("ObligationNotOpen", "The missing-argument obligation is already resolved.")
            | ObligationStatus.Open ->
                let mutable attached = false

                let updateNode (node: SemanticNode) =
                    if node.Id <> obligation.CallNodeId then
                        node
                    else
                        match node.Kind with
                        | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
                            attached <- true
                            let binding: ArgumentBinding =
                                { TargetParameterId = obligation.ParameterId
                                  ValueNodeId = request.ResolutionValueNodeId }
                            { node with
                                Kind = SemanticNodeKind.DirectCall(targetFunctionId, arguments @ [ binding ]) }
                        | SemanticNodeKind.ParameterReference _
                        | SemanticNodeKind.PatternReference _
                        | SemanticNodeKind.StringLiteral _
                        | SemanticNodeKind.BoolLiteral _
                        | SemanticNodeKind.UnitLiteral
                        | SemanticNodeKind.Int64Literal _
                        | SemanticNodeKind.Float64Literal _
                        | SemanticNodeKind.StringIsEmpty _
                        | SemanticNodeKind.StringContains _
                        | SemanticNodeKind.Int64WrappingAdd _
                        | SemanticNodeKind.Int64WrappingSubtract _
                        | SemanticNodeKind.Int64WrappingMultiply _
                        | SemanticNodeKind.Int64Equal _
                        | SemanticNodeKind.Int64LessThan _
                        | SemanticNodeKind.Int64LessThanOrEqual _
                        | SemanticNodeKind.Int64GreaterThan _
                        | SemanticNodeKind.Int64GreaterThanOrEqual _
                        | SemanticNodeKind.Float64Add _
                        | SemanticNodeKind.Float64Subtract _
                        | SemanticNodeKind.Float64Multiply _
                        | SemanticNodeKind.Float64Divide _
                        | SemanticNodeKind.Float64Equal _
                        | SemanticNodeKind.Float64LessThan _
                        | SemanticNodeKind.Float64IsNaN _
                        | SemanticNodeKind.Float64IsFinite _
                        | SemanticNodeKind.Float64IsInfinite _
                        | SemanticNodeKind.ListEmpty _
                        | SemanticNodeKind.ListConstruct _
                        | SemanticNodeKind.ListLength _
                        | SemanticNodeKind.ListAppendItem _
                        | SemanticNodeKind.ListConcat _
                        | SemanticNodeKind.ListMap _
                        | SemanticNodeKind.ListFilter _
                        | SemanticNodeKind.ListAny _
                        | SemanticNodeKind.ListAll _
                        | SemanticNodeKind.CapabilityInvoke _
                        | SemanticNodeKind.ProductConstruct _
                        | SemanticNodeKind.FieldAccess _
                        | SemanticNodeKind.UnionConstruct _
                        | SemanticNodeKind.OptionNone _
                        | SemanticNodeKind.OptionSome _
                        | SemanticNodeKind.ResultOk _
                        | SemanticNodeKind.ResultErr _
                        | SemanticNodeKind.Conditional _
                        | SemanticNodeKind.MatchUnion _
                        | SemanticNodeKind.MatchOption _
                        | SemanticNodeKind.MatchResult _ -> node

                let updatedFunctions =
                    snapshot.Functions
                    |> List.map (fun owner -> { owner with Body = ValueGraph.mapNodes updateNode owner.Body })

                if not attached then
                    Error("ObligationTargetMismatch", "The obligated direct-call node no longer exists.")
                else
                    let updatedObligations: MissingArgumentObligation list =
                        snapshot.Obligations
                        |> List.map (fun item ->
                            if item.Id = obligation.Id then
                                { item with
                                    Status =
                                        ObligationStatus.Resolved
                                            { TransactionId = transactionId
                                              ValueNodeId = request.ResolutionValueNodeId } }
                            else
                                item)

                    let updatedSnapshot: Snapshot =
                        { snapshot with
                            Functions = updatedFunctions
                            Obligations = updatedObligations }

                    let attachedDiff: ArgumentAttached =
                        { CallNodeId = obligation.CallNodeId
                          TargetParameterId = obligation.ParameterId
                          ValueNodeId = request.ResolutionValueNodeId }

                    let resolvedDiff: MissingArgumentResolved =
                        { ObligationId = obligation.Id
                          CallNodeId = obligation.CallNodeId
                          ParameterId = obligation.ParameterId
                          ValueNodeId = request.ResolutionValueNodeId }

                    let updatedChanges: ChangeAccumulator =
                        { changes with
                            ArgumentsAttached = attachedDiff :: changes.ArgumentsAttached
                            ObligationsResolved = resolvedDiff :: changes.ObligationsResolved }

                    Ok(updatedSnapshot, updatedChanges)

    let private applyOperation
        (transactionId: TransactionId)
        ((snapshot, changes): Snapshot * ChangeAccumulator)
        (operation: SemanticOperation)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match operation with
        | RenameDisplayName(_, newDisplayName) when String.IsNullOrWhiteSpace newDisplayName ->
            Error("InvalidDisplayName", "A display name cannot be blank.")
        | RenameDisplayName(RenameTarget.TypeTarget id, newDisplayName) ->
            renameType id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.FunctionTarget id, newDisplayName) ->
            renameFunction id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | AddFunctionParameter request -> addFunctionParameter transactionId request snapshot changes
        | ResolveMissingArgument request -> resolveMissingArgument transactionId request snapshot changes

    let private netRenames (baseSnapshot: Snapshot) (proposedSnapshot: Snapshot) =
        let typeNames = baseSnapshot.Types |> List.map (fun item -> item.Id, item.DisplayName) |> Map.ofList
        let functionNames = baseSnapshot.Functions |> List.map (fun item -> item.Id, item.DisplayName) |> Map.ofList

        let typeRenames =
            proposedSnapshot.Types
            |> List.choose (fun item ->
                match Map.tryFind item.Id typeNames with
                | Some oldName when oldName <> item.DisplayName -> Some(TypeRenamed(item.Id, oldName, item.DisplayName))
                | Some _
                | None -> None)

        let functionRenames =
            proposedSnapshot.Functions
            |> List.choose (fun item ->
                match Map.tryFind item.Id functionNames with
                | Some oldName when oldName <> item.DisplayName -> Some(FunctionRenamed(item.Id, oldName, item.DisplayName))
                | Some _
                | None -> None)

        typeRenames @ functionRenames
        |> List.sortBy (function
            | TypeRenamed(id, _, _) -> "type:" + StableId.value id
            | FunctionRenamed(id, _, _) -> "function:" + StableId.value id)

    let private sortChanges (changes: ChangeAccumulator): ChangeAccumulator =
        { changes with
            ParametersAdded =
                changes.ParametersAdded
                |> List.sortBy (fun item -> StableId.value item.OwnerFunctionId, StableId.value item.AddedParameterId)
            ArgumentsAttached =
                changes.ArgumentsAttached
                |> List.sortBy (fun item -> StableId.value item.CallNodeId, StableId.value item.TargetParameterId)
            ObligationsCreated =
                changes.ObligationsCreated |> List.sortBy (fun item -> StableId.value item.ObligationId)
            ObligationsResolved =
                changes.ObligationsResolved |> List.sortBy (fun item -> StableId.value item.ObligationId) }

    let simulate (baseSnapshot: Snapshot) (request: TransactionRequest): SimulationResult =
        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> reject "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] when request.BaseSnapshotId <> baseSnapshot.Id ->
            reject "StaleBaseSnapshot" "The transaction base snapshot does not match the supplied snapshot."
        | [] when request.ProposedSnapshotId = baseSnapshot.Id ->
            reject "InvalidProposedSnapshotId" "The proposed snapshot ID must differ from the base snapshot ID."
        | [] when List.isEmpty request.Operations ->
            reject "NoOperations" "A transaction must contain at least one semantic operation."
        | [] ->
            let applied =
                request.Operations
                |> List.fold
                    (fun state operation ->
                        match state with
                        | Error error -> Error error
                        | Ok current -> applyOperation request.Id current operation)
                    (Ok(baseSnapshot, emptyChanges))

            match applied with
            | Error(code, message) -> reject code message
            | Ok(changed, accumulatedChanges) ->
                let proposed: Snapshot =
                    { changed with
                        Id = request.ProposedSnapshotId
                        ParentId = Some baseSnapshot.Id }

                match SnapshotValidator.validate proposed with
                | issue :: _ -> reject "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [] ->
                    let changes = sortChanges accumulatedChanges
                    let renames = netRenames baseSnapshot proposed
                    let diff: SemanticDiff =
                        { Renames = renames
                          ParametersAdded = changes.ParametersAdded
                          ArgumentsAttached = changes.ArgumentsAttached
                          ObligationsCreated = changes.ObligationsCreated
                          ObligationsResolved = changes.ObligationsResolved }

                    let hasChanges =
                        not (List.isEmpty diff.Renames)
                        || not (List.isEmpty diff.ParametersAdded)
                        || not (List.isEmpty diff.ArgumentsAttached)
                        || not (List.isEmpty diff.ObligationsCreated)
                        || not (List.isEmpty diff.ObligationsResolved)

                    if not hasChanges then
                        reject "NoChange" "The transaction has no net semantic change."
                    else
                        Proposed
                            { TransactionId = request.Id
                              BaseSnapshotId = request.BaseSnapshotId
                              ProposedSnapshot = proposed
                              Diff = diff }
