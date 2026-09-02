namespace NyamNyam.Core

/// Ephemeral typed construction intent for bounded cross-domain pure expressions.
/// Child expressions are nested recipes; production graph-node IDs remain system-owned.
[<RequireQualifiedAccess>]
type PureExpressionRecipe =
    | Parameter of ParameterId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of PureExpressionRecipe
    | StringContains of PureExpressionRecipe * PureExpressionRecipe
    | Int64WrappingAdd of PureExpressionRecipe * PureExpressionRecipe
    | Int64WrappingSubtract of PureExpressionRecipe * PureExpressionRecipe
    | Int64WrappingMultiply of PureExpressionRecipe * PureExpressionRecipe
    | Int64Equal of PureExpressionRecipe * PureExpressionRecipe
    | Int64LessThan of PureExpressionRecipe * PureExpressionRecipe
    | Int64LessThanOrEqual of PureExpressionRecipe * PureExpressionRecipe
    | Int64GreaterThan of PureExpressionRecipe * PureExpressionRecipe
    | Int64GreaterThanOrEqual of PureExpressionRecipe * PureExpressionRecipe
    | Float64Add of PureExpressionRecipe * PureExpressionRecipe
    | Float64Subtract of PureExpressionRecipe * PureExpressionRecipe
    | Float64Multiply of PureExpressionRecipe * PureExpressionRecipe
    | Float64Divide of PureExpressionRecipe * PureExpressionRecipe
    | Float64Equal of PureExpressionRecipe * PureExpressionRecipe
    | Float64LessThan of PureExpressionRecipe * PureExpressionRecipe
    | Float64IsNaN of PureExpressionRecipe
    | Float64IsFinite of PureExpressionRecipe
    | Float64IsInfinite of PureExpressionRecipe
    | ListEmpty of itemType: TypeRef
    | ListConstruct of itemType: TypeRef * items: PureExpressionRecipe list
    | ListLength of PureExpressionRecipe
    | ListAppendItem of list: PureExpressionRecipe * item: PureExpressionRecipe
    | ListConcat of left: PureExpressionRecipe * right: PureExpressionRecipe
    | ListMap of source: PureExpressionRecipe * mapperFunctionId: FunctionId
    | ListFilter of source: PureExpressionRecipe * predicateFunctionId: FunctionId
    | ListAny of source: PureExpressionRecipe * predicateFunctionId: FunctionId
    | ListAll of source: PureExpressionRecipe * predicateFunctionId: FunctionId
    | ProductConstruct of typeId: TypeId * fields: (FieldId * PureExpressionRecipe) list
    | FieldAccess of source: PureExpressionRecipe * fieldId: FieldId
    | UnionConstruct of variantId: VariantId * fields: (FieldId * PureExpressionRecipe) list
    | OptionNone of itemType: TypeRef
    | OptionSome of PureExpressionRecipe
    | ResultOk of errorType: TypeRef * value: PureExpressionRecipe
    | ResultErr of okType: TypeRef * error: PureExpressionRecipe

type AgentReplaceFunctionBodyWithPureExpressionRecipe =
    { TargetFunctionId: FunctionId
      ExpectedBaseSnapshotId: SnapshotId
      ExpectedCurrentResultNodeId: GraphNodeId
      Replacement: PureExpressionRecipe }

type AgentPureExpressionStructuralChangeReview =
    { RequestedChange: AgentReplaceFunctionBodyWithPureExpressionRecipe
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: FunctionBodyReplacementDiff
      BaseFocus: FunctionFocusView
      ProposedFocus: FunctionFocusView }

type private PureExpressionStructuralProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentPureExpressionStructuralChangeReview }

type AgentPureExpressionStructuralChangeProposal =
    private
    | AgentPureExpressionStructuralChangeProposal of PureExpressionStructuralProposalState

