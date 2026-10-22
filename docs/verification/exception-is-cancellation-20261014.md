# Generic exception cancellation classification — 2026-10-14

`ExceptionIsCancellation(Exception) -> Bool` is a pure generic primitive. Its C#
projection is exactly `edi.SourceException is System.OperationCanceledException`.
Catch still captures OCE. Selective handlers use ordinary Conditional and
ExceptionRethrow to propagate cancellation and recover ordinary faults. No
Rrawrlet-specific primitive or task/cleanup runtime policy was added.

## Implementation and persistence

The fresh isolated checkout is `/home/kw4/Documents/Codex/2026-10-14/task/nyrwvy`,
branch `feat/exception-is-cancellation`, based exactly on PR391 main
`9e56e835e0978d539f41778f8e2149c8c4b2b279`. The prior checkout at
`/home/kw4/Documents/Codex/2026-10-13/task-17/nyrwvy` stays clean at
`46110d3701bfb897a7e0083f7afcaab51f7e56eb`. No previous work was modified.
Home/ancestor instructions and repository instructions were inspected; available
`.agents/skills` contain no applicable Nyrwvy workflow. Commands run via Nushell.
Installed locked dependencies and restore assets were copied locally; source was
rebuilt in the isolated checkout. No dependency declaration changed.

The unary node crosses semantic shape/dependency/type mapping, symbolic and exact
recipes, inference, full validation, parameter migration, JSON/Nyason codecs,
typed IR, execution lowering/analysis/serialization, C# emission and viewer labels.
Invalid operands are rejected before publication and independently by full
validation. The classifier is intrinsically pure. Exception remains opaque,
non-equatable and prohibited at capability/run-value boundaries.

Snapshots containing the predicate require JSON v17 / Nyason v13 and both
inspection IRs use v12. Existing Exception-only graphs retain JSON v16 / Nyason
v12 and IR v11; baseline graphs retain their existing bytes/minimum version.
Tests exercise both codec round trips, downgrade refusal, reopen and identical
emission. The viewer renders “exception is cancellation”. A supplementary ordinary
tool probe accepts the exact parameterSlot recipe and reads the classifier through
the normal function-reader projection without changing the saved graph.

## Compiled generic proof

Normal create/review/commit authoring builds the generic fixture. Saved/reopened
source and symbols match. Same-path and copied-path replay preserve graph,
source, symbols and virtual cases. Offline installed Roslyn compiles Generated.cs
unedited; its hash is checked before and after execution, including the moved
bundle.

Thirty-seven real C# controls cover:

- Ordinary InvalidOperationException with a cancellation message, under clear and
  cancelled ambient tokens; ordinary wrapper with an OCE InnerException.
- OCE with absent, clear, cancelled and other tokens; TaskCanceledException and a
  real subclass, each under clear/cancelled ambient tokens.
- Pure classification and selective recovery, original identity and throwing host
  stack, unchanged ambient tokens, and unchanged broad Catch recovery of OCE.
- Authored predicate inversion, constant true and constant false handlers compiled
  with the same fixture: 48 altered-policy observations, of which 32 differ from
  the production acceptance contract and are rejected by its expectations.
- Ok, typed Err and Abort bypass; context faults outside the boundary; ordinary
  and OCE body faults competing with release; ordinary and OCE original faults
  competing with handler cleanup faults, direct TaskCancel/parent cancellation,
  shield restoration, drainage and once-only release.

The host creates actual exception objects. Virtual fake throws only establish
ordinary exception behavior. Virtual and real share the emitter/runtime and are
not independent oracles.

## Opt-in snapshot composition and actual Python

The default `containSnapshotFaults` remains false. Only the opt-in snapshotFault
handler changes: it rethrows cancellation and returns Err(ExceptionMessage) for
ordinary failures. Snapshot invocation is the entire protected body; commit
publication, checks, Member cleanup and outer release remain outside. Instrumented
graphs grow from 125 functions / 2,748 nodes to 127 / 2,760: two functions and
twelve nodes.

