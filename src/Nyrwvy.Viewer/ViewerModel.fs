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
    | Int64ToHexString
    | StringParseInt64
    | StringParseInt64Hex
    | Float64ToString
    | StringParseFloat64
    | StringCodePoints
    | StringToLower
    | StringToUpper
    | StringDecodeUtf8Lossy
    | Float64RoundToInt64
    | Float64FloorToInt64
    | Float64Sqrt
    | Float64Abs
    | Float64Negate
    | Float64Floor
    | Float64Ceiling
    | Float64Truncate
    | StringUtf8ByteCount
    | StringFromCodePoint
    | StringToCodePoint
    | StringLength
    | StringSlice
    | StringContains
    | StringIndexOf
    | StringSpan
    | StringSpanBack
    | StringLastIndexOf
    | StringStartsWith
    | StringEndsWith
    | StringSplit
    | StringSplitLines
    | StringJoin
    | StringEqual
    | StringLessThan
    | ValueEqual
    | StringConcat
    | Int64WrappingAdd
    | Int64WrappingSubtract
    | Int64WrappingMultiply
    | Int64FloorDivide
    | Int64FloorModulo
    | Int64Min
    | Int64Max
    | Int64CheckedAdd
    | Int64CheckedSubtract
    | Int64CheckedMultiply
    | ListRange
    | StringRepeat
    | Float64Round
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
    | Int64ToFloat64
    | Int64WrappingAbs
    | Bits32FromInt64
    | Bits32ToInt64
    | Bits32Xor
    | Bits32And
    | Bits32Or
    | Bits32Not
    | Bits32Add
    | Bits32RotateRight
    | Bits32ShiftRight
    | Bits32ShiftLeft
    | ListEmpty
    | SetEmpty
    | MapEmpty
    | ListConstruct
    | ListLength
    | MapValues
    | MapKeys
    | SetToList
    | MapSize
    | SetFromList
    | SetSize
    | ListReverse
    | ListLast
    | ListDropLast
    | ListAppendItem
    | ListItemAt
    | MapRemove
    | SetInsert
    | SetRemove
    | SetUnion
    | SetIntersection
    | SetDifference
    | MapContainsKey
    | SetContains
    | ListIndexOf
    | ListLastIndexOf
    | MapGet
    | MapGetOrDefault
    | ListSlice
    | MapInsert
    | ListConcat
    | ListFold
    | MapFold
    | ListEffectFold
    | UseResource
    | ParallelAll
    | CancellationRequested
    | TaskScope
    | TaskStart
    | TaskWait of mode: TaskWaitMode
    | TaskCancel
    | ListMap
    | MapFromList
    | ListFilter
    | MapFilter
    | ListSortBy
    | ListMinBy
    | MapMapValues
    | ListAny
    | ListFindIndex
    | ListAll
    | CapabilityConstruct of CapabilityId * (CapabilityOperationId * FunctionId) list
    | FunctionReference
    | FunctionInvoke
    | DirectCall
    | CapabilityInvoke
    | ProductConstruct
    | FieldAccess
    | ProductUpdate
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
        | SemanticNodeKind.Int64ToHexString _ -> ViewerNodeRole.Int64ToHexString, []
        | SemanticNodeKind.StringParseInt64 _ -> ViewerNodeRole.StringParseInt64, []
        | SemanticNodeKind.StringParseInt64Hex _ -> ViewerNodeRole.StringParseInt64Hex, []
        | SemanticNodeKind.Float64ToString _ -> ViewerNodeRole.Float64ToString, []
        | SemanticNodeKind.StringParseFloat64 _ -> ViewerNodeRole.StringParseFloat64, []
        | SemanticNodeKind.StringCodePoints _ -> ViewerNodeRole.StringCodePoints, []
        | SemanticNodeKind.StringToLower _ -> ViewerNodeRole.StringToLower, []
        | SemanticNodeKind.StringToUpper _ -> ViewerNodeRole.StringToUpper, []
        | SemanticNodeKind.StringDecodeUtf8Lossy _ -> ViewerNodeRole.StringDecodeUtf8Lossy, []
        | SemanticNodeKind.Float64RoundToInt64 _ -> ViewerNodeRole.Float64RoundToInt64, []
        | SemanticNodeKind.Float64FloorToInt64 _ -> ViewerNodeRole.Float64FloorToInt64, []
        | SemanticNodeKind.Float64Sqrt _ -> ViewerNodeRole.Float64Sqrt, []
        | SemanticNodeKind.Float64Abs _ -> ViewerNodeRole.Float64Abs, []
        | SemanticNodeKind.Float64Negate _ -> ViewerNodeRole.Float64Negate, []
        | SemanticNodeKind.Float64Floor _ -> ViewerNodeRole.Float64Floor, []
        | SemanticNodeKind.Float64Ceiling _ -> ViewerNodeRole.Float64Ceiling, []
        | SemanticNodeKind.Float64Truncate _ -> ViewerNodeRole.Float64Truncate, []
        | SemanticNodeKind.StringUtf8ByteCount _ -> ViewerNodeRole.StringUtf8ByteCount, []
        | SemanticNodeKind.StringFromCodePoint _ -> ViewerNodeRole.StringFromCodePoint, []
        | SemanticNodeKind.StringToCodePoint _ -> ViewerNodeRole.StringToCodePoint, []
        | SemanticNodeKind.StringLength _ -> ViewerNodeRole.StringLength, []
        | SemanticNodeKind.StringSlice _ -> ViewerNodeRole.StringSlice, []
        | SemanticNodeKind.StringContains _ -> ViewerNodeRole.StringContains, []
        | SemanticNodeKind.StringIndexOf _ -> ViewerNodeRole.StringIndexOf, []
        | SemanticNodeKind.StringSpan _ -> ViewerNodeRole.StringSpan, []
        | SemanticNodeKind.StringSpanBack _ -> ViewerNodeRole.StringSpanBack, []
        | SemanticNodeKind.StringLastIndexOf _ -> ViewerNodeRole.StringLastIndexOf, []
        | SemanticNodeKind.StringStartsWith _ -> ViewerNodeRole.StringStartsWith, []
        | SemanticNodeKind.StringEndsWith _ -> ViewerNodeRole.StringEndsWith, []
        | SemanticNodeKind.StringSplit _ -> ViewerNodeRole.StringSplit, []
        | SemanticNodeKind.StringSplitLines _ -> ViewerNodeRole.StringSplitLines, []
        | SemanticNodeKind.StringJoin _ -> ViewerNodeRole.StringJoin, []
        | SemanticNodeKind.StringEqual _ -> ViewerNodeRole.StringEqual, []
        | SemanticNodeKind.StringLessThan _ -> ViewerNodeRole.StringLessThan, []
        | SemanticNodeKind.ValueEqual _ -> ViewerNodeRole.ValueEqual, []
        | SemanticNodeKind.StringConcat _ -> ViewerNodeRole.StringConcat, []
        | SemanticNodeKind.Int64WrappingAdd _ -> ViewerNodeRole.Int64WrappingAdd, []
        | SemanticNodeKind.Int64WrappingSubtract _ -> ViewerNodeRole.Int64WrappingSubtract, []
        | SemanticNodeKind.Int64WrappingMultiply _ -> ViewerNodeRole.Int64WrappingMultiply, []
        | SemanticNodeKind.Int64FloorDivide _ -> ViewerNodeRole.Int64FloorDivide, []
        | SemanticNodeKind.Int64FloorModulo _ -> ViewerNodeRole.Int64FloorModulo, []
        | SemanticNodeKind.Int64Min _ -> ViewerNodeRole.Int64Min, []
        | SemanticNodeKind.Int64Max _ -> ViewerNodeRole.Int64Max, []
        | SemanticNodeKind.Int64CheckedAdd _ -> ViewerNodeRole.Int64CheckedAdd, []
        | SemanticNodeKind.Int64CheckedSubtract _ -> ViewerNodeRole.Int64CheckedSubtract, []
        | SemanticNodeKind.Int64CheckedMultiply _ -> ViewerNodeRole.Int64CheckedMultiply, []
        | SemanticNodeKind.ListRange _ -> ViewerNodeRole.ListRange, []
        | SemanticNodeKind.StringRepeat _ -> ViewerNodeRole.StringRepeat, []
        | SemanticNodeKind.Float64Round _ -> ViewerNodeRole.Float64Round, []
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
        | SemanticNodeKind.Int64ToFloat64 _ -> ViewerNodeRole.Int64ToFloat64, []
        | SemanticNodeKind.Int64WrappingAbs _ -> ViewerNodeRole.Int64WrappingAbs, []
        | SemanticNodeKind.Bits32FromInt64 _ -> ViewerNodeRole.Bits32FromInt64, []
        | SemanticNodeKind.Bits32ToInt64 _ -> ViewerNodeRole.Bits32ToInt64, []
        | SemanticNodeKind.Bits32Xor _ -> ViewerNodeRole.Bits32Xor, []
        | SemanticNodeKind.Bits32And _ -> ViewerNodeRole.Bits32And, []
        | SemanticNodeKind.Bits32Or _ -> ViewerNodeRole.Bits32Or, []
        | SemanticNodeKind.Bits32Not _ -> ViewerNodeRole.Bits32Not, []
        | SemanticNodeKind.Bits32Add _ -> ViewerNodeRole.Bits32Add, []
        | SemanticNodeKind.Bits32RotateRight _ -> ViewerNodeRole.Bits32RotateRight, []
        | SemanticNodeKind.Bits32ShiftRight _ -> ViewerNodeRole.Bits32ShiftRight, []
        | SemanticNodeKind.Bits32ShiftLeft _ -> ViewerNodeRole.Bits32ShiftLeft, []
        | SemanticNodeKind.ListEmpty _ -> ViewerNodeRole.ListEmpty, []
        | SemanticNodeKind.SetEmpty _ -> ViewerNodeRole.SetEmpty, []
        | SemanticNodeKind.MapEmpty _ -> ViewerNodeRole.MapEmpty, []
        | SemanticNodeKind.ListConstruct _ -> ViewerNodeRole.ListConstruct, []
        | SemanticNodeKind.ListLength _ -> ViewerNodeRole.ListLength, []
        | SemanticNodeKind.MapValues _ -> ViewerNodeRole.MapValues, []
        | SemanticNodeKind.MapKeys _ -> ViewerNodeRole.MapKeys, []
        | SemanticNodeKind.SetToList _ -> ViewerNodeRole.SetToList, []
        | SemanticNodeKind.MapSize _ -> ViewerNodeRole.MapSize, []
        | SemanticNodeKind.SetFromList _ -> ViewerNodeRole.SetFromList, []
        | SemanticNodeKind.SetSize _ -> ViewerNodeRole.SetSize, []
        | SemanticNodeKind.ListReverse _ -> ViewerNodeRole.ListReverse, []
        | SemanticNodeKind.ListLast _ -> ViewerNodeRole.ListLast, []
        | SemanticNodeKind.ListDropLast _ -> ViewerNodeRole.ListDropLast, []
        | SemanticNodeKind.ListAppendItem _ -> ViewerNodeRole.ListAppendItem, []
        | SemanticNodeKind.ListItemAt _ -> ViewerNodeRole.ListItemAt, []
        | SemanticNodeKind.MapRemove _ -> ViewerNodeRole.MapRemove, []
        | SemanticNodeKind.SetInsert _ -> ViewerNodeRole.SetInsert, []
        | SemanticNodeKind.SetRemove _ -> ViewerNodeRole.SetRemove, []
        | SemanticNodeKind.SetUnion _ -> ViewerNodeRole.SetUnion, []
        | SemanticNodeKind.SetIntersection _ -> ViewerNodeRole.SetIntersection, []
        | SemanticNodeKind.SetDifference _ -> ViewerNodeRole.SetDifference, []
        | SemanticNodeKind.MapContainsKey _ -> ViewerNodeRole.MapContainsKey, []
        | SemanticNodeKind.SetContains _ -> ViewerNodeRole.SetContains, []
        | SemanticNodeKind.ListIndexOf _ -> ViewerNodeRole.ListIndexOf, []
        | SemanticNodeKind.ListLastIndexOf _ -> ViewerNodeRole.ListLastIndexOf, []
        | SemanticNodeKind.MapGet _ -> ViewerNodeRole.MapGet, []
        | SemanticNodeKind.MapGetOrDefault _ -> ViewerNodeRole.MapGetOrDefault, []
        | SemanticNodeKind.ListSlice _ -> ViewerNodeRole.ListSlice, []
        | SemanticNodeKind.MapInsert _ -> ViewerNodeRole.MapInsert, []
        | SemanticNodeKind.ListConcat _ -> ViewerNodeRole.ListConcat, []
        | SemanticNodeKind.ListFold _ -> ViewerNodeRole.ListFold, []
        | SemanticNodeKind.MapFold _ -> ViewerNodeRole.MapFold, []
        | SemanticNodeKind.ListEffectFold _ -> ViewerNodeRole.ListEffectFold, []
        | SemanticNodeKind.UseResource _ -> ViewerNodeRole.UseResource, []
        | SemanticNodeKind.ParallelAll _ -> ViewerNodeRole.ParallelAll, []
        | SemanticNodeKind.CancellationRequested -> ViewerNodeRole.CancellationRequested, []
        | SemanticNodeKind.TaskScope _ -> ViewerNodeRole.TaskScope, []
        | SemanticNodeKind.TaskStart _ -> ViewerNodeRole.TaskStart, []
        | SemanticNodeKind.TaskWait(_, mode, _) -> ViewerNodeRole.TaskWait mode, []
        | SemanticNodeKind.TaskCancel _ -> ViewerNodeRole.TaskCancel, []
        | SemanticNodeKind.ListMap _ -> ViewerNodeRole.ListMap, []
        | SemanticNodeKind.MapFromList _ -> ViewerNodeRole.MapFromList, []
        | SemanticNodeKind.ListFilter _ -> ViewerNodeRole.ListFilter, []
        | SemanticNodeKind.MapFilter _ -> ViewerNodeRole.MapFilter, []
        | SemanticNodeKind.ListSortBy _ -> ViewerNodeRole.ListSortBy, []
        | SemanticNodeKind.ListMinBy _ -> ViewerNodeRole.ListMinBy, []
        | SemanticNodeKind.MapMapValues _ -> ViewerNodeRole.MapMapValues, []
        | SemanticNodeKind.ListAny _ -> ViewerNodeRole.ListAny, []
        | SemanticNodeKind.ListFindIndex _ -> ViewerNodeRole.ListFindIndex, []
        | SemanticNodeKind.ListAll _ -> ViewerNodeRole.ListAll, []
        | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
            ViewerNodeRole.CapabilityConstruct(capabilityId, List.sortBy fst implementations), []
        | SemanticNodeKind.FunctionReference _ -> ViewerNodeRole.FunctionReference, []
        | SemanticNodeKind.FunctionInvoke _ -> ViewerNodeRole.FunctionInvoke, []
        | SemanticNodeKind.DirectCall _ -> ViewerNodeRole.DirectCall, []
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
        | SemanticNodeKind.ProductUpdate(typeId, _, _) ->
            ViewerNodeRole.ProductUpdate,
            [ { Kind = ViewerRelationKind.ConstructsType
                Target = ViewerTarget.Type typeId } ]
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
