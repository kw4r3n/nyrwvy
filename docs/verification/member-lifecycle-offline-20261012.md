# Bounded offline Member lifecycle — 2026-10-12

**Outcome:** a Nyrwvy-owned controller was authored, saved/reopened/replayed and
executed as unedited generated C#. Thirteen of fifteen scenarios match the
pinned Python Team oracle. Two negative scenarios demonstrate concrete fidelity
gaps; this is not a claim that Nyrwvy can fully replace Team.

The later [bounded no-core follow-ups](member-lifecycle-followup-20261012.md)
preserve these baseline divergences and verify two semantic adaptations. The
committed synthetic fixtures plus `examples/member-lifecycle/materialize.mjs`
reconstitute the exact projections; private runtime transcripts and binaries
remain outside Git.

## Sources and tool identities

Latest remote main refs were checked and fetched into isolated local clones:

- Nyrwvy HEAD: `ec6f3c4eb18c7c1e5385ce19a92fb93b06cac851`.
- rrawrlet HEAD: `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`.
- Original PC checkouts were clean and were neither edited nor checked out.
- PC execution was used because an installed .NET SDK was available there.
- Nushell `0.116.1`, Node `v26.11.1`, Python `3.14.7`, .NET SDK `10.0.112`.
- Runtime and reference pack `10.0.12`, Roslyn `5.0.0-dev`, 167 reference assemblies.
- AuthoringSession was built from this isolated Nyrwvy HEAD with existing cached
  packages, `-m:1`, zero warnings/errors. No packages or SDKs were installed.
- Compilation used the existing `dynamic-worker-collector/compile-runtime.mjs`
  helper and installed compiler/reference files; no restore or network at runtime.

Ordinary `createGraphTools` calls produced **50 functions / 955 graph nodes**,
including the separate six-function caller-cancellation negative probe. Four
product types, one `Io` effect and one ten-operation external capability. No
changes to graph/core/runtime, no new primitives and no new dependencies.

## Saved artifact identity

| Artifact | SHA-256 |
| --- | --- |
| `members.json` | `427f2e02169e337fa153b960cce140497723878887c37d9be8c7e058a115b1ef` |
| `Generated.cs` | `aaffa604f59219f371bbf25627914e07e950bbf74c360be6f6bad586671b7850` |
| `symbols.json` | `bbf8c3011395be74f1cfc6cf5da3c5aab910906f26261408cf51de4857b715b4` |
| `cases.json` | `c24b07ff9bc86316542b3f902dedfe06dc0410bfc16a8957ab39b09775f0a31d` |

Snapshot ID: `01a12dad-d683-78cc-aad7-e6b0720388ac`.
Snapshot fingerprint:
`sha256:8564f4b5388fa2faaa63f0b23fc22e06193a733a5f27e5743d737b81d09fb73e`.
Semantic fingerprint:
`sha256:6b8f797cc5592174ffc9b5bbcaa4cf8217ff9bc9afd01e399df2d57518bd51fc`.

Reopening and re-emitting matches both source and symbols. Runtime checks hash
the input and compiler copy of `Generated.cs` before/after; both remain unchanged.
Replaying does not edit the graph. Fresh authoring allocates new stable IDs and
therefore new byte hashes; replay of this saved bundle retains these identities.

## Evidence and comparison

The deliverable bundle contains `members.json`, `Generated.cs`, `symbols.json`,
`authoring.json`, `cases.json`, `transcript.jsonl`, `bundle-identity.json`,
`evidence.json`, `replay-evidence.json`, `replay-identity.json`,
`Runtime.Program.cs`, `runtime-evidence.json`, and `oracle-evidence.json`.
The surrounding handoff package adds repository/tool provenance, a patch,
portable replay identity and this report. Commands are in the
[example README](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/member-lifecycle/README.md).

- Initial saved/reopened virtual execution: **15/15 assertion passes**.
- Reopened execution and copied-directory replay: **15/15 each**.
- Real-runtime unedited projection: **15/15 assertion passes**.
- Actual Team differential comparison: **13 matched / 2 demonstrated gaps**.
- Negative oracle controls: **7/7 rejected** (early settlement, superseded
  admission, repeated discard, changed continuation workspace, checks changing
  completion, false snapshot commit, expected error cancelling a peer).