[<RequireQualifiedAccess>]
module AgentPureExpressionStructuralChangeProposal =
    let private state (AgentPureExpressionStructuralChangeProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentPureExpressionStructuralWorkflow =
    open StructuralTransactionInternals

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private listType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private optionType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
    let private resultType okType errorType =
        TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])

    let private listItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) -> Some itemType
        | _ -> None

    let rec private recipeStats recipe =
        let fieldStats fields =
            fields |> List.map (snd >> recipeStats)

        let aggregateChildren childStats =
            let count = 1 + (childStats |> List.sumBy fst)
            let depth =
                match childStats with
                | [] -> 1
                | values -> 1 + (values |> List.map snd |> List.max)
            count, depth

        match recipe with
        | PureExpressionRecipe.Parameter _
        | PureExpressionRecipe.StringLiteral _
        | PureExpressionRecipe.BoolLiteral _
        | PureExpressionRecipe.UnitLiteral
        | PureExpressionRecipe.Int64Literal _
        | PureExpressionRecipe.Float64Literal _
        | PureExpressionRecipe.ListEmpty _
        | PureExpressionRecipe.OptionNone _ -> 1, 1
        | PureExpressionRecipe.StringIsEmpty child
        | PureExpressionRecipe.Float64IsNaN child
        | PureExpressionRecipe.Float64IsFinite child
        | PureExpressionRecipe.Float64IsInfinite child
        | PureExpressionRecipe.ListLength child
        | PureExpressionRecipe.ListMap(child, _)
        | PureExpressionRecipe.ListFilter(child, _)
        | PureExpressionRecipe.ListAny(child, _)
        | PureExpressionRecipe.ListAll(child, _)
        | PureExpressionRecipe.FieldAccess(child, _)
        | PureExpressionRecipe.OptionSome child
        | PureExpressionRecipe.ResultOk(_, child)
        | PureExpressionRecipe.ResultErr(_, child) ->
            let count, depth = recipeStats child
            1 + count, 1 + depth
        | PureExpressionRecipe.StringContains(left, right)
        | PureExpressionRecipe.Int64WrappingAdd(left, right)
        | PureExpressionRecipe.Int64WrappingSubtract(left, right)
        | PureExpressionRecipe.Int64WrappingMultiply(left, right)
        | PureExpressionRecipe.Int64Equal(left, right)
        | PureExpressionRecipe.Int64LessThan(left, right)
        | PureExpressionRecipe.Int64LessThanOrEqual(left, right)
        | PureExpressionRecipe.Int64GreaterThan(left, right)
        | PureExpressionRecipe.Int64GreaterThanOrEqual(left, right)
        | PureExpressionRecipe.Float64Add(left, right)
        | PureExpressionRecipe.Float64Subtract(left, right)
        | PureExpressionRecipe.Float64Multiply(left, right)
        | PureExpressionRecipe.Float64Divide(left, right)
        | PureExpressionRecipe.Float64Equal(left, right)
        | PureExpressionRecipe.Float64LessThan(left, right)
        | PureExpressionRecipe.ListAppendItem(left, right)
        | PureExpressionRecipe.ListConcat(left, right) ->
            let leftCount, leftDepth = recipeStats left
            let rightCount, rightDepth = recipeStats right
            1 + leftCount + rightCount, 1 + max leftDepth rightDepth
        | PureExpressionRecipe.ListConstruct(_, items) ->
            items |> List.map recipeStats |> aggregateChildren
        | PureExpressionRecipe.ProductConstruct(_, fields)
        | PureExpressionRecipe.UnionConstruct(_, fields) ->
            fields |> fieldStats |> aggregateChildren

    let private validateTypeRef (snapshot: Snapshot) typeRef =
        let typeIds = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id) |> Set.ofList
        let capabilityIds = snapshot.Capabilities |> List.map (fun capability -> capability.Id) |> Set.ofList

        let invalid message = diagnostic "PureExpressionInvalidTypeRef" message

        let rec validate typeRef =
            match typeRef with
            | TypeRef.Primitive _ -> Ok()
            | TypeRef.Named typeId when Set.contains typeId typeIds -> Ok()
            | TypeRef.Named typeId ->
                Error(invalid $"Type reference names unknown type '{StableId.value typeId}'.")
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

    let private inferType (snapshot: Snapshot) (target: SemanticFunction) recipe =
        let parametersById =
            target.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList

        let functionsById =
            snapshot.Functions
            |> List.map (fun functionEntity -> functionEntity.Id, functionEntity)
            |> Map.ofList

        let typesById =
            snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id, typeEntity) |> Map.ofList

        let variantsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product _ -> []
                | SemanticTypeKind.Union variants ->
                    variants |> List.map (fun variant -> variant.Id, (typeEntity.Id, variant)))
            |> Map.ofList

        let productFieldsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product fields ->
                    fields |> List.map (fun field -> field.Id, (typeEntity.Id, field))
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
            | Some callback when callback.Parameters.Length <> 1 ->
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

        let rec infer recipe =
            let unary role expected result child =
                match infer child with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "value" expected actual)
                | Ok _ -> Ok result

            let binary role expected result left right =
                match infer left with
                | Error error -> Error error
                | Ok actual when actual <> expected -> Error(typeMismatch role "left" expected actual)
                | Ok _ ->
                    match infer right with
                    | Error error -> Error error
                    | Ok actual when actual <> expected -> Error(typeMismatch role "right" expected actual)
                    | Ok _ -> Ok result

            let validateFieldBindings
                role
                (expectedFields: SemanticField list)
                (bindings: (FieldId * PureExpressionRecipe) list)
                =
                let duplicateField =
                    bindings
                    |> List.countBy fst
                    |> List.tryFind (fun (_, count) -> count > 1)

                match duplicateField with
                | Some(fieldId, _) ->
                    Error(
                        diagnostic
                            "PureExpressionDuplicateFieldBinding"
                            $"{role} binds field '{StableId.value fieldId}' more than once."
                    )
                | None ->
                    let expectedIds =
                        expectedFields
                        |> List.map (fun (field: SemanticField) -> field.Id)
                        |> Set.ofList
                    let actualIds = bindings |> List.map fst |> Set.ofList

                    if expectedIds <> actualIds then
                        Error(
                            diagnostic
                                "PureExpressionFieldBindingMismatch"
                                $"{role} must bind exactly the fields declared by its target."
                        )
                    else
                        let rec validateFields (fields: SemanticField list) =
                            match fields with
                            | [] -> Ok()
                            | field :: rest ->
                                let _, value =
                                    bindings |> List.find (fun (fieldId, _) -> fieldId = field.Id)

                                match infer value with
                                | Error error -> Error error
                                | Ok actual when actual <> field.Type ->
                                    Error(
                                        diagnostic
                                            "PureExpressionFieldBindingTypeMismatch"
                                            $"{role} field '{StableId.value field.Id}' requires {field.Type}, but the recipe produces {actual}."
                                    )
                                | Ok _ -> validateFields rest

                        validateFields expectedFields

            match recipe with
            | PureExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId parametersById with
                | Some parameter -> Ok parameter.Type
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionParameterOutOfScope"
                            $"Parameter '{StableId.value parameterId}' is not owned by the target function."
                    )
            | PureExpressionRecipe.StringLiteral _ -> Ok stringType
            | PureExpressionRecipe.BoolLiteral _ -> Ok boolType
            | PureExpressionRecipe.UnitLiteral -> Ok unitType
            | PureExpressionRecipe.Int64Literal _ -> Ok int64Type
            | PureExpressionRecipe.Float64Literal _ -> Ok float64Type
            | PureExpressionRecipe.StringIsEmpty value ->
                unary "StringIsEmpty" stringType boolType value
            | PureExpressionRecipe.StringContains(value, substring) ->
                binary "StringContains" stringType boolType value substring
            | PureExpressionRecipe.Int64WrappingAdd(left, right) ->
                binary "Int64WrappingAdd" int64Type int64Type left right
            | PureExpressionRecipe.Int64WrappingSubtract(left, right) ->
                binary "Int64WrappingSubtract" int64Type int64Type left right
            | PureExpressionRecipe.Int64WrappingMultiply(left, right) ->
                binary "Int64WrappingMultiply" int64Type int64Type left right
            | PureExpressionRecipe.Int64Equal(left, right) ->
                binary "Int64Equal" int64Type boolType left right
            | PureExpressionRecipe.Int64LessThan(left, right) ->
                binary "Int64LessThan" int64Type boolType left right
            | PureExpressionRecipe.Int64LessThanOrEqual(left, right) ->
                binary "Int64LessThanOrEqual" int64Type boolType left right
            | PureExpressionRecipe.Int64GreaterThan(left, right) ->
                binary "Int64GreaterThan" int64Type boolType left right
            | PureExpressionRecipe.Int64GreaterThanOrEqual(left, right) ->
                binary "Int64GreaterThanOrEqual" int64Type boolType left right
            | PureExpressionRecipe.Float64Add(left, right) ->
                binary "Float64Add" float64Type float64Type left right
            | PureExpressionRecipe.Float64Subtract(left, right) ->
                binary "Float64Subtract" float64Type float64Type left right
            | PureExpressionRecipe.Float64Multiply(left, right) ->
                binary "Float64Multiply" float64Type float64Type left right
            | PureExpressionRecipe.Float64Divide(left, right) ->
                binary "Float64Divide" float64Type float64Type left right
            | PureExpressionRecipe.Float64Equal(left, right) ->
                binary "Float64Equal" float64Type boolType left right
            | PureExpressionRecipe.Float64LessThan(left, right) ->
                binary "Float64LessThan" float64Type boolType left right
            | PureExpressionRecipe.Float64IsNaN value ->
                unary "Float64IsNaN" float64Type boolType value
            | PureExpressionRecipe.Float64IsFinite value ->
                unary "Float64IsFinite" float64Type boolType value
            | PureExpressionRecipe.Float64IsInfinite value ->
                unary "Float64IsInfinite" float64Type boolType value
            | PureExpressionRecipe.ListEmpty itemType ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() -> Ok(listType itemType)
            | PureExpressionRecipe.ListConstruct(itemType, items) ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() ->
                    let rec validateItems = function
                        | [] -> Ok(listType itemType)
                        | item :: rest ->
                            match infer item with
                            | Error error -> Error error
                            | Ok actual when actual <> itemType ->
                                Error(typeMismatch "ListConstruct" "item" itemType actual)
                            | Ok _ -> validateItems rest
                    validateItems items
            | PureExpressionRecipe.ListLength list ->
                match infer list with
                | Error error -> Error error
                | Ok actual ->
                    match listItemType actual with
                    | Some _ -> Ok int64Type
                    | None -> Error(listMismatch "ListLength" "value" actual)
            | PureExpressionRecipe.ListAppendItem(list, item) ->
                match infer list with
                | Error error -> Error error
                | Ok listResultType ->
                    match listItemType listResultType with
                    | None -> Error(listMismatch "ListAppendItem" "list" listResultType)
                    | Some itemType ->
                        match infer item with
                        | Error error -> Error error
                        | Ok actual when actual <> itemType ->
                            Error(typeMismatch "ListAppendItem" "item" itemType actual)
                        | Ok _ -> Ok listResultType
            | PureExpressionRecipe.ListConcat(left, right) ->
                match infer left with
                | Error error -> Error error
                | Ok leftType ->
                    match listItemType leftType with
                    | None -> Error(listMismatch "ListConcat" "left" leftType)
                    | Some _ ->
                        match infer right with
                        | Error error -> Error error
                        | Ok rightType when rightType <> leftType ->
                            Error(typeMismatch "ListConcat" "right" leftType rightType)
                        | Ok _ -> Ok leftType
            | PureExpressionRecipe.ListMap(source, mapperFunctionId) ->
                match infer source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListMap" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListMap" mapperFunctionId sourceItemType false with
                        | Error error -> Error error
                        | Ok mapper -> Ok(listType mapper.ResultType)
            | PureExpressionRecipe.ListFilter(source, predicateFunctionId) ->
                match infer source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFilter" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListFilter" predicateFunctionId sourceItemType true with
                        | Error error -> Error error
                        | Ok _ -> Ok sourceType
            | PureExpressionRecipe.ListAny(source, predicateFunctionId) ->
                match infer source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAny" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAny" predicateFunctionId sourceItemType true with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | PureExpressionRecipe.ListAll(source, predicateFunctionId) ->
                match infer source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAll" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAll" predicateFunctionId sourceItemType true with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | PureExpressionRecipe.ProductConstruct(typeId, fields) ->
                match Map.tryFind typeId typesById with
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionProductTypeNotFound"
                            $"Product construction references unknown type '{StableId.value typeId}'."
                    )
                | Some { Kind = SemanticTypeKind.Product expectedFields } ->
                    match validateFieldBindings "ProductConstruct" expectedFields fields with
                    | Error error -> Error error
                    | Ok() -> Ok(TypeRef.Named typeId)
                | Some _ ->
                    Error(
                        diagnostic
                            "PureExpressionProductTargetMismatch"
                            $"Product construction target '{StableId.value typeId}' is not a product type."
                    )
            | PureExpressionRecipe.FieldAccess(source, fieldId) ->
                match Map.tryFind fieldId productFieldsById with
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionProductFieldNotFound"
                            $"Field access references field '{StableId.value fieldId}' that is not owned by a product type."
                    )
                | Some(ownerTypeId, field) ->
                    match infer source with
                    | Error error -> Error error
                    | Ok(TypeRef.Named sourceTypeId) when sourceTypeId = ownerTypeId -> Ok field.Type
                    | Ok actual ->
                        Error(
                            diagnostic
                                "PureExpressionFieldAccessTypeMismatch"
                                $"Field access requires {TypeRef.Named ownerTypeId}, but the source recipe produces {actual}."
                        )
            | PureExpressionRecipe.UnionConstruct(variantId, fields) ->
                match Map.tryFind variantId variantsById with
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionUnionVariantNotFound"
                            $"Union construction references unknown variant '{StableId.value variantId}'."
                    )
                | Some(ownerTypeId, variant) ->
                    match validateFieldBindings "UnionConstruct" variant.Fields fields with
                    | Error error -> Error error
                    | Ok() -> Ok(TypeRef.Named ownerTypeId)
            | PureExpressionRecipe.OptionNone itemType ->
                match validateTypeRef snapshot itemType with
                | Error error -> Error error
                | Ok() -> Ok(optionType itemType)
            | PureExpressionRecipe.OptionSome value ->
                infer value |> Result.map optionType
            | PureExpressionRecipe.ResultOk(errorType, value) ->
                match validateTypeRef snapshot errorType with
                | Error error -> Error error
                | Ok() -> infer value |> Result.map (fun okType -> resultType okType errorType)
            | PureExpressionRecipe.ResultErr(okType, error) ->
                match validateTypeRef snapshot okType with
                | Error error -> Error error
                | Ok() -> infer error |> Result.map (resultType okType)

        infer recipe

    /// Validates target identity, bounds, scope and recipe types before production IDs are allocated.
    let private validatePreflight
        (request: AgentReplaceFunctionBodyWithPureExpressionRecipe)
        (baseSnapshot: Snapshot)
        : Result<SemanticFunction, TransactionDiagnostic> =
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            Error(
                diagnostic
                    "StructuralExpectedBaseMismatch"
                    "The structural request targets a different base snapshot."
            )
        else
            match baseSnapshot.Functions |> List.tryFind (fun functionEntity -> functionEntity.Id = request.TargetFunctionId) with
            | None ->
                Error(diagnostic "StructuralTargetNotFound" "The target function does not exist.")
            | Some target when target.Body.ResultNodeId <> request.ExpectedCurrentResultNodeId ->
                Error(
                    diagnostic
                        "StructuralExpectedResultMismatch"
                        "The target function result node changed after inspection."
                )
            | Some target ->
                let nodeCount, depth = recipeStats request.Replacement

                if depth > 32 then
                    Error(
                        diagnostic
                            "StructuralRecipeDepthLimitExceeded"
                            "The structural recipe is deeper than 32 nodes."
                    )
                elif nodeCount > 64 then
                    Error(
                        diagnostic
                            "StructuralRecipeNodeLimitExceeded"
                            "The structural recipe contains more than 64 nodes."
                    )
                else
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

    /// Materialization is deterministic left-to-right post-order with no caller-controlled IDs or CSE.
    let rec private materialize (snapshot: Snapshot) recipe =
        let unary kind child =
            let childNodes, childResult = materialize snapshot child
            let nodeId = StableIdAllocator.newGraphNodeId ()
            childNodes @ [ { Id = nodeId; Kind = kind childResult } ], nodeId

        let binary kind left right =
            let leftNodes, leftResult = materialize snapshot left
            let rightNodes, rightResult = materialize snapshot right
            let nodeId = StableIdAllocator.newGraphNodeId ()
            leftNodes
            @ rightNodes
            @ [ { Id = nodeId
                  Kind = kind (leftResult, rightResult) } ],
            nodeId

        let rec many = function
            | [] -> [], []
            | head :: tail ->
                let headNodes, headResult = materialize snapshot head
                let tailNodes, tailResults = many tail
                headNodes @ tailNodes, headResult :: tailResults

        let listCallback kind source callbackFunctionId =
            let sourceNodes, sourceResult = materialize snapshot source
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ [ { Id = nodeId; Kind = kind (sourceResult, callbackFunctionId) } ], nodeId

        let materializeFieldBindings
            (expectedFields: SemanticField list)
            (bindings: (FieldId * PureExpressionRecipe) list)
            =
            let rec loop (fields: SemanticField list) =
                match fields with
                | [] -> [], []
                | field :: rest ->
                    let _, value = bindings |> List.find (fun (fieldId, _) -> fieldId = field.Id)
                    let valueNodes, valueResult = materialize snapshot value
                    let restNodes, restBindings = loop rest
                    valueNodes @ restNodes,
                    { TargetFieldId = field.Id
                      ValueNodeId = valueResult } :: restBindings

            loop expectedFields

        match recipe with
        | PureExpressionRecipe.Parameter parameterId ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ], nodeId
        | PureExpressionRecipe.StringLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral value } ], nodeId
        | PureExpressionRecipe.BoolLiteral value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.BoolLiteral value } ], nodeId
        | PureExpressionRecipe.UnitLiteral ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.UnitLiteral } ], nodeId
        | PureExpressionRecipe.Int64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Int64Literal value } ], nodeId
        | PureExpressionRecipe.Float64Literal value ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.Float64Literal value } ], nodeId
        | PureExpressionRecipe.StringIsEmpty value ->
            unary SemanticNodeKind.StringIsEmpty value
        | PureExpressionRecipe.StringContains(value, substring) ->
            binary SemanticNodeKind.StringContains value substring
        | PureExpressionRecipe.Int64WrappingAdd(left, right) ->
            binary SemanticNodeKind.Int64WrappingAdd left right
        | PureExpressionRecipe.Int64WrappingSubtract(left, right) ->
            binary SemanticNodeKind.Int64WrappingSubtract left right
        | PureExpressionRecipe.Int64WrappingMultiply(left, right) ->
            binary SemanticNodeKind.Int64WrappingMultiply left right
        | PureExpressionRecipe.Int64Equal(left, right) ->
            binary SemanticNodeKind.Int64Equal left right
        | PureExpressionRecipe.Int64LessThan(left, right) ->
            binary SemanticNodeKind.Int64LessThan left right
        | PureExpressionRecipe.Int64LessThanOrEqual(left, right) ->
            binary SemanticNodeKind.Int64LessThanOrEqual left right
        | PureExpressionRecipe.Int64GreaterThan(left, right) ->
            binary SemanticNodeKind.Int64GreaterThan left right
        | PureExpressionRecipe.Int64GreaterThanOrEqual(left, right) ->
            binary SemanticNodeKind.Int64GreaterThanOrEqual left right
        | PureExpressionRecipe.Float64Add(left, right) ->
            binary SemanticNodeKind.Float64Add left right
        | PureExpressionRecipe.Float64Subtract(left, right) ->
            binary SemanticNodeKind.Float64Subtract left right
        | PureExpressionRecipe.Float64Multiply(left, right) ->
            binary SemanticNodeKind.Float64Multiply left right
        | PureExpressionRecipe.Float64Divide(left, right) ->
            binary SemanticNodeKind.Float64Divide left right
        | PureExpressionRecipe.Float64Equal(left, right) ->
            binary SemanticNodeKind.Float64Equal left right
        | PureExpressionRecipe.Float64LessThan(left, right) ->
            binary SemanticNodeKind.Float64LessThan left right
        | PureExpressionRecipe.Float64IsNaN value ->
            unary SemanticNodeKind.Float64IsNaN value
        | PureExpressionRecipe.Float64IsFinite value ->
            unary SemanticNodeKind.Float64IsFinite value
        | PureExpressionRecipe.Float64IsInfinite value ->
            unary SemanticNodeKind.Float64IsInfinite value
        | PureExpressionRecipe.ListEmpty itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.ListEmpty itemType } ], nodeId
        | PureExpressionRecipe.ListConstruct(itemType, items) ->
            let itemNodes, itemResults = many items
            let nodeId = StableIdAllocator.newGraphNodeId ()
            itemNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.ListConstruct(itemType, itemResults) } ], nodeId
        | PureExpressionRecipe.ListLength list ->
            unary SemanticNodeKind.ListLength list
        | PureExpressionRecipe.ListAppendItem(list, item) ->
            binary SemanticNodeKind.ListAppendItem list item
        | PureExpressionRecipe.ListConcat(left, right) ->
            binary SemanticNodeKind.ListConcat left right
        | PureExpressionRecipe.ListMap(source, mapperFunctionId) ->
            listCallback SemanticNodeKind.ListMap source mapperFunctionId
        | PureExpressionRecipe.ListFilter(source, predicateFunctionId) ->
            listCallback SemanticNodeKind.ListFilter source predicateFunctionId
        | PureExpressionRecipe.ListAny(source, predicateFunctionId) ->
            listCallback SemanticNodeKind.ListAny source predicateFunctionId
        | PureExpressionRecipe.ListAll(source, predicateFunctionId) ->
            listCallback SemanticNodeKind.ListAll source predicateFunctionId
        | PureExpressionRecipe.ProductConstruct(typeId, fields) ->
            let expectedFields =
                match snapshot.Types |> List.find (fun typeEntity -> typeEntity.Id = typeId) with
                | { Kind = SemanticTypeKind.Product expectedFields } -> expectedFields
                | _ -> failwith "Product recipe passed preflight with a non-product type."

            let fieldNodes, fieldBindings = materializeFieldBindings expectedFields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            fieldNodes
            @ [ { Id = nodeId
                  Kind = SemanticNodeKind.ProductConstruct(typeId, fieldBindings) } ],
            nodeId
        | PureExpressionRecipe.FieldAccess(source, fieldId) ->
            let sourceNodes, sourceResult = materialize snapshot source
            let nodeId = StableIdAllocator.newGraphNodeId ()
            sourceNodes @ [ { Id = nodeId; Kind = SemanticNodeKind.FieldAccess(sourceResult, fieldId) } ], nodeId
        | PureExpressionRecipe.UnionConstruct(variantId, fields) ->
            let variant =
                snapshot.Types
                |> List.collect (fun typeEntity ->
                    match typeEntity.Kind with
                    | SemanticTypeKind.Product _ -> []
                    | SemanticTypeKind.Union variants -> variants)
                |> List.find (fun variant -> variant.Id = variantId)

            let fieldNodes, fieldBindings = materializeFieldBindings variant.Fields fields
            let nodeId = StableIdAllocator.newGraphNodeId ()
            fieldNodes
            @ [ { Id = nodeId
                  Kind = SemanticNodeKind.UnionConstruct(variantId, fieldBindings) } ],
            nodeId
        | PureExpressionRecipe.OptionNone itemType ->
            let nodeId = StableIdAllocator.newGraphNodeId ()
            [ { Id = nodeId; Kind = SemanticNodeKind.OptionNone itemType } ], nodeId
        | PureExpressionRecipe.OptionSome value ->
            unary SemanticNodeKind.OptionSome value
        | PureExpressionRecipe.ResultOk(errorType, value) ->
            unary (fun valueNodeId -> SemanticNodeKind.ResultOk(errorType, valueNodeId)) value
        | PureExpressionRecipe.ResultErr(okType, error) ->
            unary (fun errorNodeId -> SemanticNodeKind.ResultErr(okType, errorNodeId)) error

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithPureExpressionRecipe)
        : Result<AgentPureExpressionStructuralChangeProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            match validatePreflight request baseSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Ok target ->
                let newNodes, newResultId = materialize baseSnapshot request.Replacement
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
                            match
                                SemanticQuery.focusFunction baseSnapshot target.Id,
                                SemanticQuery.focusFunction proposedSnapshot target.Id
                            with
                            | Error error, _
                            | _, Error error -> Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                            | Ok baseFocus, Ok proposedFocus ->
                                let proposedTarget =
                                    proposedSnapshot.Functions
                                    |> List.find (fun functionEntity -> functionEntity.Id = target.Id)

                                let diff =
                                    { OwnerFunctionId = target.Id
                                      OldResultNodeId = target.Body.ResultNodeId
                                      NewResultNodeId = newResultId
                                      RemovedNodes =
                                        ValueGraph.collectNodes target.Body
                                        |> summarizeNodes baseSnapshot target
                                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                                      AddedNodes =
                                        summarizeNodes
                                            proposedSnapshot
                                            proposedTarget
                                            proposedTarget.Body.Nodes }

                                let review =
                                    { RequestedChange = request
                                      Receipt = receipt
                                      Readiness = readiness
                                      ResultingChange = diff
                                      BaseFocus = baseFocus
                                      ProposedFocus = proposedFocus }

                                Ok(
                                    AgentPureExpressionStructuralChangeProposal
                                        { TransactionId = transactionId
                                          ProposedSnapshot = proposedSnapshot
                                          Review = review }
                                )

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentPureExpressionStructuralChangeProposal proposal)
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

    let commit workspace proposal =
        publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

    let commitWithPolicy workspace proposal policy =
        publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentPureExpressionStructuralChangeProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
