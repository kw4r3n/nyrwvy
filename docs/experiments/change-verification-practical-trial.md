# Practical batch change-verification trial

**Status:** Observed result

This is a local trial of the [one-shot verifier](semantic-change-verification.md),
using the existing `config-request-batch/v2` graph. It is not adoption of a durable
verification workflow. Requirements, observations, nine sampled inputs, two held-out
inputs and three synthetic negative controls were committed in `eae9cd8` before
candidate authoring. See the [executable trial](../../examples/change-verification-batch/README.md).

## Real changes and observations

1. A behavior-preserving refactor changes `missingLine` from nested StringConcat
   to an empty-separator StringJoin over its five pieces. Its saved graph has 8
   nodes versus the baseline's 9; this is a modest graph/readability simplification,
   not a measured application-speed improvement. Function/parameter identities
   and pinned dependency bytes remain unchanged. Nine differential cases agree;
   nine independent ordinary expected-value assertions and the existing five
   full-batch saved cases pass.
2. A new report requirement escapes embedded CR/LF in the request/error pieces
   to visible `\r`/`\n`, preserving other characters, delimiters and final LF.
   This is an intentional improvement, not an invented historical defect. Default
   preservation checks report 4 counterexamples and 5 agreements. Supplying the
   prewritten intended results yields 9 intended results tested. The real batch
   pipeline, given the original frozen `a\nb` request and a missing response,
   saves one physical line instead of two; its summary and close/disposal checks
   pass. The existing five saved cases also pass unchanged. `BatchSummary.failed`
   still retains the original request string; only the text report is escaped.

The new formatter has 24 graph nodes versus the refactor's 8: explicit escaping
has implementation and allocation cost. Reversibility, Unicode line separators,
and transformation of the original request used for file lookup are not requested
or claimed. Literal backslashes and non-CR/LF Unicode are preserved.

The impact closure identifies seven functions (formatter and six transitive
callers), but differential evidence directly covers only the formatter. A
full-batch comparison with real host bindings is explicitly unknown. Ordinary
host-backed saved tests and the focused acquisition regression cover broader
execution/cleanup; they are not upgraded to equivalence claims.

## Synthetic negative controls

| Control | Structural checks | Sampled verifier | Ordinary existing saved tests | Held-out challenge |
| --- | --- | --- | --- | --- |
| Wrong `missing ` prefix | Accept valid graph | 9 counterexamples | Expected report assertion fails | Not needed |
| Omit error LF escaping while preserving CR escaping | Accept valid graph | 2 counterexamples, 7 intended results tested | Existing fixture errors are plain `missing`; no boundary coverage | Boundary strings already in sampled suite |
| Change only `delta-outside` request output | Accept valid graph | All 9 intended results tested; defect missed | All 5 pass; defect missed | 1 counterexample, 1 agreement |

The deliberately missed control violates the global requirement but agrees with
sampled observations. It demonstrates that the verifier cannot invent missing
cases or prove a universal property. The held-out inputs were fixed beforehand;
expectations were never rewritten to fit outputs. An intended change reported
under preservation mode is a property mismatch, not a verifier false alarm.
No unexplained false alarms or verifier runtime defects were observed.

## Concrete burden and timing

Two behavior requirements, five observation categories, nine sampled inputs and
two held-out inputs were chosen. One existing sampled input became an additional
host scenario. The same inputs feed baseline assertions, differential checks,
explicit changed-result assertions and held-out challenges. Expected strings are
computed by a native JavaScript oracle defined from the frozen requirement before
proposals, not manually copied from candidate results. No new fake-capability
scripts or fixture programs were needed for this pure target.

The runner explicitly records six judgment decisions. The custom script is about
220 lines, chiefly bundle handling, ordinary replay, evidence assertions and
negative-control isolation. This is real integration overhead, not a general
verification framework. Authoring/elicitation person-time was not measured and
no productivity gain is inferred.

In the two completed exploratory executions, nine-case verifier stages took
roughly 1.2–5.5 seconds on this machine, including projection, compilation/cache
and child-process launch. A two-case held-out stage took about 0.3 seconds. Ordinary
nine-case pure assertions varied with cache/startup; whole-batch five-case replay
also varied, with a cold/setup-heavy run substantially slower. Detailed measured
step times are retained in each `results.json`. These are command wall times,
not a controlled benchmark or evidence that the refactor runs faster. Initial
v2 bundle generation/authoring and person-time are separate, unmeasured costs.

The ordinary formatter checks require local publication before runPure; the
verifier supplies before/after feedback while the original saved graph remains
intact. Running ordinary tests on a candidate still works via an isolated copied
bundle and graph commit. The verifier does not replace their broader resource,
exception or cancellation assertions.

## Recommendation

Keep this as an experimental review aid for bounded pure/sequential changes.
It usefully reveals exact observed changes before publication, distinguishes
explicit intended results from preservation, and names transitive impact. It
adds little assurance beyond well-chosen existing expected-value tests on the
same cases. The coverage-gap control and whole-program unknown are decisive
limits for this real example.

Do not adopt it as a general semantic approval gate yet. Reuse the small domain
case set and existing runPure/host tests; measure another effectful sequential
program before extending policy. A lightweight conversion from existing
supported case files may be useful later, but this trial does not justify a
new condition registry, runtime, scheduler or concurrency prover. No verifier
runtime changes were required. Inspection also aligned the LF-only negative control
with its frozen requirement; exploratory runs had omitted both CR and LF. The runner was corrected to refresh its copied
working projection as well as archived stages, preventing an exported C# file
from appearing current after the graph changed.

Final hashes, retained bundles, transcripts, exact timings and final touched-area
checks are provided in the task's validation report. Nothing was pushed, merged
or applied to the original checkout.
