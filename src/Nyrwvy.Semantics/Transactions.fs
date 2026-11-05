namespace Nyrwvy.Core

open System

[<RequireQualifiedAccess>]
type RenameTarget =
    | TypeTarget of TypeId
    | FunctionTarget of FunctionId
    /// A Product field or a Union variant field.
    | FieldTarget of FieldId
    | VariantTarget of VariantId
    | ParameterTarget of ParameterId
    | EffectTarget of EffectId
    | CapabilityTarget of CapabilityId
    | CapabilityOperationTarget of CapabilityOperationId
    | CapabilityParameterTarget of CapabilityParameterId

type CallPlan =
    | BindExistingValue of CallNodeId: GraphNodeId * ValueNodeId: GraphNodeId
    | LeaveMissing of CallNodeId: GraphNodeId * ObligationId: ObligationId

type AddFunctionParameterRequest =
    { TargetFunctionId: FunctionId
      NewParameterId: ParameterId
      NewParameterDisplayName: string
      NewParameterType: TypeRef
      CallSitePlans: CallPlan list }

/// `AddedNodes` are materialized value nodes (system-owned IDs) inserted just before the construct
/// in its own graph; empty when `ValueNodeId` names an existing node.
type ProductFieldConstructionPlan =
    { ConstructNodeId: GraphNodeId
      ValueNodeId: GraphNodeId
      AddedNodes: SemanticNode list }

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

/// The program implementation one `CapabilityConstruct` node gives a newly added operation.
type CapabilityImplementationPlan =
    { ConstructNodeId: GraphNodeId
      ImplementationFunctionId: FunctionId }

/// Adds one operation (with system-owned operation and parameter IDs) to an existing Capability.
/// Every `CapabilityConstruct` of that Capability must plan an implementation for it, so the
/// implementation set stays complete in one publication.
type AddCapabilityOperationRequest =
    { TargetCapabilityId: CapabilityId
      Operation: SemanticCapabilityOperation
      ImplementationPlans: CapabilityImplementationPlan list }

/// The arm one `MatchUnion` node gains for a newly added variant: pattern bindings for the new
/// variant's fields (in field order, at the match input's type arguments) and a body already
/// materialized in the arm's lexical scope with system-owned node and binding IDs.
type UnionVariantArmPlan =
    { MatchNodeId: GraphNodeId
      FieldBindings: PatternFieldBinding list
      Body: ValueGraph }

/// Adds one variant (with system-owned variant and field IDs) to an existing Union. Every
/// `MatchUnion` over that Union must plan an arm for it, so every match stays exhaustive in one
/// publication; constructing the new variant is ordinary later authoring.
type AddUnionVariantRequest =
    { TargetTypeId: TypeId
      Variant: SemanticVariant
      ArmPlans: UnionVariantArmPlan list }

/// A declaration whose visibility `SetExported` changes: a type, function, effect or capability.
[<RequireQualifiedAccess>]
type VisibilityTarget =
    | TypeTarget of TypeId
    | FunctionTarget of FunctionId
    | EffectTarget of EffectId
    | CapabilityTarget of CapabilityId

/// A wait or task-binding marker that `SetWaitAttribute` changes
/// (docs/decisions/wait-and-task-bound-attributes.md).
[<RequireQualifiedAccess>]
type WaitAttribute =
    | MayWait of CapabilityOperationId
    | TaskBound of CapabilityId
    | MustNotWait of FunctionId

type SemanticOperation =
    | RenameDisplayName of RenameTarget * string
    /// Sets one wait or task-binding marker; every stable ID and reference is kept.
    | SetWaitAttribute of WaitAttribute * value: bool
    /// Marks one declaration exported (usable from dependents) or private. Only the visibility
    /// changes; every stable ID and reference is kept.
    | SetExported of VisibilityTarget * exported: bool
    | AddFunctionParameter of AddFunctionParameterRequest
    | AddProductField of AddProductFieldRequest
    | RemoveProductField of FieldId
    | DeleteFunction of FunctionId
    | DeleteType of TypeId
    | DeleteEffect of EffectId
    | DeleteCapability of CapabilityId
    | DeleteCapabilityOperation of CapabilityOperationId
    | AddCapabilityOperation of AddCapabilityOperationRequest
    | ResolveMissingArgument of ResolveMissingArgumentRequest
    | AddFunctionRequiredEffect of FunctionRequiredEffectRequest
    | RemoveFunctionRequiredEffect of FunctionRequiredEffectRequest
    | RemoveFunctionParameter of ParameterId
    | RemoveUnionVariant of VariantId
    | AddUnionVariant of AddUnionVariantRequest

type TransactionRequest =
    { Id: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      Operations: SemanticOperation list }

type SymbolRename =
    | TypeRenamed of TypeId * oldDisplayName: string * newDisplayName: string
    | FunctionRenamed of FunctionId * oldDisplayName: string * newDisplayName: string
    /// A Product field (no owner variant) or a Union variant field.
    | FieldRenamed of
        ownerTypeId: TypeId * ownerVariantId: VariantId option * FieldId * oldDisplayName: string * newDisplayName: string
    | VariantRenamed of ownerTypeId: TypeId * VariantId * oldDisplayName: string * newDisplayName: string
    | ParameterRenamed of ownerFunctionId: FunctionId * ParameterId * oldDisplayName: string * newDisplayName: string
    | EffectRenamed of EffectId * oldDisplayName: string * newDisplayName: string
    | CapabilityRenamed of CapabilityId * oldDisplayName: string * newDisplayName: string
    | CapabilityOperationRenamed of
        ownerCapabilityId: CapabilityId * CapabilityOperationId * oldDisplayName: string * newDisplayName: string
    | CapabilityParameterRenamed of
        ownerCapabilityId: CapabilityId
        * ownerOperationId: CapabilityOperationId
        * CapabilityParameterId
        * oldDisplayName: string
        * newDisplayName: string

/// One declaration whose visibility differs between the base and the proposed snapshot. `Kind` is
/// `type`, `function`, `effect` or `capability`; `Exported` is the proposed visibility.
type DeclarationVisibilityChange =
    { Kind: string
      Id: string
      DisplayName: string
      Exported: bool }

/// One marker that differs between the base and the proposed snapshot. `Attribute` is `mayWait`
/// (a capability operation), `taskBound` (a capability) or `mustNotWait` (a function).
type DeclarationAttributeChange =
    { Attribute: string
      Id: string
      DisplayName: string
      Value: bool }

type ParameterAdded =
    { OwnerFunctionId: FunctionId
      AddedParameterId: ParameterId
      AddedDisplayName: string
      AddedType: TypeRef }

/// One removed function parameter. Every DirectCall to the function lost its binding for it (the
/// bound value node stays in the caller graph); every obligation for it was retired, open or resolved.
type ParameterRemoved =
    { OwnerFunctionId: FunctionId
      RemovedParameterId: ParameterId
      DisplayName: string
      AffectedCallNodeIds: GraphNodeId list
      RetiredObligations: MissingArgumentObligation list }

/// One removed Union variant. Every MatchUnion arm naming it dropped it; an arm left with no
/// variant was removed with its body.
type UnionVariantRemoved =
    { OwnerTypeId: TypeId
      RemovedVariantId: VariantId
      DisplayName: string
      RemovedFieldIds: FieldId list
      AffectedMatchNodeIds: GraphNodeId list }

/// One variant added to an existing Union; its variant and field IDs are new.
type UnionVariantAdded =
    { OwnerTypeId: TypeId
      Variant: SemanticVariant }

/// One `MatchUnion` node that gained an arm for an added variant.
type UnionMatchArmAdded =
    { OwnerFunctionId: FunctionId
      MatchNodeId: GraphNodeId
      VariantId: VariantId
      PatternBindingIds: PatternBindingId list }

type ProductFieldRemoved =
    { OwnerTypeId: TypeId
      RemovedFieldId: FieldId
      DisplayName: string
      AffectedConstructNodeIds: GraphNodeId list }

type EffectRemoved =
    { EffectId: EffectId
      DisplayName: string }

type CapabilityRemoved =
    { CapabilityId: CapabilityId
      DisplayName: string
      RemovedOperationIds: CapabilityOperationId list
      RemovedParameterIds: CapabilityParameterId list }

type CapabilityOperationRemoved =
    { OwnerCapabilityId: CapabilityId
      OperationId: CapabilityOperationId
      DisplayName: string
      RemovedParameterIds: CapabilityParameterId list }

/// One operation added to an existing Capability; its operation and parameter IDs are new.
type CapabilityOperationAdded =
    { OwnerCapabilityId: CapabilityId
      Operation: SemanticCapabilityOperation }

/// One `CapabilityConstruct` node that received an implementation for an added operation.
type CapabilityImplementationBound =
    { OwnerFunctionId: FunctionId
      ConstructNodeId: GraphNodeId
      OperationId: CapabilityOperationId
      ImplementationFunctionId: FunctionId }

type TypeRemoved =
    { TypeId: TypeId
      DisplayName: string
      RemovedVariantIds: VariantId list
      RemovedFieldIds: FieldId list }

