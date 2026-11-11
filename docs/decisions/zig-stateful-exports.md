# Zig exports that keep state between calls

- **Status:** Accepted, built ahead of trigger (owner decision 2026-11-10)
- **Built:** slices 1 and 2 on 2026-11-10; slice 3 on 2026-11-11 (any type the flattened ABI cannot carry crosses whole as an opaque `ny_value`: a structural type tag checked before anything is copied, the call sharing the argument, one `ny_release` for every type; the check is `tools/zig/emitter/value-handle-check/run.sh`). Rhifill does not use value handles yet: its pane tree still lives in the host. Rhifill's `layoutFrame` bench was not run, so slice 1's cost is unmeasured and the arena-keeping fallback is not built. Differences from the design below: the `Pty` struct is never freed either (a read or write that passed the handle check runs with the lock released and may start after the close, so freeing it would leave that call a dangling pointer; the last of the close and running reads or writes closes the fds); a wrong-capability handle is refused at the export (`nyHandleIs`), and with one handle capability in `libraries/host` only the null case is checked; the 64 KiB read limit is in `host.zig` and the README, and the emitted doc comment of an export that may wait says only that status 3 runs it again; `run-zig.mjs` appends its harness to the emitted source so it can set the checking allocator.
- **Date:** 2026-11-06
- **Plan:** [backend-neutral semantics and generated library bindings](../plans/multi-backend-and-bindings.md#order), Order step 5; §5, §7, §8 and §10
- **Builds on:** [memory ownership in the Zig backend](zig-backend-memory-ownership.md), [capability handles](capability-handles.md), [wait and task-bound attributes](wait-and-task-bound-attributes.md), [C# mapping tables](csharp-mapping-tables.md)

Library mode cannot hold a handle: values live in an arena that is reset when an export returns (`tools/zig/emitter/library_support.zig`, lines 2-4 and 19), every export holds one mutex for its whole run (line 14), and an export can neither take nor return a handle capability (`tools/zig/emitter/main.zig`, `libraryExports`). So `Ptys` and `Pty` in `libraries/host` run only on C#. This record says how to lift that, and whether to do it now.

## Recommendation

**Write this down and do not build it now.** No program needs it today (see [Demand](#demand)). Build the slice a trigger names:

| Trigger | Slice |
| --- | --- |
| A Zig host wants Nyr to own a loop over a handle (Rhifill moves `ptyLoop` or `agentLoop` off its C code, or a second program needs `Ptys` on Zig) | [Counted library](#slice-1-counted-library), then [capability handles](#slice-2-capability-handles) |
| A Zig host duplicates Nyr logic over a value it cannot flatten, and the duplicate caused a defect or real change pain (Rhifill's native pane tree is the candidate) | [Counted library](#slice-1-counted-library), then [value handles](#slice-3-value-handles) |
| A second kind B library ([plan §10](../plans/multi-backend-and-bindings.md#10-backend-specific-libraries)) has handles | As the first row |

The ABI gap that had actually been hit was smaller and unrelated to state: `Option` and record results. It is closed (see [Result types](#result-types-come-first)).

## Demand

**Rhifill's native host needs no state in Nyr.** Paths are under `rhifill/`.

- It owns its PTY in C: `openpty` at `spikes/native-pane/src/bridge.c:403`, a non-blocking fd at `:457`, polled from its own loop (`:613`).
- It calls Nyr only for pure rules, with scalars and flat lists: `ny_layoutFrame` (`spikes/native-pane/src/main.zig:420`), `ny_pickPane` (`spikes/native-pane/src/panes.zig:214`), `ny_busyStart` (`bridge.c:349`), `ny_isAgentName` and the combined `ny_agentNext` (one call returning both the alert and the state, `bridge.c:367-369`). Its exports are listed in `app/nyr/workspace.json.backends.json`; there is no `zigTables`.
- Its state lives in the C `Host` struct (`agent_state`, `last_output`, `busy_since`, `bridge.c:50`), goes in by value on each call and comes back through an out parameter (`bridge.c:138-143`, `:157-159`). That replaces the C# `agentLoop` (`app/Program.cs:139`) with one call per transition. The state is three integers, so the copy is trivial.
- `app/nyr/workspace.json` has no `mustNotWait` function.

**The other users stay on C#.** The C# Rhifill is the only user of `Ptys` (`docs/nyrwvy-boundary.md:40`, `app/Program.cs:89`), and it works. rrawrlet declares `{"targets": ["csharp"]}` (`rrawrlet.nyrec.backends.json`) and does not use `Ptys`.

**Friction that exists, neither of it a handle.** `docs/native-host-probe.md:110`: `configLine` (a record) and `parseCellRatio` (an `Option` result) were rejected by `emitZigLibrary`, so a small parser moved to the host. `docs/native-host-probe.md:94`: the recursive `PaneTree` does not cross, so the native host keeps a bounded tree of its own, called prototype orchestration there. Only the second could use state, and flattening has served so far.

## Alternatives

**A. Host-held state, Nyr as pure rules (today).** The host owns the PTY and copies state in and out. It costs nothing and is what real use chose. Its limit: loops over a handle and non-flat state stay in host code, and `Ptys` stays C#-only. **Chosen until a trigger fires.**

**B. Close is the release; handle records are not counted (below).** No change to retain and release sites, the same model as the C# tables, and use after close is always a defined failure. **Chosen when a trigger fires.**

**C. Counted capability handles.** `counted` (`main.zig`, about line 1609) and `nyCounted` start to include capabilities, the last reference closes the resource, and hosts get `ny_retain` and `ny_release`. This catches a forgotten close, but it changes every retain and release site for capability-typed bindings, and running a close at an arbitrary release point conflicts with a close that may wait (`Ptys.close` waits up to 2 s, then until the child is reaped) and can fail. C# has no equivalent. Rejected: nothing has been hurt by a forgotten close that `UseResource` would not catch.

An integer ID into a table in the Zig file needs no emitter change, but it is the forgeable handle [capability handles](capability-handles.md) rejected for Strings, so it is not considered.

## Design

No semantic node, no snapshot schema change, no change to the validator. Three slices; 2 and 3 do not depend on each other.

### Slice 1: counted library

Values kept past a call, and values in use while the lock is released, cannot live in the reset arena.

- **Storage.** Library mode sets `ny_values` to `std.heap.smp_allocator` unless it is already set, as the runner does (`runner_support.zig:305`). A test harness may set a checking allocator first. Reference counting stages a to e already emit the releases.
- **Exports.** After copying its result, an export does `nyRelease(result)`. Argument conversion validates every String before moving any argument, because an early return can no longer rely on the arena.
- **Scratch.** A declaration's `Allocator` argument becomes an arena created for that host call and freed after `nyHost` copies the result, not the shared `ny_arena`. It wraps the thread-safe `smp_allocator`, so a declaration can use it with the lock released.
- **The arena.** `nyExportLeave` no longer resets. `ny_arena` holds only runtime records (task and cancellation records, `runtime.zig` lines 41-43), the same process-life shortcut the runner has; programs that use tasks stay untested in library mode.

Rhifill's `layoutFrame` runs on this path every frame, so measure it before and after with its existing bench. If it gets slower by an amount the owner rejects, a library with no table and no handle keeps the arena: one emitted constant checked in `nyExportEnter` and `nyExportLeave`. One mode is preferred if the numbers allow it.

### Slice 2: capability handles

**What survives where.**

| Thing | Storage | Lives until |
| --- | --- | --- |
| The declaration's struct (`Pty`, holding fds) | The declaration's own allocation, never the `Allocator` argument | Its close declaration frees it |
| Handle record (`open` flag, type tag, pointer to the struct) | `smp_allocator` | Process exit (see below) |
| Bytes or text a declaration returns | Per-call scratch, copied to counted storage | End of the host call |
| Nyr values in a frame blocked on a read | Counted storage | The frame releases them |

**C ABI.**

```c
typedef struct ny_handle ny_handle;   /* opaque, one type for every handle capability */

/* Result<Pty, String>: status 0 sets *out; status 4 is Err, its text through ny_last_failure. */
int ny_openShell(const char *a0, size_t a0_len, int64_t cols, int64_t rows, ny_handle **out);
/* Arguments are borrowed. A handle stays valid memory for the life of the process. */
int ny_write(ny_handle *a0, const uint8_t *a1, size_t a1_len);
int ny_closePty(ny_handle *a0, int64_t *out);   /* closes the handle */
size_t ny_open_handles(void);                   /* handles not yet closed */
```

These are exports of ordinary Nyr functions, named as today (`ny_<function>`). There is no `ny_release` for a capability handle: **close is the release**, the operation the program declares, as in the C# tables ([C# mapping tables](csharp-mapping-tables.md)). A handle crosses as a pointer to its record. A null pointer or a handle of another capability (the tag) gets status 1, not a crash.

A Nyr wrapper function is the export's normal shape: `Ptys.open` takes `List<String>` and `Map<String, String>`, which have no ABI, and a wrapper builds them from Strings.

**Wrapping.** The table marks the capability and the closing operation:

```json
"Pty":  { "handle": "Pty", "operations": {
  "read": { "declaration": "ptyRead" }, "write": { "declaration": "ptyWrite" },
  "resize": { "declaration": "ptyResize" }, "foregroundCommand": { "declaration": "ptyForeground" } } },
"Ptys": { "operations": {
  "open": { "declaration": "ptyOpen" }, "close": { "declaration": "ptyClose", "closes": "pty" } } }
```

Two optional fields, so `version` stays 1. `"handle"` names a Zig type in `source`; C#'s table has a flag, but Zig has to name the type. `"closes"` names the parameter, the same fact as C#'s `release: true`. `zig_mapping.zig` reads both and also copies the operation's `mayWait` from the graph into `HostBinding`, so the table holds no waiting fact.

Declarations take the struct pointer where the handle goes:

```zig
pub fn ptyOpen(allocator: Allocator, argv: []const []const u8, env: Env, cols: i64, rows: i64) !*Pty
pub fn ptyRead(allocator: Allocator, self: *Pty) !?[]const u8
pub fn ptyClose(allocator: Allocator, pty: *Pty) !?i64
```

`C_<capability>` gains a `handle` field for a handle capability. `hostValue` accepts a handle capability in a result, including inside `Result` and `Option`, and accepts `Map<String, String>` as a pair of key and value slices. A table-bound capability without `"handle"` keeps today's stateless form, supplied by the export.

**Close, use after close, leak.**

- Every operation on a handle checks `open` under the lock. If it is false, the call fails with the host failure "Handle is not open", the message C# uses (status 1).
- A `closes` operation clears `open` and lowers the open count under the lock, then calls its declaration. So a second close is the same defined failure.
- The record stays allocated for the life of the process (about 24 bytes). That is what makes these failures safe with no generation table or reference count, and it is why Nyr frames and Products may hold a capability, which `counted` does not track, without a dangling pointer. This leaks one small record per handle ever opened; free it, with an ABI that says when, if a program opens enough handles to matter.
- A handle the program never closes keeps its fd and child until the process exits. This matches C#, where the program's `UseResource` is the guard. `ny_open_handles` lets a host or a check see it.
- A `taskBound` handle capability is rejected at emission for now. A host calling from any other thread is not checked.

**The lock and operations that wait.** The rule is one mutex, released around anything that may wait:

- An operation marked `mayWait` calls its declaration with `ny_export_lock` released and relocks before `nyHost` copies the result. This is the rule [memory ownership](zig-backend-memory-ownership.md) section 4 already states for `ny_gate`. It applies to every marked operation, so `Fs` and `Process.run` stop stalling other exports too.
- It is safe because counts change only under the lock and the declaration touches no count. The frame's reference pins the arguments, so no other thread can reuse their storage, and the declaration's allocator is the per-call scratch. Decision A (plain counts) still holds.
- That covers Nyr state, not the declarations. A declaration bound to a `mayWait` operation must be safe to run concurrently with any other declaration; this is the table author's contract, like agreement with the Nyr body for kind A. `host.zig` routes everything through `std.Io.Threaded.global_single_threaded` (lines 6-8), so that shared Io is the first thing this slice checks or replaces.
- `Ptys.close` may run while another thread is blocked in `Pty.read`, which then returns `None` (`libraries/host/README.md`). The close takes the lock only to clear `open`. In `host.zig`, `read` polls the master fd and an eventfd, `close` signals the eventfd, and the last of the two to leave closes the fd and frees the struct. This synchronisation belongs to the declaration; the emitter does not know about it.
- A declaration must not call an export, since the mutex is not recursive.

**`Pty.write` must be marked `mayWait`.** A full PTY buffer blocks a write until the child reads, and under one mutex an unmarked write would then stall every export. Rhifill already treats it as blocking: a queue thread in C# (`app/Program.cs:213-223`), a pending queue natively (`spikes/native-pane/src/bridge.c:249-264`). Marking it is a correction to `host.json` through `setMayWait`.

**"An export that may wait must not be called from a function declared not to wait."** The validator cannot see a C caller, so this stays the host's duty, with the fact made visible:

- `emitZigLibrary` adds `"mayWait": true` and `"waitPath"` to an export's entry in its reply, the fact `tools/zig/host/query.zig` (line 1331) already reports, and the generated doc comment says "may wait".
- An export of a `mustNotWait` function is already proved not to wait by validation.
- Every export can still wait on the mutex. After this slice, that wait is bounded by other exports' CPU work and unmarked declarations, never by an external event. That fits what `mayWait` means, but a long pure export still delays a UI thread. Measure before changing it.

**Errors.**

| Status | Meaning |
| --- | --- |
| 0 | ok |
| 1 | fault, including "Handle is not open", null and wrong handle |
| 2 | cancelled (still reserved) |
| 3 | result buffer too small |
| 4 | new: `Err` of a `Result<_, String>` result, text through `ny_last_failure` |

Status 3 means "call again", which re-runs the export. For `readText` that is harmless; for `Pty.read` the chunk would be lost. So `ptyRead` reads at most 64 KiB, the export's doc comment states the size, and a host must pass a buffer at least that large. A host passing a smaller buffer loses a chunk; if this bites, the export keeps the result for the next call.

**Cancellation.** No new ABI now: closing the handle from another thread is what ends a blocked read, and status 2 stays reserved. `ny_token` stays null in an export, so `CancellationRequested` reads false. `Fs` and `Process.run` are not interruptible. When a host needs to cancel them, the existing plan applies: a token handle argument the host signals ([memory ownership](zig-backend-memory-ownership.md), section 3), plus an fd in the declaration's arguments to wake on.

**Callbacks.** Not needed for this slice. A Zig program binds a sink capability like `PaneSink` in its own table to a declaration in its own Zig file, so `ptyLoop` runs inside one long export on the host's reader thread and delivers through that declaration without a function pointer crossing the ABI. Function-pointer callbacks (plan §5) stay out of scope.

### Slice 3: value handles

Any non-generic Nyr value a host cannot flatten: String, Bytes, Product, Union, List, Set, Map, and `Option` or `Result` of them, including Products holding a capability handle. Tasks, exceptions and scopes stay rejected.

```c
typedef struct ny_value ny_value;     /* opaque: type tag, release function, one owned reference */

int ny_treeEmpty(ny_value **out);
int ny_treeSplit(const ny_value *a0, int64_t pane, ny_value **out);   /* a new value; a0 unchanged */
int ny_treePick(const ny_value *a0, int64_t pane, int64_t dx, int64_t dy, int64_t *out);
void ny_release(ny_value *value);     /* takes the lock; NULL does nothing */
```

- **State is explicit.** The host holds a `ny_value*` and replaces it with the result of the next call. Nothing global exists in the library, so old versions stay valid and values stay immutable.
- **One generic `ny_release`** replaces the `ny_free_<type>` that [memory ownership](zig-backend-memory-ownership.md) section 3 expected: the box stores its own release function, so no per-type symbol is needed.
- **No `ny_retain`.** The host holds one reference per box and arguments are borrowed. A host with two owners counts them itself.
- **Fields are read through exports** written in Nyr. There are no generated accessors.
- **A wrong tag or null is status 1. A released box is undefined,** like `free`; the generated facade prevents it, and the host is trusted as [memory ownership](zig-backend-memory-ownership.md#consequences) already says.
- **Borrowed arguments cost a copy.** The export shares the argument into the call, so its count is at least 2 and count-of-one reuse does not apply: an update copies the top box and any list it sets an item in. Appends to shared lists still avoid this (stage d). Accept it until a measured workload suffers; then add an export marker for a consumed argument.

### Result types come first

`Option` and Product export results landed in [kw4r3n/nyrwvy#637](https://github.com/kw4r3n/nyrwvy/pull/637), which answers `configLine` and `parseCellRatio` (`docs/native-host-probe.md:110`). `Pty.read` returns `Option<Bytes>` and `Ptys.open` a `Result`, so slice 2 still needs these or Nyr wrappers around them:

- `Bytes` as `ptr, len` and `out, out_cap, out_len`, like a String without the UTF-8 check;
- `Result<T, String>` as status 4.

## What C# needs for parity

C# already has the semantics: a generated handle class, `release: true` as the close, and the same "Handle is not open" failure. `Ptys` and `Pty` need nothing. Each item below is needed only when a C# host P/Invokes a stateful Zig library:

- the facade (`csharp_facade.zig`) emits a class holding the `ny_handle*`, whose calls map status to exceptions;
- a `SafeHandle` over `ny_value*` whose `ReleaseHandle` calls `ny_release`;
- `mayWait` from the reply as a doc comment.

One difference remains: neither backend closes a forgotten handle, but C#'s object is collected and Zig's record stays.

## How to test

**No saved case can prove slice 2:** `runPure` never loads a table, and a fake can only `throw` from an operation returning a handle ([capability handles](capability-handles.md), point 2). The ordinary pure functions behind slice 3 keep their saved cases.

- **Slice 1.** `libraries/host/check/run-zig.mjs` sets a checking allocator in its harness and asserts nothing is live after its calls. Rhifill's `tools/check-zig-layout-cases.mjs` and `tests/layout` still pass, and its bench gives the before and after.
- **Slice 2.** `run-zig.mjs`, which today covers everything "except for the pty", gains the checks `check/Program.cs` makes against `NyHost.cs`: open, write, read, resize, foreground command, exit code. New: while one thread is blocked in `read`, a second thread's export returns (the lock is released); `close` from that thread makes `read` return `None`; use after close and a second close give status 1 and the C# message; a wrong-capability handle gives status 1; `ny_open_handles` is 0 at the end and nothing is live. Emitter unit tests in `main.zig` cover a handle capability without its table marks and the `taskBound` rejection.
- **Slice 3.** A harness builds a value over many updates, checks old versions still read correctly, releases everything and asserts nothing is live (`tools/zig/emitter/value-handle-check/run.sh`, which also checks a wrong tag and a null box against status 1).
- **Reply change.** The `mayWait` entries change `emitZigLibrary`'s reply, so run `scripts/test-extended.fish`.

## Size and scope

| Slice | Files | Notes |
| --- | --- | --- |
| Result types | 3 | `main.zig`, a check, the as-built text |
| 1. Counted library | 4 | `library_support.zig`, `main.zig`, `run-zig.mjs`, the as-built text; plus the bench run in Rhifill |
| 2. Capability handles | 9 | `main.zig`, `library_support.zig`, `zig_mapping.zig`, `emit_zig_library.zig`, `host.zig` (the most code: spawn, read, close), `host.zig.json`, `host.json` (`Pty.write`), `run-zig.mjs`, `libraries/host/README.md` |
| 3. Value handles | 4 | `main.zig`, `library_support.zig`, a check, the as-built text |

The smallest first step of the handle path is slice 1. Alone it gives a host nothing and puts the hot path at risk, so it waits for a trigger too.

**Out of scope:** function-pointer callbacks, record and out-struct results beyond the types above, tasks across the ABI, a cancellation token, `taskBound` handles, two Nyr libraries in one process, PTYs off Linux with glibc (C#'s needs that too), and a generator for the table.

## Consequences

- When built, this replaces in [memory ownership](zig-backend-memory-ownership.md): the per-call arena reset (its stated revisit condition), the `ny_free_<type>` naming and "Handle capabilities are not supported" in the as-built section.
- Without the reset, `ny_arena_capacity` no longer measures values; a memory check on a counted library uses process RSS or a checking allocator.
- `ny_last_failure` also carries status 4's `Err` text, so its wording becomes "until its next failure or `Err`".
- A handle record is a small permanent allocation, and a forgotten close keeps its resource until exit.
- A host must pass a large enough buffer to a stream read.

## Revisit conditions

Build on the [triggers](#recommendation). Reconsider alternative C if a forgotten close causes a real leak that `UseResource` would not have prevented; freeing records if opens reach a count where they matter; borrowed arguments if a measured workload suffers the copy; and the whole record if Rhifill drops the native host or retires C#.
