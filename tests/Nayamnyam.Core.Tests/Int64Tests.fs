namespace Nayamnyam.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nayamnyam.Core
open Xunit

type private Int64Fixture =
    { Snapshot: Snapshot
      AddFunctionId: FunctionId
      SubtractFunctionId: FunctionId
      MultiplyFunctionId: FunctionId
      EqualFunctionId: FunctionId
      LessFunctionId: FunctionId
      LessOrEqualFunctionId: FunctionId
      GreaterFunctionId: FunctionId
      GreaterOrEqualFunctionId: FunctionId
      AddResultNodeId: GraphNodeId
      AddLeftNodeId: GraphNodeId
      AddRightNodeId: GraphNodeId }

module Int64Tests =
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-int64-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-int64-" + name)
    let private snapshotId = StableId.createForTesting<SnapshotTag> "snapshot-int64"
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool

    let private literal id value: SemanticNode =
        { Id = nodeId id
          Kind = SemanticNodeKind.Int64Literal value }

    let private binaryFunction
        name
        resultType
        leftValue
        rightValue
        kind
        : SemanticFunction * GraphNodeId * GraphNodeId * GraphNodeId =
        let leftId = nodeId (name + "-left")
        let rightId = nodeId (name + "-right")
        let resultId = nodeId (name + "-result")
        let left = { Id = leftId; Kind = SemanticNodeKind.Int64Literal leftValue }
        let right = { Id = rightId; Kind = SemanticNodeKind.Int64Literal rightValue }
        let result = { Id = resultId; Kind = kind leftId rightId }
        { Id = functionId name
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

    let private createFixture () =
        let add, addLeft, addRight, addResult =
            binaryFunction
                "wrapping-add"
                int64Type
                Int64.MaxValue
                1L
                (fun left right -> SemanticNodeKind.Int64WrappingAdd(left, right))
        let subtract, _, _, _ =
            binaryFunction
                "wrapping-subtract"
                int64Type
                Int64.MinValue
                1L
                (fun left right -> SemanticNodeKind.Int64WrappingSubtract(left, right))
        let multiply, _, _, _ =
            binaryFunction
                "wrapping-multiply"
                int64Type
                Int64.MaxValue
                2L
                (fun left right -> SemanticNodeKind.Int64WrappingMultiply(left, right))
        let equal, _, _, _ =
            binaryFunction
                "equal"
                boolType
                3L
                3L
                (fun left right -> SemanticNodeKind.Int64Equal(left, right))
        let less, _, _, _ =
            binaryFunction
                "less-than"
                boolType
                2L
                3L
                (fun left right -> SemanticNodeKind.Int64LessThan(left, right))
        let lessOrEqual, _, _, _ =
            binaryFunction
                "less-than-or-equal"
                boolType
                3L
                3L
                (fun left right -> SemanticNodeKind.Int64LessThanOrEqual(left, right))
        let greater, _, _, _ =
            binaryFunction
                "greater-than"
                boolType
                4L
                3L
                (fun left right -> SemanticNodeKind.Int64GreaterThan(left, right))
        let greaterOrEqual, _, _, _ =
            binaryFunction
                "greater-than-or-equal"
                boolType
                3L
                3L
                (fun left right -> SemanticNodeKind.Int64GreaterThanOrEqual(left, right))

        { Snapshot =
            { Id = snapshotId
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ add; subtract; multiply; equal; less; lessOrEqual; greater; greaterOrEqual ]
              Obligations = [] }
          AddFunctionId = add.Id
          SubtractFunctionId = subtract.Id
          MultiplyFunctionId = multiply.Id
          EqualFunctionId = equal.Id
          LessFunctionId = less.Id
          LessOrEqualFunctionId = lessOrEqual.Id
          GreaterFunctionId = greater.Id
          GreaterOrEqualFunctionId = greaterOrEqual.Id
          AddResultNodeId = addResult
          AddLeftNodeId = addLeft
          AddRightNodeId = addRight }

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

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

    [<Fact>]
    let ``Int64 nodes expose local types dependencies and viewer roles`` () =
        let fixture = createFixture ()
        Assert.Empty(SnapshotValidator.validate fixture.Snapshot)

        let focus = SemanticQuery.focusFunction fixture.Snapshot fixture.AddFunctionId |> unwrap "Focus query"
        let resultType =
            focus.NodeTypes
            |> List.find (fun item -> item.NodeId = fixture.AddResultNodeId)
            |> fun item -> item.Type
        Assert.Equal(Some int64Type, resultType)

        let dependencies =
            SemanticQuery.getDependencies fixture.Snapshot fixture.AddFunctionId fixture.AddResultNodeId
            |> unwrap "Dependency query"
        Assert.Equal<GraphNodeId list>([ fixture.AddLeftNodeId; fixture.AddRightNodeId ] |> List.sortBy StableId.value, dependencies)

        let viewer = ViewerProjection.focus focus
        let resultNode = viewer.Graph.Nodes |> List.find (fun node -> node.Id = fixture.AddResultNodeId)
        Assert.Equal(ViewerNodeRole.Int64WrappingAdd, resultNode.Role)
        Assert.Equal(Some int64Type, resultNode.Type)

        let html = ViewerHtml.renderFocusDocument viewer
        Assert.Contains("Int64 wrapping add", html)
        Assert.Contains("<code>Int64</code>", html)

    [<Fact>]
    let ``Int64 operations reject non-Int64 operands`` () =
        let fixture = createFixture ()
        let changedFunctions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.AddFunctionId then
                    functionEntity
                else
                    let nodes =
                        functionEntity.Body.Nodes
                        |> List.map (fun node ->
                            if node.Id = fixture.AddRightNodeId then
                                { node with Kind = SemanticNodeKind.StringLiteral "not-an-int64" }
                            else node)
                    { functionEntity with Body = { functionEntity.Body with Nodes = nodes } })
        let invalid = { fixture.Snapshot with Functions = changedFunctions }
        let issues = SnapshotValidator.validate invalid
        Assert.Contains(issues, fun issue -> issue.Code = "Int64OperandTypeMismatch")

    [<Fact>]
    let ``JSON and Nyason preserve full-range Int64 literals and node kinds`` () =
        let fixture = createFixture ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap "JSON serialization"
        let fromJson = SnapshotCodec.deserialize json |> unwrap "JSON deserialization"
        let nyason = NyasonCodec.serialize fixture.Snapshot |> unwrap "Nyason serialization"
        let fromNyason = NyasonCodec.deserialize nyason |> unwrap "Nyason deserialization"

        let literals snapshot =
            snapshot.Functions
            |> List.collect (fun functionEntity -> ValueGraph.collectNodes functionEntity.Body)
            |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.Int64Literal value -> Some value
                | _ -> None)

        for restored in [ fromJson; fromNyason ] do
            let values = literals restored
            Assert.Contains(Int64.MinValue, values)
            Assert.Contains(Int64.MaxValue, values)
            let add = restored.Functions |> List.find (fun functionEntity -> functionEntity.Id = fixture.AddFunctionId)
            let result = add.Body.Nodes |> List.find (fun node -> node.Id = fixture.AddResultNodeId)
            match result.Kind with
            | SemanticNodeKind.Int64WrappingAdd _ -> ()
            | other -> failwithf "Expected Int64WrappingAdd after round-trip, got %A" other

        Assert.Contains("\"schemaVersion\":5", json)
        Assert.Contains("int64-wrapping-add", nyason)
        Assert.Contains("primitive-int64", nyason)

    [<Fact>]
    let ``Typed and Execution IR preserve every initial Int64 semantic role`` () =
        let fixture = createFixture ()
        let typed, execution = lower fixture

        let typedKinds = typed.Functions |> List.collect (fun functionEntity -> functionEntity.Body.Nodes) |> List.map (fun node -> node.Kind)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64WrappingAdd _ -> true | _ -> false)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64WrappingSubtract _ -> true | _ -> false)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64WrappingMultiply _ -> true | _ -> false)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64Equal _ -> true | _ -> false)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64LessThan _ -> true | _ -> false)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64LessThanOrEqual _ -> true | _ -> false)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64GreaterThan _ -> true | _ -> false)
        Assert.Contains(typedKinds, function TypedSemanticNodeKind.Int64GreaterThanOrEqual _ -> true | _ -> false)

        let executionKinds =
            execution.Functions
            |> List.collect (fun functionEntity -> functionEntity.Blocks)
            |> List.collect (fun block -> block.Instructions)
            |> List.map (fun instruction -> instruction.Kind)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64WrappingAdd _ -> true | _ -> false)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64WrappingSubtract _ -> true | _ -> false)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64WrappingMultiply _ -> true | _ -> false)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64Equal _ -> true | _ -> false)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64LessThan _ -> true | _ -> false)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64LessThanOrEqual _ -> true | _ -> false)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64GreaterThan _ -> true | _ -> false)
        Assert.Contains(executionKinds, function ExecutionInstructionKind.Int64GreaterThanOrEqual _ -> true | _ -> false)

        let typedJson = TypedSemanticIrCodec.serialize typed
        let executionJson = ExecutionIrCodec.serialize execution
        Assert.Contains("\"schemaVersion\":2", typedJson)
        Assert.Contains("\"primitiveInt64\"", typedJson)
        Assert.Contains("\"int64WrappingAdd\"", executionJson)

    [<Fact>]
    let ``C# source makes Int64 wrapping and minimum literal explicit`` () =
        let fixture = createFixture ()
        let artifact = emit fixture
        Assert.Contains("long.MinValue", artifact.Source)
        Assert.Contains("unchecked(", artifact.Source)
        Assert.Contains(" + ", artifact.Source)
        Assert.Contains(" - ", artifact.Source)
        Assert.Contains(" * ", artifact.Source)

    [<Fact>]
    let ``generated C# executes Int64 wrapping arithmetic and comparisons`` () =
        let fixture = createFixture ()
        let artifact = emit fixture
        let tempRoot = Path.Combine(Path.GetTempPath(), "nayamnyam-int64-" + Guid.NewGuid().ToString("N"))
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
            let call functionId = "Console.WriteLine(NyFunctions." + CSharpBackendNames.functionName functionId + "());"
            let harness =
                String.concat
                    Environment.NewLine
                    ([ "using System;"; "using Nayamnyam.Generated;"; "" ]
                     @ [ call fixture.AddFunctionId
                         call fixture.SubtractFunctionId
                         call fixture.MultiplyFunctionId
                         call fixture.EqualFunctionId
                         call fixture.LessFunctionId
                         call fixture.LessOrEqualFunctionId
                         call fixture.GreaterFunctionId
                         call fixture.GreaterOrEqualFunctionId ])

            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), projectFile)
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), harness)

            let buildExit, buildOut, buildErr =
                runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            if buildExit <> 0 then
                failwithf "Generated Int64 C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s" buildOut buildErr artifact.Source

            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated Int64 C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr

            let normalized = runOut.Replace("\r\n", "\n").Trim()
            let expected =
                String.concat "\n"
                    [ string Int64.MinValue
                      string Int64.MaxValue
                      "-2"
                      "True"
                      "True"
                      "True"
                      "True"
                      "True" ]
            Assert.Equal(expected, normalized)
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``Int64 semantic changes participate in semantic fingerprints`` () =
        let fixture = createFixture ()
        let original = SemanticFingerprint.compute fixture.Snapshot |> unwrap "original semantic fingerprint"
        let changedFunctions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.AddFunctionId then functionEntity
                else
                    let nodes =
                        functionEntity.Body.Nodes
                        |> List.map (fun node ->
                            if node.Id = fixture.AddLeftNodeId then
                                { node with Kind = SemanticNodeKind.Int64Literal(Int64.MaxValue - 1L) }
                            else node)
                    { functionEntity with Body = { functionEntity.Body with Nodes = nodes } })
        let changed = { fixture.Snapshot with Functions = changedFunctions }
        let changedFingerprint = SemanticFingerprint.compute changed |> unwrap "changed semantic fingerprint"
        Assert.NotEqual(original, changedFingerprint)