type FunctionRemoved =
    { FunctionId: FunctionId
      DisplayName: string
      RemovedNodeIds: GraphNodeId list
      /// Resolved records owned by the removed body, retained exactly in the reviewed diff.
      /// Open obligations still block deletion; no live body's obligations are retired.
      RetiredObligations: MissingArgumentObligation list }

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
      /// Visibility changes of `SetExported`, net of the base snapshot, in `kind:id` order.
      VisibilityChanges: DeclarationVisibilityChange list
      /// Marker changes of `SetWaitAttribute`, net of the base snapshot, in `attribute:id` order.
      AttributeChanges: DeclarationAttributeChange list
      ParametersAdded: ParameterAdded list
      ProductFieldsAdded: ProductFieldAdded list
      ProductFieldsBound: ProductFieldBound list
      ProductFieldsRemoved: ProductFieldRemoved list
      FunctionsRemoved: FunctionRemoved list
      TypesRemoved: TypeRemoved list
      EffectsRemoved: EffectRemoved list
      CapabilitiesRemoved: CapabilityRemoved list
      CapabilityOperationsRemoved: CapabilityOperationRemoved list
      CapabilityOperationsAdded: CapabilityOperationAdded list
      CapabilityImplementationsBound: CapabilityImplementationBound list
      ArgumentsAttached: ArgumentAttached list
      ObligationsCreated: MissingArgumentCreated list
      ObligationsResolved: MissingArgumentResolved list
      FunctionRequiredEffectsChanged: FunctionRequiredEffectChange list
      ParametersRemoved: ParameterRemoved list
      UnionVariantsRemoved: UnionVariantRemoved list
      UnionVariantsAdded: UnionVariantAdded list
      UnionMatchArmsAdded: UnionMatchArmAdded list }

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
      TypesRemoved: TypeRemoved list
      EffectsRemoved: EffectRemoved list
      CapabilitiesRemoved: CapabilityRemoved list
      CapabilityOperationsRemoved: CapabilityOperationRemoved list
      CapabilityOperationsAdded: CapabilityOperationAdded list
      CapabilityImplementationsBound: CapabilityImplementationBound list
      ArgumentsAttached: ArgumentAttached list
      ObligationsCreated: MissingArgumentCreated list
      ObligationsResolved: MissingArgumentResolved list
      FunctionRequiredEffectsChanged: FunctionRequiredEffectChange list
      ParametersRemoved: ParameterRemoved list
      UnionVariantsRemoved: UnionVariantRemoved list
      UnionVariantsAdded: UnionVariantAdded list
      UnionMatchArmsAdded: UnionMatchArmAdded list }

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
          TypesRemoved = []
          EffectsRemoved = []
          CapabilitiesRemoved = []
          CapabilityOperationsRemoved = []
          CapabilityOperationsAdded = []
          CapabilityImplementationsBound = []
          ArgumentsAttached = []
          ObligationsCreated = []
          ObligationsResolved = []
          FunctionRequiredEffectsChanged = []
          ParametersRemoved = []
          UnionVariantsRemoved = []
          UnionVariantsAdded = []
          UnionMatchArmsAdded = [] }

    /// Sets one declaration's visibility; TargetNotFound when no declaration has the ID and
    /// VisibilityUnchanged when it already has that visibility.
    let private setExported (target: VisibilityTarget) (exported: bool) (snapshot: Snapshot) =
        let apply label (items: 'T list) (idOf: 'T -> string) (exportedOf: 'T -> bool) (update: 'T -> 'T) (rebuild: 'T list -> Snapshot) =
            match items |> List.tryFind (fun item -> idOf item = label) with
            | None -> Error("TargetNotFound", $"The declaration '{label}' to change the visibility of was not found.")
            | Some item when exportedOf item = exported ->
                let visibility = if exported then "exported" else "private"
                Error("VisibilityUnchanged", $"The declaration '{label}' is already {visibility}.")
            | Some _ -> Ok(rebuild (items |> List.map (fun item -> if idOf item = label then update item else item)))
        match target with
        | VisibilityTarget.TypeTarget id ->
            apply (StableId.value id) snapshot.Types (fun item -> StableId.value item.Id) (fun item -> item.Exported)
                (fun item -> { item with Exported = exported }) (fun types -> { snapshot with Types = types })
        | VisibilityTarget.FunctionTarget id ->
            apply (StableId.value id) snapshot.Functions (fun item -> StableId.value item.Id) (fun item -> item.Exported)
                (fun item -> { item with Exported = exported }) (fun functions -> { snapshot with Functions = functions })
        | VisibilityTarget.EffectTarget id ->
            apply (StableId.value id) snapshot.Effects (fun item -> StableId.value item.Id) (fun item -> item.Exported)
                (fun item -> { item with Exported = exported }) (fun effects -> { snapshot with Effects = effects })
        | VisibilityTarget.CapabilityTarget id ->
            apply (StableId.value id) snapshot.Capabilities (fun item -> StableId.value item.Id) (fun item -> item.Exported)
                (fun item -> { item with Exported = exported }) (fun capabilities -> { snapshot with Capabilities = capabilities })

    /// Sets one wait or task-binding marker; TargetNotFound when nothing has the ID and
    /// AttributeUnchanged when the marker already has that value.
    let private setWaitAttribute (attribute: WaitAttribute) (value: bool) (snapshot: Snapshot) =
        let shown = if value then "true" else "false"
        let unchanged name label = Error("AttributeUnchanged", $"The declaration '{label}' already has {name} {shown}.")
        let notFound label = Error("TargetNotFound", $"The declaration '{label}' to change was not found.")
        match attribute with
        | WaitAttribute.MayWait id ->
            match snapshot.Capabilities |> List.tryPick (fun capability -> capability.Operations |> List.tryFind (fun op -> op.Id = id)) with
            | None -> notFound (StableId.value id)
            | Some op when op.MayWait = value -> unchanged "mayWait" (StableId.value id)
            | Some _ ->
                Ok
                    { snapshot with
                        Capabilities =
                            snapshot.Capabilities
                            |> List.map (fun capability ->
                                { capability with Operations = capability.Operations |> List.map (fun op -> if op.Id = id then { op with MayWait = value } else op) }) }
        | WaitAttribute.TaskBound id ->
            match snapshot.Capabilities |> List.tryFind (fun capability -> capability.Id = id) with
            | None -> notFound (StableId.value id)
            | Some capability when capability.TaskBound = value -> unchanged "taskBound" (StableId.value id)
            | Some _ ->
                Ok { snapshot with Capabilities = snapshot.Capabilities |> List.map (fun capability -> if capability.Id = id then { capability with TaskBound = value } else capability) }
        | WaitAttribute.MustNotWait id ->
            match snapshot.Functions |> List.tryFind (fun fn -> fn.Id = id) with
            | None -> notFound (StableId.value id)
            | Some fn when fn.MustNotWait = value -> unchanged "mustNotWait" (StableId.value id)
            | Some _ ->
                Ok { snapshot with Functions = snapshot.Functions |> List.map (fun fn -> if fn.Id = id then { fn with MustNotWait = value } else fn) }

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

    /// The renamed snapshot, or TargetNotFound when no member has the ID. A rename changes only
    /// the display name: every stable ID and every reference stays as it is.
    let private renameMember label (id: StableId<'Kind>) (renamed: Snapshot option) =
        match renamed with
        | Some snapshot -> Ok snapshot
        | None -> Error("TargetNotFound", $"{label} '{StableId.value id}' was not found.")

    let private renameField (id: FieldId) newDisplayName (snapshot: Snapshot) =
        let mutable found = false
        let rename (field: SemanticField) =
            if field.Id = id then
                found <- true
                { field with DisplayName = newDisplayName }
            else field
        let types =
            snapshot.Types
            |> List.map (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product fields -> { item with Kind = SemanticTypeKind.Product(List.map rename fields) }
                | SemanticTypeKind.Union variants ->
                    { item with
                        Kind =
                            SemanticTypeKind.Union(
                                variants |> List.map (fun variant -> { variant with Fields = List.map rename variant.Fields })
                            ) })
        renameMember "Field" id (if found then Some { snapshot with Types = types } else None)

    let private renameVariant (id: VariantId) newDisplayName (snapshot: Snapshot) =
        let mutable found = false
        let types =
            snapshot.Types
            |> List.map (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product _ -> item
                | SemanticTypeKind.Union variants ->
                    { item with
                        Kind =
                            SemanticTypeKind.Union(
                                variants
                                |> List.map (fun variant ->
                                    if variant.Id = id then
                                        found <- true
                                        { variant with DisplayName = newDisplayName }
                                    else variant)
                            ) })
        renameMember "Union variant" id (if found then Some { snapshot with Types = types } else None)

    let private renameParameter (id: ParameterId) newDisplayName (snapshot: Snapshot) =
        let mutable found = false
        let functions =
            snapshot.Functions
            |> List.map (fun item ->
                { item with
                    Parameters =
                        item.Parameters
                        |> List.map (fun parameter ->
                            if parameter.Id = id then
                                found <- true
                                { parameter with DisplayName = newDisplayName }
                            else parameter) })
        renameMember "Parameter" id (if found then Some { snapshot with Functions = functions } else None)

    let private renameEffect (id: EffectId) newDisplayName (snapshot: Snapshot) =
        let found = snapshot.Effects |> List.exists (fun item -> item.Id = id)
        let effects =
            snapshot.Effects
            |> List.map (fun item -> if item.Id = id then { item with DisplayName = newDisplayName } else item)
        renameMember "Effect" id (if found then Some { snapshot with Effects = effects } else None)

    let private renameCapability (id: CapabilityId) newDisplayName (snapshot: Snapshot) =
        let found = snapshot.Capabilities |> List.exists (fun item -> item.Id = id)
        let capabilities =
            snapshot.Capabilities
            |> List.map (fun item -> if item.Id = id then { item with DisplayName = newDisplayName } else item)
        renameMember "Capability" id (if found then Some { snapshot with Capabilities = capabilities } else None)

    let private renameCapabilityOperation (id: CapabilityOperationId) newDisplayName (snapshot: Snapshot) =
        let mutable found = false
        let capabilities =
            snapshot.Capabilities
            |> List.map (fun capability ->
                { capability with
                    Operations =
                        capability.Operations
                        |> List.map (fun operation ->
                            if operation.Id = id then
                                found <- true
                                { operation with DisplayName = newDisplayName }
                            else operation) })
        renameMember "Capability operation" id (if found then Some { snapshot with Capabilities = capabilities } else None)

    let private renameCapabilityParameter (id: CapabilityParameterId) newDisplayName (snapshot: Snapshot) =
        let mutable found = false
        let capabilities =
            snapshot.Capabilities
            |> List.map (fun capability ->
                { capability with
                    Operations =
                        capability.Operations
                        |> List.map (fun operation ->
                            { operation with
                                Parameters =
                                    operation.Parameters
                                    |> List.map (fun parameter ->
                                        if parameter.Id = id then
                                            found <- true
                                            { parameter with DisplayName = newDisplayName }
                                        else parameter) }) })
        renameMember "Capability parameter" id (if found then Some { snapshot with Capabilities = capabilities } else None)

    let private callPlanNodeId = function
        | BindExistingValue(callNodeId, _) -> callNodeId
        | LeaveMissing(callNodeId, _) -> callNodeId

    let private affectedCalls targetFunctionId (snapshot: Snapshot) =
        snapshot.Functions
        |> List.collect (fun owner ->
            ValueGraph.collectNodes owner.Body
            |> List.choose (fun node ->
                match SemanticNodeShape.tryDirectCall node with
                | Some(functionId, _) when functionId = targetFunctionId -> Some(owner.Id, node.Id)
                | Some _
                | None -> None))

    let private describeFunction (snapshot: Snapshot) (id: FunctionId) =
        let name =
            snapshot.Functions
            |> List.tryFind (fun fn -> fn.Id = id)
            |> Option.map (fun fn -> fn.DisplayName)
            |> Option.defaultValue "?"
        $"'{name}' ({StableId.value id})"

    let private nonCallUseLabel = function
        | FunctionReferenceRole.DirectCall -> None
        | FunctionReferenceRole.PureListCallback
        | FunctionReferenceRole.EffectListCallback -> Some "callback"
        | FunctionReferenceRole.Worker -> Some "worker"
        | FunctionReferenceRole.Body -> Some "scoped body"
        | FunctionReferenceRole.FunctionValue -> Some "function value"
        | FunctionReferenceRole.CapabilityImplementation _ -> Some "capability implementation"

    /// Functions that use the target other than through a DirectCall, each with the kinds of use.
    /// Only direct calls can be planned, since the other roles fix the function's signature.
    let private nonCallUses targetFunctionId (snapshot: Snapshot) =
        snapshot.Functions
        |> List.choose (fun owner ->
            let labels =
                ValueGraph.collectNodes owner.Body
                |> List.collect SemanticNodeShape.functionReferences
                |> List.filter (fun reference -> reference.FunctionId = targetFunctionId)
                |> List.choose (fun reference -> nonCallUseLabel reference.Role)
                |> List.distinct
                |> List.sort
            if List.isEmpty labels then None else Some(owner.Id, labels))
        |> List.sortBy (fst >> StableId.value)

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
            | Some targetFunction when targetFunction.Parameters |> List.exists (fun parameter -> parameter.DisplayName = request.NewParameterDisplayName) ->
                Error("DuplicateParameterDisplayName", "The function already has a parameter with this display name.")
            | Some targetFunction ->
                match nonCallUses request.TargetFunctionId snapshot with
                | _ :: _ as uses ->
                    let users =
                        uses
                        |> List.map (fun (ownerId, labels) ->
                            let kinds = labels |> String.concat ", "
                            $"{describeFunction snapshot ownerId} ({kinds})")
                        |> String.concat "; "
                    Error(
                        "ParameterAdditionNonCallUse",
                        $"Function {describeFunction snapshot targetFunction.Id} is used other than as a direct call by {users}. Those uses fix its signature, so only direct calls can supply a new parameter; replace those uses before adding a parameter to this function."
                    )
                | [] ->
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
                                        // A type parameter the new type introduces joins the declared list last.
                                        TypeParameters.reconcile { functionEntity with Parameters = functionEntity.Parameters @ [ newParameter ] }
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
                        | SemanticNodeKind.ProductConstruct(typeId, _, _) when typeId = target.Id -> Some(owner, node.Id)
                        | _ -> None))
            let affectedIds = affected |> List.map snd |> Set.ofList
            let plannedIds = request.ConstructionPlans |> List.map (fun plan -> plan.ConstructNodeId)
            let plansById =
                request.ConstructionPlans
                |> List.map (fun plan -> plan.ConstructNodeId, plan.ValueNodeId)
                |> Map.ofList
            let addedById =
                request.ConstructionPlans
                |> List.map (fun plan -> plan.ConstructNodeId, plan.AddedNodes)
                |> Map.ofList
            let rec withAddedNodes (graph: ValueGraph) =
                { graph with
                    Nodes =
                        graph.Nodes
                        |> List.collect (fun node ->
                            let node = SemanticNodeShape.mapNestedGraphs withAddedNodes node
                            (Map.tryFind node.Id addedById |> Option.defaultValue []) @ [ node ]) }
            let inserted =
                { snapshot with
                    Functions = snapshot.Functions |> List.map (fun owner -> { owner with Body = withAddedNodes owner.Body }) }
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
                        let owner = inserted.Functions |> List.find (fun item -> item.Id = owner.Id)
                        SemanticFacts.createNodeTypeResolver inserted owner candidate <> Some request.NewFieldType)
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
                        inserted.Functions
                        |> List.map (fun owner ->
                            let mapNode (node: SemanticNode) =
                                match node.Kind, Map.tryFind node.Id plansById with
                                | SemanticNodeKind.ProductConstruct(typeId, typeArguments, bindings), Some valueNodeId ->
                                    { node with
                                        Kind = SemanticNodeKind.ProductConstruct(typeId, typeArguments, bindings @
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
                | SemanticNodeKind.ProductUpdate(_, _, bindings) ->
                    bindings |> List.exists (fun binding -> binding.TargetFieldId = fieldId)
                | _ -> false)
            let affected = nodes |> List.choose (fun (fn, node) ->
                match node.Kind with
                | SemanticNodeKind.ProductConstruct(typeId, _, bindings)
                    when typeId = owner.Id && bindings |> List.exists (fun binding -> binding.TargetFieldId = fieldId) ->
                    Some(fn, node.Id)
                | _ -> None)
            if changes.ProductFieldsAdded |> List.exists (fun item -> item.AddedFieldId = fieldId) then
                Error("ProductFieldCreatedInTransaction", "Remove a newly added field in a separate reviewed change.")
            elif hasAccess then
                Error("ProductFieldStillReferenced", "Repair all field accesses and product updates of this field before removing it.")
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
                        | SemanticNodeKind.ProductConstruct(typeId, typeArguments, bindings) when typeId = owner.Id ->
                            let kept = bindings |> List.filter (fun binding -> binding.TargetFieldId <> fieldId)
                            { node with Kind = SemanticNodeKind.ProductConstruct(typeId, typeArguments, kept) }
                        | _ -> node
                    { fn with Body = ValueGraph.mapNodes mapNode fn.Body })
                let removed: ProductFieldRemoved =
                    { OwnerTypeId = owner.Id
                      RemovedFieldId = field.Id
                      DisplayName = field.DisplayName
                      AffectedConstructNodeIds = affected |> List.map snd |> List.sortBy StableId.value }
                Ok({ snapshot with Types = updatedTypes; Functions = updatedFunctions },
                    { changes with ProductFieldsRemoved = removed :: changes.ProductFieldsRemoved })

    // Earlier operations in a batch may leave the intermediate snapshot invalid. Only issues the
    // deletion itself introduces are blamed on it; issues already present before the deletion are
    // left for a later operation to repair or for the final whole-snapshot validation to report.
    let private afterScheduledDeletions (operations: SemanticOperation list) (snapshot: Snapshot) =
        let functions = operations |> List.choose (function DeleteFunction id -> Some id | _ -> None) |> Set.ofList
        let types = operations |> List.choose (function DeleteType id -> Some id | _ -> None) |> Set.ofList
        let effects = operations |> List.choose (function DeleteEffect id -> Some id | _ -> None) |> Set.ofList
        let capabilities = operations |> List.choose (function DeleteCapability id -> Some id | _ -> None) |> Set.ofList
        let capabilityOperations = operations |> List.choose (function DeleteCapabilityOperation id -> Some id | _ -> None) |> Set.ofList
        { snapshot with
            Functions = snapshot.Functions |> List.filter (fun item -> not (Set.contains item.Id functions))
            Types = snapshot.Types |> List.filter (fun item -> not (Set.contains item.Id types))
            Effects = snapshot.Effects |> List.filter (fun item -> not (Set.contains item.Id effects))
            Capabilities = snapshot.Capabilities |> List.filter (fun item -> not (Set.contains item.Id capabilities)) |> List.map (fun item ->
                { item with Operations = item.Operations |> List.filter (fun operation -> not (Set.contains operation.Id capabilityOperations)) }) }

    let private ensureDeletionSafe code name (scheduled: SemanticOperation list) (current: SemanticOperation option) (beforeDeletion: Snapshot) (candidate: Snapshot) =
        let beforeScheduled =
            match current with
            | Some operation -> scheduled |> List.filter ((<>) operation)
            | None -> scheduled
        match candidate |> afterScheduledDeletions scheduled |> SnapshotValidator.validate with
        | [] -> Ok candidate
        | candidateIssues ->
            let key (issue: ValidationIssue) = issue.Code, issue.Message
            // Matched as a multiset so a deletion that repeats an existing issue is still blamed.
            let preExisting = beforeDeletion |> afterScheduledDeletions beforeScheduled |> SnapshotValidator.validate |> List.countBy key |> Map.ofList
            let introduced =
                candidateIssues
                |> List.fold
                    (fun (remaining, introduced) issue ->
                        match Map.tryFind (key issue) remaining with
                        | Some count when count > 0 -> Map.add (key issue) (count - 1) remaining, introduced
                        | Some _
                        | None -> remaining, issue :: introduced)
                    (preExisting, [])
                |> snd
                |> List.rev
            match introduced with
            | [] -> Ok candidate
            | issues ->
                // Every blocking issue is listed, so one rejection shows the whole repair; the
                // `references` query lists the same sites before the deletion is attempted.
                let listed = issues |> List.map (fun issue -> $"{issue.Code}: {issue.Message}") |> String.concat " ; "
                let count = if issues.Length = 1 then "" else $" ({issues.Length} blocking issues)"
                Error(code, $"Repair all references before deleting this {name}{count}: {listed}")

    let private deleteType
        (scheduled: SemanticOperation list)
        (typeId: TypeId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Types |> List.tryFind (fun item -> item.Id = typeId) with
        | None -> Error("TargetNotFound", "The type to delete was not found.")
        | Some target ->
            if changes.ProductFieldsAdded |> List.exists (fun item -> item.OwnerTypeId = typeId)
               || changes.ProductFieldsRemoved |> List.exists (fun item -> item.OwnerTypeId = typeId)
               || changes.UnionVariantsRemoved |> List.exists (fun item -> item.OwnerTypeId = typeId) then
                Error("TypeModifiedInTransaction", "Delete a type in a separate reviewed change from its field edits.")
            else
                let candidate = { snapshot with Types = snapshot.Types |> List.filter (fun item -> item.Id <> typeId) }
                match ensureDeletionSafe "TypeStillReferenced" "type" scheduled (Some(DeleteType typeId)) snapshot candidate with
                | Error error -> Error error
                | Ok candidate ->
                    let variantIds, fieldIds =
                        match target.Kind with
                        | SemanticTypeKind.Product fields -> [], fields |> List.map (fun item -> item.Id)
                        | SemanticTypeKind.Union variants ->
                            variants |> List.map (fun item -> item.Id),
                            variants |> List.collect (fun variant -> variant.Fields |> List.map (fun field -> field.Id))
                    let removed: TypeRemoved =
                        { TypeId = typeId
                          DisplayName = target.DisplayName
                          RemovedVariantIds = variantIds |> List.sortBy StableId.value
                          RemovedFieldIds = fieldIds |> List.sortBy StableId.value }
                    Ok(candidate, { changes with TypesRemoved = removed :: changes.TypesRemoved })

    let private deleteEffect
        (scheduled: SemanticOperation list)
        (effectId: EffectId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Effects |> List.tryFind (fun item -> item.Id = effectId) with
        | None -> Error("TargetNotFound", "The effect to delete was not found.")
        | Some target ->
            let candidate = { snapshot with Effects = snapshot.Effects |> List.filter (fun item -> item.Id <> effectId) }
            ensureDeletionSafe "EffectStillReferenced" "effect" scheduled (Some(DeleteEffect effectId)) snapshot candidate
            |> Result.map (fun safe ->
                safe,
                { changes with
                    EffectsRemoved =
                        { EffectId = target.Id; DisplayName = target.DisplayName } :: changes.EffectsRemoved })

    let private deleteCapability
        (scheduled: SemanticOperation list)
        (capabilityId: CapabilityId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Capabilities |> List.tryFind (fun item -> item.Id = capabilityId) with
        | None -> Error("TargetNotFound", "The capability to delete was not found.")
        | Some target ->
            let candidate =
                { snapshot with Capabilities = snapshot.Capabilities |> List.filter (fun item -> item.Id <> capabilityId) }
            ensureDeletionSafe "CapabilityStillReferenced" "capability" scheduled (Some(DeleteCapability capabilityId)) snapshot candidate
            |> Result.map (fun safe ->
                let removed: CapabilityRemoved =
                    { CapabilityId = target.Id
                      DisplayName = target.DisplayName
                      RemovedOperationIds = target.Operations |> List.map (fun item -> item.Id) |> List.sortBy StableId.value
                      RemovedParameterIds =
                          target.Operations
                          |> List.collect (fun operation -> operation.Parameters |> List.map (fun parameter -> parameter.Id))
                          |> List.sortBy StableId.value }
                safe, { changes with CapabilitiesRemoved = removed :: changes.CapabilitiesRemoved })

    let private deleteCapabilityOperation
        (scheduled: SemanticOperation list)
        (operationId: CapabilityOperationId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        let target =
            snapshot.Capabilities |> List.tryPick (fun capability ->
                capability.Operations |> List.tryFind (fun operation -> operation.Id = operationId)
                |> Option.map (fun operation -> capability, operation))
        match target with
        | None -> Error("TargetNotFound", "The capability operation to delete was not found.")
        | Some _ when changes.CapabilityOperationsAdded |> List.exists (fun item -> item.Operation.Id = operationId) ->
            Error("CapabilityOperationCreatedInTransaction", "Delete a newly added operation in a separate reviewed change.")
        | Some(owner, operation) ->
            let candidate =
                { snapshot with
                    Capabilities = snapshot.Capabilities |> List.map (fun capability ->
                        if capability.Id = owner.Id then
                            { capability with Operations = capability.Operations |> List.filter (fun item -> item.Id <> operationId) }
                        else capability) }
            ensureDeletionSafe "CapabilityOperationStillReferenced" "capability operation" scheduled (Some(DeleteCapabilityOperation operationId)) snapshot candidate
            |> Result.map (fun safe ->
                let removed: CapabilityOperationRemoved =
                    { OwnerCapabilityId = owner.Id
                      OperationId = operation.Id
                      DisplayName = operation.DisplayName
                      RemovedParameterIds = operation.Parameters |> List.map (fun item -> item.Id) |> List.sortBy StableId.value }
                safe, { changes with CapabilityOperationsRemoved = removed :: changes.CapabilityOperationsRemoved })

    let private addCapabilityOperation
        (request: AddCapabilityOperationRequest)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        let operation = request.Operation
        match snapshot.Capabilities |> List.tryFind (fun item -> item.Id = request.TargetCapabilityId) with
        | None -> Error("TargetNotFound", "The target capability was not found.")
        | Some target ->
            let existingOperationIds =
                snapshot.Capabilities |> List.collect (fun item -> item.Operations |> List.map (fun op -> op.Id)) |> Set.ofList
            let existingParameterIds =
                snapshot.Capabilities
                |> List.collect (fun item -> item.Operations |> List.collect (fun op -> op.Parameters |> List.map (fun p -> p.Id)))
                |> Set.ofList
            let parameterNames = operation.Parameters |> List.map (fun item -> item.DisplayName)
            // Every program implementation of the Capability, with its owning function.
            let affected =
                snapshot.Functions
                |> List.collect (fun owner ->
                    ValueGraph.collectNodes owner.Body
                    |> List.choose (fun node ->
                        match node.Kind with
                        | SemanticNodeKind.CapabilityConstruct(capabilityId, _) when capabilityId = target.Id -> Some(owner, node.Id)
                        | _ -> None))
            let affectedIds = affected |> List.map snd |> Set.ofList
            let plannedIds = request.ImplementationPlans |> List.map (fun plan -> plan.ConstructNodeId)
            let plansById =
                request.ImplementationPlans
                |> List.map (fun plan -> plan.ConstructNodeId, plan.ImplementationFunctionId)
                |> Map.ofList
            if String.IsNullOrWhiteSpace operation.DisplayName then
                Error("InvalidDisplayName", "The new operation display name cannot be blank.")
            elif target.Operations |> List.exists (fun item -> item.DisplayName = operation.DisplayName) then
                Error("DuplicateCapabilityOperationDisplayName", $"Capability '{target.DisplayName}' already has an operation named '{operation.DisplayName}'; operation display names must be unique within their Capability.")
            elif parameterNames |> List.exists String.IsNullOrWhiteSpace then
                Error("InvalidDisplayName", "Operation parameter display names cannot be blank.")
            elif List.distinct parameterNames <> parameterNames then
                Error("DuplicateCapabilityParameterDisplayName", "Operation parameter display names must be distinct within their operation.")
            elif Set.contains operation.Id existingOperationIds
                 || operation.Parameters |> List.exists (fun item -> Set.contains item.Id existingParameterIds)
                 || (operation.Parameters |> List.map (fun item -> item.Id) |> List.distinct).Length <> operation.Parameters.Length then
                Error("DuplicateCapabilityOperationId", "The new operation or parameter ID already exists.")
            elif snapshot.Effects |> List.exists (fun effect -> effect.Id = operation.EffectId) |> not then
                Error("EffectNotFound", $"Effect '{StableId.value operation.EffectId}' was not found.")
            elif plannedIds.Length <> affectedIds.Count || Set.ofList plannedIds <> affectedIds then
                let constructs =
                    affected
                    |> List.map (fun (owner, nodeId) -> $"'{StableId.value nodeId}' in function {describeFunction snapshot owner.Id}")
                    |> String.concat ", "
                let listed = if List.isEmpty affected then "there are none" else constructs
                Error(
                    "CapabilityImplementationPlanMismatch",
                    $"Every program implementation (capabilityConstruct) of Capability '{target.DisplayName}' requires exactly one implementation plan for the new operation; the constructions are: {listed}."
                )
            else
                let updatedCapabilities =
                    snapshot.Capabilities
                    |> List.map (fun item ->
                        if item.Id = target.Id then { item with Operations = item.Operations @ [ operation ] } else item)
                let updatedFunctions =
                    snapshot.Functions
                    |> List.map (fun owner ->
                        let mapNode (node: SemanticNode) =
                            match node.Kind, Map.tryFind node.Id plansById with
                            | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations), Some functionId ->
                                { node with Kind = SemanticNodeKind.CapabilityConstruct(capabilityId, implementations @ [ operation.Id, functionId ]) }
                            | _ -> node
                        { owner with Body = ValueGraph.mapNodes mapNode owner.Body })
                let candidate = { snapshot with Capabilities = updatedCapabilities; Functions = updatedFunctions }
                // The planned implementations are checked here with the same contract the validator
                // applies, so the rejection names the operation's own code instead of a whole-snapshot one.
                let invalidPlan =
                    affected
                    |> List.tryPick (fun (owner, nodeId) ->
                        let functionId = Map.find nodeId plansById
                        CapabilityImplementations.validate candidate target.Id [ operation.Id, functionId ]
                        |> List.filter (fun (code, _) -> code <> "CapabilityImplementationSetMismatch")
                        |> List.tryHead
                        |> Option.map (fun (code, message) -> code, $"Capability construction '{StableId.value nodeId}' in function {describeFunction snapshot owner.Id}: {message}"))
                match invalidPlan with
                | Some error -> Error error
                | None ->
                    let added: CapabilityOperationAdded = { OwnerCapabilityId = target.Id; Operation = operation }
                    let bound =
                        affected
                        |> List.map (fun (owner, nodeId) ->
                            { OwnerFunctionId = owner.Id
                              ConstructNodeId = nodeId
                              OperationId = operation.Id
                              ImplementationFunctionId = Map.find nodeId plansById })
                    Ok(
                        candidate,
                        { changes with
                            CapabilityOperationsAdded = added :: changes.CapabilityOperationsAdded
                            CapabilityImplementationsBound = bound @ changes.CapabilityImplementationsBound }
                    )

    let private deleteFunction
        (scheduled: SemanticOperation list)
        (functionId: FunctionId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match snapshot.Functions |> List.tryFind (fun fn -> fn.Id = functionId) with
        | None -> Error("TargetNotFound", "The function to delete was not found.")
        | Some target ->
            // A function's references to itself (SelfCall/DirectCall, its own callbacks) disappear with
            // its body, so only other functions' references block the deletion.
            let callers =
                SemanticQuery.getCallers snapshot functionId
                |> Result.map (List.filter (fun site ->
                    site.CallerFunctionId <> functionId
                    && not (scheduled |> List.exists (function DeleteFunction id -> id = site.CallerFunctionId | _ -> false))))
            let bodyNodeIds =
                ValueGraph.collectNodes target.Body |> List.map (fun node -> node.Id) |> Set.ofList
            let ownedObligations =
                snapshot.Obligations |> List.filter (fun item -> Set.contains item.CallNodeId bodyNodeIds)
            match callers with
            | Error _ -> Error("FunctionCallSiteInspectionFailed", "The function's callers could not be inspected.")
            | Ok (_ :: _ as sites) ->
                let describe = describeFunction snapshot
                let referrers =
                    sites
                    |> List.map (fun site -> site.CallerFunctionId)
                    |> List.distinct
                    |> List.sortBy StableId.value
                    |> List.map describe
                    |> String.concat ", "
                Error(
                    "FunctionStillReferenced",
                    $"Function {describe functionId} is still referenced by {referrers}. Repair or remove those direct calls, callbacks and function references before deleting this function."
                )
            | Ok [] when ownedObligations |> List.exists (fun item -> item.Status = ObligationStatus.Open) ->
                Error("FunctionHasObligations", "Resolve open obligations attached to this function before deletion. Deleting the function retires only its resolved obligations, with their exact records in the reviewed diff.")
            | Ok [] ->
                let removed: FunctionRemoved =
                    { FunctionId = target.Id
                      DisplayName = target.DisplayName
                      RemovedNodeIds = bodyNodeIds |> Set.toList |> List.sortBy StableId.value
                      RetiredObligations = ownedObligations |> List.sortBy (fun item -> StableId.value item.Id) }
                Ok(
                    { snapshot with
                        Functions = snapshot.Functions |> List.filter (fun fn -> fn.Id <> functionId)
                        Obligations = snapshot.Obligations |> List.filter (fun item -> not (Set.contains item.CallNodeId bodyNodeIds)) },
                    { changes with FunctionsRemoved = removed :: changes.FunctionsRemoved }
                )

    let private removeFunctionParameter
        (parameterId: ParameterId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        let target =
            snapshot.Functions
            |> List.tryPick (fun fn ->
                fn.Parameters
                |> List.tryFind (fun parameter -> parameter.Id = parameterId)
                |> Option.map (fun parameter -> fn, parameter))
        match target with
        | None -> Error("TargetNotFound", "The function parameter to remove was not found.")
        | Some(owner, parameter) ->
            let describe = describeFunction snapshot
            let subject = $"Parameter {StableId.describe parameter.DisplayName parameterId} of function {describe owner.Id}"
            let referenced =
                ValueGraph.collectNodes owner.Body
                |> List.exists (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.ParameterReference id -> id = parameterId
                    | _ -> false)
            let bindsParameter (fn: SemanticFunction) =
                ValueGraph.collectNodes fn.Body
                |> List.exists (fun node ->
                    match SemanticNodeShape.tryDirectCall node with
                    | Some(calleeId, arguments) when calleeId = owner.Id ->
                        arguments |> List.exists (fun argument -> argument.TargetParameterId = parameterId)
                    | Some _
                    | None -> false)
            if changes.ParametersAdded |> List.exists (fun item -> item.AddedParameterId = parameterId) then
                Error("ParameterCreatedInTransaction", "Remove a newly added parameter in a separate reviewed change.")
            elif referenced then
                Error(
                    "ParameterStillReferenced",
                    $"{subject} is still referenced by the function body; replace those references before removing it."
                )
            else
                match nonCallUses owner.Id snapshot with
                | _ :: _ as uses ->
                    let users =
                        uses
                        |> List.map (fun (ownerId, labels) ->
                            let kinds = labels |> String.concat ", "
                            $"{describe ownerId} ({kinds})")
                        |> String.concat "; "
                    Error(
                        "ParameterRemovalNonCallUse",
                        $"Function {describe owner.Id} is used other than as a direct call by {users}. Those uses fix its signature, so only direct calls can drop an argument; replace those uses before removing a parameter of this function."
                    )
                | [] ->
                    match
                        snapshot.Functions
                        |> List.filter (fun fn -> bindsParameter fn && not (List.isEmpty fn.RequiredEffects))
                        |> List.sortBy (fun fn -> StableId.value fn.Id)
                    with
                    | effectful :: _ ->
                        Error(
                            "ParameterRemovalMayDiscardEffects",
                            $"Function {describe effectful.Id} declares effects and binds an argument to {subject}; preserve evaluation explicitly before removing the argument binding."
                        )
                    | [] ->
                        let dropBinding (node: SemanticNode) =
                            match node.Kind with
                            | SemanticNodeKind.DirectCall(calleeId, arguments) when calleeId = owner.Id ->
                                let kept = arguments |> List.filter (fun argument -> argument.TargetParameterId <> parameterId)
                                { node with Kind = SemanticNodeKind.DirectCall(calleeId, kept) }
                            | _ -> node
                        let updatedFunctions =
                            snapshot.Functions
                            |> List.map (fun fn ->
                                let parameters =
                                    if fn.Id = owner.Id then
                                        fn.Parameters |> List.filter (fun item -> item.Id <> parameterId)
                                    else fn.Parameters
                                // A type parameter no remaining parameter type mentions leaves the declared list;
                                // a result or body that still uses it fails validation (TypeParameterNotDeclared).
                                TypeParameters.reconcile { fn with Parameters = parameters; Body = ValueGraph.mapNodes dropBinding fn.Body })
                        let retired, kept = snapshot.Obligations |> List.partition (fun item -> item.ParameterId = parameterId)
                        let removed: ParameterRemoved =
                            { OwnerFunctionId = owner.Id
                              RemovedParameterId = parameterId
                              DisplayName = parameter.DisplayName
                              AffectedCallNodeIds = affectedCalls owner.Id snapshot |> List.map snd |> List.sortBy StableId.value
                              RetiredObligations = retired |> List.sortBy (fun item -> StableId.value item.Id) }
                        Ok(
                            { snapshot with Functions = updatedFunctions; Obligations = kept },
                            { changes with ParametersRemoved = removed :: changes.ParametersRemoved }
                        )

    /// A pattern-binding ID for a field a narrowed arm must now bind, derived from the transaction,
    /// the match node and the field so that re-simulating the same request yields the same snapshot.
    let private derivedPatternBindingId (transactionId: TransactionId) (matchNodeId: GraphNodeId) (fieldId: FieldId) =
        StableIdAllocator.derived<PatternBindingTag>
            $"{StableId.value transactionId}|{StableId.value matchNodeId}|{StableId.value fieldId}"

    let private removeUnionVariant
        (transactionId: TransactionId)
        (variantId: VariantId)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        let target =
            snapshot.Types
            |> List.tryPick (fun item ->
                match item.Kind with
                | SemanticTypeKind.Union variants ->
                    variants
                    |> List.tryFind (fun variant -> variant.Id = variantId)
                    |> Option.map (fun variant -> item, variants, variant)
                | SemanticTypeKind.Product _ -> None)
        match target with
        | None -> Error("TargetNotFound", "The Union variant to remove was not found.")
        | Some _ when changes.UnionVariantsAdded |> List.exists (fun added -> added.Variant.Id = variantId) ->
            // The variant ID is system-owned, so a batch cannot name a variant it adds; the review does.
            Error("UnionVariantCreatedInTransaction", "The Union variant to remove was added in this transaction; leave it out of the batch instead.")
        | Some(owner, variants, variant) ->
            let describe = describeFunction snapshot
            let subject = $"Variant {StableId.describe variant.DisplayName variantId} of Union {StableId.describe owner.DisplayName owner.Id}"
            let nodes =
                snapshot.Functions
                |> List.collect (fun fn -> ValueGraph.collectNodes fn.Body |> List.map (fun node -> fn, node))
            let constructors =
                nodes
                |> List.choose (fun (fn, node) ->
                    match node.Kind with
                    | SemanticNodeKind.UnionConstruct(id, _, _) when id = variantId -> Some fn.Id
                    | _ -> None)
                |> List.distinct
                |> List.sortBy StableId.value
            let matches =
                nodes
                |> List.choose (fun (fn, node) ->
                    match node.Kind with
                    | SemanticNodeKind.MatchUnion(_, arms) when arms |> List.exists (fun arm -> List.contains variantId arm.VariantIds) ->
                        Some(fn, node.Id, arms)
                    | _ -> None)
            let dropsArm (arms: UnionMatchArm list) = arms |> List.exists (fun arm -> arm.VariantIds = [ variantId ])
            if List.length variants = 1 then
                Error("UnionVariantLast", $"{subject} is the Union's only variant; delete the type instead.")
            elif not (List.isEmpty constructors) then
                let users = constructors |> List.map describe |> String.concat ", "
                Error(
                    "UnionVariantStillConstructed",
                    $"{subject} is still constructed by {users}; replace those constructions before removing the variant."
                )
            else
                match
                    matches
                    |> List.filter (fun (fn, _, arms) -> dropsArm arms && not (List.isEmpty fn.RequiredEffects))
                    |> List.sortBy (fun (fn, nodeId, _) -> StableId.value fn.Id, StableId.value nodeId)
                with
                | (fn, nodeId, _) :: _ ->
                    Error(
                        "UnionVariantRemovalMayDiscardEffects",
                        $"Function {describe fn.Id} declares effects and its match '{StableId.value nodeId}' has an arm for {subject} alone; preserve evaluation explicitly before removing that arm."
                    )
                | [] ->
                    let remaining = variants |> List.filter (fun item -> item.Id <> variantId)
                    // A generic Union's narrowed arm binds the fields at the match input's type arguments.
                    let narrow (inputArguments: TypeRef list) (matchNodeId: GraphNodeId) (arm: UnionMatchArm): UnionMatchArm option =
                        if not (List.contains variantId arm.VariantIds) then Some arm
                        else
                            match arm.VariantIds |> List.filter (fun id -> id <> variantId) with
                            | [] -> None
                            | [ single ] ->
                                // An arm naming several variants binds no fields; one naming one variant
                                // binds each of its fields exactly once, so the narrowed arm binds them unused.
                                let fields =
                                    remaining
                                    |> List.tryFind (fun item -> item.Id = single)
                                    |> Option.map (fun item -> item.Fields)
                                    |> Option.defaultValue []
                                let bindings: PatternFieldBinding list =
                                    fields
                                    |> List.map (fun field ->
                                        { SourceFieldId = field.Id
                                          Binding =
                                            { Id = derivedPatternBindingId transactionId matchNodeId field.Id
                                              DisplayName = None
                                              Type = TypeParameters.instantiate owner inputArguments field.Type } })
                                Some { arm with VariantIds = [ single ]; FieldBindings = bindings }
                            | several -> Some { arm with VariantIds = several }
                    let pruneMatch (inputType: GraphNodeId -> TypeRef option) (node: SemanticNode) =
                        match node.Kind with
                        | SemanticNodeKind.MatchUnion(inputNodeId, arms) when arms |> List.exists (fun arm -> List.contains variantId arm.VariantIds) ->
                            let inputArguments =
                                match inputType inputNodeId with
                                | Some(TypeRef.Named(_, arguments)) -> arguments
                                | _ -> []
                            { node with Kind = SemanticNodeKind.MatchUnion(inputNodeId, arms |> List.choose (narrow inputArguments node.Id)) }
                        | _ -> node
                    let candidate =
                        { snapshot with
                            Types =
                                snapshot.Types
                                |> List.map (fun item ->
                                    if item.Id = owner.Id then { item with Kind = SemanticTypeKind.Union remaining } else item)
                            Functions =
                                let resolverFor = SemanticFacts.createNodeTypeResolverFactory snapshot
                                snapshot.Functions
                                |> List.map (fun fn ->
                                    { fn with Body = ValueGraph.mapNodes (pruneMatch (resolverFor fn)) fn.Body }) }
                    ensureDeletionSafe "UnionVariantStillReferenced" "Union variant" [] None snapshot candidate
                    |> Result.map (fun safe ->
                        let removed: UnionVariantRemoved =
                            { OwnerTypeId = owner.Id
                              RemovedVariantId = variantId
                              DisplayName = variant.DisplayName
                              RemovedFieldIds = variant.Fields |> List.map (fun field -> field.Id) |> List.sortBy StableId.value
                              AffectedMatchNodeIds =
                                matches |> List.map (fun (_, nodeId, _) -> nodeId) |> List.sortBy StableId.value }
                        safe, { changes with UnionVariantsRemoved = removed :: changes.UnionVariantsRemoved })

    let private addUnionVariant
        (request: AddUnionVariantRequest)
        (snapshot: Snapshot)
        (changes: ChangeAccumulator)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        let variant = request.Variant
        match snapshot.Types |> List.tryFind (fun item -> item.Id = request.TargetTypeId) with
        | None -> Error("TargetNotFound", "The target type was not found.")
        | Some { Kind = SemanticTypeKind.Product _ } -> Error("TargetNotUnion", "The target type of addUnionVariant must be a Union.")
        | Some({ Kind = SemanticTypeKind.Union variants } as owner) ->
            let describe = describeFunction snapshot
            let unionVariantIds = variants |> List.map (fun item -> item.Id) |> Set.ofList
            let existingVariantIds =
                snapshot.Types
                |> List.collect (fun item ->
                    match item.Kind with
                    | SemanticTypeKind.Union items -> items |> List.map (fun item -> item.Id)
                    | SemanticTypeKind.Product _ -> [])
                |> Set.ofList
            let existingFieldIds =
                snapshot.Types
                |> List.collect (fun item ->
                    match item.Kind with
                    | SemanticTypeKind.Union items -> items |> List.collect (fun item -> item.Fields |> List.map (fun field -> field.Id))
                    | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.Id))
                |> Set.ofList
            let fieldNames = variant.Fields |> List.map (fun field -> field.DisplayName)
            let fieldIds = variant.Fields |> List.map (fun field -> field.Id)
            // Every exhaustive match over the Union, with its owner and its input's type arguments.
            // Each arm names variants of exactly one Union, so one arm identifies the matched Union.
            let resolverFor = SemanticFacts.createNodeTypeResolverFactory snapshot
            let affected =
                snapshot.Functions
                |> List.collect (fun fn ->
                    let inputType = resolverFor fn
                    ValueGraph.collectNodes fn.Body
                    |> List.choose (fun node ->
                        match node.Kind with
                        | SemanticNodeKind.MatchUnion(inputNodeId, arms) when
                            arms |> List.exists (fun arm -> arm.VariantIds |> List.exists (fun id -> Set.contains id unionVariantIds)) ->
                            let arguments =
                                match inputType inputNodeId with
                                | Some(TypeRef.Named(_, arguments)) -> arguments
                                | _ -> []
                            Some(fn, node.Id, arguments)
                        | _ -> None))
            let affectedIds = affected |> List.map (fun (_, nodeId, _) -> nodeId) |> Set.ofList
            let plannedIds = request.ArmPlans |> List.map (fun plan -> plan.MatchNodeId)
            let plansById = request.ArmPlans |> List.map (fun plan -> plan.MatchNodeId, plan) |> Map.ofList
            if String.IsNullOrWhiteSpace variant.DisplayName then
                Error("InvalidDisplayName", "The new variant display name cannot be blank.")
            elif variants |> List.exists (fun item -> item.DisplayName = variant.DisplayName) then
                Error("DuplicateUnionVariantDisplayName", $"Union '{owner.DisplayName}' already has a variant named '{variant.DisplayName}'; variant display names must be unique within their Union.")
            elif fieldNames |> List.exists String.IsNullOrWhiteSpace then
                Error("InvalidDisplayName", "Variant field display names cannot be blank.")
            elif List.distinct fieldNames <> fieldNames then
                Error("DuplicateFieldDisplayName", "Variant field display names must be distinct within their variant.")
            elif Set.contains variant.Id existingVariantIds
                 || fieldIds |> List.exists (fun id -> Set.contains id existingFieldIds)
                 || (List.distinct fieldIds).Length <> fieldIds.Length then
                Error("DuplicateUnionVariantId", "The new variant or field ID already exists.")
            elif plannedIds.Length <> affectedIds.Count || Set.ofList plannedIds <> affectedIds then
                let matches =
                    affected
                    |> List.map (fun (fn, nodeId, _) -> $"'{StableId.value nodeId}' in function {describe fn.Id}")
                    |> String.concat ", "
                let listed = if List.isEmpty affected then "there are none" else matches
                Error(
                    "UnionMatchArmPlanMismatch",
                    $"Every match (matchUnion) over Union '{owner.DisplayName}' requires exactly one arm plan for the new variant; the matches are: {listed}."
                )
            else
                // Each plan binds every new field exactly once, at the field type the match input's
                // type arguments give it, as the validator will check the published arm.
                let invalidPlan =
                    affected
                    |> List.tryPick (fun (fn, nodeId, arguments) ->
                        let plan = Map.find nodeId plansById
                        let site = $"match '{StableId.value nodeId}' in function {describe fn.Id}"
                        let expected = variant.Fields |> List.map (fun field -> field.Id, TypeParameters.instantiate owner arguments field.Type) |> List.sortBy (fst >> StableId.value)
                        let actual = plan.FieldBindings |> List.map (fun item -> item.SourceFieldId, item.Binding.Type) |> List.sortBy (fst >> StableId.value)
                        if (actual |> List.map fst) <> (expected |> List.map fst) then
                            Some("UnionMatchArmPlanFieldMismatch", $"The arm plan for {site} must bind each field of the new variant exactly once.")
                        elif actual <> expected then
                            Some("UnionMatchArmBindingTypeMismatch", $"The arm plan for {site} binds a field at a type other than the one the match input gives it.")
                        else None)
                match invalidPlan with
                | Some error -> Error error
                | None ->
                    let updatedTypes =
                        snapshot.Types
                        |> List.map (fun item ->
                            if item.Id = owner.Id then { item with Kind = SemanticTypeKind.Union(variants @ [ variant ]) } else item)
                    let extend (node: SemanticNode) =
                        match node.Kind, Map.tryFind node.Id plansById with
                        | SemanticNodeKind.MatchUnion(inputNodeId, arms), Some plan ->
                            let arm: UnionMatchArm = { VariantIds = [ variant.Id ]; FieldBindings = plan.FieldBindings; Body = plan.Body }
                            { node with Kind = SemanticNodeKind.MatchUnion(inputNodeId, arms @ [ arm ]) }
                        | _ -> node
                    let updatedFunctions =
                        snapshot.Functions
                        |> List.map (fun fn -> if affected |> List.exists (fun (owner, _, _) -> owner.Id = fn.Id) then { fn with Body = ValueGraph.mapNodes extend fn.Body } else fn)
                    let added: UnionVariantAdded = { OwnerTypeId = owner.Id; Variant = variant }
                    let arms =
                        affected
                        |> List.map (fun (fn, nodeId, _) ->
                            { OwnerFunctionId = fn.Id
                              MatchNodeId = nodeId
                              VariantId = variant.Id
                              PatternBindingIds = (Map.find nodeId plansById).FieldBindings |> List.map (fun item -> item.Binding.Id) })
                    Ok(
                        { snapshot with Types = updatedTypes; Functions = updatedFunctions },
                        { changes with
                            UnionVariantsAdded = added :: changes.UnionVariantsAdded
                            UnionMatchArmsAdded = arms @ changes.UnionMatchArmsAdded }
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
                        match SemanticNodeShape.tryDirectCall node with
                        | Some(targetFunctionId, arguments) ->
                            attached <- true
                            let binding: ArgumentBinding =
                                { TargetParameterId = obligation.ParameterId
                                  ValueNodeId = request.ResolutionValueNodeId }
                            { node with
                                Kind = SemanticNodeKind.DirectCall(targetFunctionId, arguments @ [ binding ]) }
                        | None -> node

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
        (scheduled: SemanticOperation list)
        ((snapshot, changes): Snapshot * ChangeAccumulator)
        (operation: SemanticOperation)
        : Result<Snapshot * ChangeAccumulator, string * string> =
        match operation with
        | RenameDisplayName(_, newDisplayName) when String.IsNullOrWhiteSpace newDisplayName ->
            Error("InvalidDisplayName", "A display name cannot be blank.")
        | RenameDisplayName(_, newDisplayName) when LooseJsonString.hasLoneSurrogate newDisplayName ->
            Error("InvalidDisplayName", "A display name cannot contain a lone UTF-16 surrogate.")
        | RenameDisplayName(RenameTarget.TypeTarget id, newDisplayName) ->
            renameType id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.FunctionTarget id, newDisplayName) ->
            renameFunction id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.FieldTarget id, newDisplayName) ->
            renameField id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.VariantTarget id, newDisplayName) ->
            renameVariant id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.ParameterTarget id, newDisplayName) ->
            renameParameter id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.EffectTarget id, newDisplayName) ->
            renameEffect id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.CapabilityTarget id, newDisplayName) ->
            renameCapability id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.CapabilityOperationTarget id, newDisplayName) ->
            renameCapabilityOperation id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | RenameDisplayName(RenameTarget.CapabilityParameterTarget id, newDisplayName) ->
            renameCapabilityParameter id newDisplayName snapshot |> Result.map (fun next -> next, changes)
        | SetExported(target, exported) -> setExported target exported snapshot |> Result.map (fun next -> next, changes)
        | SetWaitAttribute(attribute, value) -> setWaitAttribute attribute value snapshot |> Result.map (fun next -> next, changes)
        | AddFunctionParameter request -> addFunctionParameter transactionId request snapshot changes
        | AddProductField request -> addProductField request snapshot changes
        | RemoveProductField fieldId -> removeProductField fieldId snapshot changes
        | DeleteFunction functionId -> deleteFunction scheduled functionId snapshot changes
        | DeleteType typeId -> deleteType scheduled typeId snapshot changes
        | DeleteEffect effectId -> deleteEffect scheduled effectId snapshot changes
        | DeleteCapability capabilityId -> deleteCapability scheduled capabilityId snapshot changes
        | DeleteCapabilityOperation operationId -> deleteCapabilityOperation scheduled operationId snapshot changes
        | AddCapabilityOperation request -> addCapabilityOperation request snapshot changes
        | ResolveMissingArgument request -> resolveMissingArgument transactionId request snapshot changes
        | AddFunctionRequiredEffect request -> changeFunctionRequiredEffect true request snapshot changes
        | RemoveFunctionRequiredEffect request -> changeFunctionRequiredEffect false request snapshot changes
        | RemoveFunctionParameter parameterId -> removeFunctionParameter parameterId snapshot changes
        | RemoveUnionVariant variantId -> removeUnionVariant transactionId variantId snapshot changes
        | AddUnionVariant request -> addUnionVariant request snapshot changes

    let private netRenames (baseSnapshot: Snapshot) (proposedSnapshot: Snapshot) =
        let changed (before: Map<StableId<'Kind>, string>) id name rename =
            match Map.tryFind id before with
            | Some oldName when oldName <> name -> Some(rename oldName)
            | Some _
            | None -> None
        let fieldsOf (item: SemanticType) =
            match item.Kind with
            | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> None, field)
            | SemanticTypeKind.Union variants ->
                variants |> List.collect (fun variant -> variant.Fields |> List.map (fun field -> Some variant.Id, field))
        let variantsOf (item: SemanticType) =
            match item.Kind with
            | SemanticTypeKind.Product _ -> []
            | SemanticTypeKind.Union variants -> variants
        let typeNames = baseSnapshot.Types |> List.map (fun item -> item.Id, item.DisplayName) |> Map.ofList
        let functionNames = baseSnapshot.Functions |> List.map (fun item -> item.Id, item.DisplayName) |> Map.ofList
        let fieldNames =
            baseSnapshot.Types |> List.collect fieldsOf |> List.map (fun (_, field) -> field.Id, field.DisplayName) |> Map.ofList
        let variantNames =
            baseSnapshot.Types |> List.collect variantsOf |> List.map (fun variant -> variant.Id, variant.DisplayName) |> Map.ofList
        let parameterNames =
            baseSnapshot.Functions
            |> List.collect (fun item -> item.Parameters |> List.map (fun parameter -> parameter.Id, parameter.DisplayName))
            |> Map.ofList
        let effectNames = baseSnapshot.Effects |> List.map (fun item -> item.Id, item.DisplayName) |> Map.ofList
        let capabilityNames = baseSnapshot.Capabilities |> List.map (fun item -> item.Id, item.DisplayName) |> Map.ofList
        let operationNames =
            baseSnapshot.Capabilities
            |> List.collect (fun item -> item.Operations |> List.map (fun operation -> operation.Id, operation.DisplayName))
            |> Map.ofList
        let capabilityParameterNames =
            baseSnapshot.Capabilities
            |> List.collect (fun item -> item.Operations |> List.collect (fun operation -> operation.Parameters))
            |> List.map (fun parameter -> parameter.Id, parameter.DisplayName)
            |> Map.ofList

        let typeRenames =
            proposedSnapshot.Types
            |> List.choose (fun item ->
                changed typeNames item.Id item.DisplayName (fun oldName -> TypeRenamed(item.Id, oldName, item.DisplayName)))

        let functionRenames =
            proposedSnapshot.Functions
            |> List.choose (fun item ->
                changed functionNames item.Id item.DisplayName (fun oldName -> FunctionRenamed(item.Id, oldName, item.DisplayName)))

        let typeMemberRenames =
            proposedSnapshot.Types
            |> List.collect (fun item ->
                (fieldsOf item
                 |> List.choose (fun (variantId, field) ->
                     changed fieldNames field.Id field.DisplayName (fun oldName ->
                         FieldRenamed(item.Id, variantId, field.Id, oldName, field.DisplayName))))
                @ (variantsOf item
                   |> List.choose (fun variant ->
                       changed variantNames variant.Id variant.DisplayName (fun oldName ->
                           VariantRenamed(item.Id, variant.Id, oldName, variant.DisplayName)))))

        let parameterRenames =
            proposedSnapshot.Functions
            |> List.collect (fun item ->
                item.Parameters
                |> List.choose (fun parameter ->
                    changed parameterNames parameter.Id parameter.DisplayName (fun oldName ->
                        ParameterRenamed(item.Id, parameter.Id, oldName, parameter.DisplayName))))

        let effectRenames =
            proposedSnapshot.Effects
            |> List.choose (fun item ->
                changed effectNames item.Id item.DisplayName (fun oldName -> EffectRenamed(item.Id, oldName, item.DisplayName)))

        let capabilityRenames =
            proposedSnapshot.Capabilities
            |> List.collect (fun item ->
                let operationRenames =
                    item.Operations
                    |> List.collect (fun operation ->
                        let renamedOperation =
                            changed operationNames operation.Id operation.DisplayName (fun oldName ->
                                CapabilityOperationRenamed(item.Id, operation.Id, oldName, operation.DisplayName))
                        let renamedParameters =
                            operation.Parameters
                            |> List.choose (fun parameter ->
                                changed capabilityParameterNames parameter.Id parameter.DisplayName (fun oldName ->
                                    CapabilityParameterRenamed(item.Id, operation.Id, parameter.Id, oldName, parameter.DisplayName)))
                        Option.toList renamedOperation @ renamedParameters)
                let renamedCapability =
                    changed capabilityNames item.Id item.DisplayName (fun oldName ->
                        CapabilityRenamed(item.Id, oldName, item.DisplayName))
                Option.toList renamedCapability @ operationRenames)

        // Type and function entries keep their established keys, so their relative order is unchanged.
        typeRenames @ functionRenames @ typeMemberRenames @ parameterRenames @ effectRenames @ capabilityRenames
        |> List.sortBy (function
            | TypeRenamed(id, _, _) -> "type:" + StableId.value id
            | FunctionRenamed(id, _, _) -> "function:" + StableId.value id
            | FieldRenamed(_, _, id, _, _) -> "field:" + StableId.value id
            | VariantRenamed(_, id, _, _) -> "variant:" + StableId.value id
            | ParameterRenamed(_, id, _, _) -> "parameter:" + StableId.value id
            | EffectRenamed(id, _, _) -> "effect:" + StableId.value id
            | CapabilityRenamed(id, _, _) -> "capability:" + StableId.value id
            | CapabilityOperationRenamed(_, id, _, _) -> "capabilityOperation:" + StableId.value id
            | CapabilityParameterRenamed(_, _, id, _, _) -> "capabilityParameter:" + StableId.value id)

    /// The declarations whose visibility differs between the base and the proposed snapshot, in
    /// `kind:id` order, so a visibility set and reset in one batch is no change.
    let private netVisibilityChanges (baseSnapshot: Snapshot) (proposedSnapshot: Snapshot) =
        let changes kind (before: 'T list) (after: 'T list) (idOf: 'T -> string) (nameOf: 'T -> string) (exportedOf: 'T -> bool) =
            let previous = before |> List.map (fun item -> idOf item, exportedOf item) |> Map.ofList
            after
            |> List.choose (fun item ->
                match Map.tryFind (idOf item) previous with
                | Some wasExported when wasExported <> exportedOf item ->
                    Some { Kind = kind; Id = idOf item; DisplayName = nameOf item; Exported = exportedOf item }
                | _ -> None)
        changes "type" baseSnapshot.Types proposedSnapshot.Types (fun item -> StableId.value item.Id) (fun item -> item.DisplayName) (fun item -> item.Exported)
        @ changes "function" baseSnapshot.Functions proposedSnapshot.Functions (fun item -> StableId.value item.Id) (fun item -> item.DisplayName) (fun item -> item.Exported)
        @ changes "effect" baseSnapshot.Effects proposedSnapshot.Effects (fun item -> StableId.value item.Id) (fun item -> item.DisplayName) (fun item -> item.Exported)
        @ changes "capability" baseSnapshot.Capabilities proposedSnapshot.Capabilities (fun item -> StableId.value item.Id) (fun item -> item.DisplayName) (fun item -> item.Exported)
        |> List.sortBy (fun item -> item.Kind + ":" + item.Id)

    /// The markers that differ between the base and the proposed snapshot, in `attribute:id` order.
    let private netAttributeChanges (baseSnapshot: Snapshot) (proposedSnapshot: Snapshot) =
        let entries (snapshot: Snapshot) =
            [ for capability in snapshot.Capabilities do
                  yield ("taskBound", StableId.value capability.Id), (capability.DisplayName, capability.TaskBound)
                  for op in capability.Operations do
                      yield ("mayWait", StableId.value op.Id), (capability.DisplayName + "." + op.DisplayName, op.MayWait)
              for fn in snapshot.Functions do
                  yield ("mustNotWait", StableId.value fn.Id), (fn.DisplayName, fn.MustNotWait) ]
        let previous = entries baseSnapshot |> Map.ofList
        entries proposedSnapshot
        |> List.choose (fun ((attribute, id), (displayName, value)) ->
            match Map.tryFind (attribute, id) previous with
            | Some(_, before) when before <> value -> Some { Attribute = attribute; Id = id; DisplayName = displayName; Value = value }
            | _ -> None)
        |> List.sortBy (fun item -> item.Attribute + ":" + item.Id)

    /// The rejection message when a rename takes a name another declaration already has in its
    /// scope. Display names resolve by-name requests; pre-existing duplicates are left alone.
    let private duplicateRename (proposed: Snapshot) (rename: SymbolRename): string option =
        let taken kind scope rule (name: string) (others: string list) =
            if List.contains name others then
                Some $"Another {kind} {scope}is already named '{name}'; {kind} display names must be unique{rule}."
            else None
        let fieldRule = " within their Product or Union variant"
        let typeOf id = proposed.Types |> List.tryFind (fun item -> item.Id = id)
        let functionOf id = proposed.Functions |> List.tryFind (fun item -> item.Id = id)
        let capabilityOf id = proposed.Capabilities |> List.tryFind (fun item -> item.Id = id)
        match rename with
        | TypeRenamed(id, _, name) ->
            proposed.Types |> List.filter (fun item -> item.Id <> id) |> List.map (fun item -> item.DisplayName)
            |> taken "type" "" "" name
        | FunctionRenamed(id, _, name) ->
            proposed.Functions |> List.filter (fun item -> item.Id <> id) |> List.map (fun item -> item.DisplayName)
            |> taken "function" "" "" name
        | EffectRenamed(id, _, name) ->
            proposed.Effects |> List.filter (fun item -> item.Id <> id) |> List.map (fun item -> item.DisplayName)
            |> taken "effect" "" "" name
        | CapabilityRenamed(id, _, name) ->
            proposed.Capabilities |> List.filter (fun item -> item.Id <> id) |> List.map (fun item -> item.DisplayName)
            |> taken "capability" "" "" name
        | FieldRenamed(typeId, variantId, id, _, name) ->
            let siblings (fields: SemanticField list) =
                fields |> List.filter (fun field -> field.Id <> id) |> List.map (fun field -> field.DisplayName)
            typeOf typeId
            |> Option.bind (fun owner ->
                match owner.Kind, variantId with
                | SemanticTypeKind.Product fields, _ ->
                    siblings fields |> taken "field" $"of Product '{owner.DisplayName}' " fieldRule name
                | SemanticTypeKind.Union variants, Some selected ->
                    variants
                    |> List.tryFind (fun variant -> variant.Id = selected)
                    |> Option.bind (fun variant ->
                        siblings variant.Fields
                        |> taken "field" $"of variant '{variant.DisplayName}' of Union '{owner.DisplayName}' " fieldRule name)
                | SemanticTypeKind.Union _, None -> None)
        | VariantRenamed(typeId, id, _, name) ->
            typeOf typeId
            |> Option.bind (fun owner ->
                match owner.Kind with
                | SemanticTypeKind.Union variants ->
                    variants |> List.filter (fun variant -> variant.Id <> id) |> List.map (fun variant -> variant.DisplayName)
                    |> taken "variant" $"of Union '{owner.DisplayName}' " " within their Union" name
                | SemanticTypeKind.Product _ -> None)
        | ParameterRenamed(functionId, id, _, name) ->
            functionOf functionId
            |> Option.bind (fun owner ->
                owner.Parameters |> List.filter (fun item -> item.Id <> id) |> List.map (fun item -> item.DisplayName)
                |> taken "parameter" $"of function '{owner.DisplayName}' " " within their function" name)
        | CapabilityOperationRenamed(capabilityId, id, _, name) ->
            capabilityOf capabilityId
            |> Option.bind (fun owner ->
                owner.Operations |> List.filter (fun item -> item.Id <> id) |> List.map (fun item -> item.DisplayName)
                |> taken "operation" $"of Capability '{owner.DisplayName}' " " within their Capability" name)
        | CapabilityParameterRenamed(capabilityId, operationId, id, _, name) ->
            capabilityOf capabilityId
            |> Option.bind (fun owner -> owner.Operations |> List.tryFind (fun item -> item.Id = operationId))
            |> Option.bind (fun operation ->
                operation.Parameters |> List.filter (fun item -> item.Id <> id) |> List.map (fun item -> item.DisplayName)
                |> taken "parameter" $"of operation '{operation.DisplayName}' " " within their operation" name)

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
            TypesRemoved = changes.TypesRemoved |> List.sortBy (fun item -> StableId.value item.TypeId)
            EffectsRemoved = changes.EffectsRemoved |> List.sortBy (fun item -> StableId.value item.EffectId)
            CapabilitiesRemoved = changes.CapabilitiesRemoved |> List.sortBy (fun item -> StableId.value item.CapabilityId)
            CapabilityOperationsRemoved = changes.CapabilityOperationsRemoved |> List.sortBy (fun item -> StableId.value item.OperationId)
            CapabilityOperationsAdded =
                changes.CapabilityOperationsAdded |> List.sortBy (fun item -> StableId.value item.OwnerCapabilityId, StableId.value item.Operation.Id)
            CapabilityImplementationsBound =
                changes.CapabilityImplementationsBound
                |> List.sortBy (fun item -> StableId.value item.OwnerFunctionId, StableId.value item.ConstructNodeId, StableId.value item.OperationId)
            ParametersRemoved =
                changes.ParametersRemoved
                |> List.sortBy (fun item -> StableId.value item.OwnerFunctionId, StableId.value item.RemovedParameterId)
            UnionVariantsRemoved =
                changes.UnionVariantsRemoved
                |> List.sortBy (fun item -> StableId.value item.OwnerTypeId, StableId.value item.RemovedVariantId)
            UnionVariantsAdded =
                changes.UnionVariantsAdded
                |> List.sortBy (fun item -> StableId.value item.OwnerTypeId, StableId.value item.Variant.Id)
            UnionMatchArmsAdded =
                changes.UnionMatchArmsAdded
                |> List.sortBy (fun item -> StableId.value item.OwnerFunctionId, StableId.value item.MatchNodeId, StableId.value item.VariantId)
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
                        | Ok current -> applyOperation request.Id request.Operations current operation)
                    (Ok(baseSnapshot, emptyChanges))

            match applied with
            | Error(code, message) -> reject code message
            | Ok(changed, accumulatedChanges) ->
                let proposed: Snapshot =
                    { changed with
                        Id = request.ProposedSnapshotId
                        ParentId = Some baseSnapshot.Id }

                let renames = netRenames baseSnapshot proposed
                // Display names resolve by-name requests, so a rename may not take another
                // declaration's name in its scope. Pre-existing duplicates are left alone.
                let duplicate = renames |> List.tryPick (duplicateRename proposed)

                match SnapshotValidator.validate proposed, duplicate with
                | issue :: _, _ -> reject "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [], Some message -> reject "DuplicateDisplayName" message
                | [], None ->
                    let changes = sortChanges accumulatedChanges
                    let diff: SemanticDiff =
                        { Renames = renames
                          VisibilityChanges = netVisibilityChanges baseSnapshot proposed
                          AttributeChanges = netAttributeChanges baseSnapshot proposed
                          ParametersAdded =
                              changes.ParametersAdded
                              |> List.filter (fun item ->
                                  proposed.Functions |> List.exists (fun fn -> fn.Id = item.OwnerFunctionId))
                          ProductFieldsAdded = changes.ProductFieldsAdded
                          ProductFieldsBound =
                              changes.ProductFieldsBound
                              |> List.filter (fun item ->
                                  proposed.Functions |> List.exists (fun fn -> fn.Id = item.OwnerFunctionId))
                          ProductFieldsRemoved = changes.ProductFieldsRemoved
                          FunctionsRemoved = changes.FunctionsRemoved
                          TypesRemoved = changes.TypesRemoved
                          EffectsRemoved = changes.EffectsRemoved
                          CapabilitiesRemoved = changes.CapabilitiesRemoved
                          CapabilityOperationsRemoved = changes.CapabilityOperationsRemoved
                          CapabilityOperationsAdded =
                              changes.CapabilityOperationsAdded
                              |> List.filter (fun item ->
                                  proposed.Capabilities |> List.exists (fun capability -> capability.Id = item.OwnerCapabilityId))
                          CapabilityImplementationsBound =
                              changes.CapabilityImplementationsBound
                              |> List.filter (fun item ->
                                  proposed.Functions |> List.exists (fun fn -> fn.Id = item.OwnerFunctionId))
                          ArgumentsAttached = changes.ArgumentsAttached
                          ObligationsCreated = changes.ObligationsCreated
                          ObligationsResolved = changes.ObligationsResolved
                          FunctionRequiredEffectsChanged = netRequiredEffectChanges baseSnapshot proposed changes
                          ParametersRemoved = changes.ParametersRemoved
                          UnionVariantsRemoved = changes.UnionVariantsRemoved
                          UnionVariantsAdded =
                              changes.UnionVariantsAdded
                              |> List.filter (fun item -> proposed.Types |> List.exists (fun item2 -> item2.Id = item.OwnerTypeId))
                          UnionMatchArmsAdded =
                              changes.UnionMatchArmsAdded
                              |> List.filter (fun item -> proposed.Functions |> List.exists (fun fn -> fn.Id = item.OwnerFunctionId)) }

                    let hasChanges =
                        not (List.isEmpty diff.Renames)
                        || not (List.isEmpty diff.VisibilityChanges)
                        || not (List.isEmpty diff.AttributeChanges)
                        || not (List.isEmpty diff.ParametersAdded)
                        || not (List.isEmpty diff.ProductFieldsAdded)
                        || not (List.isEmpty diff.ProductFieldsBound)
                        || not (List.isEmpty diff.ProductFieldsRemoved)
                        || not (List.isEmpty diff.FunctionsRemoved)
                        || not (List.isEmpty diff.TypesRemoved)
                        || not (List.isEmpty diff.EffectsRemoved)
                        || not (List.isEmpty diff.CapabilitiesRemoved)
                        || not (List.isEmpty diff.CapabilityOperationsRemoved)
                        || not (List.isEmpty diff.CapabilityOperationsAdded)
                        || not (List.isEmpty diff.CapabilityImplementationsBound)
                        || not (List.isEmpty diff.ArgumentsAttached)
                        || not (List.isEmpty diff.ObligationsCreated)
                        || not (List.isEmpty diff.ObligationsResolved)
                        || not (List.isEmpty diff.FunctionRequiredEffectsChanged)
                        || not (List.isEmpty diff.ParametersRemoved)
                        || not (List.isEmpty diff.UnionVariantsRemoved)
                        || not (List.isEmpty diff.UnionVariantsAdded)
                        || not (List.isEmpty diff.UnionMatchArmsAdded)

                    if not hasChanges then
                        reject "NoChange" "The transaction has no net semantic change."
                    else
                        Proposed
                            { TransactionId = request.Id
                              BaseSnapshotId = request.BaseSnapshotId
                              ProposedSnapshot = proposed
                              Diff = diff }
