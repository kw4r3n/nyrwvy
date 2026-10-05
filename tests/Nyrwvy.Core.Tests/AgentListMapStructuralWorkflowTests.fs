namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

module AgentListMapStructuralWorkflowTests =
    type private Fixture =
        { Snapshot: Snapshot
          TargetFunctionId: FunctionId
          SourceParameterId: ParameterId
          NonListParameterId: ParameterId
          OldResultNodeId: GraphNodeId
          MapperFunctionId: FunctionId
          WrongInputMapperFunctionId: FunctionId
          WrongResultMapperFunctionId: FunctionId
          EffectfulMapperFunctionId: FunctionId
          TwoParameterMapperFunctionId: FunctionId
          ForeignParameterId: ParameterId
          CallerFunctionId: FunctionId
          UnrelatedFunctionId: FunctionId }

    let private functionId name =
        StableId.createForTesting<FunctionTag> ("function-listmap-structural-" + name)

    let private parameterId name =
        StableId.createForTesting<ParameterTag> ("parameter-listmap-structural-" + name)

    let private nodeId name =
        StableId.createForTesting<GraphNodeTag> ("node-listmap-structural-" + name)

    let private snapshotId name =
        StableId.createForTesting<SnapshotTag> ("snapshot-listmap-structural-" + name)

    let private effectId =
        StableId.createForTesting<EffectTag> "effect-listmap-structural-test"

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
        (bodyBuilder: GraphNodeId -> SemanticNode list * GraphNodeId)
        : SemanticFunction =
        let parameter: SemanticParameter =
            { Id = parameterId (name + "-value")
              DisplayName = "value"
              Type = parameterType }
        let parameterNodeId = nodeId (name + "-parameter")
        let parameterNode =
            { Id = parameterNodeId
              Kind = SemanticNodeKind.ParameterReference parameter.Id }
        let extraNodes, resultNodeId = bodyBuilder parameterNodeId
        { Id = functionId name
          DisplayName = name
          Parameters = [ parameter ]
          ResultType = resultType
          RequiredEffects = requiredEffects
          Body =
            { Nodes = parameterNode :: extraNodes
              ResultNodeId = resultNodeId } }

    let private createFixture (): Fixture =
        let mapper =
            unaryFunction "positive" int64Type boolType [] (fun valueNodeId ->
                let zeroId = nodeId "positive-zero"
                let resultId = nodeId "positive-result"
                [ { Id = zeroId; Kind = SemanticNodeKind.Int64Literal 0L }
                  { Id = resultId; Kind = SemanticNodeKind.Int64GreaterThan(valueNodeId, zeroId) } ],
                resultId)

        let wrongInputMapper =
            unaryFunction "wrong-input" stringType boolType [] (fun _ ->
                let resultId = nodeId "wrong-input-result"
                [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

        let wrongResultMapper =
            unaryFunction "wrong-result" int64Type int64Type [] (fun valueNodeId -> [], valueNodeId)

        let effectfulMapper =
            unaryFunction "effectful" int64Type boolType [ effectId ] (fun _ ->
                let resultId = nodeId "effectful-result"
                [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

        let firstParameter: SemanticParameter =
            { Id = parameterId "two-first"
              DisplayName = "first"
              Type = int64Type }
        let secondParameter: SemanticParameter =
            { Id = parameterId "two-second"
              DisplayName = "second"
              Type = int64Type }
        let twoParameterResultId = nodeId "two-result"
        let twoParameterMapper: SemanticFunction =
            { Id = functionId "two-parameter"
              DisplayName = "two_parameter"
              Parameters = [ firstParameter; secondParameter ]
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = twoParameterResultId; Kind = SemanticNodeKind.BoolLiteral true } ]
                  ResultNodeId = twoParameterResultId } }

        let boolIdentity =
            unaryFunction "bool-identity" boolType boolType [] (fun valueNodeId -> [], valueNodeId)

        let sourceParameter: SemanticParameter =
            { Id = parameterId "target-values"
              DisplayName = "values"
              Type = int64ListType }
        let nonListParameter: SemanticParameter =
            { Id = parameterId "target-count"
              DisplayName = "count"
              Type = int64Type }
        let oldResultId = nodeId "target-old-result"
        let target: SemanticFunction =
            { Id = functionId "target"
              DisplayName = "map_values"
              Parameters = [ sourceParameter; nonListParameter ]
              ResultType = boolListType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = oldResultId; Kind = SemanticNodeKind.ListEmpty boolType } ]
                  ResultNodeId = oldResultId } }

        let firstId = nodeId "caller-first"
        let secondId = nodeId "caller-second"
        let thirdId = nodeId "caller-third"
        let listId = nodeId "caller-list"
        let countId = nodeId "caller-count"
        let callId = nodeId "caller-call"
        let anyId = nodeId "caller-any"
        let caller: SemanticFunction =
            { Id = functionId "caller"
              DisplayName = "caller"
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = firstId; Kind = SemanticNodeKind.Int64Literal 1L }
                      { Id = secondId; Kind = SemanticNodeKind.Int64Literal -2L }
                      { Id = thirdId; Kind = SemanticNodeKind.Int64Literal 3L }
                      { Id = listId
                        Kind = SemanticNodeKind.ListConstruct(int64Type, [ firstId; secondId; thirdId ]) }
                      { Id = countId; Kind = SemanticNodeKind.Int64Literal 0L }
                      { Id = callId
                        Kind =
                            SemanticNodeKind.DirectCall(
                                target.Id,
                                [ { TargetParameterId = sourceParameter.Id; ValueNodeId = listId }
                                  { TargetParameterId = nonListParameter.Id; ValueNodeId = countId } ]
                            ) }
                      { Id = anyId; Kind = SemanticNodeKind.ListAny(callId, boolIdentity.Id, []) } ]
                  ResultNodeId = anyId } }

        let foreignParameter: SemanticParameter =
            { Id = parameterId "foreign-values"
              DisplayName = "foreign_values"
              Type = int64ListType }
        let foreignResultId = nodeId "foreign-result"
        let foreign: SemanticFunction =
            { Id = functionId "foreign"
              DisplayName = "foreign"
              Parameters = [ foreignParameter ]
              ResultType = int64ListType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = foreignResultId; Kind = SemanticNodeKind.ParameterReference foreignParameter.Id } ]
                  ResultNodeId = foreignResultId } }

        let unrelatedResultId = nodeId "unrelated-result"
        let unrelated: SemanticFunction =
            { Id = functionId "unrelated"
              DisplayName = "unrelated"
              Parameters = []
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = unrelatedResultId; Kind = SemanticNodeKind.Int64Literal 99L } ]
                  ResultNodeId = unrelatedResultId } }

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = [ { Id = effectId; DisplayName = "test_effect" } ]
              Capabilities = []
              Functions =
                [ mapper
                  wrongInputMapper
                  wrongResultMapper
                  effectfulMapper
                  twoParameterMapper
                  boolIdentity
                  target
                  caller
                  foreign
                  unrelated ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        { Snapshot = snapshot
          TargetFunctionId = target.Id
          SourceParameterId = sourceParameter.Id
          NonListParameterId = nonListParameter.Id
          OldResultNodeId = oldResultId
          MapperFunctionId = mapper.Id
          WrongInputMapperFunctionId = wrongInputMapper.Id
          WrongResultMapperFunctionId = wrongResultMapper.Id
          EffectfulMapperFunctionId = effectfulMapper.Id
          TwoParameterMapperFunctionId = twoParameterMapper.Id
          ForeignParameterId = foreignParameter.Id
          CallerFunctionId = caller.Id
          UnrelatedFunctionId = unrelated.Id }

    let private createWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request fixture sourceParameterId mapperFunctionId: AgentReplaceFunctionBodyWithListMap =
        { TargetFunctionId = fixture.TargetFunctionId
          ExpectedBaseSnapshotId = fixture.Snapshot.Id
          ExpectedCurrentResultNodeId = fixture.OldResultNodeId
          SourceParameterId = sourceParameterId
          MapperFunctionId = mapperFunctionId }

    let private simulate workspace structuralRequest =
        match AgentListMapStructuralWorkflow.simulate workspace structuralRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected ListMap structural simulation failure: %A" error

    let private commit workspace proposal =
        match AgentListMapStructuralWorkflow.commit workspace proposal with
        | AgentStructuralCommitResult.Committed(next, publication) -> next, publication
        | AgentStructuralCommitResult.Rejected diagnostics ->
            failwithf "Unexpected ListMap structural commit rejection: %A" diagnostics

    let private assertRejectedCode expected result =
        match result with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private assertUuidV7 id =
        let payload = StableId.value id
        let mutable parsed = Guid.Empty
        Assert.True(Guid.TryParseExact(payload, "D", &parsed), $"Not a GUID: {payload}")
        Assert.Equal('7', payload[14])

    [<Fact>]
    let ``ListMap structural simulation materializes exactly two fresh reviewed nodes`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal = request fixture fixture.SourceParameterId fixture.MapperFunctionId |> simulate workspace
        let review = AgentListMapStructuralChangeProposal.review proposal

        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(fixture.SourceParameterId, review.RequestedChange.SourceParameterId)
        Assert.Equal(fixture.MapperFunctionId, review.RequestedChange.MapperFunctionId)
        Assert.Equal(2, review.ResultingChange.AddedNodes.Length)

        let roles = review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role)
        if roles <> [ "ParameterReference"; "ListMap" ] then
            failwithf "Unexpected ListMap materialization order: %A" roles

        let sourceSummary = review.ResultingChange.AddedNodes[0]
        let mapSummary = review.ResultingChange.AddedNodes[1]
        Assert.Equal(Some fixture.SourceParameterId, sourceSummary.ParameterId)
        Assert.Equal(Some fixture.MapperFunctionId, mapSummary.FunctionId)
        Assert.Equal(Some int64ListType, sourceSummary.Type)
        Assert.Equal(Some boolListType, mapSummary.Type)
        if mapSummary.Dependencies <> [ sourceSummary.NodeId ] then
            failwithf "Unexpected ListMap dependencies: %A" mapSummary.Dependencies
        Assert.Equal(mapSummary.NodeId, review.ResultingChange.NewResultNodeId)

        let oldIds = review.ResultingChange.RemovedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        let newIds = review.ResultingChange.AddedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        Assert.True(Set.intersect oldIds newIds |> Set.isEmpty)
        review.ResultingChange.AddedNodes |> List.iter (fun node -> assertUuidV7 node.NodeId)
        assertUuidV7 (AgentListMapStructuralChangeProposal.transactionId proposal)

    [<Fact>]
    let ``ListMap structural preflight rejects target guards and invalid source parameters`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let valid = request fixture fixture.SourceParameterId fixture.MapperFunctionId

        { valid with ExpectedBaseSnapshotId = snapshotId "wrong-base" }
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralExpectedBaseMismatch"

        { valid with ExpectedCurrentResultNodeId = nodeId "wrong-result" }
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralExpectedResultMismatch"

        { valid with TargetFunctionId = functionId "missing-target" }
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralTargetNotFound"

        { valid with SourceParameterId = fixture.ForeignParameterId }
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListSourceParameterOutOfScope"

        { valid with SourceParameterId = fixture.NonListParameterId }
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListSourceTypeMismatch"

        let unrelated = fixture.Snapshot.Functions |> List.find (fun fn -> fn.Id = fixture.UnrelatedFunctionId)
        { valid with
            TargetFunctionId = unrelated.Id
            ExpectedCurrentResultNodeId = unrelated.Body.ResultNodeId }
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListTargetResultTypeMismatch"

    [<Fact>]
    let ``ListMap structural preflight rejects invalid mapper contracts`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let withMapper mapperId = request fixture fixture.SourceParameterId mapperId

        withMapper (functionId "missing-mapper")
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListMapperNotFound"

        withMapper fixture.TwoParameterMapperFunctionId
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListMapperArityMismatch"

        withMapper fixture.WrongInputMapperFunctionId
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListMapperInputTypeMismatch"

        withMapper fixture.WrongResultMapperFunctionId
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListMapperResultTypeMismatch"

        withMapper fixture.EffectfulMapperFunctionId
        |> AgentListMapStructuralWorkflow.simulate workspace
        |> assertRejectedCode "StructuralListMapperEffectsNotAllowed"

    [<Fact>]
    let ``ListMap commit preserves caller declarations target signature and unrelated semantics`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal = request fixture fixture.SourceParameterId fixture.MapperFunctionId |> simulate workspace
        let committed, publication = commit workspace proposal
        let after = AgentWorkspace.snapshotForStructuralEditing committed
        let before = fixture.Snapshot

        Assert.Equal(AgentListMapStructuralChangeProposal.proposedSnapshotId proposal, publication.PublishedSnapshotId)
        Assert.Equal(publication.Receipt.ProposedSnapshotId, publication.PublishedSnapshotId)

        if before.Types <> after.Types then failwith "ListMap replacement changed semantic types."
        if before.Effects <> after.Effects then failwith "ListMap replacement changed effects."
        if before.Capabilities <> after.Capabilities then failwith "ListMap replacement changed capabilities."
        if before.Obligations <> after.Obligations then failwith "ListMap replacement changed obligations."

        let findFunction id snapshot = snapshot.Functions |> List.find (fun fn -> fn.Id = id)
        let beforeCaller = findFunction fixture.CallerFunctionId before
        let afterCaller = findFunction fixture.CallerFunctionId after
        let beforeUnrelated = findFunction fixture.UnrelatedFunctionId before
        let afterUnrelated = findFunction fixture.UnrelatedFunctionId after
        if beforeCaller <> afterCaller then failwith "Caller changed during ListMap replacement."
        if beforeUnrelated <> afterUnrelated then failwith "Unrelated function changed during ListMap replacement."

        let beforeTarget = findFunction fixture.TargetFunctionId before
        let afterTarget = findFunction fixture.TargetFunctionId after
        Assert.Equal(beforeTarget.Id, afterTarget.Id)
        Assert.Equal(beforeTarget.DisplayName, afterTarget.DisplayName)
        if beforeTarget.Parameters <> afterTarget.Parameters then failwith "Target parameters changed."
        Assert.Equal(beforeTarget.ResultType, afterTarget.ResultType)
        if beforeTarget.RequiredEffects <> afterTarget.RequiredEffects then failwith "Target effects changed."
        Assert.Equal(2, afterTarget.Body.Nodes.Length)
        Assert.NotEqual(beforeTarget.Body.ResultNodeId, afterTarget.Body.ResultNodeId)

    [<Fact>]
    let ``ListMap reviewed proposal rejects stale publication`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let structural = request fixture fixture.SourceParameterId fixture.MapperFunctionId |> simulate workspace

        let rename =
            AgentWorkflow.simulate
                workspace
                [ AgentOperationIntent.RenameDisplayName(
                    AgentRenameTarget.Function fixture.UnrelatedFunctionId,
                    "unrelated_advanced"
                  ) ]
            |> unwrap "ordinary advance simulation"

        let advanced =
            match AgentWorkflow.commit workspace rename with
            | AgentCommitResult.Committed(next, _) -> next
            | AgentCommitResult.Rejected diagnostics -> failwithf "Advance commit failed: %A" diagnostics

        match AgentListMapStructuralWorkflow.commit advanced structural with
        | AgentStructuralCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | result -> failwithf "Expected stale ListMap structural rejection, got %A" result

    let private obligationSnapshot resolved =
        let fixture = createFixture ()
        let helperParameter: SemanticParameter =
            { Id = parameterId "helper-values"
              DisplayName = "values"
              Type = int64ListType }
        let helperResultId = nodeId "helper-result"
        let helper: SemanticFunction =
            { Id = functionId "helper"
              DisplayName = "helper"
              Parameters = [ helperParameter ]
              ResultType = boolListType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = helperResultId; Kind = SemanticNodeKind.ListEmpty boolType } ]
                  ResultNodeId = helperResultId } }

        let target = fixture.Snapshot.Functions |> List.find (fun fn -> fn.Id = fixture.TargetFunctionId)
        let sourceRefId = nodeId "obligation-source"
        let callId = fixture.OldResultNodeId
        let arguments =
            if resolved then
                [ { TargetParameterId = helperParameter.Id; ValueNodeId = sourceRefId } ]
            else
                []
        let bodyNodes =
            (if resolved then
                 [ { Id = sourceRefId; Kind = SemanticNodeKind.ParameterReference fixture.SourceParameterId } ]
             else
                 [])
            @ [ { Id = callId; Kind = SemanticNodeKind.DirectCall(helper.Id, arguments) } ]
        let guardedTarget = { target with Body = { Nodes = bodyNodes; ResultNodeId = callId } }

        let origin = StableId.createForTesting<TransactionTag> "transaction-listmap-obligation"
        let status =
            if resolved then
                ObligationStatus.Resolved { TransactionId = origin; ValueNodeId = sourceRefId }
            else
                ObligationStatus.Open
        let obligation: MissingArgumentObligation =
            { Id = StableId.createForTesting<ObligationTag> (if resolved then "obligation-listmap-resolved" else "obligation-listmap-open")
              OriginTransactionId = origin
              CallNodeId = callId
              ParameterId = helperParameter.Id
              Status = status }
        let snapshot =
            { fixture.Snapshot with
                Functions =
                    helper
                    :: (fixture.Snapshot.Functions
                        |> List.map (fun fn -> if fn.Id = target.Id then guardedTarget else fn))
                Obligations = [ obligation ] }
        Assert.Empty(SnapshotValidator.validate snapshot)
        guardedTarget, snapshot, fixture

    [<Fact>]
    let ``ListMap preflight rejects open and resolved obligations on the replaced body`` () =
        for resolved in [ false; true ] do
            let target, snapshot, fixture = obligationSnapshot resolved
            let workspace = createWorkspace snapshot
            let guardedRequest: AgentReplaceFunctionBodyWithListMap =
                { TargetFunctionId = target.Id
                  ExpectedBaseSnapshotId = snapshot.Id
                  ExpectedCurrentResultNodeId = target.Body.ResultNodeId
                  SourceParameterId = fixture.SourceParameterId
                  MapperFunctionId = fixture.MapperFunctionId }
            AgentListMapStructuralWorkflow.simulate workspace guardedRequest
            |> assertRejectedCode "StructuralBodyReferencedByObligation"

    [<Fact>]
    let ``ListMap committed snapshot roundtrips JSON Nyason and fingerprints`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal = request fixture fixture.SourceParameterId fixture.MapperFunctionId |> simulate workspace
        let committed, _ = commit workspace proposal
        let snapshot = AgentWorkspace.snapshotForStructuralEditing committed

        let json = SnapshotCodec.serialize snapshot |> unwrap "JSON serialize"
        let jsonRestored = SnapshotCodec.deserialize json |> unwrap "JSON deserialize"
        let nyason = NyasonCodec.serialize snapshot |> unwrap "Nyason serialize"
        let nyasonRestored = NyasonCodec.deserialize nyason |> unwrap "Nyason deserialize"

        let snapshotFingerprint = SnapshotFingerprint.compute snapshot |> unwrap "snapshot fingerprint"
        let semanticFingerprint = SemanticFingerprint.compute snapshot |> unwrap "semantic fingerprint"

        for label, restored in [ "JSON", jsonRestored; "Nyason", nyasonRestored ] do
            Assert.Equal(snapshotFingerprint, SnapshotFingerprint.compute restored |> unwrap (label + " snapshot fingerprint"))
            Assert.Equal(semanticFingerprint, SemanticFingerprint.compute restored |> unwrap (label + " semantic fingerprint"))
            let target = restored.Functions |> List.find (fun fn -> fn.Id = fixture.TargetFunctionId)
            Assert.Equal(2, target.Body.Nodes.Length)
            let sourceNodeId =
                target.Body.Nodes
                |> List.tryPick (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.ParameterReference sourceParameterId
                        when sourceParameterId = fixture.SourceParameterId -> Some node.Id
                    | _ -> None)
                |> Option.defaultWith (fun () ->
                    failwithf "%s restored body missing expected source ParameterReference: %A" label target.Body.Nodes)
            let mapNodeId, sourceId =
                target.Body.Nodes
                |> List.tryPick (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.ListMap(sourceId, mapperId, _)
                        when mapperId = fixture.MapperFunctionId -> Some(node.Id, sourceId)
                    | _ -> None)
                |> Option.defaultWith (fun () ->
                    failwithf "%s restored body missing expected ListMap: %A" label target.Body.Nodes)
            Assert.Equal(sourceNodeId, sourceId)
            Assert.Equal(mapNodeId, target.Body.ResultNodeId)

    [<Fact>]
    let ``ListMap request authority exposes only existing stable IDs and exact guards`` () =
        let fields =
            FSharpType.GetRecordFields(typeof<AgentReplaceFunctionBodyWithListMap>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList
        let names = fields |> List.map fst
        let expected =
            [ "TargetFunctionId"
              "ExpectedBaseSnapshotId"
              "ExpectedCurrentResultNodeId"
              "SourceParameterId"
              "MapperFunctionId" ]
        if names <> expected then
            failwithf "ListMap request authority changed. Expected %A, got %A" expected names

        let forbidden =
            [ typeof<Snapshot>
              typeof<ValueGraph>
              typeof<SemanticNode>
              typeof<SemanticNodeKind>
              typeof<GraphNodeId list> ]
        if fields |> List.exists (fun (_, fieldType) -> forbidden |> List.exists ((=) fieldType)) then
            failwithf "ListMap request exposes raw/new graph authority: %A" fields

    let private runDotnet workingDirectory arguments =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        startInfo.Environment["DOTNET_NOLOGO"] <- "1"
        for argument in arguments do startInfo.ArgumentList.Add(argument)
        use child = Process.Start(startInfo)
        if isNull child then failwith "Failed to start dotnet."
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        child.ExitCode, stdout.Result, stderr.Result

    [<Fact; Trait("Suite", "Extended")>]
    let ``committed ListMap body lowers emits builds and executes`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal = request fixture fixture.SourceParameterId fixture.MapperFunctionId |> simulate workspace
        let committed, _ = commit workspace proposal

        let typed = AgentExecution.projectTypedSemanticIr committed |> unwrap "Typed Semantic IR projection"
        let execution = ExecutionIr.lower typed |> unwrap "Execution IR lowering"
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "C# emission"
        Assert.Contains(".Map(", artifact.Source)
        Assert.Contains("NyFunctions." + CSharpBackendNames.functionName fixture.MapperFunctionId, artifact.Source)

        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-listmap-structural-" + Guid.NewGuid().ToString("N"))
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
            let callerName = CSharpBackendNames.functionName fixture.CallerFunctionId
            let harness =
                $"""using System;
using Nyrwvy.Generated;
Console.WriteLine(NyFunctions.{callerName}() ? "true" : "false");
"""
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), projectFile)
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), harness)

            let buildExit, buildOut, buildErr =
                runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            if buildExit <> 0 then
                failwithf "Generated ListMap C# build failed.\nstdout:\n%s\nstderr:\n%s" buildOut buildErr

            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated ListMap C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr
            Assert.Equal("true", runOut.Trim())
        finally
            if Directory.Exists tempRoot then Directory.Delete(tempRoot, true)
