# Examples

Small, self-contained programs that exercise the maintained authoring tools. Each directory's README gives the exact commands; all of them are run from the repository root after `npm ci` and `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`.

The snapshot files are the programs; `runner/Generated.cs` files are projections written by `emitCSharp` and are never edited by hand. Most directories below are probes: each was authored through the ordinary tools to check one contract, and its README states the base commit and what it does and does not establish. Directories marked *(fast)* are exercised by the fast suite (`scripts/tests`) and those marked *(extended)* by the local extended suite (`.opencode/tests-extended`); the others are run only through their own documented commands.

Every example authors in the current forms: no authoring script uses a [retired recipe kind](../docs/decisions/retired-authoring-kinds.md) or a legacy `pin`, and each committed snapshot is written at the current schema by the [migration command](../docs/specs/agent-authoring-session.md#snapshot-migration), whose report is committed beside it as `<snapshot>.migration.json` (the legacy cutover of 2026-10-19, see [removed examples](#removed-examples)).

## Start here

- [`function-abort/`](function-abort/README.md): a deterministic script that authors `readValidated(path, files)` through the `nyrwvy_*` tool handlers, reviews and commits, reopens the snapshot and runs three scenarios with `runPure`.
- [`run-fixtures/`](run-fixtures/README.md) *(fast)*: send saved `runPure`/`runPureByName` request JSON directly through ordinary MCP, with concise case summaries and full evidence saved to disk; includes a RunValue quick reference.

## Programs kept as snapshots

- [`verification-planner/`](verification-planner/README.md): a four-function pure program kept as a snapshot plus its generated C# runner; its README records how it was authored and maintained through the tools, and `--test --maintained` checks all 1,024 input combinations.
- [`mini-work-planner/`](mini-work-planner/README.md): a next-action planner for one work item, the fixture used by the [Codex CLI MCP maintenance probe](../docs/verification/codex-cli-mcp-maintenance-probe-run-2.md).
- [`config-request-batch/`](config-request-batch/README.md): a file-backed configured batch with retries, expected failures, host exceptions, cooperative cancellation and resource cleanup; saved cases rerun after reopening and moving a pinned dependency.
- [`change-verification-batch/`](change-verification-batch/README.md): experimental precommit comparison of real v2 batch formatter changes, explicit intended results and negative controls.

## Tasks, cancellation and exceptions

- [`scoped-result-handoff/`](scoped-result-handoff/README.md): two independently waiting requests and one receiver that routes responses by ID; scoped one-shot cells, virtual fixtures, saved replay and an unedited real-runtime projection.
- [`task-wait-observer/`](task-wait-observer/README.md) *(extended)*: four reusable generic functions for cancellable task observation, published through ordinary `simulateCreateFunctionByName` and `commit` calls.
- [`task-cancellation-outcome/`](task-cancellation-outcome/README.md) *(extended)*: explicit worker-local cancellation-to-Result composition, wrong-boundary controls and real exception identity/stack proof; existing task and Member policies unchanged.
- [`exception-boundary/`](exception-boundary/README.md) *(extended)*: an explicit catch callback, captured exception message access and rethrow, composed with `CancellationShield`, `TaskScope`, `TaskCancel` and `UseResource`.
- [`cleanup-shield/`](cleanup-shield/README.md) *(extended)*: parent cancellation during finish and rollback, using a lexical cancellation shield for the mutation tail of an integration.
- [`bounded-stream/`](bounded-stream/README.md): a minimal regression probe that discarded heads of advancing OneShot cursors are collected while their workers and owner stay live.
- [`bounded-worker-stream/`](bounded-worker-stream/README.md): two long-lived producers with independent OneShot chains and acknowledgement-bounded input.
- [`dynamic-worker-collector/`](dynamic-worker-collector/README.md): two to four workers that register after a running collector has started, with failure, individual cancellation and shutdown cases; its `lib.mjs` is the recipe helper library the other task examples share.

## Removed examples

The Member lifecycle series (an offline port of a Python Team controller) and its Offline Leader and session continuations were removed from `main` on 2026-10-19 by step 2 of the [legacy cutover](../docs/plans/post-m2-roadmap.md#legacy-cutover-planned), as the evaluation runners were ([tools](../tools/README.md#evaluation-runners)). They remain reproducible from commit `bca45b30053b68abbfd071480e7c062735f410e6`, the last `main` commit that contains them; no archive tag was created. Their [verification records](../docs/verification/README.md#offline-teammember-reconstruction) are unchanged and link to that commit.

They could not be re-recorded honestly: their fixtures authenticate graph hashes computed from authoring scripts that use the retired kinds (`stringEqual`, `int64Equal`, `mapGetOrDefault`, `int64GreaterThan`), their committed snapshots (schema 13 and 14) hold those nodes, their oracles compare against a pinned Python Team checkout that is not part of this repository, and `offline-session-replay` retains compiled binaries and authenticated ancestor inputs of parent sessions. Each later example in the series authors on top of the earlier ones (`member-lifecycle` → `member-controller` → `member-concurrency` → `member-owner-cancellation` → … → `offline-leader-team` → `offline-leader-jsonrpc` → `offline-session-*`), so the chain goes together. The committed Python oracle expectations were left with them.

| Removed directory | Last recorded as |
| --- | --- |
| `member-lifecycle`, `member-lifecycle-followup`, `member-integration` | committed snapshots at schema 14, 14 and 13 with retired nodes; Python Team differential oracles |
| `member-controller`, `member-snapshot-faults`, `member-concurrency`, `member-owner-cancellation`, `member-determined-settlement`, `member-close-intent` | authored at run time on top of `member-lifecycle`; fixtures record graph hashes and Python oracle expectations |
| `offline-leader-team`, `offline-leader-jsonrpc` | authored on top of `member-concurrency`; proof fixtures record graph hashes |
| `offline-session-transport`, `offline-session-owned-io`, `offline-session-bounded-input`, `offline-session-reader-propagation`, `offline-session-journal`, `offline-session-registry`, `offline-session-physical-git`, `offline-session-epoch`, `offline-session-epoch-disposition`, `offline-session-additional-admission`, `offline-session-additional-disposition` | authored on top of `offline-leader-jsonrpc`; proof fixtures record graph and bundle hashes |
| `offline-session-replay` | retained compiled binaries and authenticated ancestor inputs of the four latest offline session oracles |

The removed extended tests were `.opencode/tests-extended/nyrwvy-authoring-member-*.test.mjs` and `nyrwvy-authoring-offline-*.test.mjs`, and the fast test `scripts/tests/retained-offline-inputs.test.mjs`.
