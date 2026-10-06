namespace Nyrwvy.Tools.AuthoringSession

open Nyrwvy.Core

/// Conditions are supplied by the caller and never inferred from candidate output.
type ChangeVerificationCase =
    { Label: string
      FunctionId: FunctionId
      Arguments: PureRunValue list
      /// None preserves baseline behavior; Some requires this explicit new result.
      ExpectedCandidate: PureRunValue option }

type ChangeVerificationObservation =
    { Case: ChangeVerificationCase
      Status: string
      Baseline: Result<PureRunResult, PureRunError>
      Candidate: Result<PureRunResult, PureRunError> }

type ChangeVerificationEvidence =
    { ProposalId: string
      BaselineSnapshotId: SnapshotId
      CandidateSnapshotId: SnapshotId
      BaselineFingerprint: string
      CandidateFingerprint: string
      AffectedFunctionIds: FunctionId list
      Observations: ChangeVerificationObservation list }

[<RequireQualifiedAccess>]
module ChangeVerification =
    /// Computed evidence: no successful cache can outlive an edit, reject, close, or reopen.
    let verify (session: AuthoringSession) proposalId cases =
        let fail message = Error(AuthoringSessionError.DependencyFailure message)
        if List.isEmpty cases || List.length cases > 32 then fail "Verification requires 1 to 32 explicit cases."
        elif cases |> List.exists (fun item -> System.String.IsNullOrWhiteSpace item.Label) then fail "Every verification case needs a nonblank condition label."
        else
            session.ExecutionContext None
            |> Result.bind (fun baseline ->
                session.ExecutionContext(Some proposalId)
                |> Result.bind (fun candidate ->
                    session.PendingProposalView proposalId
                    |> Result.bind (fun view ->
                        // Conservative caller closure across both graphs; no second diff engine.
                        let functions = baseline.Declarations.Functions @ candidate.Declarations.Functions
                        let rec expand affected =
                            let callers = functions |> List.choose (fun fn ->
                                let calls = ValueGraph.collectNodes fn.Body |> List.exists (fun node ->
                                    SemanticNodeShape.functionReferences node
                                    |> List.exists (fun reference -> Set.contains reference.FunctionId affected))
                                if calls then Some fn.Id else None)
                            let next = Set.union affected (Set.ofList callers)
                            if next = affected then affected else expand next
                        let affected = view.FunctionIds |> Set.ofList |> expand |> Set.toList
                        let concurrent context functionId =
                            context.Project()
                            |> Result.map (fun program -> CSharpBackend.usesConcurrencyRuntime (ExecutionCallGraph.forEntry program functionId))
                            |> Result.defaultValue true
                        let observations = cases |> List.map (fun item ->
                            let before = PureRun.runContext baseline item.FunctionId item.Arguments None
                            let expectation = item.ExpectedCandidate |> Option.orElseWith (fun () -> before |> Result.toOption |> Option.map (fun result -> result.Actual))
                            let after = PureRun.runContext candidate item.FunctionId item.Arguments expectation
                            let status =
                                match before, after with
                                | Ok old, Ok fresh ->
                                    let callsAgree = old.Calls = fresh.Calls
                                    let valuesAgree = old.Actual = fresh.Actual
                                    if concurrent baseline item.FunctionId || concurrent candidate item.FunctionId then "unknown"
                                    elif not callsAgree then "counterexample"
                                    else match item.ExpectedCandidate with
                                         | Some _ -> if fresh.Matches = Some true then "intended-change-tested" else "counterexample"
                                         | None -> if valuesAgree then "tested-agreement" else "counterexample"
                                | _ -> "unknown"
                            { Case = item; Status = status; Baseline = before; Candidate = after })
                        // Detect source changes during the bounded child runs before reporting evidence.
                        session.VerifyPersistedSource()
                        |> Result.map (fun () ->
                            { ProposalId = proposalId
                              BaselineSnapshotId = baseline.SnapshotId
                              CandidateSnapshotId = candidate.SnapshotId
                              BaselineFingerprint = baseline.Fingerprint
                              CandidateFingerprint = candidate.Fingerprint
                              AffectedFunctionIds = affected
                              Observations = observations }))))
