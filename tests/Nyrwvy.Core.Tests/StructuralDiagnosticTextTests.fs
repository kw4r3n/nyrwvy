namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Structural rejections render types by display name in wire TypeRef notation, name callbacks
/// with their actual signature, and end with the recipe path of the node that failed
/// (probe 4, friction 4).
module StructuralDiagnosticTextTests =
    let private diagnosticOwnerId<'tag> value = StableId.createForTesting<'tag> ("diagnostic-owner-" + value)

    [<Fact>]
    let ``validator node errors carry the exact owner despite equal display names`` () =
        let ownerId, otherId = diagnosticOwnerId<FunctionTag> "owner", diagnosticOwnerId<FunctionTag> "other"
        let input, result, otherResult = diagnosticOwnerId<GraphNodeTag> "input", diagnosticOwnerId<GraphNodeTag> "result", diagnosticOwnerId<GraphNodeTag> "other-result"
        let make functionId resultType nodes resultId =
            { Id = functionId; DisplayName = "summary"; Parameters = []; ResultType = resultType; RequiredEffects = []; Body = { Nodes = nodes; ResultNodeId = resultId } }
        let owner = make ownerId (TypeRef.Primitive PrimitiveType.String)
                        [ { Id = input; Kind = SemanticNodeKind.StringLiteral(StableId.value otherId) }
                          { Id = result; Kind = SemanticNodeKind.Int64ToString input } ] result
        let other = make otherId (TypeRef.Primitive PrimitiveType.Int64) [ { Id = otherResult; Kind = SemanticNodeKind.Int64Literal 0L } ] otherResult
        let issue = SnapshotValidator.validate { Snapshot.empty (diagnosticOwnerId<SnapshotTag> "base") with Functions = [ other; owner ] }
                    |> List.find (fun issue -> issue.Code = "Int64IntrinsicTypeMismatch")
        Assert.Contains("In function", issue.Message)
        Assert.Contains("summary", issue.Message)
        Assert.Contains(StableId.value ownerId, issue.Message)
        Assert.Contains(StableId.value result, issue.Message)
        Assert.DoesNotContain(StableId.value otherId, issue.Message)

    let private id<'tag> name = StableId.createForTesting<'tag> ("diag-text-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64

    let private tailTypeId = id<TypeTag> "tail"
    let private findingTypeId = id<TypeTag> "finding"
    let private tailLineFieldId = id<FieldTag> "tail-line"
    let private findingLineFieldId = id<FieldTag> "finding-line"
    let private countStepId = id<FunctionTag> "count-step"
    let private cmpLenId = id<FunctionTag> "cmp-len"

    let private snapshot =
        let product typeId name fieldId: SemanticType =
            { Id = typeId
              DisplayName = name
              Kind = SemanticTypeKind.Product [ { Id = fieldId; DisplayName = "line"; Type = int64Type } ] }
        let int64Function functionId name (parameters: (string * TypeRef) list): SemanticFunction =
            let resultNodeId = id<GraphNodeTag> (name + "-result")
            { Id = functionId
              DisplayName = name
              Parameters =
                parameters
                |> List.map (fun (parameterName, parameterType) ->
                    { Id = id<ParameterTag> (name + "-" + parameterName); DisplayName = parameterName; Type = parameterType })
              ResultType = int64Type
              RequiredEffects = []
              Body = { Nodes = [ { Id = resultNodeId; Kind = SemanticNodeKind.Int64Literal 0L } ]; ResultNodeId = resultNodeId } }
        let value: Snapshot =
            { Snapshot.empty (id<SnapshotTag> "base") with
                Types = [ product tailTypeId "Tail" tailLineFieldId; product findingTypeId "Finding" findingLineFieldId ]
                Functions =
                    [ int64Function countStepId "countStep" [ "line", stringType; "acc", int64Type ]
                      int64Function cmpLenId "cmpLen" [ "a", stringType; "b", stringType ] ] }
        Assert.Empty(SnapshotValidator.validate value)
        value

    let private S = """{"kind":"primitive","primitive":"string"}"""
    let private I = """{"kind":"primitive","primitive":"int64"}"""
    let private listOf item = $$"""{"kind":"builtinGeneric","generic":"list","arguments":[{{item}}]}"""
    let private optionOf item = $$"""{"kind":"builtinGeneric","generic":"option","arguments":[{{item}}]}"""
    let private named name = $$"""{"kind":"named","name":"{{name}}"}"""

    /// Sends one `simulateCreateFunctionByName` and returns the single diagnostic's code and message.
    let private reject (parameters: (string * string) list) resultType body =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-diag-text-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> function Ok json -> json | Error error -> failwithf "%A" error)
            use session = AuthoringSession.openExisting path |> function Ok value -> value | Error error -> failwithf "%A" error
            let parameterJson =
                parameters
                |> List.map (fun (name, typeJson) -> $$"""{"displayName":"{{name}}","type":{{typeJson}}}""")
                |> String.concat ","
            let request =
                $$"""{"id":"r","op":"simulateCreateFunctionByName","displayName":"probe","parameters":[{{parameterJson}}],"resultType":{{resultType}},"requiredEffects":[],"body":{{body}}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            let text = response.RootElement.ToString()
            Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), text)
            let error = response.RootElement.GetProperty("error")
            Assert.Equal("SimulationRejected", error.GetProperty("code").GetString())
            let diagnostic = error.GetProperty("details").GetProperty("diagnostics").[0]
            let message = diagnostic.GetProperty("message").GetString()
            Assert.DoesNotContain("StableId", message)
            Assert.DoesNotContain("Primitive", message)
            Assert.DoesNotContain("BuiltinGeneric", message)
            diagnostic.GetProperty("code").GetString(), message
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``a ListFold folder with swapped parameters is named with its actual and expected signature`` () =
        let code, message =
            reject [ "xs", listOf S ] I """{"kind":"listFold","source":{"kind":"parameter","name":"xs"},"seed":{"kind":"int64Literal","value":0},"function":"countStep"}"""
        Assert.Equal("PureExpressionCallbackInputTypeMismatch", code)
        Assert.Equal(
            $"ListFold folder 'countStep' ({StableId.value countStepId}) must have type (accumulator, item) -> accumulator, here (Int64, String) -> Int64; "
            + "the folder has type (line: String, acc: Int64) -> Int64. At body (listFold).",
            message
        )

    [<Fact>]
    let ``a field access on the wrong owner names both types`` () =
        let code, message =
            reject [ "t", named "Tail" ] I """{"kind":"fieldAccess","source":{"kind":"parameter","name":"t"},"type":"Finding","field":"line"}"""
        Assert.Equal("PureExpressionFieldAccessTypeMismatch", code)
        Assert.Equal(
            $"Field access to 'line' ({StableId.value findingLineFieldId}) requires 'Finding' ({StableId.value findingTypeId}), "
            + $"but the source recipe produces 'Tail' ({StableId.value tailTypeId}). At body (fieldAccess).",
            message
        )

    [<Fact>]
    let ``a comparator-style sort callback is named with its result type`` () =
        let code, message =
            reject [ "xs", listOf S ] (listOf S) """{"kind":"listSortBy","source":{"kind":"parameter","name":"xs"},"function":"cmpLen"}"""
        Assert.Equal("PureExpressionCallbackResultTypeMismatch", code)
        Assert.Equal(
            $"ListSortBy less-than callback 'cmpLen' ({StableId.value cmpLenId}) must return Bool; it has type (a: String, b: String) -> Int64. At body (listSortBy).",
            message
        )

    [<Fact>]
    let ``a branch type mismatch renders both types in wire notation`` () =
        let code, message =
            reject
                [ "text", S ]
                (optionOf S)
                ("""{"kind":"conditional","condition":{"kind":"stringIsEmpty","value":{"kind":"parameter","name":"text"}},"whenTrue":{"kind":"optionNone","itemType":""" + S + """},"whenFalse":{"kind":"parameter","name":"text"}}""")
        Assert.Equal("PureExpressionBranchTypeMismatch", code)
        Assert.Equal(
            "Conditional normally completing branches must produce one common type; found Option<String> and String. At body (conditional).",
            message
        )

    [<Fact>]
    let ``the path locates a nested operand and a list item`` () =
        let _, nested =
            reject
                [ "text", S ]
                I
                """{"kind":"conditional","condition":{"kind":"boolLiteral","value":true},"whenTrue":{"kind":"int64Literal","value":1},"whenFalse":{"kind":"stringLength","value":{"kind":"int64Literal","value":2}}}"""
        Assert.Equal("StringLength value operand requires String, but the recipe produces Int64. At body.whenFalse (stringLength).", nested)
        let _, item =
            reject
                []
                (listOf I)
                ("""{"kind":"listConstruct","itemType":""" + I + ""","items":[{"kind":"int64Literal","value":1},{"kind":"stringLength","value":{"kind":"int64Literal","value":2}}]}""")
        Assert.EndsWith("At body.items[1] (stringLength).", item)
        let _, argument =
            reject
                [ "text", S ]
                I
                """{"kind":"directCall","function":"countStep","arguments":[{"parameter":"line","value":{"kind":"parameter","name":"text"}},{"parameter":"acc","value":{"kind":"stringLength","value":{"kind":"int64Literal","value":3}}}]}"""
        Assert.EndsWith("At body.arguments[1].value (stringLength).", argument)
        let _, searchFrom =
            reject
                [ "text", S ]
                (optionOf I)
                """{"kind":"stringIndexOf","value":{"kind":"parameter","name":"text"},"substring":{"kind":"stringLiteral","value":"a"},"from":{"kind":"stringLength","value":{"kind":"int64Literal","value":4}}}"""
        Assert.EndsWith("At body.from (stringLength).", searchFrom)
        let _, separator =
            reject
                [ "text", S ]
                (listOf S)
                """{"kind":"stringSplit","value":{"kind":"parameter","name":"text"},"separator":{"kind":"stringLength","value":{"kind":"int64Literal","value":5}}}"""
        Assert.EndsWith("At body.separator (stringLength).", separator)
        let _, lowered =
            reject
                [ "text", S ]
                I
                """{"kind":"stringUtf8ByteCount","value":{"kind":"stringToLower","value":{"kind":"int64Literal","value":6}}}"""
        Assert.Equal("StringToLower value operand requires String, but the recipe produces Int64. At body.value (stringToLower).", lowered)
        let _, codePoint =
            reject
                [ "text", S ]
                (optionOf I)
                """{"kind":"stringToCodePoint","value":{"kind":"stringFromCodePoint","value":{"kind":"parameter","name":"text"}}}"""
        Assert.Equal("StringFromCodePoint value operand requires Int64, but the recipe produces String. At body.value (stringFromCodePoint).", codePoint)
        let _, floatText =
            reject
                [ "text", S ]
                (optionOf """{"kind":"primitive","primitive":"float64"}""")
                """{"kind":"stringParseFloat64","value":{"kind":"float64ToString","value":{"kind":"parameter","name":"text"}}}"""
        Assert.Equal("Float64ToString value operand requires Float64, but the recipe produces String. At body.value (float64ToString).", floatText)
        let _, joinSeparator =
            reject
                [ "xs", listOf S ]
                S
                """{"kind":"stringJoin","list":{"kind":"parameter","name":"xs"},"separator":{"kind":"stringLength","value":{"kind":"int64Literal","value":7}}}"""
        Assert.EndsWith("At body.separator (stringLength).", joinSeparator)
        let _, sliceEnd =
            reject
                [ "xs", listOf S ]
                (listOf S)
                """{"kind":"listSlice","list":{"kind":"parameter","name":"xs"},"start":{"kind":"int64Literal","value":0},"end":{"kind":"stringLength","value":{"kind":"int64Literal","value":8}}}"""
        Assert.EndsWith("At body.end (stringLength).", sliceEnd)
        let _, sliceList =
            reject
                [ "xs", listOf S ]
                (listOf S)
                """{"kind":"listSlice","list":{"kind":"stringLength","value":{"kind":"int64Literal","value":9}},"start":{"kind":"int64Literal","value":0},"end":{"kind":"int64Literal","value":1}}"""
        Assert.EndsWith("At body.list (stringLength).", sliceList)
        let _, spanSet =
            reject
                [ "text", S ]
                I
                """{"kind":"stringSpan","value":{"kind":"parameter","name":"text"},"set":{"kind":"stringLength","value":{"kind":"int64Literal","value":10}},"from":{"kind":"int64Literal","value":0}}"""
        Assert.EndsWith("At body.set (stringLength).", spanSet)
        let _, spanEnd =
            reject
                [ "text", S ]
                I
                """{"kind":"stringSpanBack","value":{"kind":"parameter","name":"text"},"set":{"kind":"stringLiteral","value":" "},"end":{"kind":"stringLength","value":{"kind":"int64Literal","value":11}}}"""
        Assert.EndsWith("At body.end (stringLength).", spanEnd)
        let _, linesValue =
            reject
                [ "text", S ]
                (listOf S)
                """{"kind":"stringSplitLines","value":{"kind":"int64Literal","value":12},"breaks":{"kind":"parameter","name":"text"}}"""
        Assert.Equal("StringSplitLines value operand requires String, but the recipe produces Int64. At body (stringSplitLines).", linesValue)
        let _, linesBreaks =
            reject
                [ "text", S ]
                (listOf S)
                """{"kind":"stringSplitLines","value":{"kind":"parameter","name":"text"},"breaks":{"kind":"stringLength","value":{"kind":"int64Literal","value":13}}}"""
        Assert.EndsWith("At body.breaks (stringLength).", linesBreaks)

    [<Fact>]
    let ``type references render as display names in wire notation`` () =
        let render = DiagnosticText.typeRef snapshot
        Assert.Equal("Option<String>", render (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ])))
        Assert.Equal("List<Tail>", render (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Named tailTypeId ])))
        Assert.Equal("Result<Int64, Finding>", render (TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ int64Type; TypeRef.Named findingTypeId ])))
        Assert.Equal("(String, Int64) -> Bool", render (TypeRef.Function([ stringType; int64Type ], TypeRef.Primitive PrimitiveType.Bool)))
        let unknown = id<TypeTag> "unknown"
        Assert.Equal($"type '{StableId.value unknown}'", render (TypeRef.Named unknown))

    /// Operations checked from `OperationSignatures` keep the texts of their former hand-written checks:
    /// a fixed operand type, a generic shape before its variables are bound, a type fixed by an earlier
    /// operand, the `left`/`right` sides of older string operations, and equatable constraints.
    [<Fact>]
    let ``fixed-signature operations keep their operand mismatch texts`` () =
        let B = """{"kind":"primitive","primitive":"bool"}"""
        let F = """{"kind":"primitive","primitive":"float64"}"""
        let setOf item = $$"""{"kind":"builtinGeneric","generic":"set","arguments":[{{item}}]}"""
        let mapOf key value = $$"""{"kind":"builtinGeneric","generic":"map","arguments":[{{key}},{{value}}]}"""
        let cases =
            [ [ "text", S ], B, """{"kind":"stringContains","value":{"kind":"parameter","name":"text"},"substring":{"kind":"int64Literal","value":1}}""",
              "PureExpressionOperandTypeMismatch", "StringContains right operand requires String, but the recipe produces Int64. At body (stringContains)."
              [ "text", S ], optionOf I, """{"kind":"stringIndexOf","value":{"kind":"int64Literal","value":1},"substring":{"kind":"parameter","name":"text"},"from":{"kind":"int64Literal","value":0}}""",
              "PureExpressionOperandTypeMismatch", "StringIndexOf left operand requires String, but the recipe produces Int64. At body (stringIndexOf)."
              [], I, """{"kind":"listLength","value":{"kind":"int64Literal","value":1}}""",
              "PureExpressionOperandTypeMismatch", "ListLength value operand requires List<T>, but the recipe produces Int64. At body (listLength)."
              [], optionOf I, """{"kind":"mapGet","map":{"kind":"int64Literal","value":1},"key":{"kind":"int64Literal","value":2}}""",
              "PureExpressionOperandTypeMismatch", "MapGet map operand requires Map<K, V>, but the recipe produces Int64. At body (mapGet)."
              [ "xs", listOf S ], I, """{"kind":"setSize","value":{"kind":"parameter","name":"xs"}}""",
              "PureExpressionOperandTypeMismatch", "SetSize value operand requires Set<T>, but the recipe produces List<String>. At body (setSize)."
              [ "xs", listOf S ], listOf S, """{"kind":"listAppendItem","list":{"kind":"parameter","name":"xs"},"item":{"kind":"int64Literal","value":1}}""",
              "PureExpressionOperandTypeMismatch", "ListAppendItem item operand requires String, but the recipe produces Int64. At body (listAppendItem)."
              [ "xs", listOf S ], listOf S, """{"kind":"listConcat","left":{"kind":"parameter","name":"xs"},"right":{"kind":"listEmpty","itemType":""" + I + """}}""",
              "PureExpressionOperandTypeMismatch", "ListConcat right operand requires List<String>, but the recipe produces List<Int64>. At body (listConcat)."
              [ "m", mapOf S I ], I, """{"kind":"mapGetOrDefault","map":{"kind":"parameter","name":"m"},"key":{"kind":"stringLiteral","value":"k"},"default":{"kind":"stringLiteral","value":"d"}}""",
              "PureExpressionOperandTypeMismatch", "MapGetOrDefault default operand requires Int64, but the recipe produces String. At body (mapGetOrDefault)."
              [ "s", setOf S ], B, """{"kind":"setContains","set":{"kind":"parameter","name":"s"},"item":{"kind":"int64Literal","value":1}}""",
              "PureExpressionOperandTypeMismatch", "SetContains item operand requires String, but the recipe produces Int64. At body (setContains)."
              [ "xs", listOf F ], setOf F, """{"kind":"setFromList","value":{"kind":"parameter","name":"xs"}}""",
              "SetItemTypeNotEquatable", "Set item type is not equatable: Float64 is not equatable: Float64; Float64 is not equatable because of NaN; compare with Float64Equal. At body (setFromList)."
              [ "xs", listOf F ], optionOf I, """{"kind":"listIndexOf","list":{"kind":"parameter","name":"xs"},"item":{"kind":"stringLiteral","value":"x"}}""",
              "ListIndexOfItemTypeNotEquatable", "ListIndexOf compares items of a type that is not equatable: Float64 is not equatable: Float64; Float64 is not equatable because of NaN; compare with Float64Equal. At body (listIndexOf)." ]
        for parameters, resultType, body, expectedCode, expectedMessage in cases do
            let code, message = reject parameters resultType body
            Assert.Equal(expectedMessage, message)
            Assert.Equal(expectedCode, code)
