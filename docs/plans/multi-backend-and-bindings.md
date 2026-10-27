# Backend-neutral semantics and generated library bindings (design note)

**Status:** §§2–9 accepted by the owner on 2026-10-27, one question at a time. §1 is a proposed shape, not yet decided. Nothing here is implemented. Each part becomes a decision record when its slice is implemented.

**Date:** 2026-10-27

## Context

The owner wants to write applications that use native and managed libraries (GUI toolkits, Skia, HarfBuzz, libghostty) almost entirely in Nyr, with hand-written host code reduced as far as possible. Adding features to Nyrwvy for this is acceptable. snap shows the cost today: after [the host capability library](host-capability-library.md), its remaining C# is the libghostty P/Invoke layer, Skia drawing, HarfBuzz shaping and Avalonia wiring, all hand-written.

C# is the only backend, and parts of the language contract are written in C# terms. The owner also wants further backends, Zig and JavaScript first (Rust is left out because it has many constructs of its own that would complicate the comparison), and possibly tools that no longer depend on .NET. For Zig, only stable core features count; toolchain churn (for example the removal of `@cImport` in 0.17) is accepted.

A read-only study of both backends (2026-10-27) found that Execution IR (`src/Nyrwvy.Execution`) carries no C#-specific content. The work is in the language contract, the binding model and the tools, which this note covers.

## 1. Layers of an application (proposed)

This shape was proposed but not decided; the owner's interest is reaching libraries from Nyr in general, not a particular GUI architecture. The agreed part is in §8: graph declarations use Nyr types only and per-backend tables bind them.

| Layer | Content | Backend-specific? |
| --- | --- | --- |
| Application | `Model`, `update(model, msg)`, `view(model)` | No |
| Shared vocabulary | View tree, draw commands, `Msg`: pure Nyr data in a library | No |
| Executor | Turns the vocabulary into calls on the raw bindings; diffs views; written in Nyr | Per backend, but Nyr, not host code |
| Raw bindings | Capabilities one-to-one with a library, generated | Per backend |