The expected provider error deliberately uses the text `deadline`; it still
becomes failed. The graph maps provider errors separately before classifying
its own deadline-expiry marker, preventing host error text from impersonating
the attempt deadline.

The Python oracle calls unchanged Team policy methods and replaces only external
effects. It verifies the exact HEAD and records a hash of `team.py`. Replies
normalize attempt UUIDs to admission order, workspace paths to ownership IDs,
fake base/commit values and the selected evidence fields. Running wait results
retain Team's reduced record shape; inspect during snapshot and checks retains
its richer shape. All ordinary replies compare exactly with both virtual and
runtime records. Per-attempt effect order is also asserted, without imposing an
arbitrary cross-worker interleaving.

Both overlap scenarios require two providers to enter a shared countdown barrier
before either proceeds; measured active maximum is two. Stop-before-start gates
worker entry and verifies zero provider invocations. Snapshot/check barriers
hold actual worker threads; stop releases them only after the graph has made its
cancel-or-join decision, and the effect checks that its token was not cancelled.
Wait timeout and caller-wait cancellation check that the Member token remains
uncancelled. Scope/resource closure happens after worker effects; finish is
acknowledged after the single provider-service close.

## Passed / failed / unimplemented matrix

| Requirement | Fidelity result | Evidence |
| --- | --- | --- |
| Two Members actually overlap | Passed | overlap-continuation, finish-active |
| wait any/all, settled records, timeout does not kill Member | Passed | overlap-continuation, stop-during-run |
| Provider completion is not settlement | Passed | snapshot and checks gated |
| Capacity occupied / previous rejected until settlement | Passed | overlap-continuation, checks-gated, stop-snapshot |
| Stop before start / during run retains workspace and releases capacity | Passed | stop-before-start, stop-during-run |
| Stop during snapshot/checks preserves determined completion | Passed | stop-snapshot, checks-gated |
| Expected error isolated, deadline timed_out, inner timeout failed | Passed | expected-error, deadline, inner-timeout |
| Snapshot failure no false commit | Passed | snapshot-error |
| Checks failed/unavailable retain completed plus evidence | Passed | checks-failed, checks-unavailable |
| Fresh-context previous reuses workspace/base with new attempt ID | Passed | completed, stopped, failed, timed-out continuations |
| Superseded/discarded previous rejected | Passed | overlap-continuation |
| Discard terminal/latest only once and marks related attempts | Passed | overlap-continuation |
| Finish closes admission, joins, closes provider once, retains workspaces | Passed | finish-active, finish-snapshot |
| Closing during snapshot skips checks not yet started | Passed | finish-snapshot |
| Caller cancellation terminates only its wait | **Failed** | caller-cancellation-gap; Member is preserved |
| Unexpected ordinary host exception becomes isolated failed Member | **Failed** | unexpected-fault; graph scope instead faults |
| Concurrent controller admission | Unimplemented / no claim | requests deliberately serialized |
| Real Git security/integration/rollback, auth/network/provider agents | Excluded | effects only |
| Catalog validation, token budget, Leader deadline pause, conversation reuse | Excluded | independent fake invocations only |
| UI, unbounded scale, malformed-request parity, live-state restart | Excluded | finite scripts, up to three admitted attempts |
| Cleanup-effect failure recovery | Unimplemented | no provider-close/workspace-discard failure claim |

Graph state owns registry, IDs, lineage, capacity, task handles and settlement;
one-shot cells retain determined status, captured commit and closing signal.
Workspace retention/discard and snapshot/check eligibility are graph decisions.
The host contains typed fake effects, barriers, clocks, narrow expected exception
translation and evidence capture. It contains no Team façade, scheduler or
admission/lineage/settlement policy. Registry state containing completed handles
crosses the resource boundary only inside graph functions; the host never sees it.

