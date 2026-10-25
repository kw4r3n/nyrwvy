namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type ValueDependencyRole =
    | Value
    | Error
    | Substring
    | SliceStart
    | SliceEnd
    | SearchFrom
    | CharacterSet
    | Prefix
    | Suffix
    | Separator
    /// The text StringReplace puts in place of each match.
    | Replacement
    | LeftOperand
    | RightOperand
    | Source
    | Item
    | TaskScope
    | Limit
    | Index
    | Key
    /// The Int64 rotation or shift amount of a Bits32 operation.
    | Amount
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

    let private contextDependencies (context: ArgumentBinding list) =
        context |> List.map (fun binding -> create (ValueDependencyRole.CallArgument binding.TargetParameterId) binding.ValueNodeId)

    /// Canonical role-aware direct value dependencies for one semantic node.
    /// Nested branch result nodes remain represented by their child ValueGraphs.
    let ofNode (node: SemanticNode): ValueDependency list =
        match node.Kind with
        | SemanticNodeKind.StringIsEmpty sourceNodeId
        | SemanticNodeKind.Int64ToString sourceNodeId
        | SemanticNodeKind.Int64ToHexString sourceNodeId
        | SemanticNodeKind.Int64ToFloat64 sourceNodeId
        | SemanticNodeKind.Int64WrappingAbs sourceNodeId
        | SemanticNodeKind.Int64BitNot sourceNodeId
        | SemanticNodeKind.Bits32FromInt64 sourceNodeId
        | SemanticNodeKind.Bits32ToInt64 sourceNodeId
        | SemanticNodeKind.Bits32Not sourceNodeId
        | SemanticNodeKind.StringParseInt64 sourceNodeId
        | SemanticNodeKind.StringParseInt64Hex sourceNodeId
        | SemanticNodeKind.Float64ToString sourceNodeId
        | SemanticNodeKind.StringParseFloat64 sourceNodeId
        | SemanticNodeKind.StringCodePoints sourceNodeId
        | SemanticNodeKind.StringToLower sourceNodeId
        | SemanticNodeKind.StringToUpper sourceNodeId
        | SemanticNodeKind.StringTrim sourceNodeId
        | SemanticNodeKind.StringTrimStart sourceNodeId
        | SemanticNodeKind.StringTrimEnd sourceNodeId
        | SemanticNodeKind.StringDecodeUtf8Lossy sourceNodeId
        | SemanticNodeKind.StringEncodeUtf8 sourceNodeId
        | SemanticNodeKind.Float64RoundToInt64 sourceNodeId
        | SemanticNodeKind.Float64FloorToInt64 sourceNodeId
        | SemanticNodeKind.Float64Sqrt sourceNodeId
        | SemanticNodeKind.Float64Abs sourceNodeId
        | SemanticNodeKind.Float64Negate sourceNodeId
        | SemanticNodeKind.Float64Floor sourceNodeId
        | SemanticNodeKind.Float64Ceiling sourceNodeId
        | SemanticNodeKind.Float64Truncate sourceNodeId
        | SemanticNodeKind.StringUtf8ByteCount sourceNodeId
        | SemanticNodeKind.StringFromCodePoint sourceNodeId
        | SemanticNodeKind.StringToCodePoint sourceNodeId
        | SemanticNodeKind.StringLength sourceNodeId
        | SemanticNodeKind.BoolNot sourceNodeId ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.StringSlice(sourceNodeId, startNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.SliceStart startNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.StringContains(sourceNodeId, substringNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId ]
        | SemanticNodeKind.Bits32RotateRight(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Bits32ShiftRight(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Bits32ShiftLeft(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Int64ShiftLeft(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Int64ShiftRight(sourceNodeId, amountNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Amount amountNodeId ]
        | SemanticNodeKind.StringIndexOf(sourceNodeId, substringNodeId, fromNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId
              create ValueDependencyRole.SearchFrom fromNodeId ]
        | SemanticNodeKind.StringSpan(sourceNodeId, setNodeId, fromNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.CharacterSet setNodeId
              create ValueDependencyRole.SearchFrom fromNodeId ]
        | SemanticNodeKind.StringSpanBack(sourceNodeId, setNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.CharacterSet setNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.StringLastIndexOfBefore(sourceNodeId, substringNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.StringLastIndexOf(sourceNodeId, substringNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId ]
        | SemanticNodeKind.StringStartsWith(sourceNodeId, prefixNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Prefix prefixNodeId ]
        | SemanticNodeKind.StringEndsWith(sourceNodeId, suffixNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Suffix suffixNodeId ]
        | SemanticNodeKind.StringSplit(sourceNodeId, separatorNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Separator separatorNodeId ]
        | SemanticNodeKind.StringSplitLines(sourceNodeId, breaksNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.CharacterSet breaksNodeId ]
        | SemanticNodeKind.StringReplace(sourceNodeId, patternNodeId, replacementNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring patternNodeId
              create ValueDependencyRole.Replacement replacementNodeId ]
        | SemanticNodeKind.StringJoin(sourceNodeId, separatorNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Separator separatorNodeId ]
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringLessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.ValueEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Min(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Max(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListRange(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringRepeat(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Round(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Min(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Max(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64FloorModulo(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32And(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Or(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListConcat(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetUnion(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetIntersection(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetDifference(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetIsSubset(leftNodeId, rightNodeId)
        | SemanticNodeKind.MapUnion(leftNodeId, rightNodeId) ->
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
        | SemanticNodeKind.MapValues sourceNodeId
        | SemanticNodeKind.MapKeys sourceNodeId
        | SemanticNodeKind.SetToList sourceNodeId
        | SemanticNodeKind.MapSize sourceNodeId
        | SemanticNodeKind.SetFromList sourceNodeId
        | SemanticNodeKind.SetSize sourceNodeId
        | SemanticNodeKind.ListReverse sourceNodeId
        | SemanticNodeKind.ListLast sourceNodeId
        | SemanticNodeKind.ListDropLast sourceNodeId
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.ListZipWith(leftNodeId, rightNodeId, _, context) ->
            [ create ValueDependencyRole.LeftOperand leftNodeId
              create ValueDependencyRole.RightOperand rightNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.ListFlatMap(sourceNodeId, _, context)
        | SemanticNodeKind.ListMap(sourceNodeId, _, context)
        | SemanticNodeKind.MapFromList(sourceNodeId, _, context)
        | SemanticNodeKind.ListFilter(sourceNodeId, _, context)
        | SemanticNodeKind.MapFilter(sourceNodeId, _, context)
        | SemanticNodeKind.SetFilter(sourceNodeId, _, context)
        | SemanticNodeKind.ListSortBy(sourceNodeId, _, context)
        | SemanticNodeKind.MapMapValues(sourceNodeId, _, context)
        | SemanticNodeKind.SetMap(sourceNodeId, _, context)
        | SemanticNodeKind.OptionMap(sourceNodeId, _, context)
        | SemanticNodeKind.OptionAndThen(sourceNodeId, _, context)
        | SemanticNodeKind.ResultMap(sourceNodeId, _, context)
        | SemanticNodeKind.ResultMapErr(sourceNodeId, _, context)
        | SemanticNodeKind.ResultAndThen(sourceNodeId, _, context)
        | SemanticNodeKind.ListMinBy(sourceNodeId, _, context)
        | SemanticNodeKind.ListMaxBy(sourceNodeId, _, context)
        | SemanticNodeKind.ListAny(sourceNodeId, _, context)
        | SemanticNodeKind.ListFindIndex(sourceNodeId, _, context)
        | SemanticNodeKind.ListAll(sourceNodeId, _, context) ->
            create ValueDependencyRole.Source sourceNodeId
            :: contextDependencies context
        | SemanticNodeKind.ListFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.ListScan(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.MapFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.SetFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.ListEffectFold(sourceNodeId, seedNodeId, _, context) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Value seedNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, _, _, context) ->
            [ create ValueDependencyRole.Value acquireNodeId
              create ValueDependencyRole.CapabilityValue releaseCapabilityNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.ParallelAll(sourceNodeId, _, context) ->
            create ValueDependencyRole.Source sourceNodeId
            :: contextDependencies context
        | SemanticNodeKind.CancellationRequested -> []
        | SemanticNodeKind.ExceptionCatch(_, bodyContext, _, handlerContext) -> contextDependencies bodyContext @ contextDependencies handlerContext
        | SemanticNodeKind.ExceptionIsCancellation value
        | SemanticNodeKind.ExceptionMessage value
        | SemanticNodeKind.ExceptionRethrow(value, _) -> [ create ValueDependencyRole.Value value ]
        | SemanticNodeKind.CancellationShield(_, context) ->
            contextDependencies context
        | SemanticNodeKind.TaskScope(_, context) ->
            contextDependencies context
        | SemanticNodeKind.TaskStart(scopeNodeId, _, context, itemNodeId) ->
            [ create ValueDependencyRole.TaskScope scopeNodeId
              create ValueDependencyRole.Item itemNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.TaskWait(tasksNodeId, _, limitNodeId) ->
            create ValueDependencyRole.Source tasksNodeId :: (limitNodeId |> Option.map (create ValueDependencyRole.Limit) |> Option.toList)
        | SemanticNodeKind.OneShotCreate(scope, _) -> [ create ValueDependencyRole.TaskScope scope ]
        | SemanticNodeKind.OneShotComplete(cell, value) -> [ create ValueDependencyRole.Source cell; create ValueDependencyRole.Value value ]
        | SemanticNodeKind.OneShotWait(cell, limit) -> create ValueDependencyRole.Source cell :: (limit |> Option.map (create ValueDependencyRole.Limit) |> Option.toList)
        | SemanticNodeKind.TaskCancel taskNodeId -> [ create ValueDependencyRole.Value taskNodeId ]
        | SemanticNodeKind.ListAppendItem(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.ListItemAt(sourceNodeId, indexNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Index indexNodeId ]
        | SemanticNodeKind.MapRemove(sourceNodeId, keyNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId ]
        | SemanticNodeKind.SetInsert(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.SetRemove(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.MapContainsKey(sourceNodeId, keyNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId ]
        | SemanticNodeKind.SetContains(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.ListIndexOf(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.ListLastIndexOf(sourceNodeId, itemNodeId)
        | SemanticNodeKind.ListContains(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.MapGet(sourceNodeId, keyNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId ]
        | SemanticNodeKind.OptionGetOrDefault(sourceNodeId, defaultNodeId)
        | SemanticNodeKind.ResultGetOrDefault(sourceNodeId, defaultNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Value defaultNodeId ]
        | SemanticNodeKind.OptionOkOr(sourceNodeId, errorNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Error errorNodeId ]
        | SemanticNodeKind.ResultOkValue sourceNodeId
        | SemanticNodeKind.ResultErrValue sourceNodeId
        | SemanticNodeKind.OptionIsSome sourceNodeId
        | SemanticNodeKind.ResultIsOk sourceNodeId -> [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.MapGetOrDefault(sourceNodeId, keyNodeId, defaultNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId
              create ValueDependencyRole.Value defaultNodeId ]
        | SemanticNodeKind.ListSlice(sourceNodeId, startNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.SliceStart startNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.ListSetAt(sourceNodeId, indexNodeId, itemNodeId)
        | SemanticNodeKind.ListInsertAt(sourceNodeId, indexNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Index indexNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.ListRemoveAt(sourceNodeId, indexNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Index indexNodeId ]
        | SemanticNodeKind.MapInsert(sourceNodeId, keyNodeId, valueNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId
              create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _ -> []
        | SemanticNodeKind.FunctionInvoke(value, arguments) ->
            create ValueDependencyRole.FunctionValue value :: (arguments |> List.mapi (fun index id -> create (ValueDependencyRole.FunctionArgument index) id))
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
        | SemanticNodeKind.ProductConstruct(_, _, fields)
        | SemanticNodeKind.UnionConstruct(_, _, fields) ->
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.map (fun field ->
                create (ValueDependencyRole.FieldValue field.TargetFieldId) field.ValueNodeId)
        | SemanticNodeKind.ProductUpdate(_, sourceNodeId, fields) ->
            create ValueDependencyRole.Source sourceNodeId
            :: (fields
                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                |> List.map (fun field ->
                    create (ValueDependencyRole.FieldValue field.TargetFieldId) field.ValueNodeId))
        | SemanticNodeKind.OptionSome valueNodeId
        | SemanticNodeKind.ResultOk(_, valueNodeId) ->
            [ create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.Abort errorNodeId
        | SemanticNodeKind.ResultErr(_, errorNodeId) ->
            [ create ValueDependencyRole.Error errorNodeId ]
        // The right operand of BoolAnd/BoolOr is a child ValueGraph, like a Conditional branch.
        | SemanticNodeKind.BoolAnd(leftNodeId, _)
        | SemanticNodeKind.BoolOr(leftNodeId, _) ->
            [ create ValueDependencyRole.LeftOperand leftNodeId ]
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
        | SemanticNodeKind.SetEmpty _
        | SemanticNodeKind.MapEmpty _
        | SemanticNodeKind.OptionNone _ -> []

/// Literal payload of a summarized node, so reviews can tell sibling literals apart.
/// Float64 literals keep their exact bits in `StructuralNodeSummary.Float64BitsHex`.
[<RequireQualifiedAccess>]
type StructuralNodeLiteral =
    | Bool of bool
    | Int64 of int64
    | String of string

type StructuralNodeSummary =
    { NodeId: GraphNodeId
      Role: string
      Type: TypeRef option
      Dependencies: GraphNodeId list
      ValueDependencies: ValueDependency list
      ParameterId: ParameterId option
      FunctionId: FunctionId option
      Float64BitsHex: string option
      Literal: StructuralNodeLiteral option
      /// Display name of the declaration the node names directly: the referenced parameter or
      /// function, the accessed field, the constructed variant or Product type, the invoked
      /// capability operation, or the constructed capability.
      ReferenceDisplayName: string option }

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