An application touches only the top two layers, so adding a backend means supplying the lower two. Libraries with a C ABI (libghostty, HarfBuzz, SDL3, Skia's C API) can share raw bindings across C# (P/Invoke), Zig and JS (wasm). A .NET-only library such as Avalonia ties its executor to C#; the layers above it do not change.

## 2. Host failure, exceptions and cancellation

The contract names `System.Exception`, `OperationCanceledException`, dispatch stacks and `AsyncLocal`. It becomes:

- **A host failure is a per-task abort with a kind (`Cancelled` or `Fault`) and a message.** Propagation is unchanged: `UseResource` releases run, a `CancellationShield` restores its caller's token, and the innermost `ExceptionCatch` stops it. C# keeps exceptions, JS uses `throw`, Zig uses error unions on effectful functions with the message in a side slot. `ExceptionIsCancellation` tests the kind, not a type.
- **Allocation failure and stack overflow are fatal, not catchable.** They end the run. Pure code therefore stays infallible on every backend; in Zig, allocating operations need no error union. The owner's position: programs are written so that allocation failure does not happen. This replaces the catchable host failure in [standard operation choices](../decisions/standard-operation-choices.md).
- **`ExceptionRethrow` guarantees the same kind and message.** A diagnostic stack is kept where the backend can, not as semantics.
- **The cancellation token stays ambient per task in the language.** External implementations receive it as an explicit argument instead of reading `AsyncLocal`, and report cancellation through a runtime call that raises a `Cancelled` failure, not by throwing `OperationCanceledException`. Only effectful functions can read `CancellationRequested`, so only they carry the hidden token parameter.

## 3. Operations that wait

- **A capability operation may be marked as waiting**: it can wait for an external event for an unbounded time. This is a property of the operation, not of a backend. The validator propagates it through calls as it propagates effect requirements and shows "may wait" in review. Builtin waits (`TaskWait`, `ParallelAll` joins, `OneShot` receive, delays) are waiting.
- **The default is not waiting.** [`libraries/host`](../../libraries/host/README.md) marks `Fs`, `Process.run` and `Pty.read`. A backend whose mapping table binds an unmarked operation to an asynchronous target reports it.
- **A function can declare that it must not wait**, checked by the validator on every backend. Synchronous entries (a key handler that must return `Handled`, a render callback) use it; on C# it also guarantees the UI thread is not blocked.
- **Lowering:** C# and Zig block a thread. JS makes every function that may wait `async`. This is the same set of functions that carries the hidden cancellation token, since both are effectful.

[Real-time processors](realtime-processors.md) can state their "no waiting" fact with this property instead of excluding every effect.

## 4. String is a sequence of Unicode scalar values

Today a String may hold unpaired surrogates, and concatenation pairs a high and a low surrogate at the seam ([language subset](../reference/language-subset.md), `StringFromCodePoint`, `StringRepeat`; storage described at line 544). It becomes a sequence of Unicode scalar values, as in Swift and Rust, so that many languages accept it natively:

- C# and JS keep native strings, which are then always well-formed UTF-16. Zig stores UTF-8 and passes it to C and Zig libraries without conversion (after flattening, see §8).
- Positions still count code points; each backend keeps an index for its storage, as `NyStr` does for UTF-16.
- `StringFromCodePoint` returns `None` for 0xD800..0xDFFF. A JSON decoder combines `😀` itself.
- Strings entering from a host with unpaired surrogates are sanitized to U+FFFD at the boundary, as `StringEncodeUtf8` already does.
- Run values, snapshot literals and the codecs reject unpaired surrogates. No stored snapshot or saved case needed migration. Implemented; see the [decision](../decisions/string-scalar-values.md).
- Case mapping pins a Unicode version instead of following ".NET runtime Unicode data".

## 5. Callbacks and events

There are no effectful closures; function values stay pure and capture-free. Callbacks are declared in the binding, in three kinds:

1. **Notification**: a widget is created with a Nyr value to deliver on an event (Elm's `onClick msg`). The generated adapter queues it as a `Msg` or calls `update`. Handles never need equality; the delivered value tells events apart.
2. **Synchronous callback** that must return a value (`Handled`, `Render(DrawingContext)`): the mapping table names a function declared "must not wait", which the adapter calls on the spot with the value given at creation. It may have effects, so a render callback can call drawing capabilities.
3. **Callback from a library thread**: always queued, as `PaneSink` does today, so Nyr code never runs on an unexpected thread.

Closures are reconsidered only if passing context explicitly becomes a measured burden.

## 6. Inheritance and override

Nyr gets no inheritance. Overriding is a library calling user code back, so it is a synchronous callback (§5, kind 2). The raw binding layer generates the adapter: a subclass in C#, `class extends` in JS, a function-pointer table or a `ptr + vtable` interface in C and Zig. Upcasts are generated as total operations (`asControl`). Downcasts are generated as `Option` operations only where the backend or the library can check the type (C# `as`, JS `instanceof`, a library-provided tag in C and Zig). Applications do not see either.

## 7. Mutable library objects

Library objects are capability handles ([capability handles](../decisions/capability-handles.md)); every read and write is an effectful operation, so mutation stays in effectful code, in practice the executor.

- The generator emits a bulk setter (`Paint.apply(PaintProps)`, a Product of `Option` fields) beside single setters.
- Out parameters become a result Product. Buffers a library writes are opaque handles or are copied to and from `Bytes`; in-place `Bytes` access is limited to what the [Bytes ownership plan](bytes-and-buffers.md) allows.
- **A capability may be bound to the task that created its handles** (UI-thread objects). The validator rejects passing such a handle to a `ParallelAll` worker or a started task, sequentially or not. This is stricter than the [sharing contract](../specs/parallel-capability-sharing.md), which only requires an assertion for concurrent use. Like waiting, it is a property in the graph.

## 8. Mapping tables and the binding generator

This revises [the host capability library](host-capability-library.md) ("no FFI", Decision 1 "no binding generator") and [semantic independence](../concepts/semantic-independence.md) (a second backend out of scope):

- **External operations stay capabilities**, with effects and fakes as today. Their implementation can be bound by a **per-backend mapping table** instead of a hand-written host class, and generated code calls the target directly. The graph never names targets, so its meaning stays backend-independent. Hand-written implementations (`ExternalImplementation`) remain as the escape hatch.
- **A binding generator is allowed.** Not wrapping per-frame APIs one call at a time becomes performance guidance, not a rule; drawing still goes through a command list handed to the executor.
- **Kept:** application decisions never move into host code. A table holds mechanical mappings only.

Columns of a table, per operation and backend:

| Column | Content | Mostly for |
| --- | --- | --- |
| Target | C function, .NET member, JS export or Zig declaration path | all |
| Conversions | String (native, UTF-8 slice, NUL-terminated copy), `Bytes` (pointer and length), Product to struct fields, Union to enum values, `Option` to null, handle to pointer or object | Zig, C# P/Invoke |
| Ownership | Argument borrowed for the call or kept by the library (reference count raised, or copied); result owned by the caller (with its free function) or borrowed from the library (lifetime of a handle) | Zig, C |
| Allocator | Position of the runtime allocator argument | Zig |
| Failure | How failure is detected (null, errno, negative result, exception, error union) and whether it becomes `Err` or a host failure | all |
| Synchrony | Whether the target returns a promise; checked against §3 | JS |
| Callbacks | Value delivered per event, function called per synchronous callback, queueing for library threads | all |
| Release | Handle release function, or what happens at reference count zero | Zig, C |

Zig reaches C through `extern fn`, `callconv(.c)` and `extern struct` (stable core) and Zig libraries by source import, whose declarations comptime `@typeInfo` can list. A Zig library takes an `Allocator` and slices; a String held as a rope is flattened before it is passed as `[]const u8`.

- **Placement:** a table sits beside the library's graph, one file per backend (for example `host.zig.json`). The generator writes it; manual corrections go in a separate override file that regeneration keeps. The validator checks that every operation is bound or explicitly unavailable on that backend. Tool-only editing, as for snapshots, may come later.
- **Generator:** part of Nyrwvy's tools. It reads C headers, .NET metadata, `.d.ts` files and Zig `@typeInfo`, and proposes declarations as graph operations for review and commit, never by editing snapshot JSON. Library repositories, which applications may take in as git submodules, hold only generated output.

## 9. Tools

The owner may move the tools themselves off .NET later (to Zig, for fewer dependencies), so .NET is not made the standard:

- **The contracts are the formats, not the implementation**: the snapshot format, Execution IR JSON, the authoring-session and MCP protocols, and the runPure wire. All are already JSON. The F# tools are today's implementation. New .NET-specific protocol fields are avoided; `hostSources` gains a backend tag.
- **Correctness is defined by saved run cases' expected values**, written from the specification as today. Every backend, C# included, is checked against them; no backend's output is the reference.
- **`runPure` takes a backend**, with no global default: a workspace declares its target backends. Each backend supplies a runner speaking the same wire: C# keeps the persistent worker; JS runs a Node or Bun child process; Zig builds generated code and a runner into one persistent executable, cached per snapshot because compiling takes seconds. A parity command runs saved cases on every available backend.
- **Two wire changes are needed:** `RunRuntimeFailure` reports §2's kind and message instead of a .NET exception type, and String run values follow §4.
- **Features arrive per backend.** The deterministic virtual-clock scheduler is the costly part for tasks. A backend declares what it supports, and a case using something unsupported is reported as unsupported on that backend, never skipped silently.
- **Escape-hatch host sources** accept the backend's own language, disabled by default as today. Other backends get a facade only when hand-written host code needs one.

## Backend notes

**JavaScript.** Int64 wraps in 64 bits; whether that is `BigInt` or a pair of 32-bit numbers is measured when the backend starts. Strings port directly. `ParallelAll` runs concurrently but not in parallel. Map and Set need structural hashing. The virtual clock needs its own scheduler, because microtask order cannot express "advance only when every task is blocked".

**Zig.** Values cannot form cycles (they are immutable and closures do not capture), so atomic reference counting manages memory completely without a collector, and a count of one permits in-place updates (Perceus-style reuse). Wrapping Int64 maps to `+%`, `-%` and `*%`; checked arithmetic to `@addWithOverflow`. Persistent collections, the scheduler and the reference-count runtime are a large amount of new runtime code.

## Specification text to change

Found by the 2026-10-27 study; only some lines were checked by hand.

- [language subset](../reference/language-subset.md): `ExceptionCatch` on `System.Exception` and `OperationCanceledException` (line 661); `Float64Min`/`Max` defined via .NET `Math.Min`/`Max` (303, restate as IEEE 754-2019 only); case mapping from .NET Unicode data (110); surrogate seams (116, 120); UTF-16 storage (544); stack overflow named as a .NET exception.
- [exception boundary](../specs/exception-boundary.md), [cancellation shield](../specs/cancellation-shield.md) (`NyRuntime.Cancellation`), [concurrency control](../decisions/concurrency-control.md) line 43 (external implementations see the ambient token).
- [standard operation choices](../decisions/standard-operation-choices.md) line 11 (allocation failure as a catchable host failure, reported as `System.OutOfMemoryException`).
- [agent authoring session](../specs/agent-authoring-session.md): `emitCSharp` as the only emit operation, `hostCapability` compiling C#, String run values as UTF-16, `RunRuntimeFailure.exceptionType`.
- `src/Nyrwvy.Backend.CSharp/CSharpBackendModel.fs` lines 7-19: the profile and binding kinds are C#-only types; a backend-neutral profile carries the table and the concurrency assertion.

## Open

- JS Int64 representation (measure first).
- Which Unicode version to pin.
- Whether mapping tables become tool-only.

## Order

Owner choice (2026-10-27): steps 2 and 3 follow the Zig backend; the order is 1, 4, 5, then 2 and 3.

1. Restate the contract: §2, §4 and the specification text above, with C# still the only backend. Done: §4 ([decision](../decisions/string-scalar-values.md)) and §2 ([decision](../decisions/host-failure-kinds.md); the explicit token argument moves to the binding ABI).
2. The waiting and task-binding properties (§3, §7) in the graph and validator.
3. Mapping tables and the generator on the C# backend. Acceptance: snap's `Native.cs` and its other hand-written binding code are generated.
4. `runPure` with a backend parameter, the parity command, and the saved cases as the conformance suite. Started: `runPure` takes `backend` (`csharp` or `zig`); the workspace declaration of targets and the parity command are still to do.
5. A second backend: Zig (owner choice). Started with Int64, Bool and Unit functions, direct calls and tail self calls (`src/Nyrwvy.Backend.Zig`), checked against the C# backend; widened step by step.