The caller-wait gap exists in both virtual and real generated `TaskWait`: its
blocking path does not observe caller cancellation. The isolated negative graph
asks its waiter to cancel, observes it pending with the Member alive, and then
cancels/joins the Member to release it. Python cancels that waiter immediately.

Unexpected faults are not disguised as expected Results. Python `execute`
catches `Exception` and records a failed Member; Nyrwvy propagates an unexpected
host fault, joins/cancels the scope and closes its provider-service resource.
Normal per-Member cleanup and a final registry report are not guaranteed on that
fault path. Expected fake provider errors and inner timeouts are the only narrow
exception translations exercised. Fixing the two gaps is separate work, not a
broad rewrite justified by this probe.

## Local failures corrected and remaining limitations

Ordinary tools rejected a pure fold that polled tasks, and later rejected two
unordered one-shot reads. They were repaired with existing `listEffectFold` and
explicit `Bind`, respectively. An initial scalar Result fixture encoding was
repaired to the documented flat shape. A deadline-oracle fixture initially
changed Team's shared attempt limit before workers started; the two initial
Members now use the same 20 ms limit. These are corrected probe/fixture errors,
not runtime passes or demonstrated missing language primitives.

Parallel MSBuild restoration initially exited without useful diagnostics;
single-node build passed. Sandbox Node synchronous child-process waits reported
`EPERM` despite child output. Approved local escalation allowed the isolated
offline compiler/runtime processes. During local validation, no manual CI, push, PR, merge, provider
network call or production Git operation was performed. The full repository
test suites were not rerun; evidence is confined to this example and oracle.

## Gap 1: caller cancellation — minimal reproduction and fix design

The saved `caller-cancellation-gap` case calls `callerCancellationRun`. Its six
graph functions use one held Member and a separate waiting caller. The caller
signals `caller-wait-entered`, then blocks in `TaskWait([member], all, no limit)`.
The coordinator waits for that barrier, cancels **the caller's task handle**,
polls it, and separately polls the Member. Both virtual and real evidence are
`caller:running` / `member:running`; the real provider also asserts that its own
token remains uncancelled. The coordinator then explicitly cancels/joins the
Member to release and join the still-blocked caller. No Member is killed merely
by cancelling its waiter.

The corresponding actual Team oracle starts `asyncio.create_task(team.wait(...))`,
cancels that waiting task, observes `CancelledError` immediately, and confirms
that the separately owned Member task is neither cancelling nor done. This is
the fidelity failure; preserving the Member alone does not make the wait correct.

The pinned emitter's `NyTasks.Wait` virtual predicate observes only member-task
completion. Its real `Task.WaitAny` / `Task.WaitAll` calls similarly omit the
waiting caller's token. `TaskCancel` does request that caller's token; the
blocking wait simply does not react to it. This diagnosis does not require a
rrawrlet-specific primitive.

Existing mechanisms can plausibly compose a bounded cancellable observer:
keep a monitor task in the controller's owning scope, have it `TaskWait` the
Member(s) and complete a scope-owned `OneShot` with the actual settled snapshots,
and have the caller `OneShotWait` that cell. `OneShotWait` already observes caller
cancellation. The monitor must outlive an individually cancelled caller and be
joined during controller shutdown, after the Members are stopped or finished.
Publishing inside the Member just before returning is insufficient to prove
actual task settlement; the monitor must observe the task handle after it finishes.
This composition has **not** been implemented or verified in this bundle. It is
the smallest example-only follow-up probe if avoiding runtime changes is the
priority, and has bounded extra task/cell cost rather than an unbounded mailbox.

The smallest general runtime proposal is cancellation-aware `TaskWait`, with
unchanged argument/result types: capture the **caller's** ambient token before
blocking, include its cancellation in the virtual wait predicate, and observe
it in the real wait path. Capture is necessary because virtual readiness
predicates can execute on a scheduler thread with a different ambient token.
On caller cancellation, stop waiting and return the ordered current snapshots;
leave unfinished Member handles as `None` and never cancel them. Graph callers
can use `CancellationRequested` to return an explicit cancelled Result. Do not
turn cooperative wait cancellation into an unhandled host exception. Completed
values and existing fault observation remain explicit, and `TaskScope.Close`'s
unconditional cancel/join drain must remain unchanged.

