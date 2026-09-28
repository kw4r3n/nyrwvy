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

[<RequireQualifiedAccess>]
type ViewerNodeRole =
    | ParameterReference
    | PatternReference
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty
    | Int64ToString
    | StringParseInt64
    | StringLength
    | StringSlice
    | StringContains
    | StringEqual
    | ValueEqual
    | StringConcat
    | Int64WrappingAdd
    | Int64WrappingSubtract
    | Int64WrappingMultiply
    | Int64FloorDivide
    | Int64FloorModulo
    | Int64Equal
    | Int64LessThan
    | Int64LessThanOrEqual
    | Int64GreaterThan
    | Int64GreaterThanOrEqual
    | Float64Add
    | Float64Subtract
    | Float64Multiply
    | Float64Divide
    | Float64Equal
    | Float64LessThan
    | Float64IsNaN
    | Float64IsFinite
    | Float64IsInfinite
    | ListEmpty
    | ListConstruct
    | ListLength
    | ListAppendItem
    | ListItemAt
    | ListConcat
    | ListFold
    | ListEffectFold
    | UseResource
    | ParallelAll
    | CancellationRequested
    | TaskScope
    | TaskStart
    | TaskWait of mode: TaskWaitMode
    | TaskCancel
    | ListMap
    | ListFilter
    | ListAny
    | ListAll
    | CapabilityConstruct of CapabilityId * (CapabilityOperationId * FunctionId) list
    | FunctionReference
    | FunctionInvoke
    | DirectCall
    | CapabilityInvoke
    | ProductConstruct
    | FieldAccess
    | UnionConstruct
    | OptionNone
    | OptionSome
    | ResultOk
    | Abort
    | ResultErr
    | BoolNot
    | BoolAnd
    | BoolOr
    | Conditional
    | MatchUnion
    | Bind
    | MatchOption
    | MatchResult

[<RequireQualifiedAccess>]
type ViewerBranchRole =
    | WhenTrue
    | WhenFalse
    | UnionVariant of VariantId
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

