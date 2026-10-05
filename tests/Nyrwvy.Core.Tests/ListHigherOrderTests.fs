namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Xunit

module ListHigherOrderTests =
    type private HigherOrderFixture =
        { Snapshot: Snapshot
          ShowcaseFunctionId: FunctionId
          PositiveFunctionId: FunctionId
          BoolIdentityFunctionId: FunctionId
          AlternateMapperFunctionId: FunctionId
          TwoParameterFunctionId: FunctionId
          WrongParameterFunctionId: FunctionId
          BadResultFunctionId: FunctionId
          EffectfulFunctionId: FunctionId
          SourceListNodeId: GraphNodeId
          MapNodeId: GraphNodeId
          FilterNodeId: GraphNodeId
          AnyNodeId: GraphNodeId
          AllNodeId: GraphNodeId
          MappedAnyNodeId: GraphNodeId
          FilteredLengthFunctionId: FunctionId
          AnyPositiveFunctionId: FunctionId
          AllPositiveFunctionId: FunctionId
          MappedAnyFunctionId: FunctionId
          EmptyAnyFunctionId: FunctionId
          EmptyAllFunctionId: FunctionId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-list-ho-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-list-ho-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-list-ho-" + name)
    let private effectId = StableId.createForTesting<EffectTag> "effect-list-ho-test"
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private int64ListType = listOf int64Type
    let private boolListType = listOf boolType

    let private unaryFunction
        name
        parameterType
        resultType
        requiredEffects
        (buildBody: GraphNodeId -> SemanticNode list * GraphNodeId)
        : SemanticFunction =
        let parameter: SemanticParameter =
            { Id = parameterId (name + "-value")
              DisplayName = "value"
              Type = parameterType }
        let parameterNodeId = nodeId (name + "-value")
        let parameterNode =
            { Id = parameterNodeId
              Kind = SemanticNodeKind.ParameterReference parameter.Id }
        let bodyNodes, resultNodeId = buildBody parameterNodeId
        { Id = functionId name
          DisplayName = name
          Parameters = [ parameter ]
          ResultType = resultType
          RequiredEffects = requiredEffects
          Body =
            { Nodes = parameterNode :: bodyNodes
              ResultNodeId = resultNodeId } }

    let private positiveFunction () =
        unaryFunction "positive" int64Type boolType [] (fun valueNodeId ->
            let zeroId = nodeId "positive-zero"
            let resultId = nodeId "positive-result"
            [ { Id = zeroId; Kind = SemanticNodeKind.Int64Literal 0L }
              { Id = resultId; Kind = SemanticNodeKind.Int64GreaterThan(valueNodeId, zeroId) } ],
            resultId)

    let private boolIdentityFunction () =
        unaryFunction "bool-identity" boolType boolType [] (fun valueNodeId -> [], valueNodeId)

    let private alternateMapperFunction () =
        unaryFunction "always-true" int64Type boolType [] (fun _ ->
            let resultId = nodeId "always-true-result"
            [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

    let private wrongParameterFunction () =
        unaryFunction "wrong-parameter" stringType boolType [] (fun _ ->
            let resultId = nodeId "wrong-parameter-result"
            [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

    let private badResultFunction () =
        unaryFunction "bad-result" int64Type int64Type [] (fun valueNodeId -> [], valueNodeId)

    let private effectfulFunction () =
        unaryFunction "effectful" int64Type boolType [ effectId ] (fun _ ->
            let resultId = nodeId "effectful-result"
            [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

    let private twoParameterFunction () =
        let first: SemanticParameter =
            { Id = parameterId "two-first"
              DisplayName = "first"
              Type = int64Type }
        let second: SemanticParameter =
            { Id = parameterId "two-second"
              DisplayName = "second"
              Type = int64Type }
        let resultId = nodeId "two-result"
        { Id = functionId "two-parameter"
          DisplayName = "two_parameter"
          Parameters = [ first; second ]
          ResultType = boolType
          RequiredEffects = []
          Body =
            { Nodes = [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ]
              ResultNodeId = resultId } }

    let private sourceNodes prefix =
        let firstId = nodeId (prefix + "-first")
        let secondId = nodeId (prefix + "-second")
        let thirdId = nodeId (prefix + "-third")
        let listId = nodeId (prefix + "-list")
        [ { Id = firstId; Kind = SemanticNodeKind.Int64Literal 1L }
          { Id = secondId; Kind = SemanticNodeKind.Int64Literal -2L }
          { Id = thirdId; Kind = SemanticNodeKind.Int64Literal 3L }
          { Id = listId
            Kind = SemanticNodeKind.ListConstruct(int64Type, [ firstId; secondId; thirdId ]) } ],
        listId

    let private createFixture (): HigherOrderFixture =
        let positive = positiveFunction ()
        let boolIdentity = boolIdentityFunction ()
        let alternateMapper = alternateMapperFunction ()
        let twoParameter = twoParameterFunction ()
        let wrongParameter = wrongParameterFunction ()
        let badResult = badResultFunction ()
        let effectful = effectfulFunction ()

        let showcaseSourceNodes, showcaseSourceId = sourceNodes "showcase"
        let mapNodeId = nodeId "showcase-map"
        let mappedAnyNodeId = nodeId "showcase-mapped-any"
        let mappedAllNodeId = nodeId "showcase-mapped-all"
        let filterNodeId = nodeId "showcase-filter"
        let anyNodeId = nodeId "showcase-any"
        let allNodeId = nodeId "showcase-all"
        let showcase: SemanticFunction =
            { Id = functionId "showcase"
              DisplayName = "higher_order_showcase"
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    showcaseSourceNodes
                    @ [ { Id = mapNodeId
                          Kind = SemanticNodeKind.ListMap(showcaseSourceId, positive.Id, []) }
                        { Id = mappedAnyNodeId
                          Kind = SemanticNodeKind.ListAny(mapNodeId, boolIdentity.Id, []) }
                        { Id = mappedAllNodeId
                          Kind = SemanticNodeKind.ListAll(mapNodeId, boolIdentity.Id, []) }
                        { Id = filterNodeId
                          Kind = SemanticNodeKind.ListFilter(showcaseSourceId, positive.Id, []) }
                        { Id = anyNodeId
                          Kind = SemanticNodeKind.ListAny(showcaseSourceId, positive.Id, []) }
                        { Id = allNodeId
                          Kind = SemanticNodeKind.ListAll(showcaseSourceId, positive.Id, []) } ]
                  ResultNodeId = mappedAllNodeId } }

        let filteredSourceNodes, filteredSourceId = sourceNodes "filtered-length"
        let filteredId = nodeId "filtered-length-filter"
        let filteredLengthId = nodeId "filtered-length-result"
        let filteredLength: SemanticFunction =
            { Id = functionId "filtered-length"
              DisplayName = "filtered_length"
              Parameters = []
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    filteredSourceNodes
                    @ [ { Id = filteredId; Kind = SemanticNodeKind.ListFilter(filteredSourceId, positive.Id, []) }
                        { Id = filteredLengthId; Kind = SemanticNodeKind.ListLength filteredId } ]
                  ResultNodeId = filteredLengthId } }

        let predicateFunction name constructor =
            let nodes, sourceId = sourceNodes name
            let resultId = nodeId (name + "-result")
            { Id = functionId name
              DisplayName = name
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes = nodes @ [ { Id = resultId; Kind = constructor sourceId positive.Id } ]
                  ResultNodeId = resultId } }

        let anyPositive = predicateFunction "any-positive" (fun source callback -> SemanticNodeKind.ListAny(source, callback, []))
        let allPositive = predicateFunction "all-positive" (fun source callback -> SemanticNodeKind.ListAll(source, callback, []))

        let mappedAnySourceNodes, mappedAnySourceId = sourceNodes "mapped-any"
        let mappedAnyListId = nodeId "mapped-any-map"
        let mappedAnyResultId = nodeId "mapped-any-result"
        let mappedAny: SemanticFunction =
            { Id = functionId "mapped-any"
              DisplayName = "mapped_any"
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    mappedAnySourceNodes
                    @ [ { Id = mappedAnyListId; Kind = SemanticNodeKind.ListMap(mappedAnySourceId, positive.Id, []) }
                        { Id = mappedAnyResultId; Kind = SemanticNodeKind.ListAny(mappedAnyListId, boolIdentity.Id, []) } ]
                  ResultNodeId = mappedAnyResultId } }

        let emptyPredicateFunction name constructor =
            let emptyId = nodeId (name + "-empty")
            let resultId = nodeId (name + "-result")
            { Id = functionId name
              DisplayName = name
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = emptyId; Kind = SemanticNodeKind.ListEmpty int64Type }
                      { Id = resultId; Kind = constructor emptyId positive.Id } ]
                  ResultNodeId = resultId } }

        let emptyAny = emptyPredicateFunction "empty-any" (fun source callback -> SemanticNodeKind.ListAny(source, callback, []))
        let emptyAll = emptyPredicateFunction "empty-all" (fun source callback -> SemanticNodeKind.ListAll(source, callback, []))

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-list-higher-order"
              ParentId = None
              Types = []
              Effects = [ { Id = effectId; DisplayName = "test_effect" } ]
              Capabilities = []
              Functions =
                [ positive
                  boolIdentity
                  alternateMapper
                  twoParameter
                  wrongParameter
                  badResult
                  effectful
                  showcase
                  filteredLength
                  anyPositive
                  allPositive
                  mappedAny
                  emptyAny
                  emptyAll ]
              Obligations = [] }

        { Snapshot = snapshot
          ShowcaseFunctionId = showcase.Id
          PositiveFunctionId = positive.Id
          BoolIdentityFunctionId = boolIdentity.Id
          AlternateMapperFunctionId = alternateMapper.Id
          TwoParameterFunctionId = twoParameter.Id
          WrongParameterFunctionId = wrongParameter.Id
          BadResultFunctionId = badResult.Id
          EffectfulFunctionId = effectful.Id
          SourceListNodeId = showcaseSourceId
          MapNodeId = mapNodeId
          FilterNodeId = filterNodeId
          AnyNodeId = anyNodeId
          AllNodeId = allNodeId
          MappedAnyNodeId = mappedAnyNodeId
          FilteredLengthFunctionId = filteredLength.Id
          AnyPositiveFunctionId = anyPositive.Id
          AllPositiveFunctionId = allPositive.Id
          MappedAnyFunctionId = mappedAny.Id
          EmptyAnyFunctionId = emptyAny.Id
          EmptyAllFunctionId = emptyAll.Id }

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private requireIssue code issues =
        if not (issues |> List.exists (fun issue -> issue.Code = code)) then
            failwithf "Expected validation issue %s, got %A" code issues

    let private replaceShowcaseNodeKind fixture targetNodeId kind snapshot =
        let functions =
            snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.ShowcaseFunctionId then functionEntity
                else
                    let nodes =
                        functionEntity.Body.Nodes
                        |> List.map (fun node -> if node.Id = targetNodeId then { node with Kind = kind } else node)
                    { functionEntity with Body = { functionEntity.Body with Nodes = nodes } })
        { snapshot with Functions = functions }

    let private lower snapshot =
        let typed = TypedSemanticIr.project snapshot |> unwrap "Typed Semantic IR projection"
        let execution = ExecutionIr.lower typed |> unwrap "Execution IR lowering"
        typed, execution

    let private emit snapshot =
        let _, execution = lower snapshot
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

    let private showcaseNode fixture snapshot nodeIdToFind =
        snapshot.Functions
        |> List.find (fun functionEntity -> functionEntity.Id = fixture.ShowcaseFunctionId)
        |> fun functionEntity -> functionEntity.Body.Nodes |> List.find (fun node -> node.Id = nodeIdToFind)

    [<Fact>]
    let ``higher-order List nodes expose types callback callees and viewer relations`` () =
        let fixture = createFixture ()
        Assert.Empty(SnapshotValidator.validate fixture.Snapshot)

        let focus = SemanticQuery.focusFunction fixture.Snapshot fixture.ShowcaseFunctionId |> unwrap "showcase focus"
        let types = focus.NodeTypes |> List.map (fun item -> item.NodeId, item.Type) |> Map.ofList
        Assert.Equal(Some boolListType, Map.find fixture.MapNodeId types)
        Assert.Equal(Some int64ListType, Map.find fixture.FilterNodeId types)
        Assert.Equal(Some boolType, Map.find fixture.AnyNodeId types)
        Assert.Equal(Some boolType, Map.find fixture.AllNodeId types)

        let callees = focus.Callees |> List.map (fun site -> site.CalleeFunctionId) |> Set.ofList
        Assert.Contains(fixture.PositiveFunctionId, callees)
        Assert.Contains(fixture.BoolIdentityFunctionId, callees)
        let related = focus.RelatedFunctionSignatures |> List.map (fun signature -> signature.FunctionId) |> Set.ofList
        Assert.Contains(fixture.PositiveFunctionId, related)
        Assert.Contains(fixture.BoolIdentityFunctionId, related)

        let viewer = ViewerProjection.focus focus
        let roles = viewer.Graph.Nodes |> List.map (fun node -> node.Role)
        for role in [ ViewerNodeRole.ListMap; ViewerNodeRole.ListFilter; ViewerNodeRole.ListAny; ViewerNodeRole.ListAll ] do
            Assert.Contains(role, roles)

        let mapViewer = viewer.Graph.Nodes |> List.find (fun node -> node.Id = fixture.MapNodeId)
        Assert.Contains(
            mapViewer.Relations,
            fun relation ->
                relation.Kind = ViewerRelationKind.CallsFunction
                && relation.Target = ViewerTarget.Function fixture.PositiveFunctionId
        )
        let html = ViewerHtml.renderFocusDocument viewer
        Assert.Contains("List map", html)
        Assert.Contains("List filter", html)
        Assert.Contains("List any", html)
        Assert.Contains("List all", html)

    [<Fact>]
    let ``higher-order List validation rejects invalid source and callback contracts`` () =
        let fixture = createFixture ()

        fixture.Snapshot
        |> replaceShowcaseNodeKind fixture fixture.SourceListNodeId (SemanticNodeKind.Int64Literal 9L)
        |> SnapshotValidator.validate
        |> requireIssue "HigherOrderSourceTypeMismatch"

        let unknown = functionId "unknown-callback"
        fixture.Snapshot
        |> replaceShowcaseNodeKind fixture fixture.MapNodeId (SemanticNodeKind.ListMap(fixture.SourceListNodeId, unknown, []))
        |> SnapshotValidator.validate
        |> requireIssue "UnknownCallbackFunction"

        fixture.Snapshot
        |> replaceShowcaseNodeKind fixture fixture.MapNodeId (SemanticNodeKind.ListMap(fixture.SourceListNodeId, fixture.TwoParameterFunctionId, []))
        |> SnapshotValidator.validate
        |> requireIssue "CallbackArityMismatch"

        fixture.Snapshot
        |> replaceShowcaseNodeKind fixture fixture.MapNodeId (SemanticNodeKind.ListMap(fixture.SourceListNodeId, fixture.WrongParameterFunctionId, []))
        |> SnapshotValidator.validate
        |> requireIssue "CallbackParameterTypeMismatch"

        fixture.Snapshot
        |> replaceShowcaseNodeKind fixture fixture.MapNodeId (SemanticNodeKind.ListMap(fixture.SourceListNodeId, fixture.EffectfulFunctionId, []))
        |> SnapshotValidator.validate
        |> requireIssue "CallbackEffectsNotAllowed"

        fixture.Snapshot
        |> replaceShowcaseNodeKind fixture fixture.AnyNodeId (SemanticNodeKind.ListAny(fixture.SourceListNodeId, fixture.BadResultFunctionId, []))
        |> SnapshotValidator.validate
        |> requireIssue "PredicateResultTypeMismatch"

    [<Fact>]
    let ``JSON and Nyason preserve higher-order List callback identity`` () =
        let fixture = createFixture ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap "JSON serialization"
        let fromJson = SnapshotCodec.deserialize json |> unwrap "JSON deserialization"
        let nyason = NyasonCodec.serialize fixture.Snapshot |> unwrap "Nyason serialization"
        let fromNyason = NyasonCodec.deserialize nyason |> unwrap "Nyason deserialization"

        let assertNodes snapshot =
            match (showcaseNode fixture snapshot fixture.MapNodeId).Kind with
            | SemanticNodeKind.ListMap(source, callback, _) ->
                Assert.Equal(fixture.SourceListNodeId, source)
                Assert.Equal(fixture.PositiveFunctionId, callback)
            | other -> failwithf "Expected ListMap, got %A" other
            match (showcaseNode fixture snapshot fixture.FilterNodeId).Kind with
            | SemanticNodeKind.ListFilter(_, callback, _) -> Assert.Equal(fixture.PositiveFunctionId, callback)
            | other -> failwithf "Expected ListFilter, got %A" other
            match (showcaseNode fixture snapshot fixture.AnyNodeId).Kind with
            | SemanticNodeKind.ListAny(_, callback, _) -> Assert.Equal(fixture.PositiveFunctionId, callback)
            | other -> failwithf "Expected ListAny, got %A" other
            match (showcaseNode fixture snapshot fixture.AllNodeId).Kind with
            | SemanticNodeKind.ListAll(_, callback, _) -> Assert.Equal(fixture.PositiveFunctionId, callback)
            | other -> failwithf "Expected ListAll, got %A" other

        assertNodes fromJson
        assertNodes fromNyason
        Assert.Contains("\"schemaVersion\":9", json)
        Assert.Contains("\"mapperFunctionId\"", json)
        Assert.Contains("\"predicateFunctionId\"", json)
        Assert.Contains("nayamnyam.snapshot.v5", nyason)
        Assert.Contains("list-map {", nyason)
        Assert.Contains("list-filter {", nyason)
        Assert.Contains("list-any {", nyason)
        Assert.Contains("list-all {", nyason)

    [<Fact>]
    let ``Typed and Execution IR preserve higher-order List roles and callback IDs`` () =
        let fixture = createFixture ()
        let typed, execution = lower fixture.Snapshot
        let typedShowcase = typed.Functions |> List.find (fun functionEntity -> functionEntity.Id = fixture.ShowcaseFunctionId)
        match typedShowcase.Body.Nodes |> List.find (fun node -> node.OriginNodeId = fixture.MapNodeId) |> fun node -> node.Kind with
        | TypedSemanticNodeKind.ListMap(source, callback, _) ->
            Assert.Equal(fixture.SourceListNodeId, source)
            Assert.Equal(fixture.PositiveFunctionId, callback)
        | other -> failwithf "Expected typed ListMap, got %A" other

        let instructions =
            execution.Functions |> List.collect (fun f -> f.Blocks) |> List.collect (fun block -> block.Instructions)
        let requireExecution origin predicate =
            let instruction = instructions |> List.find (fun item -> item.OriginNodeId = origin)
            if not (predicate instruction.Kind) then failwithf "Unexpected execution instruction: %A" instruction.Kind
        requireExecution fixture.MapNodeId (function ExecutionInstructionKind.ListMap(_, callback, _) -> callback = fixture.PositiveFunctionId | _ -> false)
        requireExecution fixture.FilterNodeId (function ExecutionInstructionKind.ListFilter(_, callback, _) -> callback = fixture.PositiveFunctionId | _ -> false)
        requireExecution fixture.AnyNodeId (function ExecutionInstructionKind.ListAny(_, callback, _) -> callback = fixture.PositiveFunctionId | _ -> false)
        requireExecution fixture.AllNodeId (function ExecutionInstructionKind.ListAll(_, callback, _) -> callback = fixture.PositiveFunctionId | _ -> false)

        let typedJson = TypedSemanticIrSerializer.serialize typed
        let executionJson = ExecutionIrSerializer.serialize execution
        Assert.Contains("\"schemaVersion\":7", typedJson)
        Assert.Contains("\"listMap\"", typedJson)
        Assert.Contains(StableId.value fixture.PositiveFunctionId, typedJson)
        Assert.Contains("\"schemaVersion\":6", executionJson)
        Assert.Contains("\"listFilter\"", executionJson)

    [<Fact>]
    let ``C# source emits dedicated pure higher-order NyList operations`` () =
        let fixture = createFixture ()
        let artifact = emit fixture.Snapshot
        Assert.Contains("public NyList<U> Map<U>(Func<T, U> mapper)", artifact.Source)
        Assert.Contains("public NyList<T> Filter(Func<T, bool> predicate)", artifact.Source)
        Assert.Contains("public bool Any(Func<T, bool> predicate)", artifact.Source)
        Assert.Contains("public bool All(Func<T, bool> predicate)", artifact.Source)
        Assert.Contains("return false;", artifact.Source)
        Assert.Contains("return true;", artifact.Source)
        Assert.Contains("NyFunctions." + CSharpBackendNames.functionName fixture.PositiveFunctionId, artifact.Source)
        Assert.Contains("NyFunctions." + CSharpBackendNames.functionName fixture.BoolIdentityFunctionId, artifact.Source)

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated C# executes pure higher-order List and empty Any All semantics`` () =
        let fixture = createFixture ()
        let artifact = emit fixture.Snapshot
        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-list-ho-" + Guid.NewGuid().ToString("N"))
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
                      call fixture.FilteredLengthFunctionId
                      call fixture.AnyPositiveFunctionId
                      call fixture.AllPositiveFunctionId
                      call fixture.MappedAnyFunctionId
                      call fixture.ShowcaseFunctionId
                      call fixture.EmptyAnyFunctionId
                      call fixture.EmptyAllFunctionId ]
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), projectFile)
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), harness)

            let buildExit, buildOut, buildErr =
                runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            if buildExit <> 0 then
                failwithf "Generated higher-order List C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s" buildOut buildErr artifact.Source
            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated higher-order List C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr
            let expected = String.concat "\n" [ "2"; "True"; "False"; "True"; "False"; "False"; "True" ]
            Assert.Equal(expected, runOut.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``higher-order List callback identity participates in semantic fingerprint`` () =
        let fixture = createFixture ()
        let original = SemanticFingerprint.compute fixture.Snapshot |> unwrap "original semantic fingerprint"
        let changed =
            fixture.Snapshot
            |> replaceShowcaseNodeKind
                fixture
                fixture.MapNodeId
                (SemanticNodeKind.ListMap(fixture.SourceListNodeId, fixture.AlternateMapperFunctionId, []))
        Assert.Empty(SnapshotValidator.validate changed)
        let changedFingerprint = SemanticFingerprint.compute changed |> unwrap "changed semantic fingerprint"
        Assert.NotEqual(original, changedFingerprint)
