namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ChangeVerificationTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected fixture error: %A" error

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-change-verification-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap
            run session
        finally Directory.Delete(root, true)

    /// The parts of a verifyChange evidence object the review page reads.
    let private verificationReport (evidence: ChangeVerificationEvidence) : obj =
        box
            {| cases = evidence.Observations |> List.map (fun item -> {| functionId = StableId.value item.Case.FunctionId; status = item.Status |})
               affectedFunctionIds = evidence.AffectedFunctionIds |> List.map StableId.value |}

    let private sample: ChangeVerificationCase =
        { Label = "preserve result"
          FunctionId = StableId.createForTesting<FunctionTag> "verification-missing"
          Arguments = []
          ExpectedCandidate = None }

    [<Fact>]
    let ``wire review preserves candidate input and candidate-only expectation diagnostics`` () =
        withSession (fun session ->
            let wire text =
                use reply = AuthoringProtocol.executeLine session text |> JsonDocument.Parse
                Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString())
                reply.RootElement.GetProperty("result").Clone()
            let publish text =
                let proposal = (wire text).GetProperty("proposalId").GetString()
                session.Commit(proposal, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            publish """{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Box","typeKind":{"kind":"product","fields":[{"displayName":"amount","type":{"kind":"primitive","primitive":"int64"}}]}}"""
            publish """{"id":"echo","op":"simulateCreateFunctionByName","exported":true,"displayName":"echo","parameters":[{"displayName":"input","type":{"kind":"named","name":"Box"}}],"resultType":{"kind":"named","name":"Box"},"requiredEffects":[],"body":{"kind":"parameter","name":"input"}}"""
            publish """{"id":"make","op":"simulateCreateFunctionByName","exported":true,"displayName":"make","parameters":[],"resultType":{"kind":"named","name":"Box"},"requiredEffects":[],"body":{"kind":"productConstruct","type":"Box","fields":[{"field":"amount","value":{"kind":"int64Literal","value":"1"}}]}}"""
            let box = session.Declarations().Types |> List.find (fun t -> t.DisplayName = "Box")
            let field = match box.Kind with SemanticTypeKind.Product fields -> fields.Head | _ -> failwith "fixture"
            let echo, make = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "echo"), session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "make")
            let migration =
                sprintf """{"id":"migration","op":"simulateProductFieldTypeMigration","fieldId":"%s","newFieldType":{"kind":"primitive","primitive":"string"},"functionRewrites":[{"functionId":"%s","replacement":{"kind":"productConstruct","typeId":"%s","fields":[{"fieldId":"%s","value":{"kind":"stringLiteral","value":"1"}}]}}]}"""
                    (StableId.value field.Id) (StableId.value make.Id) (StableId.value box.Id) (StableId.value field.Id)
                |> wire
            let product kind value = sprintf """{"kind":"product","typeId":"%s","fields":[{"fieldId":"%s","value":{"kind":"%s","value":%s}}]}""" (StableId.value box.Id) (StableId.value field.Id) kind value
            let request =
                sprintf """{"id":"review","op":"verifyChange","proposalId":"%s","cases":[{"label":"old input","functionId":"%s","arguments":[%s]},{"label":"candidate expectation","functionId":"%s","arguments":[],"expectedCandidate":%s},{"label":"new input","functionId":"%s","arguments":[%s]}]}"""
                    (migration.GetProperty("proposalId").GetString()) (StableId.value echo.Id) (product "int64" "\"1\"") (StableId.value make.Id) (product "bool" "true") (StableId.value echo.Id) (product "string" "\"1\"")
            let before = File.ReadAllBytes session.Path
            let cases = (wire request).GetProperty("evidence").GetProperty("cases")
            for index, code, root, actual in [ 0, "RunProductShapeMismatch", "arguments", "int64"; 1, "RunProductShapeMismatch", "expected", "bool" ] do
                let case = cases[index]
                Assert.Equal("unknown", case.GetProperty("status").GetString())
                Assert.True(case.GetProperty("baseline").TryGetProperty("actual") |> fst)
                Assert.False(case.GetProperty("baseline").TryGetProperty("error") |> fst)
                let error = case.GetProperty("candidate").GetProperty("error")
                Assert.Equal(code, error.GetProperty("code").GetString())
                let details = error.GetProperty("details")
                let path = details.GetProperty("path")
                Assert.Equal(root, path[0].GetString())
                Assert.Equal("amount", path[path.GetArrayLength() - 1].GetString())
                Assert.Equal("string", details.GetProperty("expectedType").GetProperty("primitive").GetString())
                Assert.Equal(actual, details.GetProperty("actualType").GetProperty("primitive").GetString())
            let reverse = cases[2]
            Assert.Equal("unknown", reverse.GetProperty("status").GetString())
            Assert.True(reverse.GetProperty("candidate").TryGetProperty("actual") |> fst)
            let baselineDetails = reverse.GetProperty("baseline").GetProperty("error").GetProperty("details")
            Assert.Equal("int64", baselineDetails.GetProperty("expectedType").GetProperty("primitive").GetString())
            Assert.Equal("string", baselineDetails.GetProperty("actualType").GetProperty("primitive").GetString())
            Assert.Equal<byte>(before, File.ReadAllBytes session.Path))

    [<Fact>]
    let ``wire review never decorates generic entry refusal as an input defect`` () =
        withSession (fun session ->
            let t = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "T")
            let created = session.SimulateCreateFunction { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "generic"; TypeParameters = None; Parameters = [ { DisplayName = "value"; Type = t } ]; ResultType = t; RequiredEffects = []; Body = ExpressionRecipe.ParameterSlot 0 } |> unwrap
            session.Commit(created.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let fn = session.Declarations().Functions |> List.exactlyOne
            let proposal = session.SimulateReplaceBody { TargetFunctionId = fn.Id; ExpectedBaseSnapshotId = session.SnapshotId; ExpectedCurrentResultNodeId = fn.Body.ResultNodeId; Replacement = ExpressionRecipe.Parameter fn.Parameters.Head.Id } |> unwrap
            let text = sprintf """{"id":"generic","op":"verifyChange","proposalId":"%s","cases":[{"label":"unsupported entry","functionId":"%s","arguments":[{"kind":"string","value":"bad"}]}]}""" proposal.ProposalId (StableId.value fn.Id)
            use reply = AuthoringProtocol.executeLine session text |> JsonDocument.Parse
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString())
            let case = reply.RootElement.GetProperty("result").GetProperty("evidence").GetProperty("cases")[0]
            Assert.Equal("unknown", case.GetProperty("status").GetString())
            for side in [ "baseline"; "candidate" ] do
                let error = case.GetProperty(side).GetProperty("error")
                Assert.Equal("RunGenericEntryUnsupported", error.GetProperty("code").GetString())
                Assert.False(error.TryGetProperty("details") |> fst))

    [<Fact>]
    let ``wire candidate expectations and observations use captured candidate names`` () =
        withSession (fun rootSession ->
            let libraryPath = Path.Combine(Path.GetDirectoryName rootSession.Path, "library.json")
            use library = AuthoringSession.createNew libraryPath |> unwrap
            let publish proposal = library.Commit((unwrap proposal).ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            library.SimulateCreateType { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = library.SnapshotId; DisplayName = "Box"; Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "amount"; Type = TypeRef.Primitive PrimitiveType.Int64 } ] } |> publish
            let box = library.Declarations().Types.Head
            let field = match box.Kind with SemanticTypeKind.Product fields -> fields.Head | _ -> failwith "fixture"
            library.SimulateCreateFunction { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = library.SnapshotId; DisplayName = "echo"; TypeParameters = None; Parameters = [ { DisplayName = "input"; Type = TypeRef.Named(box.Id, []) } ]; ResultType = TypeRef.Named(box.Id, []); RequiredEffects = []; Body = ExpressionRecipe.ParameterSlot 0 } |> publish
            let fn, oldPin = library.Declarations().Functions.Head, library.SnapshotId
            use session = AuthoringSession.createNewWithDependencies (Path.Combine(Path.GetDirectoryName rootSession.Path, "consumer.json")) [ libraryPath ] |> unwrap
            library.SimulateOperations [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Type box.Id, "RenamedBox") ] |> publish
            let change = AuthoringDependencyChangeRequest.Selection { Sources = [ libraryPath ]; Replaces = [ StableId.value oldPin ] }
            let proposal = session.SimulateDependencyUpgrade(change, { Fields = []; Functions = []; Operations = [] }, session.SnapshotId) |> unwrap
            let value = sprintf """{"kind":"product","typeId":"%s","fields":[{"fieldId":"%s","value":{"kind":"int64","value":"1"}}]}""" (StableId.value box.Id) (StableId.value field.Id)
            let line = sprintf """{"id":"names","op":"verifyChange","proposalId":"%s","cases":[{"label":"renamed type","functionId":"%s","arguments":[%s],"expectedCandidate":%s}]}""" proposal.ProposalId (StableId.value fn.Id) value value
            let before = File.ReadAllBytes session.Path, File.ReadAllBytes(session.Path + ".dependency.json")
            use reply = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString())
            let case = reply.RootElement.GetProperty("result").GetProperty("evidence").GetProperty("cases")[0]
            let arguments = case.GetProperty("arguments")
            Assert.Equal("Box", arguments[0].GetProperty("type").GetString())
            Assert.Equal("Box", case.GetProperty("baseline").GetProperty("actual").GetProperty("type").GetString())
            Assert.Equal("RenamedBox", case.GetProperty("candidate").GetProperty("actual").GetProperty("type").GetString())
            Assert.Equal("RenamedBox", case.GetProperty("expectedCandidate").GetProperty("type").GetString())
            Assert.Equal("intended-change-tested", case.GetProperty("status").GetString())
            Assert.Equal(before, (File.ReadAllBytes session.Path, File.ReadAllBytes(session.Path + ".dependency.json"))))

    [<Fact>]
    let ``empty oversized and blank conditions never produce evidence`` () =
        withSession (fun session ->
            for cases in [ []; List.replicate 33 sample; [ { sample with Label = " " } ] ] do
                match ChangeVerification.verify session "missing" cases with
                | Error(AuthoringSessionError.DependencyFailure _) -> ()
                | other -> failwithf "Invalid case set accepted: %A" other)

    [<Fact>]
    let ``wire input cannot submit a forged evidence success`` () =
        withSession (fun session ->
            use reply = AuthoringProtocol.executeLine session """{"id":"forged","op":"verifyChange","proposalId":"missing","cases":[],"evidence":{"status":"tested-agreement"}}""" |> JsonDocument.Parse
            Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
            Assert.Contains("evidence", reply.RootElement.GetProperty("error").GetProperty("message").GetString()))

    [<Fact>]
    let ``source edits invalidate evaluation and preserve edited source`` () =
        withSession (fun session ->
            let edited = File.ReadAllText(session.Path) + "\n"
            File.WriteAllText(session.Path, edited)
            match ChangeVerification.verify session "missing" [ sample ] with
            | Error AuthoringSessionError.ExternalModification -> ()
            | other -> failwithf "Changed source accepted: %A" other
            Assert.Equal(edited, File.ReadAllText session.Path))

    [<Fact>]
    let ``dependency selection edits invalidate evaluation without publishing`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-verification-dependency-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let importedPath = Path.Combine(root, "dependency.json")
            (AuthoringSession.createNew importedPath |> unwrap).Close()
            use session = AuthoringSession.createNewWithDependencies (Path.Combine(root, "local.json")) [ importedPath ] |> unwrap
            let baseline = File.ReadAllText session.Path
            let manifest = session.Path + ".dependency.json"
            File.AppendAllText(manifest, "\n")
            match ChangeVerification.verify session "missing" [ sample ] with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Changed dependency selection accepted: %A" other
            Assert.Equal(baseline, File.ReadAllText session.Path)
        finally Directory.Delete(root, true)

    [<Theory>]
    [<InlineData("counterexample", "counterexample")>]
    [<InlineData("unknown", "unknown")>]
    [<InlineData("tested-agreement", "tested agreement / explicit intended results; no proof")>]
    [<InlineData("intended-change-tested", "tested agreement / explicit intended results; no proof")>]
    let ``review separates structural readiness from prominent behavioral summary`` status expectedSummary =
        withSession (fun session ->
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId
                  TypeParameters = None
                  DisplayName = "unitValue"
                  Parameters = []
                  ResultType = TypeRef.Primitive PrimitiveType.Unit
                  RequiredEffects = []
                  Body = ExpressionRecipe.UnitLiteral }
            let proposal = session.SimulateCreateFunction request |> unwrap
            let view = session.PendingProposalView proposal.ProposalId |> unwrap
            let evidence: ChangeVerificationEvidence =
                { ProposalId = proposal.ProposalId
                  BaselineSnapshotId = session.SnapshotId
                  CandidateSnapshotId = view.ProposedSnapshot.Value.Id
                  BaselineDeclarations = session.Declarations()
                  CandidateDeclarations = (session.ExecutionContext(Some proposal.ProposalId) |> unwrap).Declarations
                  BaselineFingerprint = "fixture-baseline"
                  CandidateFingerprint = "fixture-candidate"
                  AffectedFunctionIds = []
                  Observations =
                    [ { Case = sample
                        Status = status
                        Baseline = Error(PureRunError.Unsupported("fixture", "presentation only"))
                        Candidate = Error(PureRunError.Unsupported("fixture", "presentation only")) } ] }
            let _, artifact = ViewerFile.writeVerificationReview session.Path None view (fun _ -> None) (verificationReport evidence) |> unwrap
            let content = ViewerRendering.page artifact.Content
            let header = content.Split("</header>")[0]
            Assert.Contains("Structural readiness: semantically ready", header)
            Assert.Contains("Behavioral verification: " + expectedSummary, header)
            // The page also closes one header per function focus; the banner belongs to the page's only.
            Assert.True(content.Split("</header>").Length > 2)
            Assert.Equal(2, content.Split("Behavioral verification:").Length)
            Assert.DoesNotContain("class=\"status\">Readiness:", header)
            Assert.Contains("does not gate commit", header)
            Assert.Contains("Explicit case entry points: 1", header)
            Assert.Contains("not dynamic coverage", header))

    [<Fact; Trait("Suite", "Extended")>]
    let ``candidate extra fake call is a counterexample without publishing`` () =
        withSession (fun session ->
            let publish (proposal: AuthoringProposal) =
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "io" } |> unwrap |> publish
            let effect = session.Declarations().Effects |> List.exactlyOne
            session.SimulateCreateCapability
                { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Provider"
                  Operations = [ { MayWait = false; DisplayName = "read"; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Int64; FailureType = None; EffectId = effect.Id } ] }
            |> unwrap |> publish
            let capability = session.Declarations().Capabilities |> List.exactlyOne
            let operation = capability.Operations |> List.exactlyOne
            let invoke value = ExpressionRecipe.CapabilityInvoke(value, operation.Id, [])
            session.SimulateCreateFunction
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "query"
                  TypeParameters = None
                  Parameters = [ { DisplayName = "provider"; Type = TypeRef.Capability capability.Id } ]
                  ResultType = TypeRef.Primitive PrimitiveType.Int64; RequiredEffects = [ effect.Id ]
                  Body = invoke (ExpressionRecipe.ParameterSlot 0) }
            |> unwrap |> publish
            let fn = session.Declarations().Functions |> List.exactlyOne
            let body = invoke (ExpressionRecipe.Parameter fn.Parameters.Head.Id)
            let proposal = session.SimulateReplaceBody
                               { TargetFunctionId = fn.Id; ExpectedBaseSnapshotId = session.SnapshotId
                                 ExpectedCurrentResultNodeId = fn.Body.ResultNodeId; Replacement = ExpressionRecipe.Bind(body, body) } |> unwrap
            let bytes = File.ReadAllBytes session.Path
            let fake = PureRunValue.FakeCapability(capability.Id,
                         [ { OperationId = operation.Id; Responses = [ PureRunFakeResponse.Return(PureRunValue.Int64 7L) ]; Cases = None } ])
            let evidence = ChangeVerification.verify session proposal.ProposalId
                               [ { sample with FunctionId = fn.Id; Arguments = [ fake ] } ] |> unwrap
            let observation = evidence.Observations |> List.exactlyOne
            Assert.Equal("counterexample", observation.Status)
            match observation.Candidate with
            | Error(PureRunError.RuntimeFailure failure) -> Assert.True(failure.BindingExhausted)
            | other -> failwithf "Expected exhausted fake: %A" other
            let fakeJson = sprintf "{\"kind\":\"fakeCapability\",\"capabilityId\":\"%s\",\"operations\":[{\"operationId\":\"%s\",\"responses\":[{\"kind\":\"return\",\"value\":{\"kind\":\"int64\",\"value\":\"7\"}}]}]}" (StableId.value capability.Id) (StableId.value operation.Id)
            let wire argument = sprintf "{\"id\":\"failure\",\"op\":\"verifyChange\",\"proposalId\":\"%s\",\"cases\":[{\"label\":\"failure boundary\",\"functionId\":\"%s\",\"arguments\":[%s]}]}" proposal.ProposalId (StableId.value fn.Id) argument
            use reply = AuthoringProtocol.executeLine session (wire fakeJson) |> JsonDocument.Parse
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString())
            let item = reply.RootElement.GetProperty("result").GetProperty("evidence").GetProperty("cases")[0]
            Assert.Equal("counterexample", item.GetProperty("status").GetString())
            let error = item.GetProperty("candidate").GetProperty("error")
            Assert.Equal("RunBindingExhausted", error.GetProperty("code").GetString())
            Assert.Equal("FakeResponsesExhaustedException", error.GetProperty("exceptionType").GetString())
            Assert.Equal("fault", error.GetProperty("failureKind").GetString())
            Assert.Equal(2, error.GetProperty("calls").GetArrayLength())
            Assert.Contains(error.GetProperty("frames").EnumerateArray(), fun frame -> frame.GetProperty("kind").GetString() = "function" && frame.GetProperty("functionId").GetString() = StableId.value fn.Id)
            Assert.False(item.GetProperty("candidate").TryGetProperty("unknown") |> fst)
            // Operator host enablement applies to runPure only, never to verifyChange.
            session.HostSourcesAllowed <- true
            let hostJson = sprintf "{\"kind\":\"hostCapability\",\"capabilityId\":\"%s\",\"implementation\":\"UnregisteredHost\"}" (StableId.value capability.Id)
            use refused = AuthoringProtocol.executeLine session (wire hostJson) |> JsonDocument.Parse
            Assert.True(refused.RootElement.GetProperty("ok").GetBoolean())
            let refusedCase = refused.RootElement.GetProperty("result").GetProperty("evidence").GetProperty("cases")[0]
            Assert.Equal("unknown", refusedCase.GetProperty("status").GetString())
            for side in [ "baseline"; "candidate" ] do
                Assert.Equal("VerificationHostUnsupported", refusedCase.GetProperty(side).GetProperty("error").GetProperty("code").GetString())
            session.Reject proposal.ProposalId |> unwrap |> ignore
            Assert.Equal<byte>(bytes, File.ReadAllBytes session.Path))

    let private result value calls: PureRunResult =
        { SnapshotId = StableId.createForTesting<SnapshotTag> "classifier-snapshot"
          FunctionId = sample.FunctionId; Actual = value; Expected = None
          Matches = None; Mismatch = None; Calls = calls }

    let private runtimeFailure exceptionType mapped =
        PureRunError.RuntimeFailure
            { SnapshotId = StableId.createForTesting<SnapshotTag> "classifier-snapshot"
              FunctionId = sample.FunctionId; Kind = "fault"; ExceptionType = exceptionType; Message = "fixture"
              Frames = if mapped then [ PureRunFrame.Function sample.FunctionId ] else []
              BindingExhausted = exceptionType = "FakeResponsesExhaustedException"; Calls = [] }

    [<Fact>]
    let ``classifier checks results explicit intentions and ordered calls`` () =
        let call key: PureRunCall =
            { ParameterId = StableId.createForTesting<ParameterTag> "binding"
              OperationId = StableId.createForTesting<CapabilityOperationTag> "read"
              Arguments = [ StableId.createForTesting<CapabilityParameterTag> "key", PureRunValue.Int64 key ] }
        let run value calls = Ok(result (PureRunValue.Int64 value) calls)
        let check expected intent before after =
            let observed = after |> Result.map (fun fresh -> { fresh with Expected = intent; Matches = intent |> Option.map ((=) fresh.Actual) })
            Assert.Equal(expected, ChangeVerification.classify false intent before observed)
        check "tested-agreement" None (run 7L []) (run 7L [])
        check "counterexample" None (run 7L []) (run 8L [])
        check "intended-change-tested" (Some(PureRunValue.Int64 8L)) (run 7L []) (run 8L [])
        check "counterexample" (Some(PureRunValue.Int64 9L)) (run 7L []) (run 8L [])
        check "counterexample" None (run 7L [ call 1L ]) (run 7L [ call 2L ])
        check "counterexample" None (run 7L [ call 1L; call 2L ]) (run 7L [ call 2L; call 1L ])
        check "counterexample" (Some(PureRunValue.Int64 7L)) (run 7L []) (run 7L [ call 1L ])
        // A raw equal expectation does not override an absent validated runner match.
        Assert.Equal("counterexample", ChangeVerification.classify false (Some(PureRunValue.Int64 7L)) (run 7L []) (run 7L []))
        // Preservation observes a new result type rather than rejecting an injected old expectation.
        check "counterexample" None (run 7L []) (Ok(result (PureRunValue.String "seven") []))
        Assert.Equal("unknown", ChangeVerification.classify true None (run 7L []) (run 7L []))

    [<Theory>]
    [<InlineData("FakeCapabilityException")>]
    [<InlineData("FakeArgumentsUnmatchedException")>]
    [<InlineData("FakeResponsesExhaustedException")>]
    [<InlineData("System.ArgumentException")>]
    [<InlineData("System.ArgumentOutOfRangeException")>]
    [<InlineData("System.InvalidOperationException")>]
    [<InlineData("System.DivideByZeroException")>]
    [<InlineData("System.OverflowException")>]
    [<InlineData("System.IndexOutOfRangeException")>]
    [<InlineData("System.NullReferenceException")>]
    let ``only mapped recognized candidate failure after successful baseline is counterexample`` exceptionType =
        let success = Ok(result (PureRunValue.Int64 7L) [])
        let failure = Error(runtimeFailure exceptionType true)
        Assert.Equal("counterexample", ChangeVerification.classify false None success failure)
        Assert.Equal("counterexample", ChangeVerification.classify false (Some(PureRunValue.Int64 8L)) success failure)
        Assert.Equal("unknown", ChangeVerification.classify false None failure success)
        Assert.Equal("unknown", ChangeVerification.classify false None failure failure)
        Assert.Equal("unknown", ChangeVerification.classify true None success failure)
        Assert.Equal("unknown", ChangeVerification.classify false None success (Error(runtimeFailure exceptionType false)))
        let foreign = match runtimeFailure exceptionType true with
                      | PureRunError.RuntimeFailure failure -> PureRunError.RuntimeFailure { failure with Frames = [ PureRunFrame.Function(StableId.createForTesting<FunctionTag> "unrelated") ] }
                      | other -> other
        Assert.Equal("unknown", ChangeVerification.classify false None success (Error foreign))

    [<Theory>]
    [<InlineData("RunTimeout")>]
    [<InlineData("RunOutputTimeout")>]
    [<InlineData("RunProcessFailure")>]
    [<InlineData("RunStartupFailure")>]
    [<InlineData("RunReapFailure")>]
    [<InlineData("RunIoFailure")>]
    [<InlineData("RunExpectedTypeMismatch")>]
    [<InlineData("RunBindingShapeMismatch")>]
    [<InlineData("VerificationHostUnsupported")>]
    [<InlineData("FutureUnsupportedDiagnostic")>]
    let ``runner validation compiler timeout and unsupported diagnostics stay unknown`` code =
        let success = Ok(result PureRunValue.Unit [])
        let failure = Error(PureRunError.Unsupported(code, "fixture"))
        Assert.Equal("unknown", ChangeVerification.classify false None success failure)
        Assert.Equal("unknown", ChangeVerification.classify false None failure success)
        Assert.Equal("unknown", ChangeVerification.classify false None failure failure)
        Assert.Equal("unknown", ChangeVerification.classify false None success (Error(PureRunError.SessionFailure AuthoringSessionError.ExternalModification)))

    [<Theory>]
    [<InlineData("System.StackOverflowException")>]
    [<InlineData("System.OutOfMemoryException")>]
    [<InlineData("System.OperationCanceledException")>]
    [<InlineData("System.Threading.Tasks.TaskCanceledException")>]
    [<InlineData("RunnerException")>]
    [<InlineData("UnknownHarnessException")>]
    let ``resource cancellation harness and unknown runtime exceptions stay unknown`` exceptionType =
        Assert.Equal("unknown", ChangeVerification.classify false None (Ok(result PureRunValue.Unit [])) (Error(runtimeFailure exceptionType true)))

    [<Fact>]
    let ``review distinguishes impact from direct case entry points`` () =
        withSession (fun session ->
            let proposal = session.SimulateCreateFunction
                               { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "unitValue"; Parameters = []
                                 TypeParameters = None
                                 ResultType = TypeRef.Primitive PrimitiveType.Unit; RequiredEffects = []; Body = ExpressionRecipe.UnitLiteral } |> unwrap
            let view = session.PendingProposalView proposal.ProposalId |> unwrap
            let other = StableId.createForTesting<FunctionTag> "affected-caller"
            let evidence: ChangeVerificationEvidence =
                { ProposalId = proposal.ProposalId; BaselineSnapshotId = session.SnapshotId; CandidateSnapshotId = view.ProposedSnapshot.Value.Id
                  BaselineDeclarations = session.Declarations(); CandidateDeclarations = (session.ExecutionContext(Some proposal.ProposalId) |> unwrap).Declarations
                  BaselineFingerprint = "baseline"; CandidateFingerprint = "candidate"
                  AffectedFunctionIds = [ sample.FunctionId; other ]
                  Observations = [ { Case = sample; Status = "tested-agreement"; Baseline = Ok(result PureRunValue.Unit []); Candidate = Ok(result PureRunValue.Unit []) } ] }
            let _, artifact = ViewerFile.writeVerificationReview session.Path None view (fun _ -> None) (verificationReport evidence) |> unwrap
            let content = ViewerRendering.page artifact.Content
            let header = content.Split("</header>")[0]
            Assert.Contains("Impact: 2 affected function(s)", header)
            Assert.Contains("1 affected function(s) directly targeted, 1 without a direct case", header)
            Assert.Contains("not dynamic coverage", header))

    [<Fact; Trait("Suite", "Extended")>]
    let ``wire explicit intentions canonicalize reordered and nested product union fields`` () =
        withSession (fun session ->
            let publish (proposal: AuthoringProposal) = session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let integer = TypeRef.Primitive PrimitiveType.Int64
            let field name itemType: AgentTypeFieldIntent = { DisplayName = name; Type = itemType }
            let createType name kind =
                session.SimulateCreateType { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = name; Kind = kind } |> unwrap |> publish
                session.Declarations().Types |> List.find (fun item -> item.DisplayName = name)
            let pair = createType "Pair" (AgentTypeDeclarationKindIntent.Product [ field "a" integer; field "b" integer ])
            let answer = createType "Answer" (AgentTypeDeclarationKindIntent.Union
                             [ { DisplayName = "Found"; Fields = [ field "pair" (TypeRef.Named(pair.Id, [])); field "count" integer ] } ])
            let envelope = createType "Envelope" (AgentTypeDeclarationKindIntent.Product
                               [ field "answer" (TypeRef.Named(answer.Id, [])); field "pairs" (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Named(pair.Id, []) ])) ])
            let productFields (t: SemanticType) = match t.Kind with SemanticTypeKind.Product fields -> fields | _ -> failwith "product"
            let variant = match answer.Kind with SemanticTypeKind.Union variants -> List.exactlyOne variants | _ -> failwith "union"
            let intValue n = sprintf "{\"kind\":\"int64\",\"value\":\"%d\"}" n
            let encodeFields reverse (fields: SemanticField list) values =
                let ordered = fields |> List.sortBy (fun item -> StableId.value item.Id) |> fun items -> if reverse then List.rev items else items
                ordered |> List.map (fun item -> sprintf "{\"fieldId\":\"%s\",\"value\":%s}" (StableId.value item.Id) (Map.find item.DisplayName values)) |> String.concat ","
            let product (t: SemanticType) reverse values = sprintf "{\"kind\":\"product\",\"typeId\":\"%s\",\"fields\":[%s]}" (StableId.value t.Id) (encodeFields reverse (productFields t) values)
            let pairValue reverse wrong = product pair reverse (Map.ofList [ "a", intValue (if wrong then 99 else 1); "b", intValue 2 ])
            let unionValue reverse wrong = sprintf "{\"kind\":\"union\",\"typeId\":\"%s\",\"variantId\":\"%s\",\"fields\":[%s]}" (StableId.value answer.Id) (StableId.value variant.Id)
                                              (encodeFields reverse variant.Fields (Map.ofList [ "pair", pairValue reverse wrong; "count", intValue 3 ]))
            let envelopeValue reverse wrong = product envelope reverse (Map.ofList [ "answer", unionValue reverse wrong; "pairs", sprintf "{\"kind\":\"list\",\"itemType\":{\"kind\":\"named\",\"typeId\":\"%s\"},\"items\":[%s]}" (StableId.value pair.Id) (pairValue reverse false) ])
            for t, value in [ pair, pairValue; answer, unionValue; envelope, envelopeValue ] do
                session.SimulateCreateFunction
                    { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "identity" + t.DisplayName
                      TypeParameters = None
                      Parameters = [ { DisplayName = "value"; Type = TypeRef.Named(t.Id, []) } ]; ResultType = TypeRef.Named(t.Id, [])
                      RequiredEffects = []; Body = ExpressionRecipe.ParameterSlot 0 } |> unwrap |> publish
                let fn = session.Declarations().Functions |> List.find (fun item -> item.DisplayName = "identity" + t.DisplayName)
                let proposal = session.SimulateReplaceBody
                                   { TargetFunctionId = fn.Id; ExpectedBaseSnapshotId = session.SnapshotId; ExpectedCurrentResultNodeId = fn.Body.ResultNodeId
                                     Replacement = ExpressionRecipe.Parameter fn.Parameters.Head.Id } |> unwrap
                let bytes = File.ReadAllBytes session.Path
                for wrong in [ false; true ] do
                    let request = sprintf "{\"id\":\"canonical-intent\",\"op\":\"verifyChange\",\"proposalId\":\"%s\",\"cases\":[{\"label\":\"canonical explicit intent\",\"functionId\":\"%s\",\"arguments\":[%s],\"expectedCandidate\":%s}]}" proposal.ProposalId (StableId.value fn.Id) (value false false) (value true wrong)
                    use reply = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                    Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString())
                    let observation = reply.RootElement.GetProperty("result").GetProperty("evidence").GetProperty("cases")[0]
                    Assert.Equal((if wrong then "counterexample" else "intended-change-tested"), observation.GetProperty("status").GetString())
                    let candidate = observation.GetProperty("candidate")
                    Assert.Equal(not wrong, candidate.GetProperty("matches").GetBoolean())
                    if wrong then Assert.NotEqual(JsonValueKind.Null, candidate.GetProperty("mismatch").ValueKind)
                    if not wrong then
                        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("mismatch").ValueKind)
                        Assert.Equal(observation.GetProperty("baseline").GetProperty("actual").ToString(), candidate.GetProperty("actual").ToString())
                session.Reject proposal.ProposalId |> unwrap |> ignore
                Assert.Equal<byte>(bytes, File.ReadAllBytes session.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``real comparisons preserve results and compare effect arguments and order`` () =
        withSession (fun session ->
            let publish (proposal: AuthoringProposal) = session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let integer = TypeRef.Primitive PrimitiveType.Int64
            session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "io" } |> unwrap |> publish
            let effect = session.Declarations().Effects |> List.exactlyOne
            session.SimulateCreateCapability
                { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Provider"
                  Operations = [ { MayWait = false; DisplayName = "read"; Parameters = [ { DisplayName = "key"; Type = integer } ]; ResultType = integer; FailureType = None; EffectId = effect.Id } ] }
            |> unwrap |> publish
            let capability = session.Declarations().Capabilities |> List.exactlyOne
            let operation = capability.Operations |> List.exactlyOne
            session.SimulateCreateFunction
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "query"
                  TypeParameters = None
                  Parameters = [ { DisplayName = "provider"; Type = TypeRef.Capability capability.Id } ]
                  ResultType = integer; RequiredEffects = [ effect.Id ]
                  Body = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, operation.Id, [ operation.Parameters.Head.Id, ExpressionRecipe.Int64Literal 1L ]) }
            |> unwrap |> publish
            let fn () = session.Declarations().Functions |> List.exactlyOne
            let invoke key = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.Parameter (fn ()).Parameters.Head.Id, operation.Id, [ operation.Parameters.Head.Id, ExpressionRecipe.Int64Literal key ])
            let propose body = session.SimulateReplaceBody
                                   { TargetFunctionId = (fn ()).Id; ExpectedBaseSnapshotId = session.SnapshotId; ExpectedCurrentResultNodeId = (fn ()).Body.ResultNodeId; Replacement = body } |> unwrap
            let fake count = PureRunValue.FakeCapability(capability.Id,
                               [ { OperationId = operation.Id; Responses = List.replicate count (PureRunFakeResponse.Return(PureRunValue.Int64 7L)); Cases = None } ])
            let check body count status =
                let bytes = File.ReadAllBytes session.Path
                let proposal = propose body
                let observation = ChangeVerification.verify session proposal.ProposalId [ { sample with FunctionId = (fn ()).Id; Arguments = [ fake count ] } ] |> unwrap |> fun e -> List.exactlyOne e.Observations
                Assert.Equal(status, observation.Status)
                Assert.Equal((observation.Baseline |> unwrap).Actual, (observation.Candidate |> unwrap).Actual)
                if status = "counterexample" then Assert.NotEqual<PureRunCall list>((observation.Baseline |> unwrap).Calls, (observation.Candidate |> unwrap).Calls)
                session.Reject proposal.ProposalId |> unwrap |> ignore
                Assert.Equal<byte>(bytes, File.ReadAllBytes session.Path)
            check (invoke 1L) 1 "tested-agreement"
            check (invoke 2L) 1 "counterexample"
            propose (ExpressionRecipe.Bind(invoke 1L, invoke 2L)) |> publish
            check (ExpressionRecipe.Bind(invoke 2L, invoke 1L)) 2 "counterexample")

    [<Fact; Trait("Suite", "Extended")>]
    let ``successful evidence cannot authorize rejected stale or reopened proposals`` () =
        withSession (fun session ->
            let publish (proposal: AuthoringProposal) = session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.SimulateCreateFunction
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "value"; Parameters = []
                  TypeParameters = None
                  ResultType = TypeRef.Primitive PrimitiveType.Unit; RequiredEffects = []; Body = ExpressionRecipe.UnitLiteral } |> unwrap |> publish
            let fn = session.Declarations().Functions |> List.exactlyOne
            let propose () = session.SimulateReplaceBody
                                 { TargetFunctionId = fn.Id; ExpectedBaseSnapshotId = session.SnapshotId; ExpectedCurrentResultNodeId = (session.Declarations().Functions |> List.exactlyOne).Body.ResultNodeId; Replacement = ExpressionRecipe.UnitLiteral } |> unwrap
            let cases = [ { sample with FunctionId = fn.Id } ]
            let verify id = ChangeVerification.verify session id cases
            let bytes = File.ReadAllBytes session.Path
            let rejected = propose ()
            Assert.Equal("tested-agreement", (verify rejected.ProposalId |> unwrap).Observations.Head.Status)
            session.Reject rejected.ProposalId |> unwrap |> ignore
            Assert.True(verify rejected.ProposalId |> Result.isError)
            Assert.Equal<byte>(bytes, File.ReadAllBytes session.Path)
            let stale = propose ()
            Assert.Equal("tested-agreement", (verify stale.ProposalId |> unwrap).Observations.Head.Status)
            propose () |> publish
            let committedBytes = File.ReadAllBytes session.Path
            Assert.True(verify stale.ProposalId |> Result.isError)
            session.Close()
            use reopened = AuthoringSession.openExisting session.Path |> unwrap
            Assert.True(ChangeVerification.verify reopened stale.ProposalId cases |> Result.isError)
            Assert.Equal<byte>(committedBytes, File.ReadAllBytes session.Path)
            Assert.Equal(PureRunValue.Unit, (PureRun.run reopened fn.Id [] None |> unwrap).Actual))
