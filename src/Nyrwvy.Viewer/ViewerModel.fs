namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type ViewerTarget =
    | Node of GraphNodeId
    | Function of FunctionId
    | Parameter of ParameterId
    | PatternBinding of PatternBindingId
    | CapabilityOperation of CapabilityOperationId
    | Type of TypeId
    | Field of FieldId
    | Variant of VariantId

[<RequireQualifiedAccess>]
type ViewerRelationKind =
    | DependsOn
    | CallsFunction
    | ReferencesFunction
    | ReadsParameter
    | ReadsPatternBinding
    | InvokesCapabilityOperation
    | ConstructsType
    | AccessesField
    | ConstructsVariant
    | BranchCondition
    | MatchInput
    | BindInput

type ViewerRelation =
    { Kind: ViewerRelationKind
      Target: ViewerTarget }

/// What a node is, as the viewer shows it. A node's operands are already shown through its value
/// dependencies, so only the kinds whose display needs more than their name (literals and their
/// payload, the task-wait mode, a capability construction's implementations) have a case of their
/// own; every other kind is `Operation` named by its semantic kind (`SemanticNodeKind.name`, e.g.
/// "ListContains"), so adding a builtin needs no viewer change.
[<RequireQualifiedAccess>]
type ViewerNodeRole =
    | StringLiteral of string
    | BoolLiteral of bool
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | TaskWait of mode: TaskWaitMode
    | CapabilityConstruct of CapabilityId * (CapabilityOperationId * FunctionId) list
    | Operation of kind: string

[<RequireQualifiedAccess>]
type ViewerBranchRole =
    | WhenTrue
    | WhenFalse
    | UnionVariant of VariantId list
    | BindBody
    | OptionSome
    | OptionNone
    | ResultOk
    | ResultErr
    /// The right operand of BoolAnd (evaluated only when left is true) or BoolOr (only when left is false).
    | RightOperand

type ViewerPatternBinding =
    { Id: PatternBindingId
      DisplayName: string option
      Type: TypeRef
      SourceFieldId: FieldId option }

type ViewerBranch =
    { Role: ViewerBranchRole
      Bindings: ViewerPatternBinding list
      Graph: ViewerGraph }

and ViewerNode =
    { Id: GraphNodeId
      Type: TypeRef option
      Role: ViewerNodeRole
      Relations: ViewerRelation list
      ValueDependencies: ValueDependency list
      Branches: ViewerBranch list }

and ViewerGraph =
    { Nodes: ViewerNode list
      ResultNodeId: GraphNodeId }

type ViewerFunctionSignature =
    { FunctionId: FunctionId
      DisplayName: string
      /// Whether snapshots that depend on this one may use the function.
      Exported: bool
      TypeParameters: (TypeParameterId * string) list
      Parameters: SemanticParameter list
      ResultType: TypeRef
      RequiredEffects: EffectId list }

type ViewerFocusModel =
    { SnapshotId: SnapshotId
      Function: ViewerFunctionSignature
      BodyEffectRequirements: BodyEffectRequirements
      Aborts: QueryFunctionAbort list
      Graph: ViewerGraph
      Callers: QueryCallSite list
      Callees: QueryCallSite list
      RelatedFunctionSignatures: QueryFunctionSignature list
      OpenObligations: QueryOpenObligation list
      ReferencedTypes: SemanticType list
      ReferencedCapabilities: SemanticCapability list
      ReferencedEffects: SemanticEffect list
      MissingFunctionIds: FunctionId list
      MissingTypeIds: TypeId list
      MissingCapabilityIds: CapabilityId list
      MissingEffectIds: EffectId list }

type ViewerChangeReviewModel =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      Diff: SemanticDiff
      Readiness: ChangeReviewReadiness
      AffectedFunctionIds: FunctionId list
      AffectedTypeIds: TypeId list
      FunctionFocus: ViewerFocusModel list }

/// One function a pending proposal creates or rewrites: its committed focus (absent for a
/// created function, or when the proposal's base is no longer the committed state) and its
/// focus in the proposed snapshot.
type ViewerProposalFunction =
    { CommittedVersion: ViewerFocusModel option
      ProposedVersion: ViewerFocusModel }

