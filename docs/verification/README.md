# Verification records

**Status:** Current

This area holds evidence from completed implementation and evaluation work. A record states what was observed at a recorded revision; it does not by itself define current behavior. For current contracts, use [`../specs/`](../specs/) and [`../reference/`](../reference/).

## Milestone 1

- [Float64 implementation contract](../../docs-archive/2026-08-pre-reorg/m1-float64-implementation-notes-20260828.md) (archived)
- [Agent evaluation initial tranche](m1-agent-evaluation-initial-tranche-20260829.md)

## Milestone 2 — Float64 structural editing

- [Float64 structural editing](m2-float64-structural-editing-20260830.md)
- [Structural contract coverage extension](m2-structural-contract-coverage-20260830.md)
- [Structural agent-eval protocol v1](m2-structural-agent-eval-v1-20260830.md)
- [Agent corpus v1 freeze](m2-agent-corpus-v1-freeze-20260830.md)
- [Controlled evaluation condition v1](m2-controlled-evaluation-condition-v1-20260830.md) and its [runtime failure](m2-controlled-evaluation-condition-v1-runtime-failure-20260830.md)
- [Controlled evaluation condition v1.1 preflight](m2-controlled-evaluation-condition-v1.1-preflight-20260830.md)
- [Controlled structural agent evaluation](m2-controlled-structural-agent-evaluation-20260831.md)

## Milestone 2 — specialized ListMap

- [ListMap structural editing](m2-listmap-structural-editing-20260831.md)
- [ListMap structural agent protocol v1](m2-listmap-structural-agent-protocol-v1-20260831.md)
- [ListMap agent corpus v1 freeze](m2-listmap-agent-corpus-v1-freeze-20260831.md)
- [ListMap controlled condition v1 preflight](m2-listmap-controlled-condition-v1-preflight-20260831.md)
- [Controlled ListMap structural agent evaluation](m2-listmap-controlled-structural-agent-evaluation-20260831.md)

## Expression and declaration authoring

- [Ordered expression binding](ordered-expression-binding-20260908.md)
- [Snapshot-backed authoring session (M3)](m3-authoring-session-20260915.md)
- [Product/union declaration authoring (M4)](m4-product-union-authoring-20260915.md)
- [Effect declaration authoring (M4)](m4-effect-authoring-20260915.md)
- [Capability and operation declaration authoring (M4)](m4-capability-authoring-20260915.md)

- [Program-defined capability implementations](program-capability-implementations-20260926.md)
- [Typed function abort and cleanup](typed-function-abort-20260926.md)
- [Serialized integration error boundary milestone](integration-error-boundary-milestone-20261012.md): 51 scenarios / 771 audits against pinned Python; finite coverage matrix, baseline pass for remaining check/apply/reverse faults, and publication blocked
- [Bounded concurrent request admission](bounded-request-admission-20261013.md): six runs from five gated scripts; task/cell ownership composition keeps wait/stop independent and publishes close cutoff before drain; local branch only
- [Argument-keyed fake responses](argument-keyed-fakes-20260927.md)
- [Fake-value diagnostic paths](fake-value-diagnostics-20261011.md): wire and semantic errors in `runPure` fake responses name the operation, case, response and value path without echoing values; acceptance rules and error categories unchanged
- [Nested pattern-reference guidance](nested-reference-guidance-20261011.md): scope diagnostics and the `patternReference` lookup now say to choose the frame that binds the intended value and that empty None frames count; no new lexical rule
- [runPure Release build, Roslyn build and early StringSlice](runpure-release-build-20260930.md)
- [runPure arguments as input and the build cache](runpure-build-cache-20261001.md): repeated calls of one entry reuse the built program (about 75 ms instead of 213 ms per call)
- [Lone surrogates in runPure String values](runpure-lone-surrogates-20261001.md): any UTF-16 sequence, as JSON `\uD800` escapes in both directions
- [Amortised List append](list-amortised-append-20260930.md)
- [Amortised String concatenation](string-amortised-concat-20261001.md): `String` is emitted as `NyStr`, so a `StringConcat` fold is linear (3.12 MB in 1.24 s where 195 KB timed out)
- [Code-point position offsets for String operations](string-position-offsets-20260930.md)
- [Bits32](bits32-20261001.md): the fixed-width bit domain, and probe 12's SHA-256 re-authored with it (NIST vectors, about 20 times the Int64 port's throughput, 17.5 MB per run)


## Live agent probes