[<RequireQualifiedAccess>]
module ViewerProjection =
    let private callbackRelation functionId =
        [ { Kind = ViewerRelationKind.CallsFunction
            Target = ViewerTarget.Function functionId } ]

    let private roleAndSemanticRelations (node: SemanticNode): ViewerNodeRole * ViewerRelation list =
        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId ->
            ViewerNodeRole.ParameterReference,
            [ { Kind = ViewerRelationKind.ReadsParameter
                Target = ViewerTarget.Parameter parameterId } ]
        | SemanticNodeKind.PatternReference bindingId ->
            ViewerNodeRole.PatternReference,
            [ { Kind = ViewerRelationKind.ReadsPatternBinding
                Target = ViewerTarget.PatternBinding bindingId } ]
        | SemanticNodeKind.StringLiteral value -> ViewerNodeRole.StringLiteral value, []
        | SemanticNodeKind.BoolLiteral value -> ViewerNodeRole.BoolLiteral value, []
        | SemanticNodeKind.UnitLiteral -> ViewerNodeRole.UnitLiteral, []
        | SemanticNodeKind.Int64Literal value -> ViewerNodeRole.Int64Literal value, []
        | SemanticNodeKind.Float64Literal bits -> ViewerNodeRole.Float64Literal bits, []
        | SemanticNodeKind.StringIsEmpty _ -> ViewerNodeRole.StringIsEmpty, []
        | SemanticNodeKind.Int64ToString _ -> ViewerNodeRole.Int64ToString, []
        | SemanticNodeKind.StringParseInt64 _ -> ViewerNodeRole.StringParseInt64, []
        | SemanticNodeKind.StringLength _ -> ViewerNodeRole.StringLength, []
        | SemanticNodeKind.StringSlice _ -> ViewerNodeRole.StringSlice, []
        | SemanticNodeKind.StringContains _ -> ViewerNodeRole.StringContains, []
        | SemanticNodeKind.StringEqual _ -> ViewerNodeRole.StringEqual, []
        | SemanticNodeKind.ValueEqual _ -> ViewerNodeRole.ValueEqual, []
        | SemanticNodeKind.StringConcat _ -> ViewerNodeRole.StringConcat, []
        | SemanticNodeKind.Int64WrappingAdd _ -> ViewerNodeRole.Int64WrappingAdd, []
        | SemanticNodeKind.Int64WrappingSubtract _ -> ViewerNodeRole.Int64WrappingSubtract, []
        | SemanticNodeKind.Int64WrappingMultiply _ -> ViewerNodeRole.Int64WrappingMultiply, []
        | SemanticNodeKind.Int64FloorDivide _ -> ViewerNodeRole.Int64FloorDivide, []
        | SemanticNodeKind.Int64FloorModulo _ -> ViewerNodeRole.Int64FloorModulo, []
        | SemanticNodeKind.Int64Equal _ -> ViewerNodeRole.Int64Equal, []
        | SemanticNodeKind.Int64LessThan _ -> ViewerNodeRole.Int64LessThan, []
        | SemanticNodeKind.Int64LessThanOrEqual _ -> ViewerNodeRole.Int64LessThanOrEqual, []
        | SemanticNodeKind.Int64GreaterThan _ -> ViewerNodeRole.Int64GreaterThan, []
        | SemanticNodeKind.Int64GreaterThanOrEqual _ -> ViewerNodeRole.Int64GreaterThanOrEqual, []
        | SemanticNodeKind.Float64Add _ -> ViewerNodeRole.Float64Add, []
        | SemanticNodeKind.Float64Subtract _ -> ViewerNodeRole.Float64Subtract, []
        | SemanticNodeKind.Float64Multiply _ -> ViewerNodeRole.Float64Multiply, []
        | SemanticNodeKind.Float64Divide _ -> ViewerNodeRole.Float64Divide, []
        | SemanticNodeKind.Float64Equal _ -> ViewerNodeRole.Float64Equal, []
        | SemanticNodeKind.Float64LessThan _ -> ViewerNodeRole.Float64LessThan, []
        | SemanticNodeKind.Float64IsNaN _ -> ViewerNodeRole.Float64IsNaN, []
        | SemanticNodeKind.Float64IsFinite _ -> ViewerNodeRole.Float64IsFinite, []
        | SemanticNodeKind.Float64IsInfinite _ -> ViewerNodeRole.Float64IsInfinite, []
        | SemanticNodeKind.ListEmpty _ -> ViewerNodeRole.ListEmpty, []
        | SemanticNodeKind.ListConstruct _ -> ViewerNodeRole.ListConstruct, []
        | SemanticNodeKind.ListLength _ -> ViewerNodeRole.ListLength, []
        | SemanticNodeKind.ListAppendItem _ -> ViewerNodeRole.ListAppendItem, []
        | SemanticNodeKind.ListItemAt _ -> ViewerNodeRole.ListItemAt, []
        | SemanticNodeKind.ListConcat _ -> ViewerNodeRole.ListConcat, []
        | SemanticNodeKind.ListFold(_, _, folderFunctionId, _) ->
            ViewerNodeRole.ListFold, callbackRelation folderFunctionId
        | SemanticNodeKind.ListEffectFold(_, _, folderFunctionId, _) ->
            ViewerNodeRole.ListEffectFold, callbackRelation folderFunctionId
        | SemanticNodeKind.UseResource(_, _, _, bodyFunctionId, _) ->
            ViewerNodeRole.UseResource, callbackRelation bodyFunctionId
        | SemanticNodeKind.ParallelAll(_, workerFunctionId, _) ->
            ViewerNodeRole.ParallelAll, callbackRelation workerFunctionId
        | SemanticNodeKind.CancellationRequested -> ViewerNodeRole.CancellationRequested, []
        | SemanticNodeKind.TaskScope(bodyFunctionId, _) -> ViewerNodeRole.TaskScope, callbackRelation bodyFunctionId
        | SemanticNodeKind.TaskStart(_, workerFunctionId, _, _) -> ViewerNodeRole.TaskStart, callbackRelation workerFunctionId
        | SemanticNodeKind.TaskWait(_, mode, _) -> ViewerNodeRole.TaskWait mode, []
        | SemanticNodeKind.TaskCancel _ -> ViewerNodeRole.TaskCancel, []
        | SemanticNodeKind.ListMap(_, mapperFunctionId, _) ->
            ViewerNodeRole.ListMap, callbackRelation mapperFunctionId
        | SemanticNodeKind.ListFilter(_, predicateFunctionId, _) ->
            ViewerNodeRole.ListFilter, callbackRelation predicateFunctionId
        | SemanticNodeKind.ListAny(_, predicateFunctionId, _) ->
            ViewerNodeRole.ListAny, callbackRelation predicateFunctionId
        | SemanticNodeKind.ListAll(_, predicateFunctionId, _) ->
            ViewerNodeRole.ListAll, callbackRelation predicateFunctionId
        | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
            ViewerNodeRole.CapabilityConstruct(capabilityId, List.sortBy fst implementations),
            (implementations |> List.map snd |> List.distinct |> List.map (fun functionId ->
                { Kind = ViewerRelationKind.ReferencesFunction; Target = ViewerTarget.Function functionId }))
        | SemanticNodeKind.FunctionReference functionId ->
            ViewerNodeRole.FunctionReference,
            [ { Kind = ViewerRelationKind.ReferencesFunction
                Target = ViewerTarget.Function functionId } ]
        | SemanticNodeKind.FunctionInvoke _ -> ViewerNodeRole.FunctionInvoke, []
        | SemanticNodeKind.DirectCall(functionId, _) ->
            ViewerNodeRole.DirectCall,
            [ { Kind = ViewerRelationKind.CallsFunction
                Target = ViewerTarget.Function functionId } ]
        | SemanticNodeKind.CapabilityInvoke(_, operationId, _) ->
            ViewerNodeRole.CapabilityInvoke,
            [ { Kind = ViewerRelationKind.InvokesCapabilityOperation
                Target = ViewerTarget.CapabilityOperation operationId } ]
        | SemanticNodeKind.ProductConstruct(typeId, _) ->
            ViewerNodeRole.ProductConstruct,
            [ { Kind = ViewerRelationKind.ConstructsType
                Target = ViewerTarget.Type typeId } ]
        | SemanticNodeKind.FieldAccess(_, fieldId) ->
            ViewerNodeRole.FieldAccess,
            [ { Kind = ViewerRelationKind.AccessesField
                Target = ViewerTarget.Field fieldId } ]
        | SemanticNodeKind.UnionConstruct(variantId, _) ->
            ViewerNodeRole.UnionConstruct,
            [ { Kind = ViewerRelationKind.ConstructsVariant
                Target = ViewerTarget.Variant variantId } ]
        | SemanticNodeKind.OptionNone _ -> ViewerNodeRole.OptionNone, []
        | SemanticNodeKind.OptionSome _ -> ViewerNodeRole.OptionSome, []
        | SemanticNodeKind.ResultOk _ -> ViewerNodeRole.ResultOk, []
        | SemanticNodeKind.Abort _ -> ViewerNodeRole.Abort, []
        | SemanticNodeKind.ResultErr _ -> ViewerNodeRole.ResultErr, []
        | SemanticNodeKind.BoolNot _ -> ViewerNodeRole.BoolNot, []
        | SemanticNodeKind.BoolAnd _ -> ViewerNodeRole.BoolAnd, []
        | SemanticNodeKind.BoolOr _ -> ViewerNodeRole.BoolOr, []
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) ->
            ViewerNodeRole.Conditional,
            [ { Kind = ViewerRelationKind.BranchCondition
                Target = ViewerTarget.Node conditionNodeId } ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _) ->
            ViewerNodeRole.MatchUnion,
            [ { Kind = ViewerRelationKind.MatchInput
                Target = ViewerTarget.Node inputNodeId } ]
        | SemanticNodeKind.Bind(inputNodeId, _) ->
            ViewerNodeRole.Bind,
            [ { Kind = ViewerRelationKind.BindInput
                Target = ViewerTarget.Node inputNodeId } ]
        | SemanticNodeKind.MatchOption(inputNodeId, _, _) ->
            ViewerNodeRole.MatchOption,
            [ { Kind = ViewerRelationKind.MatchInput
                Target = ViewerTarget.Node inputNodeId } ]
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) ->
            ViewerNodeRole.MatchResult,
            [ { Kind = ViewerRelationKind.MatchInput
                Target = ViewerTarget.Node inputNodeId } ]

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
            let role, semanticRelations = roleAndSemanticRelations node
            let valueDependencies = ValueDependency.ofNode node
            let dependencyRelations =
                valueDependencies
                |> List.map (fun dependency ->
                    { Kind = ViewerRelationKind.DependsOn
                      Target = ViewerTarget.Node dependency.NodeId })

            let branches =
                match node.Kind with
                | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                    [ projectBranch ViewerBranchRole.WhenTrue [] whenTrue
                      projectBranch ViewerBranchRole.WhenFalse [] whenFalse ]
                | SemanticNodeKind.BoolAnd(_, right)
                | SemanticNodeKind.BoolOr(_, right) -> [ projectBranch ViewerBranchRole.RightOperand [] right ]
                | SemanticNodeKind.MatchUnion(_, arms) ->
                    arms
                    |> List.sortBy (fun arm -> StableId.value arm.VariantId)
                    |> List.map (fun arm ->
                        let bindings =
                            arm.FieldBindings
                            |> List.sortBy (fun item -> StableId.value item.SourceFieldId)
                            |> List.map (fun item -> patternBinding (Some item.SourceFieldId) item.Binding)
                        projectBranch (ViewerBranchRole.UnionVariant arm.VariantId) bindings arm.Body)
                | SemanticNodeKind.Bind(_, body) ->
                    [ projectBranch ViewerBranchRole.BindBody [ patternBinding None body.ValueBinding ] body.Body ]
                | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                    [ projectBranch
                        ViewerBranchRole.OptionSome
                        [ patternBinding None someArm.ValueBinding ]
                        someArm.Body
                      projectBranch ViewerBranchRole.OptionNone [] noneBody ]
                | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                    [ projectBranch
                        ViewerBranchRole.ResultOk
                        [ patternBinding None okArm.ValueBinding ]
                        okArm.Body
                      projectBranch
                        ViewerBranchRole.ResultErr
                        [ patternBinding None errArm.ErrorBinding ]
                        errArm.Body ]
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.Int64Literal _
                | SemanticNodeKind.Float64Literal _
                | SemanticNodeKind.StringIsEmpty _
                | SemanticNodeKind.Int64ToString _
                | SemanticNodeKind.StringParseInt64 _
                | SemanticNodeKind.StringLength _
                | SemanticNodeKind.StringSlice _
                | SemanticNodeKind.BoolNot _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.StringEqual _
                | SemanticNodeKind.ValueEqual _
                | SemanticNodeKind.StringConcat _
                | SemanticNodeKind.Int64WrappingAdd _
                | SemanticNodeKind.Int64WrappingSubtract _
                | SemanticNodeKind.Int64WrappingMultiply _
                | SemanticNodeKind.Int64FloorDivide _
                | SemanticNodeKind.Int64FloorModulo _
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
                | SemanticNodeKind.ListItemAt _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListFold _
                | SemanticNodeKind.ListEffectFold _
                | SemanticNodeKind.UseResource _
                | SemanticNodeKind.ParallelAll _
                | SemanticNodeKind.CancellationRequested
                | SemanticNodeKind.TaskScope _
                | SemanticNodeKind.TaskStart _
                | SemanticNodeKind.TaskWait _
                | SemanticNodeKind.TaskCancel _
                | SemanticNodeKind.ListMap _
                | SemanticNodeKind.ListFilter _
                | SemanticNodeKind.ListAny _
                | SemanticNodeKind.ListAll _
                | SemanticNodeKind.CapabilityConstruct _
                | SemanticNodeKind.FunctionReference _
                | SemanticNodeKind.FunctionInvoke _
                | SemanticNodeKind.DirectCall _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.Abort _
                | SemanticNodeKind.ResultErr _ -> []

            { Id = node.Id
              Type = Map.tryFind node.Id nodeTypes |> Option.defaultValue None
              Role = role
              Relations = semanticRelations @ dependencyRelations
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
