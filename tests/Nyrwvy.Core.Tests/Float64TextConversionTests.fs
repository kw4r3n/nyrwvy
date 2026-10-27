namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Float64ToString and StringParseFloat64: Python repr(float) and float(text) on the JSON number
/// grammar as an Option, pure and never failing. Expected values below were generated with CPython 3.
module Float64TextConversionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("float64-text-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value
    let private floatBits (bits: uint64) = ExpressionRecipe.Float64Literal(Float64Bits.create bits)
    let private runFloat (bits: uint64) = PureRunValue.Float64(Float64Bits.create bits)
    let private runFloatOption bits =
        PureRunValue.OptionValue(floatType, bits |> Option.map runFloat)

    /// Python repr(float) for each binary64 bit pattern.
    let private reprCases =
        [ 0x3fb999999999999aUL, "0.1" // 0.1
          0x3fd3333333333334UL, "0.30000000000000004" // 0.1 + 0.2
          0x4341c37937e08000UL, "1e+16" // 1e16
          0x430c6bf526340000UL, "1000000000000000.0" // 1e15
          0x437b69b4ba630f35UL, "1.2345678901234568e+17" // 123456789012345678.0
          0x3eef75104d551d69UL, "1.5e-05" // 1.5e-5
          0x3f1a36e2eb1c432dUL, "0.0001" // 0.0001
          0x0000000000000001UL, "5e-324" // 5e-324
          0x7fefffffffffffffUL, "1.7976931348623157e+308" // 1.7976931348623157e308
          0x8000000000000000UL, "-0.0" // -0.0
          0x0000000000000000UL, "0.0" // 0.0
          0x7ff8000000000000UL, "nan" // nan
          0x7ff0000000000000UL, "inf" // inf
          0xfff0000000000000UL, "-inf" // -inf
          0x4340000000000000UL, "9007199254740992.0" // 2.0**53
          0x3fd5555555555555UL, "0.3333333333333333" // 1/3
          0x4059000000000000UL, "100.0" // 100.0
          0x3ee4f8b588e368f1UL, "1e-05" // 1e-5
          0x4376345785d8a000UL, "1e+17" // 1e17
          0x43118b54f22aeb00UL, "1234567890123456.0" // 1234567890123456.0
          0x42dc12218377de40UL, "123456789012345.0" // 123456789012345.0
          0x3ff0000000000000UL, "1.0" // 1.0
          0xc004000000000000UL, "-2.5" // -2.5
          0x3f50624dd2f1a9fcUL, "0.001" // 0.001
          0x7e37e43c8800759cUL, "1e+300" // 1e300
          0x4480f0cf064dd592UL, "1e+22" // 1e22
          0x000fffffffffffffUL, "2.225073858507201e-308" ] // largest subnormal

    /// Python float(text) for JSON-number text; None where the text is not a JSON number.
    let private parseCases =
        [ "0", Some 0x0000000000000000UL
          "-0", Some 0x8000000000000000UL
          "007", Some 0x401c000000000000UL // leading zeros, as StringParseInt64 accepts them
          "-00.500", Some 0xbfe0000000000000UL
          "0.1", Some 0x3fb999999999999aUL
          "1E5", Some 0x40f86a0000000000UL
          "1e+2", Some 0x4059000000000000UL
          "1e-2", Some 0x3f847ae147ae147bUL
          "1e400", Some 0x7ff0000000000000UL // overflow is infinity, as json.loads gives
          "-1e400", Some 0xfff0000000000000UL
          "1e-400", Some 0x0000000000000000UL // underflow keeps the sign
          "-1e-400", Some 0x8000000000000000UL
          "1e99999999999999999999", Some 0x7ff0000000000000UL
          "-1e99999999999999999999", Some 0xfff0000000000000UL
          "0e-99999999999999999999", Some 0x0000000000000000UL
          "-0e99999999999999999999", Some 0x8000000000000000UL
          // 1 + 2^-53 exactly is a tie and rounds to even; anything above it rounds up.
          "1.00000000000000011102230246251565404236316680908203125", Some 0x3ff0000000000000UL
          "1.00000000000000011", Some 0x3ff0000000000000UL
          "1.000000000000000111022302462515654042363166809082031250001", Some 0x3ff0000000000001UL
          "123456789012345678901234567890", Some 0x45f8ee90ff6c373eUL
          "2.2250738585072011e-308", Some 0x000fffffffffffffUL
          "9007199254740993", Some 0x4340000000000000UL // 2^53 + 1 ties to even
          "4.9406564584124654e-324", Some 0x0000000000000001UL
          "2.4703282292062327e-324", Some 0x0000000000000000UL // just below half the smallest subnormal
          "2.4703282292062328e-324", Some 0x0000000000000001UL
          "1.7976931348623158e308", Some 0x7fefffffffffffffUL
          "", None
          "-", None
          "+1", None
          " 1", None
          "1 ", None
          "\t1", None
          "1\n", None
          ".5", None
          "5.", None
          "-.5", None
          "1.e5", None
          "1e", None
          "1e+", None
          "1.5e5.5", None
          "--1", None
          "inf", None
          "-inf", None
          "Infinity", None
          "nan", None
          "NaN", None
          "1_000", None
          "0x10", None
          "١", None // ARABIC-INDIC DIGIT ONE, which Python float accepts
          "１", None // FULLWIDTH DIGIT ONE
          "1,5", None ]

    [<Fact>]
    let ``Float64ToString and StringParseFloat64 type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "floatText"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped Float64 text builtin was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ stringType ] stringType (ExpressionRecipe.Float64ToString(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.Float64ToString(slot 0))))
        Assert.Equal(mismatch, rejects (request [ floatType ] (optionOf floatType) (ExpressionRecipe.StringParseFloat64(slot 0))))
        // Result types: String and Option<Float64>, not Float64.
        Assert.NotEqual<string>("", rejects (request [ floatType ] floatType (ExpressionRecipe.Float64ToString(slot 0))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] floatType (ExpressionRecipe.StringParseFloat64(slot 0))))
        let cases =
            [ "float64ToString", "NyText.FromFloat64(", floatType, stringType, ExpressionRecipe.Float64ToString(slot 0), SemanticNodeKind.StringLiteral "1.0", "Float64OperandTypeMismatch"
              "stringParseFloat64", "NyText.ParseFloat64(", stringType, optionOf floatType, ExpressionRecipe.StringParseFloat64(slot 0), SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0), "StringIntrinsicTypeMismatch" ]
        for kind, emitted, parameterType, resultType, body, wrongOperand, validationCode in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ parameterType ] resultType body)
                |> unwrap ("simulate " + kind)
            let committed =
                match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
                | other -> failwithf "Commit failed: %A" other
            Assert.Empty(SnapshotValidator.validate committed)
            let json = SnapshotCodec.serialize committed |> unwrap "serialize"
            Assert.Contains($"\"{kind}\"", json)
            let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
            let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
            let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            Assert.Contains($"\"{kind}\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains($"\"{kind}\"", ExecutionIrSerializer.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            Assert.Contains(emitted, artifact.Source)
            // A persisted node over an operand of the other type is a validation error.
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
                                            { node with Kind = wrongOperand }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = validationCode)

    [<Fact>]
    let ``the viewer names Float64ToString and StringParseFloat64 nodes`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-float64-text-viewer-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap "session"
            let create name parameterType resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = [ { DisplayName = "input"; Type = parameterType } ]
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
                session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let cases =
                [ "repr", floatType, stringType, ExpressionRecipe.Float64ToString(ExpressionRecipe.ParameterSlot 0), ViewerNodeRole.Operation "Float64ToString", "float64 to string"
                  "parse", stringType, optionOf floatType, ExpressionRecipe.StringParseFloat64(ExpressionRecipe.ParameterSlot 0), ViewerNodeRole.Operation "StringParseFloat64", "parse float64" ]
            for name, parameterType, resultType, body, role, label in cases do
                let fn = create name parameterType resultType body
                let viewer = session.FocusFunction fn.Id |> unwrap "focus" |> ViewerProjection.focus
                let node = viewer.Graph.Nodes |> List.find (fun item -> item.Id = fn.Body.ResultNodeId)
                Assert.Equal(role, node.Role)
                Assert.Equal<ValueDependencyRole list>([ ValueDependencyRole.Source ], node.ValueDependencies |> List.map (fun item -> item.Role))
                Assert.Contains(label, ViewerHtml.renderFocusDocument viewer)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored Float64ToString and StringParseFloat64 match Python repr and float`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-float64-text-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let author name parameterType resultType kind =
                let line =
                    sprintf
                        """{"id":"%s","op":"simulateCreateFunctionByName","exported":true,"displayName":"%s","parameters":[{"displayName":"input","type":{"kind":"primitive","primitive":"%s"}}],"resultType":%s,"requiredEffects":[],"body":{"kind":"%s","value":{"kind":"parameter","name":"input"}}}"""
                        name name parameterType resultType kind
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            author "repr" "float64" """{"kind":"primitive","primitive":"string"}""" "float64ToString"
            author "parse" "string" """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"float64"}]}""" "stringParseFloat64"
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name arguments =
                (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            Assert.Equal(PureRunValue.String "0.30000000000000004", run "repr" [ runFloat 0x3fd3333333333334UL ])
            Assert.Equal(PureRunValue.String "-0.0", run "repr" [ runFloat 0x8000000000000000UL ])
            Assert.Equal(PureRunValue.String "nan", run "repr" [ runFloat 0x7ff8000000000000UL ])
            Assert.Equal(runFloatOption (Some 0x3fb999999999999aUL), run "parse" [ PureRunValue.String "0.1" ])
            Assert.Equal(runFloatOption (Some 0x8000000000000000UL), run "parse" [ PureRunValue.String "-1e-400" ])
            Assert.Equal(runFloatOption None, run "parse" [ PureRunValue.String "+1" ])
            let create name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = parameters
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            create "reprTable" [] (listOf stringType)
                (ExpressionRecipe.ListConstruct(stringType, reprCases |> List.map (fst >> floatBits >> ExpressionRecipe.Float64ToString)))
            create "parseTable" [] (listOf (optionOf floatType))
                (ExpressionRecipe.ListConstruct(optionOf floatType, parseCases |> List.map (fst >> text >> ExpressionRecipe.StringParseFloat64)))
            // Every repr reads back as the same Float64 (bitwise, -0.0 included) except nan and the
            // infinities, whose Python spellings are not JSON numbers.
            create "roundTripTable" [] (listOf (optionOf floatType))
                (ExpressionRecipe.ListConstruct(optionOf floatType, reprCases |> List.map (fst >> floatBits >> ExpressionRecipe.Float64ToString >> ExpressionRecipe.StringParseFloat64)))
            // Composition with the rest of the String surface: repr text is ASCII, one byte per code point.
            create "reprIsAscii" [] boolType
                (ExpressionRecipe.Int64Equal(
                    ExpressionRecipe.StringUtf8ByteCount(ExpressionRecipe.Float64ToString(floatBits 0x437b69b4ba630f35UL)),
                    ExpressionRecipe.StringLength(ExpressionRecipe.Float64ToString(floatBits 0x437b69b4ba630f35UL))))
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            check "reprTable" (PureRunValue.StringList(reprCases |> List.map snd))
            check "parseTable" (PureRunValue.ListValue(optionOf floatType, parseCases |> List.map (snd >> runFloatOption)))
            let roundTripExpected =
                reprCases
                |> List.map (fun (bits, repr) ->
                    if repr = "nan" || repr = "inf" || repr = "-inf" then None else Some bits)
            check "roundTripTable" (PureRunValue.ListValue(optionOf floatType, roundTripExpected |> List.map runFloatOption))
            check "reprIsAscii" (PureRunValue.Bool true)
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