Compatibility risk: existing code that deliberately kept waiting after its own
cancellation would now resume earlier, and cancellation-versus-completion/fault
precedence needs a documented contract. This proposal is **not implemented**.
Before/after tests should cover the saved held-Member reproducer, any/all with
zero/finite/no limits, already completed values, cancellation/completion races,
unchanged Member tokens, prompt virtual wake-up without jumping to a Member
deadline, and parent-scope cancellation still joining all tasks before resource
release. The before result is `caller:running`; the proposed caller explicitly
reports cancelled after its wait returns, with the Member still running.

## Gap 2: unexpected host fault — boundary and smallest follow-up

The saved `unexpected-fault` case spawns two Members, then waits all. The first
fake provider throws an ordinary unexpected exception rather than returning a
typed Err. That single throw is enough to reproduce the gap; the second healthy
Member demonstrates Team's isolation but this fault case does not claim a
deterministic overlap or cross-worker cancellation order.

Python `Team.execute` catches ordinary `Exception`, records the failing Member
as failed with its error, closes that agent in `finally`, and permits the healthy
Member to complete. The graph's provider capability instead throws out of
`providerTask`. `NyTask.Snapshot` rethrows the task exception when observed;
closing the nested scope preserves it, and closing the controller scope cancels
and joins unfinished siblings before rethrowing. The global provider-service
resource still closes exactly once. The normal `memberClose` step for the
throwing worker is bypassed and no normal final controller report is produced.
The runtime evidence is `RunRuntimeFailure`, not an isolated failed Member.

Existing typed `Result.Err` already isolates expected failures. A separate
`TaskScope`, `OneShot`, or `UseResource` alone cannot catch a raw unexpected
exception: scope exit rethrows it, and the maintained graph surface has no
general exception catcher. Preserving raw thrown exceptions while isolating
them as graph-owned Member failures therefore cannot be claimed from current
mechanisms.

The smallest no-core follow-up is an **explicit provider-effect boundary probe**:
return separately tagged expected errors and unexpected ordinary provider faults
as typed data, retaining cause evidence, and let graph code decide failed status
and cleanup. Global cancellation must keep its separate cooperative path;
untranslated infrastructure faults must still fault the scope. This changes the
host contract rather than fixing raw graph exception handling, so it requires
an explicit boundary decision and cannot be presented as the current probe
already matching Team. It is not a host Team façade: only the provider effect
translates the fault; registry, status, capacity, cleanup and isolation stay graph-owned.

If raw-exception isolation is required, the smallest general runtime/design
candidate is **opt-in task fault capture as data at task start**, leaving default
`TaskStart`/`TaskScope` propagation unchanged. The captured task would return an
outer typed failure containing fault evidence, so the owning scope never has an
unhandled exception to rethrow; graph code consumes that outer failure. Merely
making `TaskWait` suppress a fault is insufficient because scope close would
rethrow it again. Merely swallowing exceptions in `TaskScope.Close` would break
existing infrastructure-fault guarantees. Fault categories and cancellation
precedence must be settled before a general capture contract is implemented.

Capture alone also does not invent cleanup. A provider lease should use the
existing graph-owned `UseResource` release so it closes on a raw throw before
the captured task settles; workspace ownership must remain outside that lease.
Compatibility risks are masking an infrastructure fault as ordinary data,
accidentally capturing global cancellation, changing the opted-in task's result
shape, and preserving the original cause when cleanup itself fails. Default
fault propagation and structural scope draining must keep their current tests.

Before/after tests should gate a throwing provider alongside a held healthy
peer, assert the captured failure releases only the failed Member's capacity,
retain its workspace/cause, let the peer complete, and count cleanup once.
Separately test explicit global cancellation still stops/joins both before
closing resources, and that legacy/default uncaptured provider faults still
produce scope failure. These proposals are scoped follow-ups, not implemented
fixes or a claim that either fidelity gap has been resolved.
