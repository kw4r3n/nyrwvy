# Verification records

**Status:** Current

This area holds evidence from completed implementation and evaluation work. A record states what was observed at a recorded revision; it does not by itself define current behavior. For current contracts, use [`../specs/`](../specs/) and [`../reference/`](../reference/).

## Milestone 1

- [Float64 implementation contract](m1-float64-implementation-notes-20260828.md)
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
- [runPure Release build, Roslyn build and early StringSlice](runpure-release-build-20260930.md)
- [runPure arguments as input and the build cache](runpure-build-cache-20261001.md): repeated calls of one entry reuse the built program (about 75 ms instead of 213 ms per call)
- [Lone surrogates in runPure String values](runpure-lone-surrogates-20261001.md): any UTF-16 sequence, as JSON `\uD800` escapes in both directions
- [Amortised List append](list-amortised-append-20260930.md)
- [Amortised String concatenation](string-amortised-concat-20261001.md): `String` is emitted as `NyStr`, so a `StringConcat` fold is linear (3.12 MB in 1.24 s where 195 KB timed out)
- [Code-point position offsets for String operations](string-position-offsets-20260930.md)
- [Bits32](bits32-20261001.md): the fixed-width bit domain, and probe 12's SHA-256 re-authored with it (NIST vectors, about 20 times the Int64 port's throughput, 17.5 MB per run)


## Live agent probes

