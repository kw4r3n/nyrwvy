namespace NyamNyam.Core

[<RequireQualifiedAccess>]
type RecipePatternSlot =
    | Field of FieldId
    | Value
    | Error

type RecipePatternReference =
    { ScopeDepth: int
      Slot: RecipePatternSlot }

/// Ephemeral typed construction intent for bounded cross-domain expressions.
/// Child expressions are nested recipes; production graph-node and pattern-binding
/// IDs remain system-owned. Effects must already be declared by the target function.
[<RequireQualifiedAccess>]
type ExpressionRecipe =
    | Parameter of ParameterId
    | PatternReference of RecipePatternReference
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of ExpressionRecipe
    | StringContains of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingAdd of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingSubtract of ExpressionRecipe * ExpressionRecipe
    | Int64WrappingMultiply of ExpressionRecipe * ExpressionRecipe
    | Int64Equal of ExpressionRecipe * ExpressionRecipe
    | Int64LessThan of ExpressionRecipe * ExpressionRecipe
    | Int64LessThanOrEqual of ExpressionRecipe * ExpressionRecipe
    | Int64GreaterThan of ExpressionRecipe * ExpressionRecipe
    | Int64GreaterThanOrEqual of ExpressionRecipe * ExpressionRecipe
    | Float64Add of ExpressionRecipe * ExpressionRecipe
    | Float64Subtract of ExpressionRecipe * ExpressionRecipe
    | Float64Multiply of ExpressionRecipe * ExpressionRecipe
    | Float64Divide of ExpressionRecipe * ExpressionRecipe
    | Float64Equal of ExpressionRecipe * ExpressionRecipe
    | Float64LessThan of ExpressionRecipe * ExpressionRecipe
    | Float64IsNaN of ExpressionRecipe
    | Float64IsFinite of ExpressionRecipe
    | Float64IsInfinite of ExpressionRecipe
    | ListEmpty of itemType: TypeRef
    | ListConstruct of itemType: TypeRef * items: ExpressionRecipe list
    | ListLength of ExpressionRecipe
    | ListAppendItem of list: ExpressionRecipe * item: ExpressionRecipe
    | ListConcat of left: ExpressionRecipe * right: ExpressionRecipe
    | ListMap of source: ExpressionRecipe * mapperFunctionId: FunctionId
    | ListFilter of source: ExpressionRecipe * predicateFunctionId: FunctionId
    | ListAny of source: ExpressionRecipe * predicateFunctionId: FunctionId
    | ListAll of source: ExpressionRecipe * predicateFunctionId: FunctionId
    | ProductConstruct of typeId: TypeId * fields: (FieldId * ExpressionRecipe) list
    | FieldAccess of source: ExpressionRecipe * fieldId: FieldId
    | UnionConstruct of variantId: VariantId * fields: (FieldId * ExpressionRecipe) list
    | OptionNone of itemType: TypeRef
    | OptionSome of ExpressionRecipe
    | ResultOk of errorType: TypeRef * value: ExpressionRecipe
    | ResultErr of okType: TypeRef * error: ExpressionRecipe
    | DirectCall of targetFunctionId: FunctionId * arguments: (ParameterId * ExpressionRecipe) list
    | CapabilityInvoke of capabilityValue: ExpressionRecipe * operationId: CapabilityOperationId * arguments: (CapabilityParameterId * ExpressionRecipe) list
    | Conditional of condition: ExpressionRecipe * whenTrue: ExpressionRecipe * whenFalse: ExpressionRecipe
    | MatchUnion of input: ExpressionRecipe * arms: (VariantId * ExpressionRecipe) list
    | Bind of value: ExpressionRecipe * body: ExpressionRecipe
    | MatchOption of input: ExpressionRecipe * someBody: ExpressionRecipe * noneBody: ExpressionRecipe
    | MatchResult of input: ExpressionRecipe * okBody: ExpressionRecipe * errBody: ExpressionRecipe

type AgentReplaceFunctionBodyWithExpressionRecipe =
    { TargetFunctionId: FunctionId
      ExpectedBaseSnapshotId: SnapshotId
      ExpectedCurrentResultNodeId: GraphNodeId
      Replacement: ExpressionRecipe }

type AgentExpressionStructuralChangeReview =
    { RequestedChange: AgentReplaceFunctionBodyWithExpressionRecipe
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: FunctionBodyReplacementDiff
      BaseFocus: FunctionFocusView
      ProposedFocus: FunctionFocusView }

type private ExpressionStructuralProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentExpressionStructuralChangeReview }

type AgentExpressionStructuralChangeProposal =
    private
    | AgentExpressionStructuralChangeProposal of ExpressionStructuralProposalState

