# Semantic change verification experiment

**Status:** Experimental

Branch: `experiment/semantic-change-verification`, based on remote main
`c65d5ad92cdb1f78a3623c02f828e7b0a198da81` (OneShot #362).

## Design and supported observations

`verifyChange` computes evidence for a pending structural proposal before commit.
It uses the existing snapshot, typed semantic IR, lowering, C# backend, bounded
`runPure` child process and scripted fake capabilities. It never publishes the
candidate. There is no new specification language or execution runtime.

A caller (including an AI concretizing the user's specification) supplies named
cases with a stable function ID, inputs and optionally `expectedCandidate`. An
absent expectation requests preservation of the baseline result and effect calls.
An explicit expectation requests that new result while preserving effect calls.
Expectations are inputs; they are never generated or rewritten from candidate
output. Changes to effect behavior require a future explicit observation contract;
this prototype does not silently allow them.

Successful sequential runs compare canonical returned values and complete ordered
fake call histories (capability parameter identity, operation identity, arguments).
Explicit expected results retain runPure's mismatch paths. Preservation compares
actual results directly, so a changed result type remains observable. Float64 follows existing
canonical NaN and signed-zero semantics. A differing observation is a
`counterexample`; matching observations are `tested-agreement` or
`intended-change-tested`. These labels apply only to supplied cases, not the full
input domain. No case is a proof.

Policy v2 reports a `counterexample` when the baseline succeeds and the candidate
raises a recognized program/fake failure with mapped semantic frames. The closed
exception list includes fake throw/unmatched/exhausted responses and ordinary
argument, invalid-operation, divide-by-zero, overflow, index and null-reference
exceptions. This is a difference under the supplied scripts, not evidence that
the scripts describe every valid environment. A runtime error alone does not
establish determinism or universal regression.

Unmapped or unrecognized exceptions, resource exhaustion (including stack/heap),
cancellation, unsupported inputs/projections, compiler/harness/process diagnostics,
timeouts and all concurrent/task executions remain `unknown`. Baseline failure
and failure on both sides remain unknown. Full run errors remain in both observations. Exceptions are not treated as successful agreement even if
both executions fail identically. Concurrent traces need a partial-order/allowed
observation policy before they can support either agreement or a counterexample.
The existing runner performs its usual bounded process execution and cleanup.

## Revision and dependency validity

Evidence records both snapshot IDs and full graph fingerprints. Full fingerprints
conservatively cover resolved dependency declarations, including unrelated edits.
Fingerprints identify content; they do not establish behavioral equivalence.
The session checks persisted source/dependency validity before execution and again
before returning evidence. A proposal with a different committed base is rejected.
Rejected/closed/reopened proposal handles cannot be verified.

Evidence is computed, not registered. Editing requires a new proposal and new
verification. Saved HTML/JSON is historical evidence with explicit identities,
never consulted by commit and never presented as current readiness. This avoids a
new persisted evidence schema and invalidation registry. The cost is rerunning
cases after edits/reopen. Commit is not automatically gated by this experimental
operation. No evidence survives as an authoritative cached success.

Affected functions are the proposal's touched functions plus transitive callers
in both graphs, using existing semantic function references (including callbacks).
Impact is conservative; supplied cases do not necessarily cover every affected
function. The response does not claim overall verified readiness.

## Protocol and review

Use the maintained `nyrwvy_request` JSONL surface, with exact IDs:

```json
{
  "id": "verify-1",
  "op": "verifyChange",
  "proposalId": "<pending proposal ID>",
  "cases": [{
    "label": "increment 10 preserves 11",
    "functionId": "<function ID>",
    "arguments": [{"kind": "int64", "value": "10"}]
  }],
  "output": "reviews/increment.html"
}
```

For a bug fix, add `"expectedCandidate": {"kind":"int64","value":"12"}`
to the relevant case, with a label explaining the intended property. The response
contains `evidence` and `reviewPath`. The generated Change Review combines the
existing structural proposal page with escaped, human-readable evidence JSON.
Output confinement reuses the existing ViewerFile writer. Default output uses
the existing proposal review path; rerunning overwrites that historical page.

Candidate execution currently supports proposal kinds that expose a proposed
snapshot (body/target edits, creation and migrations). Operations proposals are
explicitly unsupported. Only fake capabilities are accepted; host C# is excluded.
The stable-ID wire format can represent new candidate-only entry points, but they
have no runnable baseline and therefore receive unknown. Name-resolved verify
input and changed-effect expectations are deferred.

## Acceptance and cost

```sh
dotnet build tools/Nyrwvy.AuthoringSession --configuration Release
python3 scripts/experimental/semantic-change-verification.py
# Retain snapshots, evidence JSON and review HTML in an empty directory:
python3 scripts/experimental/semantic-change-verification.py artifacts/semantic-change-verification
```

Executable examples cover a safe arithmetic refactor, a changed result, explicit
intended result, wrong intended result, transitive caller impact, invalid inputs,
timeout, differing fake-call arguments with identical results, failing fakes,
rejection preserving exact baseline bytes, stale/reopened handles, and persisted
commit save/reopen. Existing PureRun tests cover the shared runtime/value surface.

Cases are bounded to 1–32, each running baseline then candidate. Each execution
inherits runPure's 30-second build and 5-second execution limits. There is no
whole-request timeout, so large suites can be slow. Existing generated build cache
is reused. Fingerprinting and full-graph projection are conservative and may cost
more on large graphs. Impact has no second diff engine but is computed by a simple
caller fixed point. No dependencies were added.

Before adoption: test larger real refactors/bug fixes, improve the review's case
presentation, measure latency, decide whether reviewed conditions should live in
existing snapshot/storage contracts, and design explicit changed-effect and
concurrent observation policies. This experiment does not claim general language
equivalence, cleanup/cancellation equivalence, or bounded proofs.

Initial validation before independent review: build succeeded with zero warnings/errors;
all 41 filtered PureRun regression tests passed; the graph-tools adapter test
passed; every executable acceptance assertion passed. Initial retained evidence is
under `artifacts/semantic-change-verification-final/evidence.json`, with a separate
HTML review for each observation. These generated artifacts are local and ignored
by Git. rrawrlet tools were absent from this task's callable catalog; no provider
job was launched. Original checkout and its untracked verification-planner were
left intact. No push, PR, merge or release was performed.


## Audit boundaries

The maintained fast regression file `ChangeVerificationTests.fs` rejects empty,
oversized and blank-label suites, rejects wire-submitted forged evidence, and
checks source and dependency-selection invalidation without publication. The
executable acceptance script additionally changes fake return values and explicit
observations on the same proposal (forcing fresh execution), reverses sequential
fake calls with the same returned value, and checks concurrent agreement remains
unknown. It writes distinct HTML pages for these comparisons.

Full graph fingerprints and base-snapshot identity intentionally invalidate all
proposal evidence after any committed edit, even an unrelated definition. This
costs extra reruns but avoids a dependency-selective cache with an incomplete
closure (types, capability declarations, callbacks, bindings, observation policy).
The caller closure is an impact view, not a cache key. Fake scripts and requested
expectations are included in evidence and recomputed on every invocation; the exact wire case JSON and its SHA-256 digest are also recorded. There
is no signature or external evidence authority. Identical graphs with changed
fakes or observations can therefore produce fresh, different evidence without
changing the graph fingerprints. Saved HTML/JSON is editable and unauthenticated;
it is never accepted by verifyChange or commit as evidence authority.

An embedded dependency's original source path is provenance. Editing that source
does not alter the pinned embedded graph; editing the selection manifest, actual
legacy pinned source, or committed graph invalidates the session/proposal. Testing
dependency upgrades against old behavior is not supported by this slice because
upgrade previews do not expose the structural candidate contract used here.

Equality covers ordered fake-operation calls and canonical argument/result values,
not compiler allocations, duration, internal state, unused script responses,
or arbitrary environment behavior. Capability parameter identity distinguishes
independently supplied environments. Stable operation/parameter IDs are required;
a signature migration can produce a conservative counterexample or unknown.
Only one run per side/case is performed; sampled agreement cannot rule out latent
nondeterminism. Programs using the known task/concurrency runtime are always
unknown. Failure/cleanup/cancellation behavioral equivalence remains deferred;
a failure, timeout, unsupported baseline, or failed candidate cannot pass even
with an explicit expectedCandidate. No proof or automatic approval is issued.

Reopening a saved report supports historical inspection, not revalidation or
restoration of proposal handles. Conditions can be retained in JSON/script by the
caller; a reopened workspace requires a fresh proposal and rerun. A persisted
condition/evidence contract and commit gating are intentionally deferred.


Each wire report includes the exact submitted `caseRequest` JSON, `casesDigest`
(SHA-256 of that UTF-8 JSON), verifier/observation-policy version, .NET runtime
version, existing generated-build context fingerprint, and compiled module IDs
for authoring, semantics, execution, and backend. These distinguish historical
runs using different code, policies, fakes or conditions. A digest is not a
signature; editable reports remain unauthenticated and cannot authorize commit.
The prominent review heading separates structural readiness from behavioral
counterexample/unknown/tested evidence, so structural validity does not imply a
behavioral pass. The summary always includes case-status counts and no-proof scope.

Review-fixed audit acceptance evidence is retained under `artifacts/semantic-change-verification-review-fixed/evidence.json`. It includes separate counterexample, unknown, tested, effect-order and concurrency review pages with prominent behavioral summaries. Final aggregate counts are reported for the frozen commit in the task validation report.

### Review follow-up: observation policy v2

Maintained xUnit tests exercise the classifier directly across result agreement,
explicit intent, different results/types, changed call arguments/order, asymmetric
and bilateral failures, recognized/mapped exception boundaries, cancellation,
resources, unsupported/timeout/process errors and concurrent entries. An extended
integration regression executes a candidate's duplicated fake call and checks
that exhaustion is a counterexample without publishing or changing saved bytes.

Change Review displays affected function count separately from unique explicit
case entry points and how many affected functions have direct cases. These are
requested entry points, not dynamic coverage: callers without direct cases may
remain unexercised, and a directly targeted entry may still be unknown. Wire
reports include `caseEntryFunctionIds`; historical v1 reports keep their original
identity and meaning. The policy/module identity changes for v2; no old report is
silently upgraded. Commit remains independent of this opt-in evidence.
