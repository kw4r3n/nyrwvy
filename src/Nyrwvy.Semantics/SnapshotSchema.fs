namespace Nyrwvy.Core

/// A persisted construct that an older snapshot schema cannot hold. Each has one row in
/// `SnapshotSchema.minimumVersions`.
[<RequireQualifiedAccess>]
type SnapshotSchemaFeature =
    /// OptionGetOrDefault, OptionMap, OptionAndThen, ResultMap, ResultMapErr and ResultAndThen.
    | OptionResultCombinators
    | ExceptionIsCancellation
    | ExceptionBoundary
    | CancellationShield
    /// CapabilityConstruct.
    | CapabilityConstruct
    | OneShot
    /// Abort.
    | FunctionAbort
    /// TaskWait with a limit operand.
    | TimedTaskWait
    /// Int64CheckedAdd, Int64CheckedSubtract and Int64CheckedMultiply.
    | CheckedInt64Arithmetic
    /// A MatchUnion arm that names several variants.
    | SharedUnionMatchArm
    /// StringToUpper.
    | StringToUpper
    /// Float64Round.
    | Float64Round
    /// The Map<K, V> type anywhere, or any Map node kind.
    | Map
    /// The Set<T> type anywhere, or any Set node kind.
    | Set
    /// ListMinBy.
    | ListMinBy
    /// ListLast and ListDropLast.
    | ListLastAndDropLast
    /// ListIndexOf, ListLastIndexOf and ListFindIndex.
    | ListIndexSearch
    /// StringReplace, StringTrim, StringTrimStart and StringTrimEnd.
    | StringReplaceAndTrim
    /// SetIsSubset and MapUnion.
    | SetIsSubsetAndMapUnion
    /// Int64BitAnd, Int64BitOr, Int64BitXor, Int64BitNot, Int64ShiftLeft, Int64ShiftRight, Float64Min and
    /// Float64Max.
    | Int64BitwiseAndFloat64MinMax
    /// ListSetAt, ListInsertAt and ListRemoveAt.
    | ListPositionalUpdate
    /// ListZipWith and ListFlatMap.
    | ListZipWithAndFlatMap
    /// ListScan.
    | ListScan
    /// A Product or Union with type parameters, a named type with type arguments, or a construct
    /// node with type arguments.
    | GenericTypes
    /// ResultGetOrDefault, OptionOkOr, ResultOkValue, ResultErrValue, OptionIsSome and ResultIsOk.
    | OptionResultAccessors
    /// ListContains.
    | ListContains
    /// StringEncodeUtf8.
    | StringEncodeUtf8
    /// SetFold, SetFilter and SetMap.
    | SetCallbacks
    /// ListMaxBy.
    | ListMaxBy
    /// Float64FloorModulo.
    | Float64FloorModulo
    /// StringLastIndexOfBefore.
    | StringLastIndexOfBefore
    /// A function whose declared type parameter list is not the one its parameter types imply
    /// (first-appearance order); written as `typeParameters` on the function, with nameless
    /// occurrences, from schema 21 on.
    | DeclaredFunctionTypeParameters
    /// A private (non-exported) declaration. From this version on a declaration's visibility is
    /// explicit: `exported` is written for an exported declaration and omitted for a private one.
    /// Older versions have no visibility, and every declaration they hold reads as exported.
    | DeclarationVisibility

/// One persisted snapshot schema: its JSON `schemaVersion` and its Nyason schema identifier.
/// The two number their versions separately but move together. The Nyason v0 codec was removed
/// (2026-10-24); its identifier survives as the schema name in migration reports.
type SnapshotSchemaVersion =
    { Json: int
      Nyason: string }

