namespace Nyrwvy.Core.Tests

open System
open System.IO
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The snapshot schema table (`SnapshotSchema.minimumVersions`): a snapshot is written at the
/// smallest version that holds what it uses, and both codecs reject a document that declares the
/// version just below a construct in it.
module SnapshotSchemaTests =
    let private unwrap label = function Ok value -> value | Error error -> failwithf "%s: %A" label error
    let private id<'tag> value = StableId.createForTesting<'tag> value
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private generic kind arguments = TypeRef.BuiltinGeneric(kind, arguments)
    let private listOf item = generic BuiltinGenericType.List [ item ]
    let private optionOf item = generic BuiltinGenericType.Option [ item ]
    let private slot = ExpressionRecipe.ParameterSlot

    let private shapeTypeId = id<TypeTag> "type-shape"
    let private circleId = id<VariantTag> "variant-circle"
    let private squareId = id<VariantTag> "variant-square"
    let private readEffectId = id<EffectTag> "effect-read"
    let private valuesId = id<CapabilityTag> "capability-values"
    let private readOperationId = id<CapabilityOperationTag> "operation-read"

    /// Declarations only, so it needs the baseline schema.
    let private initial: Snapshot =
        { Snapshot.empty (id<SnapshotTag> "snapshot-schema") with
            Types =
                [ { TypeParameters = []; Id = shapeTypeId
                    DisplayName = "Shape"
                    Kind =
                      SemanticTypeKind.Union
                          [ { Id = circleId; DisplayName = "Circle"; Fields = [] }
                            { Id = squareId; DisplayName = "Square"; Fields = [] } ] } ]
            Effects = [ { Id = readEffectId; DisplayName = "Read" } ]
            Capabilities =
                [ { Id = valuesId
                    DisplayName = "Values"
                    Operations =
                      [ { Id = readOperationId
                          DisplayName = "read"
                          Parameters = []
                          ResultType = int64Type
                          FailureType = None
                          EffectId = readEffectId } ] } ] }

    type private Create = string -> (string * TypeRef) list -> TypeRef -> EffectId list -> ExpressionRecipe -> SemanticFunction

    /// Publishes the functions `define` creates on top of `initial` and returns the stored snapshot.
    let private build (define: Create -> unit): Snapshot =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-snapshot-schema-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize initial |> unwrap "serialize initial")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            let create name parameters resultType effects body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      DisplayName = name
                      Parameters = parameters |> List.map (fun (displayName, parameterType) -> { DisplayName = displayName; Type = parameterType })
                      ResultType = resultType
                      RequiredEffects = effects
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap ("commit " + name) |> ignore
                session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            define create
            session.Close()
            SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "read back"
        finally
            Directory.Delete(root, true)

    /// One program per gated construct (two for the types that can appear without their nodes).
    let private cases: (string * SnapshotSchemaFeature * (Create -> unit)) list =
        [ "option get or default", SnapshotSchemaFeature.OptionResultCombinators, (fun create ->
              create "getOr" [ "option", optionOf stringType; "fallback", stringType ] stringType [] (ExpressionRecipe.OptionGetOrDefault(slot 0, slot 1)) |> ignore)
          "result is ok", SnapshotSchemaFeature.OptionResultAccessors, (fun create ->
              create "isOk" [ "result", generic BuiltinGenericType.Result [ stringType; int64Type ] ] boolType [] (ExpressionRecipe.ResultIsOk(slot 0)) |> ignore)
          "option ok or", SnapshotSchemaFeature.OptionResultAccessors, (fun create ->
              create "okOr" [ "option", optionOf stringType; "error", int64Type ] (generic BuiltinGenericType.Result [ stringType; int64Type ]) [] (ExpressionRecipe.OptionOkOr(slot 0, slot 1)) |> ignore)
          "exception classification", SnapshotSchemaFeature.ExceptionIsCancellation, (fun create ->
              create "isCancellation" [ "exception", generic BuiltinGenericType.Exception [] ] boolType [] (ExpressionRecipe.ExceptionIsCancellation(slot 0)) |> ignore)
          "exception node", SnapshotSchemaFeature.ExceptionBoundary, (fun create ->
              let result = generic BuiltinGenericType.Result [ stringType; stringType ]
              let body = create "body" [ "item", TypeRef.Primitive PrimitiveType.Unit ] result [] (ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.StringLiteral "ok"))
              let handler = create "handler" [ "exception", generic BuiltinGenericType.Exception [] ] result [] (ExpressionRecipe.ExceptionRethrow(slot 0, result))
              create "caught" [] result [] (ExpressionRecipe.ExceptionCatch(body.Id, [], handler.Id, [])) |> ignore)
          "exception type only", SnapshotSchemaFeature.ExceptionBoundary, (fun create ->
              let exceptionType = generic BuiltinGenericType.Exception []
              create "sameException" [ "exception", exceptionType ] exceptionType [] (slot 0) |> ignore)
          "cancellation shield", SnapshotSchemaFeature.CancellationShield, (fun create ->
              let result = generic BuiltinGenericType.Result [ boolType; stringType ]
              let body = create "body" [ "item", TypeRef.Primitive PrimitiveType.Unit ] result [] (ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.CancellationRequested))
              create "shield" [] result [] (ExpressionRecipe.CancellationShield(body.Id, [])) |> ignore)
          "capability construction", SnapshotSchemaFeature.CapabilityConstruct, (fun create ->
              let ten = create "ten" [] int64Type [] (ExpressionRecipe.Int64Literal 10L)
              create "values" [] (TypeRef.Capability valuesId) [] (ExpressionRecipe.CapabilityConstruct(valuesId, [ readOperationId, ten.Id ])) |> ignore)
          "one-shot node", SnapshotSchemaFeature.OneShot, (fun create ->
              create "cell" [ "scope", generic BuiltinGenericType.TaskScope [] ] (generic BuiltinGenericType.OneShot [ stringType ]) [] (ExpressionRecipe.OneShotCreate(slot 0, stringType)) |> ignore)
          "one-shot type only", SnapshotSchemaFeature.OneShot, (fun create ->
              let cell = generic BuiltinGenericType.OneShot [ stringType ]
              create "sameCell" [ "cell", cell ] cell [] (slot 0) |> ignore)
          "abort", SnapshotSchemaFeature.FunctionAbort, (fun create ->
              let result = generic BuiltinGenericType.Result [ stringType; stringType ]
              create "stop" [] result [] (ExpressionRecipe.Abort(ExpressionRecipe.StringLiteral "stop")) |> ignore)
          "timed task wait", SnapshotSchemaFeature.TimedTaskWait, (fun create ->
              let tasks = listOf (generic BuiltinGenericType.Task [ int64Type ])
              create "waitAll" [ "tasks", tasks; "limit", optionOf int64Type ] (listOf (optionOf int64Type)) []
                  (ExpressionRecipe.TaskWait(slot 0, TaskWaitMode.All, Some(slot 1))) |> ignore)
          "checked add", SnapshotSchemaFeature.CheckedInt64Arithmetic, (fun create ->
              create "add" [ "a", int64Type; "b", int64Type ] (optionOf int64Type) [] (ExpressionRecipe.Int64CheckedAdd(slot 0, slot 1)) |> ignore)
          "shared match arm", SnapshotSchemaFeature.SharedUnionMatchArm, (fun create ->
              create "isShape" [ "shape", TypeRef.Named(shapeTypeId, []) ] boolType []
                  (ExpressionRecipe.MatchUnion(slot 0, [ [ circleId; squareId ], ExpressionRecipe.BoolLiteral true ])) |> ignore)
          "upper case", SnapshotSchemaFeature.StringToUpper, (fun create ->
              create "upper" [ "text", stringType ] stringType [] (ExpressionRecipe.StringToUpper(slot 0)) |> ignore)
          "string replace", SnapshotSchemaFeature.StringReplaceAndTrim, (fun create ->
              create "replace" [ "text", stringType ] stringType []
                  (ExpressionRecipe.StringReplace(slot 0, ExpressionRecipe.StringLiteral "a", ExpressionRecipe.StringLiteral "b")) |> ignore)
          "string trim", SnapshotSchemaFeature.StringReplaceAndTrim, (fun create ->
              create "trim" [ "text", stringType ] stringType [] (ExpressionRecipe.StringTrimEnd(slot 0)) |> ignore)
          "round to digits", SnapshotSchemaFeature.Float64Round, (fun create ->
              create "round" [ "value", float64Type; "digits", int64Type ] float64Type [] (ExpressionRecipe.Float64Round(slot 0, slot 1)) |> ignore)
          "map node", SnapshotSchemaFeature.Map, (fun create ->
              create "emptyMap" [] (generic BuiltinGenericType.Map [ stringType; int64Type ]) [] (ExpressionRecipe.MapEmpty(stringType, int64Type)) |> ignore)
          "map type only", SnapshotSchemaFeature.Map, (fun create ->
              let counts = generic BuiltinGenericType.Map [ stringType; int64Type ]
              create "same" [ "counts", optionOf counts ] (optionOf counts) [] (slot 0) |> ignore)
          "set node", SnapshotSchemaFeature.Set, (fun create ->
              create "emptySet" [] (generic BuiltinGenericType.Set [ int64Type ]) [] (ExpressionRecipe.SetEmpty int64Type) |> ignore)
          "set type only", SnapshotSchemaFeature.Set, (fun create ->
              let seen = generic BuiltinGenericType.Set [ int64Type ]
              create "same" [ "seen", listOf seen ] (listOf seen) [] (slot 0) |> ignore)
          "minimum by", SnapshotSchemaFeature.ListMinBy, (fun create ->
              let less = create "less" [ "a", int64Type; "b", int64Type ] boolType [] (ExpressionRecipe.Int64LessThan(slot 0, slot 1))
              create "smallest" [ "items", listOf int64Type ] (optionOf int64Type) [] (ExpressionRecipe.ListMinBy(slot 0, less.Id, [])) |> ignore)
          "last item", SnapshotSchemaFeature.ListLastAndDropLast, (fun create ->
              create "last" [ "items", listOf int64Type ] (optionOf int64Type) [] (ExpressionRecipe.ListLast(slot 0)) |> ignore)
          "index of", SnapshotSchemaFeature.ListIndexSearch, (fun create ->
              create "find" [ "items", listOf int64Type; "item", int64Type ] (optionOf int64Type) [] (ExpressionRecipe.ListIndexOf(slot 0, slot 1)) |> ignore)
          "set subset", SnapshotSchemaFeature.SetIsSubsetAndMapUnion, (fun create ->
              let seen = generic BuiltinGenericType.Set [ int64Type ]
              create "subset" [ "left", seen; "right", seen ] boolType [] (ExpressionRecipe.SetIsSubset(slot 0, slot 1)) |> ignore)
          "map union", SnapshotSchemaFeature.SetIsSubsetAndMapUnion, (fun create ->
              let counts = generic BuiltinGenericType.Map [ stringType; int64Type ]
              create "merge" [ "left", counts; "right", counts ] counts [] (ExpressionRecipe.MapUnion(slot 0, slot 1)) |> ignore)
          "int64 shift", SnapshotSchemaFeature.Int64BitwiseAndFloat64MinMax, (fun create ->
              create "shift" [ "value", int64Type; "count", int64Type ] int64Type [] (ExpressionRecipe.Int64ShiftRight(slot 0, slot 1)) |> ignore)
          "float64 minimum", SnapshotSchemaFeature.Int64BitwiseAndFloat64MinMax, (fun create ->
              create "least" [ "a", float64Type; "b", float64Type ] float64Type [] (ExpressionRecipe.Float64Min(slot 0, slot 1)) |> ignore)
          "positional update", SnapshotSchemaFeature.ListPositionalUpdate, (fun create ->
              create "replace" [ "items", listOf int64Type; "index", int64Type; "item", int64Type ] (listOf int64Type) []
                  (ExpressionRecipe.ListSetAt(slot 0, slot 1, slot 2)) |> ignore)
          "zip with", SnapshotSchemaFeature.ListZipWithAndFlatMap, (fun create ->
              let add = create "add" [ "a", int64Type; "b", int64Type ] int64Type [] (ExpressionRecipe.Int64WrappingAdd(slot 0, slot 1))
              create "sums" [ "xs", listOf int64Type; "ys", listOf int64Type ] (listOf int64Type) [] (ExpressionRecipe.ListZipWith(slot 0, slot 1, add.Id, [])) |> ignore)
          "flat map", SnapshotSchemaFeature.ListZipWithAndFlatMap, (fun create ->
              let twice = create "twice" [ "a", int64Type ] (listOf int64Type) [] (ExpressionRecipe.ListConstruct(int64Type, [ slot 0; slot 0 ]))
              create "doubled" [ "xs", listOf int64Type ] (listOf int64Type) [] (ExpressionRecipe.ListFlatMap(slot 0, twice.Id, [])) |> ignore) ]

    let caseNames: obj array seq = cases |> Seq.map (fun (name, _, _) -> [| box name |])

    [<Fact>]
    let ``the table gives every construct one minimum version that is supported and above the baseline`` () =
        let features = FSharpType.GetUnionCases typeof<SnapshotSchemaFeature> |> Array.map (fun case -> case.Name) |> Set.ofArray
        let rows = SnapshotSchema.minimumVersions |> List.map (fun (feature, _, _) -> string feature)
        Assert.Equal<Set<string>>(features, Set.ofList rows)
        Assert.Equal(rows.Length, (Set.ofList rows).Count)
        // Generic types need a generic declaration, which `initial` cannot hold without gating every
        // case; GenericTypeTests checks their version and the rejection one version below.
        let testedElsewhere = Set.ofList [ string SnapshotSchemaFeature.GenericTypes ]
        Assert.Equal<Set<string>>(features - testedElsewhere, cases |> List.map (fun (_, feature, _) -> string feature) |> Set.ofList)
        for _, minimum, _ in SnapshotSchema.minimumVersions do
            Assert.Contains(minimum, SnapshotSchema.supported)
            Assert.True(minimum.Json > SnapshotSchema.baseline.Json)
        let json = SnapshotSchema.supported |> List.map (fun version -> version.Json)
        Assert.Equal<int list>(List.sort json |> List.distinct, json)
        Assert.Equal(SnapshotSchema.baseline, SnapshotSchema.supported.Head)

    [<Fact>]
    let ``a snapshot that uses no gated construct keeps the baseline version`` () =
        let snapshot = build (fun create -> create "plain" [ "text", stringType ] stringType [] (ExpressionRecipe.StringToLower(slot 0)) |> ignore)
        Assert.Empty(SnapshotSchema.usedFeatures snapshot)
        Assert.Contains($"\"schemaVersion\":{SnapshotSchema.baseline.Json}", SnapshotCodec.serialize snapshot |> unwrap "json")
        Assert.Contains(SnapshotSchema.baseline.Nyason, NyasonCodec.serialize snapshot |> unwrap "nyason")

    [<Theory>]
    [<MemberData(nameof caseNames)>]
    let ``a gated construct is written at its minimum version and rejected one version below`` (name: string) =
        let _, feature, define = cases |> List.find (fun (caseName, _, _) -> caseName = name)
        let snapshot = build define
        let expectedFeatures =
            match name with
            | "exception classification" -> Set.ofList [ feature; SnapshotSchemaFeature.ExceptionBoundary ]
            | "set subset" -> Set.ofList [ feature; SnapshotSchemaFeature.Set ]
            | "map union" -> Set.ofList [ feature; SnapshotSchemaFeature.Map ]
            | _ -> Set.singleton feature
        Assert.Equal<Set<SnapshotSchemaFeature>>(expectedFeatures, SnapshotSchema.usedFeatures snapshot)
        let minimum = SnapshotSchema.minimumVersion feature
        let label = SnapshotSchema.featureName feature
        let previous = SnapshotSchema.supported |> List.findBack (fun version -> version.Json < minimum.Json)
        Assert.Equal(minimum, SnapshotSchema.required snapshot)

        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        let declared = $"\"schemaVersion\":{minimum.Json}"
        Assert.Contains(declared, json)
        Assert.Equal(json, SnapshotCodec.deserialize json |> unwrap "json round trip" |> SnapshotCodec.serialize |> unwrap "json again")
        Assert.Equal(
            Error(SnapshotCodecError.InvalidShape $"{label} requires snapshot schemaVersion {minimum.Json}."),
            SnapshotCodec.deserialize (json.Replace(declared, $"\"schemaVersion\":{previous.Json}")))

        let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
        Assert.Contains($"\"{minimum.Nyason}\"", nyason)
        Assert.Equal(json, NyasonCodec.deserialize nyason |> unwrap "nyason round trip" |> SnapshotCodec.serialize |> unwrap "json from nyason")
        Assert.Equal(
            Error(NyasonCodecError.InvalidShape $"{label} requires {minimum.Nyason}."),
            NyasonCodec.deserialize (nyason.Replace($"\"{minimum.Nyason}\"", $"\"{previous.Nyason}\"")))

    [<Fact>]
    let ``versions outside the supported range are unsupported`` () =
        let json = SnapshotCodec.serialize initial |> unwrap "json"
        let declared = $"\"schemaVersion\":{SnapshotSchema.baseline.Json}"
        let newest = (List.last SnapshotSchema.supported).Json
        for version in [ SnapshotSchema.baseline.Json - 1; newest + 1 ] do
            Assert.Equal(Error(SnapshotCodecError.UnsupportedSchemaVersion version), SnapshotCodec.deserialize (json.Replace(declared, $"\"schemaVersion\":{version}")))
        let nyason = NyasonCodec.serialize initial |> unwrap "nyason"
        let future = $"nayamnyam.snapshot.v{(List.last SnapshotSchema.supported).Nyason.Split('v') |> Array.last |> int |> (+) 1}"
        Assert.Equal(Error(NyasonCodecError.UnsupportedSchema future), NyasonCodec.deserialize (nyason.Replace(SnapshotSchema.baseline.Nyason, future)))
