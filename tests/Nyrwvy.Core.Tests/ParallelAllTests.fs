namespace Nyrwvy.Core.Tests

open System
open System.IO
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
        { ExpectedBaseSnapshotId = current session
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
            let effectRequest: AgentCreateEffectDeclarationRequest = { ExpectedBaseSnapshotId = current session; DisplayName = "Exec" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let operation name parameter resultType: AgentCapabilityOperationIntent =
                { DisplayName = name; Parameters = [ { DisplayName = parameter; Type = stringType } ]; ResultType = resultType; FailureType = None; EffectId = effect.Id }
            let capability name operations =
                let request: AgentCreateCapabilityDeclarationRequest = { ExpectedBaseSnapshotId = current session; DisplayName = name; Operations = operations }
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
        PureRunValue.FakeCapability(fixture.Jobs.Id, [ { OperationId = (List.exactlyOne fixture.Jobs.Operations).Id; Responses = responses } ])

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
            Assert.Equal("PureExpressionListCallbackInputTypeMismatch", reject [ fixture.Effect.Id ] parameters (ExpressionRecipe.ParallelAll(ExpressionRecipe.ParameterSlot 0, runJob.Id, [])))
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText fixture.Path) |> unwrap "decode"
            Assert.Empty(SnapshotValidator.validate snapshot)
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (NyasonCodec.deserialize (NyasonCodec.serialize snapshot |> unwrap "nyason") |> unwrap "nyason decode"))
            let artifact = session.EmitCSharp(CSharpBackendProfile.externalImplementations (fixture.Jobs.Operations @ fixture.Files.Operations |> List.map (fun item -> item.Id))) |> unwrap "emit"
            Assert.Contains("public static class NyRuntime", artifact.Source)
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
                    [ { OperationId = (fixture.Files.Operations |> List.find (fun item -> item.DisplayName = "open")).Id; Responses = [ PureRunFakeResponse.Return(okText "h1") ] }
                      { OperationId = (fixture.Files.Operations |> List.find (fun item -> item.DisplayName = "close")).Id
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
