namespace Nyrwvy.Core

/// Typed Semantic IR is a derived, backend-neutral projection of one fully
/// validated and semantically complete snapshot. It intentionally excludes
/// display names, obligations, transaction history, storage shape, and viewer data.
type TypedSemanticField =
    { Id: FieldId
      Type: TypeRef }

type TypedSemanticVariant =
    { Id: VariantId
      Fields: TypedSemanticField list }

[<RequireQualifiedAccess>]
type TypedSemanticTypeKind =
    | Product of TypedSemanticField list
    | Union of TypedSemanticVariant list

type TypedSemanticType =
    { Id: TypeId
      Kind: TypedSemanticTypeKind }

type TypedSemanticCapabilityParameter =
    { Id: CapabilityParameterId
      Type: TypeRef }

type TypedSemanticCapabilityOperation =
    { Id: CapabilityOperationId
      Parameters: TypedSemanticCapabilityParameter list
      ResultType: TypeRef
      FailureType: TypeRef option
      EffectId: EffectId }

type TypedSemanticCapability =
    { Id: CapabilityId
      Operations: TypedSemanticCapabilityOperation list }

type TypedSemanticParameter =
    { Id: ParameterId
      Type: TypeRef }

type TypedSemanticArgument =
    { TargetParameterId: ParameterId
      ValueNodeId: GraphNodeId }

type TypedSemanticCapabilityArgument =
    { TargetCapabilityParameterId: CapabilityParameterId
      ValueNodeId: GraphNodeId }

type TypedSemanticFieldValue =
    { TargetFieldId: FieldId
      ValueNodeId: GraphNodeId }

type TypedSemanticPatternBinding =
    { Id: PatternBindingId
      Type: TypeRef }

type TypedSemanticPatternField =
    { SourceFieldId: FieldId
      Binding: TypedSemanticPatternBinding }

type TypedSemanticUnionArm =
    { VariantId: VariantId
      FieldBindings: TypedSemanticPatternField list
      Body: TypedSemanticGraph }

and TypedSemanticValueArm =
    { ValueBinding: TypedSemanticPatternBinding
      Body: TypedSemanticGraph }

and TypedSemanticErrorArm =
    { ErrorBinding: TypedSemanticPatternBinding
      Body: TypedSemanticGraph }

and [<RequireQualifiedAccess>] TypedSemanticNodeKind =
    | ParameterReference of ParameterId
    | PatternReference of PatternBindingId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of valueNodeId: GraphNodeId
    | Int64ToString of valueNodeId: GraphNodeId
    | StringParseInt64 of valueNodeId: GraphNodeId
    | StringLength of valueNodeId: GraphNodeId
    | StringSlice of valueNodeId: GraphNodeId * startNodeId: GraphNodeId * endNodeId: GraphNodeId
    | StringContains of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    | StringEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | StringConcat of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Add of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Subtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Multiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Divide of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64IsNaN of valueNodeId: GraphNodeId
    | Float64IsFinite of valueNodeId: GraphNodeId
    | Float64IsInfinite of valueNodeId: GraphNodeId
    | ListEmpty
    | ListConstruct of itemNodeIds: GraphNodeId list
    | ListLength of listNodeId: GraphNodeId
    | ListAppendItem of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    | ListConcat of leftListNodeId: GraphNodeId * rightListNodeId: GraphNodeId
    | ListFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: TypedSemanticArgument list
    | ParallelAll of sourceListNodeId: GraphNodeId * workerFunctionId: FunctionId * context: TypedSemanticArgument list
    | CancellationRequested
    | TaskScope of bodyFunctionId: FunctionId * context: TypedSemanticArgument list
    | TaskStart of scopeNodeId: GraphNodeId * workerFunctionId: FunctionId * context: TypedSemanticArgument list * itemNodeId: GraphNodeId
    | TaskWait of tasksNodeId: GraphNodeId * mode: TaskWaitMode * limitNodeId: GraphNodeId option
    | TaskCancel of taskNodeId: GraphNodeId
    | UseResource of acquireNodeId: GraphNodeId * releaseCapabilityNodeId: GraphNodeId * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListEffectFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListMap of sourceListNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListFilter of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListAny of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: TypedSemanticArgument list
    | ListAll of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: TypedSemanticArgument list
    /// Construct a stateless capability using named pure functions, one per operation.
    | CapabilityConstruct of CapabilityId * implementations: (CapabilityOperationId * FunctionId) list
    | FunctionReference of FunctionId
    | FunctionInvoke of GraphNodeId * GraphNodeId list
    | DirectCall of FunctionId * TypedSemanticArgument list
    | CapabilityInvoke of capabilityValueNodeId: GraphNodeId * CapabilityOperationId * TypedSemanticCapabilityArgument list
    | ProductConstruct of TypedSemanticFieldValue list
    | FieldAccess of sourceNodeId: GraphNodeId * FieldId
    | UnionConstruct of VariantId * TypedSemanticFieldValue list
    | OptionNone
    | OptionSome of valueNodeId: GraphNodeId
    | ResultOk of valueNodeId: GraphNodeId
    | Abort of errorNodeId: GraphNodeId
    | ResultErr of errorNodeId: GraphNodeId
    | BoolNot of operandNodeId: GraphNodeId
    | BoolAnd of leftNodeId: GraphNodeId * right: TypedSemanticGraph
    | BoolOr of leftNodeId: GraphNodeId * right: TypedSemanticGraph
    | Conditional of conditionNodeId: GraphNodeId * whenTrue: TypedSemanticGraph * whenFalse: TypedSemanticGraph
    | MatchUnion of inputNodeId: GraphNodeId * TypedSemanticUnionArm list
    | Bind of inputNodeId: GraphNodeId * body: TypedSemanticValueArm
    | MatchOption of inputNodeId: GraphNodeId * someArm: TypedSemanticValueArm * noneBody: TypedSemanticGraph
    | MatchResult of inputNodeId: GraphNodeId * okArm: TypedSemanticValueArm * errArm: TypedSemanticErrorArm