Most probes in this section are *porting probes*: a slice of an existing program was re-authored in Nyrwvy through the AuthoringSession protocol and its outputs were compared case by case with the original. The usual subject, **rrawrlet**, is a separate Python application by the same author (an OpenCode-based agent-team tool) chosen because it is real, unrelated code; its functions are named in the records for orientation only. The probe scripts (`author.py`, `mkcases.py`, oracles) and the raw case files were never part of this repository and are private, as is rrawrlet itself. Each probe record therefore states what was observed at the recorded Nyrwvy and rrawrlet commits, with the counts, failures and timings as the author saw them; a reader can verify the language changes the probes motivated through the linked decisions, specs and tests, but cannot rerun the comparison. "Probe N" numbers the probes in order; "order N" is the numbered entry of the [implementation queue](../plans/post-m2-roadmap.md#immediate-execution-queue) that a probe's finding became.

- [Codex CLI MCP maintenance probe, Run 1](codex-cli-mcp-maintenance-probe.md) — host approval blocker
- [Codex CLI MCP maintenance probe, Run 2](codex-cli-mcp-maintenance-probe-run-2.md) — qualified maintenance success
- [Expression recipe reference live probe](expression-recipe-reference-live-probe.md)
- [rrawrlet slice probes: summary](rrawrlet-slice-probes-summary.md): the 2026-09-28 re-probe and probes 3-19 and 25, one row each with what it established, the gaps found and the orders and decisions they led to (consolidated on 2026-10-18; the individual records are at commit `b3a958a`). Results against the real Python ranged from 26/26 (re-probe) to 2,762/2,775 (probe 19); recursive types by name (probe 7) was the only blocker recorded; findings became orders 6-31 and later fed orders 32, 33, 34, 41, 43, 44, 49 and 56
- [Music-logic probe 20](music-logic-probe20-20261001.md): #283's just-intonation module (exact ratios at the Int64 edge, prime vectors, degree, voicing, cents and 41-EDO steps, `exp2`, compromise score, `NoteSink` progression), 3,400/3,400 against Python; no blocker; Float64 to Int64 rounding met the issue's decision rule and became order 34
- [Map/Set probes 21-24: summary](map-set-probes-summary.md): word counts and a dependency planner authored four times on orders 44-54, 1,000/1,000 to 2,200/2,200 against a JavaScript reference, no blocker; counted folds, the upsert, an explicit stack, loop-state threading and a copying queue became orders 48-55 (consolidated on 2026-10-18; the individual records are at commit `b3a958a`)
- [Task communication probe 26](task-communication-probe26-20261007.md): a reduced `codex.py` client written as probe 25's event loop and as a reader task with scoped `OneShot` chains, 9/9 scenarios each, no Python oracle; 508 and 491 nodes, so `OneShot` does not remove the reader cost and no channel is queued; a cell cannot be awaited together with a task without a helper task

## Project review and follow-ups

- [Project review 2026-09-22](project-review-20260922.md) and its [prioritized follow-ups](project-review-follow-ups-20260922.md)
- [Pinned-dependency review follow-ups](pinned-dependency-review-follow-ups.md)
- [Retained dependency pins (#209)](retained-dependency-pins.md)
- [Request-scoped snapshot computation reuse (#210)](scoped-snapshot-computations.md)
- [Source ownership and evaluation compatibility (#211)](source-ownership-boundaries.md)
- [Build and suite review follow-ups (#212, #213)](build-and-suite-review-follow-ups.md)
- [Semantic-facts scaling repair](semantic-facts-scaling.md)

## Offline Team/Member reconstruction

The `member-*`, `offline-leader-*` and `offline-session-*` examples these records cite (and `offline-session-replay`) were removed from `main` on 2026-10-19 by the [legacy cutover](../plans/post-m2-roadmap.md#legacy-cutover-planned): their fixtures authenticate graph hashes and retained binaries of forms the cutover retires, so they could not be re-recorded honestly. Their links below point at commit `archive/legacy-examples-pre-cutover-20261019`, the last `main` commit that contains them ([examples](../../examples/README.md#removed-examples)). The records themselves are unchanged.

- [Bounded two-worker stream](bounded-worker-stream-20261011.md): two long-lived producers feed a slower sink with at most two unacknowledged records, owned by existing `OneShot` and `TaskScope` operations (21 functions, 686 nodes); verified on the TaskStart capture-release branch (PR #376).
- [Bounded-stream prerequisite: retained moving cursors](bounded-stream-cursor-retention-20261011.md): historical baseline showing worker callbacks retaining moving cursors; superseded by the capture repair below.
- [TaskStart capture lifetime repair](task-start-capture-release-20261011.md): the TaskStart emitter no longer holds block-scoped context/item snapshots for a worker's lifetime.
- [Dynamic worker/collector probe](dynamic-worker-collector-probe-20261011.md): a dynamically registered multi-producer graph expressed with existing `OneShot` and `TaskScope`; no new channel, time value, yield or runtime change.
- [Bounded offline Member lifecycle](member-lifecycle-offline-20261012.md): a Nyrwvy-owned controller authored, saved/reopened/replayed and run as generated C#; 13 of 15 scenarios match the pinned Python Team oracle, two negative scenarios show fidelity gaps.
- [Offline Member lifecycle: frozen no-core follow-ups](member-lifecycle-followup-20261012.md): follow-ups to the lifecycle record, later extracted into the generic task observer.
- [Generic cancellable task observation](task-wait-observer-20261012.md): a reusable graph composition lets a cancelled caller finish while its work tasks and settlement monitor stay pending; no TaskWait runtime change.
- [Offline Member integration composition](member-integration-20261012.md): 28 sequential policy cases, exact effect ordering and rollback, unchanged Team comparison, virtual/replay/C# evidence; no core change.
- [Member integration save-failure ledger](member-integration-save-failure-20261012.md): baseline gap reproduced; 36 scenarios / 46 per-request comparisons preserve HEAD across save Err and skip repeated effects, with unchanged Python and emitted C#; no core change.
- [Raw save exception ledger retention](raw-save-ledger-20261012.md): 38 scenarios / 537 request observations match pinned Python after a first mocked save throws, in virtual execution and emitted C#.
- [Raw HEAD-read exception](raw-head-read-20261012.md): verified closure with no RED and no repair; 42 scenarios / 609 request observations match.
- [Serialized Member controller](member-controller-20261012.md): live eligibility, ledger, observer and shield composition; 22 mixed scenarios / 289 per-request Python matches, integrated-continuation adjustment and raw-fault gap.
- [Parent cancellation shielding for finish and rollback](cleanup-shield-20261012.md): generic lexical shield; unchanged pinned-main negative reproduction, 14 Python/virtual/replay/C# matches and resource-drain controls.
- [Determined Member settlement](determined-member-settlement-20261013.md): a determined Member's cleanup and snapshot survive owner cancellation through a small settlement tail after the provider scope returns.
- [Admitted close intent after cancelled handoffs](close-intent-handoff-20261013.md): an admitted close keeps suppressing later Member checks when parent cancellation closes the state handoffs first.
- [Completed mutation state after owner cancellation](completed-mutation-recovery-20261013.md): the admission recipe retains completed immutable mutation state after parent cancellation closes its handoffs (harness corrected 2026-10-14 by the pre-join repair).
- [Whole-owner cancellation and shutdown](member-owner-cancellation-20261013.md): six gated scripts, baseline/adapted virtual and twelve real runs; post-drain owner cancellation reporting through ordinary composition, with explicit pinned Python lifecycle divergences.
- [Owner pre-join observation repair](owner-prejoin-observation-20261014.md): deterministic old running/stopped comparison RED; explicit Member join and alternate scheduling GREEN in both cancellation modes, with unchanged runtime and pinned Python divergences.
- [Task-local cancellation-to-Result composition](task-cancellation-outcome-20261014.md): baseline/wait-only child faults and scope rethrow versus chosen worker Err, real gated cancellation, sibling ownership and cleanup/release fault controls.
- [Generic exception cancellation classification](exception-is-cancellation-20261014.md): the pure `ExceptionIsCancellation` primitive, projected to an `OperationCanceledException` test, for selective handlers.
- [Task start after a seen ancestor cancellation](task-start-cancellation-causality-20261017.md): the generated C# runtime starts a task cancelled when its starter has already seen an ancestor scope cancelled (ancestor-path check); real-thread regression cases, virtual clock unchanged.
- [Offline Leader → Team → Member session](offline-leader-team-session-20261014.md): four scripted turns, no-effect queued command cancellation, responsive held integration, retained HEAD, close cutoff and joined reporting, with moved replay and compiled fault/boundary controls.
- [Offline JSON-RPC session continuation](offline-leader-jsonrpc-session-20261014.md): the re-authored parser/binding boundary of the [`offline-leader-jsonrpc`](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-leader-jsonrpc/README.md) example (PR396).
- [Opt-in snapshot-fault containment](snapshot-fault-containment-20261013.md): eleven bounded scripts, ordinary-fault baseline RED, failed Final retention and explicit OCE/CancelledError divergence; defaults unchanged.
- [Offline session pipe continuation](offline-session-transport-20261014.md): real compiled JSONL pipes, fragmented UTF-8/coalescing, EOF and in-flight root cancellation, protected mutation tail, joined peer-closed input and four compiled negative controls; built on PR395 and PR396 (both merged, as is this PR397).
- [Owned input and bounded offline pipe I/O](offline-session-owned-io-20261014.md): graph reader/ordered dispatch, retained-stdin cancellation shutdown, foreground EOF, broken/stalled output, explicit restart-replay limit, six causal controls and moved byte/hash replay; built on PR395 → PR396 → PR397 (all merged).
- [Bounded input before decoding](offline-session-bounded-input-20261014.md): graph byte policy, bounded Linux framing, UTF-8/boundary/cancellation probes, relaxed-policy controls, moved replay and a held-foreground reader-fault limit; built on the owned-I/O continuation and PR395 → PR396 → PR397 (all merged).
- [Owned reader failure propagation](offline-session-reader-propagation-20261014.md): a failing owned reader propagates to the session in the offline reconstruction (PR400).
- [Durable application journal](offline-session-journal-20261014.md): historical candidate whose complete-effect-to-success inference independent review invalidated; superseded by the actual-outcome repair.
- [Actual-outcome recovery after cancellation and crash](offline-session-actual-outcome-20261014.md): a request result is recovered only from a graph-written actual outcome or receipt; a crash before the first receipt stays unresolved (PR401).
- [Offline registry continuation](offline-session-registry-20261014.md): the [registry example](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-session-registry/README.md) closes the restoration gap where an identical integration ID replayed a retained outcome (PR402).
- [Registry identity and actual-data consistency repair](offline-session-registry-repair-20261014.md): two P2 repairs requested by independent review of the registry candidate.
- [Bounded physical Git reconciliation](offline-session-physical-git-20261014.md): unchanged reviewed registry graph, disposable commit/ref/index/worktree effects, 206 fresh physical processes/52 subprocess-exit SIGKILL observations, exact retained cancellation, fail-closed partial/unowned state, paired controls and moved byte/hash replay; built on PR401/PR402 (both merged); independent review was pending when recorded.
- [Physical Git executable-mode repair](offline-session-physical-git-mode-repair-20261014.md): fixes the review's mode-only false-absence/ready-admission finding using retained Git modes, causal content-only controls and adjacent index/symlink checks; preserves outcome uncertainty and the existing reconstruction scope.
- [Fenced physical Git child launch](offline-session-git-child-20261014.md): scoped child-process launch for the physical Git binding, with its [overflow evidence correction](offline-session-git-child-evidence-repair-20261014.md).
- [Owned-tail continuation and observation-release evidence](offline-session-git-continuation-20261014.md): offline experimental continuation with exact source/artifact hashes; independent review was pending when recorded.
- [Explicit Git retention disposition](offline-session-git-disposition-20261014.md): historical candidate with 686 measured processes; its owner-input/receipt-order and ID findings are fixed in the [review repair](offline-session-git-disposition-review-repair-20261014.md).
- [Release authority bound to accepted requests](offline-session-git-release-authority-repair-20261014.md) and its [authority JSON value repair](offline-session-git-authority-values-repair-20261014.md): author repairs of review findings P2-1 and P2-2.
- [Interrupted continuation disposition](offline-session-git-interruption-20261014.md): separately authorized receipt after terminal stage custody and exact current-state inspection; preserves both unknown outcomes and closed admission; original/moved crash and invalid-record checks; independent review pending.
- [One renewed admission epoch](offline-session-epoch-20261014.md) and its [pinned owner dispatch repair](offline-session-epoch-owner-repair-20261014.md): a renewed admission epoch; review found that empty owner configuration could fall through to direct Git, fixed by the repair.
- [Interrupted fresh epoch disposition](offline-session-epoch-disposition-20261015.md): a disposition route for a fresh epoch whose third outcome is unknown, which the old aggregate epoch revalidation could not tell apart; review pending when recorded.
- [One additional admission](offline-session-additional-admission-20261015.md): the published executable refused an additional begin/integrate pair after its completed disposition; the successor composes existing recipes to admit it, with no core change.
- [Finite offline admission/disposition milestone](offline-terminal-milestone-20261015.md): ends the finite slice with a terminal retain-unresolved disposition; all four outcomes stay unknown and startup stays closed.
- [Reconstruction capability checklist](reconstruction-capability-checklist.md): what the offline Linux-pipe reconstruction demonstrates and which application behavior is still missing.

## Storage format

- [Snapshot record format evaluation](snapshot-record-format-20261023.md): the record format ADR's round trip, diff locality, corruption and load criteria pass; the ADR is accepted.
