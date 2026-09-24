namespace Nyrwvy.Core

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

type ProductFieldConstructionPlan =
    { ConstructNodeId: GraphNodeId
      ValueNodeId: GraphNodeId }

type AddProductFieldRequest =
    { TargetTypeId: TypeId
      NewFieldId: FieldId
      NewFieldDisplayName: string
      NewFieldType: TypeRef
      ConstructionPlans: ProductFieldConstructionPlan list }

type ResolveMissingArgumentRequest =
    { TargetObligationId: ObligationId
      ResolutionValueNodeId: GraphNodeId }

type FunctionRequiredEffectRequest =
    { TargetFunctionId: FunctionId
      EffectId: EffectId }

type SemanticOperation =
    | RenameDisplayName of RenameTarget * string
    | AddFunctionParameter of AddFunctionParameterRequest
    | AddProductField of AddProductFieldRequest
    | RemoveProductField of FieldId
    | DeleteFunction of FunctionId
    | ResolveMissingArgument of ResolveMissingArgumentRequest
    | AddFunctionRequiredEffect of FunctionRequiredEffectRequest
    | RemoveFunctionRequiredEffect of FunctionRequiredEffectRequest

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

type ProductFieldRemoved =
    { OwnerTypeId: TypeId
      RemovedFieldId: FieldId
      DisplayName: string
      AffectedConstructNodeIds: GraphNodeId list }

type FunctionRemoved =
    { FunctionId: FunctionId
      DisplayName: string
      RemovedNodeIds: GraphNodeId list }

type ProductFieldAdded =
    { OwnerTypeId: TypeId
      AddedFieldId: FieldId
      AddedDisplayName: string
      AddedType: TypeRef }

type ProductFieldBound =
    { OwnerFunctionId: FunctionId
      ConstructNodeId: GraphNodeId
      TargetFieldId: FieldId
      ValueNodeId: GraphNodeId }

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

[<RequireQualifiedAccess>]
type FunctionRequiredEffectChange =
    | Added of ownerFunctionId: FunctionId * effectId: EffectId
    | Removed of ownerFunctionId: FunctionId * effectId: EffectId

type SemanticDiff =
    { Renames: SymbolRename list
      ParametersAdded: ParameterAdded list
      ProductFieldsAdded: ProductFieldAdded list
      ProductFieldsBound: ProductFieldBound list
      ProductFieldsRemoved: ProductFieldRemoved list
      FunctionsRemoved: FunctionRemoved list
      ArgumentsAttached: ArgumentAttached list
      ObligationsCreated: MissingArgumentCreated list
      ObligationsResolved: MissingArgumentResolved list
      FunctionRequiredEffectsChanged: FunctionRequiredEffectChange list }

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
      ProductFieldsAdded: ProductFieldAdded list
      ProductFieldsBound: ProductFieldBound list
      ProductFieldsRemoved: ProductFieldRemoved list
      FunctionsRemoved: FunctionRemoved list
      ArgumentsAttached: ArgumentAttached list
      ObligationsCreated: MissingArgumentCreated list
      ObligationsResolved: MissingArgumentResolved list
      FunctionRequiredEffectsChanged: FunctionRequiredEffectChange list }

