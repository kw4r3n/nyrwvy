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
    /// A closed observation policy: only mapped, recognized program/fake failures are
    /// counterexamples. Resource exhaustion, cancellation and unrecognized exceptions
    /// remain unknown, as do all runner, compiler, process and validation diagnostics.
    let classify isConcurrent expectedCandidate before after =
        let concreteFailure (failure: PureRunRuntimeFailure) =
            let recognized =
                match failure.ExceptionType with
                | "FakeCapabilityException" | "FakeArgumentsUnmatchedException" | "FakeResponsesExhaustedException"
                | "System.ArgumentException" | "System.ArgumentOutOfRangeException"
                | "System.InvalidOperationException" | "System.DivideByZeroException"
                | "System.OverflowException" | "System.IndexOutOfRangeException" | "System.NullReferenceException" -> true
                | _ -> false
            recognized && (failure.Frames |> List.exists (function PureRunFrame.Function id -> id = failure.FunctionId | _ -> false))
        if isConcurrent then "unknown"
        else
            match before, after with
            | Ok old, Ok fresh ->
                if old.Calls <> fresh.Calls then "counterexample"
                else
                    match expectedCandidate with
                    // runPure recursively canonicalizes expected Product/Union/container values.
                    // Compare its validated match, not the caller's raw field ordering.
                    | Some _ -> if fresh.Matches = Some true then "intended-change-tested" else "counterexample"
                    | None -> if old.Actual = fresh.Actual then "tested-agreement" else "counterexample"
            | Ok _, Error(PureRunError.RuntimeFailure failure) when concreteFailure failure -> "counterexample"
            | _ -> "unknown"

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
                            context.Project functionId
                            |> Result.map (fun program ->
                                // Retained declarations can need NyTask classes without this entry
                                // performing any concurrent operation. Classify its reachable instructions,
                                // not the backend's runtime-emission flag for all declared field types.
                                program.Functions
                                |> List.collect ExecutionCallGraph.instructions
                                |> List.exists (fun instruction ->
                                    ExecutionInstructionKind.uses instruction.Kind
                                    |> List.exists (function
                                        | ExecutionInstructionUse.TaskRuntime
                                        | ExecutionInstructionUse.ConcurrencyRuntime -> true
                                        | _ -> false)))
                            |> Result.defaultValue true
                        let observations = cases |> List.map (fun item ->
                            let before = PureRun.runContext baseline item.FunctionId item.Arguments None
                            // Preserve the actual observation even if the candidate result type changed.
                            let after = PureRun.runContext candidate item.FunctionId item.Arguments item.ExpectedCandidate
                            let status = classify (concurrent baseline item.FunctionId || concurrent candidate item.FunctionId) item.ExpectedCandidate before after
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