/// Change Review of a pending structural proposal (replacement, creation, migration,
/// declaration). The engine's review is kept verbatim as `EngineReview`, the JSON the
/// authoring session returned to the agent; the page adds before/after focus beside it and
/// reconstructs nothing from either.
type ViewerProposalReviewModel =
    { TransactionId: TransactionId
      ProposalKind: string
      BaseSnapshotId: SnapshotId
      ProposedSnapshotId: SnapshotId
      BaseIsCommitted: bool
      Readiness: TransactionReadiness
      ChangedFunctions: ViewerProposalFunction list
      DeclaredTypes: SemanticType list
      DeclaredEffects: SemanticEffect list
      DeclaredCapabilities: SemanticCapability list
      EngineReview: string }

[<RequireQualifiedAccess>]
module ViewerProjection =
    let private role (node: SemanticNode): ViewerNodeRole =
        match node.Kind with
        | SemanticNodeKind.StringLiteral value -> ViewerNodeRole.StringLiteral value
        | SemanticNodeKind.BoolLiteral value -> ViewerNodeRole.BoolLiteral value
        | SemanticNodeKind.Int64Literal value -> ViewerNodeRole.Int64Literal value
        | SemanticNodeKind.Float64Literal bits -> ViewerNodeRole.Float64Literal bits
        | SemanticNodeKind.TaskWait(_, mode, _) -> ViewerNodeRole.TaskWait mode
        | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
            ViewerNodeRole.CapabilityConstruct(capabilityId, List.sortBy fst implementations)
        | kind -> ViewerNodeRole.Operation(SemanticNodeKind.name kind)

    /// The declarations and control inputs a node names beyond its value dependencies and the
    /// functions `SemanticNodeShape.functionReferences` lists.
    let private semanticRelations (node: SemanticNode): ViewerRelation list =
        let relation kind target = [ { Kind = kind; Target = target } ]
        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId ->
            relation ViewerRelationKind.ReadsParameter (ViewerTarget.Parameter parameterId)
        | SemanticNodeKind.PatternReference bindingId ->
            relation ViewerRelationKind.ReadsPatternBinding (ViewerTarget.PatternBinding bindingId)
        | SemanticNodeKind.CapabilityInvoke(_, operationId, _) ->
            relation ViewerRelationKind.InvokesCapabilityOperation (ViewerTarget.CapabilityOperation operationId)
        | SemanticNodeKind.ProductConstruct(typeId, _, _)
        | SemanticNodeKind.ProductUpdate(typeId, _, _) ->
            relation ViewerRelationKind.ConstructsType (ViewerTarget.Type typeId)
        | SemanticNodeKind.FieldAccess(_, fieldId) ->
            relation ViewerRelationKind.AccessesField (ViewerTarget.Field fieldId)
        | SemanticNodeKind.UnionConstruct(variantId, _, _) ->
            relation ViewerRelationKind.ConstructsVariant (ViewerTarget.Variant variantId)
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) ->
            relation ViewerRelationKind.BranchCondition (ViewerTarget.Node conditionNodeId)
        | SemanticNodeKind.MatchUnion(inputNodeId, _)
        | SemanticNodeKind.MatchOption(inputNodeId, _, _)
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) ->
            relation ViewerRelationKind.MatchInput (ViewerTarget.Node inputNodeId)
        | SemanticNodeKind.Bind(inputNodeId, _) ->
            relation ViewerRelationKind.BindInput (ViewerTarget.Node inputNodeId)
        | _ -> []

    let private patternBinding sourceFieldId (binding: PatternBinding): ViewerPatternBinding =
        { Id = binding.Id
          DisplayName = binding.DisplayName
          Type = binding.Type
          SourceFieldId = sourceFieldId }

    let rec private projectGraph
        (nodeTypes: Map<GraphNodeId, TypeRef option>)
        (graph: ValueGraph)
        : ViewerGraph =
        let projectBranch role bindings body =
            { Role = role
              Bindings = bindings
              Graph = projectGraph nodeTypes body }

        let projectNode (node: SemanticNode): ViewerNode =
            let role = role node
            let semanticRelations = semanticRelations node
            // SemanticNodeShape is the one source of the functions a node names.
            let functionRelations =
                SemanticNodeShape.functionReferences node
                |> List.map (fun reference ->
                    { Kind =
                        if FunctionReferenceRole.isInvoked reference.Role then
                            ViewerRelationKind.CallsFunction
                        else
                            ViewerRelationKind.ReferencesFunction
                      Target = ViewerTarget.Function reference.FunctionId })
                |> List.distinct
            let valueDependencies = ValueDependency.ofNode node
            let dependencyRelations =
                valueDependencies
                |> List.map (fun dependency ->
                    { Kind = ViewerRelationKind.DependsOn
                      Target = ViewerTarget.Node dependency.NodeId })

            let branchRole (role: NestedGraphRole) =
                match role with
                | NestedGraphRole.WhenTrue -> ViewerBranchRole.WhenTrue
                | NestedGraphRole.WhenFalse -> ViewerBranchRole.WhenFalse
                | NestedGraphRole.BoolAndRight
                | NestedGraphRole.BoolOrRight -> ViewerBranchRole.RightOperand
                | NestedGraphRole.UnionArm variantIds -> ViewerBranchRole.UnionVariant(variantIds |> List.sortBy StableId.value)
                | NestedGraphRole.BindBody -> ViewerBranchRole.BindBody
                | NestedGraphRole.OptionSome -> ViewerBranchRole.OptionSome
                | NestedGraphRole.OptionNone -> ViewerBranchRole.OptionNone
                | NestedGraphRole.ResultOk -> ViewerBranchRole.ResultOk
                | NestedGraphRole.ResultErr -> ViewerBranchRole.ResultErr

            // Union arms and their field bindings are shown in stable ID order; the other
            // scopes keep evaluation order.
            let branches =
                SemanticNodeShape.nestedScopes node
                |> List.sortBy (fun scope ->
                    match scope.Role with
                    | NestedGraphRole.UnionArm variantIds -> Some(variantIds |> List.map StableId.value |> List.min)
                    | _ -> None)
                |> List.map (fun scope ->
                    let bindings =
                        scope.Bindings
                        |> List.sortBy (fun binding ->
                            match binding with
                            | NestedBinding.Field(fieldId, _) -> Some(StableId.value fieldId)
                            | _ -> None)
                        |> List.map (fun binding ->
                            match binding with
                            | NestedBinding.Field(fieldId, pattern) -> patternBinding (Some fieldId) pattern
                            | NestedBinding.Value pattern
                            | NestedBinding.Error pattern -> patternBinding None pattern)
                    projectBranch (branchRole scope.Role) bindings scope.Graph)

            { Id = node.Id
              Type = Map.tryFind node.Id nodeTypes |> Option.defaultValue None
              Role = role
              Relations = semanticRelations @ functionRelations @ dependencyRelations
              ValueDependencies = valueDependencies
              Branches = branches }

        { Nodes =
            graph.Nodes
            |> List.map projectNode
            |> List.sortBy (fun node -> StableId.value node.Id)
          ResultNodeId = graph.ResultNodeId }

    let focus (source: FunctionFocusView): ViewerFocusModel =
        let nodeTypes =
            source.NodeTypes
            |> List.map (fun item -> item.NodeId, item.Type)
            |> Map.ofList

        { SnapshotId = source.SnapshotId
          Function =
            { FunctionId = source.Function.Id
              DisplayName = source.Function.DisplayName
              Exported = source.Function.Exported
              TypeParameters = source.Function.TypeParameters
              Parameters = source.Function.Parameters
              ResultType = source.Function.ResultType
              RequiredEffects = source.Function.RequiredEffects }
          Graph = projectGraph nodeTypes source.Function.Body
          BodyEffectRequirements = source.BodyEffectRequirements
          Aborts = source.Aborts
          Callers = source.Callers
          Callees = source.Callees
          RelatedFunctionSignatures = source.RelatedFunctionSignatures
          OpenObligations = source.OpenObligations
          ReferencedTypes = source.ReferencedTypes
          ReferencedCapabilities = source.ReferencedCapabilities
          ReferencedEffects = source.ReferencedEffects
          MissingFunctionIds = source.MissingFunctionIds
          MissingTypeIds = source.MissingTypeIds
          MissingCapabilityIds = source.MissingCapabilityIds
          MissingEffectIds = source.MissingEffectIds }

    let changeReview (source: ChangeReviewView): ViewerChangeReviewModel =
        { TransactionId = source.TransactionId
          BaseSnapshotId = source.BaseSnapshotId
          ProposedSnapshotId = source.ProposedSnapshotId
          Diff = source.Diff
          Readiness = source.Readiness
          AffectedFunctionIds = source.AffectedFunctionIds
          AffectedTypeIds = source.AffectedTypeIds
          FunctionFocus = source.FunctionFocus |> List.map focus }
