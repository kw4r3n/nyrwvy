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

    let private sample: ChangeVerificationCase =
        { Label = "preserve result"
          FunctionId = StableId.createForTesting<FunctionTag> "verification-missing"
          Arguments = []
          ExpectedCandidate = None }

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
                { ExpectedBaseSnapshotId = session.SnapshotId
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
                  BaselineFingerprint = "fixture-baseline"
                  CandidateFingerprint = "fixture-candidate"
                  AffectedFunctionIds = []
                  Observations =
                    [ { Case = sample
                        Status = status
                        Baseline = Error(PureRunError.Unsupported("fixture", "presentation only"))
                        Candidate = Error(PureRunError.Unsupported("fixture", "presentation only")) } ] }
            let _, artifact = ViewerFile.writeVerificationReview session.Path None view (fun _ -> None) evidence "{}" |> unwrap
            let header = artifact.Content.Split("</header>")[0]
            Assert.Contains("Structural readiness: semantically ready", header)
            Assert.Contains("Behavioral verification: " + expectedSummary, header)
            Assert.DoesNotContain("class=\"status\">Readiness:", header)
            Assert.Contains("does not gate commit", header))
