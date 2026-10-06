# Semantic change verification experiment

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
Preservation comparisons reuse runPure's mismatch paths. Float64 follows existing
canonical NaN and signed-zero semantics. A differing observation is a
`counterexample`; matching observations are `tested-agreement` or
`intended-change-tested`. These labels apply only to supplied cases, not the full
input domain. No case is a proof.

Failures, exhausted fakes, unsupported inputs/projections, process limits and all
concurrent/task/cancellation executions are `unknown`. Full run errors remain in
both observations. Exceptions are not treated as successful agreement even if
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
dotnet build tools/Nyrwvy.AuthoringSession
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

Validation in this isolated clone: build succeeded with zero warnings/errors;
all 41 filtered PureRun regression tests passed; the graph-tools adapter test
passed; every executable acceptance assertion passed. Final retained evidence is
under `artifacts/semantic-change-verification-final/evidence.json`, with a separate
HTML review for each observation. These generated artifacts are local and ignored
by Git. rrawrlet tools were absent from this task's callable catalog; no provider
job was launched. Original checkout and its untracked verification-planner were
left intact. No push, PR, merge or release was performed.
