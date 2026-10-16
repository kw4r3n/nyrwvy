namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module FoundationTests =
    [<Fact>]
    let ``stable identity is independent from display names`` () =
        let fixture = TestGraph.create ()
        let before = fixture.Snapshot.Functions |> List.find (fun item -> item.Id = fixture.ValidateFunctionId)
        let after = { before with DisplayName = "validate_profile" }

        Assert.Equal(before.Id, after.Id)

        if before.DisplayName = after.DisplayName then
            failwith "Changing a display name unexpectedly changed the identity check."

    [<Fact>]
    let ``direct calls exist only as semantic graph nodes`` () =
        let fixture = TestGraph.create ()
        let run = fixture.Snapshot.Functions |> List.find (fun item -> item.Id = fixture.RunFunctionId)
        let calls =
            ValueGraph.collectNodes run.Body
            |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.DirectCall(targetFunctionId, arguments) -> Some(node.Id, targetFunctionId, arguments)
                | SemanticNodeKind.CapabilityConstruct _
                | SemanticNodeKind.FunctionReference _
                | SemanticNodeKind.FunctionInvoke _
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
                | SemanticNodeKind.StringLength _
                | SemanticNodeKind.StringCodePoints _
                | SemanticNodeKind.StringToLower _
                | SemanticNodeKind.StringToUpper _
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
                | SemanticNodeKind.StringSlice _
                | SemanticNodeKind.BoolNot _
                | SemanticNodeKind.BoolAnd _
                | SemanticNodeKind.BoolOr _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.StringIndexOf _
                | SemanticNodeKind.StringSpan _
                | SemanticNodeKind.StringSpanBack _
                | SemanticNodeKind.StringLastIndexOf _
                | SemanticNodeKind.StringStartsWith _
                | SemanticNodeKind.StringEndsWith _
                | SemanticNodeKind.StringJoin _
                | SemanticNodeKind.StringSplit _
                | SemanticNodeKind.StringSplitLines _
                | SemanticNodeKind.StringReplace _
                | SemanticNodeKind.StringTrim _
                | SemanticNodeKind.StringTrimStart _
                | SemanticNodeKind.StringTrimEnd _
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
                | SemanticNodeKind.Int64CheckedAdd _
                | SemanticNodeKind.Int64CheckedSubtract _
                | SemanticNodeKind.Int64CheckedMultiply _
                | SemanticNodeKind.ListRange _
                | SemanticNodeKind.StringRepeat _
                | SemanticNodeKind.Float64Round _
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
                | SemanticNodeKind.SetEmpty _
                | SemanticNodeKind.MapEmpty _
                | SemanticNodeKind.ListConstruct _
                | SemanticNodeKind.ListLength _
                | SemanticNodeKind.MapValues _
                | SemanticNodeKind.MapKeys _
                | SemanticNodeKind.SetToList _
                | SemanticNodeKind.MapSize _
                | SemanticNodeKind.SetFromList _
                | SemanticNodeKind.SetSize _
                | SemanticNodeKind.ListReverse _
                | SemanticNodeKind.ListLast _
                | SemanticNodeKind.ListDropLast _
                | SemanticNodeKind.ListSlice _
                | SemanticNodeKind.MapInsert _
                | SemanticNodeKind.ListItemAt _
                | SemanticNodeKind.MapRemove _
                | SemanticNodeKind.SetInsert _
                | SemanticNodeKind.SetRemove _
                | SemanticNodeKind.SetUnion _
                | SemanticNodeKind.SetIntersection _
                | SemanticNodeKind.SetDifference _
                | SemanticNodeKind.SetIsSubset _
                | SemanticNodeKind.MapUnion _
                | SemanticNodeKind.MapContainsKey _
                | SemanticNodeKind.SetContains _
                | SemanticNodeKind.ListIndexOf _
                | SemanticNodeKind.ListLastIndexOf _
                | SemanticNodeKind.MapGet _
                | SemanticNodeKind.MapGetOrDefault _
                | SemanticNodeKind.ListAppendItem _
                | SemanticNodeKind.ListConcat _
                | SemanticNodeKind.ListFold _
                | SemanticNodeKind.MapFold _
                | SemanticNodeKind.ListEffectFold _
                | SemanticNodeKind.UseResource _
                | SemanticNodeKind.ParallelAll _
                | SemanticNodeKind.CancellationRequested
                | SemanticNodeKind.TaskScope _
                | SemanticNodeKind.ExceptionCatch _
                | SemanticNodeKind.ExceptionIsCancellation _
                | SemanticNodeKind.ExceptionMessage _
                | SemanticNodeKind.ExceptionRethrow _
                | SemanticNodeKind.CancellationShield _
                | SemanticNodeKind.TaskStart _
                | SemanticNodeKind.TaskWait _
                | SemanticNodeKind.OneShotCreate _
                | SemanticNodeKind.OneShotComplete _
                | SemanticNodeKind.OneShotWait _
                | SemanticNodeKind.TaskCancel _
                | SemanticNodeKind.ListMap _
                | SemanticNodeKind.MapFromList _
                | SemanticNodeKind.ListFilter _
                | SemanticNodeKind.MapFilter _
                | SemanticNodeKind.ListSortBy _
                | SemanticNodeKind.MapMapValues _
                | SemanticNodeKind.ListMinBy _
                | SemanticNodeKind.ListAny _
                | SemanticNodeKind.ListFindIndex _
                | SemanticNodeKind.ListAll _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.ProductUpdate _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.Abort _
                | SemanticNodeKind.ResultErr _
                | SemanticNodeKind.Conditional _
                | SemanticNodeKind.MatchUnion _
                | SemanticNodeKind.Bind _
                | SemanticNodeKind.MatchOption _
                | SemanticNodeKind.MatchResult _ -> None)

        let callNodeId, targetFunctionId, arguments = List.exactlyOne calls
        Assert.Equal(fixture.RunCallNodeId, callNodeId)
        Assert.Equal(fixture.ValidateFunctionId, targetFunctionId)
        Assert.Single(arguments) |> ignore

    [<Fact>]
    let ``empty snapshot does not infer semantic facts`` () =
        let id = StableId.createForTesting<SnapshotTag> "snapshot-fixture-1"
        let snapshot = Snapshot.empty id

        Assert.Empty(snapshot.Types)
        Assert.Empty(snapshot.Effects)
        Assert.Empty(snapshot.Capabilities)
        Assert.Empty(snapshot.Functions)
        Assert.Empty(snapshot.Obligations)

        if Option.isSome snapshot.ParentId then
            failwith "An empty snapshot unexpectedly inferred a parent snapshot."
