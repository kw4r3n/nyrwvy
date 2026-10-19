namespace Nyrwvy.Core

/// Scope frames, owner contexts, snapshot indexing and diagnostic locating shared by recipe inference and materialization.
module internal ExpressionRecipeSupport =
    open StructuralTransactionInternals

    /// What opened a lexical frame; used only to describe visible frames in scope diagnostics.
    [<RequireQualifiedAccess>]
    type internal ScopeFrameKind =
        | UnionArm of VariantId list
        | BindBody
        | OptionSome
        | OptionNone
        | ResultOk
        | ResultErr

    type internal ScopeFrame<'Binding> =
        { Kind: ScopeFrameKind
          Slots: (RecipePatternSlot * 'Binding) list }

    type internal PatternTypeFrame = ScopeFrame<TypeRef>
    type internal PatternBindingFrame = ScopeFrame<PatternBinding>

    type internal ParameterSlotContext =
        { Type: TypeRef
          ParameterId: ParameterId option
          DisplayName: string }

    type internal ExpressionOwnerContext =
        { ParametersById: Map<ParameterId, SemanticParameter>
          ParameterSlots: ParameterSlotContext list option
          RequiredEffects: EffectId list
          ResultType: TypeRef
          /// The owner's declared type parameters, which name its occurrences in diagnostics.
          TypeParameters: (TypeParameterId * string) list
          /// The owner's own identity; None only while a new function is preflighted.
          SelfFunctionId: FunctionId option
          /// The owner's parameters in declaration order, addressed by SelfCall slots.
          SelfParameters: ParameterSlotContext list
          Description: string }

    let internal ownerForExistingFunction (target: SemanticFunction) =
        { ParametersById =
            target.Parameters
            |> List.map (fun parameter -> parameter.Id, parameter)
            |> Map.ofList
          ParameterSlots = None
          RequiredEffects = target.RequiredEffects
          ResultType = target.ResultType
          TypeParameters = target.TypeParameters
          SelfFunctionId = Some target.Id
          SelfParameters =
            target.Parameters
            |> List.map (fun parameter ->
                { Type = parameter.Type
                  ParameterId = Some parameter.Id
                  DisplayName = parameter.DisplayName })
          Description = $"target function {StableId.describe target.DisplayName target.Id}" }

    /// The owner's parameters in a diagnostic, by slot, display name and (when allocated) ID.
    let internal describeOwnerParameters (owner: ExpressionOwnerContext) =
        match owner.SelfParameters with
        | [] -> $"{owner.Description} has no parameters."
        | parameters ->
            let entries =
                parameters
                |> List.mapi (fun slot parameter ->
                    match parameter.ParameterId with
                    | Some parameterId -> $"{slot} = {StableId.describe parameter.DisplayName parameterId}"
                    | None -> $"{slot} = '{parameter.DisplayName}'")
                |> String.concat ", "
            $"Parameters of {owner.Description}: {entries}."

    /// An effect in a diagnostic: display name and ID when declared, the ID alone otherwise.
    let internal effectLabel (snapshot: Snapshot) effectId =
        match snapshot.Effects |> List.tryFind (fun effect -> effect.Id = effectId) with
        | Some effect -> StableId.describe effect.DisplayName effectId
        | None -> $"'{StableId.value effectId}'"

    let internal stringType = TypeRef.Primitive PrimitiveType.String
    let internal boolType = TypeRef.Primitive PrimitiveType.Bool
    let internal unitType = TypeRef.Primitive PrimitiveType.Unit
    let internal int64Type = TypeRef.Primitive PrimitiveType.Int64
    let internal float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private bits32Type = TypeRef.Primitive PrimitiveType.Bits32
    let internal listType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let internal optionType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
    let internal resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])

    /// The first key that occurs more than once in `items`, if any.
    let internal tryFindDuplicate keyOf items =
        items |> List.countBy keyOf |> List.tryFind (fun (_, count) -> count > 1) |> Option.map fst

    /// A recipe MatchUnion's arms resolved against the union's variants, in declaration order of each
    /// arm's first variant: the variants each arm covers (an `otherwise` arm expanded), its body, and
    /// whether its body sees the variant's fields (only an explicit arm naming exactly one variant).
    let internal resolveUnionArms (variants: SemanticVariant list) (arms: (VariantId list * ExpressionRecipe) list) =
        let explicitArms, otherwiseArms = arms |> List.partition (fun (variantIds, _) -> not (List.isEmpty variantIds))
        let named = explicitArms |> List.collect fst
        let declared = variants |> List.map (fun variant -> variant.Id) |> Set.ofList
        let remaining = variants |> List.filter (fun variant -> not (List.contains variant.Id named))
        match tryFindDuplicate id named, otherwiseArms with
        | Some variantId, _ ->
            Error(diagnostic "PureExpressionMatchArmDuplicate" $"MatchUnion contains variant '{StableId.value variantId}' more than once.")
        | None, _ when not (Set.isSubset (Set.ofList named) declared) ->
            Error(diagnostic "PureExpressionMatchArmMismatch" "MatchUnion must contain every declared variant exactly once and no other variants.")
        | None, _ :: _ :: _ ->
            Error(diagnostic "PureExpressionMatchOtherwiseDuplicate" "MatchUnion has more than one otherwise arm.")
        | None, [ _ ] when List.isEmpty remaining ->
            Error(diagnostic "PureExpressionMatchOtherwiseUnused" "MatchUnion has an otherwise arm, but every variant is already named by another arm.")
        | None, [] when not (List.isEmpty remaining) ->
            Error(diagnostic "PureExpressionMatchArmMismatch" "MatchUnion must contain every declared variant exactly once and no other variants.")
        | None, otherwiseArm ->
            let variantIndex variantId = variants |> List.findIndex (fun variant -> variant.Id = variantId)
            let covered variantIds = variants |> List.filter (fun variant -> List.contains variant.Id variantIds)
            let explicitResolved =
                explicitArms
                |> List.map (fun (variantIds, body) -> covered variantIds, body, (List.length variantIds = 1))
            let otherwiseResolved = otherwiseArm |> List.map (fun (_, body) -> remaining, body, false)
            Ok(
                explicitResolved @ otherwiseResolved
                |> List.sortBy (fun (armVariants: SemanticVariant list, _, _) -> variantIndex armVariants.Head.Id)
            )

    let internal mapOf keyType valueType = TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ])

    let internal setOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ])

    let internal setItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]) -> Some itemType
        | _ -> None

    let internal mapEntryTypes typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) -> Some(keyType, valueType)
        | _ -> None

    let internal listItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) -> Some itemType
        | _ -> None

    let internal recipeChildren recipe =
        match recipe with
        | ExpressionRecipe.Parameter _
        | ExpressionRecipe.ParameterSlot _
        | ExpressionRecipe.PatternReference _
        | ExpressionRecipe.StringLiteral _
        | ExpressionRecipe.BoolLiteral _
        | ExpressionRecipe.UnitLiteral
        | ExpressionRecipe.Int64Literal _
        | ExpressionRecipe.Float64Literal _
        | ExpressionRecipe.ListEmpty _
        | ExpressionRecipe.SetEmpty _
        | ExpressionRecipe.MapEmpty _
        | ExpressionRecipe.OptionNone _ -> Seq.empty
        | ExpressionRecipe.StringIsEmpty child
        | ExpressionRecipe.Int64ToString child
        | ExpressionRecipe.Int64ToHexString child
        | ExpressionRecipe.StringParseInt64 child
        | ExpressionRecipe.StringParseInt64Hex child
        | ExpressionRecipe.Float64ToString child
        | ExpressionRecipe.StringParseFloat64 child
        | ExpressionRecipe.StringCodePoints child
        | ExpressionRecipe.StringToLower child
        | ExpressionRecipe.StringToUpper child
        | ExpressionRecipe.StringTrim child
        | ExpressionRecipe.StringTrimStart child
        | ExpressionRecipe.StringTrimEnd child
        | ExpressionRecipe.StringDecodeUtf8Lossy child
        | ExpressionRecipe.StringEncodeUtf8 child
        | ExpressionRecipe.Float64RoundToInt64 child
        | ExpressionRecipe.Float64FloorToInt64 child
        | ExpressionRecipe.Float64Sqrt child
        | ExpressionRecipe.Float64Abs child
        | ExpressionRecipe.Float64Negate child
        | ExpressionRecipe.Float64Floor child
        | ExpressionRecipe.Float64Ceiling child
        | ExpressionRecipe.Float64Truncate child
        | ExpressionRecipe.StringUtf8ByteCount child
        | ExpressionRecipe.StringFromCodePoint child
        | ExpressionRecipe.StringToCodePoint child
        | ExpressionRecipe.StringLength child
        | ExpressionRecipe.BoolNot child
        | ExpressionRecipe.Float64IsNaN child
        | ExpressionRecipe.Float64IsFinite child
        | ExpressionRecipe.Float64IsInfinite child
        | ExpressionRecipe.Int64ToFloat64 child
        | ExpressionRecipe.Int64WrappingAbs child
        | ExpressionRecipe.Int64BitNot child
        | ExpressionRecipe.Bits32FromInt64 child
        | ExpressionRecipe.Bits32ToInt64 child
        | ExpressionRecipe.Bits32Not child
        | ExpressionRecipe.ListLength child
        | ExpressionRecipe.MapValues child
        | ExpressionRecipe.MapKeys child
        | ExpressionRecipe.SetToList child
        | ExpressionRecipe.MapSize child
        | ExpressionRecipe.SetFromList child
        | ExpressionRecipe.SetSize child
        | ExpressionRecipe.ListReverse child
        | ExpressionRecipe.ListLast child
        | ExpressionRecipe.ListDropLast child
        | ExpressionRecipe.FieldAccess(child, _)
        | ExpressionRecipe.OptionSome child
        | ExpressionRecipe.ResultOk(_, child)
        | ExpressionRecipe.Abort child
        | ExpressionRecipe.ResultErr(_, child) -> Seq.singleton child
        | ExpressionRecipe.StringSlice(value, start, stop) -> seq { value; start; stop }
        | ExpressionRecipe.StringIndexOf(value, substring, from) -> seq { value; substring; from }
        | ExpressionRecipe.StringReplace(value, pattern, replacement) -> seq { value; pattern; replacement }
        | ExpressionRecipe.StringSpan(value, set, bound)
        | ExpressionRecipe.StringSpanBack(value, set, bound) -> seq { value; set; bound }
        | ExpressionRecipe.StringLastIndexOfBefore(value, set, bound) -> seq { value; set; bound }
        | ExpressionRecipe.ListSlice(list, start, stop) -> seq { list; start; stop }
        | ExpressionRecipe.ListSetAt(list, index, item)
        | ExpressionRecipe.ListInsertAt(list, index, item) -> seq { list; index; item }
        | ExpressionRecipe.MapInsert(map, key, value) -> seq { map; key; value }
        | ExpressionRecipe.MapGetOrDefault(map, key, defaultValue) -> seq { map; key; defaultValue }
        | ExpressionRecipe.OptionGetOrDefault(option, defaultValue) -> seq { option; defaultValue }
        | ExpressionRecipe.ResultGetOrDefault(result, defaultValue) -> seq { result; defaultValue }
        | ExpressionRecipe.OptionOkOr(option, error) -> seq { option; error }
        | ExpressionRecipe.ResultOkValue child
        | ExpressionRecipe.ResultErrValue child
        | ExpressionRecipe.OptionIsSome child
        | ExpressionRecipe.ResultIsOk child -> Seq.singleton child
        | ExpressionRecipe.StringContains(left, right)
        | ExpressionRecipe.StringLastIndexOf(left, right)
        | ExpressionRecipe.StringStartsWith(left, right)
        | ExpressionRecipe.StringEndsWith(left, right)
        | ExpressionRecipe.StringSplit(left, right)
        | ExpressionRecipe.StringSplitLines(left, right)
        | ExpressionRecipe.StringJoin(left, right)
        | ExpressionRecipe.StringEqual(left, right)
        | ExpressionRecipe.ValueEqual(left, right)
        | ExpressionRecipe.StringLessThan(left, right)
        | ExpressionRecipe.StringConcat(left, right)
        | ExpressionRecipe.Bind(left, right)
        | ExpressionRecipe.BoolAnd(left, right)
        | ExpressionRecipe.BoolOr(left, right)
        | ExpressionRecipe.Int64WrappingAdd(left, right)
        | ExpressionRecipe.Int64WrappingSubtract(left, right)
        | ExpressionRecipe.Int64WrappingMultiply(left, right)
        | ExpressionRecipe.Int64FloorDivide(left, right)
        | ExpressionRecipe.Int64FloorModulo(left, right)
        | ExpressionRecipe.Int64Min(left, right)
        | ExpressionRecipe.Int64Max(left, right)
        | ExpressionRecipe.Int64BitAnd(left, right)
        | ExpressionRecipe.Int64BitOr(left, right)
        | ExpressionRecipe.Int64BitXor(left, right)
        | ExpressionRecipe.Int64ShiftLeft(left, right)
        | ExpressionRecipe.Int64ShiftRight(left, right)
        | ExpressionRecipe.Int64CheckedAdd(left, right)
        | ExpressionRecipe.Int64CheckedSubtract(left, right)
        | ExpressionRecipe.Int64CheckedMultiply(left, right)
        | ExpressionRecipe.ListRange(left, right)
        | ExpressionRecipe.StringRepeat(left, right)
        | ExpressionRecipe.Float64Round(left, right)
        | ExpressionRecipe.Int64Equal(left, right)
        | ExpressionRecipe.Int64LessThan(left, right)
        | ExpressionRecipe.Int64LessThanOrEqual(left, right)
        | ExpressionRecipe.Int64GreaterThan(left, right)
        | ExpressionRecipe.Int64GreaterThanOrEqual(left, right)
        | ExpressionRecipe.Float64Add(left, right)
        | ExpressionRecipe.Float64Min(left, right)
        | ExpressionRecipe.Float64Max(left, right)
        | ExpressionRecipe.Float64Subtract(left, right)
        | ExpressionRecipe.Float64Multiply(left, right)
        | ExpressionRecipe.Float64Divide(left, right)
        | ExpressionRecipe.Float64FloorModulo(left, right)
        | ExpressionRecipe.Float64Equal(left, right)
        | ExpressionRecipe.Float64LessThan(left, right)
        | ExpressionRecipe.Bits32Xor(left, right)
        | ExpressionRecipe.Bits32And(left, right)
        | ExpressionRecipe.Bits32Or(left, right)
        | ExpressionRecipe.Bits32Add(left, right)
        | ExpressionRecipe.Bits32RotateRight(left, right)
        | ExpressionRecipe.Bits32ShiftRight(left, right)
        | ExpressionRecipe.Bits32ShiftLeft(left, right)
        | ExpressionRecipe.ListAppendItem(left, right)
        | ExpressionRecipe.ListItemAt(left, right)
        | ExpressionRecipe.ListRemoveAt(left, right)
        | ExpressionRecipe.MapRemove(left, right)
        | ExpressionRecipe.SetInsert(left, right)
        | ExpressionRecipe.SetRemove(left, right)
        | ExpressionRecipe.MapContainsKey(left, right)
        | ExpressionRecipe.SetContains(left, right)
        | ExpressionRecipe.ListIndexOf(left, right)
        | ExpressionRecipe.ListLastIndexOf(left, right)
        | ExpressionRecipe.ListContains(left, right)
        | ExpressionRecipe.MapGet(left, right)
        | ExpressionRecipe.ListConcat(left, right)
        | ExpressionRecipe.SetUnion(left, right)
        | ExpressionRecipe.SetIntersection(left, right)
        | ExpressionRecipe.SetDifference(left, right)
        | ExpressionRecipe.SetIsSubset(left, right)
        | ExpressionRecipe.MapUnion(left, right) -> seq { yield left; yield right }
        | ExpressionRecipe.ListFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.MapFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.SetFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.ListEffectFold(source, seed, _, context) -> seq { yield source; yield seed; yield! context |> Seq.map snd }
        | ExpressionRecipe.UseResource(acquire, releaseCapability, _, _, context) ->
            seq { yield acquire; yield releaseCapability; yield! context |> Seq.map snd }
        | ExpressionRecipe.ParallelAll(source, _, context) -> seq { yield source; yield! context |> Seq.map snd }
        | ExpressionRecipe.CancellationRequested -> Seq.empty
        | ExpressionRecipe.ExceptionCatch(_, bodyContext, _, handlerContext) -> (bodyContext @ handlerContext) |> Seq.map snd
        | ExpressionRecipe.ExceptionIsCancellation value
        | ExpressionRecipe.ExceptionMessage value
        | ExpressionRecipe.ExceptionRethrow(value, _) -> Seq.singleton value
        | ExpressionRecipe.CancellationShield(_, context) -> context |> Seq.map snd
        | ExpressionRecipe.TaskScope(_, context) -> context |> Seq.map snd
        | ExpressionRecipe.TaskStart(scope, _, context, item) -> seq { yield scope; yield item; yield! context |> Seq.map snd }
        | ExpressionRecipe.OneShotCreate(scope, _) -> Seq.singleton scope
        | ExpressionRecipe.OneShotComplete(cell, value) -> seq { yield cell; yield value }
        | ExpressionRecipe.OneShotWait(cell, limit) -> seq { yield cell; yield! Option.toList limit }
        | ExpressionRecipe.TaskWait(tasks, _, limit) -> seq { yield tasks; yield! Option.toList limit }
        | ExpressionRecipe.TaskCancel task -> Seq.singleton task
        | ExpressionRecipe.ListZipWith(left, right, _, context) -> seq { yield left; yield right; yield! context |> Seq.map snd }
        | ExpressionRecipe.ListFlatMap(source, _, context)
        | ExpressionRecipe.ListMap(source, _, context)
        | ExpressionRecipe.MapFromList(source, _, context)
        | ExpressionRecipe.ListFilter(source, _, context)
        | ExpressionRecipe.MapFilter(source, _, context)
        | ExpressionRecipe.SetFilter(source, _, context)
        | ExpressionRecipe.ListSortBy(source, _, context)
        | ExpressionRecipe.MapMapValues(source, _, context)
        | ExpressionRecipe.SetMap(source, _, context)
        | ExpressionRecipe.OptionMap(source, _, context)
        | ExpressionRecipe.OptionAndThen(source, _, context)
        | ExpressionRecipe.ResultMap(source, _, context)
        | ExpressionRecipe.ResultMapErr(source, _, context)
        | ExpressionRecipe.ResultAndThen(source, _, context)
        | ExpressionRecipe.ListMinBy(source, _, context)
        | ExpressionRecipe.ListMaxBy(source, _, context)
        | ExpressionRecipe.ListAny(source, _, context)
        | ExpressionRecipe.ListFindIndex(source, _, context)
        | ExpressionRecipe.ListAll(source, _, context) -> seq { yield source; yield! context |> Seq.map snd }
        | ExpressionRecipe.ListConstruct(_, items) -> items |> Seq.ofList
        | ExpressionRecipe.ProductConstruct(_, _, fields)
        | ExpressionRecipe.UnionConstruct(_, _, fields) -> fields |> Seq.map snd
        | ExpressionRecipe.ProductUpdate(_, source, fields) -> seq { yield source; yield! fields |> Seq.map snd }
        | ExpressionRecipe.CapabilityConstruct _
        | ExpressionRecipe.FunctionReference _ -> Seq.empty
        | ExpressionRecipe.FunctionInvoke(value, arguments) -> seq { yield value; yield! arguments }
        | ExpressionRecipe.DirectCall(_, arguments) -> arguments |> Seq.map snd
        | ExpressionRecipe.SelfCall arguments -> arguments |> Seq.map snd
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

    /// The wire property that holds a recipe field. Named union fields already use the wire name;
    /// an unnamed single operand is `value`, and unnamed operand pairs are `left`/`right`
    /// (`value`/`substring` for StringContains).
    let private recipeFieldName caseName (fields: System.Reflection.PropertyInfo[]) index =
        let name = fields[index].Name
        if not (name.StartsWith "Item") then name
        elif fields.Length = 1 then "value"
        else
            match caseName, index with
            | "StringContains", 0 -> "value"
            | "StringContains", _ -> "substring"
            | ("StringIndexOf" | "StringLastIndexOf" | "StringStartsWith" | "StringEndsWith" | "StringSplit"), 0 -> "value"
            | ("StringIndexOf" | "StringLastIndexOf"), 1 -> "substring"
            | "StringIndexOf", _ -> "from"
            | "StringStartsWith", _ -> "prefix"
            | "StringEndsWith", _ -> "suffix"
            | "StringSplit", _ -> "separator"
            | _, 0 -> "left"
            | _ -> "right"

    /// The recipe kind in wire spelling, e.g. `optionSome`.
    let private recipeKindName (recipe: ExpressionRecipe) =
        let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(recipe, typeof<ExpressionRecipe>)
        string (System.Char.ToLowerInvariant case.Name[0]) + case.Name.Substring 1

    /// The path of `target` (by reference) inside `root`, in the submitted request's property
    /// names: `body.whenFalse.value`, `body.arguments[1].value`, `replacement.arms[0].body`.
    /// Generic over the recipe cases, so a new case needs no change here.
    let private recipePath (rootLabel: string) (root: ExpressionRecipe) (target: ExpressionRecipe) =
        let rec search path (node: ExpressionRecipe) =
            if obj.ReferenceEquals(node, target) then Some path
            else
                let case, values = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(node, typeof<ExpressionRecipe>)
                let fields = case.GetFields()
                values
                |> Seq.indexed
                |> Seq.tryPick (fun (index, value) ->
                    let name = recipeFieldName case.Name fields index
                    match value with
                    | :? ExpressionRecipe as child -> search $"{path}.{name}" child
                    | :? (ExpressionRecipe option) as child -> child |> Option.bind (search $"{path}.{name}")
                    | :? string -> None
                    | :? System.Collections.IEnumerable as items ->
                        items
                        |> Seq.cast<obj>
                        |> Seq.indexed
                        |> Seq.tryPick (fun (itemIndex, item) ->
                            match item with
                            | :? ExpressionRecipe as child -> search $"{path}.{name}[{itemIndex}]" child
                            | null -> None
                            | _ when Microsoft.FSharp.Reflection.FSharpType.IsTuple(item.GetType()) ->
                                let property = if name = "arms" then "body" else "value"
                                Microsoft.FSharp.Reflection.FSharpValue.GetTupleFields item
                                |> Array.tryPick (function
                                    | :? ExpressionRecipe as child -> search $"{path}.{name}[{itemIndex}].{property}" child
                                    | _ -> None)
                            | _ -> None)
                    | _ -> None)
        search rootLabel root

    /// Appends the recipe path and kind of the node that produced `error`, when known.
    let internal locate rootLabel root (failure: (TransactionDiagnostic * ExpressionRecipe) option) (error: TransactionDiagnostic) =
        match rootLabel, failure with
        | Some label, Some(known, node) when known = error ->
            match recipePath label root node with
            | Some path -> { error with Message = $"{error.Message} At {path} ({recipeKindName node})." }
            | None -> error
        | _ -> error

    /// Snapshot lookups built once per simulate or preflight and shared by inference and materialization.
    type internal SnapshotIndex =
        { Snapshot: Snapshot
          FunctionsById: Map<FunctionId, SemanticFunction>
          TypesById: Map<TypeId, SemanticType>
          CapabilityOperationsById: Map<CapabilityOperationId, CapabilityId * SemanticCapabilityOperation>
          VariantsById: Map<VariantId, TypeId * SemanticVariant>
          ProductFieldsById: Map<FieldId, TypeId * SemanticField>
          TypeIds: Set<TypeId>
          CapabilityIds: Set<CapabilityId> }

    let internal buildSnapshotIndex (snapshot: Snapshot) =
        { Snapshot = snapshot
          FunctionsById = snapshot.Functions |> List.map (fun functionEntity -> functionEntity.Id, functionEntity) |> Map.ofList
          TypesById = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id, typeEntity) |> Map.ofList
          CapabilityOperationsById =
            snapshot.Capabilities
            |> List.collect (fun capability ->
                capability.Operations |> List.map (fun operation -> operation.Id, (capability.Id, operation)))
            |> Map.ofList
          VariantsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product _ -> []
                | SemanticTypeKind.Union variants -> variants |> List.map (fun variant -> variant.Id, (typeEntity.Id, variant)))
            |> Map.ofList
          ProductFieldsById =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.Id, (typeEntity.Id, field))
                | SemanticTypeKind.Union _ -> [])
            |> Map.ofList
          TypeIds = snapshot.Types |> List.map (fun typeEntity -> typeEntity.Id) |> Set.ofList
          CapabilityIds = snapshot.Capabilities |> List.map (fun capability -> capability.Id) |> Set.ofList }

    let internal validateTypeRef (index: SnapshotIndex) typeRef =
        let invalid message = diagnostic "PureExpressionInvalidTypeRef" message

        let rec validate typeRef =
            match typeRef with
            | TypeRef.Primitive _
            // Scope (a parameter type must declare it) is checked on the resulting snapshot.
            | TypeRef.TypeParameter _ -> Ok()
            | TypeRef.Named(typeId, arguments) when Set.contains typeId index.TypeIds ->
                let declared = index.TypesById[typeId]
                if declared.TypeParameters.Length <> arguments.Length then
                    Error(
                        diagnostic
                            "NamedTypeArityMismatch"
                            $"{declared.DisplayName} takes {declared.TypeParameters.Length} type arguments, but the type reference gives {arguments.Length}."
                    )
                else
                    arguments |> List.fold (fun state arg -> state |> Result.bind (fun () -> validate arg)) (Ok())
            | TypeRef.Named(typeId, _) -> Error(invalid $"Type reference names unknown type '{StableId.value typeId}'.")
            | TypeRef.Capability capabilityId when Set.contains capabilityId index.CapabilityIds -> Ok()
            | TypeRef.Capability capabilityId ->
                Error(invalid $"Type reference names unknown capability '{StableId.value capabilityId}'.")
            | TypeRef.Function(arguments, result) ->
                (arguments @ [ result ]) |> List.fold (fun state arg -> state |> Result.bind (fun () -> validate arg)) (Ok())
            | TypeRef.BuiltinGeneric(kind, arguments) ->
                let expectedArity =
                    match kind with
                    | BuiltinGenericType.Exception -> 0
                    | BuiltinGenericType.TaskScope -> 0
                    | BuiltinGenericType.Option
                    | BuiltinGenericType.List
                    | BuiltinGenericType.Set
                    | BuiltinGenericType.OneShot
                    | BuiltinGenericType.Task -> 1
                    | BuiltinGenericType.Result
                    | BuiltinGenericType.Map -> 2

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

    /// A declared field by ID with a description of its owning product or variant.
    let private tryFindFieldWithOwner (snapshot: Snapshot) fieldId =
        snapshot.Types
        |> List.tryPick (fun typeEntity ->
            match typeEntity.Kind with
            | SemanticTypeKind.Product fields ->
                fields
                |> List.tryFind (fun field -> field.Id = fieldId)
                |> Option.map (fun field -> field, $"product '{typeEntity.DisplayName}'")
            | SemanticTypeKind.Union variants ->
                variants
                |> List.tryPick (fun variant ->
                    variant.Fields
                    |> List.tryFind (fun field -> field.Id = fieldId)
                    |> Option.map (fun field -> field, $"variant '{variant.DisplayName}' of '{typeEntity.DisplayName}'")))

    /// A pattern slot in a diagnostic: a field by display name and ID with its owning variant/type
    /// (the ID alone when no declaration has it), or `Value`/`Error`.
    let private describePatternSlot (snapshot: Snapshot) slot =
        match slot with
        | RecipePatternSlot.Value -> "Value"
        | RecipePatternSlot.Error -> "Error"
        | RecipePatternSlot.Field fieldId ->
            tryFindFieldWithOwner snapshot fieldId
            |> Option.map (fun (field, owner) -> $"field {StableId.describe field.DisplayName fieldId} of {owner}")
            |> Option.defaultValue $"field '{StableId.value fieldId}' (not a declared field)"

    let private shortSlotName (snapshot: Snapshot) slot =
        match slot with
        | RecipePatternSlot.Value -> "Value"
        | RecipePatternSlot.Error -> "Error"
        | RecipePatternSlot.Field fieldId ->
            tryFindFieldWithOwner snapshot fieldId
            |> Option.map (fun (field, _) -> $"'{field.DisplayName}'")
            |> Option.defaultValue $"'{StableId.value fieldId}'"

    /// One visible frame in a diagnostic, e.g. `arm 'Stop' of MatchUnion on 'Action' (binds 'member')`.
    let private describeScopeFrame (snapshot: Snapshot) (frame: ScopeFrame<'Binding>) =
        let opener =
            match frame.Kind with
            | ScopeFrameKind.UnionArm variantIds ->
                snapshot.Types
                |> List.tryPick (fun typeEntity ->
                    match typeEntity.Kind with
                    | SemanticTypeKind.Union variants when variants |> List.exists (fun variant -> List.contains variant.Id variantIds) ->
                        let names =
                            variants
                            |> List.filter (fun variant -> List.contains variant.Id variantIds)
                            |> List.map (fun variant -> variant.DisplayName)
                            |> String.concat "|"
                        Some $"arm '{names}' of MatchUnion on '{typeEntity.DisplayName}'"
                    | SemanticTypeKind.Union _
                    | SemanticTypeKind.Product _ -> None)
                |> Option.defaultValue ("arm '" + (variantIds |> List.map StableId.value |> String.concat "|") + "' of MatchUnion")
            | ScopeFrameKind.BindBody -> "Bind body"
            | ScopeFrameKind.OptionSome -> "Some arm of MatchOption"
            | ScopeFrameKind.OptionNone -> "None arm of MatchOption"
            | ScopeFrameKind.ResultOk -> "Ok arm of MatchResult"
            | ScopeFrameKind.ResultErr -> "Err arm of MatchResult"
        let bindings =
            match frame.Slots with
            | [] -> "no bindings"
            | slots -> "binds " + (slots |> List.map (fst >> shortSlotName snapshot) |> String.concat ", ")
        $"{opener} ({bindings})"

    /// The frames visible at a pattern reference, innermost first, and the depths where the slot is bound.
    let private describeVisibleFrames (snapshot: Snapshot) (frames: ScopeFrame<'Binding> list) slot =
        let listing =
            match frames with
            | [] -> "No frames are visible here; only a Bind body or a match arm opens one."
            | _ ->
                let entries =
                    frames
                    |> List.mapi (fun depth frame -> $"depth {depth} = {describeScopeFrame snapshot frame}")
                    |> String.concat "; "
                $"Visible frames, innermost first: {entries}."
        let foundAt =
            frames
            |> List.indexed
            |> List.choose (fun (depth, frame) ->
                if frame.Slots |> List.exists (fun (candidate, _) -> candidate = slot) then Some depth else None)
        match foundAt with
        | [] -> listing
        | [ depth ] -> $"The slot is found at depth {depth}. {listing}"
        | depths ->
            let text = depths |> List.map string |> String.concat ", "
            $"The slot is found at depths {text}; choose the frame that binds the intended value. Empty frames still count toward scope depth. {listing}"

    let internal resolvePatternType (snapshot: Snapshot) (frames: PatternTypeFrame list) (reference: RecipePatternReference) =
        let slotText () = describePatternSlot snapshot reference.Slot
        if reference.ScopeDepth < 0 then
            Error(
                diagnostic
                    "PureExpressionPatternScopeDepthInvalid"
                    $"Pattern-reference scope depth must be non-negative (slot {slotText ()}). {describeVisibleFrames snapshot frames reference.Slot}"
            )
        else
            match List.tryItem reference.ScopeDepth frames with
            | None ->
                Error(
                    diagnostic
                        "PureExpressionPatternScopeNotFound"
                        $"Pattern-reference scope depth {reference.ScopeDepth} for slot {slotText ()} is not visible here. {describeVisibleFrames snapshot frames reference.Slot}"
                )
            | Some frame ->
                match frame.Slots |> List.tryFind (fun (slot, _) -> slot = reference.Slot) with
                | Some(_, typeRef) -> Ok typeRef
                | None ->
                    Error(
                        diagnostic
                            "PureExpressionPatternSlotNotFound"
                            $"Pattern-reference slot {slotText ()} does not exist in scope depth {reference.ScopeDepth}. {describeVisibleFrames snapshot frames reference.Slot}"
                    )

    /// Successful inference results keyed by recipe identity and scope, so materialization, which
    /// recovers the input type of every match and bind, infers each subtree once. Valid only
    /// for one snapshot index and owner.
    type internal InferenceCache() =
        let find (table: System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * 'T) list>) frames recipe =
            match table.TryGetValue recipe with
            | true, entries -> entries |> List.tryPick (fun (known, value) -> if known = frames then Some value else None)
            | false, _ -> None
        let add (table: System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * 'T) list>) frames recipe value =
            let entries =
                match table.TryGetValue recipe with
                | true, entries -> entries
                | false, _ -> []
            table[recipe] <- (frames, value) :: entries
        let values = System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * TypeRef) list>(HashIdentity.Reference)
        let completions =
            System.Collections.Generic.Dictionary<ExpressionRecipe, (PatternTypeFrame list * GraphCompletion) list>(HashIdentity.Reference)
        member _.TryValue(frames, recipe) = find values frames recipe
        member _.AddValue(frames, recipe, value) = add values frames recipe value
        member _.TryCompletion(frames, recipe) = find completions frames recipe
        member _.AddCompletion(frames, recipe, value) = add completions frames recipe value

    /// The `OperationSignatures` entry of each `ExpressionRecipe` case, indexed by union tag.
    let private fixedSignatureEntries =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<ExpressionRecipe>)
        |> Array.map (fun case -> OperationSignatures.tryFind (string (System.Char.ToLowerInvariant case.Name[0]) + case.Name.Substring 1))

    let private readRecipeTag =
        Microsoft.FSharp.Reflection.FSharpValue.PreComputeUnionTagReader(typeof<ExpressionRecipe>)

    let internal fixedSignatureEntry (recipe: ExpressionRecipe) =
        match fixedSignatureEntries[readRecipeTag recipe] with
        | Some entry -> entry
        | None -> invalidOp $"Recipe kind '{recipeKindName recipe}' is inferred from OperationSignatures but has no entry there."
