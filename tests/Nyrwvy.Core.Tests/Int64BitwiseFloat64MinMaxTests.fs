namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Numerics
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Int64BitAnd, Int64BitOr, Int64BitXor, Int64BitNot, Int64ShiftLeft and Int64ShiftRight (two's-complement
/// bitwise operations with the shift count clamped to 0..64) and Float64Min and Float64Max (IEEE 754-2019
/// minimum and maximum) (post-M2 order 59). See docs/decisions/int64-bitwise-and-float64-min-max.md.
module Int64BitwiseFloat64MinMaxTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("int64-bitwise-" + name)
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private float64Type = """{"kind":"primitive","primitive":"float64"}"""
    let private parameter name = """{"kind":"parameter","name":""" + "\"" + name + "\"}"

    let private create name parameters resultType body =
        """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":""" + "\"" + name + "\""
        + ""","parameters":[""" + (parameters |> List.map (fun (n, t) -> """{"displayName":""" + "\"" + n + "\"" + ""","type":""" + t + "}") |> String.concat ",")
        + """],"resultType":""" + resultType + ""","requiredEffects":[],"body":""" + body + "}"

    let private binary kind operandType =
        create kind [ "a", operandType; "b", operandType ] operandType
            ("""{"kind":""" + "\"" + kind + "\"" + ""","left":""" + parameter "a" + ""","right":""" + parameter "b" + "}")

    let private shift kind =
        create kind [ "value", int64Type; "count", int64Type ] int64Type
            ("""{"kind":""" + "\"" + kind + "\"" + ""","value":""" + parameter "value" + ""","count":""" + parameter "count" + "}")

    /// Authors one function per operation, named by its wire kind, in a fresh session and passes `call`
    /// and the snapshot path on.
    let private withProgram name (body: (string -> JsonDocument) -> string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let call (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            let commit (line: string) =
                use proposal = call line
                Assert.True(proposal.RootElement.GetProperty("ok").GetBoolean(), proposal.RootElement.ToString())
                let proposalId = proposal.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use committed = call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposalId + "\"}")
                Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            for kind in [ "int64BitAnd"; "int64BitOr"; "int64BitXor" ] do
                commit (binary kind int64Type)
            commit (create "int64BitNot" [ "a", int64Type ] int64Type ("""{"kind":"int64BitNot","value":""" + parameter "a" + "}"))
            commit (shift "int64ShiftLeft")
            commit (shift "int64ShiftRight")
            commit (binary "float64Min" float64Type)
            commit (binary "float64Max" float64Type)
            body call path
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private rejection (call: string -> JsonDocument) line =
        use response = call line
        let root = response.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.ToString())
        root.ToString()

    [<Fact>]
    let ``Int64 bitwise operations, shifts, Float64Min and Float64Max type check, persist at schema 17 and lower through every layer`` () =
        withProgram "layers" (fun call path ->
            let mismatch = "PureExpressionOperandTypeMismatch"
            Assert.Contains(mismatch, rejection call (create "badAnd" [ "a", float64Type; "b", int64Type ] int64Type """{"kind":"int64BitAnd","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}"""))
            Assert.Contains(mismatch, rejection call (create "badNot" [ "a", float64Type ] int64Type """{"kind":"int64BitNot","value":{"kind":"parameter","name":"a"}}"""))
            Assert.Contains(mismatch, rejection call (create "badCount" [ "a", int64Type; "b", float64Type ] int64Type """{"kind":"int64ShiftLeft","value":{"kind":"parameter","name":"a"},"count":{"kind":"parameter","name":"b"}}"""))
            Assert.Contains(mismatch, rejection call (create "badMin" [ "a", int64Type; "b", int64Type ] int64Type """{"kind":"float64Min","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}"""))
            Assert.Contains("FunctionCreationResultTypeMismatch", rejection call (create "badResult" [ "a", int64Type; "b", int64Type ] float64Type """{"kind":"int64BitXor","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}"""))
            // Shifts name their operands value and count, not left and right.
            rejection call (create "badFields" [ "a", int64Type; "b", int64Type ] int64Type """{"kind":"int64ShiftRight","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}""") |> ignore
            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap "serialize"
            for kind in [ "int64BitAnd"; "int64BitOr"; "int64BitXor"; "int64BitNot"; "int64ShiftLeft"; "int64ShiftRight"; "float64Min"; "float64Max" ] do
                Assert.Contains("\"" + kind + "\"", json)
            Assert.Contains("\"countNodeId\"", json)
            Assert.Contains("\"schemaVersion\":17", json)
            Assert.True(Result.isError (SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":17", "\"schemaVersion\":16"))))
            let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
            for tag in [ "int64-bit-and"; "int64-bit-or"; "int64-bit-xor"; "int64-bit-not"; "int64-shift-left"; "int64-shift-right"; "float64-min"; "float64-max" ] do
                Assert.Contains(tag, nyason)
            Assert.Contains("nayamnyam.snapshot.v13", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let execution = TypedSemanticIr.project restored |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            for fragment in [ " & "; " | "; " ^ "; " = ~"; " >= 64L ? 0L : "; " < 0L ? -1L : 0L"; "System.Math.Min("; "System.Math.Max(" ] do
                Assert.Contains(fragment, source))

    /// Python semantics on unbounded integers, reduced to two's-complement Int64: the reference does not
    /// use .NET shifts, whose count is masked to 6 bits.
    let private wrap (value: BigInteger) =
        let modulus = BigInteger.Pow(2I, 64)
        let reduced = ((value % modulus) + modulus) % modulus
        if reduced >= BigInteger.Pow(2I, 63) then reduced - modulus else reduced

    let private expectedShiftLeft (value: int64) (count: int64) =
        let count = int (max 0L (min 64L count))
        int64 (wrap (BigInteger value * BigInteger.Pow(2I, count)))

    let private expectedShiftRight (value: int64) (count: int64) =
        let count = int (max 0L (min 64L count))
        int64 (BigInteger.Divide(BigInteger value - (if value < 0L then BigInteger.Pow(2I, count) - 1I else 0I), BigInteger.Pow(2I, count)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Int64 bitwise operations, clamped shifts and IEEE Float64Min and Float64Max under runPure`` () =
        withProgram "run" (fun call _ ->
            let int64Value (value: int64) = """{"kind":"int64","value":""" + "\"" + string value + "\"}"
            let run fn arguments expected =
                use result = call ("""{"id":"run","op":"runPureByName","function":""" + "\"" + fn + "\"" + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
                let root = result.RootElement
                Assert.True(root.GetProperty("ok").GetBoolean() && root.GetProperty("result").GetProperty("matches").GetBoolean(), fn + " " + String.concat ", " arguments + ": " + root.ToString())
            let values = [ 0L; 1L; 5L; -5L; 0x0F0FL; -1L; Int64.MaxValue; Int64.MinValue ]
            for left in values do
                run "int64BitNot" [ int64Value left ] (int64Value (-left - 1L))
                for right in [ 0L; 3L; -6L; 0xFFL; Int64.MinValue ] do
                    run "int64BitAnd" [ int64Value left; int64Value right ] (int64Value (left &&& right))
                    run "int64BitOr" [ int64Value left; int64Value right ] (int64Value (left ||| right))
                    run "int64BitXor" [ int64Value left; int64Value right ] (int64Value (left ^^^ right))
            for value in [ 5L; -5L; 1L; -1L; Int64.MaxValue; Int64.MinValue ] do
                for count in [ -1L; 0L; 1L; 63L; 64L; 100L; Int64.MinValue; Int64.MaxValue ] do
                    run "int64ShiftLeft" [ int64Value value; int64Value count ] (int64Value (expectedShiftLeft value count))
                    run "int64ShiftRight" [ int64Value value; int64Value count ] (int64Value (expectedShiftRight value count))
            // Spot checks of the stated semantics, independent of the reference functions above.
            run "int64ShiftLeft" [ int64Value 5L; int64Value -1L ] (int64Value 5L)
            run "int64ShiftLeft" [ int64Value 1L; int64Value 63L ] (int64Value Int64.MinValue)
            run "int64ShiftLeft" [ int64Value -5L; int64Value 64L ] (int64Value 0L)
            run "int64ShiftLeft" [ int64Value 3L; int64Value 100L ] (int64Value 0L)
            run "int64ShiftRight" [ int64Value -5L; int64Value 1L ] (int64Value -3L)
            run "int64ShiftRight" [ int64Value Int64.MinValue; int64Value 63L ] (int64Value -1L)
            run "int64ShiftRight" [ int64Value Int64.MinValue; int64Value 64L ] (int64Value -1L)
            run "int64ShiftRight" [ int64Value -5L; int64Value 100L ] (int64Value -1L)
            run "int64ShiftRight" [ int64Value 5L; int64Value 100L ] (int64Value 0L)
            run "int64ShiftRight" [ int64Value Int64.MaxValue; int64Value 64L ] (int64Value 0L)
            run "int64ShiftRight" [ int64Value -5L; int64Value -1L ] (int64Value -5L)
            let float64Value (value: string) = """{"kind":"float64","value":""" + value + "}"
            let nan, negativeZero, zero = "\"NaN\"", "\"-0\"", "0"
            for left, right, minimum, maximum in
                [ nan, "1", nan, nan
                  "1", nan, nan, nan
                  nan, nan, nan, nan
                  negativeZero, zero, negativeZero, zero
                  zero, negativeZero, negativeZero, zero
                  negativeZero, negativeZero, negativeZero, negativeZero
                  "1.5", "-2.5", "-2.5", "1.5"
                  "\"-Infinity\"", "\"Infinity\"", "\"-Infinity\"", "\"Infinity\""
                  nan, "\"Infinity\"", nan, nan ] do
                run "float64Min" [ float64Value left; float64Value right ] (float64Value minimum)
                run "float64Max" [ float64Value left; float64Value right ] (float64Value maximum))
