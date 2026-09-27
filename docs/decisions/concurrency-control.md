# Concurrency control beyond `ParallelAll`

- **Status:** Accepted; phase 1 implemented (no time limit)
- **Date:** 2026-09-25
- **Queue item:** [post-M2 roadmap, immediate execution queue, item 1](../plans/post-m2-roadmap.md#immediate-execution-queue)
- **Related gaps:** [Semantic independence](../concepts/semantic-independence.md#current-gaps): "no abort" and "shared capability under `ParallelAll`"; [Q-010](open-questions.md), [Q-012](open-questions.md)

This note settles the open decisions for the queue item before any construct is implemented. It adds no behavior. Implementation follows the phases below, and the implemented contract belongs in the [expression authoring specification](../specs/expression-authoring.md), which then becomes the authority.

## Problem

`ParallelAll` starts one worker per item, waits for **all** of them, and cancels the rest on the first `Err` or host exception. rrawrlet's team (`team.py`, rrawrlet `d02990a`) needs more than that, and the 2026-09-25 slice probe could not express it:

| Need | rrawrlet | `ParallelAll` |
| --- | --- | --- |
| N1 Start members one at a time, as the leader decides | `spawn` returns an id; members accumulate over many leader turns | All workers start at once from one list |
| N2 Wait for whichever finishes first, with a limit | `wait(ids, mode="any"|"all", seconds<=60)` returns a status snapshot; unfinished members keep running | Waits for all; no limit |
| N3 Stop one member from outside | `stop(id)`: cancel, wait for cleanup, status `stopped` | Only all-cancel on first failure |
| N4 Per-member time limit | `asyncio.timeout(attempt_seconds)`, status `timed_out`, cleanup still runs | None |
| N5 Stop everything on a global condition | token budget reached: cancel every running member | Only through a worker's own `Err` |
| N6 Nothing outlives the team | `close()` stops remaining members before the summary | Holds: no task outlives the node |
| N7 One member failing does not stop the others | failure is recorded as status | Holds, by returning `Ok` with a failed outcome |

N6 and N7 already hold and must keep holding. N1–N5 are the gap.

rrawrlet exposes `spawn`/`wait`/`stop` as separate tool calls over time, which looks unstructured. The same control can be written as **one structured loop** that owns the members: ask the leader for its next action, perform it, repeat, and stop everything when the loop ends. That observation drives the decision below.

## Decision

Add a **task scope**: a structured region that owns individually started tasks. Tasks are waited for and cancelled through handles, and every task has finished when the scope completes. Four constructs, one builtin generic type, and one clock rule.

### Constructs

| Construct | Type | Meaning |
| --- | --- | --- |
| `TaskScope(body, context)` | `Result<T, E>` | Creates a scope value of builtin type `TaskScope` and calls the body function with it (other body parameters come from context, as in `UseResource`). When the body returns, whether `Ok` or `Err`, or raises a host exception, cancellation is requested for every unfinished task, and the node waits for all of them before completing. Result: the body's result. A host exception from the body or from any task is rethrown after all tasks finish; the body's exception wins, otherwise the task started first. |
| `TaskStart(scope, worker, context, item)` | `Task<Result<U, E>>` | Starts the worker function (same shape as a `ParallelAll` worker: one unbound parameter, returns `Result<U, E>`, effects declared by the enclosing function, capabilities via context) as a new task in that scope and returns its handle immediately. Effectful. |
| `TaskWait(tasks, mode, limit)` | `List<Option<Result<U, E>>>` | `mode` is `Any` or `All`; `limit` is `Option<Int64>` milliseconds. Returns when the mode is satisfied (Any: at least one listed task has finished; All: every listed task has) or when the limit elapses, whichever comes first. The result lists every input task in input order: `Some(result)` if it has finished, `None` if it is still running. Tasks keep running after the call returns. `Any` on an empty list returns `[]` immediately. Effectful. |
| `TaskCancel(task)` | `Unit` | Requests cooperative cancellation of one task and returns without waiting. Idempotent; a no-op for a finished task. To observe the outcome, wait for the task. Effectful. |

`Task<T>` is a new builtin generic in the same family as `List`, `Option` and `Result`, so review shows a handle's result type. It has no operations other than the three above. `TaskScope` is a builtin opaque type.

A cancelled task is not a special state. The worker sees `CancellationRequested = true` (and external implementations see the same ambient token, as today), stops at its next check, and returns its own `Result`, typically an `Err` it chose, such as `Stopped`. `TaskWait` reports that result like any other. This keeps `Task<T>` a single-outcome type and puts the "stopped" meaning in the program's own error type rather than in a runtime state.

### Cancellation propagation

Cancellation flows from parent to child and never the other way:

- A task's token is linked to its scope. A scope inside a `ParallelAll` worker or another task inherits that worker's cancellation. When the parent is cancelled, every task in the scope sees `CancellationRequested`.
- `CancellationRequested` in the body of a `TaskScope` reads the body's own inherited token (N5: the body checks a budget and cancels its tasks, or simply returns so the scope cancels them).
- A task's `Err` does **not** cancel its siblings (N7). Only the scope's exit, `TaskCancel`, or the parent cancels tasks. `ParallelAll` keeps its fail-fast contract unchanged.

### Handles after the scope

Values stay managed and copyable ([Q-012](open-questions.md) is unchanged), so a handle can be returned out of its scope. This note defines that case instead of forbidding it: after its scope completes, every handle is finished. `TaskWait` on it returns `Some(result)` at once, and `TaskCancel` is a no-op. No runtime error and no ownership check are needed. A handle from scope A used inside scope B behaves the same way; B's exit does not cancel A's tasks.

### Time

Time enters only through `TaskWait`'s `limit`, in milliseconds of a monotonic clock. There is no time value, no sleep, and no deadline type in this step.

- A per-task limit (N4) is written as wait-with-limit followed by `TaskCancel` for the tasks still running. See the sketch below.
- The C# backend uses `Task.WhenAny` with a `Task.Delay` bound to a monotonic clock.
- `runPure` uses a **virtual clock**, so runs are deterministic. A fake response may carry `delayMillis`. The runner advances virtual time only when every running task is blocked on a delayed or `awaitCancellation` response, and it completes delayed responses in `(due time, task start order)` order. The same program and fakes therefore always produce the same Any-wait outcome. Real elapsed time never affects a `runPure` result.

### Shared capabilities

This states the guarantee that `ParallelAll` currently inherits silently, and it applies to both constructs:

- Operations of one capability value may be invoked concurrently by different tasks or workers. The host implementation must tolerate that; Nyrwvy does not serialize calls.
- Within one task, calls happen in that task's program order.
- A task's calls happen before any `TaskWait` that reports the task as finished returns, and before its scope completes. There is no ordering between tasks that are both running.
- Whether a capability may **declare** that it is not concurrency-safe, so validation could reject such concurrent use, stays with [Q-010](open-questions.md). This note does not add that property.

### Abort

This note does not add a non-local abort. The part of the abort gap that concerns concurrency is covered: leaving a scope early (returning `Err` from its body) cancels and waits for its tasks, and `UseResource` release inside those tasks runs on the tasks' own exits. A general "abort from any depth" for sequential code stays a separate gap in [Semantic independence](../concepts/semantic-independence.md#current-gaps).

## Sketch: rrawrlet's team as one loop

Names are illustrative. `Leader.next` is a capability operation returning a Union `Action = Spawn { assignment } | Wait { members, any, limit } | Stop { member } | Finish`.

```text
run_team(leader, agent, repo, workspaces) =
  TaskScope(body = team_loop, context = { leader, agent, repo, workspaces, members = [] })

team_loop(scope, leader, ..., members: List<Task<Result<Outcome, MemberError>>>) : Result<Summary, TeamError> =
  match leader.next(snapshot(members)) with
  | Spawn a  -> team_loop(..., ListAppendItem(members, TaskStart(scope, member_worker, ctx, a)))       // N1
  | Wait w   -> Bind(TaskWait(w.members, w.any ? Any : All, Some(w.limit)), report -> team_loop(...))  // N2
  | Stop m   -> Bind(TaskCancel(m), _ -> team_loop(...))                                               // N3
  | Finish   -> Ok(summary)                                                                            // N6: scope cancels and awaits the rest

member_worker(agent, ..., assignment) : Result<Outcome, MemberError> =
  UseResource(acquire = workspaces.create(...), release = workspaces.remove, body = member_body)       // cleanup on stop and timeout

// N4, per-member limit, written by the loop:
//   TaskWait([m], All, Some(limit_ms)) = [None]  ->  TaskCancel(m); the worker returns Err(TimedOut) at its next check
// N5, budget: the loop checks the budget after each leader turn and returns Err(BudgetReached); the scope cancels every member.
```

Recursion through `DirectCall` already exists, so the loop needs no new looping construct. The loop holds handles in an ordinary `List`.

## Reason

The task scope meets N1–N5 while keeping N6 and N7, and it keeps task lifetime visible in the graph. The alternatives each miss one of those:

| Alternative | Why not |
| --- | --- |
| `ParallelAny` / race only (first `Ok` wins, the rest are cancelled) | Covers "first to finish" but not N2's "report and keep the others running", N1, or N3. It would be a second fixed policy next to `ParallelAll`. |
| A host-provided `Tasks` capability (`spawn`/`wait`/`stop` as operations) | The workers are Nyrwvy functions, and a capability cannot start one without function values. It would also hide task lifetime from review and validation, which is the thing Nyrwvy is meant to make explicit. |
| Unstructured futures, storable anywhere with no owning scope | Breaks N6: a task could outlive everything that could stop it, and there would be no point where cleanup is guaranteed. |
| Effect handlers or continuations for suspension | Out of scope per [Semantic independence](../concepts/semantic-independence.md#out-of-scope); renaming await as an operation does not define cancellation, lifetime or cleanup. |
| Cancelled as a distinct runtime state of `Task<T>` | Needs a builtin state Union, and it duplicates what the worker already reports through its own `Err`. |
| A time type and a clock capability now | Only `TaskWait` needs time today. A general time value can come later without changing `TaskWait`'s `Int64` milliseconds. |

## Consequences

- Validation gains: `TaskStart` worker shape (same rules and diagnostics family as `ParallelAll`), `TaskWait` element type agreement, `TaskScope` body shape (same family as `UseResource`), and the effect rule that all three are effectful.
- Persistence, typed IR, execution IR, viewer, wire recipes and C# emission each gain the four constructs and `Task<T>`, following the `ParallelAll`/`UseResource` pattern.
- `runPure` gains `Task<T>` in the value model only as far as results are concerned. A handle is not a run value and cannot be an argument or an expectation (`RunBindingPlacement`-style rejection). It also gains the virtual clock and `delayMillis` fake responses.
- The fake-targeting gap (queue item 3) is now addressed by [argument-keyed response queues](../specs/agent-authoring-session.md#argument-keyed-fake-responses), including delayed responses.

## Implementation phases

1. **Scope, start, wait (no limit), cancel.** Builtin `Task<T>` and `TaskScope`. `runPure` without a clock. Acceptance: the sketch's loop, with `Wait` limited to `All`/`Any` without a limit, runs through `runPure` with fakes, including stop-one and scope-exit cleanup.
2. **Time limits (implemented).** `TaskWait` `limit`, the virtual clock, and `delayMillis` fakes. Acceptance: the per-member limit (N4) and an Any-wait whose outcome depends only on scripted delays, both stable across repeated runs.
3. **Revisit.** Q-010 (declared concurrency safety) and the sequential abort gap, only if a probe shows need.

### Phase 1 as implemented

Phase 1 is specified in [expression authoring](../specs/expression-authoring.md) (task scopes) and passes its acceptance test (`TaskScopeTests`): the sketch's loop starts three members, waits for all of one, waits for any, stops one, and finishes while one still runs, all through `runPure` with fakes. It settles details the sections above left open:

- `TaskWait`'s mode is a fixed part of the node, not a value. The limit operand arrives in phase 2.
- `Task<T>` and `TaskScope` may not appear in declared types or capability operations. This keeps handles out of `runPure` values and the host boundary. Storing a handle with a name, as in a `Member { name, task }` Product, is deferred until a probe needs it; the acceptance loop keeps handles in a `List` and selects one by index with `ListEffectFold`.
- `TaskWait` rethrows a host exception from a task it reports as finished, so an `All` wait cannot hang on a faulted task.
- `TaskStart` on a scope that has already completed runs the worker at once, already cancelled.
- A helper function that uses task constructs without declaring an effect is not checked for effect order at its call sites, the same limitation `CancellationRequested` has.

### Phase 2 as implemented

Phase 2 is specified in [expression authoring](../specs/expression-authoring.md) (`TaskWait`) and the [authoring session](../specs/agent-authoring-session.md) (`delayMillis` fakes and the virtual clock), and passes its acceptance test (`TaskScopeTests`): four members with scripted delays give the same Any-wait and limited-wait reports on repeated runs, a member that exceeds a 250 ms limit is reported running, stopped and then observed as stopped, and a run whose tasks all block with nothing due fails instead of hanging. It settles these details:

- The limit is an optional operand of type `Option<Int64>`. An absent operand means `None`, so phase 1 graphs and snapshots stay valid without migration, and a loop can still pass a limit that varies per turn. A negative limit counts as 0. A graph that uses the operand is written with the next schema of every encoding (snapshot JSON 12, Nyason v8, Typed Semantic IR 9, Execution IR 8), so a reader that predates limits rejects it by version rather than by an unknown field.
- `delayMillis` applies to `return` and `throw` fake responses and is not interrupted by cancellation. A fake that should stop when asked uses `awaitCancellation`.
- The virtual clock runs one thread at a time, not only in time order. The note's rule ("advance only when every running task is blocked") alone would still let concurrently running tasks consume fake responses in a timing-dependent order, so a scripted delay could reach the wrong member. Running one thread at a time, in start order, makes the whole run deterministic, which also pins down fake order for `ParallelAll` under `runPure`. It does not by itself solve queue item 3: responses are still assigned by call order, not by argument.
- When a delay and a wait become due at the same time, the delay completes first, so a wait observes everything due at its time.

## Accepted choices

Accepted on 2026-09-25:

1. The task scope (scope + start + wait + cancel) is the direction, rather than stopping at a race construct and leaving N1–N3 inexpressible.
2. Cancelled tasks report through the worker's own `Result`, not a distinct runtime state.
3. Time is limited to `TaskWait`'s `Int64` millisecond limit, with a virtual clock in `runPure`.

## Revisit conditions

- A probe needs a task that outlives its scope, or cross-scope cancellation.
- A capability implementation cannot tolerate concurrent calls, and host-side serialization is not acceptable (Q-010).
- A time value is needed outside waiting (timestamps, schedules).
