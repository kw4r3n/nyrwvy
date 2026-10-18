# Examples

Small, self-contained programs that exercise the maintained authoring tools. Each directory's README gives the exact commands; all of them are run from the repository root after `npm ci` and `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`.

The snapshot files are the programs; `runner/Generated.cs` files are projections written by `emitCSharp` and are never edited by hand. Most directories below are probes: each was authored through the ordinary tools to check one contract, and its README states the base commit and what it does and does not establish. Directories marked *(fast)* are exercised by the fast suite (`scripts/tests`) and those marked *(extended)* by the local extended suite (`.opencode/tests-extended`); the others are run only through their own documented commands.

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
- [`dynamic-worker-collector/`](dynamic-worker-collector/README.md): two to four workers that register after a running collector has started, with failure, individual cancellation and shutdown cases.

## Member lifecycle (offline port of a Python Team controller)

- [`member-lifecycle/`](member-lifecycle/README.md): bounded offline Member lifecycle controller, actual Python Team differential oracle, unedited C# runtime checks, and explicit cancellation/fault fidelity gaps.
- [`member-lifecycle-followup/`](member-lifecycle-followup/README.md): no-core cancellable observer and tagged provider-effect adaptations, with raw-fault controls retained.
- [`member-integration/`](member-integration/README.md): bounded sequential integration/rollback policy over observed facts, synthetic Git effects, and unchanged Python Team comparison.
- [`member-controller/`](member-controller/README.md) *(extended)*: serialized offline lifecycle/integration composition, 22 independent oracle matches and an explicit raw commit-fault gap.
- [`member-snapshot-faults/`](member-snapshot-faults/README.md) *(extended)*: opt-in snapshot-only ExceptionCatch composition, retained failed Final and an explicit Python cancellation-class divergence.
- [`member-concurrency/`](member-concurrency/README.md) *(extended)*: bounded concurrent Member admission under the unchanged Python Team's lock policy.
- [`member-owner-cancellation/`](member-owner-cancellation/README.md) *(extended)*: whole-owner cancellation and shutdown, including the pre-join repair.
- [`member-determined-settlement/`](member-determined-settlement/README.md) *(extended)*: Member status captured after the provider scope and published under a cancellation shield.
- [`member-close-intent/`](member-close-intent/README.md) *(extended)*: close intent after cancellation, with the close latch owned by a separate shielded scope.

## Offline Leader and session (continuations of the Member series)

- [`offline-leader-team/`](offline-leader-team/README.md) *(extended)*: scripted Leader turns compose the maintained typed relay, admission, Member defaults, integration and joined shutdown; moved replay, per-request effects and compiled boundary controls.
- [`offline-leader-jsonrpc/`](offline-leader-jsonrpc/README.md) *(extended)*: the JSON parser, envelope checks and serialization re-authored as graph recipes in front of the Leader/Team/Member session.
- [`offline-session-transport/`](offline-session-transport/README.md) *(extended)*: the same session over actual child-process stdin/stdout pipes.
- [`offline-session-owned-io/`](offline-session-owned-io/README.md) *(extended)*: an owned graph reader and ordered dispatcher over Linux pipes, cancellation without peer EOF, observable output failure, causal controls and moved replay.
- [`offline-session-bounded-input/`](offline-session-bounded-input/README.md) *(extended)*: graph-supplied byte policy, bounded framing before strict UTF-8 decoding, exact-limit/cancellation probes, relaxed-policy controls and moved replay.
- [`offline-session-reader-propagation/`](offline-session-reader-propagation/README.md) *(extended)*: owned reader failure propagation while foreground work is held.
- [`offline-session-journal/`](offline-session-journal/README.md) *(extended)*: durable integration requests and pending-intent reconciliation.
- [`offline-session-registry/`](offline-session-registry/README.md) *(extended)*: a durable registry and integrated HEAD restoration.
- [`offline-session-physical-git/`](offline-session-physical-git/README.md) *(extended)*: the reviewed durable registry graph bound to disposable local commit/ref/index/worktree effects, subprocess-exit crash reconciliation, exact retained outcomes, negative controls and moved byte/hash replay.
- [`offline-session-epoch/`](offline-session-epoch/README.md): one explicit graph-authored renewed admission epoch, blocked predecessor identities and one fresh once-only integration in controlled offline physical fixtures.
- [`offline-session-epoch-disposition/`](offline-session-epoch-disposition/README.md): explicit applied fresh-tail disposition, three retained unknowns and closed startup.
- [`offline-session-additional-admission/`](offline-session-additional-admission/README.md) *(extended)*: one explicit additional admission and selected Member/integration with three old unknowns retained.
- [`offline-session-additional-disposition/`](offline-session-additional-disposition/README.md): terminal applied-tail disposition, four unknowns, bounded evidence encoding and the [finite acceptance index](../docs/verification/offline-terminal-milestone-20261015.md).
- [`offline-session-replay/`](offline-session-replay/README.md) *(fast: input pack only)*: Git-tracked authenticated ancestor inputs and one-command opt-in reproduction of the four latest finite offline session oracles above; no private parent artifacts required.