/// The one place that says which snapshot schema each persisted construct needs. Ordinary writes
/// use the current version; the frozen fingerprint encoding uses each graph's minimum version.
/// Both codecs reject a document whose declared version is lower than a construct in it needs.
/// A new persisted construct gets a row here, normally with a new version; `featureOfNodeKind`
/// has no wildcard, so a new node kind does not compile until it is assigned. See docs/specs/schema-evolution-policy.md.
[<RequireQualifiedAccess>]
module SnapshotSchema =
    let private version json nyason = { Json = json; Nyason = nyason }

    /// Graphs that use none of the constructs below.
    let baseline = version 9 "nayamnyam.snapshot.v5"
    let private v10 = version 10 "nayamnyam.snapshot.v6"
    let private v11 = version 11 "nayamnyam.snapshot.v7"
    let private v12 = version 12 "nayamnyam.snapshot.v8"
    let private v13 = version 13 "nayamnyam.snapshot.v9"
    let private v14 = version 14 "nayamnyam.snapshot.v10"
    let private v15 = version 15 "nayamnyam.snapshot.v11"

    let private v16 = version 16 "nayamnyam.snapshot.v12"

    let private v17 = version 17 "nayamnyam.snapshot.v13"

    let private v18 = version 18 "nayamnyam.snapshot.v14"

    let private v19 = version 19 "nayamnyam.snapshot.v15"

    let private v20 = version 20 "nayamnyam.snapshot.v16"

    let private v21 = version 21 "nayamnyam.snapshot.v17"

    let private v22 = version 22 "nayamnyam.snapshot.v18"

    /// Every version the codecs read, oldest first.
    let supported = [ baseline; v10; v11; v12; v13; v14; v15; v16; v17; v18; v19; v20; v21; v22 ]

    /// The newest version: the one ordinary writers and migration write every snapshot at, whatever
    /// constructs it uses (docs/specs/schema-evolution-policy.md, deprecation policy).
    let current = v22

    /// The first version the record form (`.nyrec`) was written at; its reader accepts this one onward.
    let firstRecord = v21

    /// The first version that writes function type parameters as a declared list and type
    /// parameter occurrences without display names; older documents derive the list on reading.
    let declaredFunctionTypeParameters = v21

    /// The minimum schema version of each construct, with the name diagnostics use for it.
    let minimumVersions: (SnapshotSchemaFeature * SnapshotSchemaVersion * string) list =
        [ SnapshotSchemaFeature.ListScan, v22, "ListScan"
          SnapshotSchemaFeature.DeclaredFunctionTypeParameters, v21, "Declared function type parameters"
          SnapshotSchemaFeature.DeclarationVisibility, v21, "A private declaration"
          SnapshotSchemaFeature.SetCallbacks, v20, "SetFold, SetFilter and SetMap"
          SnapshotSchemaFeature.ListMaxBy, v20, "ListMaxBy"
          SnapshotSchemaFeature.Float64FloorModulo, v20, "Float64FloorModulo"
          SnapshotSchemaFeature.StringLastIndexOfBefore, v20, "StringLastIndexOfBefore"
          SnapshotSchemaFeature.OptionResultAccessors, v19, "Option and Result accessors"
          SnapshotSchemaFeature.ListContains, v19, "ListContains"
          SnapshotSchemaFeature.StringEncodeUtf8, v19, "StringEncodeUtf8"
          SnapshotSchemaFeature.GenericTypes, v18, "Generic Product and Union types"
          SnapshotSchemaFeature.ExceptionIsCancellation, v17, "Exception cancellation classification"
          SnapshotSchemaFeature.StringReplaceAndTrim, v17, "StringReplace and StringTrim"
          SnapshotSchemaFeature.ListZipWithAndFlatMap, v17, "ListZipWith and ListFlatMap"
          SnapshotSchemaFeature.OptionResultCombinators, v17, "Option and Result combinators"
          SnapshotSchemaFeature.ExceptionBoundary, v16, "Host exception boundary"
          SnapshotSchemaFeature.CancellationShield, v15, "Lexical cancellation shield"
          SnapshotSchemaFeature.OneShot, v14, "Scoped one-shot handoff"
          SnapshotSchemaFeature.CapabilityConstruct, v10, "Capability construction"
          SnapshotSchemaFeature.FunctionAbort, v11, "Function abort"
          SnapshotSchemaFeature.TimedTaskWait, v12, "Timed TaskWait"
          SnapshotSchemaFeature.CheckedInt64Arithmetic, v13, "Checked Int64 arithmetic"
          SnapshotSchemaFeature.SharedUnionMatchArm, v13, "A MatchUnion arm with several variants"
          SnapshotSchemaFeature.StringToUpper, v13, "StringToUpper"
          SnapshotSchemaFeature.Float64Round, v13, "Float64Round"
          SnapshotSchemaFeature.Map, v13, "Map"
          SnapshotSchemaFeature.Set, v13, "Set"
          SnapshotSchemaFeature.ListMinBy, v13, "ListMinBy"
          SnapshotSchemaFeature.ListLastAndDropLast, v13, "ListLast and ListDropLast"
          SnapshotSchemaFeature.ListIndexSearch, v13, "ListIndexOf, ListLastIndexOf and ListFindIndex"
          SnapshotSchemaFeature.SetIsSubsetAndMapUnion, v17, "SetIsSubset and MapUnion"
          SnapshotSchemaFeature.Int64BitwiseAndFloat64MinMax, v17, "Int64 bitwise operations and shifts, Float64Min and Float64Max"
          SnapshotSchemaFeature.ListPositionalUpdate, v17, "ListSetAt, ListInsertAt and ListRemoveAt" ]

    let private rows =
        minimumVersions |> List.map (fun (feature, minimum, name) -> feature, (minimum, name)) |> Map.ofList

    let minimumVersion (feature: SnapshotSchemaFeature) = Map.find feature rows |> fst

    let featureName (feature: SnapshotSchemaFeature) = Map.find feature rows |> snd

    let tryJson (json: int) = supported |> List.tryFind (fun candidate -> candidate.Json = json)

    /// Whether documents of `declared` carry declaration visibility: an exported declaration is
    /// written with an explicit marker and an omitted marker means private. Below that version a
    /// document has no visibility and every declaration in it reads as exported, which is what the
    /// declarations meant before visibility existed; the bytes and fingerprints of such a
    /// document are unchanged.
    let writesVisibility (declared: SnapshotSchemaVersion) =
        declared.Json >= (minimumVersion SnapshotSchemaFeature.DeclarationVisibility).Json

    /// The construct a node's own kind needs, if any; nested graphs are visited separately.
    let featureOfNodeKind (kind: SemanticNodeKind): SnapshotSchemaFeature option =
        match kind with
        | SemanticNodeKind.OptionGetOrDefault _
        | SemanticNodeKind.OptionMap _
        | SemanticNodeKind.OptionAndThen _
        | SemanticNodeKind.ResultMap _
        | SemanticNodeKind.ResultMapErr _
        | SemanticNodeKind.ResultAndThen _ -> Some SnapshotSchemaFeature.OptionResultCombinators
        | SemanticNodeKind.ResultGetOrDefault _
        | SemanticNodeKind.OptionOkOr _
        | SemanticNodeKind.ResultOkValue _
        | SemanticNodeKind.ResultErrValue _
        | SemanticNodeKind.OptionIsSome _
        | SemanticNodeKind.ResultIsOk _ -> Some SnapshotSchemaFeature.OptionResultAccessors
        | SemanticNodeKind.ListContains _ -> Some SnapshotSchemaFeature.ListContains
        | SemanticNodeKind.SetFold _
        | SemanticNodeKind.SetFilter _
        | SemanticNodeKind.SetMap _ -> Some SnapshotSchemaFeature.SetCallbacks
        | SemanticNodeKind.ListMaxBy _ -> Some SnapshotSchemaFeature.ListMaxBy
        | SemanticNodeKind.Float64FloorModulo _ -> Some SnapshotSchemaFeature.Float64FloorModulo
        | SemanticNodeKind.StringLastIndexOfBefore _ -> Some SnapshotSchemaFeature.StringLastIndexOfBefore
        | SemanticNodeKind.StringEncodeUtf8 _ -> Some SnapshotSchemaFeature.StringEncodeUtf8
        | SemanticNodeKind.ExceptionIsCancellation _ -> Some SnapshotSchemaFeature.ExceptionIsCancellation
        | SemanticNodeKind.ExceptionCatch _
        | SemanticNodeKind.ExceptionMessage _
        | SemanticNodeKind.ExceptionRethrow _ -> Some SnapshotSchemaFeature.ExceptionBoundary
        | SemanticNodeKind.CancellationShield _ -> Some SnapshotSchemaFeature.CancellationShield
        | SemanticNodeKind.OneShotCreate _
        | SemanticNodeKind.OneShotComplete _
        | SemanticNodeKind.OneShotWait _ -> Some SnapshotSchemaFeature.OneShot
        | SemanticNodeKind.CapabilityConstruct _ -> Some SnapshotSchemaFeature.CapabilityConstruct
        | SemanticNodeKind.Abort _ -> Some SnapshotSchemaFeature.FunctionAbort
        | SemanticNodeKind.TaskWait(_, _, Some _) -> Some SnapshotSchemaFeature.TimedTaskWait
        | SemanticNodeKind.MatchUnion(_, arms) when arms |> List.exists (fun arm -> List.length arm.VariantIds > 1) ->
            Some SnapshotSchemaFeature.SharedUnionMatchArm
        | SemanticNodeKind.Int64CheckedAdd _
        | SemanticNodeKind.Int64CheckedSubtract _
        | SemanticNodeKind.Int64CheckedMultiply _ -> Some SnapshotSchemaFeature.CheckedInt64Arithmetic
        | SemanticNodeKind.StringToUpper _ -> Some SnapshotSchemaFeature.StringToUpper
        | SemanticNodeKind.StringReplace _
        | SemanticNodeKind.StringTrim _
        | SemanticNodeKind.StringTrimStart _
        | SemanticNodeKind.StringTrimEnd _ -> Some SnapshotSchemaFeature.StringReplaceAndTrim
        | SemanticNodeKind.Float64Round _ -> Some SnapshotSchemaFeature.Float64Round
        | SemanticNodeKind.SetIsSubset _
        | SemanticNodeKind.MapUnion _ -> Some SnapshotSchemaFeature.SetIsSubsetAndMapUnion
        | SemanticNodeKind.MapEmpty _
        | SemanticNodeKind.MapValues _
        | SemanticNodeKind.MapKeys _
        | SemanticNodeKind.MapSize _
        | SemanticNodeKind.MapRemove _
        | SemanticNodeKind.MapContainsKey _
        | SemanticNodeKind.MapGet _
        | SemanticNodeKind.MapGetOrDefault _
        | SemanticNodeKind.MapInsert _
        | SemanticNodeKind.MapFold _
        | SemanticNodeKind.MapFromList _
        | SemanticNodeKind.MapFilter _
        | SemanticNodeKind.MapMapValues _ -> Some SnapshotSchemaFeature.Map
        | SemanticNodeKind.SetEmpty _
        | SemanticNodeKind.SetToList _
        | SemanticNodeKind.SetFromList _
        | SemanticNodeKind.SetSize _
        | SemanticNodeKind.SetInsert _
        | SemanticNodeKind.SetRemove _
        | SemanticNodeKind.SetUnion _
        | SemanticNodeKind.SetIntersection _
        | SemanticNodeKind.SetDifference _
        | SemanticNodeKind.SetContains _ -> Some SnapshotSchemaFeature.Set
        | SemanticNodeKind.ListMinBy _ -> Some SnapshotSchemaFeature.ListMinBy
        | SemanticNodeKind.ListLast _
        | SemanticNodeKind.ListDropLast _ -> Some SnapshotSchemaFeature.ListLastAndDropLast
        | SemanticNodeKind.ListIndexOf _
        | SemanticNodeKind.ListLastIndexOf _
        | SemanticNodeKind.ListFindIndex _ -> Some SnapshotSchemaFeature.ListIndexSearch
        | SemanticNodeKind.Int64BitAnd _
        | SemanticNodeKind.Int64BitOr _
        | SemanticNodeKind.Int64BitXor _
        | SemanticNodeKind.Int64BitNot _
        | SemanticNodeKind.Int64ShiftLeft _
        | SemanticNodeKind.Int64ShiftRight _
        | SemanticNodeKind.Float64Min _
        | SemanticNodeKind.Float64Max _ -> Some SnapshotSchemaFeature.Int64BitwiseAndFloat64MinMax
        | SemanticNodeKind.ListSetAt _
        | SemanticNodeKind.ListInsertAt _
        | SemanticNodeKind.ListRemoveAt _ -> Some SnapshotSchemaFeature.ListPositionalUpdate
        | SemanticNodeKind.ListZipWith _
        | SemanticNodeKind.ListFlatMap _ -> Some SnapshotSchemaFeature.ListZipWithAndFlatMap
        | SemanticNodeKind.ListScan _ -> Some SnapshotSchemaFeature.ListScan
        | SemanticNodeKind.ProductConstruct(_, _ :: _, _)
        | SemanticNodeKind.UnionConstruct(_, _ :: _, _) -> Some SnapshotSchemaFeature.GenericTypes
        | SemanticNodeKind.TaskWait _
        | SemanticNodeKind.MatchUnion _
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.StringIsEmpty _
        | SemanticNodeKind.Int64ToString _
        | SemanticNodeKind.Int64ToHexString _
        | SemanticNodeKind.StringParseInt64 _
        | SemanticNodeKind.StringParseInt64Hex _
        | SemanticNodeKind.Float64ToString _
        | SemanticNodeKind.StringParseFloat64 _
        | SemanticNodeKind.StringCodePoints _
        | SemanticNodeKind.StringToLower _
        | SemanticNodeKind.StringDecodeUtf8Lossy _
        | SemanticNodeKind.Float64RoundToInt64 _
        | SemanticNodeKind.Float64FloorToInt64 _
        | SemanticNodeKind.Float64Sqrt _
        | SemanticNodeKind.Float64Abs _
        | SemanticNodeKind.Float64Negate _
        | SemanticNodeKind.Float64Floor _
        | SemanticNodeKind.Float64Ceiling _
        | SemanticNodeKind.Float64Truncate _
        | SemanticNodeKind.StringUtf8ByteCount _
        | SemanticNodeKind.StringFromCodePoint _
        | SemanticNodeKind.StringToCodePoint _
        | SemanticNodeKind.StringLength _
        | SemanticNodeKind.StringSlice _
        | SemanticNodeKind.StringContains _
        | SemanticNodeKind.StringIndexOf _
        | SemanticNodeKind.StringSpan _
        | SemanticNodeKind.StringSpanBack _
        | SemanticNodeKind.StringLastIndexOf _
        | SemanticNodeKind.StringStartsWith _
        | SemanticNodeKind.StringEndsWith _
        | SemanticNodeKind.StringSplit _
        | SemanticNodeKind.StringSplitLines _
        | SemanticNodeKind.StringJoin _
        | SemanticNodeKind.StringEqual _
        | SemanticNodeKind.StringLessThan _
        | SemanticNodeKind.ValueEqual _
        | SemanticNodeKind.StringConcat _
        | SemanticNodeKind.Int64WrappingAdd _
        | SemanticNodeKind.Int64WrappingSubtract _
        | SemanticNodeKind.Int64WrappingMultiply _
        | SemanticNodeKind.Int64FloorDivide _
        | SemanticNodeKind.Int64FloorModulo _
        | SemanticNodeKind.Int64Min _
        | SemanticNodeKind.Int64Max _
        | SemanticNodeKind.ListRange _
        | SemanticNodeKind.StringRepeat _
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
        | SemanticNodeKind.Int64ToFloat64 _
        | SemanticNodeKind.Int64WrappingAbs _
        | SemanticNodeKind.Bits32FromInt64 _
        | SemanticNodeKind.Bits32ToInt64 _
        | SemanticNodeKind.Bits32Xor _
        | SemanticNodeKind.Bits32And _
        | SemanticNodeKind.Bits32Or _
        | SemanticNodeKind.Bits32Not _
        | SemanticNodeKind.Bits32Add _
        | SemanticNodeKind.Bits32RotateRight _
        | SemanticNodeKind.Bits32ShiftRight _
        | SemanticNodeKind.Bits32ShiftLeft _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.ListConstruct _
        | SemanticNodeKind.ListLength _
        | SemanticNodeKind.ListReverse _
        | SemanticNodeKind.ListAppendItem _
        | SemanticNodeKind.ListItemAt _
        | SemanticNodeKind.ListSlice _
        | SemanticNodeKind.ListConcat _
        | SemanticNodeKind.ListFold _
        | SemanticNodeKind.ParallelAll _
        | SemanticNodeKind.CancellationRequested
        | SemanticNodeKind.UseResource _
        | SemanticNodeKind.TaskScope _
        | SemanticNodeKind.TaskStart _
        | SemanticNodeKind.TaskCancel _
        | SemanticNodeKind.ListEffectFold _
        | SemanticNodeKind.ListMap _
        | SemanticNodeKind.ListFilter _
        | SemanticNodeKind.ListSortBy _
        | SemanticNodeKind.ListAny _
        | SemanticNodeKind.ListAll _
        | SemanticNodeKind.DirectCall _
        | SemanticNodeKind.FunctionReference _
        | SemanticNodeKind.FunctionInvoke _
        | SemanticNodeKind.CapabilityInvoke _
        | SemanticNodeKind.ProductConstruct _
        | SemanticNodeKind.FieldAccess _
        | SemanticNodeKind.ProductUpdate _
        | SemanticNodeKind.UnionConstruct _
        | SemanticNodeKind.OptionNone _
        | SemanticNodeKind.OptionSome _
        | SemanticNodeKind.ResultOk _
        | SemanticNodeKind.ResultErr _
        | SemanticNodeKind.BoolNot _
        | SemanticNodeKind.BoolAnd _
        | SemanticNodeKind.BoolOr _
        | SemanticNodeKind.Conditional _
        | SemanticNodeKind.Bind _
        | SemanticNodeKind.MatchOption _
        | SemanticNodeKind.MatchResult _ -> None

    let rec private typeFeatures (typeRef: TypeRef): SnapshotSchemaFeature list =
        match typeRef with
        | TypeRef.Primitive _
        | TypeRef.Named(_, [])
        | TypeRef.Capability _
        | TypeRef.TypeParameter _ -> []
        | TypeRef.Named(_, arguments) -> SnapshotSchemaFeature.GenericTypes :: (arguments |> List.collect typeFeatures)
        | TypeRef.Function(parameters, result) -> (result :: parameters) |> List.collect typeFeatures
        | TypeRef.BuiltinGeneric(generic, arguments) ->
            let own =
                match generic with
                | BuiltinGenericType.Exception -> [ SnapshotSchemaFeature.ExceptionBoundary ]
                | BuiltinGenericType.OneShot -> [ SnapshotSchemaFeature.OneShot ]
                | BuiltinGenericType.Map -> [ SnapshotSchemaFeature.Map ]
                | BuiltinGenericType.Set -> [ SnapshotSchemaFeature.Set ]
                | BuiltinGenericType.Option
                | BuiltinGenericType.Result
                | BuiltinGenericType.List
                | BuiltinGenericType.Task
                | BuiltinGenericType.TaskScope -> []
            own @ (arguments |> List.collect typeFeatures)

    /// Every construct with a row in `minimumVersions` that the snapshot uses, in declarations,
    /// signatures, node kinds, node payload types and pattern binding types.
    let usedFeatures (snapshot: Snapshot): Set<SnapshotSchemaFeature> =
        let declarationTypes =
            [ for semanticType in snapshot.Types do
                  match semanticType.Kind with
                  | SemanticTypeKind.Product fields -> yield! fields |> List.map (fun field -> field.Type)
                  | SemanticTypeKind.Union variants ->
                      for variant in variants do
                          yield! variant.Fields |> List.map (fun field -> field.Type)
              for capability in snapshot.Capabilities do
                  for operation in capability.Operations do
                      yield! operation.Parameters |> List.map (fun parameter -> parameter.Type)
                      yield operation.ResultType
                      yield! Option.toList operation.FailureType
              for fn in snapshot.Functions do
                  yield! fn.Parameters |> List.map (fun parameter -> parameter.Type)
                  yield fn.ResultType ]
        let nodeFeatures (node: SemanticNode) =
            let bindingTypes =
                SemanticNodeShape.nestedScopes node
                |> List.collect (fun scope -> scope.Bindings |> List.map (fun binding -> (NestedBinding.pattern binding).Type))
            Option.toList (featureOfNodeKind node.Kind)
            @ ((SemanticNodeShape.payloadTypeRefs node @ bindingTypes) |> List.collect typeFeatures)
        let hasPrivateDeclaration =
            (snapshot.Types |> List.exists (fun item -> not item.Exported))
            || (snapshot.Effects |> List.exists (fun item -> not item.Exported))
            || (snapshot.Capabilities |> List.exists (fun item -> not item.Exported))
            || (snapshot.Functions |> List.exists (fun item -> not item.Exported))
        [ yield! declarationTypes |> List.collect typeFeatures
          if hasPrivateDeclaration then yield SnapshotSchemaFeature.DeclarationVisibility
          if snapshot.Types |> List.exists (fun semanticType -> not (List.isEmpty semanticType.TypeParameters)) then
              yield SnapshotSchemaFeature.GenericTypes
          if snapshot.Functions |> List.exists (fun fn -> not (TypeParameters.isDerived fn)) then
              yield SnapshotSchemaFeature.DeclaredFunctionTypeParameters
          for fn in snapshot.Functions do
              yield! ValueGraph.collectNodes fn.Body |> List.collect nodeFeatures ]
        |> Set.ofList

    /// The smallest supported version that holds every construct the snapshot uses.
    let required (snapshot: Snapshot): SnapshotSchemaVersion =
        usedFeatures snapshot
        |> Seq.map minimumVersion
        |> Seq.fold (fun current candidate -> if candidate.Json > current.Json then candidate else current) baseline

    /// The first construct (in table order) that the snapshot uses and the declared version
    /// cannot hold, with the version it needs and its name.
    let firstUnsupported (declared: SnapshotSchemaVersion) (snapshot: Snapshot) =
        let used = usedFeatures snapshot
        minimumVersions
        |> List.tryFind (fun (feature, minimum, _) -> Set.contains feature used && declared.Json < minimum.Json)
