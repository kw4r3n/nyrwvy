namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Float64RoundToInt64 and Float64FloorToInt64 (post-M2 order 34; rounding half away from zero since
/// order 43) and floor as Option<Int64>, None for NaN, the infinities and results outside Int64. Never fail.
module Float64ToInt64Tests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("float64-to-int64-" + name)
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Theory>]
    [<InlineData("round", "float64RoundToInt64", "float64-round-to-int64", "NyNumber.RoundToInt64(")>]
    [<InlineData("floor", "float64FloorToInt64", "float64-floor-to-int64", "NyNumber.FloorToInt64(")>]
    let ``Float64 to Int64 conversions type check, persist, and lower through every layer``
        (mode: string, wireKind: string, nyasonKind: string, csharpCall: string) =
        let build value =
            if mode = "round" then ExpressionRecipe.Float64RoundToInt64 value else ExpressionRecipe.Float64FloorToInt64 value
        let snapshot = Snapshot.empty (id<SnapshotTag> ("base-" + mode))
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = mode
              Parameters = [ { DisplayName = "value"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped conversion was accepted: %A" other
        let body = build (ExpressionRecipe.ParameterSlot 0)
        Assert.Equal("PureExpressionOperandTypeMismatch", rejects (request intType (optionOf intType) body))
        Assert.NotEqual<string>("", rejects (request floatType intType body))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request floatType (optionOf intType) body) |> unwrap "simulate"
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
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "Float64OperandTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored conversions match Python round and math.floor`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-float64-to-int64-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let call (line: string) =
                use response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                response.RootElement.GetProperty("result").Clone()
            let proposal =
                call
                    """{"id":"both","op":"simulateCreateFunctionByName","exported":true,"displayName":"both","parameters":[{"displayName":"value","type":{"kind":"primitive","primitive":"float64"}}],"resultType":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}]},"requiredEffects":[],"body":{"kind":"listConstruct","itemType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]},"items":[{"kind":"float64RoundToInt64","value":{"kind":"parameter","name":"value"}},{"kind":"float64FloorToInt64","value":{"kind":"parameter","name":"value"}}]}}"""
            call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposal.GetProperty("proposalId").GetString() + "\"}") |> ignore
            let option (value: string) =
                if isNull value then """{"kind":"option","itemKind":"int64","hasValue":false}"""
                else """{"kind":"option","itemKind":"int64","hasValue":true,"value":""" + "\"" + value + "\"}"
            // Each expected pair is x rounded half away from zero and math.floor(x), or None where the
            // result is outside Int64 or x is not finite.
            let cases: (string * string * string) list =
                [ "0.0", "0", "0"
                  "\"-0\"", "0", "0"
                  "0.5", "1", "0"
                  "1.5", "2", "1"
                  "2.5", "3", "2"
                  "-0.5", "-1", "-1"
                  "-1.5", "-2", "-2"
                  "-2.5", "-3", "-3"
                  "0.49999999999999994", "0", "0"
                  "-0.49999999999999994", "0", "-1"
                  "4503599627370495.5", "4503599627370496", "4503599627370495"
                  "-4503599627370495.5", "-4503599627370496", "-4503599627370496"
                  "4503599627370497.0", "4503599627370497", "4503599627370497"
                  "5e-324", "0", "0"
                  "-5e-324", "0", "-1"
                  "123.456", "123", "123"
                  "-123.456", "-123", "-124"
                  // The largest double below 2^63, then -2^63 itself, both in range; 2^63 and the
                  // double below -2^63 are not.
                  "9223372036854774784.0", "9223372036854774784", "9223372036854774784"
                  "-9223372036854775808.0", "-9223372036854775808", "-9223372036854775808"
                  "9223372036854775808.0", null, null
                  "-9223372036854777856.0", null, null
                  "1e300", null, null
                  "\"Infinity\"", null, null
                  "\"-Infinity\"", null, null
                  "\"NaN\"", null, null ]
            for value, rounded, floored in cases do
                let result =
                    call (
                        """{"id":"run","op":"runPureByName","function":"both","arguments":[{"kind":"float64","value":"""
                        + value
                        + """}],"expected":{"kind":"list","itemType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]},"items":["""
                        + option rounded + "," + option floored + "]}}")
                Assert.True(result.GetProperty("matches").GetBoolean(), value + ": " + result.ToString())
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
