namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Xunit

module ListTests =
    type private ListFixture =
        { Snapshot: Snapshot
          PipelineFunctionId: FunctionId
          EmptyLengthFunctionId: FunctionId
          FirstItemNodeId: GraphNodeId
          SecondItemNodeId: GraphNodeId
          FirstListNodeId: GraphNodeId
          ThirdItemNodeId: GraphNodeId
          AppendedListNodeId: GraphNodeId
          FourthItemNodeId: GraphNodeId
          FifthItemNodeId: GraphNodeId
          SecondListNodeId: GraphNodeId
          ConcatenatedListNodeId: GraphNodeId
          PipelineLengthNodeId: GraphNodeId
          EmptyListNodeId: GraphNodeId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-list-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-list-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-list-" + name)
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private int64ListType = listOf int64Type

    let private createFixture (): ListFixture =
        // Lexical ID order deliberately differs from semantic list order.
        let firstItemNodeId = nodeId "z-first-item"
        let secondItemNodeId = nodeId "a-second-item"
        let firstListNodeId = nodeId "first-list"
        let thirdItemNodeId = nodeId "third-item"
        let appendedListNodeId = nodeId "appended-list"
        let fourthItemNodeId = nodeId "fourth-item"
        let fifthItemNodeId = nodeId "fifth-item"
        let secondListNodeId = nodeId "second-list"
        let concatenatedListNodeId = nodeId "concatenated-list"
        let pipelineLengthNodeId = nodeId "pipeline-length"

        let pipeline: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "pipeline-length"
              TypeParameters = []
              DisplayName = "pipeline_length"
              Parameters = []
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = firstItemNodeId; Kind = SemanticNodeKind.Int64Literal 1L }
                      { Id = secondItemNodeId; Kind = SemanticNodeKind.Int64Literal 2L }
                      { Id = firstListNodeId
                        Kind = SemanticNodeKind.ListConstruct(int64Type, [ firstItemNodeId; secondItemNodeId ]) }
                      { Id = thirdItemNodeId; Kind = SemanticNodeKind.Int64Literal 3L }
                      { Id = appendedListNodeId
                        Kind = SemanticNodeKind.ListAppendItem(firstListNodeId, thirdItemNodeId) }
                      { Id = fourthItemNodeId; Kind = SemanticNodeKind.Int64Literal 4L }
                      { Id = fifthItemNodeId; Kind = SemanticNodeKind.Int64Literal 5L }
                      { Id = secondListNodeId
                        Kind = SemanticNodeKind.ListConstruct(int64Type, [ fourthItemNodeId; fifthItemNodeId ]) }
                      { Id = concatenatedListNodeId
                        Kind = SemanticNodeKind.ListConcat(appendedListNodeId, secondListNodeId) }
                      { Id = pipelineLengthNodeId
                        Kind = SemanticNodeKind.ListLength concatenatedListNodeId } ]
                  ResultNodeId = pipelineLengthNodeId } }

        let emptyListNodeId = nodeId "empty-list"
        let emptyLengthNodeId = nodeId "empty-length"
        let emptyLength: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "empty-length"
              TypeParameters = []
              DisplayName = "empty_length"
              Parameters = []
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = emptyListNodeId; Kind = SemanticNodeKind.ListEmpty int64Type }
                      { Id = emptyLengthNodeId; Kind = SemanticNodeKind.ListLength emptyListNodeId } ]
                  ResultNodeId = emptyLengthNodeId } }

        // Explicit List<T> signature ensures persistence codecs exercise List TypeRef too.
        let identityParameterId = parameterId "identity-input"
        let identityNodeId = nodeId "identity-input"
        let identity: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "identity"
              TypeParameters = []
              DisplayName = "list_identity"
              Parameters =
                [ { Id = identityParameterId
                    DisplayName = "input"
                    Type = int64ListType } ]
              ResultType = int64ListType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = identityNodeId
                        Kind = SemanticNodeKind.ParameterReference identityParameterId } ]
                  ResultNodeId = identityNodeId } }

        { Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-list-basic"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ pipeline; emptyLength; identity ]
              Obligations = [] }
          PipelineFunctionId = pipeline.Id
          EmptyLengthFunctionId = emptyLength.Id
          FirstItemNodeId = firstItemNodeId
          SecondItemNodeId = secondItemNodeId
          FirstListNodeId = firstListNodeId
          ThirdItemNodeId = thirdItemNodeId
          AppendedListNodeId = appendedListNodeId
          FourthItemNodeId = fourthItemNodeId
          FifthItemNodeId = fifthItemNodeId
          SecondListNodeId = secondListNodeId
          ConcatenatedListNodeId = concatenatedListNodeId
          PipelineLengthNodeId = pipelineLengthNodeId
          EmptyListNodeId = emptyListNodeId }

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private requireIssue code issues =
        if not (issues |> List.exists (fun issue -> issue.Code = code)) then
            failwithf "Expected validation issue %s, got %A" code issues

    let private requireMatch label predicate items =
        if not (items |> List.exists predicate) then
            failwithf "Expected %s, got %A" label items

    let private pipelineFunction (fixture: ListFixture) (snapshot: Snapshot) =
        snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = fixture.PipelineFunctionId)

    let private replacePipelineNodeKind
        (fixture: ListFixture)
        (targetNodeId: GraphNodeId)
        (kind: SemanticNodeKind)
        (snapshot: Snapshot)
        : Snapshot =
        let functions =
            snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.PipelineFunctionId then functionEntity
                else
                    let nodes =
                        functionEntity.Body.Nodes
                        |> List.map (fun node -> if node.Id = targetNodeId then { node with Kind = kind } else node)
                    { functionEntity with Body = { functionEntity.Body with Nodes = nodes } })
        { snapshot with Functions = functions }

    let private lower (fixture: ListFixture) =
        let typed = TypedSemanticIr.project fixture.Snapshot |> unwrap "Typed Semantic IR projection"
        let execution = ExecutionIr.lower typed |> unwrap "Execution IR lowering"
        typed, execution

    let private emit (fixture: ListFixture) =
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
        for argument in arguments do startInfo.ArgumentList.Add(argument)
        use childProcess = Process.Start(startInfo)
        if isNull childProcess then failwith "Failed to start dotnet process."
        let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
        let stderrTask = childProcess.StandardError.ReadToEndAsync()
        childProcess.WaitForExit()
        childProcess.ExitCode, stdoutTask.Result, stderrTask.Result

    [<Fact>]
    let ``List nodes expose complete types dependencies and viewer roles`` () =
        let fixture = createFixture ()
        Assert.Empty(SnapshotValidator.validate fixture.Snapshot)

        let focus = SemanticQuery.focusFunction fixture.Snapshot fixture.PipelineFunctionId |> unwrap "pipeline focus"
        let typesByNode = focus.NodeTypes |> List.map (fun item -> item.NodeId, item.Type) |> Map.ofList
        Assert.Equal(Some int64ListType, Map.find fixture.FirstListNodeId typesByNode)
        Assert.Equal(Some int64ListType, Map.find fixture.AppendedListNodeId typesByNode)
        Assert.Equal(Some int64ListType, Map.find fixture.ConcatenatedListNodeId typesByNode)
        Assert.Equal(Some int64Type, Map.find fixture.PipelineLengthNodeId typesByNode)

        let dependencies =
            SemanticQuery.getDependencies fixture.Snapshot fixture.PipelineFunctionId fixture.ConcatenatedListNodeId
            |> unwrap "concat dependencies"
        Assert.Equal<GraphNodeId list>(
            [ fixture.AppendedListNodeId; fixture.SecondListNodeId ] |> List.sortBy StableId.value,
            dependencies
        )

        let viewer = ViewerProjection.focus focus
        let roles = viewer.Graph.Nodes |> List.map (fun node -> node.Role)
        for role in
            [ ViewerNodeRole.Operation "ListConstruct"
              ViewerNodeRole.Operation "ListAppendItem"
              ViewerNodeRole.Operation "ListConcat"
              ViewerNodeRole.Operation "ListLength" ] do
            Assert.Contains(role, roles)

        let emptyViewer =
            SemanticQuery.focusFunction fixture.Snapshot fixture.EmptyLengthFunctionId
            |> unwrap "empty focus"
            |> ViewerProjection.focus
        Assert.Contains(ViewerNodeRole.Operation "ListEmpty", emptyViewer.Graph.Nodes |> List.map (fun node -> node.Role))

        let html = ViewerRendering.focus viewer
        Assert.Contains("List construct", html)
        Assert.Contains("List append item", html)
        Assert.Contains("List concat", html)
        Assert.Contains("List length", html)
        Assert.Contains("<code>List&lt;Int64&gt;</code>", html)

    [<Fact>]
    let ``List validation reports precise shape and item type failures`` () =
        let fixture = createFixture ()

        fixture.Snapshot
        |> replacePipelineNodeKind
            fixture
            fixture.FirstListNodeId
            (SemanticNodeKind.ListConstruct(stringType, [ fixture.FirstItemNodeId; fixture.SecondItemNodeId ]))
        |> SnapshotValidator.validate
        |> requireIssue "ListConstructItemTypeMismatch"

        fixture.Snapshot
        |> replacePipelineNodeKind fixture fixture.ThirdItemNodeId (SemanticNodeKind.StringLiteral "three")
        |> SnapshotValidator.validate
        |> requireIssue "ListAppendItemTypeMismatch"

        let stringRight =
            fixture.Snapshot
            |> replacePipelineNodeKind fixture fixture.FourthItemNodeId (SemanticNodeKind.StringLiteral "four")
            |> replacePipelineNodeKind fixture fixture.FifthItemNodeId (SemanticNodeKind.StringLiteral "five")
            |> replacePipelineNodeKind
                fixture
                fixture.SecondListNodeId
                (SemanticNodeKind.ListConstruct(stringType, [ fixture.FourthItemNodeId; fixture.FifthItemNodeId ]))
        stringRight |> SnapshotValidator.validate |> requireIssue "ListConcatItemTypeMismatch"

        let malformedParameter: SemanticParameter =
            { Id = parameterId "malformed"
              DisplayName = "malformed"
              Type = TypeRef.BuiltinGeneric(BuiltinGenericType.List, []) }
        let malformed =
            { fixture.Snapshot with
                Functions =
                    fixture.Snapshot.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.Id = fixture.PipelineFunctionId then
                            { functionEntity with Parameters = functionEntity.Parameters @ [ malformedParameter ] }
                        else functionEntity) }
        malformed |> SnapshotValidator.validate |> requireIssue "BuiltinGenericArityMismatch"

    [<Fact>]
    let ``JSON round-trips List nodes without reordering items`` () =
        let fixture = createFixture ()
        let json = SnapshotCodec.serializeCanonical fixture.Snapshot |> unwrap "JSON serialization"
        let fromJson = SnapshotCodec.deserialize json |> unwrap "JSON deserialization"

        let firstConstructItems snapshot =
            let node =
                pipelineFunction fixture snapshot
                |> fun functionEntity -> functionEntity.Body.Nodes |> List.find (fun node -> node.Id = fixture.FirstListNodeId)
            match node.Kind with
            | SemanticNodeKind.ListConstruct(itemType, items) -> itemType, items
            | other -> failwithf "Expected ListConstruct, got %A" other

        for restored in [ fromJson ] do
            let itemType, items = firstConstructItems restored
            Assert.Equal(int64Type, itemType)
            Assert.Equal<GraphNodeId list>([ fixture.FirstItemNodeId; fixture.SecondItemNodeId ], items)

        Assert.Contains("\"schemaVersion\":9", json)
        Assert.Contains("\"generic\":\"list\"", json)

    [<Fact>]
    let ``Typed and Execution IR preserve all basic List roles and item order`` () =
        let fixture = createFixture ()
        let typed, execution = lower fixture
        let typedKinds = typed.Functions |> List.collect (fun f -> f.Body.Nodes) |> List.map (fun node -> node.Kind)
        for label, predicate in
            [ "Typed ListEmpty", (function TypedSemanticNodeKind.ListEmpty -> true | _ -> false)
              "Typed ListConstruct", (function TypedSemanticNodeKind.ListConstruct _ -> true | _ -> false)
              "Typed ListLength", (function TypedSemanticNodeKind.ListLength _ -> true | _ -> false)
              "Typed ListAppendItem", (function TypedSemanticNodeKind.ListAppendItem _ -> true | _ -> false)
              "Typed ListConcat", (function TypedSemanticNodeKind.ListConcat _ -> true | _ -> false) ] do
            requireMatch label predicate typedKinds

        let typedPipeline = typed.Functions |> List.find (fun f -> f.Id = fixture.PipelineFunctionId)
        match typedPipeline.Body.Nodes |> List.find (fun node -> node.OriginNodeId = fixture.FirstListNodeId) |> fun node -> node.Kind with
        | TypedSemanticNodeKind.ListConstruct items ->
            Assert.Equal<GraphNodeId list>([ fixture.FirstItemNodeId; fixture.SecondItemNodeId ], items)
        | other -> failwithf "Expected typed ListConstruct, got %A" other

        let instructions =
            execution.Functions |> List.collect (fun f -> f.Blocks) |> List.collect (fun block -> block.Instructions)
        let kinds = instructions |> List.map (fun instruction -> instruction.Kind)
        for label, predicate in
            [ "Execution ListEmpty", (function ExecutionInstructionKind.ListEmpty -> true | _ -> false)
              "Execution ListConstruct", (function ExecutionInstructionKind.ListConstruct _ -> true | _ -> false)
              "Execution ListLength", (function ExecutionInstructionKind.ListLength _ -> true | _ -> false)
              "Execution ListAppendItem", (function ExecutionInstructionKind.ListAppendItem _ -> true | _ -> false)
              "Execution ListConcat", (function ExecutionInstructionKind.ListConcat _ -> true | _ -> false) ] do
            requireMatch label predicate kinds

        let valueIdFor originNodeId =
            instructions |> List.find (fun instruction -> instruction.OriginNodeId = originNodeId) |> fun instruction -> instruction.Result.Id
        match instructions |> List.find (fun instruction -> instruction.OriginNodeId = fixture.FirstListNodeId) |> fun instruction -> instruction.Kind with
        | ExecutionInstructionKind.ListConstruct values ->
            Assert.Equal<ExecutionValueId list>(
                [ valueIdFor fixture.FirstItemNodeId; valueIdFor fixture.SecondItemNodeId ],
                values
            )
        | other -> failwithf "Expected execution ListConstruct, got %A" other

        let typedJson = TypedSemanticIrSerializer.serialize typed
        let executionJson = ExecutionIrSerializer.serialize execution
        Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", typedJson)
        Assert.Contains("\"kind\":\"list\"", typedJson)
        Assert.Contains("\"listConstruct\"", typedJson)
        Assert.Contains($"\"schemaVersion\":{ExecutionIrSerializer.schemaVersion}", executionJson)
        Assert.Contains("\"listConstruct\"", executionJson)

    [<Fact>]
    let ``C# source uses an immutable-style dedicated NyList runtime`` () =
        let artifact = createFixture () |> emit
        Assert.Contains("public sealed class NyList<T>", artifact.Source)
        Assert.Contains("private readonly object[]? _root;", artifact.Source)
        Assert.Contains("private readonly Store _tail;", artifact.Source)
        Assert.Contains("private readonly int _count;", artifact.Source)
        Assert.Contains("public long Length => Count;", artifact.Source)
        Assert.Contains("AppendItem(T item)", artifact.Source)
        Assert.Contains("Concat(NyList<T> other)", artifact.Source)
        Assert.Contains("Array.Copy", artifact.Source)
        Assert.DoesNotContain("System.Collections.Generic.List", artifact.Source)

    [<Fact>]
    let ``generated C# executes basic List construction append concat and length`` () =
        let fixture = createFixture ()
        let artifact = emit fixture
        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-list-basic-" + Guid.NewGuid().ToString("N"))
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
                String.concat Environment.NewLine
                    [ "using System;"
                      "using Nyrwvy.Generated;"
                      ""
                      call fixture.PipelineFunctionId
                      call fixture.EmptyLengthFunctionId ]
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), projectFile)
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), harness)

            let buildExit, buildOut, buildErr =
                runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            if buildExit <> 0 then
                failwithf "Generated List C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s" buildOut buildErr artifact.Source
            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated List C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr
            Assert.Equal("5\n0", runOut.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``ListConstruct item order is semantic fingerprint state`` () =
        let fixture = createFixture ()
        let original = SemanticFingerprint.compute fixture.Snapshot |> unwrap "original semantic fingerprint"
        let reordered =
            fixture.Snapshot
            |> replacePipelineNodeKind
                fixture
                fixture.FirstListNodeId
                (SemanticNodeKind.ListConstruct(int64Type, [ fixture.SecondItemNodeId; fixture.FirstItemNodeId ]))
        Assert.Empty(SnapshotValidator.validate reordered)
        let changed = SemanticFingerprint.compute reordered |> unwrap "reordered semantic fingerprint"
        Assert.NotEqual(original, changed)