The actual Python oracle is unchanged and pinned to
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f` at
`/home/kw4/Documents/Codex/2026-10-12/task-12/rrawrlet-oracle`. The oracle validates
HEAD and exact team.py/codex.py/runner.py/team_tools.py bytes against Git before
import. Actual execute, wait, inspect, stop, close, lock and await_cleanup run;
only external provider/Git/check/save effects are synthetic. All 11 actual
contracts pass. Retained oracle source hashes are in the fixture and closure.

The comparison retains 9 baseline RED assertions, 33 protected-mode passes,
18 unchanged controls and 19 record-mutation controls. Ten real scripts agree
with their virtual observations. The explicit cancellation script is compared
separately because a virtual fake throw is ordinary: real OCE escapes both
baseline and opt-in, while the fake is recovered by the opt-in handler.

A supplementary real compiled probe invokes the same emitted memberSettlement
callback through the existing NyTaskScope. It explicitly joins before reading
actual phase/commit cells: the phase remains completed, commit is absent, no
Final returns, and the task is faulted with the original clear-token OCE
(IsCanceled is false). Scope closure propagates the same object; the task and
provider are drained before once-only release. This is a task/phase observation,
not a new post-scope registry API. In the normal controller invocation, wait
propagates OCE and no retained Final view is produced.

Actual Python instead reports a cancelled Member task, retains completed status
with no commit/checks/final save, and drains before release. Task cancellation,
retained outcome and drainage are separate facts. Ordinary failures retain failed
status; Final still has no error-text field, so Python diagnostics are not copied.
Raw checks faults, release priority and ordinary-fault containment remain as in
the prior bounded proof. No whole-shutdown parity claim is made.

## Verification and independent-review checkpoint

Focused semantic tests passed 54 checks before aggregate verification. Final
serial suite results, commands, local log/artifact hashes, oracle proofs and
preserved checkout identities are in the [current closure](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-snapshot-faults/fixtures/cancellation-closure.json).

| Final serial check | Result |
| --- | --- |
| Release solution build, no restore | Zero warnings/errors |
| .NET fast / extended | 864 / 154 (123 Core + 31 Extended) |
| Adapter / Node / MCP / Bun | 8 / 46 / 12 / 23 |
| Adapter and Bun typechecks | Both pass |
| Partition / fish contract and syntax / whitespace | Pass |

The generic [runtime expectations](../../examples/exception-boundary/runtime-expected.json)
and [real snapshot observations](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-snapshot-faults/fixtures/runtime-observations.json)
are retained for inspection. Local complete transcripts/bundles are under
`artifacts/cancellation-predicate/`.

VSTest initially required loopback sockets blocked by the filesystem sandbox;
expanded execution was approved. A supplementary harness initially expected a
cancelled C# task; actual execution disproved that expectation, which was
corrected to retain the existing faulted-task semantics. Both failed diagnostic
logs remain hashed in the closure. The first Node aggregate passed 45 of 46 tests;
the existing owner-cancellation comparison observed a peer as running rather than
stopped in a view taken before an explicit peer join. The focused rerun passed.
The preserved prior authoring binary produces byte-identical C# for the default
snapshot graph, and prior/base source trees agree on the relevant source and owner
fixture. This supports a timing-sensitive existing probe, not an established
new semantic regression. No owner policy or comparison assertion was changed.
The full Node aggregate rerun passed all 46 tests; both outcomes remain in the closure.
Nushell expanded the Bun glob without its leading `./`, selecting no tests;
explicit quoted paths then passed all 23 tests. That selector failure remains
hashed too. No automatic approval review rejected an action.

The exact tested local commit/tree is returned to the parent for independent
review. Independent review remains pending; there is no reviewer acceptance
claim. No feature branch push occurs before parent review confirmation. Parent
handles PR/merge. No live provider, credential/access changes, deletion, force
push or merge occurred.

Queued-handler protection, bare-owner shutdown, atomic concurrent check cutoff,
arbitrary fault classes/interleavings, physical Git recovery, provider shutdown
and restart persistence remain excluded. Existing registry/ticket storage bounds
are unchanged. The compiled mutants test this bounded handler policy; record
mutants do not establish general schedule coverage.
