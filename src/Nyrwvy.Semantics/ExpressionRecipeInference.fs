namespace Nyrwvy.Core

/// Type inference and preflight checking of expression recipes.
module internal ExpressionRecipeInference =
    open StructuralTransactionInternals
    open ExpressionRecipeSupport

    /// A declared type's fields as seen through a reference with these type arguments.
    let private instantiateFields (owner: SemanticType) (arguments: TypeRef list) (fields: SemanticField list) =
        fields |> List.map (fun field -> { field with Type = TypeParameters.instantiate owner arguments field.Type })

    let internal inferTypeWithContext
        (index: SnapshotIndex)
        (owner: ExpressionOwnerContext)
        (cache: InferenceCache)
        (initialFrames: PatternTypeFrame list)
        (rootLabel: string option)
        recipe
        =
        let snapshot = index.Snapshot
        let functionsById = index.FunctionsById
        let typesById = index.TypesById
        let capabilityOperationsById = index.CapabilityOperationsById
        let variantsById = index.VariantsById
        let productFieldsById = index.ProductFieldsById
        let oneShotPayloadRequirements = lazy (OneShotPayloadRequirements.derive snapshot)

        let typeParameterNames = TypeParameters.names snapshot |> fun declared -> owner.TypeParameters |> List.fold (fun names (id, name) -> Map.add id name names) declared
        let ty = DiagnosticText.typeRefIn owner.TypeParameters snapshot
        let tyPair = DiagnosticText.typeRefPairIn owner.TypeParameters snapshot
        let callbackName = DiagnosticText.functionName
        let callbackSignature = DiagnosticText.signature snapshot

        let typeMismatch role side expected actual =
            let expected, produced = tyPair expected actual
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires {expected}, but the recipe produces {produced}."

        let listMismatch role side actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires List<T>, but the recipe produces {ty actual}."

        let mapMismatch role side actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires Map<K, V>, but the recipe produces {ty actual}."

        let setMismatch role side actual =
            diagnostic
                "PureExpressionOperandTypeMismatch"
                $"{role} {side} operand requires Set<T>, but the recipe produces {ty actual}."

        let setItemNotEquatable itemType =
            diagnostic
                "SetItemTypeNotEquatable"
                $"Set item type is not equatable: {EquatableTypes.explainIn typeParameterNames snapshot.Types itemType}"

        let mapKeyNotEquatable keyType =
            diagnostic
                "MapKeyTypeNotEquatable"
                $"Map key type is not equatable: {EquatableTypes.explainIn typeParameterNames snapshot.Types keyType}"

        /// The first of `effects` that the owner does not declare.
        let missingEffect effects =
            effects |> List.tryFind (fun effectId -> not (List.contains effectId owner.RequiredEffects))

        let missingEffectError subject effectId =
            diagnostic
                "ExpressionCallMissingEffectRequirement"
                $"{subject} requires effect {effectLabel snapshot effectId} not declared by {owner.Description}."

        /// Types context bindings with `inferValue` and returns the callback's unbound parameters with
        /// the type arguments that the context values determine for a generic callback.
        let validateCallbackContext role (callback: SemanticFunction) (context: (ParameterId * ExpressionRecipe) list) inferValue =
            let parametersById = callback.Parameters |> List.map (fun parameter -> parameter.Id, parameter) |> Map.ofList
            match tryFindDuplicate fst context with
            | Some parameterId ->
                Error(diagnostic "PureExpressionDuplicateCallArgument" $"{role} binds callback parameter '{StableId.value parameterId}' more than once.")
            | None ->
                context
                |> List.fold (fun state (parameterId, value) ->
                    state
                    |> Result.bind (fun substitution ->
                        match Map.tryFind parameterId parametersById with
                        | None ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentTargetNotFound"
                                    $"{role} context binds '{StableId.value parameterId}', which is not a parameter of callback {callbackName callback}: {callbackSignature callback}."
                            )
                        | Some parameter ->
                            match inferValue value with
                            | Error error -> Error error
                            | Ok actual ->
                                match TypeParameters.matchType substitution parameter.Type actual with
                                | Some extended -> Ok extended
                                | None ->
                                    Error(
                                        diagnostic
                                            "PureExpressionCallbackContextTypeMismatch"
                                            $"{role} context value for parameter {StableId.describe parameter.DisplayName parameterId} of callback {callbackName callback} has type {ty actual}, but the parameter expects {ty parameter.Type}."
                                    ))) (Ok Map.empty)
                |> Result.map (fun substitution ->
                    callback.Parameters |> List.filter (fun parameter -> not (context |> List.exists (fun (bound, _) -> bound = parameter.Id))),
                    substitution)

        let effectsFix (role: string) =
            if role.StartsWith "List" || role = "MapFromList" then " To run effects once per item, in order, use ListEffectFold instead (its folder takes the accumulator and the item)."
            else " Do the effectful work outside the callback and pass only pure values into it."

        /// Returns the callback and the type arguments that its context and item types determine.
        let validateListCallback role callbackFunctionId context sourceItemType predicateResultRequired inferValue =
            match Map.tryFind callbackFunctionId functionsById with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionCallbackNotFound"
                        $"{role} callback function '{StableId.value callbackFunctionId}' does not exist."
                )
            | Some callback ->
                match validateCallbackContext role callback context inferValue with
                | Error error -> Error error
                | Ok(unbound, _) when List.length unbound <> 1 ->
                    Error(
                        diagnostic
                            "PureExpressionCallbackArityMismatch"
                            $"{role} callback function {callbackName callback} must have exactly one parameter not bound by context; it has type {callbackSignature callback} and context binds {List.length context}."
                    )
                | Ok(unbound, substitution) ->
                    match TypeParameters.matchType substitution unbound.Head.Type sourceItemType with
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackInputTypeMismatch"
                                $"{role} callback function {callbackName callback} input type does not match the source list item type {ty sourceItemType}; the callback has type {callbackSignature callback}."
                        )
                    | Some substitution when predicateResultRequired && TypeParameters.substitute substitution callback.ResultType <> boolType ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackResultTypeMismatch"
                                $"{role} predicate {callbackName callback} must return Bool; it has type {callbackSignature callback}."
                        )
                    | Some _ when not (PureFunctions.isPure snapshot callback.Id) ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackEffectsNotAllowed"
                                $"{role} callback function {callbackName callback} must be pure, including its transitive calls and ambient task or cancellation behavior.{effectsFix role}"
                        )
                    | Some substitution -> Ok(callback, substitution)

        /// The OptionMap, OptionAndThen, ResultMap, ResultMapErr and ResultAndThen callback: exactly one
        /// parameter not bound by context, receiving `payloadType` (`payloadName` in messages), with no
        /// required effects. Returns the callback and its substituted result type.
        let validateCaseCallback role callbackFunctionId context payloadType (payloadName: string) inferValue =
            match Map.tryFind callbackFunctionId functionsById with
            | None ->
                Error(diagnostic "PureExpressionCallbackNotFound" $"{role} callback function '{StableId.value callbackFunctionId}' does not exist.")
            | Some callback ->
                match validateCallbackContext role callback context inferValue with
                | Error error -> Error error
                | Ok(unbound, _) when List.length unbound <> 1 ->
                    Error(
                        diagnostic
                            "PureExpressionCallbackArityMismatch"
                            $"{role} callback function {callbackName callback} must have exactly one parameter not bound by context; it has type {callbackSignature callback} and context binds {List.length context}."
                    )
                | Ok(unbound, substitution) ->
                    match TypeParameters.matchType substitution unbound.Head.Type payloadType with
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackInputTypeMismatch"
                                $"{role} callback function {callbackName callback} input type does not match the {payloadName} type {ty payloadType}; the callback has type {callbackSignature callback}."
                        )
                    | Some _ when not (PureFunctions.isPure snapshot callback.Id) ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackEffectsNotAllowed"
                                $"{role} callback function {callbackName callback} must be pure, including its transitive calls and ambient task or cancellation behavior."
                        )
                    | Some substitution -> Ok(callback, TypeParameters.substitute substitution callback.ResultType)

        let optionMismatch role actual =
            diagnostic "PureExpressionOperandTypeMismatch" $"{role} option operand requires Option<T>, but the recipe produces {ty actual}."

        let resultMismatch role actual =
            diagnostic "PureExpressionOperandTypeMismatch" $"{role} result operand requires Result<T, E>, but the recipe produces {ty actual}."

        let binderResultMismatch role (callback: SemanticFunction) expected =
            diagnostic
                "PureExpressionCallbackResultTypeMismatch"
                $"{role} callback function {callbackName callback} must return {expected}; it has type {callbackSignature callback}."

        /// The ListSortBy and ListMinBy callback: exactly two parameters not bound by context, both of the item type
        /// (left, right), returning Bool, with no required effects.
        let validateLessThanCallback role callbackFunctionId context sourceItemType inferValue =
            match Map.tryFind callbackFunctionId functionsById with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionCallbackNotFound"
                        $"{role} callback function '{StableId.value callbackFunctionId}' does not exist."
                )
            | Some callback ->
                match validateCallbackContext role callback context inferValue with
                | Error error -> Error error
                | Ok(unbound, _) when List.length unbound <> 2 ->
                    Error(
                        diagnostic
                            "PureExpressionCallbackArityMismatch"
                            $"{role} callback function {callbackName callback} must have exactly two parameters not bound by context (left, right); it has type {callbackSignature callback} and context binds {List.length context}."
                    )
                | Ok(unbound, substitution) ->
                    match
                        TypeParameters.matchFrom substitution [ unbound.Head.Type, sourceItemType; unbound.Tail.Head.Type, sourceItemType ]
                    with
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackInputTypeMismatch"
                                $"{role} callback function {callbackName callback} input types must both match the source list item type {ty sourceItemType}; the callback has type {callbackSignature callback}."
                        )
                    | Some substitution when TypeParameters.substitute substitution callback.ResultType <> boolType ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackResultTypeMismatch"
                                $"{role} less-than callback {callbackName callback} must return Bool; it has type {callbackSignature callback}."
                        )
                    | Some _ when not (PureFunctions.isPure snapshot callback.Id) ->
                        Error(
                            diagnostic
                                "PureExpressionCallbackEffectsNotAllowed"
                                $"{role} callback function {callbackName callback} must be pure, including its transitive calls and ambient task or cancellation behavior."
                        )
                    | Some substitution -> Ok(callback, substitution)

        // The innermost node whose own check produced the propagating diagnostic, to locate it.
        let failure: (TransactionDiagnostic * ExpressionRecipe) option ref = ref None
        let track recipe result =
            match result with
            | Error error ->
                match failure.Value with
                | Some(known, _) when known = error -> ()
                | _ -> failure.Value <- Some(error, recipe)
            | Ok _ -> ()
            result

        let rec infer frames recipe =
            match cache.TryValue(frames, recipe) with
            | Some value -> Ok value
            | None ->
                let result = track recipe (inferNode frames recipe)
                result |> Result.iter (fun value -> cache.AddValue(frames, recipe, value))
                result
        and inferCompletion frames recipe =
            match cache.TryCompletion(frames, recipe) with
            | Some completion -> Ok completion
            | None ->
                let result = track recipe (inferCompletionNode frames recipe)
                result |> Result.iter (fun completion -> cache.AddCompletion(frames, recipe, completion))
                result
        and inferNode frames recipe =
            // A fixed-signature operation, checked from its `OperationSignatures` entry.
            let fixedSignature () =
                let entry = fixedSignatureEntry recipe
                let role = string (System.Char.ToUpperInvariant entry.Kind[0]) + entry.Kind.Substring 1
                let fail failure =
                    match failure with
                    | OperationSignatures.OperandFailure.TypeMismatch(side, expected, actual) -> typeMismatch role side expected actual
                    | OperationSignatures.OperandFailure.ShapeMismatch(side, expected, actual) ->
                        diagnostic
                            "PureExpressionOperandTypeMismatch"
                            $"{role} {side} operand requires {expected}, but the recipe produces {ty actual}."
                    | OperationSignatures.OperandFailure.NotEquatable(constraintEntry, itemType) ->
                        diagnostic constraintEntry.Code (constraintEntry.Message + EquatableTypes.explainIn typeParameterNames snapshot.Types itemType)
                OperationSignatures.check
                    (infer frames)
                    (EquatableTypes.isEquatable snapshot.Types)
                    fail
                    entry
                    (recipeChildren recipe |> List.ofSeq)

            let validateFieldBindings role (expectedFields: SemanticField list) (bindings: (FieldId * ExpressionRecipe) list) =
                match tryFindDuplicate fst bindings with
                | Some fieldId ->
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
                                            (let expected, produced = tyPair field.Type actual in
                                             $"{role} field {StableId.describe field.DisplayName field.Id} requires {expected}, but the recipe produces {produced}.")
                                    )
                                | Ok _ -> validateFields rest
                        validateFields expectedFields

            /// The type arguments of a construction: the given ones, checked against the declared
            /// type parameters, or else the ones that matching the declared field types against the
            /// field values determines. A type parameter no field value determines must be given.
            let typeArgumentsFor
                role
                (owner: SemanticType)
                (declaredFields: SemanticField list)
                (given: TypeRef list)
                (bindings: (FieldId * ExpressionRecipe) list)
                : Result<TypeRef list, TransactionDiagnostic> =
                let parameters = owner.TypeParameters
                if List.isEmpty parameters && List.isEmpty given then Ok []
                elif not (List.isEmpty given) then
                    if given.Length <> parameters.Length then
                        Error(diagnostic "PureExpressionTypeArgumentArityMismatch" $"{role} of {owner.DisplayName} gives {given.Length} type arguments; {owner.DisplayName} declares {parameters.Length}.")
                    else
                        given
                        |> List.fold (fun state typeRef -> state |> Result.bind (fun () -> validateTypeRef index typeRef)) (Ok())
                        |> Result.map (fun () -> given)
                else
                    let pairs =
                        declaredFields
                        |> List.choose (fun field ->
                            bindings
                            |> List.tryFind (fun (fieldId, _) -> fieldId = field.Id)
                            |> Option.map (fun (_, value) -> field, value))
                    let rec derive substitution = function
                        | [] -> Ok substitution
                        | ((field: SemanticField), value) :: rest ->
                            match infer frames value with
                            | Error error -> Error error
                            | Ok actual ->
                                match TypeParameters.matchType substitution field.Type actual with
                                | Some next -> derive next rest
                                | None ->
                                    let expected, produced = tyPair (TypeParameters.substitute substitution field.Type) actual
                                    Error(diagnostic "PureExpressionFieldBindingTypeMismatch" $"{role} field {StableId.describe field.DisplayName field.Id} requires {expected}, but the recipe produces {produced}.")
                    derive Map.empty pairs
                    |> Result.bind (fun substitution ->
                        match parameters |> List.filter (fun (id, _) -> not (Map.containsKey id substitution)) with
                        | [] -> Ok(parameters |> List.map (fun (id, _) -> substitution[id]))
                        | missing ->
                            let names = missing |> List.map snd |> String.concat ", "
                            Error(diagnostic "PureExpressionTypeArgumentsRequired" $"{role} of {owner.DisplayName} cannot derive type parameter {names} from its field values; give typeArguments."))

            let validateCallArguments (callee: SemanticFunction) (bindings: (ParameterId * ExpressionRecipe) list) =
                match tryFindDuplicate fst bindings with
                | Some parameterId ->
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
                                $"DirectCall targets parameter '{StableId.value parameterId}' not owned by callee {callbackName callee}: {callbackSignature callee}."
                        )
                    | None ->
                        let actualIds = bindings |> List.map fst |> Set.ofList
                        match callee.Parameters |> List.tryFind (fun parameter -> not (Set.contains parameter.Id actualIds)) with
                        | Some parameter ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentMissing"
                                    $"DirectCall is missing required parameter {StableId.describe parameter.DisplayName parameter.Id} for callee {callbackName callee}: {callbackSignature callee}."
                            )
                        | None ->
                            // A call to the owner itself is a self call: it takes the owner's exact
                            // parameter types, like SelfCall, so a generic owner never calls itself at
                            // other type arguments (there is no polymorphic recursion, and a tail self
                            // call runs as a jump that reuses the parameter locals).
                            let isSelfCall = owner.SelfFunctionId = Some callee.Id
                            // Any other generic callee's type arguments follow from matching each parameter type.
                            let rec validateParameters substitution = function
                                | [] -> Ok substitution
                                | (parameter: SemanticParameter) :: rest ->
                                    let _, value = bindings |> List.find (fun (parameterId, _) -> parameterId = parameter.Id)
                                    match infer frames value with
                                    | Error error -> Error error
                                    | Ok actual when isSelfCall ->
                                        if actual = parameter.Type then validateParameters substitution rest
                                        else
                                            let expected, produced = tyPair parameter.Type actual
                                            Error(
                                                diagnostic
                                                    "PureExpressionCallArgumentTypeMismatch"
                                                    $"DirectCall parameter {StableId.describe parameter.DisplayName parameter.Id} of {owner.Description} requires {expected}, but the recipe produces {produced}. A function calls itself at its own parameter types; there is no polymorphic recursion."
                                            )
                                    | Ok actual ->
                                        match TypeParameters.matchType substitution parameter.Type actual with
                                        | Some extended -> validateParameters extended rest
                                        | None ->
                                            let expected, produced = tyPair (TypeParameters.substitute substitution parameter.Type) actual
                                            Error(
                                                diagnostic
                                                    "PureExpressionCallArgumentTypeMismatch"
                                                    $"DirectCall parameter {StableId.describe parameter.DisplayName parameter.Id} of callee {callbackName callee} requires {expected}, but the recipe produces {produced}."
                                            )
                            validateParameters Map.empty callee.Parameters

            let validateCapabilityArguments
                (operation: SemanticCapabilityOperation)
                (bindings: (CapabilityParameterId * ExpressionRecipe) list)
                =
                match tryFindDuplicate fst bindings with
                | Some parameterId ->
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
                                $"CapabilityInvoke must bind exactly the parameters declared by operation {StableId.describe operation.DisplayName operation.Id}."
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
                                            (let expected, produced = tyPair parameter.Type actual in
                                             $"CapabilityInvoke parameter {StableId.describe parameter.DisplayName parameter.Id} of operation {StableId.describe operation.DisplayName operation.Id} requires {expected}, but the recipe produces {produced}.")
                                    )
                                | Ok _ -> validateParameters rest
                        validateParameters operation.Parameters

            match recipe with
            | ExpressionRecipe.OriginalTarget ->
                match owner.OriginalTarget with
                | Some original -> Ok original.Type
                | None ->
                    Error(
                        diagnostic
                            "OriginalTargetOutsideTargetedReplacement"
                            "originalTarget is accepted only in the replacement of a targeted replacement."
                    )
            | ExpressionRecipe.Parameter parameterId ->
                match Map.tryFind parameterId owner.ParametersById with
                | Some parameter -> Ok parameter.Type
                | None ->
                    let foreign =
                        snapshot.Functions
                        |> List.tryPick (fun functionEntity ->
                            functionEntity.Parameters
                            |> List.tryFind (fun parameter -> parameter.Id = parameterId)
                            |> Option.map (fun parameter ->
                                $"Parameter {StableId.describe parameter.DisplayName parameterId} of function {StableId.describe functionEntity.DisplayName functionEntity.Id}"))
                        |> Option.defaultValue $"Parameter '{StableId.value parameterId}'"
                    Error(
                        diagnostic
                            "PureExpressionParameterOutOfScope"
                            $"{foreign} is not owned by {owner.Description}. {describeOwnerParameters owner}"
                    )
            | ExpressionRecipe.ParameterSlot index ->
                match owner.ParameterSlots with
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionParameterSlotUnavailable"
                            "ParameterSlot is available only while creating a new function."
                    )
                | Some _ when index < 0 ->
                    Error(
                        diagnostic
                            "PureExpressionParameterSlotOutOfRange"
                            $"Parameter slot {index} is outside the requested parameter list. {describeOwnerParameters owner}"
                    )
                | Some slots ->
                    match List.tryItem index slots with
                    | Some slot -> Ok slot.Type
                    | None ->
                        Error(
                            diagnostic
                                "PureExpressionParameterSlotOutOfRange"
                                $"Parameter slot {index} is outside the requested parameter list. {describeOwnerParameters owner}"
                        )
            | ExpressionRecipe.PatternReference reference -> resolvePatternType snapshot frames reference
            | ExpressionRecipe.StringLiteral value when isNull value ->
                Error(diagnostic "PureExpressionNullStringLiteral" "StringLiteral requires a non-null value.")
            | ExpressionRecipe.StringLiteral _ -> Ok stringType
            | ExpressionRecipe.BoolLiteral _ -> Ok boolType
            | ExpressionRecipe.UnitLiteral -> Ok unitType
            | ExpressionRecipe.Int64Literal _ -> Ok int64Type
            | ExpressionRecipe.Float64Literal _ -> Ok float64Type
            | ExpressionRecipe.BoolNot _
            | ExpressionRecipe.StringIsEmpty _
            | ExpressionRecipe.StringContains _
            | ExpressionRecipe.StringEqual _
            | ExpressionRecipe.StringLessThan _
            | ExpressionRecipe.StringConcat _
            | ExpressionRecipe.StringLength _
            | ExpressionRecipe.StringSlice _
            | ExpressionRecipe.StringCodePoints _
            | ExpressionRecipe.StringIndexOf _
            | ExpressionRecipe.StringLastIndexOf _
            | ExpressionRecipe.StringSpan _
            | ExpressionRecipe.StringSpanBack _
            | ExpressionRecipe.StringLastIndexOfBefore _
            | ExpressionRecipe.StringStartsWith _
            | ExpressionRecipe.StringEndsWith _
            | ExpressionRecipe.StringSplit _
            | ExpressionRecipe.StringSplitLines _
            | ExpressionRecipe.StringReplace _
            | ExpressionRecipe.StringTrim _
            | ExpressionRecipe.StringTrimStart _
            | ExpressionRecipe.StringTrimEnd _
            | ExpressionRecipe.StringJoin _
            | ExpressionRecipe.StringToLower _
            | ExpressionRecipe.StringToUpper _
            | ExpressionRecipe.StringUtf8ByteCount _
            | ExpressionRecipe.StringDecodeUtf8Lossy _
            | ExpressionRecipe.StringEncodeUtf8 _
            | ExpressionRecipe.StringFromCodePoint _
            | ExpressionRecipe.StringToCodePoint _
            | ExpressionRecipe.Int64ToString _
            | ExpressionRecipe.StringParseInt64 _
            | ExpressionRecipe.Int64ToHexString _
            | ExpressionRecipe.StringParseInt64Hex _
            | ExpressionRecipe.StringRepeat _
            | ExpressionRecipe.Float64ToString _
            | ExpressionRecipe.StringParseFloat64 _
            | ExpressionRecipe.Int64WrappingAdd _
            | ExpressionRecipe.Int64WrappingSubtract _
            | ExpressionRecipe.Int64WrappingMultiply _
            | ExpressionRecipe.Int64FloorDivide _
            | ExpressionRecipe.Int64FloorModulo _
            | ExpressionRecipe.Int64Min _
            | ExpressionRecipe.Int64Max _
            | ExpressionRecipe.Int64BitAnd _
            | ExpressionRecipe.Int64BitOr _
            | ExpressionRecipe.Int64BitXor _
            | ExpressionRecipe.Int64ShiftLeft _
            | ExpressionRecipe.Int64ShiftRight _
            | ExpressionRecipe.Int64CheckedAdd _
            | ExpressionRecipe.Int64CheckedSubtract _
            | ExpressionRecipe.Int64CheckedMultiply _
            | ExpressionRecipe.Int64WrappingAbs _
            | ExpressionRecipe.Int64BitNot _
            | ExpressionRecipe.Int64Equal _
            | ExpressionRecipe.Int64LessThan _
            | ExpressionRecipe.Int64LessThanOrEqual _
            | ExpressionRecipe.Int64GreaterThan _
            | ExpressionRecipe.Int64GreaterThanOrEqual _
            | ExpressionRecipe.Int64ToFloat64 _
            | ExpressionRecipe.Float64Add _
            | ExpressionRecipe.Float64Min _
            | ExpressionRecipe.Float64Max _
            | ExpressionRecipe.Float64Subtract _
            | ExpressionRecipe.Float64Multiply _
            | ExpressionRecipe.Float64Divide _
            | ExpressionRecipe.Float64FloorModulo _
            | ExpressionRecipe.Float64Equal _
            | ExpressionRecipe.Float64LessThan _
            | ExpressionRecipe.Float64IsNaN _
            | ExpressionRecipe.Float64IsFinite _
            | ExpressionRecipe.Float64IsInfinite _
            | ExpressionRecipe.Float64Sqrt _
            | ExpressionRecipe.Float64Abs _
            | ExpressionRecipe.Float64Negate _
            | ExpressionRecipe.Float64Floor _
            | ExpressionRecipe.Float64Ceiling _
            | ExpressionRecipe.Float64Truncate _
            | ExpressionRecipe.Float64RoundToInt64 _
            | ExpressionRecipe.Float64Round _
            | ExpressionRecipe.Float64FloorToInt64 _
            | ExpressionRecipe.Bits32FromInt64 _
            | ExpressionRecipe.Bits32ToInt64 _
            | ExpressionRecipe.Bits32Xor _
            | ExpressionRecipe.Bits32And _
            | ExpressionRecipe.Bits32Or _
            | ExpressionRecipe.Bits32Not _
            | ExpressionRecipe.Bits32Add _
            | ExpressionRecipe.Bits32RotateRight _
            | ExpressionRecipe.Bits32ShiftRight _
            | ExpressionRecipe.Bits32ShiftLeft _
            | ExpressionRecipe.BytesEmpty
            | ExpressionRecipe.BytesFill _
            | ExpressionRecipe.BytesLength _
            | ExpressionRecipe.BytesGet _
            | ExpressionRecipe.BytesSet _
            | ExpressionRecipe.BytesAppend _
            | ExpressionRecipe.BytesConcat _
            | ExpressionRecipe.BytesSlice _
            | ExpressionRecipe.BytesFromLatin1 _
            | ExpressionRecipe.BytesToLatin1 _
            | ExpressionRecipe.ListLength _
            | ExpressionRecipe.ListItemAt _
            | ExpressionRecipe.ListSlice _
            | ExpressionRecipe.ListSetAt _
            | ExpressionRecipe.ListInsertAt _
            | ExpressionRecipe.ListRemoveAt _
            | ExpressionRecipe.ListRange _
            | ExpressionRecipe.ListReverse _
            | ExpressionRecipe.ListLast _
            | ExpressionRecipe.ListDropLast _
            | ExpressionRecipe.ListAppendItem _
            | ExpressionRecipe.ListConcat _
            | ExpressionRecipe.CancellationRequested
            | ExpressionRecipe.ListIndexOf _
            | ExpressionRecipe.ListLastIndexOf _
            | ExpressionRecipe.ListContains _
            | ExpressionRecipe.MapInsert _
            | ExpressionRecipe.MapGet _
            | ExpressionRecipe.MapGetOrDefault _
            | ExpressionRecipe.MapContainsKey _
            | ExpressionRecipe.MapRemove _
            | ExpressionRecipe.MapSize _
            | ExpressionRecipe.MapKeys _
            | ExpressionRecipe.MapValues _
            | ExpressionRecipe.SetFromList _
            | ExpressionRecipe.SetInsert _
            | ExpressionRecipe.SetContains _
            | ExpressionRecipe.SetRemove _
            | ExpressionRecipe.SetUnion _
            | ExpressionRecipe.SetIntersection _
            | ExpressionRecipe.SetDifference _
            | ExpressionRecipe.SetIsSubset _
            | ExpressionRecipe.MapUnion _
            | ExpressionRecipe.SetSize _
            | ExpressionRecipe.SetToList _
            | ExpressionRecipe.OptionSome _
            | ExpressionRecipe.OptionGetOrDefault _
            | ExpressionRecipe.ResultGetOrDefault _
            | ExpressionRecipe.OptionOkOr _
            | ExpressionRecipe.ResultOkValue _
            | ExpressionRecipe.ResultErrValue _
            | ExpressionRecipe.OptionIsSome _
            | ExpressionRecipe.ResultIsOk _ -> fixedSignature ()
            | ExpressionRecipe.BoolAnd(left, right)
            | ExpressionRecipe.BoolOr(left, right) ->
                let role =
                    match recipe with
                    | ExpressionRecipe.BoolAnd _ -> "BoolAnd"
                    | _ -> "BoolOr"
                match infer frames left with
                | Error error -> Error error
                | Ok actual when actual <> boolType -> Error(typeMismatch role "left" boolType actual)
                | Ok _ ->
                    // The right operand is a nested branch: it may abort, otherwise it must be Bool.
                    match inferCompletion frames right with
                    | Error error -> Error error
                    | Ok(GraphCompletion.Normal actual) when actual <> boolType -> Error(typeMismatch role "right" boolType actual)
                    | Ok _ -> Ok boolType
            | ExpressionRecipe.ValueEqual(left, right) ->
                match infer frames left with
                | Error error -> Error error
                | Ok leftType ->
                    match infer frames right with
                    | Error error -> Error error
                    | Ok rightType when rightType <> leftType -> Error(typeMismatch "ValueEqual" "right" leftType rightType)
                    | Ok _ when not (EquatableTypes.isEquatable snapshot.Types leftType) ->
                        Error(
                            diagnostic
                                "ValueEqualTypeNotEquatable"
                                $"ValueEqual compares values of a type that is not equatable: {EquatableTypes.explainIn typeParameterNames snapshot.Types leftType}"
                        )
                    | Ok _ -> Ok boolType
            | ExpressionRecipe.ListEmpty itemType ->
                match validateTypeRef index itemType with
                | Error error -> Error error
                | Ok() -> Ok(listType itemType)
            | ExpressionRecipe.SetEmpty itemType ->
                match validateTypeRef index itemType with
                | Error error -> Error error
                | Ok() when not (EquatableTypes.isEquatable snapshot.Types itemType) -> Error(setItemNotEquatable itemType)
                | Ok() -> Ok(setOf itemType)
            | ExpressionRecipe.MapEmpty(keyType, valueType) ->
                let mapType = mapOf keyType valueType
                match validateTypeRef index keyType |> Result.bind (fun () -> validateTypeRef index valueType) with
                | Error error -> Error error
                | Ok() when not (EquatableTypes.isEquatable snapshot.Types keyType) -> Error(mapKeyNotEquatable keyType)
                | Ok() -> Ok mapType
            | ExpressionRecipe.ListConstruct(itemType, items) ->
                match validateTypeRef index itemType with
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
            | ExpressionRecipe.ListFold(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFold" "source" sourceType)
                    | Some itemType ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ListFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "ListFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"ListFold folder {callbackName folder} must have exactly two parameters not bound by context (accumulator, item); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                    let matched =
                                        TypeParameters.matchFrom substitution [ unbound.Head.Type, seedType; unbound.Tail.Head.Type, itemType ]
                                    match matched with
                                    | Some current when TypeParameters.substitute current folder.ResultType = seedType ->
                                        if PureFunctions.isPure snapshot folder.Id then Ok seedType
                                        else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"ListFold folder {callbackName folder} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                    | Some current when
                                        (match TypeParameters.substitute current folder.ResultType with
                                         | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ]) -> okType = seedType
                                         | _ -> false) ->
                                        // Same shape as ListEffectFold's Result mode: point there instead of a pass-through arm.
                                        Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListFold folder {callbackName folder} must return the accumulator type {ty seedType}; the folder has type {callbackSignature folder}. To stop at the first Err, use ListEffectFold with the same seed and folder: it accepts a pure folder returning Result<S, E> and yields Result<S, E>.")
                                    | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListFold folder {callbackName folder} must have type (accumulator, item) -> accumulator, here ({ty seedType}, {ty itemType}) -> {ty seedType}; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.ListScan(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListScan" "source" sourceType)
                    | Some itemType ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ListScan folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "ListScan" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"ListScan folder {callbackName folder} must have exactly two parameters not bound by context (accumulator, item); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                    let matched =
                                        TypeParameters.matchFrom substitution [ unbound.Head.Type, seedType; unbound.Tail.Head.Type, itemType ]
                                    match matched with
                                    | Some current when TypeParameters.substitute current folder.ResultType = seedType ->
                                        if PureFunctions.isPure snapshot folder.Id then Ok(listType seedType)
                                        else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"ListScan folder {callbackName folder} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                    | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListScan folder {callbackName folder} must have type (accumulator, item) -> accumulator, here ({ty seedType}, {ty itemType}) -> {ty seedType}; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.MapFold(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match mapEntryTypes sourceType with
                    | None -> Error(mapMismatch "MapFold" "map" sourceType)
                    | Some(keyType, valueType) ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"MapFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "MapFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 3 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"MapFold folder {callbackName folder} must have exactly three parameters not bound by context (accumulator, key, value); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                    let matched =
                                        TypeParameters.matchFrom substitution [ unbound[0].Type, seedType; unbound[1].Type, keyType; unbound[2].Type, valueType ]
                                    match matched with
                                    | Some current when TypeParameters.substitute current folder.ResultType = seedType ->
                                        if PureFunctions.isPure snapshot folder.Id then Ok seedType
                                        else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"MapFold folder {callbackName folder} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                    | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"MapFold folder {callbackName folder} must have type (accumulator, key, value) -> accumulator, here ({ty seedType}, {ty keyType}, {ty valueType}) -> {ty seedType}; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.MapFilter(source, keepFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match mapEntryTypes sourceType with
                    | None -> Error(mapMismatch "MapFilter" "map" sourceType)
                    | Some(keyType, valueType) ->
                        match Map.tryFind keepFunctionId functionsById with
                        | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"MapFilter callback '{StableId.value keepFunctionId}' does not exist.")
                        | Some keep ->
                            match validateCallbackContext "MapFilter" keep context (infer frames) with
                            | Error error -> Error error
                            | Ok(unbound, _) when unbound.Length <> 2 ->
                                Error(diagnostic "PureExpressionCallbackArityMismatch" $"MapFilter callback {callbackName keep} must have exactly two parameters not bound by context (key, value); it has type {callbackSignature keep} and context binds {List.length context}.")
                            | Ok(unbound, substitution) ->
                                let matched =
                                    TypeParameters.matchFrom substitution [ unbound[0].Type, keyType; unbound[1].Type, valueType ]
                                match matched with
                                | Some current when TypeParameters.substitute current keep.ResultType = boolType ->
                                    if PureFunctions.isPure snapshot keep.Id then Ok sourceType
                                    else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"MapFilter callback {callbackName keep} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"MapFilter callback {callbackName keep} must have type (key, value) -> Bool, here ({ty keyType}, {ty valueType}) -> Bool; the callback has type {callbackSignature keep}.")
            | ExpressionRecipe.MapMapValues(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match mapEntryTypes sourceType with
                    | None -> Error(mapMismatch "MapMapValues" "map" sourceType)
                    | Some(keyType, valueType) ->
                        match Map.tryFind mapperFunctionId functionsById with
                        | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"MapMapValues callback '{StableId.value mapperFunctionId}' does not exist.")
                        | Some mapper ->
                            match validateCallbackContext "MapMapValues" mapper context (infer frames) with
                            | Error error -> Error error
                            | Ok(unbound, _) when unbound.Length <> 2 ->
                                Error(diagnostic "PureExpressionCallbackArityMismatch" $"MapMapValues callback {callbackName mapper} must have exactly two parameters not bound by context (key, value); it has type {callbackSignature mapper} and context binds {List.length context}.")
                            | Ok(unbound, substitution) ->
                                let matched =
                                    TypeParameters.matchFrom substitution [ unbound[0].Type, keyType; unbound[1].Type, valueType ]
                                match matched with
                                | Some current ->
                                    if PureFunctions.isPure snapshot mapper.Id then Ok(mapOf keyType (TypeParameters.substitute current mapper.ResultType))
                                    else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"MapMapValues callback {callbackName mapper} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                | None -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"MapMapValues callback {callbackName mapper} must have type (key, value) -> U, here ({ty keyType}, {ty valueType}) -> U; the callback has type {callbackSignature mapper}.")
            | ExpressionRecipe.ListEffectFold(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListEffectFold" "source" sourceType)
                    | Some itemType ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ListEffectFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "ListEffectFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"ListEffectFold folder {callbackName folder} must have exactly two parameters not bound by context (accumulator, item); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                  match
                                      TypeParameters.matchFrom substitution [ unbound.Head.Type, seedType; unbound.Tail.Head.Type, itemType ]
                                  with
                                  | None -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListEffectFold folder {callbackName folder} must take (accumulator, item), here ({ty seedType}, {ty itemType}); the folder has type {callbackSignature folder}.")
                                  | Some current ->
                                    match missingEffect folder.RequiredEffects with
                                    | Some effectId -> Error(missingEffectError $"ListEffectFold folder {callbackName folder}" effectId)
                                    | None ->
                                        match TypeParameters.substitute current folder.ResultType with
                                        | resultType when resultType = seedType -> Ok seedType
                                        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ]) as resultType when okType = seedType -> Ok resultType
                                        | _ ->
                                            Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListEffectFold folder {callbackName folder} must return the accumulator type S or Result<S, E>, here {ty seedType} or Result<{ty seedType}, E>; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.UseResource(acquire, releaseCapability, releaseOperationId, bodyFunctionId, context) ->
                let fail code message = Error(diagnostic code message)
                match infer frames acquire with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ resourceType; errorType ])) ->
                    match Map.tryFind bodyFunctionId functionsById, Map.tryFind releaseOperationId capabilityOperationsById with
                    | None, _ -> fail "PureExpressionCallbackNotFound" $"UseResource body '{StableId.value bodyFunctionId}' does not exist."
                    | _, None -> fail "ExpressionCapabilityOperationNotFound" $"UseResource references unknown release operation '{StableId.value releaseOperationId}'."
                    | Some body, Some(ownerCapabilityId, operation) ->
                        match validateCallbackContext "UseResource" body context (infer frames), infer frames releaseCapability with
                        | Error error, _
                        | _, Error error -> Error error
                        | Ok(unbound, substitution), Ok receiverType ->
                          match (match unbound with [ resource ] -> TypeParameters.matchType substitution resource.Type resourceType | _ -> None) with
                          | None ->
                            fail "UseResourceBodyMismatch" $"UseResource body {callbackName body} must have exactly one parameter not bound by context, of the acquired resource type {ty resourceType}; the body has type {callbackSignature body}."
                          | Some _ when receiverType <> TypeRef.Capability ownerCapabilityId ->
                            fail "UseResourceReleaseReceiverMismatch" "UseResource release capability value does not own the release operation."
                          | Some bodySubstitution ->
                            let bodyResult = TypeParameters.substitute bodySubstitution body.ResultType
                            let releaseReturnsResult = operation.ResultType = resultType unitType errorType
                            match bodyResult, missingEffect (body.RequiredEffects @ [ operation.EffectId ]) with
                            | _, Some effectId -> Error(missingEffectError "UseResource" effectId)
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; bodyError ]), None when bodyError <> errorType ->
                                fail "UseResourceResultMismatch" $"UseResource body {callbackName body} must return Result<T, E> with the acquisition error type E = {ty errorType}; it returns {ty bodyResult}."
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]), None ->
                                match operation.Parameters with
                                | [ parameter ] when parameter.Type = resourceType && (operation.ResultType = unitType || releaseReturnsResult) ->
                                    Ok bodyResult
                                | _ ->
                                    fail "UseResourceReleaseSignatureMismatch" "The release operation must take exactly the resource and return Unit or Result<Unit, E>."
                            | _ -> fail "UseResourceResultMismatch" $"UseResource body {callbackName body} must return Result<T, E> with the acquisition error type E = {ty errorType}; it returns {ty bodyResult}."
                | Ok actual -> fail "UseResourceAcquireTypeMismatch" $"UseResource acquisition must produce Result<R, E>, not {ty actual}."
            | ExpressionRecipe.ExceptionCatch(bodyId, bodyContext, handlerId, handlerContext) ->
                let callback role id context inputType =
                    match Map.tryFind id functionsById with
                    | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ExceptionCatch {role} function does not exist.")
                    | Some callback ->
                        validateCallbackContext "ExceptionCatch" callback context (infer frames)
                        |> Result.bind (fun (unbound, substitution) ->
                            match (match unbound with [ input ] -> TypeParameters.matchType substitution input.Type inputType | _ -> None), missingEffect callback.RequiredEffects with
                            | None, _ -> Error(diagnostic "ExceptionCatchCallbackMismatch" $"ExceptionCatch {role} must have one unbound input of type {ty inputType}.")
                            | _, Some effectId -> Error(missingEffectError "ExceptionCatch" effectId)
                            | Some current, None -> Ok(TypeParameters.substitute current callback.ResultType))
                match callback "body" bodyId bodyContext (TypeRef.Primitive PrimitiveType.Unit), callback "handler" handlerId handlerContext (TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) with
                | Ok bodyResult, Ok handlerResult when bodyResult = handlerResult ->
                    match bodyResult with
                    | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) -> Ok bodyResult
                    | _ -> Error(diagnostic "ExceptionCatchResultMismatch" "ExceptionCatch callbacks must return the same Result<T, E>.")
                | Ok _, Ok _ -> Error(diagnostic "ExceptionCatchResultMismatch" "ExceptionCatch callbacks must return the same Result<T, E>.")
                | Error error, _ | _, Error error -> Error error
            | ExpressionRecipe.ExceptionIsCancellation value ->
                match infer frames value with
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) -> Ok boolType
                | Ok _ -> Error(diagnostic "ExceptionValueTypeMismatch" "ExceptionIsCancellation requires Exception.")
                | Error error -> Error error
            | ExpressionRecipe.ExceptionMessage value ->
                match infer frames value with
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) -> Ok stringType
                | Ok _ -> Error(diagnostic "ExceptionValueTypeMismatch" "ExceptionMessage requires Exception.")
                | Error error -> Error error
            | ExpressionRecipe.ExceptionRethrow(value, resultType) ->
                match validateTypeRef index resultType, infer frames value with
                | Ok (), Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])) -> Ok resultType
                | Ok (), Ok _ -> Error(diagnostic "ExceptionValueTypeMismatch" "ExceptionRethrow requires Exception.")
                | Error error, _ | _, Error error -> Error error
            | ExpressionRecipe.CancellationShield(bodyFunctionId, context) ->
                match Map.tryFind bodyFunctionId functionsById with
                | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"CancellationShield body '{StableId.value bodyFunctionId}' does not exist.")
                | Some body ->
                    match validateCallbackContext "CancellationShield" body context (infer frames) with
                    | Error error -> Error error
                    | Ok(unbound, substitution) ->
                        let scopeType = TypeRef.Primitive PrimitiveType.Unit
                        match (match unbound with [ scope ] -> TypeParameters.matchType substitution scope.Type scopeType | _ -> None),
                              missingEffect body.RequiredEffects with
                        | None, _ -> Error(diagnostic "CancellationShieldBodyMismatch" $"CancellationShield body {callbackName body} must have exactly one parameter not bound by context, of type Unit; the body has type {callbackSignature body}.")
                        | _, Some effectId ->
                            Error(missingEffectError $"CancellationShield body {StableId.describe body.DisplayName body.Id}" effectId)
                        | Some current, None ->
                            match TypeParameters.substitute current body.ResultType with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result -> Ok result
                            | other -> Error(diagnostic "CancellationShieldResultMismatch" $"CancellationShield body {callbackName body} must return Result<T, E>, not {ty other}.")
            | ExpressionRecipe.TaskScope(bodyFunctionId, context) ->
                match Map.tryFind bodyFunctionId functionsById with
                | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"TaskScope body '{StableId.value bodyFunctionId}' does not exist.")
                | Some body ->
                    match validateCallbackContext "TaskScope" body context (infer frames) with
                    | Error error -> Error error
                    | Ok(unbound, substitution) ->
                        let scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
                        match (match unbound with [ scope ] -> TypeParameters.matchType substitution scope.Type scopeType | _ -> None),
                              missingEffect body.RequiredEffects with
                        | None, _ -> Error(diagnostic "TaskScopeBodyMismatch" $"TaskScope body {callbackName body} must have exactly one parameter not bound by context, of type TaskScope; the body has type {callbackSignature body}.")
                        | _, Some effectId ->
                            Error(missingEffectError $"TaskScope body {StableId.describe body.DisplayName body.Id}" effectId)
                        | Some current, None ->
                            match TypeParameters.substitute current body.ResultType with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result -> Ok result
                            | other -> Error(diagnostic "TaskScopeResultMismatch" $"TaskScope body {callbackName body} must return Result<T, E>, not {ty other}.")
            | ExpressionRecipe.TaskStart(scope, workerFunctionId, context, item) ->
                match infer frames scope, infer frames item with
                | Error error, _
                | _, Error error -> Error error
                | Ok scopeType, _ when scopeType <> TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) ->
                    Error(diagnostic "TaskStartScopeTypeMismatch" $"TaskStart scope must be a TaskScope, not {ty scopeType}.")
                | Ok _, Ok itemType ->
                    match Map.tryFind workerFunctionId functionsById with
                    | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"TaskStart worker '{StableId.value workerFunctionId}' does not exist.")
                    | Some worker ->
                        match validateCallbackContext "TaskStart" worker context (infer frames) with
                        | Error error -> Error error
                        | Ok(unbound, substitution) ->
                            let matched = match unbound with [ parameter ] -> TypeParameters.matchType substitution parameter.Type itemType | _ -> None
                            match matched, missingEffect worker.RequiredEffects with
                            | None, _ ->
                                Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"TaskStart worker {callbackName worker} must have exactly one parameter not bound by context, of the item type {ty itemType}; the worker has type {callbackSignature worker}.")
                            | _, Some effectId ->
                                Error(missingEffectError $"TaskStart worker {StableId.describe worker.DisplayName worker.Id}" effectId)
                            | Some current, None ->
                                match TypeParameters.substitute current worker.ResultType with
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) as result ->
                                    Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ result ]))
                                | other -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"TaskStart worker {callbackName worker} must return Result<U, E>, not {ty other}.")
            | ExpressionRecipe.TaskWait(tasks, _, limit) ->
                let limitType =
                    match limit |> Option.map (infer frames) with
                    | None -> Ok()
                    | Some(Error error) -> Error error
                    | Some(Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))) -> Ok()
                    | Some(Ok actual) -> Error(diagnostic "TaskWaitLimitTypeMismatch" $"TaskWait limit requires Option<Int64>, not {ty actual}.")
                match infer frames tasks, limitType with
                | Error error, _
                | _, Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ result ]) ])), Ok() ->
                    Ok(listType (optionType result))
                | Ok actual, Ok() -> Error(diagnostic "TaskWaitInputTypeMismatch" $"TaskWait requires List<Task<T>>, not {ty actual}.")
            | ExpressionRecipe.OneShotCreate(scope, valueType) ->
                match infer frames scope, validateTypeRef index valueType with
                | Error error, _ | _, Error error -> Error error
                | Ok actual, _ when actual <> TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> Error(diagnostic "OneShotScopeTypeMismatch" "OneShotCreate requires TaskScope.")
                | _, _ when TaskTypes.mentionsScope valueType -> Error(diagnostic "OneShotScopePayloadUnsupported" "A OneShot payload cannot contain TaskScope.")
                | _ -> Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ valueType ]))
            | ExpressionRecipe.OneShotComplete(cell, value) ->
                match infer frames cell, infer frames value with
                | Error error, _ | _, Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ expected ])), Ok actual when expected = actual -> Ok boolType
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ _ ])), _ -> Error(diagnostic "OneShotValueTypeMismatch" "OneShotComplete payload must match the cell's exact type.")
                | _ -> Error(diagnostic "OneShotCellTypeMismatch" "OneShotComplete requires OneShot<T>.")
            | ExpressionRecipe.OneShotWait(cell, limit) ->
                let limitType = match limit |> Option.map (infer frames) with
                                | None | Some(Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))) -> Ok()
                                | Some(Error error) -> Error error
                                | _ -> Error(diagnostic "OneShotLimitTypeMismatch" "OneShotWait requires Option<Int64> limit.")
                match infer frames cell, limitType with
                | Error error, _ | _, Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ valueType ])), Ok() -> Ok(optionType valueType)
                | _ -> Error(diagnostic "OneShotCellTypeMismatch" "OneShotWait requires OneShot<T>.")
            | ExpressionRecipe.TaskCancel task ->
                match infer frames task with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ _ ])) -> Ok unitType
                | Ok actual -> Error(diagnostic "TaskCancelInputTypeMismatch" $"TaskCancel requires Task<T>, not {ty actual}.")
            | ExpressionRecipe.ParallelAll(source, workerFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType, Map.tryFind workerFunctionId functionsById with
                    | None, _ -> Error(listMismatch "ParallelAll" "source" sourceType)
                    | _, None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ParallelAll worker '{StableId.value workerFunctionId}' does not exist.")
                    | Some itemType, Some worker ->
                        match validateCallbackContext "ParallelAll" worker context (infer frames) with
                        | Error error -> Error error
                        | Ok(unbound, substitution) ->
                            let matched = match unbound with [ item ] -> TypeParameters.matchType substitution item.Type itemType | _ -> None
                            match matched, missingEffect worker.RequiredEffects with
                            | None, _ ->
                                Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ParallelAll worker {callbackName worker} must have exactly one parameter not bound by context, of the item type {ty itemType}; the worker has type {callbackSignature worker}.")
                            | _, Some effectId ->
                                Error(missingEffectError $"ParallelAll worker {StableId.describe worker.DisplayName worker.Id}" effectId)
                            | Some current, None ->
                                match TypeParameters.substitute current worker.ResultType with
                                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                    Ok(resultType (listType okType) errorType)
                                | other -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ParallelAll worker {callbackName worker} must return Result<U, E>, not {ty other}.")
            | ExpressionRecipe.ListMap(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListMap" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListMap" mapperFunctionId context sourceItemType false (infer frames) with
                        | Error error -> Error error
                        | Ok(mapper, substitution) -> Ok(listType (TypeParameters.substitute substitution mapper.ResultType))
            | ExpressionRecipe.ListZipWith(left, right, combinerFunctionId, context) ->
                match infer frames left with
                | Error error -> Error error
                | Ok leftType ->
                    match listItemType leftType with
                    | None -> Error(listMismatch "ListZipWith" "left" leftType)
                    | Some leftItemType ->
                        match infer frames right with
                        | Error error -> Error error
                        | Ok rightType ->
                            match listItemType rightType with
                            | None -> Error(listMismatch "ListZipWith" "right" rightType)
                            | Some rightItemType ->
                                match Map.tryFind combinerFunctionId functionsById with
                                | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"ListZipWith combiner '{StableId.value combinerFunctionId}' does not exist.")
                                | Some combiner ->
                                    match validateCallbackContext "ListZipWith" combiner context (infer frames) with
                                    | Error error -> Error error
                                    | Ok(unbound, _) when unbound.Length <> 2 ->
                                        Error(diagnostic "PureExpressionCallbackArityMismatch" $"ListZipWith combiner {callbackName combiner} must have exactly two parameters not bound by context (left item, right item); it has type {callbackSignature combiner} and context binds {List.length context}.")
                                    | Ok(unbound, substitution) ->
                                        let matched =
                                            TypeParameters.matchFrom substitution [ unbound.Head.Type, leftItemType; unbound.Tail.Head.Type, rightItemType ]
                                        match matched with
                                        | None ->
                                            Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"ListZipWith combiner {callbackName combiner} must take (left item, right item), here ({ty leftItemType}, {ty rightItemType}); the combiner has type {callbackSignature combiner}.")
                                        | Some _ when not (PureFunctions.isPure snapshot combiner.Id) ->
                                            Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"ListZipWith combiner {callbackName combiner} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                        | Some current -> Ok(listType (TypeParameters.substitute current combiner.ResultType))
            | ExpressionRecipe.ListFlatMap(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFlatMap" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListFlatMap" mapperFunctionId context sourceItemType false (infer frames) with
                        | Error error -> Error error
                        | Ok(mapper, substitution) ->
                            match TypeParameters.substitute substitution mapper.ResultType with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ _ ]) as resultType -> Ok resultType
                            | other ->
                                Error(diagnostic "PureExpressionCallbackResultTypeMismatch" $"ListFlatMap mapper {callbackName mapper} must return List<R>, not {ty other}; it has type {callbackSignature mapper}. Use ListMap for a mapper that returns one item.")
            | ExpressionRecipe.OptionMap(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) ->
                    validateCaseCallback "OptionMap" mapperFunctionId context itemType "Some value" (infer frames)
                    |> Result.map (fun (_, mapped) -> optionType mapped)
                | Ok sourceType -> Error(optionMismatch "OptionMap" sourceType)
            | ExpressionRecipe.OptionAndThen(source, binderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) ->
                    match validateCaseCallback "OptionAndThen" binderFunctionId context itemType "Some value" (infer frames) with
                    | Error error -> Error error
                    | Ok(_, (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ _ ]) as bound)) -> Ok bound
                    | Ok(binder, _) -> Error(binderResultMismatch "OptionAndThen" binder "Option<R>")
                | Ok sourceType -> Error(optionMismatch "OptionAndThen" sourceType)
            | ExpressionRecipe.ResultMap(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                    validateCaseCallback "ResultMap" mapperFunctionId context okType "Ok value" (infer frames)
                    |> Result.map (fun (_, mapped) -> resultType mapped errorType)
                | Ok sourceType -> Error(resultMismatch "ResultMap" sourceType)
            | ExpressionRecipe.ResultMapErr(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                    validateCaseCallback "ResultMapErr" mapperFunctionId context errorType "Err error" (infer frames)
                    |> Result.map (fun (_, mapped) -> resultType okType mapped)
                | Ok sourceType -> Error(resultMismatch "ResultMapErr" sourceType)
            | ExpressionRecipe.ResultAndThen(source, binderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                    match validateCaseCallback "ResultAndThen" binderFunctionId context okType "Ok value" (infer frames) with
                    | Error error -> Error error
                    | Ok(_, (TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; boundError ]) as bound)) when boundError = errorType -> Ok bound
                    | Ok(binder, _) -> Error(binderResultMismatch "ResultAndThen" binder $"Result<R, {ty errorType}>")
                | Ok sourceType -> Error(resultMismatch "ResultAndThen" sourceType)
            | ExpressionRecipe.MapFromList(source, keyOfFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "MapFromList" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "MapFromList" keyOfFunctionId context sourceItemType false (infer frames) with
                        | Error error -> Error error
                        | Ok(keyOf, substitution) ->
                            let keyType = TypeParameters.substitute substitution keyOf.ResultType
                            if EquatableTypes.isEquatable snapshot.Types keyType then Ok(mapOf keyType sourceItemType)
                            else Error(mapKeyNotEquatable keyType)
            | ExpressionRecipe.ListFilter(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFilter" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListFilter" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok sourceType
            | ExpressionRecipe.ListSortBy(source, lessThanFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListSortBy" "source" sourceType)
                    | Some sourceItemType ->
                        match validateLessThanCallback "ListSortBy" lessThanFunctionId context sourceItemType (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok sourceType
            | ExpressionRecipe.ListMinBy(source, lessThanFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListMinBy" "source" sourceType)
                    | Some sourceItemType ->
                        match validateLessThanCallback "ListMinBy" lessThanFunctionId context sourceItemType (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok(optionType sourceItemType)
            | ExpressionRecipe.ListMaxBy(source, lessThanFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListMaxBy" "source" sourceType)
                    | Some sourceItemType ->
                        match validateLessThanCallback "ListMaxBy" lessThanFunctionId context sourceItemType (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok(optionType sourceItemType)
            | ExpressionRecipe.SetFold(source, seed, folderFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match setItemType sourceType with
                    | None -> Error(setMismatch "SetFold" "set" sourceType)
                    | Some itemType ->
                        match infer frames seed with
                        | Error error -> Error error
                        | Ok seedType ->
                            match Map.tryFind folderFunctionId functionsById with
                            | None -> Error(diagnostic "PureExpressionCallbackNotFound" $"SetFold folder '{StableId.value folderFunctionId}' does not exist.")
                            | Some folder ->
                                match validateCallbackContext "SetFold" folder context (infer frames) with
                                | Error error -> Error error
                                | Ok(unbound, _) when unbound.Length <> 2 ->
                                    Error(diagnostic "PureExpressionCallbackArityMismatch" $"SetFold folder {callbackName folder} must have exactly two parameters not bound by context (accumulator, item); it has type {callbackSignature folder} and context binds {List.length context}.")
                                | Ok(unbound, substitution) ->
                                    match TypeParameters.matchFrom substitution [ unbound.Head.Type, seedType; unbound.Tail.Head.Type, itemType ] with
                                    | Some current when TypeParameters.substitute current folder.ResultType = seedType ->
                                        if PureFunctions.isPure snapshot folder.Id then Ok seedType
                                        else Error(diagnostic "PureExpressionCallbackEffectsNotAllowed" $"SetFold folder {callbackName folder} must be pure, including its transitive calls and ambient task or cancellation behavior.")
                                    | _ -> Error(diagnostic "PureExpressionCallbackInputTypeMismatch" $"SetFold folder {callbackName folder} must have type (accumulator, item) -> accumulator, here ({ty seedType}, {ty itemType}) -> {ty seedType}; the folder has type {callbackSignature folder}.")
            | ExpressionRecipe.SetFilter(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match setItemType sourceType with
                    | None -> Error(setMismatch "SetFilter" "set" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "SetFilter" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok sourceType
            | ExpressionRecipe.SetMap(source, mapperFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match setItemType sourceType with
                    | None -> Error(setMismatch "SetMap" "set" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "SetMap" mapperFunctionId context sourceItemType false (infer frames) with
                        | Error error -> Error error
                        | Ok(mapper, substitution) ->
                            let resultItemType = TypeParameters.substitute substitution mapper.ResultType
                            if EquatableTypes.isEquatable snapshot.Types resultItemType then Ok(setOf resultItemType)
                            else Error(setItemNotEquatable resultItemType)
            | ExpressionRecipe.ListAny(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAny" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAny" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | ExpressionRecipe.ListFindIndex(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListFindIndex" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListFindIndex" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok(optionType int64Type)
            | ExpressionRecipe.ListAll(source, predicateFunctionId, context) ->
                match infer frames source with
                | Error error -> Error error
                | Ok sourceType ->
                    match listItemType sourceType with
                    | None -> Error(listMismatch "ListAll" "source" sourceType)
                    | Some sourceItemType ->
                        match validateListCallback "ListAll" predicateFunctionId context sourceItemType true (infer frames) with
                        | Error error -> Error error
                        | Ok _ -> Ok boolType
            | ExpressionRecipe.ProductConstruct(typeId, typeArguments, fields) ->
                match Map.tryFind typeId typesById with
                | None ->
                    Error(diagnostic "PureExpressionProductTypeNotFound" $"Product construction references unknown type '{StableId.value typeId}'.")
                | Some({ Kind = SemanticTypeKind.Product declaredFields } as owner) ->
                    typeArgumentsFor "ProductConstruct" owner declaredFields typeArguments fields
                    |> Result.bind (fun arguments ->
                        validateFieldBindings "ProductConstruct" (instantiateFields owner arguments declaredFields) fields
                        |> Result.map (fun () -> TypeRef.Named(typeId, arguments)))
                | Some _ ->
                    Error(diagnostic "PureExpressionProductTargetMismatch" $"Product construction target '{StableId.value typeId}' is not a product type.")
            | ExpressionRecipe.FieldAccess(source, fieldId) ->
                match Map.tryFind fieldId productFieldsById with
                | None ->
                    Error(diagnostic "PureExpressionProductFieldNotFound" $"Field access references field '{StableId.value fieldId}' that is not owned by a product type.")
                | Some(ownerTypeId, field) ->
                    match infer frames source with
                    | Error error -> Error error
                    | Ok(TypeRef.Named(sourceTypeId, arguments)) when sourceTypeId = ownerTypeId ->
                        match Map.tryFind ownerTypeId typesById with
                        | Some owner -> Ok(TypeParameters.instantiate owner arguments field.Type)
                        | None -> Ok field.Type
                    | Ok actual ->
                        Error(diagnostic "PureExpressionFieldAccessTypeMismatch" $"Field access to {StableId.describe field.DisplayName field.Id} requires {DiagnosticText.namedType snapshot ownerTypeId}, but the source recipe produces {DiagnosticText.typeRefWithIdIn owner.TypeParameters snapshot actual}.")
            | ExpressionRecipe.ProductUpdate(typeId, source, fields) ->
                match Map.tryFind typeId typesById with
                | None ->
                    Error(diagnostic "PureExpressionProductTypeNotFound" $"Product update references unknown type '{StableId.value typeId}'.")
                | Some({ Kind = SemanticTypeKind.Product declaredFields } as productType) ->
                    let declaredIds = declaredFields |> List.map (fun field -> field.Id) |> Set.ofList
                    match fields |> List.tryFind (fun (fieldId, _) -> not (Set.contains fieldId declaredIds)) with
                    | _ when List.isEmpty fields ->
                        Error(diagnostic "PureExpressionEmptyProductUpdate" $"Product update of {DiagnosticText.namedType snapshot typeId} must replace at least one field.")
                    | Some(fieldId, _) ->
                        Error(diagnostic "PureExpressionFieldBindingMismatch" $"Product update of {DiagnosticText.namedType snapshot typeId} binds field '{StableId.value fieldId}', which that type does not declare.")
                    | None ->
                        match infer frames source with
                        | Error error -> Error error
                        | Ok(TypeRef.Named(sourceTypeId, arguments) as sourceType) when sourceTypeId = typeId ->
                            let boundIds = fields |> List.map fst |> Set.ofList
                            let updatedFields = instantiateFields productType arguments declaredFields |> List.filter (fun field -> Set.contains field.Id boundIds)
                            validateFieldBindings "ProductUpdate" updatedFields fields |> Result.map (fun () -> sourceType)
                        | Ok actual ->
                            Error(diagnostic "PureExpressionProductUpdateSourceMismatch" $"Product update of {DiagnosticText.namedType snapshot typeId} requires a source of that type, but the source recipe produces {DiagnosticText.typeRefWithIdIn owner.TypeParameters snapshot actual}.")
                | Some _ ->
                    Error(diagnostic "PureExpressionProductTargetMismatch" $"Product update target '{StableId.value typeId}' is not a product type.")
            | ExpressionRecipe.UnionConstruct(variantId, typeArguments, fields) ->
                match Map.tryFind variantId variantsById with
                | None ->
                    Error(diagnostic "PureExpressionUnionVariantNotFound" $"Union construction references unknown variant '{StableId.value variantId}'.")
                | Some(ownerTypeId, variant) ->
                    match Map.tryFind ownerTypeId typesById with
                    | None -> validateFieldBindings "UnionConstruct" variant.Fields fields |> Result.map (fun () -> TypeRef.Named(ownerTypeId, []))
                    | Some owner ->
                        typeArgumentsFor "UnionConstruct" owner variant.Fields typeArguments fields
                        |> Result.bind (fun arguments ->
                            validateFieldBindings "UnionConstruct" (instantiateFields owner arguments variant.Fields) fields
                            |> Result.map (fun () -> TypeRef.Named(ownerTypeId, arguments)))
            | ExpressionRecipe.OptionNone itemType ->
                match validateTypeRef index itemType with
                | Error error -> Error error
                | Ok() -> Ok(optionType itemType)
            | ExpressionRecipe.ResultOk(errorType, value) ->
                match validateTypeRef index errorType with
                | Error error -> Error error
                | Ok() -> infer frames value |> Result.map (fun okType -> resultType okType errorType)
            | ExpressionRecipe.ResultErr(okType, error) ->
                match validateTypeRef index okType with
                | Error error -> Error error
                | Ok() -> infer frames error |> Result.map (resultType okType)
            | ExpressionRecipe.CapabilityConstruct(capabilityId, implementations) ->
                match CapabilityImplementations.validate snapshot capabilityId implementations with
                | (code, message) :: _ -> Error(diagnostic code message)
                | [] -> Ok(TypeRef.Capability capabilityId)
            | ExpressionRecipe.FunctionReference target ->
                match Map.tryFind target functionsById with
                | None -> Error(diagnostic "UnknownFunctionReference" "The referenced function does not exist.")
                | Some fn when not (List.isEmpty (TypeParameters.ofFunction fn)) -> Error(diagnostic "GenericFunctionReferenceUnsupported" "Use a monomorphic wrapper for a generic function reference.")
                | Some _ when not (PureFunctions.isPure snapshot target) -> Error(diagnostic "ImpureFunctionReference" "Function references require transitive purity and no ambient cancellation reads.")
                | Some fn -> Ok(TypeRef.Function(List.map (fun (p: SemanticParameter) -> p.Type) fn.Parameters, fn.ResultType))
            | ExpressionRecipe.FunctionInvoke(value, arguments) ->
                match infer frames value with
                | Error error -> Error error
                | Ok(TypeRef.Function(parameters, result)) when parameters.Length = arguments.Length ->
                    List.zip parameters arguments
                    |> List.fold (fun state (expected, argument) ->
                        state |> Result.bind (fun () ->
                            infer frames argument |> Result.bind (fun actual ->
                                if actual = expected then Ok()
                                else Error(diagnostic "FunctionInvokeArgumentTypeMismatch" "Function invocation argument has the wrong type.")))) (Ok())
                    |> Result.map (fun () -> result)
                | Ok(TypeRef.Function _) -> Error(diagnostic "FunctionInvokeArityMismatch" "Function invocation arguments must match its positional parameter count.")
                | Ok _ -> Error(diagnostic "FunctionInvokeTargetTypeMismatch" "Function invocation requires a function value.")
            | ExpressionRecipe.DirectCall(targetFunctionId, arguments) ->
                match Map.tryFind targetFunctionId functionsById with
                | None ->
                    Error(diagnostic "PureExpressionCallTargetNotFound" $"DirectCall references unknown function '{StableId.value targetFunctionId}'.")
                | Some callee ->
                    match missingEffect callee.RequiredEffects with
                    | Some effectId -> Error(missingEffectError $"DirectCall callee {callbackName callee}" effectId)
                    | None ->
                        validateCallArguments callee arguments |> Result.bind (fun substitution ->
                            if OneShotPayloadRequirements.violates oneShotPayloadRequirements.Value callee.Id substitution then
                                Error(diagnostic "OneShotScopePayloadUnsupported" "This generic call would place TaskScope inside a OneShot payload, directly or through another generic call.")
                            else Ok(TypeParameters.substitute substitution callee.ResultType))
            | ExpressionRecipe.SelfCall arguments ->
                // The owner's RequiredEffects trivially cover its own, so only the arguments are checked.
                let slotCount = List.length owner.SelfParameters
                match tryFindDuplicate fst arguments with
                | Some slot ->
                    Error(diagnostic "PureExpressionDuplicateCallArgument" $"SelfCall binds parameter slot {slot} more than once.")
                | None ->
                    match arguments |> List.tryFind (fun (slot, _) -> slot < 0 || slot >= slotCount) with
                    | Some(slot, _) ->
                        Error(
                            diagnostic
                                "PureExpressionCallArgumentTargetNotFound"
                                $"SelfCall targets parameter slot {slot}, but {owner.Description} has {slotCount} parameter(s). {describeOwnerParameters owner}"
                        )
                    | None ->
                        let bound = arguments |> List.map fst |> Set.ofList
                        match List.init slotCount id |> List.tryFind (fun slot -> not (Set.contains slot bound)) with
                        | Some slot ->
                            Error(
                                diagnostic
                                    "PureExpressionCallArgumentMissing"
                                    $"SelfCall is missing required parameter slot {slot} ('{owner.SelfParameters[slot].DisplayName}') of {owner.Description}."
                            )
                        | None ->
                            let rec validateSlots = function
                                | [] -> Ok owner.ResultType
                                | (slot, (parameter: ParameterSlotContext)) :: rest ->
                                    let _, value = arguments |> List.find (fun (candidate, _) -> candidate = slot)
                                    match infer frames value with
                                    | Error error -> Error error
                                    | Ok actual when actual <> parameter.Type ->
                                        Error(
                                            diagnostic
                                                "PureExpressionCallArgumentTypeMismatch"
                                                (let expected, produced = tyPair parameter.Type actual in
                                                 $"SelfCall parameter slot {slot} requires {expected}, but the recipe produces {produced}. A function calls itself at its own parameter types; there is no polymorphic recursion.")
                                        )
                                    | Ok _ -> validateSlots rest
                            validateSlots (List.indexed owner.SelfParameters)
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
                                $"CapabilityInvoke operation {StableId.describe operation.DisplayName operationId} requires {ty (TypeRef.Capability ownerCapabilityId)}, but the receiver produces {ty actual}."
                        )
                    | Ok _ when not (List.contains operation.EffectId owner.RequiredEffects) ->
                        Error(
                            diagnostic
                                "ExpressionCapabilityMissingEffectRequirement"
                                $"CapabilityInvoke operation {StableId.describe operation.DisplayName operationId} requires effect {effectLabel snapshot operation.EffectId} not declared by {owner.Description}."
                        )
                    | Ok _ ->
                        validateCapabilityArguments operation arguments |> Result.map (fun () -> operation.ResultType)
            | ExpressionRecipe.Abort _
            | ExpressionRecipe.Conditional _
            | ExpressionRecipe.MatchUnion _
            | ExpressionRecipe.Bind _
            | ExpressionRecipe.MatchOption _
            | ExpressionRecipe.MatchResult _ ->
                inferCompletion frames recipe
                |> Result.bind (function
                    | GraphCompletion.Normal value -> Ok value
                    | GraphCompletion.Aborts -> Error(diagnostic "AbortValueUse" $"An abrupt expression cannot be used as a value in {owner.Description}."))

        and inferCompletionNode frames recipe =
            let merge role left right =
                match GraphCompletion.merge [ Some left; Some right ] with
                | Some result -> Ok result
                | None ->
                    let leftText, rightText = DiagnosticText.completionPairIn owner.TypeParameters snapshot left right
                    Error(diagnostic "PureExpressionBranchTypeMismatch" $"{role} normally completing branches must produce one common type; found {leftText} and {rightText}.")
            match recipe with
            | ExpressionRecipe.Abort payload ->
                match owner.ResultType with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; errorType ]) ->
                    infer frames payload |> Result.bind (fun actual ->
                        if actual = errorType then Ok GraphCompletion.Aborts
                        else
                            let expected, produced = tyPair errorType actual
                            Error(diagnostic "AbortPayloadTypeMismatch" $"Abort payload has type {produced}, but {owner.Description} requires {expected}."))
                | _ -> Error(diagnostic "AbortOwnerResultTypeMismatch" $"Abort requires {owner.Description} to return Result<T, E>.")
            | ExpressionRecipe.Conditional(condition, whenTrue, whenFalse) ->
                match infer frames condition with
                | Error error -> Error error
                | Ok actual when actual <> boolType -> Error(typeMismatch "Conditional" "condition" boolType actual)
                | Ok _ ->
                    match inferCompletion frames whenTrue with
                    | Error error -> Error error
                    | Ok trueType ->
                        match inferCompletion frames whenFalse with
                        | Error error -> Error error
                        | Ok falseType -> merge "Conditional" trueType falseType
            | ExpressionRecipe.MatchUnion(input, arms) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.Named(inputTypeId, inputArguments)) ->
                    match Map.tryFind inputTypeId typesById with
                    | Some({ Kind = SemanticTypeKind.Union declaredVariants } as inputType) ->
                        let variants =
                            declaredVariants |> List.map (fun variant -> { variant with Fields = instantiateFields inputType inputArguments variant.Fields })
                        match resolveUnionArms variants arms with
                        | Error error -> Error error
                        | Ok resolved ->
                            let rec inferArms expected = function
                                | [] ->
                                    match expected with
                                    | Some resultType -> Ok resultType
                                    | None -> Error(diagnostic "PureExpressionMatchHasNoArms" "MatchUnion requires at least one declared variant to derive a result type.")
                                | ((armVariants: SemanticVariant list), body, bindsFields) :: rest ->
                                    let frame: PatternTypeFrame =
                                        { Kind = ScopeFrameKind.UnionArm(armVariants |> List.map (fun variant -> variant.Id))
                                          Slots =
                                            if bindsFields then
                                                armVariants.Head.Fields |> List.map (fun field -> RecipePatternSlot.Field field.Id, field.Type)
                                            else [] }
                                    match inferCompletion (frame :: frames) body with
                                    | Error error -> Error error
                                    | Ok actual ->
                                        match expected with
                                        | None -> inferArms (Some actual) rest
                                        | Some expectedType -> merge "MatchUnion" expectedType actual |> Result.bind (fun merged -> inferArms (Some merged) rest)
                            inferArms None resolved
                    | Some _
                    | None ->
                        Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchUnion input must produce an existing named union type.")
            | ExpressionRecipe.Bind(input, body) ->
                match infer frames input with
                | Error error -> Error error
                | Ok inputType -> inferCompletion ({ Kind = ScopeFrameKind.BindBody; Slots = [ RecipePatternSlot.Value, inputType ] } :: frames) body
            | ExpressionRecipe.MatchOption(input, someBody, noneBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) ->
                    let someFrame: PatternTypeFrame = { Kind = ScopeFrameKind.OptionSome; Slots = [ RecipePatternSlot.Value, itemType ] }
                    match inferCompletion (someFrame :: frames) someBody with
                    | Error error -> Error error
                    | Ok someType ->
                        match inferCompletion ({ Kind = ScopeFrameKind.OptionNone; Slots = [] } :: frames) noneBody with
                        | Error error -> Error error
                        | Ok noneType -> merge "MatchOption" someType noneType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchOption input must produce Option<T>.")
            | ExpressionRecipe.MatchResult(input, okBody, errBody) ->
                match infer frames input with
                | Error error -> Error error
                | Ok(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])) ->
                    let okFrame: PatternTypeFrame = { Kind = ScopeFrameKind.ResultOk; Slots = [ RecipePatternSlot.Value, okType ] }
                    let errFrame: PatternTypeFrame = { Kind = ScopeFrameKind.ResultErr; Slots = [ RecipePatternSlot.Error, errorType ] }
                    match inferCompletion (okFrame :: frames) okBody with
                    | Error error -> Error error
                    | Ok okResultType ->
                        match inferCompletion (errFrame :: frames) errBody with
                        | Error error -> Error error
                        | Ok errResultType -> merge "MatchResult" okResultType errResultType
                | Ok _ -> Error(diagnostic "PureExpressionMatchInputTypeMismatch" "MatchResult input must produce Result<T,E>.")

            | _ -> infer frames recipe |> Result.map GraphCompletion.Normal

        match inferCompletion initialFrames recipe with
        | Error error -> Error(locate rootLabel recipe failure.Value error)
        | Ok completion -> Ok completion

    let internal inferTypeWithFrames rootLabel index target initialFrames recipe =
        inferTypeWithContext index (ownerForExistingFunction target) (InferenceCache()) initialFrames rootLabel recipe

    let internal inferType rootLabel index target recipe = inferTypeWithFrames rootLabel index target [] recipe
