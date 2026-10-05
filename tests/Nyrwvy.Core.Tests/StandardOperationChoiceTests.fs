namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Standard operations added after the owner's choices of 2026-10-01 (post-M2 orders 37 to 39):
/// ListRange and StringRepeat (an allocation too large is a host failure), Int64ToHexString and
/// StringParseInt64Hex (Python format(x, "x") with a '-' sign), and Int64Min, Int64Max and
/// Int64WrappingAbs; and the owner choice of 2026-10-03 (order 40): Int64CheckedAdd,
/// Int64CheckedSubtract and Int64CheckedMultiply, the exact result or None outside Int64; and
/// Float64Round (order 43), decimal places half away from zero on the shortest decimal text.
module StandardOperationChoiceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("standard-operation-choices-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    /// Parameter types, result type and body of a one-operation function for each mode.
    let private shapeOf mode =
        let slot = ExpressionRecipe.ParameterSlot
        match mode with
        | "range" -> [ intType; intType ], listOf intType, ExpressionRecipe.ListRange(slot 0, slot 1)
        | "repeat" -> [ textType; intType ], textType, ExpressionRecipe.StringRepeat(slot 0, slot 1)
        | "hex" -> [ intType ], textType, ExpressionRecipe.Int64ToHexString(slot 0)
        | "parseHex" -> [ textType ], optionOf intType, ExpressionRecipe.StringParseInt64Hex(slot 0)
        | "min" -> [ intType; intType ], intType, ExpressionRecipe.Int64Min(slot 0, slot 1)
        | "max" -> [ intType; intType ], intType, ExpressionRecipe.Int64Max(slot 0, slot 1)
        | "abs" -> [ intType ], intType, ExpressionRecipe.Int64WrappingAbs(slot 0)
        | "checkedAdd" -> [ intType; intType ], optionOf intType, ExpressionRecipe.Int64CheckedAdd(slot 0, slot 1)
        | "checkedSubtract" -> [ intType; intType ], optionOf intType, ExpressionRecipe.Int64CheckedSubtract(slot 0, slot 1)
        | "checkedMultiply" -> [ intType; intType ], optionOf intType, ExpressionRecipe.Int64CheckedMultiply(slot 0, slot 1)
        | "round" -> [ floatType; intType ], floatType, ExpressionRecipe.Float64Round(slot 0, slot 1)
        | other -> failwithf "Unknown mode %s" other

    [<Theory>]
    [<InlineData("range", "listRange", "list-range", "FromOwnedArray(items)", "Int64OperandTypeMismatch")>]
    [<InlineData("repeat", "stringRepeat", "string-repeat", ".Insert(0, text, (int)count)", "StringIntrinsicTypeMismatch")>]
    [<InlineData("hex", "int64ToHexString", "int64-to-hex-string", "ToString(\"x\"", "Int64IntrinsicTypeMismatch")>]
    [<InlineData("parseHex", "stringParseInt64Hex", "string-parse-int64-hex", "magnitude << 4", "StringIntrinsicTypeMismatch")>]
    [<InlineData("min", "int64Min", "int64-min", "System.Math.Min(", "Int64OperandTypeMismatch")>]
    [<InlineData("max", "int64Max", "int64-max", "System.Math.Max(", "Int64OperandTypeMismatch")>]
    [<InlineData("abs", "int64WrappingAbs", "int64-wrapping-abs", "unchecked(-", "Int64IntrinsicTypeMismatch")>]
    [<InlineData("checkedAdd", "int64CheckedAdd", "int64-checked-add", "NyNumber.CheckedAdd(", "Int64OperandTypeMismatch")>]
    [<InlineData("checkedSubtract", "int64CheckedSubtract", "int64-checked-subtract", "NyNumber.CheckedSubtract(", "Int64OperandTypeMismatch")>]
    [<InlineData("checkedMultiply", "int64CheckedMultiply", "int64-checked-multiply", "NyNumber.CheckedMultiply(", "Int64OperandTypeMismatch")>]
    [<InlineData("round", "float64Round", "float64-round", "NyNumber.Round(", "Float64OperandTypeMismatch")>]
    let ``Standard operations type check, persist, and lower through every layer``
        (mode: string, wireKind: string, nyasonKind: string, csharpCall: string, issueCode: string) =
        let parameterTypes, resultType, body = shapeOf mode
        let wrongFor parameterType = if parameterType = intType then textType else intType
        let snapshot = Snapshot.empty (id<SnapshotTag> ("base-" + mode))
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request (types: TypeRef list) result: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = mode
              Parameters = types |> List.mapi (fun index parameterType -> { DisplayName = $"p{index}"; Type = parameterType })
              ResultType = result
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped operation was accepted: %A" other
        for index in 0 .. parameterTypes.Length - 1 do
            let mistyped = parameterTypes |> List.mapi (fun at parameterType -> if at = index then wrongFor parameterType else parameterType)
            Assert.NotEqual<string>("", rejects (request mistyped resultType))
        Assert.NotEqual<string>("", rejects (request parameterTypes (wrongFor resultType)))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request parameterTypes resultType) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"" + wireKind + "\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let nyason = NyasonCodec.serialize decoded |> unwrap "nyason"
        Assert.Contains(nyasonKind, nyason)
        let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"" + wireKind + "\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"" + wireKind + "\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains(csharpCall, artifact.Source)
        // Every parameter reference replaced by a literal of the other primitive type.
        let parameters = committed.Functions.Head.Parameters
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference reference ->
                                        let parameter = parameters |> List.find (fun parameter -> parameter.Id = reference)
                                        let literal =
                                            if parameter.Type = intType then SemanticNodeKind.StringLiteral "1"
                                            else SemanticNodeKind.Int64Literal 1L
                                        { node with Kind = literal }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = issueCode)

    let private withSession name (body: (string -> JsonElement) -> AuthoringSession -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let call (line: string) =
                use response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                response.RootElement.GetProperty("result").Clone()
            body call session
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private stringType = """{"kind":"primitive","primitive":"string"}"""
    let private int64 (value: string) = """{"kind":"int64","value":""" + "\"" + value + "\"}"
    let private text (value: string) = """{"kind":"string","value":""" + JsonSerializer.Serialize value + "}"
    let private parameter name = """{"kind":"parameter","name":""" + "\"" + name + "\"}"

    /// Defines a function from JSON fragments: (name, type) parameters, the result type and the body.
    let private define (call: string -> JsonElement) name (parameters: (string * string) list) resultType body =
        let parameterList =
            parameters
            |> List.map (fun (parameterName, parameterType) -> """{"displayName":""" + "\"" + parameterName + "\",\"type\":" + parameterType + "}")
            |> String.concat ","
        let proposal =
            call (
                """{"id":"define","op":"simulateCreateFunctionByName","displayName":""" + "\"" + name + "\""
                + ""","parameters":[""" + parameterList + """],"resultType":""" + resultType
                + ""","requiredEffects":[],"body":""" + body + "}")
        call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposal.GetProperty("proposalId").GetString() + "\"}") |> ignore

    let private expectRun (call: string -> JsonElement) name (arguments: string list) expected =
        let result =
            call (
                """{"id":"run","op":"runPureByName","function":""" + "\"" + name + "\""
                + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
        Assert.True(result.GetProperty("matches").GetBoolean(), name + " " + String.concat ", " arguments + ": " + result.ToString())

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored hexadecimal text matches Python format and strict int base 16`` () =
        withSession "hex" (fun call _ ->
            define call "hex" [ "value", int64Type ] stringType """{"kind":"int64ToHexString","value":{"kind":"parameter","name":"value"}}"""
            define call "parseHex" [ "text", stringType ] """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}""" """{"kind":"stringParseInt64Hex","value":{"kind":"parameter","name":"text"}}"""
            // Expected values from Python format(value, "x").
            for value, expected in
                [ "0", "0"; "1", "1"; "15", "f"; "16", "10"; "255", "ff"; "-1", "-1"; "-255", "-ff"; "4096", "1000"
                  "9223372036854775807", "7fffffffffffffff"; "-9223372036854775808", "-8000000000000000"
                  "81985529216486895", "123456789abcdef"; "-9223372036854775807", "-7fffffffffffffff" ] do
                expectRun call "hex" [ int64 value ] (text expected)
                expectRun call "parseHex" [ text expected ] ("""{"kind":"option","itemKind":"int64","hasValue":true,"value":""" + "\"" + value + "\"}")
            // Python int(text, 16) where the text is '-'? followed by hex digits and the value fits Int64;
            // everything else, including what int() also accepts ('0x', '+', whitespace, '_'), is None.
            let none = """{"kind":"option","itemKind":"int64","hasValue":false}"""
            for input, expected in
                [ "FF", "255"; "-0", "0"; "00ff", "255"; "aBcD", "43981"; "0000000000000000000001", "1"
                  "8000000000000000", null; "-8000000000000001", null; "ffffffffffffffffff", null
                  "", null; "-", null; "0x10", null; "+1", null; " 1", null; "1 ", null; "1_0", null; "g", null
                  "--1", null; "１", null ] do
                let expectedValue =
                    if isNull expected then none
                    else """{"kind":"option","itemKind":"int64","hasValue":true,"value":""" + "\"" + expected + "\"}"
                expectRun call "parseHex" [ text input ] expectedValue)

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored Int64 min, max and wrapping abs`` () =
        withSession "int64-min-max-abs" (fun call _ ->
            let pair kind = """{"kind":""" + "\"" + kind + "\"" + ""","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}"""
            define call "min" [ "a", int64Type; "b", int64Type ] int64Type (pair "int64Min")
            define call "max" [ "a", int64Type; "b", int64Type ] int64Type (pair "int64Max")
            define call "abs" [ "a", int64Type ] int64Type """{"kind":"int64WrappingAbs","value":{"kind":"parameter","name":"a"}}"""
            for a, b, low, high in
                [ "1", "2", "1", "2"; "2", "1", "1", "2"; "-3", "3", "-3", "3"; "7", "7", "7", "7"
                  "-9223372036854775808", "9223372036854775807", "-9223372036854775808", "9223372036854775807" ] do
                expectRun call "min" [ int64 a; int64 b ] (int64 low)
                expectRun call "max" [ int64 a; int64 b ] (int64 high)
            for value, expected in
                [ "5", "5"; "-5", "5"; "0", "0"; "9223372036854775807", "9223372036854775807"
                  "-9223372036854775807", "9223372036854775807"; "-9223372036854775808", "-9223372036854775808" ] do
                expectRun call "abs" [ int64 value ] (int64 expected))

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored Float64Round rounds the decimal text half away from zero`` () =
        withSession "float64-round" (fun call _ ->
            let floatType = """{"kind":"primitive","primitive":"float64"}"""
            define call "round" [ "x", floatType; "digits", int64Type ] floatType """{"kind":"float64Round","value":{"kind":"parameter","name":"x"},"digits":{"kind":"parameter","name":"digits"}}"""
            let float (value: string) = """{"kind":"float64","value":""" + value + "}"
            // (value, digits, expected); quoted values are the special forms of the wire format.
            for value, digits, expected in
                [ "2.675", "2", "2.68" // the binary64 is just below 2.675; the decimal text decides
                  "1.005", "2", "1.01"
                  "0.125", "2", "0.13"
                  "-0.125", "2", "-0.13"
                  "0.30000000000000004", "2", "0.3"
                  "2.5", "0", "3"
                  "-2.5", "0", "-3"
                  "0.5", "0", "1"
                  "0.49999999999999994", "0", "0"
                  "1234.5678", "-2", "1200"
                  "1250", "-2", "1300"
                  "-1250", "-2", "-1300"
                  "49", "-2", "0"
                  "-49", "-2", "\"-0\""
                  "50", "-2", "100"
                  "1.5e-5", "5", "2e-5"
                  "1e-5", "5", "1e-5"
                  "5e-324", "323", "1e-323"
                  "1.23456789", "100", "1.23456789"
                  "123.456", "-1000", "0"
                  "1e300", "-300", "1e300"
                  "9.5e300", "-301", "1e301"
                  "1.7976931348623157e308", "-308", "\"Infinity\"" // 2e308 is beyond Float64
                  "\"-0\"", "2", "\"-0\""
                  "\"NaN\"", "2", "\"NaN\""
                  "\"-Infinity\"", "2", "\"-Infinity\""
                  "123.456", "9223372036854775807", "123.456"
                  "123.456", "-9223372036854775808", "0" ] do
                expectRun call "round" [ float value; int64 digits ] (float expected))

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored checked Int64 arithmetic matches Python's exact result within Int64`` () =
        withSession "int64-checked" (fun call _ ->
            let pair kind = """{"kind":""" + "\"" + kind + "\"" + ""","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}"""
            let optionInt64 = """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
            let operations =
                [ "add", "int64CheckedAdd", (fun (a: bigint) b -> a + b)
                  "subtract", "int64CheckedSubtract", (fun a b -> a - b)
                  "multiply", "int64CheckedMultiply", (fun a b -> a * b) ]
            for name, kind, _ in operations do
                define call name [ "a", int64Type; "b", int64Type ] optionInt64 (pair kind)
            // Python: exact = a op b; Some(exact) if -2**63 <= exact < 2**63 else None.
            let expected (exact: bigint) =
                if exact >= bigint Int64.MinValue && exact <= bigint Int64.MaxValue then
                    """{"kind":"option","itemKind":"int64","hasValue":true,"value":""" + "\"" + exact.ToString() + "\"}"
                else """{"kind":"option","itemKind":"int64","hasValue":false}"""
            let edges =
                [ Int64.MinValue; Int64.MinValue + 1L; -4294967296L; -3037000500L; -3037000499L; -2L; -1L; 0L
                  1L; 2L; 3037000499L; 3037000500L; 4294967296L; Int64.MaxValue - 1L; Int64.MaxValue ]
            let random = Random(20261003)
            let pairs =
                [ for a in edges do
                      for b in edges -> a, b
                  for _ in 1 .. 40 -> random.NextInt64(Int64.MinValue, Int64.MaxValue), random.NextInt64(Int64.MinValue, Int64.MaxValue)
                  for _ in 1 .. 40 -> random.NextInt64(-4000000000L, 4000000000L), random.NextInt64(-4000000000L, 4000000000L) ]
            for a, b in pairs do
                for name, _, exact in operations do
                    expectRun call name [ int64 (string a); int64 (string b) ] (expected (exact (bigint a) (bigint b)))
        )

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored ListRange and StringRepeat match Python, and a huge size is a host failure`` () =
        withSession "range-repeat" (fun call session ->
            define call "range" [ "start", int64Type; "end", int64Type ] """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"int64"}]}""" """{"kind":"listRange","start":{"kind":"parameter","name":"start"},"end":{"kind":"parameter","name":"end"}}"""
            define call "repeat" [ "text", stringType; "count", int64Type ] stringType """{"kind":"stringRepeat","text":{"kind":"parameter","name":"text"},"count":{"kind":"parameter","name":"count"}}"""
            let ints (items: int64 list) =
                """{"kind":"list","itemKind":"int64","items":[""" + (items |> List.map (fun item -> "\"" + string item + "\"") |> String.concat ",") + "]}"
            // Expected values from Python list(range(start, end)).
            for start, stop in
                [ 0L, 5L; 5L, 0L; 3L, 3L; -2L, 2L; Int64.MaxValue - 2L, Int64.MaxValue; Int64.MinValue, Int64.MinValue + 2L ] do
                let expected = if stop <= start then [] else [ for offset in 0L .. stop - start - 1L -> start + offset ]
                expectRun call "range" [ int64 (string start); int64 (string stop) ] (ints expected)
            // Expected values from Python text * count.
            for value, count, expected in
                [ "ab", "3", "ababab"; "ab", "1", "ab"; "ab", "0", ""; "ab", "-1", ""; "", "5", ""
                  "\U0001F600", "2", "\U0001F600\U0001F600"; "x", "-9223372036854775808", "" ] do
                expectRun call "repeat" [ text value; int64 count ] (text expected)
            // A size that cannot be allocated fails the run, as a stack overflow does, instead of
            // returning a value.
            for name, arguments in
                [ "range", [ int64 "-9223372036854775808"; int64 "9223372036854775807" ]
                  "repeat", [ text "ab"; int64 "9223372036854775807" ] ] do
                let request =
                    """{"id":"huge","op":"runPureByName","function":""" + "\"" + name + "\""
                    + ""","arguments":[""" + String.concat "," arguments + "]}"
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                let root = response.RootElement
                Assert.False(root.GetProperty("ok").GetBoolean(), root.ToString())
                let error = root.GetProperty("error")
                Assert.Equal("RunRuntimeFailure", error.GetProperty("code").GetString())
                Assert.Contains("OutOfMemoryException", root.ToString()))
