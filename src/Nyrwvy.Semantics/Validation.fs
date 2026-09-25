namespace Nyrwvy.Core

open System

[<Struct>]
type ValidationIssue =
    { Code: string
      Message: string }

[<RequireQualifiedAccess>]
module SnapshotValidator =
    let private nodeDependencies (node: SemanticNode): GraphNodeId list =
        match node.Kind with
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.OptionNone _ -> []
        | SemanticNodeKind.Int64ToString valueNodeId
        | SemanticNodeKind.StringParseInt64 valueNodeId
        | SemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            [ valueNodeId; substringNodeId ]
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
            [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.Float64IsNaN valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ListConstruct(_, itemNodeIds) -> itemNodeIds
        | SemanticNodeKind.ListLength listNodeId
        | SemanticNodeKind.ListMap(listNodeId, _)
        | SemanticNodeKind.ListFilter(listNodeId, _)
        | SemanticNodeKind.ListAny(listNodeId, _)
        | SemanticNodeKind.ListAll(listNodeId, _) -> [ listNodeId ]
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | SemanticNodeKind.ListFold(listNodeId, seedNodeId, _) -> [ listNodeId; seedNodeId ]
        | SemanticNodeKind.DirectCall(_, arguments) ->
            arguments |> List.map (fun argument -> argument.ValueNodeId)
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | SemanticNodeKind.ProductConstruct(_, fields)
        | SemanticNodeKind.UnionConstruct(_, fields) ->
            fields |> List.map (fun field -> field.ValueNodeId)
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | SemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ResultOk(_, valueNodeId) -> [ valueNodeId ]
        | SemanticNodeKind.ResultErr(_, errorNodeId) -> [ errorNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
            [ conditionNodeId; whenTrue.ResultNodeId; whenFalse.ResultNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
            inputNodeId :: (arms |> List.map (fun arm -> arm.Body.ResultNodeId))
        | SemanticNodeKind.Bind(inputNodeId, body) -> [ inputNodeId; body.Body.ResultNodeId ]
        | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
            [ inputNodeId; someArm.Body.ResultNodeId; noneBody.ResultNodeId ]
        | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
            [ inputNodeId; okArm.Body.ResultNodeId; errArm.Body.ResultNodeId ]

    let private directValueReferences (node: SemanticNode): GraphNodeId list =
        match node.Kind with
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.Int64ToString valueNodeId
        | SemanticNodeKind.StringParseInt64 valueNodeId
        | SemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            [ valueNodeId; substringNodeId ]
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
            [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.Float64IsNaN valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ListConstruct(_, itemNodeIds) -> itemNodeIds
        | SemanticNodeKind.ListLength listNodeId
        | SemanticNodeKind.ListMap(listNodeId, _)
        | SemanticNodeKind.ListFilter(listNodeId, _)
        | SemanticNodeKind.ListAny(listNodeId, _)
        | SemanticNodeKind.ListAll(listNodeId, _) -> [ listNodeId ]
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | SemanticNodeKind.DirectCall(_, arguments) ->
            arguments |> List.map (fun argument -> argument.ValueNodeId)
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | SemanticNodeKind.ProductConstruct(_, fields)
        | SemanticNodeKind.UnionConstruct(_, fields) ->
            fields |> List.map (fun field -> field.ValueNodeId)
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | SemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ResultOk(_, valueNodeId) -> [ valueNodeId ]
        | SemanticNodeKind.ResultErr(_, errorNodeId) -> [ errorNodeId ]
        | SemanticNodeKind.ListFold(listNodeId, seedNodeId, _) -> [ listNodeId; seedNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.Bind(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.OptionNone _ -> []

    let private hasDependencyCycle (nodesById: Map<GraphNodeId, SemanticNode>): bool =
        let acyclic = System.Collections.Generic.HashSet<GraphNodeId>()
        let rec visit path nodeId =
            if Set.contains nodeId path then
                true
            elif acyclic.Contains nodeId then
                false
            else
                match Map.tryFind nodeId nodesById with
                | None -> false
                | Some node ->
                    let nextPath = Set.add nodeId path
                    let hasCycle = nodeDependencies node |> List.exists (visit nextPath)
                    if not hasCycle then acyclic.Add nodeId |> ignore
                    hasCycle

        nodesById |> Map.exists (fun nodeId _ -> visit Set.empty nodeId)

    let private commonType (types: TypeRef option list): TypeRef option =
        match types with
        | [] -> None
        | Some first :: rest when rest |> List.forall (fun item -> item = Some first) -> Some first
        | Some _ :: _
        | None :: _ -> None

    let private validateUncached (snapshot: Snapshot): ValidationIssue list =
        let issues = ResizeArray<ValidationIssue>()
        let add code message = issues.Add({ Code = code; Message = message })

        let duplicateBy items code describe getId =
            items
            |> List.countBy getId
            |> List.filter (fun (_, count) -> count > 1)
            |> List.iter (fun (id, _) -> add code $"{describe} '{StableId.value id}' occurs more than once.")

        if snapshot.ParentId = Some snapshot.Id then
            add "SnapshotParentSelfReference" "A snapshot cannot name itself as its parent."

        let fieldEntries: (TypeId * VariantId option * SemanticField) list =
            snapshot.Types
            |> List.collect (fun owner ->
                match owner.Kind with
                | SemanticTypeKind.Product fields ->
                    fields |> List.map (fun field -> owner.Id, None, field)
                | SemanticTypeKind.Union variants ->
                    variants
                    |> List.collect (fun variant ->
                        variant.Fields
                        |> List.map (fun field -> owner.Id, Some variant.Id, field)))

        let variantEntries: (TypeId * SemanticVariant) list =
            snapshot.Types
            |> List.collect (fun owner ->
                match owner.Kind with
                | SemanticTypeKind.Product _ -> []
                | SemanticTypeKind.Union variants ->
                    variants |> List.map (fun variant -> owner.Id, variant))

        let capabilityOperationEntries: (CapabilityId * SemanticCapabilityOperation) list =
            snapshot.Capabilities
            |> List.collect (fun owner ->
                owner.Operations |> List.map (fun operation -> owner.Id, operation))

        let capabilityParameterEntries: (CapabilityOperationId * SemanticCapabilityParameter) list =
            capabilityOperationEntries
            |> List.collect (fun (_, operation) ->
                operation.Parameters |> List.map (fun parameter -> operation.Id, parameter))

        let parameterEntries: (FunctionId * SemanticParameter) list =
            snapshot.Functions
            |> List.collect (fun owner ->
                owner.Parameters |> List.map (fun parameter -> owner.Id, parameter))

        let nodeEntries: (FunctionId * SemanticNode) list =
            snapshot.Functions
            |> List.collect (fun owner ->
                ValueGraph.collectNodes owner.Body |> List.map (fun node -> owner.Id, node))

        let patternEntries: (FunctionId * PatternBinding) list =
            snapshot.Functions
            |> List.collect (fun owner ->
                SemanticFacts.collectPatternBindings owner.Body
                |> List.map (fun binding -> owner.Id, binding))

        duplicateBy snapshot.Types "DuplicateTypeId" "Type ID" (fun item -> item.Id)
        duplicateBy snapshot.Effects "DuplicateEffectId" "Effect ID" (fun item -> item.Id)
        duplicateBy snapshot.Capabilities "DuplicateCapabilityId" "Capability ID" (fun item -> item.Id)
        duplicateBy snapshot.Functions "DuplicateFunctionId" "Function ID" (fun item -> item.Id)
        duplicateBy fieldEntries "DuplicateFieldId" "Field ID" (fun (_, _, field) -> field.Id)
        duplicateBy variantEntries "DuplicateVariantId" "Variant ID" (fun (_, variant) -> variant.Id)
        duplicateBy
            capabilityOperationEntries
            "DuplicateCapabilityOperationId"
            "Capability operation ID"
            (fun (_, operation) -> operation.Id)
        duplicateBy
            capabilityParameterEntries
            "DuplicateCapabilityParameterId"
            "Capability parameter ID"
            (fun (_, parameter) -> parameter.Id)
        duplicateBy parameterEntries "DuplicateParameterId" "Parameter ID" (fun (_, parameter) -> parameter.Id)
        duplicateBy nodeEntries "DuplicateGraphNodeId" "Graph node ID" (fun (_, node) -> node.Id)
        duplicateBy
            patternEntries
            "DuplicatePatternBindingId"
            "Pattern binding ID"
            (fun (_, binding) -> binding.Id)
        duplicateBy snapshot.Obligations "DuplicateObligationId" "Obligation ID" (fun obligation -> obligation.Id)

        snapshot.Obligations
        |> List.filter (fun obligation -> obligation.Status = ObligationStatus.Open)
        |> List.countBy (fun obligation -> obligation.CallNodeId, obligation.ParameterId)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.iter (fun ((callNodeId, parameterId), _) ->
            add
                "DuplicateOpenMissingArgumentObligation"
                $"Call '{StableId.value callNodeId}' has more than one open obligation for parameter '{StableId.value parameterId}'.")

        snapshot.Types
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item -> add "BlankTypeDisplayName" $"Type '{StableId.value item.Id}' has a blank display name.")

        snapshot.Effects
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item -> add "BlankEffectDisplayName" $"Effect '{StableId.value item.Id}' has a blank display name.")

        snapshot.Capabilities
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item -> add "BlankCapabilityDisplayName" $"Capability '{StableId.value item.Id}' has a blank display name.")

        capabilityOperationEntries
        |> List.map snd
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item ->
            add
                "BlankCapabilityOperationDisplayName"
                $"Capability operation '{StableId.value item.Id}' has a blank display name.")

        capabilityParameterEntries
        |> List.map snd
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item ->
            add
                "BlankCapabilityParameterDisplayName"
                $"Capability parameter '{StableId.value item.Id}' has a blank display name.")

        fieldEntries
        |> List.map (fun (_, _, field) -> field)
        |> List.filter (fun field -> String.IsNullOrWhiteSpace field.DisplayName)
        |> List.iter (fun field -> add "BlankFieldDisplayName" $"Field '{StableId.value field.Id}' has a blank display name.")

        variantEntries
        |> List.map snd
        |> List.filter (fun variant -> String.IsNullOrWhiteSpace variant.DisplayName)
        |> List.iter (fun variant -> add "BlankVariantDisplayName" $"Variant '{StableId.value variant.Id}' has a blank display name.")

        snapshot.Functions
        |> List.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> List.iter (fun item -> add "BlankFunctionDisplayName" $"Function '{StableId.value item.Id}' has a blank display name.")

        parameterEntries
        |> List.map snd
        |> List.filter (fun parameter -> String.IsNullOrWhiteSpace parameter.DisplayName)
        |> List.iter (fun parameter ->
            add "BlankParameterDisplayName" $"Parameter '{StableId.value parameter.Id}' has a blank display name.")

        patternEntries
        |> List.map snd
        |> List.choose (fun binding ->
            binding.DisplayName |> Option.map (fun displayName -> binding.Id, displayName))
        |> List.filter (fun (_, displayName) -> String.IsNullOrWhiteSpace displayName)
        |> List.iter (fun (id, _) ->
            add "BlankPatternBindingDisplayName" $"Pattern binding '{StableId.value id}' has a blank display name.")

        let typeIds = snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList
        let effectIds = snapshot.Effects |> List.map (fun item -> item.Id) |> Set.ofList
        let capabilityIds = snapshot.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList
        let typesById = snapshot.Types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let functionsById = snapshot.Functions |> List.map (fun item -> item.Id, item) |> Map.ofList
        let variantsById =
            variantEntries
            |> List.map (fun (ownerId, variant) -> variant.Id, (ownerId, variant))
            |> Map.ofList
        let capabilityOperationsById: Map<CapabilityOperationId, CapabilityId * SemanticCapabilityOperation> =
            capabilityOperationEntries
            |> List.map (fun (ownerId, operation) -> operation.Id, (ownerId, operation))
            |> Map.ofList
        let productFieldsById: Map<FieldId, TypeId * SemanticField> =
            fieldEntries
            |> List.choose (fun (ownerId, variantId, field) ->
                match variantId with
                | None -> Some(field.Id, (ownerId, field))
                | Some _ -> None)
            |> Map.ofList
        let nodeOwners =
            nodeEntries
            |> List.map (fun (ownerId, node) -> node.Id, (ownerId, node))
            |> Map.ofList

        let rec validateTypeRef (context: string) (typeRef: TypeRef) =
            match typeRef with
            | TypeRef.Primitive _ -> ()
            | TypeRef.Named typeId ->
                if not (Set.contains typeId typeIds) then
                    add "UnknownNamedType" $"{context} references unknown type '{StableId.value typeId}'."
            | TypeRef.Capability capabilityId ->
                if not (Set.contains capabilityId capabilityIds) then
                    add
                        "UnknownCapabilityType"
                        $"{context} references unknown capability '{StableId.value capabilityId}'."
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
                if arguments.Length <> 1 then
                    add
                        "BuiltinGenericArityMismatch"
                        $"{context} uses Option with {arguments.Length} type arguments; exactly 1 is required."
                arguments |> List.iter (validateTypeRef context)
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
                if arguments.Length <> 2 then
                    add
                        "BuiltinGenericArityMismatch"
                        $"{context} uses Result with {arguments.Length} type arguments; exactly 2 are required."
                arguments |> List.iter (validateTypeRef context)
            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
                if arguments.Length <> 1 then
                    add
                        "BuiltinGenericArityMismatch"
                        $"{context} uses List with {arguments.Length} type arguments; exactly 1 is required."
                arguments |> List.iter (validateTypeRef context)

        for typeEntity in snapshot.Types do
            match typeEntity.Kind with
            | SemanticTypeKind.Product fields ->
                fields
                |> List.iter (fun field ->
                    validateTypeRef $"Field '{StableId.value field.Id}'" field.Type)
            | SemanticTypeKind.Union variants ->
                variants
                |> List.iter (fun variant ->
                    variant.Fields
                    |> List.iter (fun field ->
                        validateTypeRef $"Field '{StableId.value field.Id}'" field.Type))

        for _, operation in capabilityOperationEntries do
            if not (Set.contains operation.EffectId effectIds) then
                add
                    "UnknownCapabilityEffect"
                    $"Capability operation '{StableId.value operation.Id}' references unknown effect '{StableId.value operation.EffectId}'."

            operation.Parameters
            |> List.iter (fun parameter ->
                validateTypeRef $"Capability parameter '{StableId.value parameter.Id}'" parameter.Type)

            validateTypeRef $"Capability operation '{StableId.value operation.Id}' result" operation.ResultType
            operation.FailureType
            |> Option.iter (validateTypeRef $"Capability operation '{StableId.value operation.Id}' failure")

        for functionEntity in snapshot.Functions do
            functionEntity.RequiredEffects
            |> List.countBy id
            |> List.filter (fun (_, count) -> count > 1)
            |> List.iter (fun (effectId, _) ->
                add
                    "DuplicateFunctionEffectRequirement"
                    $"Function '{StableId.value functionEntity.Id}' lists effect '{StableId.value effectId}' more than once.")

            for effectId in functionEntity.RequiredEffects do
                if not (Set.contains effectId effectIds) then
                    add
                        "UnknownFunctionEffectRequirement"
                        $"Function '{StableId.value functionEntity.Id}' requires unknown effect '{StableId.value effectId}'."

            validateTypeRef $"Function '{StableId.value functionEntity.Id}' result" functionEntity.ResultType
            functionEntity.Parameters
            |> List.iter (fun parameter ->
                validateTypeRef $"Parameter '{StableId.value parameter.Id}'" parameter.Type)
            SemanticFacts.collectPatternBindings functionEntity.Body
            |> List.iter (fun binding ->
                validateTypeRef $"Pattern binding '{StableId.value binding.Id}'" binding.Type)

        let openObligationPairs =
            snapshot.Obligations
            |> List.choose (fun obligation ->
                match obligation.Status with
                | ObligationStatus.Open -> Some(obligation.CallNodeId, obligation.ParameterId)
                | ObligationStatus.Resolved _ -> None)
            |> Set.ofList

        let createNodeTypeResolver = SemanticFacts.createNodeTypeResolverFactory snapshot

        for functionEntity in snapshot.Functions do
            let allNodes = ValueGraph.collectNodes functionEntity.Body
            let nodesById = allNodes |> List.map (fun node -> node.Id, node) |> Map.ofList
            let parametersById =
                functionEntity.Parameters
                |> List.map (fun parameter -> parameter.Id, parameter)
                |> Map.ofList
            let requiredEffects = Set.ofList functionEntity.RequiredEffects
            let nodeType = createNodeTypeResolver functionEntity

            let requireNodeType code message expectedType nodeId =
                match nodeType nodeId with
                | Some actualType when actualType <> expectedType -> add code message
                | Some _
                | None -> ()

            let requireBinaryOperands code role expectedType nodeId leftNodeId rightNodeId =
                requireNodeType
                    code
                    $"{role} node '{StableId.value nodeId}' requires a {role} left operand."
                    expectedType
                    leftNodeId
                requireNodeType
                    code
                    $"{role} node '{StableId.value nodeId}' requires a {role} right operand."
                    expectedType
                    rightNodeId

            let requireInt64Operands nodeId leftNodeId rightNodeId =
                requireBinaryOperands
                    "Int64OperandTypeMismatch"
                    "Int64"
                    (TypeRef.Primitive PrimitiveType.Int64)
                    nodeId
                    leftNodeId
                    rightNodeId

            let requireFloat64Operands nodeId leftNodeId rightNodeId =
                requireBinaryOperands
                    "Float64OperandTypeMismatch"
                    "Float64"
                    (TypeRef.Primitive PrimitiveType.Float64)
                    nodeId
                    leftNodeId
                    rightNodeId

            let requireFloat64Operand nodeId valueNodeId =
                requireNodeType
                    "Float64OperandTypeMismatch"
                    $"Float64 node '{StableId.value nodeId}' requires a Float64 operand."
                    (TypeRef.Primitive PrimitiveType.Float64)
                    valueNodeId

            let validateListCallback
                (nodeId: GraphNodeId)
                (sourceListNodeId: GraphNodeId)
                (callbackFunctionId: FunctionId)
                (predicateResultRequired: bool)
                =
                let sourceItemType =
                    match nodeType sourceListNodeId with
                    | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) -> Some itemType
                    | Some _ ->
                        add
                            "ListHigherOrderSourceTypeMismatch"
                            $"Higher-order List node '{StableId.value nodeId}' requires List<T> input."
                        None
                    | None -> None

                match Map.tryFind callbackFunctionId functionsById with
                | None ->
                    add
                        "UnknownListCallbackFunction"
                        $"Higher-order List node '{StableId.value nodeId}' references unknown callback function '{StableId.value callbackFunctionId}'."
                | Some callback ->
                    if callback.Parameters.Length <> 1 then
                        add
                            "ListCallbackArityMismatch"
                            $"Higher-order List node '{StableId.value nodeId}' requires callback '{StableId.value callback.Id}' to have exactly one parameter."
                    else
                        match sourceItemType with
                        | Some itemType when callback.Parameters.Head.Type <> itemType ->
                            add
                                "ListCallbackParameterTypeMismatch"
                                $"Higher-order List node '{StableId.value nodeId}' callback parameter type does not match the source list item type."
                        | Some _
                        | None -> ()

                    if not (List.isEmpty callback.RequiredEffects) then
                        add
                            "ListCallbackEffectsNotAllowed"
                            $"Higher-order List node '{StableId.value nodeId}' requires a pure callback with no required effects."

                    if predicateResultRequired
                       && callback.ResultType <> TypeRef.Primitive PrimitiveType.Bool then
                        add
                            "ListPredicateResultTypeMismatch"
                            $"Higher-order List node '{StableId.value nodeId}' predicate callback must return Bool."

            let validateFieldBindings
                (context: string)
                (expectedFields: SemanticField list)
                (bindings: FieldValueBinding list)
                =
                bindings
                |> List.countBy (fun binding -> binding.TargetFieldId)
                |> List.filter (fun (_, count) -> count > 1)
                |> List.iter (fun (fieldId, _) ->
                    add
                        "DuplicateFieldBinding"
                        $"{context} binds field '{StableId.value fieldId}' more than once.")

                let expectedIds: Set<FieldId> =
                    expectedFields |> List.map (fun field -> field.Id) |> Set.ofList
                let actualIds: Set<FieldId> =
                    bindings |> List.map (fun binding -> binding.TargetFieldId) |> Set.ofList

                if expectedIds <> actualIds then
                    add "FieldBindingMismatch" $"{context} must bind exactly the fields declared by its target."

                for binding in bindings do
                    let targetField =
                        expectedFields
                        |> List.tryFind (fun field -> field.Id = binding.TargetFieldId)

                    match targetField, nodeType binding.ValueNodeId with
                    | Some field, Some valueType when field.Type <> valueType ->
                        add
                            "FieldValueTypeMismatch"
                            $"{context} binds a value with the wrong type to field '{StableId.value field.Id}'."
                    | Some _, Some _
                    | Some _, None
                    | None, _ -> ()

            let rec validateGraph
                (outerVisibleNodes: Set<GraphNodeId>)
                (visiblePatterns: Set<PatternBindingId>)
                (context: string)
                (graph: ValueGraph)
                =
                let localNodeIds = graph.Nodes |> List.map (fun node -> node.Id) |> Set.ofList
                let visibleNodes = Set.union outerVisibleNodes localNodeIds

                if not (Set.contains graph.ResultNodeId localNodeIds) then
                    add
                        "UnknownGraphResultNode"
                        $"{context} result node '{StableId.value graph.ResultNodeId}' is not owned directly by that graph."

                for node in graph.Nodes do
                    for referencedNodeId in directValueReferences node do
                        if not (Set.contains referencedNodeId visibleNodes) then
                            add
                                "ValueNodeOutOfScope"
                                $"Graph node '{StableId.value node.Id}' references value node '{StableId.value referencedNodeId}' outside its lexical value-graph scope."

                    match node.Kind with
                    | SemanticNodeKind.ParameterReference parameterId ->
                        if not (Map.containsKey parameterId parametersById) then
                            add
                                "UnknownParameterReference"
                                $"Graph node '{StableId.value node.Id}' references parameter '{StableId.value parameterId}' outside its owning function."
                    | SemanticNodeKind.PatternReference bindingId ->
                        if not (Set.contains bindingId visiblePatterns) then
                            add
                                "UnknownPatternReference"
                                $"Graph node '{StableId.value node.Id}' references pattern binding '{StableId.value bindingId}' outside its lexical binding scope."
                    | SemanticNodeKind.StringLiteral value ->
                        if isNull value then
                            add
                                "NullStringLiteral"
                                $"String literal node '{StableId.value node.Id}' has a null value."
                    | SemanticNodeKind.BoolLiteral _
                    | SemanticNodeKind.UnitLiteral
                    | SemanticNodeKind.Int64Literal _
                    | SemanticNodeKind.Float64Literal _ -> ()
                    | SemanticNodeKind.StringIsEmpty valueNodeId ->
                        requireNodeType
                            "StringIntrinsicTypeMismatch"
                            $"StringIsEmpty node '{StableId.value node.Id}' requires a String value."
                            (TypeRef.Primitive PrimitiveType.String)
                            valueNodeId
                    | SemanticNodeKind.Int64ToString valueNodeId ->
                        requireNodeType
                            "Int64IntrinsicTypeMismatch"
                            $"Int64ToString node '{StableId.value node.Id}' requires an Int64 value."
                            (TypeRef.Primitive PrimitiveType.Int64)
                            valueNodeId
                    | SemanticNodeKind.StringParseInt64 valueNodeId ->
                        requireNodeType
                            "StringIntrinsicTypeMismatch"
                            $"StringParseInt64 node '{StableId.value node.Id}' requires a String value."
                            (TypeRef.Primitive PrimitiveType.String)
                            valueNodeId
                    | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                        requireNodeType
                            "StringIntrinsicTypeMismatch"
                            $"StringContains node '{StableId.value node.Id}' requires a String value."
                            (TypeRef.Primitive PrimitiveType.String)
                            valueNodeId
                        requireNodeType
                            "StringIntrinsicTypeMismatch"
                            $"StringContains node '{StableId.value node.Id}' requires a String substring."
                            (TypeRef.Primitive PrimitiveType.String)
                            substringNodeId
                    | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
                        requireBinaryOperands
                            "StringIntrinsicTypeMismatch" "StringEqual" (TypeRef.Primitive PrimitiveType.String)
                            node.Id leftNodeId rightNodeId
                    | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
                        requireBinaryOperands
                            "StringIntrinsicTypeMismatch" "StringConcat" (TypeRef.Primitive PrimitiveType.String)
                            node.Id leftNodeId rightNodeId
                    | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
                        requireInt64Operands node.Id leftNodeId rightNodeId
                    | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
                    | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
                        requireFloat64Operands node.Id leftNodeId rightNodeId
                    | SemanticNodeKind.Float64IsNaN valueNodeId
                    | SemanticNodeKind.Float64IsFinite valueNodeId
                    | SemanticNodeKind.Float64IsInfinite valueNodeId ->
                        requireFloat64Operand node.Id valueNodeId
                    | SemanticNodeKind.ListEmpty itemType ->
                        validateTypeRef $"ListEmpty node '{StableId.value node.Id}' item type" itemType
                    | SemanticNodeKind.ListConstruct(itemType, itemNodeIds) ->
                        validateTypeRef $"ListConstruct node '{StableId.value node.Id}' item type" itemType
                        for itemNodeId in itemNodeIds do
                            requireNodeType
                                "ListConstructItemTypeMismatch"
                                $"ListConstruct node '{StableId.value node.Id}' contains an item whose type does not match its declared item type."
                                itemType
                                itemNodeId
                    | SemanticNodeKind.ListLength listNodeId ->
                        match nodeType listNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ _ ])) -> ()
                        | Some _ ->
                            add
                                "ListLengthInputTypeMismatch"
                                $"ListLength node '{StableId.value node.Id}' requires List<T> input."
                        | None -> ()
                    | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
                        match nodeType listNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) ->
                            match nodeType itemNodeId with
                            | Some actualItemType when actualItemType <> itemType ->
                                add
                                    "ListAppendItemTypeMismatch"
                                    $"ListAppendItem node '{StableId.value node.Id}' item does not match the list item type."
                            | Some _
                            | None -> ()
                        | Some _ ->
                            add
                                "ListAppendListTypeMismatch"
                                $"ListAppendItem node '{StableId.value node.Id}' requires List<T> as its list input."
                        | None -> ()
                    | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
                        match nodeType leftListNodeId, nodeType rightListNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ leftItemType ])),
                          Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ rightItemType ])) ->
                            if leftItemType <> rightItemType then
                                add
                                    "ListConcatItemTypeMismatch"
                                    $"ListConcat node '{StableId.value node.Id}' requires both lists to have the same item type."
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ _ ])), Some _ ->
                            add
                                "ListConcatRightTypeMismatch"
                                $"ListConcat node '{StableId.value node.Id}' requires List<T> as its right input."
                        | Some _, Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ _ ])) ->
                            add
                                "ListConcatLeftTypeMismatch"
                                $"ListConcat node '{StableId.value node.Id}' requires List<T> as its left input."
                        | Some _, Some _ ->
                            add
                                "ListConcatLeftTypeMismatch"
                                $"ListConcat node '{StableId.value node.Id}' requires List<T> as its left input."
                            add
                                "ListConcatRightTypeMismatch"
                                $"ListConcat node '{StableId.value node.Id}' requires List<T> as its right input."
                        | None, _
                        | _, None -> ()
                    | SemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId) ->
                        match nodeType sourceListNodeId, nodeType seedNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])), Some seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> add "UnknownListCallbackFunction" $"ListFold node '{StableId.value node.Id}' references an unknown folder."
                            | Some folder ->
                                if folder.Parameters.Length <> 2 then
                                    add "ListCallbackArityMismatch" "ListFold folder must have exactly two parameters (accumulator, item)."
                                else
                                    if folder.Parameters.Head.Type <> seedType || folder.Parameters.Tail.Head.Type <> itemType then
                                        add "ListCallbackParameterTypeMismatch" "ListFold folder input types must match seed and list item types."
                                if folder.ResultType <> seedType then
                                    add "ListCallbackResultTypeMismatch" "ListFold folder must return the seed type."
                                if not (List.isEmpty folder.RequiredEffects) then
                                    add "ListCallbackEffectsNotAllowed" "ListFold folder must be pure."
                        | Some _, Some _ -> add "ListHigherOrderSourceTypeMismatch" "ListFold requires List<T> input."
                        | _ -> ()
                    | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId) ->
                        validateListCallback node.Id sourceListNodeId mapperFunctionId false
                    | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId)
                    | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId)
                    | SemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId) ->
                        validateListCallback node.Id sourceListNodeId predicateFunctionId true
                    | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
                        match Map.tryFind targetFunctionId functionsById with
                        | None ->
                            add
                                "UnknownCallTarget"
                                $"Call node '{StableId.value node.Id}' references unknown function '{StableId.value targetFunctionId}'."
                        | Some targetFunction ->
                            arguments
                            |> List.countBy (fun argument -> argument.TargetParameterId)
                            |> List.filter (fun (_, count) -> count > 1)
                            |> List.iter (fun (parameterId, _) ->
                                add
                                    "DuplicateArgumentTarget"
                                    $"Call node '{StableId.value node.Id}' binds parameter '{StableId.value parameterId}' more than once.")

                            for argument in arguments do
                                let targetParameter =
                                    targetFunction.Parameters
                                    |> List.tryFind (fun parameter -> parameter.Id = argument.TargetParameterId)

                                if Option.isNone targetParameter then
                                    add
                                        "UnknownArgumentTargetParameter"
                                        $"Call node '{StableId.value node.Id}' targets parameter '{StableId.value argument.TargetParameterId}' not owned by its callee."

                                match targetParameter, nodeType argument.ValueNodeId with
                                | Some parameter, Some valueType when parameter.Type <> valueType ->
                                    add
                                        "ArgumentTypeMismatch"
                                        $"Call node '{StableId.value node.Id}' binds a value with the wrong type to parameter '{StableId.value parameter.Id}'."
                                | Some _, Some _
                                | Some _, None
                                | None, _ -> ()

                            for parameter in targetFunction.Parameters do
                                let hasBinding =
                                    arguments
                                    |> List.exists (fun argument -> argument.TargetParameterId = parameter.Id)
                                let hasOpenObligation =
                                    Set.contains (node.Id, parameter.Id) openObligationPairs

                                if not hasBinding && not hasOpenObligation then
                                    add
                                        "MissingArgument"
                                        $"Call node '{StableId.value node.Id}' has no binding or open obligation for parameter '{StableId.value parameter.Id}'."

                            for effectId in targetFunction.RequiredEffects do
                                if not (Set.contains effectId requiredEffects) then
                                    add
                                        "MissingEffectRequirement"
                                        $"Function '{StableId.value functionEntity.Id}' calls '{StableId.value targetFunction.Id}' but does not declare required effect '{StableId.value effectId}'."
                    | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, operationId, arguments) ->
                        match Map.tryFind operationId capabilityOperationsById with
                        | None ->
                            add
                                "UnknownCapabilityOperation"
                                $"Capability-invoke node '{StableId.value node.Id}' references unknown operation '{StableId.value operationId}'."
                        | Some(ownerCapabilityId, operation) ->
                            match nodeType capabilityValueNodeId with
                            | Some(TypeRef.Capability capabilityId) when capabilityId = ownerCapabilityId -> ()
                            | Some _ ->
                                add
                                    "CapabilityValueTypeMismatch"
                                    $"Capability-invoke node '{StableId.value node.Id}' does not receive the capability that owns operation '{StableId.value operationId}'."
                            | None -> ()

                            arguments
                            |> List.countBy (fun argument -> argument.TargetCapabilityParameterId)
                            |> List.filter (fun (_, count) -> count > 1)
                            |> List.iter (fun (parameterId, _) ->
                                add
                                    "DuplicateCapabilityArgumentTarget"
                                    $"Capability-invoke node '{StableId.value node.Id}' binds capability parameter '{StableId.value parameterId}' more than once.")

                            let expectedParameterIds =
                                operation.Parameters |> List.map (fun parameter -> parameter.Id) |> Set.ofList
                            let actualParameterIds =
                                arguments
                                |> List.map (fun argument -> argument.TargetCapabilityParameterId)
                                |> Set.ofList

                            if expectedParameterIds <> actualParameterIds then
                                add
                                    "CapabilityArgumentSetMismatch"
                                    $"Capability-invoke node '{StableId.value node.Id}' must bind exactly the parameters declared by operation '{StableId.value operationId}'."

                            for argument in arguments do
                                let targetParameter =
                                    operation.Parameters
                                    |> List.tryFind (fun parameter -> parameter.Id = argument.TargetCapabilityParameterId)

                                match targetParameter, nodeType argument.ValueNodeId with
                                | Some parameter, Some valueType when parameter.Type <> valueType ->
                                    add
                                        "CapabilityArgumentTypeMismatch"
                                        $"Capability-invoke node '{StableId.value node.Id}' binds a value with the wrong type to capability parameter '{StableId.value parameter.Id}'."
                                | Some _, Some _
                                | Some _, None
                                | None, _ -> ()

                            if not (Set.contains operation.EffectId requiredEffects) then
                                add
                                    "MissingEffectRequirement"
                                    $"Function '{StableId.value functionEntity.Id}' invokes capability operation '{StableId.value operation.Id}' but does not declare required effect '{StableId.value operation.EffectId}'."
                    | SemanticNodeKind.ProductConstruct(typeId, fields) ->
                        match Map.tryFind typeId typesById with
                        | Some { Kind = SemanticTypeKind.Product expectedFields } ->
                            validateFieldBindings $"Product node '{StableId.value node.Id}'" expectedFields fields
                        | Some _ ->
                            add
                                "InvalidProductConstructTarget"
                                $"Product node '{StableId.value node.Id}' targets a non-product type."
                        | None ->
                            add
                                "UnknownProductConstructType"
                                $"Product node '{StableId.value node.Id}' targets unknown type '{StableId.value typeId}'."
                    | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
                        match Map.tryFind fieldId productFieldsById with
                        | None ->
                            add
                                "UnknownProductField"
                                $"Field-access node '{StableId.value node.Id}' references a field that is not owned by a product type."
                        | Some(ownerTypeId, _) ->
                            match nodeType sourceNodeId with
                            | Some(TypeRef.Named sourceTypeId) when sourceTypeId = ownerTypeId -> ()
                            | Some _ ->
                                add
                                    "FieldAccessTypeMismatch"
                                    $"Field-access node '{StableId.value node.Id}' reads a field from the wrong source type."
                            | None -> ()
                    | SemanticNodeKind.UnionConstruct(variantId, fields) ->
                        match Map.tryFind variantId variantsById with
                        | None ->
                            add
                                "UnknownUnionVariant"
                                $"Union node '{StableId.value node.Id}' references unknown variant '{StableId.value variantId}'."
                        | Some(_, variant) ->
                            validateFieldBindings $"Union node '{StableId.value node.Id}'" variant.Fields fields
                    | SemanticNodeKind.OptionNone itemType ->
                        validateTypeRef $"Option.None node '{StableId.value node.Id}'" itemType
                    | SemanticNodeKind.OptionSome _ -> ()
                    | SemanticNodeKind.ResultOk(errorType, _) ->
                        validateTypeRef $"Result.Ok node '{StableId.value node.Id}' error type" errorType
                    | SemanticNodeKind.ResultErr(okType, _) ->
                        validateTypeRef $"Result.Err node '{StableId.value node.Id}' ok type" okType
                    | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                        match nodeType conditionNodeId with
                        | Some(TypeRef.Primitive PrimitiveType.Bool) -> ()
                        | Some _ ->
                            add
                                "ConditionalConditionTypeMismatch"
                                $"Conditional node '{StableId.value node.Id}' requires a Bool condition."
                        | None -> ()

                        if commonType [ nodeType whenTrue.ResultNodeId; nodeType whenFalse.ResultNodeId ] |> Option.isNone then
                            add
                                "ConditionalBranchTypeMismatch"
                                $"Conditional node '{StableId.value node.Id}' branches do not produce one common type."

                        validateGraph
                            visibleNodes
                            visiblePatterns
                            $"Conditional true branch of '{StableId.value node.Id}'"
                            whenTrue
                        validateGraph
                            visibleNodes
                            visiblePatterns
                            $"Conditional false branch of '{StableId.value node.Id}'"
                            whenFalse
                    | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                        match nodeType inputNodeId with
                        | Some(TypeRef.Named inputTypeId) ->
                            match Map.tryFind inputTypeId typesById with
                            | Some { Kind = SemanticTypeKind.Union variants } ->
                                arms
                                |> List.countBy (fun arm -> arm.VariantId)
                                |> List.filter (fun (_, count) -> count > 1)
                                |> List.iter (fun (variantId, _) ->
                                    add
                                        "DuplicateUnionMatchArm"
                                        $"Union match node '{StableId.value node.Id}' has duplicate arm for variant '{StableId.value variantId}'.")

                                let expectedVariantIds =
                                    variants |> List.map (fun variant -> variant.Id) |> Set.ofList
                                let actualVariantIds =
                                    arms |> List.map (fun arm -> arm.VariantId) |> Set.ofList

                                if expectedVariantIds <> actualVariantIds then
                                    add
                                        "NonExhaustiveUnionMatch"
                                        $"Union match node '{StableId.value node.Id}' must contain exactly one arm for every variant."

                                for arm in arms do
                                    match variants |> List.tryFind (fun variant -> variant.Id = arm.VariantId) with
                                    | None -> ()
                                    | Some variant ->
                                        let expectedFieldIds =
                                            variant.Fields |> List.map (fun field -> field.Id) |> Set.ofList
                                        let actualFieldIds =
                                            arm.FieldBindings
                                            |> List.map (fun item -> item.SourceFieldId)
                                            |> Set.ofList

                                        if expectedFieldIds <> actualFieldIds
                                           || arm.FieldBindings.Length <> actualFieldIds.Count then
                                            add
                                                "PatternFieldBindingMismatch"
                                                $"Union match arm '{StableId.value arm.VariantId}' must bind each variant field exactly once."

                                        for fieldBinding in arm.FieldBindings do
                                            match
                                                variant.Fields
                                                |> List.tryFind (fun field -> field.Id = fieldBinding.SourceFieldId)
                                            with
                                            | Some field when field.Type <> fieldBinding.Binding.Type ->
                                                add
                                                    "PatternBindingTypeMismatch"
                                                    $"Pattern binding '{StableId.value fieldBinding.Binding.Id}' does not match its variant field type."
                                            | Some _
                                            | None -> ()

                                        let armPatterns =
                                            arm.FieldBindings
                                            |> List.map (fun item -> item.Binding.Id)
                                            |> Set.ofList

                                        validateGraph
                                            visibleNodes
                                            (Set.union visiblePatterns armPatterns)
                                            $"Union match arm '{StableId.value arm.VariantId}'"
                                            arm.Body
                            | Some _
                            | None ->
                                add
                                    "MatchUnionInputTypeMismatch"
                                    $"Union match node '{StableId.value node.Id}' requires a declared union input."
                        | Some _ ->
                            add
                                "MatchUnionInputTypeMismatch"
                                $"Union match node '{StableId.value node.Id}' requires a declared union input."
                        | None -> ()

                        let armTypes = arms |> List.map (fun arm -> nodeType arm.Body.ResultNodeId)
                        if commonType armTypes |> Option.isNone then
                            add
                                "MatchArmTypeMismatch"
                                $"Union match node '{StableId.value node.Id}' arms do not produce one common type."
                    | SemanticNodeKind.Bind(inputNodeId, body) ->
                        requireNodeType
                            "PatternBindingTypeMismatch"
                            $"Bind binding '{StableId.value body.ValueBinding.Id}' does not match its input type."
                            body.ValueBinding.Type
                            inputNodeId
                        validateGraph
                            visibleNodes
                            (Set.add body.ValueBinding.Id visiblePatterns)
                            $"Bind body of '{StableId.value node.Id}'"
                            body.Body
                    | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                        match nodeType inputNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) ->
                            if someArm.ValueBinding.Type <> itemType then
                                add
                                    "PatternBindingTypeMismatch"
                                    $"Option match binding '{StableId.value someArm.ValueBinding.Id}' does not match the Option item type."
                        | Some _ ->
                            add
                                "MatchOptionInputTypeMismatch"
                                $"Option match node '{StableId.value node.Id}' requires Option<T> input."
                        | None -> ()

                        if commonType [ nodeType someArm.Body.ResultNodeId; nodeType noneBody.ResultNodeId ] |> Option.isNone then
                            add
                                "MatchArmTypeMismatch"
                                $"Option match node '{StableId.value node.Id}' arms do not produce one common type."

                        validateGraph
                            visibleNodes
                            (Set.add someArm.ValueBinding.Id visiblePatterns)
                            $"Option.Some arm of '{StableId.value node.Id}'"
                            someArm.Body
                        validateGraph
                            visibleNodes
                            visiblePatterns
                            $"Option.None arm of '{StableId.value node.Id}'"
                            noneBody
                    | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                        match nodeType inputNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                            if okArm.ValueBinding.Type <> okType then
                                add
                                    "PatternBindingTypeMismatch"
                                    $"Result.Ok binding '{StableId.value okArm.ValueBinding.Id}' does not match the Result ok type."
                            if errArm.ErrorBinding.Type <> errorType then
                                add
                                    "PatternBindingTypeMismatch"
                                    $"Result.Err binding '{StableId.value errArm.ErrorBinding.Id}' does not match the Result error type."
                        | Some _ ->
                            add
                                "MatchResultInputTypeMismatch"
                                $"Result match node '{StableId.value node.Id}' requires Result<T,E> input."
                        | None -> ()

                        if commonType [ nodeType okArm.Body.ResultNodeId; nodeType errArm.Body.ResultNodeId ] |> Option.isNone then
                            add
                                "MatchArmTypeMismatch"
                                $"Result match node '{StableId.value node.Id}' arms do not produce one common type."

                        validateGraph
                            visibleNodes
                            (Set.add okArm.ValueBinding.Id visiblePatterns)
                            $"Result.Ok arm of '{StableId.value node.Id}'"
                            okArm.Body
                        validateGraph
                            visibleNodes
                            (Set.add errArm.ErrorBinding.Id visiblePatterns)
                            $"Result.Err arm of '{StableId.value node.Id}'"
                            errArm.Body

            validateGraph
                Set.empty
                Set.empty
                $"Function '{StableId.value functionEntity.Id}' body"
                functionEntity.Body

            match nodeType functionEntity.Body.ResultNodeId with
            | Some resultType when resultType <> functionEntity.ResultType ->
                add
                    "FunctionResultTypeMismatch"
                    $"Function '{StableId.value functionEntity.Id}' result node type does not match its declared result type."
            | Some _
            | None -> ()

            if hasDependencyCycle nodesById then
                add
                    "GraphNodeDependencyCycle"
                    $"Function '{StableId.value functionEntity.Id}' contains a cycle between value-producing graph nodes."

        for obligation in snapshot.Obligations do
            match Map.tryFind obligation.CallNodeId nodeOwners with
            | None ->
                add
                    "InvalidObligationTarget"
                    $"Obligation '{StableId.value obligation.Id}' targets unknown graph node '{StableId.value obligation.CallNodeId}'."
            | Some(ownerFunctionId, callNode) ->
                match callNode.Kind with
                | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
                    let targetParameter =
                        Map.tryFind targetFunctionId functionsById
                        |> Option.bind (fun target ->
                            target.Parameters
                            |> List.tryFind (fun parameter -> parameter.Id = obligation.ParameterId))

                    if Option.isNone targetParameter then
                        add
                            "InvalidObligationParameter"
                            $"Obligation '{StableId.value obligation.Id}' targets a parameter not owned by the call's callee."

                    let matchingBindings =
                        arguments
                        |> List.filter (fun argument -> argument.TargetParameterId = obligation.ParameterId)

                    match obligation.Status with
                    | ObligationStatus.Open ->
                        if not (List.isEmpty matchingBindings) then
                            add
                                "SatisfiedOpenObligation"
                                $"Open obligation '{StableId.value obligation.Id}' already has a matching argument binding."
                    | ObligationStatus.Resolved resolution ->
                        let exactBinding =
                            matchingBindings
                            |> List.exists (fun argument -> argument.ValueNodeId = resolution.ValueNodeId)

                        if not exactBinding then
                            add
                                "InvalidObligationResolution"
                                $"Resolved obligation '{StableId.value obligation.Id}' does not match the call's argument binding."

                        match Map.tryFind resolution.ValueNodeId nodeOwners with
                        | Some(valueOwnerId, _) when valueOwnerId = ownerFunctionId -> ()
                        | Some _
                        | None ->
                            add
                                "InvalidObligationResolutionValue"
                                $"Resolved obligation '{StableId.value obligation.Id}' references a value node outside the call's owning function."
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
                | SemanticNodeKind.MatchResult _ ->
                    add
                        "InvalidObligationTarget"
                        $"Obligation '{StableId.value obligation.Id}' does not target a direct-call node."

        issues
        |> Seq.sortBy (fun issue -> issue.Code, issue.Message)
        |> List.ofSeq

    let private validateCached = SnapshotComputationScope.Memoize("validation", validateUncached)
    let validate (snapshot: Snapshot): ValidationIssue list = validateCached snapshot