and TypedSemanticNode =
    { OriginNodeId: GraphNodeId
      Completion: GraphCompletion
      Kind: TypedSemanticNodeKind }
    member this.Type =
        match this.Completion with
        | GraphCompletion.Normal value -> value
        | GraphCompletion.Aborts -> invalidOp "An abrupt node has no value type."

and TypedSemanticGraph =
    { Nodes: TypedSemanticNode list
      ResultNodeId: GraphNodeId }

type TypedSemanticFunction =
    { Id: FunctionId
      Parameters: TypedSemanticParameter list
      ResultType: TypeRef
      RequiredEffects: EffectId list
      Body: TypedSemanticGraph }

type TypedSemanticProgram =
    { SourceSnapshotId: SnapshotId
      Types: TypedSemanticType list
      Effects: EffectId list
      Capabilities: TypedSemanticCapability list
      Functions: TypedSemanticFunction list }

[<RequireQualifiedAccess>]
type TypedSemanticIrError =
    | InvalidSnapshot of ValidationIssue list
    | IncompleteSnapshot of ObligationId list
    | MissingNodeType of FunctionId * GraphNodeId

[<RequireQualifiedAccess>]
module TypedSemanticIr =
    let private traverse (mapper: 'a -> Result<'b, 'error>) (items: 'a list): Result<'b list, 'error> =
        let folder state item =
            match state with
            | Error error -> Error error
            | Ok values -> mapper item |> Result.map (fun value -> value :: values)

        items
        |> List.fold folder (Ok [])
        |> Result.map List.rev

    let private projectPatternBinding (binding: PatternBinding): TypedSemanticPatternBinding =
        { Id = binding.Id
          Type = binding.Type }

    let private projectFieldValue (binding: FieldValueBinding): TypedSemanticFieldValue =
        { TargetFieldId = binding.TargetFieldId
          ValueNodeId = binding.ValueNodeId }

    let private projectArgument (argument: ArgumentBinding): TypedSemanticArgument =
        { TargetParameterId = argument.TargetParameterId
          ValueNodeId = argument.ValueNodeId }

    let private projectContext (context: ArgumentBinding list) =
        context |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId) |> List.map projectArgument

    let private projectCapabilityArgument
        (argument: CapabilityArgumentBinding)
        : TypedSemanticCapabilityArgument =
        { TargetCapabilityParameterId = argument.TargetCapabilityParameterId
          ValueNodeId = argument.ValueNodeId }

    let rec private projectGraph
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        (resolveType: GraphNodeId -> GraphCompletion option)
        (graph: ValueGraph)
        : Result<TypedSemanticGraph, TypedSemanticIrError> =
        let projectPatternField (binding: PatternFieldBinding): TypedSemanticPatternField =
            { SourceFieldId = binding.SourceFieldId
              Binding = projectPatternBinding binding.Binding }

        let projectUnionArm (arm: UnionMatchArm): Result<TypedSemanticUnionArm, TypedSemanticIrError> =
            projectGraph snapshot functionEntity resolveType arm.Body
            |> Result.map (fun body ->
                { VariantId = arm.VariantId
                  FieldBindings =
                    arm.FieldBindings
                    |> List.sortBy (fun binding -> StableId.value binding.SourceFieldId)
                    |> List.map projectPatternField
                  Body = body })

        let projectValueArm (arm: ValueMatchArm): Result<TypedSemanticValueArm, TypedSemanticIrError> =
            projectGraph snapshot functionEntity resolveType arm.Body
            |> Result.map (fun body ->
                { ValueBinding = projectPatternBinding arm.ValueBinding
                  Body = body })

        let projectErrorArm (arm: ErrorMatchArm): Result<TypedSemanticErrorArm, TypedSemanticIrError> =
            projectGraph snapshot functionEntity resolveType arm.Body
            |> Result.map (fun body ->
                { ErrorBinding = projectPatternBinding arm.ErrorBinding
                  Body = body })

        let projectNode (node: SemanticNode): Result<TypedSemanticNode, TypedSemanticIrError> =
            match resolveType node.Id with
            | None -> Error(TypedSemanticIrError.MissingNodeType(functionEntity.Id, node.Id))
            | Some nodeType ->
                let projectedKind: Result<TypedSemanticNodeKind, TypedSemanticIrError> =
                    match node.Kind with
                    | SemanticNodeKind.ParameterReference parameterId ->
                        Ok(TypedSemanticNodeKind.ParameterReference parameterId)
                    | SemanticNodeKind.PatternReference bindingId ->
                        Ok(TypedSemanticNodeKind.PatternReference bindingId)
                    | SemanticNodeKind.StringLiteral value ->
                        Ok(TypedSemanticNodeKind.StringLiteral value)
                    | SemanticNodeKind.BoolLiteral value ->
                        Ok(TypedSemanticNodeKind.BoolLiteral value)
                    | SemanticNodeKind.UnitLiteral ->
                        Ok TypedSemanticNodeKind.UnitLiteral
                    | SemanticNodeKind.Int64Literal value ->
                        Ok(TypedSemanticNodeKind.Int64Literal value)
                    | SemanticNodeKind.Float64Literal bits ->
                        Ok(TypedSemanticNodeKind.Float64Literal bits)
                    | SemanticNodeKind.StringIsEmpty valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringIsEmpty valueNodeId)
                    | SemanticNodeKind.Int64ToString valueNodeId ->
                        Ok(TypedSemanticNodeKind.Int64ToString valueNodeId)
                    | SemanticNodeKind.StringParseInt64 valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringParseInt64 valueNodeId)
                    | SemanticNodeKind.StringLength valueNodeId ->
                        Ok(TypedSemanticNodeKind.StringLength valueNodeId)
                    | SemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
                        Ok(TypedSemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId))
                    | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                        Ok(TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId))
                    | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId))
                    | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64Equal(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Add(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Divide(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64Equal(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
                        Ok(TypedSemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId))
                    | SemanticNodeKind.Float64IsNaN valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64IsNaN valueNodeId)
                    | SemanticNodeKind.Float64IsFinite valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64IsFinite valueNodeId)
                    | SemanticNodeKind.Float64IsInfinite valueNodeId ->
                        Ok(TypedSemanticNodeKind.Float64IsInfinite valueNodeId)
                    | SemanticNodeKind.ListEmpty _ ->
                        Ok TypedSemanticNodeKind.ListEmpty
                    | SemanticNodeKind.ListConstruct(_, itemNodeIds) ->
                        Ok(TypedSemanticNodeKind.ListConstruct itemNodeIds)
                    | SemanticNodeKind.ListLength listNodeId ->
                        Ok(TypedSemanticNodeKind.ListLength listNodeId)
                    | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId))
                    | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
                        Ok(TypedSemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId))
                    | SemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, projectContext context))
                    | SemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, projectContext context))
                    | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, projectContext context))
                    | SemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, projectContext context))
                    | SemanticNodeKind.CancellationRequested -> Ok TypedSemanticNodeKind.CancellationRequested
                    | SemanticNodeKind.TaskScope(bodyFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.TaskScope(bodyFunctionId, projectContext context))
                    | SemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, context, itemNodeId) ->
                        Ok(TypedSemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, projectContext context, itemNodeId))
                    | SemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId) -> Ok(TypedSemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId))
                    | SemanticNodeKind.TaskCancel taskNodeId -> Ok(TypedSemanticNodeKind.TaskCancel taskNodeId)
                    | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, projectContext context))
                    | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, projectContext context))
                    | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, projectContext context))
                    | SemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, context) ->
                        Ok(TypedSemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, projectContext context))
                    | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) -> Ok(TypedSemanticNodeKind.CapabilityConstruct(capabilityId, implementations))
                    | SemanticNodeKind.FunctionReference target -> Ok(TypedSemanticNodeKind.FunctionReference target)
                    | SemanticNodeKind.FunctionInvoke(value, arguments) -> Ok(TypedSemanticNodeKind.FunctionInvoke(value, arguments))
                    | SemanticNodeKind.DirectCall(functionId, arguments) ->
                        Ok(
                            TypedSemanticNodeKind.DirectCall(
                                functionId,
                                arguments
                                |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                                |> List.map projectArgument
                            )
                        )
                    | SemanticNodeKind.CapabilityInvoke(capabilityNodeId, operationId, arguments) ->
                        Ok(
                            TypedSemanticNodeKind.CapabilityInvoke(
                                capabilityNodeId,
                                operationId,
                                arguments
                                |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                                |> List.map projectCapabilityArgument
                            )
                        )
                    | SemanticNodeKind.ProductConstruct(_, fields) ->
                        Ok(
                            TypedSemanticNodeKind.ProductConstruct(
                                fields
                                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                                |> List.map projectFieldValue
                            )
                        )
                    | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
                        Ok(TypedSemanticNodeKind.FieldAccess(sourceNodeId, fieldId))
                    | SemanticNodeKind.UnionConstruct(variantId, fields) ->
                        Ok(
                            TypedSemanticNodeKind.UnionConstruct(
                                variantId,
                                fields
                                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                                |> List.map projectFieldValue
                            )
                        )
                    | SemanticNodeKind.OptionNone _ ->
                        Ok TypedSemanticNodeKind.OptionNone
                    | SemanticNodeKind.OptionSome valueNodeId ->
                        Ok(TypedSemanticNodeKind.OptionSome valueNodeId)
                    | SemanticNodeKind.ResultOk(_, valueNodeId) ->
                        Ok(TypedSemanticNodeKind.ResultOk valueNodeId)
                    | SemanticNodeKind.Abort errorNodeId -> Ok(TypedSemanticNodeKind.Abort errorNodeId)
                    | SemanticNodeKind.ResultErr(_, errorNodeId) ->
                        Ok(TypedSemanticNodeKind.ResultErr errorNodeId)
                    | SemanticNodeKind.BoolNot operandNodeId ->
                        Ok(TypedSemanticNodeKind.BoolNot operandNodeId)
                    | SemanticNodeKind.BoolAnd(leftNodeId, right) ->
                        projectGraph snapshot functionEntity resolveType right
                        |> Result.map (fun projectedRight -> TypedSemanticNodeKind.BoolAnd(leftNodeId, projectedRight))
                    | SemanticNodeKind.BoolOr(leftNodeId, right) ->
                        projectGraph snapshot functionEntity resolveType right
                        |> Result.map (fun projectedRight -> TypedSemanticNodeKind.BoolOr(leftNodeId, projectedRight))
                    | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                        match
                            projectGraph snapshot functionEntity resolveType whenTrue,
                            projectGraph snapshot functionEntity resolveType whenFalse
                        with
                        | Ok projectedTrue, Ok projectedFalse ->
                            Ok(TypedSemanticNodeKind.Conditional(conditionNodeId, projectedTrue, projectedFalse))
                        | Error error, _
                        | _, Error error -> Error error
                    | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                        traverse projectUnionArm arms
                        |> Result.map (fun projectedArms ->
                            TypedSemanticNodeKind.MatchUnion(
                                inputNodeId,
                                projectedArms |> List.sortBy (fun arm -> StableId.value arm.VariantId)
                            ))
                    | SemanticNodeKind.Bind(inputNodeId, body) ->
                        projectValueArm body
                        |> Result.map (fun projected -> TypedSemanticNodeKind.Bind(inputNodeId, projected))
                    | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                        match
                            projectValueArm someArm,
                            projectGraph snapshot functionEntity resolveType noneBody
                        with
                        | Ok projectedSome, Ok projectedNone ->
                            Ok(TypedSemanticNodeKind.MatchOption(inputNodeId, projectedSome, projectedNone))
                        | Error error, _
                        | _, Error error -> Error error
                    | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                        match projectValueArm okArm, projectErrorArm errArm with
                        | Ok projectedOk, Ok projectedErr ->
                            Ok(TypedSemanticNodeKind.MatchResult(inputNodeId, projectedOk, projectedErr))
                        | Error error, _
                        | _, Error error -> Error error

                projectedKind
                |> Result.map (fun kind ->
                    { OriginNodeId = node.Id
                      Completion = nodeType
                      Kind = kind })

        graph.Nodes
        |> List.sortBy (fun node -> StableId.value node.Id)
        |> traverse projectNode
        |> Result.map (fun nodes ->
            { Nodes = nodes
              ResultNodeId = graph.ResultNodeId })

    let private projectType (typeEntity: SemanticType): TypedSemanticType =
        let projectField (field: SemanticField): TypedSemanticField =
            { Id = field.Id
              Type = field.Type }

        let projectVariant (variant: SemanticVariant): TypedSemanticVariant =
            { Id = variant.Id
              Fields =
                variant.Fields
                |> List.sortBy (fun field -> StableId.value field.Id)
                |> List.map projectField }

        let kind =
            match typeEntity.Kind with
            | SemanticTypeKind.Product fields ->
                TypedSemanticTypeKind.Product(
                    fields
                    |> List.sortBy (fun field -> StableId.value field.Id)
                    |> List.map projectField
                )
            | SemanticTypeKind.Union variants ->
                TypedSemanticTypeKind.Union(
                    variants
                    |> List.sortBy (fun variant -> StableId.value variant.Id)
                    |> List.map projectVariant
                )

        { Id = typeEntity.Id
          Kind = kind }

    let private projectCapability (capability: SemanticCapability): TypedSemanticCapability =
        let projectParameter
            (parameter: SemanticCapabilityParameter)
            : TypedSemanticCapabilityParameter =
            { Id = parameter.Id
              Type = parameter.Type }

        let projectOperation
            (operation: SemanticCapabilityOperation)
            : TypedSemanticCapabilityOperation =
            { Id = operation.Id
              Parameters = operation.Parameters |> List.map projectParameter
              ResultType = operation.ResultType
              FailureType = operation.FailureType
              EffectId = operation.EffectId }

        { Id = capability.Id
          Operations =
            capability.Operations
            |> List.sortBy (fun operation -> StableId.value operation.Id)
            |> List.map projectOperation }

    let private projectFunction
        (snapshot: Snapshot)
        (createNodeTypeResolver: SemanticFunction -> GraphNodeId -> GraphCompletion option)
        (functionEntity: SemanticFunction)
        : Result<TypedSemanticFunction, TypedSemanticIrError> =
        let resolveType = createNodeTypeResolver functionEntity

        projectGraph snapshot functionEntity resolveType functionEntity.Body
        |> Result.map (fun body ->
            { Id = functionEntity.Id
              Parameters =
                functionEntity.Parameters
                |> List.map (fun parameter ->
                    { Id = parameter.Id
                      Type = parameter.Type })
              ResultType = functionEntity.ResultType
              RequiredEffects = functionEntity.RequiredEffects |> List.sortBy StableId.value
              Body = body })

    let project (snapshot: Snapshot): Result<TypedSemanticProgram, TypedSemanticIrError> =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> Error(TypedSemanticIrError.InvalidSnapshot issues)
        | Ok(SemanticReadiness.Incomplete obligationIds) ->
            Error(TypedSemanticIrError.IncompleteSnapshot obligationIds)
        | Ok SemanticReadiness.Complete ->
            let createNodeTypeResolver = SemanticFacts.createNodeCompletionResolverFactory snapshot
            snapshot.Functions
            |> List.sortBy (fun functionEntity -> StableId.value functionEntity.Id)
            |> traverse (projectFunction snapshot createNodeTypeResolver)
            |> Result.map (fun functions ->
                { SourceSnapshotId = snapshot.Id
                  Types =
                    snapshot.Types
                    |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)
                    |> List.map projectType
                  Effects = snapshot.Effects |> List.map (fun effect -> effect.Id) |> List.sortBy StableId.value
                  Capabilities =
                    snapshot.Capabilities
                    |> List.sortBy (fun capability -> StableId.value capability.Id)
                    |> List.map projectCapability
                  Functions = functions })
