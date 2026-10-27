namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Standard operations whose meaning is fixed with no choice to make (post-M2 orders 35 and 36):
/// ListReverse, and the IEEE 754 Float64 -> Float64 operations Sqrt, Abs, Negate, Floor, Ceiling and
/// Truncate. None of them fails.
module StandardOperationTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("standard-operations-" + name)
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private recipeOf mode value =
        match mode with
        | "sqrt" -> ExpressionRecipe.Float64Sqrt value
        | "abs" -> ExpressionRecipe.Float64Abs value
        | "negate" -> ExpressionRecipe.Float64Negate value
        | "floor" -> ExpressionRecipe.Float64Floor value
        | "ceiling" -> ExpressionRecipe.Float64Ceiling value
        | "truncate" -> ExpressionRecipe.Float64Truncate value
        | "reverse" -> ExpressionRecipe.ListReverse value
        | "dropLast" -> ExpressionRecipe.ListDropLast value
        | "last" -> ExpressionRecipe.ListLast value
        | other -> failwithf "Unknown mode %s" other

    [<Theory>]
    [<InlineData("sqrt", "float64Sqrt", "System.Math.Sqrt(")>]
    [<InlineData("abs", "float64Abs", "System.Math.Abs(")>]
    [<InlineData("negate", "float64Negate", " = -")>]
    [<InlineData("floor", "float64Floor", "System.Math.Floor(")>]
    [<InlineData("ceiling", "float64Ceiling", "System.Math.Ceiling(")>]
    [<InlineData("truncate", "float64Truncate", "System.Math.Truncate(")>]
    [<InlineData("reverse", "listReverse", ".Reverse()")>]
    [<InlineData("dropLast", "listDropLast", ".DropLast()")>]
    [<InlineData("last", "listLast", ".Last()")>]
    let ``Standard operations type check, persist, and lower through every layer``
        (mode: string, wireKind: string, csharpCall: string) =
        // ListReverse keeps its List<T> type, so a List<String> stands in for every item type.
        let valueType, wrongType, issueCode =
            if mode = "reverse" then listOf textType, textType, "ListReverseInputTypeMismatch"
            elif mode = "dropLast" then listOf textType, textType, "ListDropLastInputTypeMismatch"
            elif mode = "last" then listOf textType, textType, "ListLastInputTypeMismatch"
            else floatType, intType, "Float64OperandTypeMismatch"
        // ListLast gives Option<T>; every other operation keeps its operand type.
        let resultType = if mode = "last" then TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ textType ]) else valueType
        let snapshot = Snapshot.empty (id<SnapshotTag> ("base-" + mode))
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = mode
              Parameters = [ { DisplayName = "value"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped operation was accepted: %A" other
        let body = recipeOf mode (ExpressionRecipe.ParameterSlot 0)
        Assert.NotEqual<string>("", rejects (request wrongType resultType body))
        Assert.NotEqual<string>("", rejects (request valueType wrongType body))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request valueType resultType body) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"" + wireKind + "\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"" + wireKind + "\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"" + wireKind + "\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains(csharpCall, artifact.Source)
        let parameterId = committed.Functions.Head.Parameters.Head.Id
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference reference when reference = parameterId ->
                                        { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = issueCode)

    let private withSession name (body: (string -> JsonElement) -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            // A snapshot written directly declares its backend as `create` would.
            File.WriteAllText(path + ".backends.json", """{"targets": ["csharp"]}""")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let call (line: string) =
                use response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                response.RootElement.GetProperty("result").Clone()
            body call
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private commit (call: string -> JsonElement) (proposal: JsonElement) =
        call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposal.GetProperty("proposalId").GetString() + "\"}") |> ignore

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored Float64 operations match IEEE 754 and Python math`` () =
        withSession "float64-unary" (fun call ->
            let float64 = """{"kind":"primitive","primitive":"float64"}"""
            let apply kind = """{"kind":""" + "\"" + kind + "\"" + ""","value":{"kind":"parameter","name":"value"}}"""
            let items =
                [ "float64Sqrt"; "float64Abs"; "float64Negate"; "float64Floor"; "float64Ceiling"; "float64Truncate" ]
                |> List.map apply
                |> String.concat ","
            commit call (
                call (
                    """{"id":"all","op":"simulateCreateFunctionByName","exported":true,"displayName":"all","parameters":[{"displayName":"value","type":"""
                    + float64 + """}],"resultType":{"kind":"builtinGeneric","generic":"list","arguments":[""" + float64
                    + """]},"requiredEffects":[],"body":{"kind":"listConstruct","itemType":""" + float64 + ""","items":[""" + items + "]}}"))
            // Expected values from Python: math.sqrt (NaN where it raises for a negative value),
            // abs, unary minus, and math.floor, math.ceil and math.trunc kept as floats with the
            // sign of the input on a zero result, as IEEE 754 roundToIntegral gives.
            let cases: (string * string list) list =
                [
                  "0", [ "0"; "0"; "\"-0\""; "0"; "0"; "0" ]
                  "\"-0\"", [ "\"-0\""; "0"; "0"; "\"-0\""; "\"-0\""; "\"-0\"" ]
                  "2.0", [ "1.4142135623730951"; "2.0"; "-2.0"; "2.0"; "2.0"; "2.0" ]
                  "-2.0", [ "\"NaN\""; "2.0"; "2.0"; "-2.0"; "-2.0"; "-2.0" ]
                  "0.5", [ "0.7071067811865476"; "0.5"; "-0.5"; "0"; "1.0"; "0" ]
                  "-0.5", [ "\"NaN\""; "0.5"; "0.5"; "-1.0"; "\"-0\""; "\"-0\"" ]
                  "1.5", [ "1.224744871391589"; "1.5"; "-1.5"; "1.0"; "2.0"; "1.0" ]
                  "-1.5", [ "\"NaN\""; "1.5"; "1.5"; "-2.0"; "-1.0"; "-1.0" ]
                  "2.5", [ "1.5811388300841898"; "2.5"; "-2.5"; "2.0"; "3.0"; "2.0" ]
                  "-2.5", [ "\"NaN\""; "2.5"; "2.5"; "-3.0"; "-2.0"; "-2.0" ]
                  "0.49999999999999994", [ "0.7071067811865475"; "0.49999999999999994"; "-0.49999999999999994"; "0"; "1.0"; "0" ]
                  "4503599627370495.5", [ "67108863.99999999"; "4503599627370495.5"; "-4503599627370495.5"; "4503599627370495.0"; "4503599627370496.0"; "4503599627370495.0" ]
                  "-4503599627370495.5", [ "\"NaN\""; "4503599627370495.5"; "4503599627370495.5"; "-4503599627370496.0"; "-4503599627370495.0"; "-4503599627370495.0" ]
                  "1e+300", [ "1e+150"; "1e+300"; "-1e+300"; "1e+300"; "1e+300"; "1e+300" ]
                  "5e-324", [ "2.2227587494850775e-162"; "5e-324"; "-5e-324"; "0"; "1.0"; "0" ]
                  "-5e-324", [ "\"NaN\""; "5e-324"; "5e-324"; "-1.0"; "\"-0\""; "\"-0\"" ]
                  "1.7976931348623157e+308", [ "1.3407807929942596e+154"; "1.7976931348623157e+308"; "-1.7976931348623157e+308"; "1.7976931348623157e+308"; "1.7976931348623157e+308"; "1.7976931348623157e+308" ]
                  "\"Infinity\"", [ "\"Infinity\""; "\"Infinity\""; "\"-Infinity\""; "\"Infinity\""; "\"Infinity\""; "\"Infinity\"" ]
                  "\"-Infinity\"", [ "\"NaN\""; "\"Infinity\""; "\"Infinity\""; "\"-Infinity\""; "\"-Infinity\""; "\"-Infinity\"" ]
                  "\"NaN\"", [ "\"NaN\""; "\"NaN\""; "\"NaN\""; "\"NaN\""; "\"NaN\""; "\"NaN\"" ]
                  "123.456", [ "11.111075555498667"; "123.456"; "-123.456"; "123.0"; "124.0"; "123.0" ] ]
            for value, expected in cases do
                let result =
                    call (
                        """{"id":"run","op":"runPureByName","function":"all","arguments":[{"kind":"float64","value":"""
                        + value + """}],"expected":{"kind":"list","itemType":""" + float64 + ""","items":["""
                        + (expected |> List.map (fun item -> """{"kind":"float64","value":""" + item + "}") |> String.concat ",")
                        + "]}}")
                Assert.True(result.GetProperty("matches").GetBoolean(), value + ": " + result.ToString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored ListReverse reverses item order`` () =
        withSession "list-reverse" (fun call ->
            let strings = """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"string"}]}"""
            commit call (
                call (
                    """{"id":"rev","op":"simulateCreateFunctionByName","exported":true,"displayName":"rev","parameters":[{"displayName":"items","type":"""
                    + strings + """}],"resultType":""" + strings
                    + ""","requiredEffects":[],"body":{"kind":"listReverse","value":{"kind":"parameter","name":"items"}}}"""))
            let list (items: string list) =
                """{"kind":"list","itemKind":"string","items":["""
                + (items |> List.map (fun item -> "\"" + item + "\"") |> String.concat ",")
                + "]}"
            for items in [ []; [ "a" ]; [ "a"; "b" ]; [ "a"; "b"; "c" ]; [ "x"; "x"; "y"; "z"; "x" ] ] do
                let result =
                    call (
                        """{"id":"run","op":"runPureByName","function":"rev","arguments":["""
                        + list items + """],"expected":""" + list (List.rev items) + "}")
                Assert.True(result.GetProperty("matches").GetBoolean(), String.Join(",", items) + ": " + result.ToString()))
