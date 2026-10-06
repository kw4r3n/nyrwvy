# Communication between tasks, and time (design note)

**Status:** Decided 2026-10-07: the owner accepted all three recommendations below (A: measure `OneShot` on probe 25's code before designing a channel; D: no time value yet; no zero-time yield). The measurement is [probe 26](../verification/task-communication-probe26-20261007.md): the `OneShot` version passed the same scenarios as the event loop at about the same size (491 and 508 nodes), so no channel is queued. The analysis below is kept as written, except for the correction in section 1.

## Context

[Probe 25](../verification/rrawrlet-slice-probe25-20261005.md#costs-ranked-by-impact) ported rrawrlet's `codex.py` and recorded three concurrency costs. Each was a first observation, and nothing was blocked:

| # | Cost | Probe 25 evidence |
| --- | --- | --- |
| 1 | No futures or channels between tasks | `_read` resolves a future that `request` awaits from another task. The reader could not be a task, so it was folded into the caller as one event loop over a 25-field state: 40 functions and 1,283 nodes for the reader, the rounds and the requests |
| 2 | No time value | Every timeout is a task that calls `Host.sleep`, and a turn's deadline is passed into every request inside the turn, because `TaskWait`'s limit covers one wait and a later wait cannot compute the time left |
| 3 | No zero-time yield | One harmless divergence: asyncio ran a ready task between two steps at the same virtual instant, which the port cannot write |

The day after the probe, #362 added [scoped one-shot cells](../specs/scoped-one-shot.md) (`OneShot<T>`, `OneShotCreate`, `OneShotComplete`, `OneShotWait`). A cell is owned by an existing `TaskScope`, takes the first value, and closes with its scope. This is the structured form of a future: the [concurrency decision](../decisions/concurrency-control.md) rejected futures with no owning scope, not this. Cost 1 therefore has to be asked again: does `OneShot` remove the reader cost, and if not, what is still missing?

## Invariants to keep

- Every task, cell and wait belongs to a scope, and nothing survives its scope ([concurrency decision](../decisions/concurrency-control.md), N6).
- `runPure` stays deterministic: real elapsed time never affects a result, and the virtual clock advances only when every running task is blocked.
- The order of two events at one instant is not part of a program's meaning. The one-shot contract already states that completing a cell does not introduce a yield.

## 1. Communication between tasks

### What `OneShot` covers

The [scoped result handoff example](../../examples/scoped-result-handoff/README.md) has a receiver task that routes responses by ID to two request tasks, each waiting on its own cell with its own timeout. This is probe 25's reader shape, with one difference: both cells are created before the receiver starts and are passed to it as start arguments (`run.mjs:58`). In `codex.py`, `request` is called again and again after the reader has started, and each call registers a new future in a shared dictionary the reader reads.

### What is still missing: registering a cell with a running task

A running task sees only its start arguments, and Nyrwvy has no shared mutable state. A request created mid-turn therefore cannot hand its cell to the reader through a field the reader already holds. It should be able to do so through a chain of cells, using only the existing surface. This has not been authored yet; the re-port in option A would confirm it:

- A declared type `Mailbox = { request: Request, next: OneShot<Mailbox> }`. A field may hold `OneShot<T>`, and recursive declarations are supported.
- The reader holds the current tail cell. A requester completes the tail with its registration, which carries the requester's reply cell and the next tail cell. If `OneShotComplete` returns false, another requester won; the loser waits on that cell, reads its `next` and tries again.
- To wait for a pipe chunk and a registration at once, the reader starts one task per source (one for `Host.read`, one for `OneShotWait` on the tail) and uses `TaskWait(Any)`. This is what the probe already does for reads and timers.

Whether this chain is cheaper than the probe's event loop is the open question. It may well not be: every requester must find the current tail, which itself needs a cell or an argument passed down.

**Correction (probe 26).** In `codex.py` only the sequential driver issues requests, so one producer is enough and no requester races another for the tail. [Probe 26](../verification/task-communication-probe26-20261007.md) wrote it that way. Option B's case is now a program with several concurrent producers into one consumer, or one where racing a cell against a task is a large share of the cost.

### Options

| Option | What it is | Why not, or what it costs |
| --- | --- | --- |
| A. No change; measure first (recommended) | Port probe 25's reader, rounds and requests again, onto `OneShot` and the mailbox chain above, and compare with 1,283 nodes and 40 functions, and with the 489/490 case results | No language change. If the chain is awkward, the measurement shows where, and option B is then decided on evidence |
| B. A scoped channel | `Channel<T>` owned by a `TaskScope`: send never blocks and returns false once the channel is closed; receive takes items in order, or returns `None` once the channel is closed and drained, or when the receiving task's cancellation is requested. A select is not needed, because a receive in its own task composes with `TaskWait(Any)` | Three or four new nodes, a builtin generic, a schema version, and the same lifetime, race and virtual-clock work that `OneShot` needed. Multiple senders at one instant need a deterministic order under `runPure`; start order is the natural rule, but it must be stated and tested. One program is the only evidence |
| C. Shared mutable state (a scoped cell that can be overwritten, or a shared `Map`) | The closest copy of Python's `_pending` dictionary | Makes ordering between tasks part of a program's meaning, which N6 and the determinism rule are meant to prevent. Not recommended |

## 2. A monotonic time value

### Whether the revisit condition is met

The concurrency decision's [revisit condition](../decisions/concurrency-control.md#revisit-conditions) is "a time value is needed outside waiting (timestamps, schedules)". Probe 25's need is a deadline that spans several waits. That is still waiting, so the condition is only arguably met. The owner decides.

### Why it would have to be a builtin

The obvious alternative needs no language change: a `Host.now` operation, as probe 25 already has `Host.sleep`. It does not work under `runPure`, because a fake response is scripted in advance and cannot read the virtual clock (`NyRuntime.Clock`, [C# backend](../specs/csharp-backend.md)). A fake `now` returns its scripted values whatever the virtual time is, so a deadline computed from it disagrees with the delays that actually elapse. Only a builtin can return the virtual clock under `runPure` and a monotonic clock otherwise.

### Options

| Option | What it is | Why not, or what it costs |
| --- | --- | --- |
| D. No change (recommended unless a second program needs it) | Keep passing the deadline down, and keep timers as tasks that call `Host.sleep` | 26 nodes and a `timers` parameter in probe 25. The cost is small and visible |
| E. `MonotonicNow(): Int64` | Effectful, in milliseconds. The origin is unspecified, so only differences have meaning. Under `runPure` it returns the virtual time, starting at 0. The time left before a deadline is `deadline − MonotonicNow()`, passed as `TaskWait`'s limit, so `TaskWait` is unchanged, as the decision foresaw | One node and no type. It is not a run value, so it cannot be scripted. Wall-clock time (timestamps for display or logs) stays out and needs its own decision |

## 3. A zero-time yield

Recommended: not added. A yield would make the order of two events at one instant part of a program's meaning, against the third invariant above. The one-shot contract made the same choice deliberately. The only evidence is one harmless divergence in one probe.

## Choices for the owner

1. **Communication:** A (measure `OneShot` and a mailbox chain on probe 25's code first) or B (design a scoped channel now). Recommended: A. The measurement would be recorded as a probe, and B is reconsidered if the chain is clearly worse than the event loop.
2. **Time:** D (no change) or E (`MonotonicNow`). Recommended: D, until a second program needs a deadline across waits. If E is chosen, it is a small order that can be done without waiting on choice 1.
3. **Yield:** confirm that it is not added.
