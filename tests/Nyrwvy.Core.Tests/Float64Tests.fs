namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.Globalization
open System.IO
open Nyrwvy.Core
open Xunit

type private Float64Fixture =
    { Snapshot: Snapshot
      AddFunctionId: FunctionId
      SubtractFunctionId: FunctionId
      MultiplyFunctionId: FunctionId
      DivideFunctionId: FunctionId
      ZeroEqualFunctionId: FunctionId
      NaNEqualFunctionId: FunctionId
      LessFunctionId: FunctionId
      IsNaNFunctionId: FunctionId
      IsFiniteFunctionId: FunctionId
      IsInfiniteFunctionId: FunctionId
      AddResultNodeId: GraphNodeId
      AddLeftNodeId: GraphNodeId
      AddRightNodeId: GraphNodeId
      ZeroNegativeNodeId: GraphNodeId
      NaNLeftNodeId: GraphNodeId }

module Float64Tests =
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-float64-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-float64-" + name)
    let private snapshotId = StableId.createForTesting<SnapshotTag> "snapshot-float64"
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool

    let private bits value = Float64Bits.ofFloat value

    let private binaryFunction
        name
        resultType
        leftBits
        rightBits
        kind
        : SemanticFunction * GraphNodeId * GraphNodeId * GraphNodeId =
        let leftId = nodeId (name + "-left")
        let rightId = nodeId (name + "-right")
        let resultId = nodeId (name + "-result")
        let left = { Id = leftId; Kind = SemanticNodeKind.Float64Literal leftBits }
        let right = { Id = rightId; Kind = SemanticNodeKind.Float64Literal rightBits }
        let result = { Id = resultId; Kind = kind leftId rightId }
        { Exported = true; Id = functionId name
          TypeParameters = []
          DisplayName = name
          Parameters = []
          ResultType = resultType
          RequiredEffects = []
          Body =
            { Nodes = [ left; right; result ]
              ResultNodeId = resultId } },
        leftId,
        rightId,
        resultId

    let private unaryFunction
        name
        valueBits
        kind
        : SemanticFunction * GraphNodeId * GraphNodeId =
        let valueId = nodeId (name + "-value")
        let resultId = nodeId (name + "-result")
        let value = { Id = valueId; Kind = SemanticNodeKind.Float64Literal valueBits }
        let result = { Id = resultId; Kind = kind valueId }
        { Exported = true; Id = functionId name
          TypeParameters = []
          DisplayName = name
          Parameters = []
          ResultType = boolType
          RequiredEffects = []
          Body =
            { Nodes = [ value; result ]
              ResultNodeId = resultId } },
        valueId,
        resultId

    let private createFixture () =
        let add, addLeft, addRight, addResult =
            binaryFunction
                "add"
                float64Type
                (bits 1.5)
                (bits 2.25)
                (fun left right -> SemanticNodeKind.Float64Add(left, right))
        let subtract, _, _, _ =
            binaryFunction
                "subtract"
                float64Type
                (bits 5.0)
                (bits 2.0)
                (fun left right -> SemanticNodeKind.Float64Subtract(left, right))
        let multiply, _, _, _ =
            binaryFunction
                "multiply"
                float64Type
                (bits -2.0)
                (bits 4.0)
                (fun left right -> SemanticNodeKind.Float64Multiply(left, right))
        let divide, _, _, _ =
            binaryFunction
                "divide-by-zero"
                float64Type
                (bits 1.0)
                (bits 0.0)
                (fun left right -> SemanticNodeKind.Float64Divide(left, right))
        let zeroEqual, _, zeroNegative, _ =
            binaryFunction
                "signed-zero-equal"
                boolType
                (bits 0.0)
                (Float64Bits.create 0x8000000000000000UL)
                (fun left right -> SemanticNodeKind.Float64Equal(left, right))
        let nanEqual, nanLeft, _, _ =
            binaryFunction
                "nan-equal"
                boolType
                (Float64Bits.create 0x7ff0000000000001UL)
                (Float64Bits.create 0xfff8000000000001UL)
                (fun left right -> SemanticNodeKind.Float64Equal(left, right))
        let less, _, _, _ =
            binaryFunction
                "less-than"
                boolType
                (bits -1.0)
                (bits 0.0)
                (fun left right -> SemanticNodeKind.Float64LessThan(left, right))
        let isNaN, _, _ =
            unaryFunction
                "is-nan"
                (Float64Bits.create 0x7ff123456789abcdUL)
                SemanticNodeKind.Float64IsNaN
        let isFinite, _, _ =
            unaryFunction "is-finite" (bits 123.5) SemanticNodeKind.Float64IsFinite
        let isInfinite, _, _ =
            unaryFunction
                "is-infinite"
                (Float64Bits.create 0x7ff0000000000000UL)
                SemanticNodeKind.Float64IsInfinite

        { Snapshot =
            { Id = snapshotId
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions =
                [ add
                  subtract
                  multiply
                  divide
                  zeroEqual
                  nanEqual
                  less
                  isNaN
                  isFinite
                  isInfinite ]
              Obligations = [] }
          AddFunctionId = add.Id
          SubtractFunctionId = subtract.Id
          MultiplyFunctionId = multiply.Id
          DivideFunctionId = divide.Id
          ZeroEqualFunctionId = zeroEqual.Id
          NaNEqualFunctionId = nanEqual.Id
          LessFunctionId = less.Id
          IsNaNFunctionId = isNaN.Id
          IsFiniteFunctionId = isFinite.Id
          IsInfiniteFunctionId = isInfinite.Id
          AddResultNodeId = addResult
          AddLeftNodeId = addLeft
          AddRightNodeId = addRight
          ZeroNegativeNodeId = zeroNegative
          NaNLeftNodeId = nanLeft }

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private requireMatch label predicate items =
        if not (items |> List.exists predicate) then
            failwithf "Expected %s, got %A" label items

    let private lower fixture =
        let typed = TypedSemanticIr.project fixture.Snapshot |> unwrap "Typed Semantic IR projection"
        let execution = ExecutionIr.lower typed |> unwrap "Execution IR lowering"
        typed, execution

    let private emit fixture =
        let _, execution = lower fixture
        CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "C# emission"

    let private runDotnet (workingDirectory: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        startInfo.Environment["DOTNET_NOLOGO"] <- "1"
        for argument in arguments do
            startInfo.ArgumentList.Add(argument)

        use childProcess = Process.Start(startInfo)
        if isNull childProcess then failwith "Failed to start dotnet process."
        let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
        let stderrTask = childProcess.StandardError.ReadToEndAsync()
        childProcess.WaitForExit()
        childProcess.ExitCode, stdoutTask.Result, stderrTask.Result

    let private replaceNodeKind functionId targetNodeId replacementKind (snapshot: Snapshot) =
        let functions =
            snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> functionId then
                    functionEntity
                else
                    let nodes =
                        functionEntity.Body.Nodes
                        |> List.map (fun node ->
                            if node.Id = targetNodeId then
                                { node with Kind = replacementKind }
                            else
                                node)
                    { functionEntity with Body = { functionEntity.Body with Nodes = nodes } })
        { snapshot with Functions = functions }

    let private literalBits (snapshot: Snapshot) =
        snapshot.Functions
        |> List.collect (fun functionEntity -> ValueGraph.collectNodes functionEntity.Body)
        |> List.choose (fun node ->
            match node.Kind with
            | SemanticNodeKind.Float64Literal value -> Some value
            | _ -> None)

    [<Fact>]
    let ``Float64 literal authority preserves exact non-NaN bits and canonicalizes every NaN`` () =
        let positiveZero = Float64Bits.create 0x0000000000000000UL
        let negativeZero = Float64Bits.create 0x8000000000000000UL
        let positiveInfinity = Float64Bits.create 0x7ff0000000000000UL
        let payloadNaN = Float64Bits.create 0x7ff0000000000001UL
        let signedPayloadNaN = Float64Bits.create 0xfff123456789abcdUL

        Assert.Equal(0x0000000000000000UL, Float64Bits.value positiveZero)
        Assert.Equal(0x8000000000000000UL, Float64Bits.value negativeZero)
        Assert.Equal(0x7ff0000000000000UL, Float64Bits.value positiveInfinity)
        Assert.Equal(Float64Bits.canonicalNaNBits, Float64Bits.value payloadNaN)
        Assert.Equal(Float64Bits.canonicalNaNBits, Float64Bits.value signedPayloadNaN)
        Assert.True(Float64Bits.isNaN payloadNaN)
        Assert.Equal("8000000000000000", Float64Bits.toHex negativeZero)

    [<Fact>]
    let ``Float64 nodes expose local types dependencies exact literals and viewer roles`` () =
        let fixture = createFixture ()
        Assert.Empty(SnapshotValidator.validate fixture.Snapshot)

        let focus = SemanticQuery.focusFunction fixture.Snapshot fixture.AddFunctionId |> unwrap "Focus query"
        let resultType =
            focus.NodeTypes
            |> List.find (fun item -> item.NodeId = fixture.AddResultNodeId)
            |> fun item -> item.Type
        Assert.Equal(Some float64Type, resultType)

        let dependencies =
            SemanticQuery.getDependencies fixture.Snapshot fixture.AddFunctionId fixture.AddResultNodeId
            |> unwrap "Dependency query"
        let expectedDependencies = [ fixture.AddLeftNodeId; fixture.AddRightNodeId ] |> List.sortBy StableId.value
        Assert.Equal<GraphNodeId list>(expectedDependencies, dependencies)

        let zeroFocus =
            SemanticQuery.focusFunction fixture.Snapshot fixture.ZeroEqualFunctionId
            |> unwrap "Signed-zero Focus query"
            |> ViewerProjection.focus
        requireMatch
            "Float64Equal viewer role"
            (fun node -> node.Role = ViewerNodeRole.Operation "Float64Equal")
            zeroFocus.Graph.Nodes
        requireMatch
            "negative-zero viewer literal"
            (fun node ->
                match node.Role with
                | ViewerNodeRole.Float64Literal value -> Float64Bits.value value = 0x8000000000000000UL
                | _ -> false)
            zeroFocus.Graph.Nodes

        let html = ViewerHtml.renderFocusDocument zeroFocus
        Assert.Contains("Float64 equal", html)
        Assert.Contains("Float64 literal 0x8000000000000000", html)
        Assert.Contains("<code>Float64</code>", html)

    [<Fact>]
    let ``Float64 operations and classifiers reject non-Float64 operands`` () =
        let fixture = createFixture ()
        let invalidBinary =
            replaceNodeKind
                fixture.AddFunctionId
                fixture.AddRightNodeId
                (SemanticNodeKind.Int64Literal 2L)
                fixture.Snapshot
        let binaryIssues = SnapshotValidator.validate invalidBinary
        requireMatch
            "Float64OperandTypeMismatch for binary operation"
            (fun issue -> issue.Code = "Float64OperandTypeMismatch")
            binaryIssues

        let classifier =
            fixture.Snapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.IsNaNFunctionId)
        let classifierValueId =
            classifier.Body.Nodes
            |> List.pick (fun node ->
                match node.Kind with
                | SemanticNodeKind.Float64Literal _ -> Some node.Id
                | _ -> None)
        let invalidClassifier =
            replaceNodeKind
                fixture.IsNaNFunctionId
                classifierValueId
                (SemanticNodeKind.BoolLiteral true)
                fixture.Snapshot
        let classifierIssues = SnapshotValidator.validate invalidClassifier
        requireMatch
            "Float64OperandTypeMismatch for classifier"
            (fun issue -> issue.Code = "Float64OperandTypeMismatch")
            classifierIssues

    [<Fact>]
    let ``JSON and Nyason preserve binary64 bits while canonicalizing external NaN payloads`` () =
        let fixture = createFixture ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap "JSON serialization"
        let restoredJson = SnapshotCodec.deserialize json |> unwrap "JSON deserialization"
        let nyason = NyasonCodec.serialize fixture.Snapshot |> unwrap "Nyason serialization"
        let restoredNyason = NyasonCodec.deserialize nyason |> unwrap "Nyason deserialization"

        for restored in [ restoredJson; restoredNyason ] do
            let values = literalBits restored |> List.map Float64Bits.value
            if not (List.contains 0x0000000000000000UL values) then
                failwith "Positive zero binary64 bits were not preserved."
            if not (List.contains 0x8000000000000000UL values) then
                failwith "Negative zero binary64 bits were not preserved."
            if not (List.contains Float64Bits.canonicalNaNBits values) then
                failwith "Canonical NaN binary64 bits were not preserved."

        let reserializedJson = SnapshotCodec.serialize restoredJson |> unwrap "JSON reserialization"
        let reserializedNyason = NyasonCodec.serialize restoredNyason |> unwrap "Nyason reserialization"
        Assert.Equal(json, reserializedJson)
        Assert.Equal(nyason, reserializedNyason)
        Assert.Contains("\"schemaVersion\":9", json)
        Assert.Contains("\"bits\":\"8000000000000000\"", json)
        Assert.Contains("primitive-float64", nyason)
        Assert.Contains("float64-divide", nyason)

        let payload = "7ff0000000000001"
        let canonical = Float64Bits.canonicalNaNBits.ToString("x16", CultureInfo.InvariantCulture)
        let externalJson = json.Replace(canonical, payload)
        let externalNyason = nyason.Replace(canonical, payload)
        let canonicalJson =
            SnapshotCodec.deserialize externalJson
            |> unwrap "external NaN JSON deserialization"
            |> SnapshotCodec.serialize
            |> unwrap "canonical JSON serialization"
        let canonicalNyason =
            NyasonCodec.deserialize externalNyason
            |> unwrap "external NaN Nyason deserialization"
            |> NyasonCodec.serialize
            |> unwrap "canonical Nyason serialization"
        Assert.DoesNotContain(payload, canonicalJson)
        Assert.DoesNotContain(payload, canonicalNyason)
        Assert.Contains(canonical, canonicalJson)
        Assert.Contains(canonical, canonicalNyason)

    [<Fact>]
    let ``Typed and Execution IR preserve every initial Float64 semantic role and exact literal bits`` () =
        let fixture = createFixture ()
        let typed, execution = lower fixture

        let typedKinds =
            typed.Functions
            |> List.collect (fun functionEntity -> functionEntity.Body.Nodes)
            |> List.map (fun node -> node.Kind)
        requireMatch "Typed Float64Literal" (function TypedSemanticNodeKind.Float64Literal _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64Add" (function TypedSemanticNodeKind.Float64Add _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64Subtract" (function TypedSemanticNodeKind.Float64Subtract _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64Multiply" (function TypedSemanticNodeKind.Float64Multiply _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64Divide" (function TypedSemanticNodeKind.Float64Divide _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64Equal" (function TypedSemanticNodeKind.Float64Equal _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64LessThan" (function TypedSemanticNodeKind.Float64LessThan _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64IsNaN" (function TypedSemanticNodeKind.Float64IsNaN _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64IsFinite" (function TypedSemanticNodeKind.Float64IsFinite _ -> true | _ -> false) typedKinds
        requireMatch "Typed Float64IsInfinite" (function TypedSemanticNodeKind.Float64IsInfinite _ -> true | _ -> false) typedKinds

        let executionKinds =
            execution.Functions
            |> List.collect (fun functionEntity -> functionEntity.Blocks)
            |> List.collect (fun block -> block.Instructions)
            |> List.map (fun instruction -> instruction.Kind)
        requireMatch "Execution Float64Literal" (function ExecutionInstructionKind.Float64Literal _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64Add" (function ExecutionInstructionKind.Float64Add _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64Subtract" (function ExecutionInstructionKind.Float64Subtract _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64Multiply" (function ExecutionInstructionKind.Float64Multiply _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64Divide" (function ExecutionInstructionKind.Float64Divide _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64Equal" (function ExecutionInstructionKind.Float64Equal _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64LessThan" (function ExecutionInstructionKind.Float64LessThan _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64IsNaN" (function ExecutionInstructionKind.Float64IsNaN _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64IsFinite" (function ExecutionInstructionKind.Float64IsFinite _ -> true | _ -> false) executionKinds
        requireMatch "Execution Float64IsInfinite" (function ExecutionInstructionKind.Float64IsInfinite _ -> true | _ -> false) executionKinds

        let typedJson = TypedSemanticIrSerializer.serialize typed
        let executionJson = ExecutionIrSerializer.serialize execution
        Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", typedJson)
        Assert.Contains("\"primitiveFloat64\"", typedJson)
        Assert.Contains("\"bits\":\"8000000000000000\"", typedJson)
        Assert.Contains("\"float64Divide\"", executionJson)
        Assert.Contains("\"float64IsNaN\"", executionJson)

    [<Fact>]
    let ``C# source emits exact binary64 literals and explicit IEEE classifiers`` () =
        let fixture = createFixture ()
        let artifact = emit fixture
        Assert.Contains("double ", artifact.Source)
        Assert.Contains("BitConverter.UInt64BitsToDouble(0x8000000000000000UL)", artifact.Source)
        Assert.Contains("BitConverter.UInt64BitsToDouble(0x7ff8000000000000UL)", artifact.Source)
        Assert.Contains("double.IsNaN(", artifact.Source)
        Assert.Contains("double.IsFinite(", artifact.Source)
        Assert.Contains("double.IsInfinity(", artifact.Source)
        Assert.Contains(" / ", artifact.Source)

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated C# executes IEEE binary64 arithmetic equality division and classification`` () =
        let fixture = createFixture ()
        let artifact = emit fixture
        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-float64-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let projectFile =
                """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
"""
            let callDouble functionId =
                "Console.WriteLine(NyFunctions."
                + CSharpBackendNames.functionName functionId
                + "().ToString(\"R\", CultureInfo.InvariantCulture));"
            let callBool functionId =
                "Console.WriteLine(NyFunctions." + CSharpBackendNames.functionName functionId + "());"
            let harness =
                String.concat
                    Environment.NewLine
                    ([ "using System;"
                       "using System.Globalization;"
                       "using Nyrwvy.Generated;"
                       "" ]
                     @ [ callDouble fixture.AddFunctionId
                         callDouble fixture.SubtractFunctionId
                         callDouble fixture.MultiplyFunctionId
                         callDouble fixture.DivideFunctionId
                         callBool fixture.ZeroEqualFunctionId
                         callBool fixture.NaNEqualFunctionId
                         callBool fixture.LessFunctionId
                         callBool fixture.IsNaNFunctionId
                         callBool fixture.IsFiniteFunctionId
                         callBool fixture.IsInfiniteFunctionId ])

            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), projectFile)
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), harness)

            let buildExit, buildOut, buildErr =
                runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            if buildExit <> 0 then
                failwithf "Generated Float64 C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s" buildOut buildErr artifact.Source

            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated Float64 C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr

            let normalized = runOut.Replace("\r\n", "\n").Trim()
            let expected =
                String.concat
                    "\n"
                    [ "3.75"
                      "3"
                      "-8"
                      "Infinity"
                      "True"
                      "False"
                      "True"
                      "True"
                      "True"
                      "True" ]
            Assert.Equal(expected, normalized)
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``Float64 semantic fingerprints distinguish signed zero and ignore NaN payload identity`` () =
        let fixture = createFixture ()
        let original = SemanticFingerprint.compute fixture.Snapshot |> unwrap "original semantic fingerprint"
        let zeroChanged =
            replaceNodeKind
                fixture.ZeroEqualFunctionId
                fixture.ZeroNegativeNodeId
                (SemanticNodeKind.Float64Literal(Float64Bits.create 0x0000000000000000UL))
                fixture.Snapshot
        let zeroChangedFingerprint =
            SemanticFingerprint.compute zeroChanged |> unwrap "signed-zero semantic fingerprint"
        Assert.NotEqual(original, zeroChangedFingerprint)

        let payloadA =
            replaceNodeKind
                fixture.NaNEqualFunctionId
                fixture.NaNLeftNodeId
                (SemanticNodeKind.Float64Literal(Float64Bits.create 0x7ff0000000000001UL))
                fixture.Snapshot
        let payloadB =
            replaceNodeKind
                fixture.NaNEqualFunctionId
                fixture.NaNLeftNodeId
                (SemanticNodeKind.Float64Literal(Float64Bits.create 0xfff123456789abcdUL))
                fixture.Snapshot
        let fingerprintA = SemanticFingerprint.compute payloadA |> unwrap "NaN payload A fingerprint"
        let fingerprintB = SemanticFingerprint.compute payloadB |> unwrap "NaN payload B fingerprint"
        Assert.Equal(fingerprintA, fingerprintB)
