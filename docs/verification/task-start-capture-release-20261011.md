# TaskStart capture lifetime repair — 2026-10-11

Base: `90e495c75dd6915440ea22c96222aefcf04f723e`.
Status: locally verified repair prepared for review; merge is a separate decision.

## Finding and repair

The original TaskStart emitter made fresh block-scoped context/item snapshots,
then held those values in a worker callback for its entire lifetime. The moving
cursor probe retained 4/4 and 256/256 completed links although only two unresolved
tails remained in the owner registry. Heads collected after settlement/closure.

The first repair copied snapshots into callback-local arguments and cleared the
closure fields before invoking the worker. It still failed under default tiered
JIT: both original heads remained at length four, with valid GC controls. Disabling
tiering made this candidate pass both lengths, while the original emitter still
retained both chains. This diagnostic was not accepted as a fix and does not
establish an exclusive managed root path.

The verified repair instead emits a private ref-parameter overload only for
functions used by TaskStart. Fresh per-start snapshot slots are passed directly
by ref. The overload copies all values into the existing mutable execution locals,
clears all caller-owned slots, then executes the same worker body. No callback
by-value argument locals or by-value worker-entry parameter copies carry the old
cursor. Ordinary public by-value signatures and their symbol names stay unchanged.
No JIT override, scheduler/runtime semantic change, new primitive or source-graph
change is required. Per-start capture isolation and exactly-once starts, including
late starts against a closed scope, remain existing behavior.

The tradeoff is an extra emitted private body for each TaskStart target. This
bounded implementation cost is not a new public callable or a generic closure
facility. Other graphs can still deliberately retain history; this is not a
universal heap bound or a complete attribution of every possible JIT root.

## Verification reported by the local executor

- Clean Release build passed.
- Source/compiler-helper checks: 5/5.
- Same saved graph reemitted: six functions and 81 nodes.
- Reopened saved graph replay: 2/2.
- Default-JIT C# retention cases: 4 and 256 advances per worker passed.
- In each live case: original heads absent (`-1,-1`), both workers live, owner
  open, unresolved registry exactly two before and after collection.
- Rooted object survived; unrooted object collected. Settlement/closure controls
  also passed while keeping the owner object alive.
- Unedited emitted projection checked; graph fixtures, public signatures and
  symbol manifest unchanged.
- TaskScope tests including Extended cases: 7/7, covering capture isolation,
  cancellation/cleanup, exception propagation and late closed-scope starts.
- Maintained fast core tests: 851/851; Node contracts: 32/32.

The maintained fast emitter regression checks ref arguments on TaskStart calls
and snapshot copy-before-clear in private worker-entry prologues. Runtime GC
acceptance remains necessary; source patterns alone cannot establish collection.
Context-bearing workers are exercised by the TaskScope fixture and cursor graph.
No separate generic type-parameter worker case was identified in those seven
TaskScope tests; a dedicated generic-worker emission case was not claimed.

The cloud executor prepared the patch and independently reviewed it. Its direct
checks were Node syntax and compiler-helper tests (3/3); its partial checkout
lacked the dependencies needed to start the source tests and had no .NET SDK.
The full Extended suite and broader JIT/platform matrix were not run.

## Evidence

The local verifier reported a 261233-byte v3 evidence archive, SHA-256:

`1acd1ea1122d410973f568dcf57b781c42800ca9a835babcd863dc3dbb26ddc3`

Its report is `REPORT-V3.ja.md` within the capture-release validation evidence.
The earlier failed-candidate evidence archive was 162119 bytes, SHA-256:

`55fbc288ea4f34c33d3023d21aa85c242fd08482d0580f276d376b45f745853e`

These records summarize local reports; they do not claim cloud runtime execution.

## Stopping point

The minimal retention repair and its regression are verified within the stated
boundary. No Channel or full streaming protocol is included in this repair. A future bounded-stream example still needs separate
admission, slow-sink backpressure, drain/cancellation and delivery verification.