[<RequireQualifiedAccess>]
module Transaction =
    let private reject code message =
        Rejected [ { Code = code; Message = message } ]

    let private emptyChanges: ChangeAccumulator =
        { ParametersAdded = []
          ProductFieldsAdded = []
          ProductFieldsBound = []
          ProductFieldsRemoved = []
          FunctionsRemoved = []
          ArgumentsAttached = []
          ObligationsCreated = []
          ObligationsResolved = []
          FunctionRequiredEffectsChanged = [] }

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
                | SemanticNodeKind.StringEqual _
                | SemanticNodeKind.StringConcat _
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
                | SemanticNodeKind.ListFold _
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
                | SemanticNodeKind.Bind _
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

    let private addProductField
        (request: AddProductFieldRequest)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Types |> List.tryFind (fun item -> item.Id = request.TargetTypeId) with
        | None -> Error("TargetNotFound", "The target product type was not found.")
        | Some { Kind = SemanticTypeKind.Union _ } ->
            Error("ProductFieldTargetNotProduct", "The target type must be a Product.")
        | Some ({ Kind = SemanticTypeKind.Product fields } as target) ->
            let existingFieldIds =
                snapshot.Types
                |> List.collect (fun item ->
                    match item.Kind with
                    | SemanticTypeKind.Product fields -> fields
                    | SemanticTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields))
                |> List.map (fun field -> field.Id)
                |> Set.ofList
            let affected =
                snapshot.Functions
                |> List.collect (fun owner ->
                    ValueGraph.collectNodes owner.Body
                    |> List.choose (fun node ->
                        match node.Kind with
                        | SemanticNodeKind.ProductConstruct(typeId, _) when typeId = target.Id -> Some(owner, node.Id)
                        | _ -> None))
            let affectedIds = affected |> List.map snd |> Set.ofList
            let plannedIds = request.ConstructionPlans |> List.map (fun plan -> plan.ConstructNodeId)
            let plansById =
                request.ConstructionPlans
                |> List.map (fun plan -> plan.ConstructNodeId, plan.ValueNodeId)
                |> Map.ofList
            if String.IsNullOrWhiteSpace request.NewFieldDisplayName then
                Error("InvalidDisplayName", "The new field display name cannot be blank.")
            elif fields |> List.exists (fun field -> field.DisplayName = request.NewFieldDisplayName) then
                Error("DuplicateFieldDisplayName", "The product already has a field with this display name.")
            elif Set.contains request.NewFieldId existingFieldIds then
                Error("DuplicateFieldId", "The new field ID already exists.")
            elif plannedIds.Length <> affectedIds.Count || Set.ofList plannedIds <> affectedIds then
                Error("ProductFieldPlanMismatch", "Every affected ProductConstruct requires exactly one field-value plan.")
            else
                let invalidValue =
                    affected
                    |> List.tryFind (fun (owner, constructNodeId) ->
                        let candidate = Map.find constructNodeId plansById
                        SemanticFacts.createNodeTypeResolver snapshot owner candidate <> Some request.NewFieldType)
                match invalidValue with
                | Some _ ->
                    Error("ProductFieldValueTypeMismatch", "The planned field value must exist in its owning function and have the new field type.")
                | None ->
                    let newField: SemanticField =
                        { Id = request.NewFieldId
                          DisplayName = request.NewFieldDisplayName
                          Type = request.NewFieldType }
                    let updatedTypes =
                        snapshot.Types
                        |> List.map (fun item ->
                            if item.Id = target.Id then
                                { item with Kind = SemanticTypeKind.Product(fields @ [ newField ]) }
                            else item)
                    let updatedFunctions =
                        snapshot.Functions
                        |> List.map (fun owner ->
                            let mapNode (node: SemanticNode) =
                                match node.Kind, Map.tryFind node.Id plansById with
                                | SemanticNodeKind.ProductConstruct(typeId, bindings), Some valueNodeId ->
                                    { node with
                                        Kind = SemanticNodeKind.ProductConstruct(typeId, bindings @
                                            [ { TargetFieldId = newField.Id; ValueNodeId = valueNodeId } ]) }
                                | _ -> node
                            { owner with Body = ValueGraph.mapNodes mapNode owner.Body })
                    let bound =
                        affected
                        |> List.map (fun (owner, constructNodeId) ->
                            { OwnerFunctionId = owner.Id
                              ConstructNodeId = constructNodeId
                              TargetFieldId = newField.Id
                              ValueNodeId = Map.find constructNodeId plansById })
                    let added: ProductFieldAdded =
                        { OwnerTypeId = target.Id
                          AddedFieldId = newField.Id
                          AddedDisplayName = newField.DisplayName
                          AddedType = newField.Type }
                    Ok(
                        { snapshot with Types = updatedTypes; Functions = updatedFunctions },
                        { changes with
                            ProductFieldsAdded = added :: changes.ProductFieldsAdded
                            ProductFieldsBound = bound @ changes.ProductFieldsBound }
                    )

    let private removeProductField
        (fieldId: FieldId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        let target =
            snapshot.Types
            |> List.tryPick (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product fields ->
                    fields |> List.tryFind (fun field -> field.Id = fieldId) |> Option.map (fun field -> item, field)
                | SemanticTypeKind.Union _ -> None)
        match target with
        | None -> Error("TargetNotFound", "The Product field to remove was not found.")
        | Some (owner, field) ->
            let nodes = snapshot.Functions |> List.collect (fun fn ->
                ValueGraph.collectNodes fn.Body |> List.map (fun node -> fn, node))
            let hasAccess = nodes |> List.exists (fun (_, node) ->
                match node.Kind with
                | SemanticNodeKind.FieldAccess(_, selectedId) -> selectedId = fieldId
                | _ -> false)
            let affected = nodes |> List.choose (fun (fn, node) ->
                match node.Kind with
                | SemanticNodeKind.ProductConstruct(typeId, bindings)
                    when typeId = owner.Id && bindings |> List.exists (fun binding -> binding.TargetFieldId = fieldId) ->
                    Some(fn, node.Id)
                | _ -> None)
            if changes.ProductFieldsAdded |> List.exists (fun item -> item.AddedFieldId = fieldId) then
                Error("ProductFieldCreatedInTransaction", "Remove a newly added field in a separate reviewed change.")
            elif hasAccess then
                Error("ProductFieldStillReferenced", "Repair all field accesses before removing this field.")
            elif affected |> List.exists (fun (fn, _) -> not (List.isEmpty fn.RequiredEffects)) then
                Error("ProductFieldMayDiscardEffects", "An affected function declares effects; preserve evaluation explicitly before removing its field binding.")
            else
                let updatedTypes = snapshot.Types |> List.map (fun item ->
                    if item.Id <> owner.Id then item else
                        match item.Kind with
                        | SemanticTypeKind.Product fields ->
                            { item with Kind = SemanticTypeKind.Product(fields |> List.filter (fun entry -> entry.Id <> fieldId)) }
                        | SemanticTypeKind.Union _ -> item)
                let updatedFunctions = snapshot.Functions |> List.map (fun fn ->
                    let mapNode (node: SemanticNode) =
                        match node.Kind with
                        | SemanticNodeKind.ProductConstruct(typeId, bindings) when typeId = owner.Id ->
                            let kept = bindings |> List.filter (fun binding -> binding.TargetFieldId <> fieldId)
                            { node with Kind = SemanticNodeKind.ProductConstruct(typeId, kept) }
                        | _ -> node
                    { fn with Body = ValueGraph.mapNodes mapNode fn.Body })
                let removed: ProductFieldRemoved =
                    { OwnerTypeId = owner.Id
                      RemovedFieldId = field.Id
                      DisplayName = field.DisplayName
                      AffectedConstructNodeIds = affected |> List.map snd |> List.sortBy StableId.value }
                Ok({ snapshot with Types = updatedTypes; Functions = updatedFunctions },
                    { changes with ProductFieldsRemoved = removed :: changes.ProductFieldsRemoved })

    let private deleteFunction
        (functionId: FunctionId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Functions |> List.tryFind (fun fn -> fn.Id = functionId) with
        | None -> Error("TargetNotFound", "The function to delete was not found.")
        | Some target ->
            let callers = SemanticQuery.getCallers snapshot functionId
            let bodyNodeIds =
                ValueGraph.collectNodes target.Body |> List.map (fun node -> node.Id) |> Set.ofList
            match callers with
            | Error _ -> Error("FunctionCallSiteInspectionFailed", "The function's callers could not be inspected.")
            | Ok (_ :: _) ->
                Error("FunctionStillReferenced", "Repair all direct calls and List callbacks before deleting this function.")
            | Ok [] when snapshot.Obligations |> List.exists (fun item -> Set.contains item.CallNodeId bodyNodeIds) ->
                Error("FunctionHasObligations", "Resolve or remove obligations attached to this function before deletion.")
            | Ok [] ->
                let removed: FunctionRemoved =
                    { FunctionId = target.Id
                      DisplayName = target.DisplayName
                      RemovedNodeIds = bodyNodeIds |> Set.toList |> List.sortBy StableId.value }
                Ok(
                    { snapshot with Functions = snapshot.Functions |> List.filter (fun fn -> fn.Id <> functionId) },
                    { changes with FunctionsRemoved = removed :: changes.FunctionsRemoved }
                )

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
                        | SemanticNodeKind.StringEqual _
                        | SemanticNodeKind.StringConcat _
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
                        | SemanticNodeKind.ListFold _
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
                        | SemanticNodeKind.Bind _
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

    let private changeFunctionRequiredEffect
        add
        (request: FunctionRequiredEffectRequest)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Functions |> List.tryFind (fun item -> item.Id = request.TargetFunctionId) with
        | None -> Error("TargetNotFound", "The target function was not found.")
        | Some targetFunction ->
            if snapshot.Effects |> List.exists (fun effect -> effect.Id = request.EffectId) |> not then
                Error("EffectNotFound", $"Effect '{StableId.value request.EffectId}' was not found.")
            elif add && List.contains request.EffectId targetFunction.RequiredEffects then
                Error("FunctionRequiredEffectAlreadyPresent", "The function already declares the required effect.")
            elif not add && not (List.contains request.EffectId targetFunction.RequiredEffects) then
                Error("FunctionRequiredEffectNotPresent", "The function does not declare the required effect.")
            else
                let requiredEffects =
                    if add then
                        targetFunction.RequiredEffects @ [ request.EffectId ]
                    else
                        targetFunction.RequiredEffects |> List.filter ((<>) request.EffectId)

                let functions =
                    snapshot.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.Id = request.TargetFunctionId then
                            { functionEntity with RequiredEffects = requiredEffects }
                        else
                            functionEntity)

                let change =
                    if add then
                        FunctionRequiredEffectChange.Added(request.TargetFunctionId, request.EffectId)
                    else
                        FunctionRequiredEffectChange.Removed(request.TargetFunctionId, request.EffectId)

                Ok(
                    { snapshot with Functions = functions },
                    { changes with
                        FunctionRequiredEffectsChanged = change :: changes.FunctionRequiredEffectsChanged }
                )

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
        | AddProductField request -> addProductField request snapshot changes
        | RemoveProductField fieldId -> removeProductField fieldId snapshot changes
        | DeleteFunction functionId -> deleteFunction functionId snapshot changes
        | ResolveMissingArgument request -> resolveMissingArgument transactionId request snapshot changes
        | AddFunctionRequiredEffect request -> changeFunctionRequiredEffect true request snapshot changes
        | RemoveFunctionRequiredEffect request -> changeFunctionRequiredEffect false request snapshot changes

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
            ProductFieldsAdded =
                changes.ProductFieldsAdded
                |> List.sortBy (fun item -> StableId.value item.OwnerTypeId, StableId.value item.AddedFieldId)
            ProductFieldsBound =
                changes.ProductFieldsBound
                |> List.sortBy (fun item -> StableId.value item.OwnerFunctionId, StableId.value item.ConstructNodeId)
            ProductFieldsRemoved =
                changes.ProductFieldsRemoved |> List.sortBy (fun item -> StableId.value item.OwnerTypeId, StableId.value item.RemovedFieldId)
            FunctionsRemoved =
                changes.FunctionsRemoved |> List.sortBy (fun item -> StableId.value item.FunctionId)
            ArgumentsAttached =
                changes.ArgumentsAttached
                |> List.sortBy (fun item -> StableId.value item.CallNodeId, StableId.value item.TargetParameterId)
            ObligationsCreated =
                changes.ObligationsCreated |> List.sortBy (fun item -> StableId.value item.ObligationId)
            ObligationsResolved =
                changes.ObligationsResolved |> List.sortBy (fun item -> StableId.value item.ObligationId)
            FunctionRequiredEffectsChanged =
                changes.FunctionRequiredEffectsChanged
                |> List.sortBy (function
                    | FunctionRequiredEffectChange.Added(functionId, effectId) ->
                        StableId.value functionId, StableId.value effectId, 0
                    | FunctionRequiredEffectChange.Removed(functionId, effectId) ->
                        StableId.value functionId, StableId.value effectId, 1) }

    let private netRequiredEffectChanges
        (baseSnapshot: Snapshot)
        (proposedSnapshot: Snapshot)
        (changes: ChangeAccumulator)
        =
        let requirements (snapshot: Snapshot) =
            snapshot.Functions
            |> List.collect (fun item -> item.RequiredEffects |> List.map (fun effectId -> item.Id, effectId))
            |> Set.ofList

        let before = requirements baseSnapshot
        let after = requirements proposedSnapshot
        changes.FunctionRequiredEffectsChanged
        |> List.distinct
        |> List.filter (function
            | FunctionRequiredEffectChange.Added(functionId, effectId) ->
                not (Set.contains (functionId, effectId) before) && Set.contains (functionId, effectId) after
            | FunctionRequiredEffectChange.Removed(functionId, effectId) ->
                Set.contains (functionId, effectId) before && not (Set.contains (functionId, effectId) after))

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
                          ProductFieldsAdded = changes.ProductFieldsAdded
                          ProductFieldsBound =
                              changes.ProductFieldsBound
                              |> List.filter (fun item ->
                                  proposed.Functions |> List.exists (fun fn -> fn.Id = item.OwnerFunctionId))
                          ProductFieldsRemoved = changes.ProductFieldsRemoved
                          FunctionsRemoved = changes.FunctionsRemoved
                          ArgumentsAttached = changes.ArgumentsAttached
                          ObligationsCreated = changes.ObligationsCreated
                          ObligationsResolved = changes.ObligationsResolved
                          FunctionRequiredEffectsChanged = netRequiredEffectChanges baseSnapshot proposed changes }

                    let hasChanges =
                        not (List.isEmpty diff.Renames)
                        || not (List.isEmpty diff.ParametersAdded)
                        || not (List.isEmpty diff.ProductFieldsAdded)
                        || not (List.isEmpty diff.ProductFieldsBound)
                        || not (List.isEmpty diff.ProductFieldsRemoved)
                        || not (List.isEmpty diff.FunctionsRemoved)
                        || not (List.isEmpty diff.ArgumentsAttached)
                        || not (List.isEmpty diff.ObligationsCreated)
                        || not (List.isEmpty diff.ObligationsResolved)
                        || not (List.isEmpty diff.FunctionRequiredEffectsChanged)

                    if not hasChanges then
                        reject "NoChange" "The transaction has no net semantic change."
                    else
                        Proposed
                            { TransactionId = request.Id
                              BaseSnapshotId = request.BaseSnapshotId
                              ProposedSnapshot = proposed
                              Diff = diff }