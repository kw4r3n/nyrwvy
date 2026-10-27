namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// G4 structured concurrency: ParallelAll starts a worker per item, always awaits all of them, and
/// the first Err or host exception cancels the rest; UseResource around it cleans up afterwards.
module ParallelAllTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private result okType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; stringType ])
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label value =
        match value with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    type private Fixture =
        { Session: AuthoringSession
          Path: string
          Effect: SemanticEffect
          Jobs: SemanticCapability
          Files: SemanticCapability
          Function: string -> SemanticFunction }

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private creation (session: AuthoringSession) name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = current session
          TypeParameters = None
          DisplayName = name
          Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = effects
          Body = body }

    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-parallel-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let session = AuthoringSession.createNew path |> unwrap "session"
            let effectRequest: AgentCreateEffectDeclarationRequest = { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "Exec" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let operation name parameter resultType: AgentCapabilityOperationIntent =
                { MayWait = false; DisplayName = name; Parameters = [ { DisplayName = parameter; Type = stringType } ]; ResultType = resultType; FailureType = None; EffectId = effect.Id }
            let capability name operations =
                let request: AgentCreateCapabilityDeclarationRequest = { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name; Operations = operations }
                session.SimulateCreateCapability request |> unwrap name |> publish session
                session.Declarations().Capabilities |> List.find (fun item -> item.DisplayName = name)
            let jobs = capability "Jobs" [ operation "run" "name" (result stringType) ]
            let files = capability "Files" [ operation "open" "path" (result stringType); operation "close" "handle" (result unitType) ]
            let op (capability: SemanticCapability) name = capability.Operations |> List.find (fun item -> item.DisplayName = name)
            let invoke receiver (operation: SemanticCapabilityOperation) argument =
                ExpressionRecipe.CapabilityInvoke(receiver, operation.Id, [ (List.exactlyOne operation.Parameters).Id, argument ])
            let slot = ExpressionRecipe.ParameterSlot
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let parameter (fn: SemanticFunction) name = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
            let create request = session.SimulateCreateFunction request |> unwrap request.DisplayName |> publish session
            create (creation session "runJob" [ "jobs", TypeRef.Capability jobs.Id; "name", stringType ] (result stringType) [ effect.Id ] (invoke (slot 0) (op jobs "run") (slot 1)))
            let runJob = find "runJob"
            create (creation session "runAll" [ "names", listOf stringType; "jobs", TypeRef.Capability jobs.Id ] (result (listOf stringType)) [ effect.Id ]
                (ExpressionRecipe.ParallelAll(slot 0, runJob.Id, [ parameter runJob "jobs", slot 1 ])))
            // The workspace body runs every job while the opened handle is held.
            create (creation session "inWorkspace" [ "jobs", TypeRef.Capability jobs.Id; "names", listOf stringType; "handle", stringType ] (result (listOf stringType)) [ effect.Id ]
                (ExpressionRecipe.ParallelAll(slot 1, runJob.Id, [ parameter runJob "jobs", slot 0 ])))
            let inWorkspace = find "inWorkspace"
            create (creation session "runInWorkspace" [ "path", stringType; "names", listOf stringType; "jobs", TypeRef.Capability jobs.Id; "files", TypeRef.Capability files.Id ] (result (listOf stringType)) [ effect.Id ]
                (ExpressionRecipe.UseResource(invoke (slot 3) (op files "open") (slot 0), slot 3, (op files "close").Id, inWorkspace.Id,
                    [ parameter inWorkspace "jobs", slot 2; parameter inWorkspace "names", slot 1 ])))
            create (creation session "notCancelled" [ "unused", stringType ] boolType [] ExpressionRecipe.CancellationRequested)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            run { Session = reopened; Path = path; Effect = effect; Jobs = jobs; Files = files
                  Function = fun name -> reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name) }
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private okText text = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.String text)
    let private errText text = PureRunValue.ResultErr(PureRunScalarKind.String, PureRunScalar.String text)

    let private jobs (fixture: Fixture) responses =
        PureRunValue.FakeCapability(fixture.Jobs.Id, [ { OperationId = (List.exactlyOne fixture.Jobs.Operations).Id; Cases = None; Responses = responses } ])

    let private profile (fixture: Fixture) =
        CSharpBackendProfile.externalImplementations (fixture.Jobs.Operations @ fixture.Files.Operations |> List.map (fun op -> op.Id))

    let private assertConcurrentRequired capabilityId outcome =
        match outcome with
        | Error(CSharpBackendError.ConcurrentCapabilityBindingRequired(_, _, actual, _)) -> Assert.Equal(capabilityId, actual)
        | other -> failwithf "Expected concurrent binding rejection: %A" other

    [<Fact>]
    let ``parallel bindings are explicit and diagnosed with semantic identities through JSON`` () =
        withFixture (fun fixture ->
            let program = fixture.Session.ProjectExecution() |> unwrap "lower"
            CSharpBackend.emit program (profile fixture) |> assertConcurrentRequired fixture.Jobs.Id
            let safe = profile fixture |> CSharpBackendProfile.withConcurrentCapabilities [ fixture.Jobs.Id ]
            // Files is used outside workers, around the join, so it may remain sequential.
            CSharpBackend.emit program safe |> unwrap "safe" |> ignore
            let unknown = StableId.createForTesting<CapabilityTag> "capability-unknown-concurrency"
            match CSharpBackend.emit program (safe |> CSharpBackendProfile.withConcurrentCapabilities [ unknown ]) with
            | Error(CSharpBackendError.InvalidConcurrentCapabilityBinding id) -> Assert.Equal(unknown, id)
            | other -> failwithf "Unknown concurrency assertion accepted: %A" other
            let operationIds = (profile fixture).CapabilityBindings |> List.map (fun binding -> StableId.value binding.OperationId) |> JsonSerializer.Serialize
            let emit extra =
                let line = $"""{{"id":"emit","op":"emitCSharp","profile":{{"kind":"externalImplementations","operationIds":{operationIds}{extra}}}}}"""
                AuthoringProtocol.executeLine fixture.Session line |> JsonDocument.Parse
            use rejected = emit ""
            let error = rejected.RootElement.GetProperty("error")
            Assert.Equal("ConcurrentCapabilityBindingRequired", error.GetProperty("code").GetString())
            let details = error.GetProperty("details")
            let worker = fixture.Function "runJob"
            Assert.Equal(StableId.value worker.Id, details.GetProperty("functionId").GetString())
            Assert.Equal(StableId.value fixture.Jobs.Id, details.GetProperty("capabilityId").GetString())
            Assert.Equal(StableId.value (List.exactlyOne fixture.Jobs.Operations).Id, details.GetProperty("operationId").GetString())
            Assert.False(String.IsNullOrEmpty(details.GetProperty("nodeId").GetString()))
            use accepted = emit ($""", "concurrentCapabilityIds":["{StableId.value fixture.Jobs.Id}"]""")
            Assert.True(accepted.RootElement.GetProperty("ok").GetBoolean(), accepted.RootElement.ToString())
            use invalid = emit ", \"concurrentCapabilityIds\":[true]"
            Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean()))

    [<Fact>]
    let ``worker reachability checks indirect calls folds releases and cycles without requiring unused context`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            let slot = ExpressionRecipe.ParameterSlot
            let param (fn: SemanticFunction) name = (fn.Parameters |> List.find (fun p -> p.DisplayName = name)).Id
            let create name parameters resultType effects body =
                session.SimulateCreateFunction(creation session name parameters resultType effects body) |> unwrap name |> publish session
                fixture.Function name
            let capType = TypeRef.Capability fixture.Jobs.Id
            let runJob = fixture.Function "runJob"
            let indirect = create "indirect" [ "jobs", capType; "name", stringType ] (result stringType) [ fixture.Effect.Id ]
                                (ExpressionRecipe.DirectCall(runJob.Id, [ param runJob "jobs", slot 0; param runJob "name", slot 1 ]))
            let folder = create "folder" [ "jobs", capType; "state", stringType; "name", stringType ] (result stringType) [ fixture.Effect.Id ]
                                (ExpressionRecipe.DirectCall(indirect.Id, [ param indirect "jobs", slot 0; param indirect "name", slot 2 ]))
            let foldWorker = create "foldWorker" [ "jobs", capType; "name", stringType ] (result stringType) [ fixture.Effect.Id ]
                                (ExpressionRecipe.ListEffectFold(ExpressionRecipe.ListConstruct(stringType, [ slot 1 ]), ExpressionRecipe.StringLiteral "", folder.Id, [ param folder "jobs", slot 0 ]))
            let createParallel name (worker: SemanticFunction) capId =
                create name [ "cap", TypeRef.Capability capId ] (result (listOf stringType)) [ fixture.Effect.Id ]
                    (ExpressionRecipe.ParallelAll(ExpressionRecipe.ListEmpty stringType, worker.Id, [ worker.Parameters.Head.Id, slot 0 ]))
            let throughFold = createParallel "throughFold" foldWorker fixture.Jobs.Id
            let body = create "resourceBody" [ "handle", stringType ] (result stringType) [] (ExpressionRecipe.ResultOk(stringType, slot 0))
            let close = fixture.Files.Operations |> List.find (fun op -> op.DisplayName = "close")
            let releaseWorker = create "releaseWorker" [ "files", TypeRef.Capability fixture.Files.Id; "name", stringType ] (result stringType) [ fixture.Effect.Id ]
                                    (ExpressionRecipe.UseResource(ExpressionRecipe.ResultOk(stringType, slot 1), slot 0, close.Id, body.Id, []))
            let throughRelease = createParallel "throughRelease" releaseWorker fixture.Files.Id
            let unused = create "unusedCapability" [ "jobs", capType; "name", stringType ] (result stringType) [] (ExpressionRecipe.ResultOk(stringType, slot 1))
            let unusedEntry = createParallel "unusedEntry" unused fixture.Jobs.Id
            let program = session.ProjectExecution() |> unwrap "lower"
            let emit entry = CSharpBackend.emit (ExecutionCallGraph.forEntry program entry) (profile fixture)
            // Even an empty source is checked structurally. The operation sits behind both a fold and a direct call.
            emit throughFold.Id |> assertConcurrentRequired fixture.Jobs.Id
            emit throughRelease.Id |> assertConcurrentRequired fixture.Files.Id
            emit unusedEntry.Id |> unwrap "unused context" |> ignore
            emit runJob.Id |> unwrap "sequential entry" |> ignore
            // A recursive edge must not prevent discovering the existing invocation or terminate traversal early.
            let addCycle (fn: ExecutionFunction) =
                if fn.SemanticFunctionId <> runJob.Id then fn else
                    let blocks = fn.Blocks |> List.map (fun block ->
                        let call = { block.Instructions.Head with Kind = ExecutionInstructionKind.DirectCall(indirect.Id, []) }
                        { block with Instructions = call :: block.Instructions })
                    { fn with Blocks = blocks }
            let recursive = { program with Functions = List.map addCycle program.Functions }
            CSharpBackend.emit (ExecutionCallGraph.forEntry recursive throughFold.Id) (profile fixture)
            |> assertConcurrentRequired fixture.Jobs.Id)

    [<Fact>]
    let ``runner rejects an unasserted host before compiling and cannot launder it through a safe fake`` () =
        withFixture (fun fixture ->
            fixture.Session.HostSourcesAllowed <- true
            // Intentionally invalid host source proves admission rejects before compilation.
            File.WriteAllText(Path.Combine(Path.GetDirectoryName fixture.Path, "Host.cs"), "not C sharp")
            let request support =
                $"""{{"id":"run","op":"runPure","functionId":"{StableId.value (fixture.Function "runAll").Id}","hostSources":["Host.cs"],"arguments":[{{"kind":"list","itemKind":"string","items":[]}},{{"kind":"hostCapability","capabilityId":"{StableId.value fixture.Jobs.Id}","implementation":"Counter"{support}}}]}}"""
            for support in [ ""; ",\"supportsConcurrentCalls\":false" ] do
                use response = AuthoringProtocol.executeLine fixture.Session (request support) |> JsonDocument.Parse
                Assert.Equal("ConcurrentCapabilityBindingRequired", response.RootElement.GetProperty("error").GetProperty("code").GetString())
            use malformed = AuthoringProtocol.executeLine fixture.Session (request ",\"supportsConcurrentCalls\":\"true\"") |> JsonDocument.Parse
            Assert.False(malformed.RootElement.GetProperty("ok").GetBoolean())
            let runAll = fixture.Function "runAll"
            let request = creation fixture.Session "mixedInstances"
                                [ "names", listOf stringType; "safe", TypeRef.Capability fixture.Jobs.Id; "unsafe", TypeRef.Capability fixture.Jobs.Id ]
                                runAll.ResultType [ fixture.Effect.Id ]
                                (ExpressionRecipe.DirectCall(runAll.Id, [ runAll.Parameters[0].Id, ExpressionRecipe.ParameterSlot 0; runAll.Parameters[1].Id, ExpressionRecipe.ParameterSlot 1 ]))
            fixture.Session.SimulateCreateFunction request |> unwrap "mixed" |> publish fixture.Session
            match PureRun.runWithHost fixture.Session (fixture.Function "mixedInstances").Id
                      [ PureRunValue.StringList []; jobs fixture []; PureRunValue.HostCapability(fixture.Jobs.Id, "Counter", false) ] None [ "Host.cs" ] with
            | Error(PureRunError.SessionFailure(AuthoringSessionError.BackendFailure error)) ->
                Error error |> assertConcurrentRequired fixture.Jobs.Id
            | other -> failwithf "Unsafe instance accepted: %A" other)

    [<Fact; Trait("Suite", "Extended")>]
    let ``shared fake consumes each ticket once in recorded call order and retains state after join`` () =
        withFixture (fun fixture ->
            let names = [ for i in 1 .. 32 -> string i ]
            let tickets = [ for i in 1 .. 32 -> "ticket-" + string i ]
            let completed = PureRun.run fixture.Session (fixture.Function "runAll").Id
                                [ PureRunValue.StringList names; jobs fixture (tickets |> List.map (okText >> PureRunFakeResponse.Return)) ] None |> unwrap "tickets"
            let assigned = List.zip completed.Calls tickets |> List.map (fun (call, ticket) ->
                (List.exactlyOne call.Arguments |> snd), ticket) |> Map.ofList
            let expected = names |> List.map (fun name -> Map.find (PureRunValue.String name) assigned)
            Assert.Equal(PureRunValue.ResultValue(listOf stringType, stringType, true, PureRunValue.StringList expected), completed.Actual)
            let runAll, runJob = fixture.Function "runAll", fixture.Function "runJob"
            let request = creation fixture.Session "afterJoin" [ "names", listOf stringType; "jobs", TypeRef.Capability fixture.Jobs.Id ] (result stringType) [ fixture.Effect.Id ]
                            (ExpressionRecipe.Bind(
                                ExpressionRecipe.DirectCall(runAll.Id, [ runAll.Parameters[0].Id, ExpressionRecipe.ParameterSlot 0; runAll.Parameters[1].Id, ExpressionRecipe.ParameterSlot 1 ]),
                                ExpressionRecipe.DirectCall(runJob.Id, [ runJob.Parameters[0].Id, ExpressionRecipe.ParameterSlot 1; runJob.Parameters[1].Id, ExpressionRecipe.StringLiteral "read" ])))
            fixture.Session.SimulateCreateFunction request |> unwrap "after join" |> publish fixture.Session
            let joined = PureRun.run fixture.Session (fixture.Function "afterJoin").Id
                            [ PureRunValue.StringList [ "a"; "b" ]; jobs fixture ([ "one"; "two"; "three" ] |> List.map (okText >> PureRunFakeResponse.Return)) ] None |> unwrap "joined"
            Assert.Equal(okText "three", joined.Actual)
            Assert.Equal(PureRunValue.String "read", List.last joined.Calls |> fun call -> List.exactlyOne call.Arguments |> snd))

    [<Fact; Trait("Suite", "Extended")>]
    let ``concurrent stateful host runs through JSON and sequential host needs no assertion`` () =
        withFixture (fun fixture ->
            fixture.Session.HostSourcesAllowed <- true
            let operation = List.exactlyOne fixture.Jobs.Operations
            let source = String.concat "\n"
                            [ "using Nyrwvy.Generated;"
                              $"public sealed class Counter : {CSharpBackendNames.capabilityName fixture.Jobs.Id}"
                              "{"
                              "    int count;"
                              $"    public NyResult<NyStr, NyStr> {CSharpBackendNames.capabilityOperationName operation.Id}(NyStr name)"
                              "    {"
                              "        int next = System.Threading.Interlocked.Increment(ref count);"
                              "        return NyResult<NyStr, NyStr>.Ok(next.ToString(System.Globalization.CultureInfo.InvariantCulture));"
                              "    }"
                              "}" ]
            File.WriteAllText(Path.Combine(Path.GetDirectoryName fixture.Path, "Host.cs"), source)
            let line =
                $"""{{"id":"host","op":"runPure","functionId":"{StableId.value (fixture.Function "runAll").Id}","hostSources":["Host.cs"],"arguments":[{{"kind":"list","itemKind":"string","items":["a","b"]}},{{"kind":"hostCapability","capabilityId":"{StableId.value fixture.Jobs.Id}","implementation":"Counter","supportsConcurrentCalls":true}}]}}"""
            use response = AuthoringProtocol.executeLine fixture.Session line |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let actual = response.RootElement.GetProperty("result").GetProperty("actual").GetProperty("value").GetProperty("items")
            Assert.Equal<string list>([ "1"; "2" ], actual.EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.sort |> Seq.toList)
            let sequential = PureRun.runWithHost fixture.Session (fixture.Function "runJob").Id
                                [ PureRunValue.HostCapability(fixture.Jobs.Id, "Counter", false); PureRunValue.String "a" ] None [ "Host.cs" ] |> unwrap "sequential"
            Assert.Equal(okText "1", sequential.Actual))

    [<Fact>]
    let ``ParallelAll checks its worker and persists`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            let runJob = fixture.Function "runJob"
            let context = [ (runJob.Parameters |> List.find (fun item -> item.DisplayName = "jobs")).Id, ExpressionRecipe.ParameterSlot 1 ]
            let reject effects parameters body =
                match session.SimulateCreateFunction(creation session "bad" parameters (result (listOf stringType)) effects body) with
                | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _))) -> diagnostic.Code
                | other -> failwithf "Invalid ParallelAll accepted: %A" other
            let parameters = [ "names", listOf stringType; "jobs", TypeRef.Capability fixture.Jobs.Id ]
            Assert.Equal("ExpressionCallMissingEffectRequirement", reject [] parameters (ExpressionRecipe.ParallelAll(ExpressionRecipe.ParameterSlot 0, runJob.Id, context)))
            Assert.Equal("PureExpressionCallbackInputTypeMismatch", reject [ fixture.Effect.Id ] parameters (ExpressionRecipe.ParallelAll(ExpressionRecipe.ParameterSlot 0, runJob.Id, [])))
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText fixture.Path) |> unwrap "decode"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let effects name = EffectRequirements.derive snapshot (fixture.Function name)
            Assert.Equal<EffectId list>([ fixture.Effect.Id ], (effects "runAll").EffectIds)
            let release = fixture.Files.Operations |> List.find (fun op -> op.DisplayName = "close")
            Assert.Contains((effects "runInWorkspace").Evidence, fun item -> item.Source = EffectRequirementSource.CapabilityOperation release.Id)
            Assert.Empty((effects "notCancelled").EffectIds)
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (SnapshotCodec.deserialize (SnapshotCodec.serialize snapshot |> unwrap "reserialize") |> unwrap "decode"))
            let artifact = session.EmitCSharp(CSharpBackendProfile.externalImplementations (fixture.Jobs.Operations @ fixture.Files.Operations |> List.map (fun item -> item.Id)) |> CSharpBackendProfile.withConcurrentCapabilities [ fixture.Jobs.Id ]) |> unwrap "emit"
            Assert.Contains("public static class NyRuntime", artifact.Source)
            Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(NyRuntime.Cancellation.Value)", artifact.Source)
            // A program without concurrency never contains the runtime helper.
            Assert.DoesNotContain("NyRuntime", SnapshotCodec.serialize (Snapshot.empty (StableId.createForTesting<SnapshotTag> "plain")) |> unwrap "plain"))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ParallelAll awaits every worker cancels on failure and cleans up`` () =
        withFixture (fun fixture ->
            let run name arguments = PureRun.run fixture.Session (fixture.Function name).Id arguments None
            let names = PureRunValue.StringList [ "a"; "b" ]
            let both = run "runAll" [ names; jobs fixture [ PureRunFakeResponse.Return(okText "done"); PureRunFakeResponse.Return(okText "done") ] ] |> unwrap "both"
            Assert.Equal(PureRunValue.ResultValue(listOf stringType, stringType, true, PureRunValue.StringList [ "done"; "done" ]), both.Actual)
            let empty = run "runAll" [ PureRunValue.StringList []; jobs fixture [] ] |> unwrap "empty"
            Assert.Equal(PureRunValue.ResultValue(listOf stringType, stringType, true, PureRunValue.StringList []), empty.Actual)
            // Whichever worker calls first fails; the other blocks like a long process until cancelled.
            let failing = [ PureRunFakeResponse.Return(errText "boom"); PureRunFakeResponse.AwaitCancellation(okText "stopped") ]
            let stopped = run "runAll" [ names; jobs fixture failing ] |> unwrap "stopped"
            Assert.Equal(PureRunValue.ResultValue(listOf stringType, stringType, false, PureRunValue.String "boom"), stopped.Actual)
            Assert.Equal(2, stopped.Calls.Length)
            let files =
                PureRunValue.FakeCapability(fixture.Files.Id,
                    [ { OperationId = (fixture.Files.Operations |> List.find (fun item -> item.DisplayName = "open")).Id; Cases = None; Responses = [ PureRunFakeResponse.Return(okText "h1") ] }
                      { OperationId = (fixture.Files.Operations |> List.find (fun item -> item.DisplayName = "close")).Id
                        Cases = None
                        Responses = [ PureRunFakeResponse.Return(PureRunValue.ResultValue(unitType, stringType, true, PureRunValue.Unit)) ] } ])
            let cleaned = run "runInWorkspace" [ PureRunValue.String "/w"; names; jobs fixture failing; files ] |> unwrap "cleaned"
            Assert.Equal(PureRunValue.ResultValue(listOf stringType, stringType, false, PureRunValue.String "boom"), cleaned.Actual)
            let operationName (call: PureRunCall) =
                (fixture.Jobs.Operations @ fixture.Files.Operations |> List.find (fun item -> item.Id = call.OperationId)).DisplayName
            Assert.Equal<string list>([ "open"; "run"; "run"; "close" ], cleaned.Calls |> List.map operationName)
            match run "runAll" [ names; jobs fixture [ PureRunFakeResponse.Throw "crash"; PureRunFakeResponse.AwaitCancellation(okText "stopped") ] ] with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("crash", failure.Message)
                Assert.Equal(2, failure.Calls.Length)
            | other -> failwithf "Worker host exception did not propagate after the others finished: %A" other
            Assert.Equal(PureRunValue.Bool false, (run "notCancelled" [ PureRunValue.String "x" ] |> unwrap "outside").Actual))

    [<Fact; Trait("Suite", "Extended")>]
    let ``nested ParallelAll inherits cancellation joins every worker and preserves failure precedence`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            let slot = ExpressionRecipe.ParameterSlot
            let parameter (fn: SemanticFunction) name = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
            let create name parameters resultType body =
                creation session name parameters resultType [ fixture.Effect.Id ] body
                |> session.SimulateCreateFunction |> unwrap name |> publish session
                fixture.Function name
            let operation = List.exactlyOne fixture.Jobs.Operations
            let invoke receiver argument =
                ExpressionRecipe.CapabilityInvoke(receiver, operation.Id, [ (List.exactlyOne operation.Parameters).Id, argument ])
            let capabilityType = TypeRef.Capability fixture.Jobs.Id
            // Separate fakes keep response assignment independent of worker scheduling. The second
            // call records completion only after the first call's cancellation wait has returned.
            let leaf =
                create "waitAndFinish" [ "waiting", capabilityType; "finished", capabilityType; "name", stringType ] (result stringType)
                    (ExpressionRecipe.Bind(invoke (slot 0) (slot 2), invoke (slot 1) (slot 2)))
            let nested =
                create "nestedWorker" [ "waiting", capabilityType; "finished", capabilityType; "name", stringType ] (result (listOf stringType))
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.StringEqual(slot 2, ExpressionRecipe.StringLiteral "outer"),
                        ExpressionRecipe.ResultErr(listOf stringType, ExpressionRecipe.StringLiteral "outer failure"),
                        ExpressionRecipe.ParallelAll(
                            ExpressionRecipe.ListConstruct(stringType, [ ExpressionRecipe.StringLiteral "child-a"; ExpressionRecipe.StringLiteral "child-b" ]),
                            leaf.Id, [ parameter leaf "waiting", slot 0; parameter leaf "finished", slot 1 ])))
            let entry =
                create "runNested" [ "names", listOf stringType; "waiting", capabilityType; "finished", capabilityType ] (result (listOf (listOf stringType)))
                    (ExpressionRecipe.ParallelAll(slot 0, nested.Id, [ parameter nested "waiting", slot 1; parameter nested "finished", slot 2 ]))
            let run names response =
                // The outer Err is the only way out of both inner waits, even if it occurs before
                // the inner CTS is created. PureRun bounds execution, so a regression cannot hang.
                PureRun.run session entry.Id
                    [ PureRunValue.StringList names
                      jobs fixture (List.replicate 2 (PureRunFakeResponse.AwaitCancellation(okText "cancelled")))
                      jobs fixture (List.replicate 2 response) ] None
            let assertJoined (calls: PureRunCall list) =
                Assert.Equal(4, calls.Length)
                for name in [ "waiting"; "finished" ] do
                    let arguments =
                        calls
                        |> List.filter (fun call -> call.ParameterId = parameter entry name)
                        |> List.map (fun call -> (List.exactlyOne call.Arguments) |> snd)
                        |> List.sort
                    Assert.Equal<PureRunValue list>([ PureRunValue.String "child-a"; PureRunValue.String "child-b" ], arguments)
            // Inner errors necessarily finish after the outer Err triggered cancellation, but
            // selection still follows input index rather than completion order at the outer node.
            for names, response, expected in
                [ [ "nested"; "outer" ], okText "stopped", "outer failure"
                  [ "nested"; "outer" ], errText "inner failure", "inner failure"
                  [ "outer"; "nested" ], errText "inner failure", "outer failure" ] do
                let completed = run names (PureRunFakeResponse.Return response) |> unwrap "nested cancellation"
                Assert.Equal(PureRunValue.ResultValue(listOf (listOf stringType), stringType, false, PureRunValue.String expected), completed.Actual)
                assertJoined completed.Calls
            match run [ "outer"; "nested" ] (PureRunFakeResponse.Throw "inner crash") with
            | Error(PureRunError.RuntimeFailure failure) ->
                // A nested host exception wins even over an earlier, lower-index outer Err.
                Assert.Equal("inner crash", failure.Message)
                Assert.False(failure.BindingExhausted)
                assertJoined failure.Calls
            | other -> failwithf "Nested host exception did not propagate after all workers finished: %A" other)