Most records in this section are *porting probes*: a slice of an existing program was re-authored in Nyrwvy through the AuthoringSession protocol and its outputs were compared case by case with the original. The usual subject, **rrawrlet**, is a separate Python application by the same author (an OpenCode-based agent-team tool) chosen because it is real, unrelated code; its functions are named in the records for orientation only. The probe scripts (`author.py`, `mkcases.py`, oracles) and the raw case files were never part of this repository and are private, as is rrawrlet itself. Each record therefore states what was observed at the recorded Nyrwvy and rrawrlet commits, with the counts, failures and timings as the author saw them; a reader can verify the language changes the probes motivated through the linked decisions, specs and tests, but cannot rerun the comparison. "Probe N" numbers the probes in order; "order N" is the numbered entry of the [implementation queue](../plans/post-m2-roadmap.md#immediate-execution-queue) that a probe's finding became.

- [Codex CLI MCP maintenance probe, Run 1](codex-cli-mcp-maintenance-probe.md) — host approval blocker
- [Codex CLI MCP maintenance probe, Run 2](codex-cli-mcp-maintenance-probe-run-2.md) — qualified maintenance success
- [Expression recipe reference live probe](expression-recipe-reference-live-probe.md)
- [rrawrlet slice re-probe](rrawrlet-slice-reprobe-20260928.md): the 2026-09-25 slice re-run after orders 1-5, by name only; data processing matches Python and the leader skeleton passes its scenarios
- [rrawrlet slice probe 3](rrawrlet-slice-probe3-20260928.md): the rebuilt slice without the workarounds orders 6-10 removed, plus `usage_stats`'s aggregation core, matching Python
- [rrawrlet slice probe 4](rrawrlet-slice-probe4-20260929.md): `review.parse_findings` and the pure core of `repo_map`, 23/23 against Python; per-character recursion overflowed at about 7.7K characters
- [rrawrlet slice probe 5](rrawrlet-slice-probe5-20260930.md): probe 4's slice re-authored with orders 17-22, 24/24 against Python with no depth workaround
- [rrawrlet slice probe 6](rrawrlet-slice-probe6-20260930.md): `LeaderGuard`'s snapshot read path with state threaded through an effectful fold, 16/16 request sequences against Python
- [rrawrlet slice probe 7](rrawrlet-slice-probe7-20260929.md): the OpenCode event observer over untyped JSON, 35/35 against Python; recursive types by name were the blocker (order 26)
- [rrawrlet slice probe 8](rrawrlet-slice-probe8-20260929.md): secret redaction over decoded JSON and the `diagnostics.py` helpers, 113/113 against Python
- [rrawrlet slice probe 9](rrawrlet-slice-probe9-20260930.md): the Team tool relay (`json.loads`, JSON-RPC, `Team.dispatch`, `json.dumps`), 226/228 against Python after order 28's code-point conversion
- [rrawrlet slice probe 10](rrawrlet-slice-probe10-20260930.md): `opencode_config.py`'s configuration and model catalog code, 317/320 against Python on the first run; one quadratic scan restructured as a fold
- [rrawrlet slice probe 11](rrawrlet-slice-probe11-20260930.md): `snapshot.py`'s transcript reduction and `model_health.py`, 301/304 against Python; nothing blocked or queued
- [rrawrlet slice probe 12](rrawrlet-slice-probe12-20261001.md): `condense` material, cache key (SHA-256 in Int64 arithmetic) and citations, 304/304 against Python
- [rrawrlet slice probe 13](rrawrlet-slice-probe13-20261001.md): `ref_backup` (Git ref snapshots and changes) and `supervisor` (`argparse` command line, a clocked poll and cleanup state machine), 371/371 against Python after 2 port bugs
- [rrawrlet slice probe 14](rrawrlet-slice-probe14-20261001.md): `repo_map` (tree-sitter outline over a `Host` parse, `os.walk`/`git ls-files`, bytes as latin-1 Strings with a hand-written UTF-8 decoder), 310/310 against Python on the first run
- [rrawrlet slice probe 15](rrawrlet-slice-probe15-20261001.md): `mcp.py` (the MCP server's event loop, 16 tools, live runs, timed waits and close over a `Host` capability), 427/428 against Python after 2 port bugs; the self-recursive loop overflows at 12,000–14,000 turns and a fold form does not
- [rrawrlet slice probe 16](rrawrlet-slice-probe16-20261001.md): `results` (`apply_result` and `application_status` with `checkout`'s symlink walk, file states and index checks over a `Host`), 518/518 against Python on the first run; without a Map the tree comparison times out at 16,000 files
- [rrawrlet slice probe 17](rrawrlet-slice-probe17-20261001.md): `verify` (`shlex.split`/`join`, check-spec validation, registered checks, `_run_check`'s `try`/`except`/`finally` over a `Host`, views of corrupted records), 1,094/1,094 against Python on the first run; no blocker
- [rrawrlet slice probe 18](rrawrlet-slice-probe18-20261001.md): the rest of `review` (`parse_findings` without a regex engine, `git_diff` with a lossy UTF-8 decode, `collect_diff`, `review`'s validation and record over a `Host`), 828/828 against Python; probe 5's three divergences now match; no blocker
- [rrawrlet slice probe 19](rrawrlet-slice-probe19-20261001.md): `web_live_requests` with `_read_json`'s body step (strict UTF-8 from the lossy builtin, `json.loads` with big integers, field checks, `to_args` as one `ProductUpdate`), 2,762/2,775 against Python on the first run (8 surrogate-pair divergences, 5 cases beyond `runPure` limits); no blocker
- [Music-logic probe 20](music-logic-probe20-20261001.md): #283's just-intonation module (exact ratios at the Int64 edge, prime vectors, degree, voicing, cents and 41-EDO steps, `exp2`, compromise score, `NoteSink` progression), 3,400/3,400 against Python; no blocker; Float64 to Int64 rounding met the issue's decision rule and became order 34
- [Map/Set probe 21](map-set-probe21-20261004.md): word counts and a dependency planner (group-by into a map of sets, reachability, topological order with cycles, dependent counts) on orders 44-47, 1,000/1,000 against a JavaScript reference; no blocker; counted folds that cannot stop early and the get-or-default upsert were the largest costs
- [Map/Set probe 22](map-set-probe22-20261005.md): probe 21's planner on orders 48-49 (tail self-calls replace the counted folds) plus parallel levels and a depth-first cycle path, 1,400/1,400 against a JavaScript reference; no blocker; 175 nodes instead of 212 for probe 21's scope; an explicit stack (117 nodes, quadratic) and finding an item's position were the new costs
- [Map/Set probe 23](map-set-probe23-20261005.md): probe 22's planner on orders 50-51 plus a linear Kahn's algorithm and Tarjan's components, 1,800/1,800 against a JavaScript reference; no blocker; the cycle path is linear on a chain (300,000 in 3.6 s), but a push after a pop still copies the stack, and on a comb both searches exceed the run limit; `tarjanLoop` is 208 nodes, a quarter of them unchanged self-call arguments
- [Map/Set probe 24](map-set-probe24-20261005.md): probe 23's planner on orders 52-54 plus a breadth-first shortest path and a project length, 2,200/2,200 against a JavaScript reference; no blocker; `keepUnlisted` cut `tarjanLoop`'s self-call arguments from 70 to 23 and the combs run at 100,000, but a queue dequeued with `ListSlice(1, …)` copies itself, and on a fan of 40,000 the shortest path and `reachable` exceed the run limit
- [rrawrlet slice probe 25](rrawrlet-slice-probe25-20261005.md): `codex.py` (the JSON-RPC client's reader, request timeouts, tool-call handlers, turn deadlines, `close`, `codex_error`, and the process-group shutdown over `/proc`), 489/490 against Python on a virtual-clock event loop after two port bugs were fixed; no blocker; one same-instant ordering asyncio allows has no zero-time yield to express it; futures or channels between tasks, a time value, and strict decoding with Python's error text were the costs
- [Task communication probe 26](task-communication-probe26-20261007.md): a reduced `codex.py` client written as probe 25's event loop and as a reader task with scoped `OneShot` chains, 9/9 scenarios each, no Python oracle; 508 and 491 nodes, so `OneShot` does not remove the reader cost and no channel is queued; a cell cannot be awaited together with a task without a helper task

## Project review and follow-ups

- [Project review 2026-09-22](project-review-20260922.md) and its [prioritized follow-ups](project-review-follow-ups-20260922.md)
- [Pinned-dependency review follow-ups](pinned-dependency-review-follow-ups.md)
- [Retained dependency pins (#209)](retained-dependency-pins.md)
- [Request-scoped snapshot computation reuse (#210)](scoped-snapshot-computations.md)
- [Source ownership and evaluation compatibility (#211)](source-ownership-boundaries.md)
- [Build and suite review follow-ups (#212, #213)](build-and-suite-review-follow-ups.md)
- [Semantic-facts scaling repair](semantic-facts-scaling.md)

- [Offline Member integration composition](member-integration-20261012.md): 28 sequential policy cases, exact effect ordering and rollback, unchanged Team comparison, virtual/replay/C# evidence; no core change.
- [Member integration save-failure ledger](member-integration-save-failure-20261012.md): baseline gap reproduced; 36 scenarios / 46 per-request comparisons preserve HEAD across save Err and skip repeated effects, with unchanged Python and emitted C#; no core change.
- [Serialized Member controller](member-controller-20261012.md): live eligibility, ledger, observer and shield composition; 22 mixed scenarios / 289 per-request Python matches, integrated-continuation adjustment and raw-fault gap.
- [Parent cancellation shielding for finish and rollback](cleanup-shield-20261012.md): generic lexical shield; unchanged pinned-main negative reproduction, 14 Python/virtual/replay/C# matches and resource-drain controls.
- [Whole-owner cancellation and shutdown](member-owner-cancellation-20261013.md): six gated scripts, baseline/adapted virtual and twelve real runs; post-drain owner cancellation reporting through ordinary composition, with explicit pinned Python lifecycle divergences.
- [Owner pre-join observation repair](owner-prejoin-observation-20261014.md): deterministic old running/stopped comparison RED; explicit Member join and alternate scheduling GREEN in both cancellation modes, with unchanged runtime and pinned Python divergences.
- [Task-local cancellation-to-Result composition](task-cancellation-outcome-20261014.md): baseline/wait-only child faults and scope rethrow versus chosen worker Err, real gated cancellation, sibling ownership and cleanup/release fault controls.
- [Offline Leader → Team → Member session](offline-leader-team-session-20261014.md): four scripted turns, no-effect queued command cancellation, responsive held integration, retained HEAD, close cutoff and joined reporting, with moved replay and compiled fault/boundary controls.
- [Opt-in snapshot-fault containment](snapshot-fault-containment-20261013.md): eleven bounded scripts, ordinary-fault baseline RED, failed Final retention and explicit OCE/CancelledError divergence; defaults unchanged.
