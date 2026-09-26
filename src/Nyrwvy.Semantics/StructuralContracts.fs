namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type ValueDependencyRole =
    | Value
    | Error
    | Substring
    | LeftOperand
    | RightOperand
    | Source
    | Item
    | ListItem of int
    | FunctionValue
    | FunctionArgument of int
    | CallArgument of ParameterId
    | CapabilityValue
    | CapabilityArgument of CapabilityParameterId
    | FieldValue of FieldId
    | BranchCondition
    | MatchInput
    | BindInput

type ValueDependency =
    { Role: ValueDependencyRole
      NodeId: GraphNodeId }

[<RequireQualifiedAccess>]
module ValueDependency =
    let private create role nodeId =
        { Role = role
          NodeId = nodeId }

    /// Canonical role-aware direct value dependencies for one semantic node.
    /// Nested branch result nodes remain represented by their child ValueGraphs.
    let ofNode (node: SemanticNode): ValueDependency list =
        match node.Kind with
        | SemanticNodeKind.StringIsEmpty sourceNodeId
        | SemanticNodeKind.Int64ToString sourceNodeId
        | SemanticNodeKind.StringParseInt64 sourceNodeId ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.StringContains(sourceNodeId, substringNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId ]
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListConcat(leftNodeId, rightNodeId) ->
            [ create ValueDependencyRole.LeftOperand leftNodeId
              create ValueDependencyRole.RightOperand rightNodeId ]
        | SemanticNodeKind.Float64IsNaN valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId ->
            [ create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.ListConstruct(_, itemNodeIds) ->
            itemNodeIds
            |> List.mapi (fun index nodeId -> create (ValueDependencyRole.ListItem index) nodeId)
        | SemanticNodeKind.ListLength sourceNodeId
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.ListMap(sourceNodeId, _, context)
        | SemanticNodeKind.ListFilter(sourceNodeId, _, context)
        | SemanticNodeKind.ListAny(sourceNodeId, _, context)
        | SemanticNodeKind.ListAll(sourceNodeId, _, context) ->
            create ValueDependencyRole.Source sourceNodeId
            :: (context |> List.map (fun binding -> create (ValueDependencyRole.CallArgument binding.TargetParameterId) binding.ValueNodeId))
        | SemanticNodeKind.ListFold(sourceNodeId, seedNodeId, _, context) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Value seedNodeId ]
            @ (context |> List.map (fun binding -> create (ValueDependencyRole.CallArgument binding.TargetParameterId) binding.ValueNodeId))
        | SemanticNodeKind.ListEffectFold(sourceNodeId, seedNodeId, _, context) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Value seedNodeId ]
            @ (context |> List.map (fun binding -> create (ValueDependencyRole.CallArgument binding.TargetParameterId) binding.ValueNodeId))
        | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, _, _, context) ->
            [ create ValueDependencyRole.Value acquireNodeId
              create ValueDependencyRole.CapabilityValue releaseCapabilityNodeId ]
            @ (context |> List.map (fun binding -> create (ValueDependencyRole.CallArgument binding.TargetParameterId) binding.ValueNodeId))
        | SemanticNodeKind.ParallelAll(sourceNodeId, _, context) ->
            create ValueDependencyRole.Source sourceNodeId
            :: (context |> List.map (fun binding -> create (ValueDependencyRole.CallArgument binding.TargetParameterId) binding.ValueNodeId))
        | SemanticNodeKind.CancellationRequested -> []
        | SemanticNodeKind.ListAppendItem(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _ -> []
        | SemanticNodeKind.FunctionInvoke(value, arguments) ->
            { Role = ValueDependencyRole.FunctionValue; NodeId = value } :: (arguments |> List.mapi (fun index id -> { Role = ValueDependencyRole.FunctionArgument index; NodeId = id }))
        | SemanticNodeKind.DirectCall(_, arguments) ->
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
            |> List.map (fun argument ->
                create (ValueDependencyRole.CallArgument argument.TargetParameterId) argument.ValueNodeId)
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            create ValueDependencyRole.CapabilityValue capabilityValueNodeId
            :: (arguments
                |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                |> List.map (fun argument ->
                    create
                        (ValueDependencyRole.CapabilityArgument argument.TargetCapabilityParameterId)
                        argument.ValueNodeId))
        | SemanticNodeKind.ProductConstruct(_, fields)
        | SemanticNodeKind.UnionConstruct(_, fields) ->
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.map (fun field ->
                create (ValueDependencyRole.FieldValue field.TargetFieldId) field.ValueNodeId)
        | SemanticNodeKind.OptionSome valueNodeId
        | SemanticNodeKind.ResultOk(_, valueNodeId) ->
            [ create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.ResultErr(_, errorNodeId) ->
            [ create ValueDependencyRole.Error errorNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) ->
            [ create ValueDependencyRole.BranchCondition conditionNodeId ]
        | SemanticNodeKind.Bind(inputNodeId, _) ->
            [ create ValueDependencyRole.BindInput inputNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _)
        | SemanticNodeKind.MatchOption(inputNodeId, _, _)
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) ->
            [ create ValueDependencyRole.MatchInput inputNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.OptionNone _ -> []

type StructuralNodeSummary =
    { NodeId: GraphNodeId
      Role: string
      Type: TypeRef option
      Dependencies: GraphNodeId list
      ValueDependencies: ValueDependency list
      ParameterId: ParameterId option
      FunctionId: FunctionId option
      Float64BitsHex: string option }

type FunctionBodyReplacementDiff =
    { OwnerFunctionId: FunctionId
      OldResultNodeId: GraphNodeId
      NewResultNodeId: GraphNodeId
      RemovedNodes: StructuralNodeSummary list
      AddedNodes: StructuralNodeSummary list }

type PublishedStructuralTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: FunctionBodyReplacementDiff
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type AgentStructuralRejectionReceipt =
    { TransactionId: TransactionId
      RetainedSnapshotId: SnapshotId }

[<RequireQualifiedAccess>]
type AgentStructuralWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | ReviewProjectionFailed of SemanticQueryError
    | FingerprintFailed of SnapshotCodecError

[<RequireQualifiedAccess>]
type AgentStructuralCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * PublishedStructuralTransaction