[<RequireQualifiedAccess>]
module AgentExpressionStructuralChangeProposal =
    let private state (AgentExpressionStructuralChangeProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentExpressionStructuralWorkflow =
    open StructuralTransactionInternals

    type private PatternTypeFrame = (RecipePatternSlot * TypeRef) list
    type private PatternBindingFrame = (RecipePatternSlot * PatternBinding) list

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private listType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private optionType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
    let private resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])

    let private listItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) -> Some itemType
        | _ -> None

    let private recipeChildren recipe =
        match recipe with
        | ExpressionRecipe.Parameter _
        | ExpressionRecipe.PatternReference _
        | ExpressionRecipe.StringLiteral _
        | ExpressionRecipe.BoolLiteral _
        | ExpressionRecipe.UnitLiteral
        | ExpressionRecipe.Int64Literal _
        | ExpressionRecipe.Float64Literal _
        | ExpressionRecipe.ListEmpty _
        | ExpressionRecipe.OptionNone _ -> Seq.empty
        | ExpressionRecipe.StringIsEmpty child
        | ExpressionRecipe.Float64IsNaN child
        | ExpressionRecipe.Float64IsFinite child
        | ExpressionRecipe.Float64IsInfinite child
        | ExpressionRecipe.ListLength child
        | ExpressionRecipe.ListMap(child, _)
        | ExpressionRecipe.ListFilter(child, _)
        | ExpressionRecipe.ListAny(child, _)
        | ExpressionRecipe.ListAll(child, _)
        | ExpressionRecipe.FieldAccess(child, _)
        | ExpressionRecipe.OptionSome child
        | ExpressionRecipe.ResultOk(_, child)
        | ExpressionRecipe.ResultErr(_, child) -> Seq.singleton child
        | ExpressionRecipe.StringContains(left, right)
        | ExpressionRecipe.Bind(left, right)
        | ExpressionRecipe.Int64WrappingAdd(left, right)
        | ExpressionRecipe.Int64WrappingSubtract(left, right)
        | ExpressionRecipe.Int64WrappingMultiply(left, right)
        | ExpressionRecipe.Int64Equal(left, right)
        | ExpressionRecipe.Int64LessThan(left, right)
        | ExpressionRecipe.Int64LessThanOrEqual(left, right)
        | ExpressionRecipe.Int64GreaterThan(left, right)
        | ExpressionRecipe.Int64GreaterThanOrEqual(left, right)
        | ExpressionRecipe.Float64Add(left, right)
        | ExpressionRecipe.Float64Subtract(left, right)
        | ExpressionRecipe.Float64Multiply(left, right)
        | ExpressionRecipe.Float64Divide(left, right)
        | ExpressionRecipe.Float64Equal(left, right)
        | ExpressionRecipe.Float64LessThan(left, right)
        | ExpressionRecipe.ListAppendItem(left, right)
        | ExpressionRecipe.ListConcat(left, right) -> seq { yield left; yield right }
        | ExpressionRecipe.ListConstruct(_, items) -> items |> Seq.ofList
        | ExpressionRecipe.ProductConstruct(_, fields)
        | ExpressionRecipe.UnionConstruct(_, fields) -> fields |> Seq.map snd
        | ExpressionRecipe.DirectCall(_, arguments) -> arguments |> Seq.map snd
        | ExpressionRecipe.CapabilityInvoke(capabilityValue, _, arguments) ->
            seq {
                yield capabilityValue
                yield! arguments |> Seq.map snd
            }
        | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
            seq { yield condition; yield whenTrue; yield whenFalse }
        | ExpressionRecipe.MatchUnion(input, arms) ->
            seq {
                yield input
                for _, body in arms do
                    yield body
            }
        | ExpressionRecipe.MatchOption(input, someBody, noneBody)
        | ExpressionRecipe.MatchResult(input, someBody, noneBody) ->
            seq { yield input; yield someBody; yield noneBody }

    let private validateTypeRef (snapshot: Snapshot) typeRef =
        let typeIds = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id) |> Set.ofList
        let capabilityIds = snapshot.Capabilities |> List.map (fun capability -> capability.Id) |> Set.ofList
        let invalid message = diagnostic "PureExpressionInvalidTypeRef" message

        let rec validate typeRef =
            match typeRef with
            | TypeRef.Primitive _ -> Ok()
            | TypeRef.Named typeId when Set.contains typeId typeIds -> Ok()
            | TypeRef.Named typeId -> Error(invalid $"Type reference names unknown type '{StableId.value typeId}'.")
            | TypeRef.Capability capabilityId when Set.contains capabilityId capabilityIds -> Ok()
            | TypeRef.Capability capabilityId ->
                Error(invalid $"Type reference names unknown capability '{StableId.value capabilityId}'.")
            | TypeRef.BuiltinGeneric(kind, arguments) ->
                let expectedArity =
                    match kind with
                    | BuiltinGenericType.Option
                    | BuiltinGenericType.List -> 1
                    | BuiltinGenericType.Result -> 2

                if List.length arguments <> expectedArity then
                    Error(invalid $"Builtin generic type '{kind}' requires {expectedArity} type argument(s).")
                else
                    let rec validateArguments = function
                        | [] -> Ok()
                        | argument :: rest ->
                            match validate argument with
                            | Error error -> Error error
                            | Ok() -> validateArguments rest
                    validateArguments arguments

        validate typeRef

    let private resolvePatternType (frames: PatternTypeFrame list) (reference: RecipePatternReference) =
        if reference.ScopeDepth < 0 then
            Error(
                diagnostic
                    "PureExpressionPatternScopeDepthInvalid"
                    "Pattern-reference scope depth must be non-negative."
            )
        else
            match List.tryItem reference.ScopeDepth frames with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionPatternScopeNotFound"
                        $"Pattern-reference scope depth {reference.ScopeDepth} is not visible here."
                )
            | Some frame ->
                match frame |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
                | Some(_, typeRef) -> Ok typeRef
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionPatternSlotNotFound"
                            $"Pattern-reference slot {reference.Slot} does not exist in scope depth {reference.ScopeDepth}."
                    )

    let private inferTypeWithFrames
        (snapshot: Snapshot)
        (target: SemanticFunction)
        (initialFrames: PatternTypeFrame list)
        recipe
        =
        let parametersById = target.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList
        let functionsById = snapshot.Functions |> List.map (fun functionEntity -> functionEntity.Id, functionEntity) |> Map.ofList
        let typesById = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id, typeEntity) |> Map.ofList
        let capabilityOperationsById =
            snapshot.Capabilities
            |> List.collect (fun capability ->
                capability.Operations |> List.map (fun operation -> operation.Id, (capability.Id, operation)))
            |> Map.ofList
        let variantsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product _ -> []
                | SemanticTypeKind.Union variants -> variants |> List.map (fun variant -> variant.Id, (typeEntity.Id, variant)))
            |> Map.ofList
        let productFieldsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.Id, (typeEntity.Id, field))
                | SemanticTypeKind.Union _ -> [])
            |> Map.ofList

        let typeMismatch role side expected actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires {expected}, but the recipe produces {actual}."

        let listMismatch role side actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires List<T>, but the recipe produces {actual}."

        let validateListCallback role callbackFunctionId sourceItemType predicateResultRequired =
            match Map.tryFind callbackFunctionId functionsById with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionListCallbackNotFound"
                        $"{role} callback function '{StableId.value callbackFunctionId}' does not exist."
                )
            | Some callback when List.length callback.Parameters <> 1 ->
                Error(
                    diagnostic
                        "PureExpressionListCallbackArityMismatch"
                        $"{role} callback function '{StableId.value callback.Id}' must have exactly one parameter."
                )
            | Some callback when callback.Parameters.Head.Type <> sourceItemType ->
                Error(
                    diagnostic
                        "PureExpressionListCallbackInputTypeMismatch"
                        $"{role} callback function '{StableId.value callback.Id}' input type does not match the source list item type."
                )
            | Some callback when predicateResultRequired && callback.ResultType <> boolType ->
                Error(
                    diagnostic
                        "PureExpressionListCallbackResultTypeMismatch"
                        $"{role} callback function '{StableId.value callback.Id}' must return Bool."
                )
            | Some callback when not (List.isEmpty callback.RequiredEffects) ->
                Error(
                    diagnostic
                        "PureExpressionListCallbackEffectsNotAllowed"
                        $"{role} callback function '{StableId.value callback.Id}' must be pure."
                )
            | Some callback -> Ok callback

        let rec infer frames recipe =
            let unary role expected result child =
                match infer frames child with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "value" expected actual)
                | Ok _ -> Ok result

            let binary role expected result left right =
                match infer frames left with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "left" expected actual)
                | Ok _ ->
                    match infer frames right with
                    | Error error -> Error error
                    | Ok actual when actual <> expected -> Error(typeMismatch role "right" expected actual)
                    | Ok _ -> Ok result

            let validateFieldBindings role (expectedFields: SemanticField list) (bindings: (FieldId * ExpressionRecipe) list) =
                match bindings |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(fieldId, _) ->
                    Error(
                        diagnostic
                            "PureExpressionDuplicateFieldBinding"
                            $"{role} binds field '{StableId.value fieldId}' more than once."
                    )
                | None ->
                    let expectedIds = expectedFields |> List.map (fun field -> field.Id) |> Set.ofList
                    let actualIds = bindings |> List.map fst |> Set.ofList
                    if expectedIds <> actualIds then
                        Error(diagnostic "PureExpressionFieldBindingMismatch" $"{role} must bind exactly the fields declared by its target.")
                    else
                        let rec validateFields = function
                            | [] -> Ok()
                            | (field: SemanticField) :: rest ->
                                let _, value = bindings |> List.find (fun (fieldId, _) -> fieldId = field.Id)
                                match infer frames value with
                                | Error error -> Error error
                                | Ok actual when actual <> field.Type ->
                                    Error(
                                        diagnostic
                                            "PureExpressionFieldBindingTypeMismatch"
                                            $"{role} field '{StableId.value field.Id}' requires {field.Type}, but the recipe produces {actual}."
                                    )
                                | Ok _ -> validateFields rest
                        validateFields expectedFields

            let validateCallArguments (callee: SemanticFunction) (bindings: (ParameterId * ExpressionRecipe) list) =
                match bindings |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(parameterId, _) ->
                    Error(
                        diagnostic
                            "PureExpressionDuplicateCallArgument"
                            $"DirectCall binds parameter '{StableId.value parameterId}' more than once."
                    )
                | None ->
                    let calleeParametersById = callee.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList
                    match bindings |> List.tryFind (fun (parameterId, _) -> not (Map.containsKey parameterId calleeParametersById)) with
                    | Some(parameterId, _) ->
                        Error(
                            diagnostic
                                "PureExpressionCallArgumentTargetNotFound"
                                $"DirectCall targets parameter '{StableId.value parameterId}' not owned by callee '{StableId.value callee.Id}'."
                        )
                    | None ->
                        let actualIds = bindings |> List.map fst |> Set.ofList
                        match callee.Parameters |> List.tryFind (fun parameter -> not (Set.contains parameter.Id actualIds)) with
                        | Some parameter ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentMissing"
                                    $"DirectCall is missing required parameter '{StableId.value parameter.Id}' for callee '{StableId.value callee.Id}'."
                            )
                        | None ->
                            let rec validateParameters = function
                                | [] -> Ok()
                                | (parameter: SemanticParameter) :: rest ->
                                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                                    match infer frames value with
                                    | Error error -> Error error
                                    | Ok actual when actual <> parameter.Type ->
                                        Error(
                                            diagnostic
                                                "PureExpressionCallArgumentTypeMismatch"
                                                $"DirectCall parameter '{StableId.value parameter.Id}' requires {parameter.Type}, but the recipe produces {actual}."
                                        )
                                    | Ok _ -> validateParameters rest
                            validateParameters callee.Parameters

            let validateCapabilityArguments
                (operation: SemanticCapabilityOperation)
                (bindings: (CapabilityParameterId * ExpressionRecipe) list)
                =
                match bindings |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(parameterId, _) ->
                    Error(
                        diagnostic
                            "ExpressionDuplicateCapabilityArgument"
                            $"CapabilityInvoke binds parameter '{StableId.value parameterId}' more than once."
                    )
                | None ->
                    let expectedIds = operation.Parameters |> List.map (fun parameter -> parameter.Id) |> Set.ofList
                    let actualIds = bindings |> List.map fst |> Set.ofList
                    if expectedIds <> actualIds then
                        Error(
                            diagnostic
                                "ExpressionCapabilityArgumentSetMismatch"
                                $"CapabilityInvoke must bind exactly the parameters declared by operation '{StableId.value operation.Id}'."
                        )
                    else
                        let rec validateParameters = function
                            | [] -> Ok()
                            | (parameter: SemanticCapabilityParameter) :: rest ->
                                let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                                match infer frames value with
                                | Error error -> Error error
                                | Ok actual when actual <> parameter.Type ->
                                    Error(
                                        diagnostic
                                            "ExpressionCapabilityArgumentTypeMismatch"
                                            $"CapabilityInvoke parameter '{StableId.value parameter.Id}' requires {parameter.Type}, but the recipe produces {actual}."
                                    )
                                | Ok _ -> validateParameters rest
                        validateParameters operation.Parameters

            let branchMismatch role expected actual =
                diagnostic
                    "PureExpressionBranchTypeMismatch"
                    $"{role} branches must produce one common type; expected {expected}, but found {actual}."

            match recipe with
            | ExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId parametersById with
                | Some parameter -> Ok parameter.Type
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionParameterOutOfScope"
                            $"Parameter '{StableId.value parameterId}' is not owned by the target function."
                    )
            | ExpressionRecipe.PatternReference reference -> resolvePatternType frames reference
            | ExpressionRecipe.StringLiteral value when isNull value ->
                Error(diagnostic "PureExpressionNullStringLiteral" "StringLiteral requires a non-null value.")
            | ExpressionRecipe.StringLiteral _ -> Ok stringType
            | ExpressionRecipe.BoolLiteral _ -> Ok boolType
            | ExpressionRecipe.UnitLiteral -> Ok unitType
            | ExpressionRecipe.Int64Literal _ -> Ok int64Type
            | ExpressionRecipe.Float64Literal _ -> Ok float64Type
            | ExpressionRecipe.StringIsEmpty value -> unary "StringIsEmpty" stringType boolType value
            | ExpressionRecipe.StringContains(value, substring) -> binary "StringContains" stringType boolType value substring
            | ExpressionRecipe.Int64WrappingAdd(left, right) -> binary "Int64WrappingAdd" int64Type int64Type left right
            | ExpressionRecipe.Int64WrappingSubtract(left, right) -> binary "Int64WrappingSubtract" int64Type int64Type left right
            | ExpressionRecipe.Int64WrappingMultiply(left, right) -> binary "Int64WrappingMultiply" int64Type int64Type left right
            | ExpressionRecipe.Int64Equal(left, right) -> binary "Int64Equal" int64Type boolType left right
            | ExpressionRecipe.Int64LessThan(left, right) -> binary "Int64LessThan" int64Type boolType left right
            | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> binary "Int64LessThanOrEqual" int64Type boolType left right
            | ExpressionRecipe.Int64GreaterThan(left, right) -> binary "Int64GreaterThan" int64Type boolType left right
            | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> binary "Int64GreaterThanOrEqual" int64Type boolType left right
            | ExpressionRecipe.Float64Add(left, right) -> binary "Float64Add" float64Type float64Type left right
            | ExpressionRecipe.Float64Subtract(left, right) -> binary "Float64Subtract" float64Type float64Type left right
            | ExpressionRecipe.Float64Multiply(left, right) -> binary "Float64Multiply" float64Type float64Type left right
            | ExpressionRecipe.Float64Divide(left, right) -> binary "Float64Divide" float64Type float64Type left right
            | ExpressionRecipe.Float64Equal(left, right) -> binary "Float64Equal" float64Type boolType left right
            | ExpressionRecipe.Float64LessThan(left, right) -> binary "Float64LessThan" float64Type boolType left right
            | ExpressionRecipe.Float64IsNaN value -> unary "Float64IsNaN" float64Type boolType value
            | ExpressionRecipe.Float64IsFinite value -> unary "Float64IsFinite" float64Type boolType value
            | ExpressionRecipe.Float64IsInfinite value -> unary "Float64IsInfinite" float64Type boolType value
            | ExpressionRecipe.ListEmpty itemType ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() -> Ok(listType itemType)
            | ExpressionRecipe.ListConstruct(itemType, items) ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() ->
                    let rec validateItems = function
                        | [] -> Ok(listType itemType)
                        | item :: rest ->
                            match infer frames item with
                            | Error error -> Error error
                            | Ok actual when actual <> itemType -> Error(typeMismatch "ListConstruct" "item" itemType actual)
                            | Ok _ -> validateItems rest
                    validateItems items
            | ExpressionRecipe.ListLength list ->
                match infer frames list with
                | Error error -> Error error
                | Ok actual ->
                    match listItemType actual with
                    | Some _ -> Ok int64Type
                    | None -> Error(listMismatch "ListLength" "value" actual)
            | ExpressionRecipe.ListAppendItem(list, item) ->
                match infer frames list with
                | Error error -> Error error
                | Ok listResultType ->
                    match listItemType listResultType with
                    | None -> Error(listMismatch "ListAppendItem" "list" listResultType)
                    | Some itemType ->
                        match infer frames item with
                        | Error error -> Error error
                        | Ok actual when actual <> itemType -> Error(typeMismatch "ListAppendItem" "item" itemType actual)
                        | Ok _ -> Ok listResultType
            | ExpressionRecipe.ListConcat(left, right) ->
                match infer frames left with
                | Error error -> Error error
                | Ok leftType ->
                    match listItemType leftType with
                    | None -> Error(listMismatch "ListConcat" "left" leftType)
                    | Some _ ->
                        match infer frames right with
                        | Error error -> Error error
                        | Ok rightType when rightType <> leftType -> Error(typeMismatch "ListConcat" "right" leftType rightType)
                        | Ok _ -> Ok leftType
            | ExpressionRecipe.ListMap(source, mapperFunctionId) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListMap" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListMap" mapperFunctionId sourceItemType false with
                        | Error error -> Error error
                        | Ok mapper -> Ok(listType mapper.ResultType)
            | ExpressionRecipe.ListFilter(source, predicateFunctionId) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFilter" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListFilter" predicateFunctionId sourceItemType true with
                        | Error error -> Error error
                        | Ok _ -> Ok sourceType
            | ExpressionRecipe.ListAny(source, predicateFunctionId) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAny" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAny" predicateFunctionId sourceItemType true with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | ExpressionRecipe.ListAll(source, predicateFunctionId) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAll" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAll" predicateFunctionId sourceItemType true with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | ExpressionRecipe.ProductConstruct(typeId, fields) ->
                match Map.tryFind typeId typesById with
                | None ->
                    Error(diagnostic "PureExpressionProductTypeNotFound" $"Product construction references unknown type '{StableId.value typeId}'.")
                | Some { Kind = SemanticTypeKind.Product expectedFields } ->
                    validateFieldBindings "ProductConstruct" expectedFields fields |> Result.map (fun () -> TypeRef.Named typeId)
                | Some _ ->
                    Error(diagnostic "PureExpressionProductTargetMismatch" $"Product construction target '{StableId.value typeId}' is not a product type.")
            | ExpressionRecipe.FieldAccess(source, fieldId) ->
                match Map.tryFind fieldId productFieldsById with
                | None ->
                    Error(diagnostic "PureExpressionProductFieldNotFound" $"Field access references field '{StableId.value fieldId}' that is not owned by a product type.")
                | Some(ownerTypeId, field) ->
                    match infer frames source with
                    | Error error -> Error error
                    | Ok(TypeRef.Named sourceTypeId) when sourceTypeId = ownerTypeId -> Ok field.Type
                    | Ok actual ->
                        Error(diagnostic "PureExpressionFieldAccessTypeMismatch" $"Field access requires {TypeRef.Named ownerTypeId}, but the source recipe produces {actual}.")
            | ExpressionRecipe.UnionConstruct(variantId, fields) ->
                match Map.tryFind variantId variantsById with
                | None ->
                    Error(diagnostic "PureExpressionUnionVariantNotFound" $"Union construction references unknown variant '{StableId.value variantId}'.")
                | Some(ownerTypeId, variant) ->
                    validateFieldBindings "UnionConstruct" variant.Fields fields |> Result.map (fun () -> TypeRef.Named ownerTypeId)
            | ExpressionRecipe.OptionNone itemType ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() -> Ok(optionType itemType)
            | ExpressionRecipe.OptionSome value -> infer frames value |> Result.map optionType
            | ExpressionRecipe.ResultOk(errorType, value) ->
                match validateTypeRef snapshot errorType with
                | Error error -> Error error
                | Ok() -> infer frames value |> Result.map (fun okType -> resultType okType errorType)
            | ExpressionRecipe.ResultErr(okType, error) ->
                match validateTypeRef snapshot okType with
                | Error error -> Error error
                | Ok() -> infer frames error |> Result.map (resultType okType)
            | ExpressionRecipe.DirectCall(targetFunctionId, arguments) ->
                match Map.tryFind targetFunctionId functionsById with
                | None ->
                    Error(diagnostic "PureExpressionCallTargetNotFound" $"DirectCall references unknown function '{StableId.value targetFunctionId}'.")
                | Some callee ->
                    match
                        callee.RequiredEffects
                        |> List.tryFind (fun effectId -> not (List.contains effectId target.RequiredEffects))
                    with
                    | Some effectId ->
                        Error(
                            diagnostic
                                "ExpressionCallMissingEffectRequirement"
                                $"DirectCall callee '{StableId.value callee.Id}' requires effect '{StableId.value effectId}' not declared by target function '{StableId.value target.Id}'."
                        )
                    | None ->
                        validateCallArguments callee arguments |> Result.map (fun () -> callee.ResultType)
            | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
                match Map.tryFind operationId capabilityOperationsById with
                | None ->
                    Error(
                        diagnostic
                            "ExpressionCapabilityOperationNotFound"
                            $"CapabilityInvoke references unknown operation '{StableId.value operationId}'."
                    )
                | Some(ownerCapabilityId, operation) ->
                    match infer frames capabilityValue with
                    | Error error -> Error error
                    | Ok actual when actual <> TypeRef.Capability ownerCapabilityId ->
                        Error(
                            diagnostic
                                "ExpressionCapabilityValueTypeMismatch"
                                $"CapabilityInvoke operation '{StableId.value operationId}' requires {TypeRef.Capability ownerCapabilityId}, but the receiver produces {actual}."
                        )
                    | Ok _ when not (List.contains operation.EffectId target.RequiredEffects) ->
                        Error(
                            diagnostic
                                "ExpressionCapabilityMissingEffectRequirement"
                                $"CapabilityInvoke operation '{StableId.value operationId}' requires effect '{StableId.value operation.EffectId}' not declared by target function '{StableId.value target.Id}'."
                        )
                    | Ok _ ->
                        validateCapabilityArguments operation arguments |> Result.map (fun () -> operation.ResultType)
            | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
                match infer frames condition with
                | Error error -> Error error
                | Ok actual when actual <> boolType -> Error(typeMismatch "Conditional" "condition" boolType actual)
                | Ok _ ->
                    match infer frames whenTrue with
                    | Error error -> Error error
                    | Ok trueType ->
                        match infer frames whenFalse with
                        | Error error -> Error error
                        | Ok falseType when falseType <> trueType -> Error(branchMismatch "Conditional" trueType falseType)
                        | Ok _ -> Ok trueType
            | ExpressionRecipe.MatchUnion(input, arms) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.Named inputTypeId) ->
                    match Map.tryFind inputTypeId typesById with
                    | Some { Kind = SemanticTypeKind.Union variants } ->
                        match arms |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
                        | Some(variantId, _) ->
                            Error(diagnostic "PureExpressionMatchArmDuplicate" $"MatchUnion contains variant '{StableId.value variantId}' more than once.")
                        | None ->
                            let expectedIds = variants |> List.map (fun variant -> variant.Id) |> Set.ofList
                            let actualIds = arms |> List.map fst |> Set.ofList
                            if expectedIds <> actualIds then
                                Error(diagnostic "PureExpressionMatchArmMismatch" "MatchUnion must contain every declared variant exactly once and no other variants.")
                            else
                                let rec inferArms expected = function
                                    | [] ->
                                        match expected with
                                        | Some resultType -> Ok resultType
                                        | None -> Error(diagnostic "PureExpressionMatchHasNoArms" "MatchUnion requires at least one declared variant to derive a result type.")
                                    | (variant: SemanticVariant) :: rest ->
                                        let _, body = arms |> List.find (fun (variantId, _) -> variantId = variant.Id)
                                        let frame: PatternTypeFrame =
                                            variant.Fields |> List.map (fun field -> RecipePatternSlot.Field field.Id, field.Type)
                                        match infer (frame :: frames) body with
                                        | Error error -> Error error
                                        | Ok actual ->
                                            match expected with
                                            | None -> inferArms (Some actual) rest
                                            | Some expectedType when actual = expectedType -> inferArms expected rest
                                            | Some expectedType -> Error(branchMismatch "MatchUnion" expectedType actual)
                                inferArms None variants
                    | Some _
                    | None ->
                        Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
            | ExpressionRecipe.Bind(input, body) ->
                match infer frames input with
                | Error error -> Error error
                | Ok inputType -> infer ([ RecipePatternSlot.Value, inputType ] :: frames) body
            | ExpressionRecipe.MatchOption(input, someBody, noneBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) ->
                    let someFrame: PatternTypeFrame = [ RecipePatternSlot.Value, itemType ]
                    match infer (someFrame :: frames) someBody with
                    | Error error -> Error error
                    | Ok someType ->
                        match infer ([] :: frames) noneBody with
                        | Error error -> Error error
                        | Ok noneType when noneType <> someType -> Error(branchMismatch "MatchOption" someType noneType)
                        | Ok _ -> Ok someType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchOption input must produce Option<T>.")
            | ExpressionRecipe.MatchResult(input, okBody, errBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                    let okFrame: PatternTypeFrame = [ RecipePatternSlot.Value, okType ]
                    let errFrame: PatternTypeFrame = [ RecipePatternSlot.Error, errorType ]
                    match infer (okFrame :: frames) okBody with
                    | Error error -> Error error
                    | Ok okResultType ->
                        match infer (errFrame :: frames) errBody with
                        | Error error -> Error error
                        | Ok errResultType when errResultType <> okResultType -> Error(branchMismatch "MatchResult" okResultType errResultType)
                        | Ok _ -> Ok okResultType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchResult input must produce Result<T,E>.")

        infer initialFrames recipe

    let private inferType snapshot target recipe = inferTypeWithFrames snapshot target [] recipe

    let private validatePreflight
        (request: AgentReplaceFunctionBodyWithExpressionRecipe)
        (baseSnapshot: Snapshot)
        : Result<SemanticFunction, TransactionDiagnostic> =
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            Error(diagnostic "StructuralExpectedBaseMismatch" "The structural request targets a different base snapshot.")
        else
            match baseSnapshot.Functions |> List.tryFind (fun functionEntity -> functionEntity.Id = request.TargetFunctionId) with
            | None -> Error(diagnostic "StructuralTargetNotFound" "The target function does not exist.")
            | Some target when target.Body.ResultNodeId <> request.ExpectedCurrentResultNodeId ->
                Error(diagnostic "StructuralExpectedResultMismatch" "The target function result node changed after inspection.")
            | Some target ->
                match validateRecipeBounds recipeChildren request.Replacement with
                | Error error -> Error error
                | Ok() ->
                    match inferType baseSnapshot target request.Replacement with
                    | Error error -> Error error
                    | Ok replacementType when replacementType <> target.ResultType ->
                        Error(
                            diagnostic
                                "PureExpressionResultTypeMismatch"
                                $"The replacement recipe produces {replacementType}, but the target function returns {target.ResultType}."
                        )
                    | Ok _ ->
                        match bodyObligationError target baseSnapshot with
                        | Some error -> Error error
                        | None -> Ok target

    let private bindingTypeFrames (frames: PatternBindingFrame list): PatternTypeFrame list =
        frames |> List.map (List.map (fun (slot, binding) -> slot, binding.Type))

    let private resolvePatternBinding (frames: PatternBindingFrame list) (reference: RecipePatternReference) =
        match List.tryItem reference.ScopeDepth frames with
        | Some frame ->
            match frame |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
            | Some(_, binding) -> binding
            | None -> failwith "Pattern recipe passed preflight with an unavailable slot."
        | None -> failwith "Pattern recipe passed preflight with an unavailable scope."

    /// Materialization is deterministic in shape/order with no caller-controlled IDs or CSE.
    let rec private materialize
        (snapshot: Snapshot)
        (target: SemanticFunction)
        (frames: PatternBindingFrame list)
        recipe
        =
        let materializeRecipe child = materialize snapshot target frames child

        let materializeGraph childFrames body =
            let nodes, resultNodeId = materialize snapshot target childFrames body
            { Nodes = nodes
              ResultNodeId = resultNodeId }

        let unary kind child =
            let childNodes, childResult = materializeRecipe child
            let nodeId = StableIdAllocator.newGraphNodeId ()
            childNodes @ [ { Id = nodeId; Kind = kind childResult } ], nodeId

        let binary kind left right =
            let leftNodes, leftResult = materializeRecipe left
            let rightNodes, rightResult = materializeRecipe right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes @ rightNodes @ [ { Id = nodeId; Kind = kind (leftResult, rightResult) } ], nodeId

        let rec many = function
            | [] -> [], []
            | head :: tail ->
                let headNodes, headResult = materializeRecipe head
                let tailNodes, tailResults = many tail
                headNodes @ tailNodes, headResult :: tailResults

        let listCallback kind source callbackFunctionId =
            let sourceNodes, sourceResult = materializeRecipe source
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ [ { Id = nodeId; Kind = kind (sourceResult, callbackFunctionId) } ], nodeId

        let materializeFieldBindings (expectedFields: SemanticField list) (bindings: (FieldId * ExpressionRecipe) list) =
            let rec loop = function
                | [] -> [], []
                | (field: SemanticField) :: rest ->
                    let _, value = bindings |> List.find (fun (fieldId, _) -> fieldId = field.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetFieldId = field.Id
                      ValueNodeId = valueResult } :: restBindings
            loop expectedFields

        let materializeCallArguments (callee: SemanticFunction) (bindings: (ParameterId * ExpressionRecipe) list) =
            let rec loop = function
                | [] -> [], []
                | (parameter: SemanticParameter) :: rest ->
                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetParameterId = parameter.Id
                      ValueNodeId = valueResult } :: restBindings
            loop callee.Parameters

        let materializeCapabilityArguments
            (operation: SemanticCapabilityOperation)
            (bindings: (CapabilityParameterId * ExpressionRecipe) list)
            =
            let rec loop = function
                | [] -> [], []
                | (parameter: SemanticCapabilityParameter) :: rest ->
                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                    let valueNodes, valueResult = materializeRecipe value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetCapabilityParameterId = parameter.Id
                      ValueNodeId = valueResult } :: restBindings
            loop operation.Parameters

        let inferHere child =
            match inferTypeWithFrames snapshot target (bindingTypeFrames frames) child with
            | Ok typeRef -> typeRef
            | Error _ -> failwith "Expression recipe passed preflight but failed materialization type recovery."

        match recipe with
        | ExpressionRecipe.Parameter parameterId ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ], nodeId
        | ExpressionRecipe.PatternReference reference ->
            let binding = resolvePatternBinding frames reference
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.PatternReference binding.Id } ], nodeId
        | ExpressionRecipe.StringLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral value } ], nodeId
        | ExpressionRecipe.BoolLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.BoolLiteral value } ], nodeId
        | ExpressionRecipe.UnitLiteral ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.UnitLiteral } ], nodeId
        | ExpressionRecipe.Int64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Int64Literal value } ], nodeId
        | ExpressionRecipe.Float64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Float64Literal value } ], nodeId
        | ExpressionRecipe.StringIsEmpty value -> unary SemanticNodeKind.StringIsEmpty value
        | ExpressionRecipe.StringContains(value, substring) -> binary SemanticNodeKind.StringContains value substring
        | ExpressionRecipe.Int64WrappingAdd(left, right) -> binary SemanticNodeKind.Int64WrappingAdd left right
        | ExpressionRecipe.Int64WrappingSubtract(left, right) -> binary SemanticNodeKind.Int64WrappingSubtract left right
        | ExpressionRecipe.Int64WrappingMultiply(left, right) -> binary SemanticNodeKind.Int64WrappingMultiply left right
        | ExpressionRecipe.Int64Equal(left, right) -> binary SemanticNodeKind.Int64Equal left right
        | ExpressionRecipe.Int64LessThan(left, right) -> binary SemanticNodeKind.Int64LessThan left right
        | ExpressionRecipe.Int64LessThanOrEqual(left, right) -> binary SemanticNodeKind.Int64LessThanOrEqual left right
        | ExpressionRecipe.Int64GreaterThan(left, right) -> binary SemanticNodeKind.Int64GreaterThan left right
        | ExpressionRecipe.Int64GreaterThanOrEqual(left, right) -> binary SemanticNodeKind.Int64GreaterThanOrEqual left right
        | ExpressionRecipe.Float64Add(left, right) -> binary SemanticNodeKind.Float64Add left right
        | ExpressionRecipe.Float64Subtract(left, right) -> binary SemanticNodeKind.Float64Subtract left right
        | ExpressionRecipe.Float64Multiply(left, right) -> binary SemanticNodeKind.Float64Multiply left right
        | ExpressionRecipe.Float64Divide(left, right) -> binary SemanticNodeKind.Float64Divide left right
        | ExpressionRecipe.Float64Equal(left, right) -> binary SemanticNodeKind.Float64Equal left right
        | ExpressionRecipe.Float64LessThan(left, right) -> binary SemanticNodeKind.Float64LessThan left right
        | ExpressionRecipe.Float64IsNaN value -> unary SemanticNodeKind.Float64IsNaN value
        | ExpressionRecipe.Float64IsFinite value -> unary SemanticNodeKind.Float64IsFinite value
        | ExpressionRecipe.Float64IsInfinite value -> unary SemanticNodeKind.Float64IsInfinite value
        | ExpressionRecipe.ListEmpty itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ListEmpty itemType } ], nodeId
        | ExpressionRecipe.ListConstruct(itemType, items) ->
            let itemNodes, itemResults = many items
            let nodeId = StableIdAllocator.newGraphNodeId ()
            itemNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListConstruct(itemType, itemResults) } ], nodeId
        | ExpressionRecipe.ListLength list -> unary SemanticNodeKind.ListLength list
        | ExpressionRecipe.ListAppendItem(list, item) -> binary SemanticNodeKind.ListAppendItem list item
        | ExpressionRecipe.ListConcat(left, right) -> binary SemanticNodeKind.ListConcat left right
        | ExpressionRecipe.ListMap(source, mapperFunctionId) -> listCallback SemanticNodeKind.ListMap source mapperFunctionId
        | ExpressionRecipe.ListFilter(source, predicateFunctionId) -> listCallback SemanticNodeKind.ListFilter source predicateFunctionId
        | ExpressionRecipe.ListAny(source, predicateFunctionId) -> listCallback SemanticNodeKind.ListAny source predicateFunctionId
        | ExpressionRecipe.ListAll(source, predicateFunctionId) -> listCallback SemanticNodeKind.ListAll source predicateFunctionId
        | ExpressionRecipe.ProductConstruct(typeId, fields) ->
            let expectedFields =
                match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                | { Kind = SemanticTypeKind.Product expectedFields } -> expectedFields
                | _ -> failwith "Product recipe passed preflight with a non-product type."
            let fieldNodes, fieldBindings = materializeFieldBindings expectedFields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ProductConstruct(typeId, fieldBindings) } ], nodeId
        | ExpressionRecipe.FieldAccess(source, fieldId) ->
            let sourceNodes, sourceResult = materializeRecipe source
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.FieldAccess(sourceResult, fieldId) } ], nodeId
        | ExpressionRecipe.UnionConstruct(variantId, fields) ->
            let variant =
                snapshot.Types
                |> List.collect (fun typeEntity ->
                    match typeEntity.Kind with
                    | SemanticTypeKind.Product _ -> []
                    | SemanticTypeKind.Union variants -> variants)
                |> List.find (fun variant -> variant.Id = variantId)
            let fieldNodes, fieldBindings = materializeFieldBindings variant.Fields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            fieldNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.UnionConstruct(variantId, fieldBindings) } ], nodeId
        | ExpressionRecipe.OptionNone itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.OptionNone itemType } ], nodeId
        | ExpressionRecipe.OptionSome value -> unary SemanticNodeKind.OptionSome value
        | ExpressionRecipe.ResultOk(errorType, value) -> unary (fun valueNodeId -> SemanticNodeKind.ResultOk(errorType, valueNodeId)) value
        | ExpressionRecipe.ResultErr(okType, error) -> unary (fun errorNodeId -> SemanticNodeKind.ResultErr(okType, errorNodeId)) error
        | ExpressionRecipe.DirectCall(targetFunctionId, arguments) ->
            let callee = snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = targetFunctionId)
            let argumentNodes, argumentBindings = materializeCallArguments callee arguments
            let nodeId = StableIdAllocator.newGraphNodeId ()
            argumentNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.DirectCall(targetFunctionId, argumentBindings) } ], nodeId
        | ExpressionRecipe.CapabilityInvoke(capabilityValue, operationId, arguments) ->
            let capabilityNodes, capabilityResult = materializeRecipe capabilityValue
            let operation =
                snapshot.Capabilities
                |> List.collect (fun capability -> capability.Operations)
                |> List.find (fun operation -> operation.Id = operationId)
            let argumentNodes, argumentBindings = materializeCapabilityArguments operation arguments
            let nodeId = StableIdAllocator.newGraphNodeId ()
            capabilityNodes @ argumentNodes
            @ [ { Id = nodeId; Kind = SemanticNodeKind.CapabilityInvoke(capabilityResult, operationId, argumentBindings) } ], nodeId
        | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
            let conditionNodes, conditionResult = materializeRecipe condition
            let trueGraph = materializeGraph frames whenTrue
            let falseGraph = materializeGraph frames whenFalse
            let nodeId = StableIdAllocator.newGraphNodeId ()
            conditionNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.Conditional(conditionResult, trueGraph, falseGraph) } ], nodeId
        | ExpressionRecipe.MatchUnion(input, arms) ->
            let inputNodes, inputResult = materializeRecipe input
            let inputType = inferHere input
            let variants =
                match inputType with
                | TypeRef.Named typeId ->
                    match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                    | { Kind = SemanticTypeKind.Union variants } -> variants
                    | _ -> failwith "MatchUnion recipe passed preflight with a non-union type."
                | _ -> failwith "MatchUnion recipe passed preflight with a non-named input type."

            let preparedArms =
                variants
                |> List.map (fun variant ->
                    let preparedFields =
                        variant.Fields
                        |> List.map (fun field ->
                            let binding: PatternBinding =
                                { Id = StableIdAllocator.newPatternBindingId ()
                                  DisplayName = None
                                  Type = field.Type }
                            { SourceFieldId = field.Id
                              Binding = binding },
                            (RecipePatternSlot.Field field.Id, binding))
                    variant,
                    (preparedFields |> List.map fst),
                    (preparedFields |> List.map snd))

            let materializedArms: UnionMatchArm list =
                preparedArms
                |> List.map (fun (variant, fieldBindings, frame) ->
                    let _, bodyRecipe = arms |> List.find (fun (variantId, _) -> variantId = variant.Id)
                    { VariantId = variant.Id
                      FieldBindings = fieldBindings
                      Body = materializeGraph (frame :: frames) bodyRecipe })

            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchUnion(inputResult, materializedArms) } ], nodeId
        | ExpressionRecipe.Bind(input, body) ->
            let inputNodes, inputResult = materializeRecipe input
            let binding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = inferHere input }
            let boundBody: ValueMatchArm =
                { ValueBinding = binding
                  Body = materializeGraph ([ RecipePatternSlot.Value, binding ] :: frames) body }
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.Bind(inputResult, boundBody) } ], nodeId
        | ExpressionRecipe.MatchOption(input, someBody, noneBody) ->
            let inputNodes, inputResult = materializeRecipe input
            let itemType =
                match inferHere input with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) -> itemType
                | _ -> failwith "MatchOption recipe passed preflight with a non-Option input."
            let someBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = itemType }
            let someArm: ValueMatchArm =
                { ValueBinding = someBinding
                  Body = materializeGraph ([ RecipePatternSlot.Value, someBinding ] :: frames) someBody }
            let noneGraph = materializeGraph ([] :: frames) noneBody
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchOption(inputResult, someArm, noneGraph) } ], nodeId
        | ExpressionRecipe.MatchResult(input, okBody, errBody) ->
            let inputNodes, inputResult = materializeRecipe input
            let okType, errorType =
                match inferHere input with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) -> okType, errorType
                | _ -> failwith "MatchResult recipe passed preflight with a non-Result input."
            let okBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = okType }
            let errBinding: PatternBinding =
                { Id = StableIdAllocator.newPatternBindingId ()
                  DisplayName = None
                  Type = errorType }
            let okArm: ValueMatchArm =
                { ValueBinding = okBinding
                  Body = materializeGraph ([ RecipePatternSlot.Value, okBinding ] :: frames) okBody }
            let errArm: ErrorMatchArm =
                { ErrorBinding = errBinding
                  Body = materializeGraph ([ RecipePatternSlot.Error, errBinding ] :: frames) errBody }
            let nodeId = StableIdAllocator.newGraphNodeId ()
            inputNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.MatchResult(inputResult, okArm, errArm) } ], nodeId

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithExpressionRecipe)
        : Result<AgentExpressionStructuralChangeProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            match validatePreflight request baseSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Ok target ->
                let newNodes, newResultId = materialize baseSnapshot target [] request.Replacement
                let replacementBody =
                    { Nodes = newNodes
                      ResultNodeId = newResultId }
                let proposedSnapshotId = StableIdAllocator.newSnapshotId ()
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = proposedSnapshotId
                        ParentId = Some baseSnapshot.Id
                        Functions =
                            baseSnapshot.Functions
                            |> List.map (fun functionEntity ->
                                if functionEntity.Id = target.Id then
                                    { functionEntity with Body = replacementBody }
                                else
                                    functionEntity) }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: _ -> rejected "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [] ->
                    match classifyReadiness proposedSnapshot with
                    | Error diagnostics -> Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        match createReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error error
                        | Ok receipt ->
                            match SemanticQuery.focusFunction baseSnapshot target.Id, SemanticQuery.focusFunction proposedSnapshot target.Id with
                            | Error error, _
                            | _, Error error -> Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                            | Ok baseFocus, Ok proposedFocus ->
                                let proposedTarget = proposedSnapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = target.Id)
                                let diff =
                                    { OwnerFunctionId = target.Id
                                      OldResultNodeId = target.Body.ResultNodeId
                                      NewResultNodeId = newResultId
                                      RemovedNodes =
                                        ValueGraph.collectNodes target.Body
                                        |> summarizeNodes baseSnapshot target
                                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                                      AddedNodes =
                                        ValueGraph.collectNodes proposedTarget.Body
                                        |> summarizeNodes proposedSnapshot proposedTarget }
                                let review =
                                    { RequestedChange = request
                                      Receipt = receipt
                                      Readiness = readiness
                                      ResultingChange = diff
                                      BaseFocus = baseFocus
                                      ProposedFocus = proposedFocus }
                                Ok(
                                    AgentExpressionStructuralChangeProposal
                                        { TransactionId = transactionId
                                          ProposedSnapshot = proposedSnapshot
                                          Review = review }
                                )

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentExpressionStructuralChangeProposal proposal)
        (policy: CommitPolicy)
        : AgentStructuralCommitResult =
        publishReviewedWithPolicy
            workspace
            proposal.TransactionId
            proposal.ProposedSnapshot
            proposal.Review.ResultingChange
            proposal.Review.Readiness
            proposal.Review.Receipt
            policy

    let commit workspace proposal = publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations
    let commitWithPolicy workspace proposal policy = publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentExpressionStructuralChangeProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
