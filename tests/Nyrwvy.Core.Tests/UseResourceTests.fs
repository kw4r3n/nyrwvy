namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// G4 scoped resources: UseResource always releases an acquired resource, on Ok and Err exits and
/// before a host exception propagates, with the body's Err taking precedence over a release Err.
module UseResourceTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private result okType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; stringType ])
    let private unwrap label value =
        match value with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    type private Fixture =
        { Session: AuthoringSession
          Path: string
          Effect: SemanticEffect
          Files: SemanticCapability
          Operation: string -> SemanticCapabilityOperation
          Lock: SemanticCapability
          Function: string -> SemanticFunction }

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private creation (session: AuthoringSession) name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { Exported = true; ExpectedBaseSnapshotId = current session
          TypeParameters = None
          DisplayName = name
          Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = effects
          Body = body }

    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-use-resource-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let session = AuthoringSession.createNew path |> unwrap "session"
            let effectRequest: AgentCreateEffectDeclarationRequest = { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "IO" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let operation name parameter resultType: AgentCapabilityOperationIntent =
                { DisplayName = name
                  Parameters = [ { DisplayName = parameter; Type = stringType } ]
                  ResultType = resultType
                  FailureType = None
                  EffectId = effect.Id }
            let capability name operations =
                let request: AgentCreateCapabilityDeclarationRequest = { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name; Operations = operations }
                session.SimulateCreateCapability request |> unwrap name |> publish session
                session.Declarations().Capabilities |> List.find (fun item -> item.DisplayName = name)
            let files =
                capability "Files"
                    [ operation "open" "path" (result stringType)
                      operation "read" "handle" (result stringType)
                      operation "close" "handle" (result unitType) ]
            let lock = capability "Lock" [ operation "acquire" "name" (result stringType); operation "release" "token" unitType ]
            let op (capability: SemanticCapability) name = capability.Operations |> List.find (fun item -> item.DisplayName = name)
            let invoke receiver (operation: SemanticCapabilityOperation) argument =
                ExpressionRecipe.CapabilityInvoke(receiver, operation.Id, [ (List.exactlyOne operation.Parameters).Id, argument ])
            let slot = ExpressionRecipe.ParameterSlot
            // readBody(files, handle) -> Result<String, String>
            creation session "readBody" [ "files", TypeRef.Capability files.Id; "handle", stringType ] (result stringType) [ effect.Id ]
                (invoke (slot 0) (op files "read") (slot 1))
            |> session.SimulateCreateFunction |> unwrap "readBody" |> publish session
            // echoBody(token) -> Result<String, String>: a pure body inside a lock
            creation session "echoBody" [ "token", stringType ] (result stringType) [] (ExpressionRecipe.ResultOk(stringType, slot 0))
            |> session.SimulateCreateFunction |> unwrap "echoBody" |> publish session
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let readBody = find "readBody"
            let filesParameter = (readBody.Parameters |> List.find (fun item -> item.DisplayName = "files")).Id
            creation session "readFile" [ "path", stringType; "files", TypeRef.Capability files.Id ] (result stringType) [ effect.Id ]
                (ExpressionRecipe.UseResource(invoke (slot 1) (op files "open") (slot 0), slot 1, (op files "close").Id, readBody.Id, [ filesParameter, slot 1 ]))
            |> session.SimulateCreateFunction |> unwrap "readFile" |> publish session
            creation session "withLock" [ "name", stringType; "lock", TypeRef.Capability lock.Id ] (result stringType) [ effect.Id ]
                (ExpressionRecipe.UseResource(invoke (slot 1) (op lock "acquire") (slot 0), slot 1, (op lock "release").Id, (find "echoBody").Id, []))
            |> session.SimulateCreateFunction |> unwrap "withLock" |> publish session
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            run { Session = reopened; Path = path; Effect = effect; Files = files; Operation = op files; Lock = lock
                  Function = fun name -> reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name) }
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``UseResource checks resource body release and effects and persists`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            let readBody = fixture.Function "readBody"
            let filesParameter = (readBody.Parameters |> List.find (fun item -> item.DisplayName = "files")).Id
            let slot = ExpressionRecipe.ParameterSlot
            let acquire = ExpressionRecipe.CapabilityInvoke(slot 1, (fixture.Operation "open").Id, [ (List.exactlyOne (fixture.Operation "open").Parameters).Id, slot 0 ])
            let reject effects body =
                match session.SimulateCreateFunction(creation session "bad" [ "path", stringType; "files", TypeRef.Capability fixture.Files.Id ] (result stringType) effects body) with
                | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _))) -> diagnostic.Code
                | other -> failwithf "Invalid UseResource accepted: %A" other
            let useWith release body context = ExpressionRecipe.UseResource(acquire, slot 1, release, body, context)
            // A pure acquisition isolates UseResource's own effect check for body and release.
            let pureAcquire = ExpressionRecipe.ResultOk(stringType, slot 0)
            Assert.Equal("ExpressionCallMissingEffectRequirement",
                reject [] (ExpressionRecipe.UseResource(pureAcquire, slot 1, (fixture.Operation "close").Id, readBody.Id, [ filesParameter, slot 1 ])))
            Assert.Equal("UseResourceBodyMismatch", reject [ fixture.Effect.Id ] (useWith (fixture.Operation "close").Id readBody.Id []))
            // `read` returns Result<String, String>, not Unit or Result<Unit, String>.
            Assert.Equal("UseResourceReleaseSignatureMismatch", reject [ fixture.Effect.Id ] (useWith (fixture.Operation "read").Id readBody.Id [ filesParameter, slot 1 ]))
            Assert.Equal("UseResourceAcquireTypeMismatch",
                reject [ fixture.Effect.Id ] (ExpressionRecipe.UseResource(slot 0, slot 1, (fixture.Operation "close").Id, readBody.Id, [ filesParameter, slot 1 ])))
            let json = File.ReadAllText fixture.Path
            Assert.Contains("\"useResource\"", json)
            let snapshot = SnapshotCodec.deserialize json |> unwrap "decode"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let persisted = SnapshotCodec.serialize snapshot |> unwrap "reserialize"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (SnapshotCodec.deserialize persisted |> unwrap "decode"))
            let typed = TypedSemanticIr.project snapshot |> unwrap "typed"
            Assert.Contains("\"useResource\"", ExecutionIrSerializer.serialize (ExecutionIr.lower typed |> unwrap "lower")))

    [<Fact; Trait("Suite", "Extended")>]
    let ``UseResource releases on every exit and combines failures`` () =
        withFixture (fun fixture ->
            let ok okType value = PureRunValue.ResultValue(okType, stringType, true, value)
            let okText (text: string) = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.String text)
            let errText (text: string) = PureRunValue.ResultErr(PureRunScalarKind.String, PureRunScalar.String text)
            let errUnit (text: string) = PureRunValue.ResultValue(unitType, stringType, false, PureRunValue.String text)
            let files opened read closed =
                PureRunValue.FakeCapability(fixture.Files.Id,
                    [ { OperationId = (fixture.Operation "open").Id; Cases = None; Responses = opened }
                      { OperationId = (fixture.Operation "read").Id; Cases = None; Responses = read }
                      { OperationId = (fixture.Operation "close").Id; Cases = None; Responses = closed } ])
            let returns value = [ PureRunFakeResponse.Return value ]
            let names (calls: PureRunCall list) =
                calls |> List.map (fun call -> (fixture.Files.Operations |> List.find (fun item -> item.Id = call.OperationId)).DisplayName)
            let run fake = PureRun.run fixture.Session (fixture.Function "readFile").Id [ PureRunValue.String "a.txt"; fake ] None
            let closed = returns (ok unitType PureRunValue.Unit)
            let success = run (files (returns (okText "h1")) (returns (okText "data")) closed) |> unwrap "success"
            Assert.Equal(okText "data", success.Actual)
            Assert.Equal<string list>([ "open"; "read"; "close" ], names success.Calls)
            let missing = run (files (returns (errText "missing")) [] []) |> unwrap "missing"
            Assert.Equal(errText "missing", missing.Actual)
            Assert.Equal<string list>([ "open" ], names missing.Calls)
            let corrupt = run (files (returns (okText "h1")) (returns (errText "corrupt")) closed) |> unwrap "corrupt"
            Assert.Equal(errText "corrupt", corrupt.Actual)
            Assert.Equal<string list>([ "open"; "read"; "close" ], names corrupt.Calls)
            let flush = run (files (returns (okText "h1")) (returns (okText "data")) (returns (errUnit "flush failed"))) |> unwrap "flush"
            Assert.Equal(errText "flush failed", flush.Actual)
            let both = run (files (returns (okText "h1")) (returns (errText "corrupt")) (returns (errUnit "flush failed"))) |> unwrap "both"
            Assert.Equal(errText "corrupt", both.Actual)
            match run (files (returns (okText "h1")) [ PureRunFakeResponse.Throw "disk gone" ] closed) with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("disk gone", failure.Message)
                Assert.Equal<string list>([ "open"; "read"; "close" ], names failure.Calls)
            | other -> failwithf "Host exception did not propagate after release: %A" other
            let lock =
                PureRunValue.FakeCapability(fixture.Lock.Id,
                    [ { OperationId = (fixture.Lock.Operations |> List.find (fun item -> item.DisplayName = "acquire")).Id; Cases = None; Responses = returns (okText "t1") }
                      { OperationId = (fixture.Lock.Operations |> List.find (fun item -> item.DisplayName = "release")).Id; Cases = None; Responses = returns PureRunValue.Unit } ])
            let locked = PureRun.run fixture.Session (fixture.Function "withLock").Id [ PureRunValue.String "n"; lock ] (Some(okText "t1")) |> unwrap "lock"
            Assert.Equal(Some true, locked.Matches)
            Assert.Equal(2, locked.Calls.Length))
