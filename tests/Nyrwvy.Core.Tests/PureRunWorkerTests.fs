namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The session's long-lived runner worker: runs of one build share a process and a load context
/// without sharing state, a dead worker is replaced, and the worker dies with the session.
module PureRunWorkerTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private slot = ExpressionRecipe.ParameterSlot
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private create (session: AuthoringSession) name parameters resultType effects body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name
              TypeParameters = None
              Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
              ResultType = resultType; RequiredEffects = effects; Body = body }
        session.SimulateCreateFunction request |> unwrap |> publish session
        find session name
    /// Replaces a function's body with a recipe over its own identity, for recursion.
    let private replace (session: AuthoringSession) name body =
        let target = find session name
        let request: AgentReplaceFunctionBodyWithExpressionRecipe =
            { TargetFunctionId = target.Id
              ExpectedBaseSnapshotId = current session
              ExpectedCurrentResultNodeId = target.Body.ResultNodeId
              Replacement = body target }
        session.SimulateReplaceBody request |> unwrap |> publish session
    let private next value = ExpressionRecipe.Int64WrappingAdd(value, ExpressionRecipe.Int64Literal 1L)
    let private selfCall (target: SemanticFunction) argument = ExpressionRecipe.DirectCall(target.Id, [ (List.exactlyOne target.Parameters).Id, argument ])
    let private alive pid =
        try not (Process.GetProcessById pid).HasExited
        with :? ArgumentException -> false
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-worker-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap
            try run session
            finally (session :> IDisposable).Dispose()
        finally Directory.Delete(root, true)
    /// An effect, a capability with one `mark(value: String): String` operation, and `probe(io, value)`
    /// that invokes it once.
    let private probe (session: AuthoringSession) =
        session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "IO" } |> unwrap |> publish session
        let effect = session.Declarations().Effects |> List.exactlyOne
        session.SimulateCreateCapability
            { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "IO"
              Operations =
                [ { DisplayName = "mark"; Parameters = [ { DisplayName = "value"; Type = textType } ]
                    ResultType = textType; FailureType = None; EffectId = effect.Id } ] }
        |> unwrap |> publish session
        let cap = session.Declarations().Capabilities |> List.exactlyOne
        let op = cap.Operations.Head
        let probe =
            create session "probe" [ "io", TypeRef.Capability cap.Id; "value", textType ] textType [ effect.Id ]
                (ExpressionRecipe.CapabilityInvoke(slot 0, op.Id, [ op.Parameters.Head.Id, slot 1 ]))
        let fake responses =
            PureRunValue.FakeCapability(cap.Id, [ { OperationId = op.Id; Cases = None; Responses = responses |> List.map (PureRunValue.String >> PureRunFakeResponse.Return) } ])
        probe, fake

    [<Fact>]
    let ``runs of one build share the worker but not its statics`` () =
        withSession (fun session ->
            let probe, fake = probe session
            Assert.Equal(None, session.PureRunWorker.ProcessId)
            let run response argument =
                PureRun.run session probe.Id [ fake [ response ]; PureRunValue.String argument ] None |> unwrap
            let first = run "one" "a"
            let pid = session.PureRunWorker.ProcessId |> Option.get
            Assert.True(alive pid)
            let second = run "two" "b"
            // The second run of the same build is served by the same worker process, yet the
            // program's call log starts empty and its fake script is this run's.
            Assert.Equal(Some pid, session.PureRunWorker.ProcessId)
            Assert.Equal(PureRunValue.String "one", first.Actual)
            Assert.Equal(PureRunValue.String "two", second.Actual)
            Assert.Equal<PureRunValue list>([ PureRunValue.String "a" ], first.Calls |> List.map (fun call -> snd call.Arguments.Head))
            Assert.Equal<PureRunValue list>([ PureRunValue.String "b" ], second.Calls |> List.map (fun call -> snd call.Arguments.Head))
            // An exhausted fake fails in its own run only.
            match PureRun.run session probe.Id [ fake []; PureRunValue.String "c" ] None with
            | Error(PureRunError.RuntimeFailure failure) -> Assert.True(failure.BindingExhausted)
            | other -> failwithf "An empty fake did not fail: %A" other
            Assert.Equal(PureRunValue.String "three", (run "three" "d").Actual))

    [<Fact>]
    let ``warming starts the worker that the first run then uses`` () =
        withSession (fun session ->
            let probe, fake = probe session
            PureRun.warm session.PureRunWorker
            let pid = session.PureRunWorker.ProcessId |> Option.get
            Assert.True(alive pid)
            let result = PureRun.run session probe.Id [ fake [ "one" ]; PureRunValue.String "a" ] None |> unwrap
            Assert.Equal(PureRunValue.String "one", result.Actual)
            Assert.Equal(Some pid, session.PureRunWorker.ProcessId))

    [<Fact>]
    let ``disposing the session ends its worker and removes its directory`` () =
        let pid, directory =
            withSession (fun session ->
                let probe, fake = probe session
                PureRun.run session probe.Id [ fake [ "one" ]; PureRunValue.String "a" ] None |> unwrap |> ignore
                let directory = session.PureRunWorker.Directory |> Option.get
                Assert.True(Directory.Exists directory)
                session.PureRunWorker.ProcessId |> Option.get, directory)
        Assert.False(alive pid)
        Assert.False(Directory.Exists directory)

    [<Fact; Trait("Suite", "Extended")>]
    let ``a worker is replaced after a timeout and after a stack overflow`` () =
        withSession (fun session ->
            let probe, fake = probe session
            // `spin` calls itself in tail position, which runs as a loop and never ends; `deep` calls
            // itself as an operand, which grows the stack until it overflows.
            let value (target: SemanticFunction) = ExpressionRecipe.Parameter (List.exactlyOne target.Parameters).Id
            create session "spin" [ "value", intType ] intType [] (slot 0) |> ignore
            replace session "spin" (fun target -> selfCall target (next (value target)))
            create session "deep" [ "value", intType ] intType [] (slot 0) |> ignore
            replace session "deep" (fun target -> next (selfCall target (next (value target))))
            let run () = (PureRun.run session probe.Id [ fake [ "ok" ]; PureRunValue.String "a" ] None |> unwrap).Actual
            Assert.Equal(PureRunValue.String "ok", run ())
            let first = session.PureRunWorker.ProcessId |> Option.get
            match PureRun.run session (find session "spin").Id [ PureRunValue.Int64 0L ] None with
            | Error(PureRunError.Unsupported("RunTimeout", message)) -> Assert.Contains("5 s", message)
            | other -> failwithf "The endless loop did not time out: %A" other
            Assert.False(alive first)
            Assert.Equal(PureRunValue.String "ok", run ())
            let second = session.PureRunWorker.ProcessId |> Option.get
            Assert.NotEqual(first, second)
            match PureRun.run session (find session "deep").Id [ PureRunValue.Int64 0L ] None with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("System.StackOverflowException", failure.ExceptionType)
                Assert.NotEmpty(failure.Frames)
            | other -> failwithf "Unbounded recursion was not a runtime failure: %A" other
            Assert.False(alive second)
            Assert.Equal(PureRunValue.String "ok", run ())
            Assert.NotEqual(Some second, session.PureRunWorker.ProcessId))
