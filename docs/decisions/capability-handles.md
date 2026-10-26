# Host handles as capability values

- **Status:** Proposed
- **Date:** 2026-10-26
- **Issue:** none yet

This decision proposes how a host hands a program an opaque handle (an open file, a connection, a process stream): an acquiring capability operation returns a **capability value**, and the handle's operations are that capability's operations. No new type kind is added. The route was checked by a port outside `runPure`; `runPure` bindings still need one change.

## Decision

**A handle is a capability value.** The program declares a capability for the handle, for example `OpenFile` with `read`, `write` and their effects, and an issuing capability, for example `Files` with `open(path): Result<OpenFile, String>` and `close(file: OpenFile): Unit`. The operation's result type is `TypeRef.Capability OpenFile`. The host's `ExternalImplementation` of `Files.open` returns its own object implementing the generated `OpenFile` interface; the program uses it only through `CapabilityInvoke` on that value.

**What it already guarantees.** Capability values carry no observable allocation identity and have no equality operation ([program-defined capability implementations](../specs/program-capability-implementations.md)), so a handle cannot be compared, forged from a String or Int64, printed or used as a Map key. A program reaches the handle's operations only through the exact capability that owns them ([expression authoring](../specs/expression-authoring.md), `CapabilityInvoke`). Effects of the handle's operations are declared by the enclosing function as for any capability.

**Lifetime.** `UseResource` with `acquire = Files.open(path)`, `R = OpenFile` and release `Files.close` scopes the handle: release runs on the body's Ok and Err exits and before a host exception propagates. The type system does not prevent use after release. An operation invoked on a released handle is a host failure (the implementation throws), as `Host.cs.template` does today with "Handle is not open"; it is not reported as `Option` or `Err`, following the host-failure choice of [standard operations with an owner choice](standard-operation-choices.md).

**Sharing across tasks.** A handle passed to `ParallelAll` workers or started tasks is a shared capability value, so the existing [sharing contract](../specs/parallel-capability-sharing.md) applies unchanged: concurrent use requires the profile's `ConcurrentCapabilityIds` assertion for the handle's capability.

**Boundaries.** Capability values are already excluded from run values: `runPure` accepts capability arguments only as top-level `fakeCapability` or `hostCapability` bindings. The [standard host runner](standard-host-runner.md) serves only functions without capability parameters. `TaskTypeAtCapabilityBoundary` is unaffected, because a handle is not task-bearing.

**Checked on 2026-10-26** by porting `examples/config-request-batch` to an `OpenFile` handle (branch `claude/openfile-handle-port`, not merged):

1. **The route works outside `runPure`.** Authoring, validation and commit accept `open(root): Result<OpenFile, String>` and `close(handle: OpenFile): Unit`. `emitCSharp` emits `OpenFile` as a generated interface, and the host's `Files.open` returns its own `OpenFile` object through it. Compiled directly against the generated source, `runBatch` gave the expected outcome for the success, retry, invalid-configuration and host-exception fixtures, each with exactly one `close disposed`, and a second `close` on the same handle failed with "Handle is not open". The host needed no lookup table. The cancellation fixture needs the virtual clock, so it was not run this way.
2. **`runPure` cannot bind the issuing capability yet.** A capability bound as `hostCapability` or `fakeCapability` must have run-value types in every operation's parameters and result (`PureRun.fs`, `fakeProblem`), so binding `BatchFiles` fails with `RunBindingTypeUnsupported`. The reason is the call log: the `Host{n}` adapter records each call's arguments, and `EncodeCall` has no form for a capability argument. Results are never recorded for host bindings, so the result half of the check is stricter than a host binding needs.
3. **`runPure` already rejects a capability result.** An entry `echoControl(control: CancellationControl): CancellationControl` fails with `RunResultTypeUnsupported`; no new rejection is needed. A Product holding a capability goes through the same type predicate but was not run.

**Change needed before acceptance.** Host and fake bindings accept capability-typed operation parameters and results; a recorded capability argument appears in `calls` as an opaque marker (generated `EncodeCall`, the session-side call decoding, the `calls` section of the [agent authoring session spec](../specs/agent-authoring-session.md), `PureRunBindingTests`). A fake *response* for an operation that returns a capability stays rejected, because no run value can script it. The saved cases of the ported example need this change.

## Reason

The one port with a host-side resource shows the cost of not having handles. `examples/config-request-batch` passes a `String` as the handle: the host keeps a `ConcurrentDictionary<string, FileStream>` keyed by it, looks it up in every operation, and throws "Handle is not open" for a missing key (`Host.cs.template`). Any String the program builds is a potential forged or stale handle, the handle can be compared, logged and stored as data, and the host owns a table whose only purpose is to map a name back to an object.

A capability value removes all of that with contracts that already exist: opacity and no equality from program-defined implementations, scoping from `UseResource`, cross-task rules from capability sharing, and exclusion from run values from the `runPure` binding rules. The host keeps the object itself, so the lookup table and its missing-key path disappear.

**Rejected: a new opaque handle type kind.** A `TypeRef` kind for handles owned by a capability (as `Exception` and `OneShot<T>` are builtin opaque types) would need a new semantic entity through every layer: the semantic graph, validation, codecs, typed and execution IR, the C# backend, the viewer, run values and both recipe wire forms. It would also need its own rules for equality, run values and sharing, which capability values already have. It adds nothing a capability value cannot express unless a handle must exist without operations of its own.

**Rejected: affine or linear handle types.** Preventing use after release statically needs ownership or use-once checking across calls, callbacks and task boundaries. The [roadmap](../../docs-archive/2026-09-general-purpose-programming/roadmap.md) already set ownership and borrowing aside, and `UseResource` with a host failure on misuse covers the measured case.

## Consequences

- No snapshot schema, codec, IR or backend change is needed; the one required change is the `runPure` binding and call-log rule above.
- A capability declared only to be a handle is an ordinary capability: it appears in review, needs a host binding when invoked, and follows declaration visibility.
- A program-defined `CapabilityConstruct` can implement a handle capability with pure functions, which is useful for tests and fakes. It has no state, so it cannot model an open/closed lifecycle; that remains the host's.
- Host implementations become simpler: the issuing operation returns the object, and handle operations act on `this`.

## Out of scope

- A handle escaping its `UseResource` body through the body's result `T`. Nothing prevents it, as nothing prevents a `Task<T>` from escaping a scope body today; use after release is then a host failure.
- Host-to-program callbacks, mutable cells shared between tasks, and handles crossing the standard host runner's wire.

## Revisit conditions

Revisit when the `runPure` binding change proves larger than the call-log marker, when a handle must exist without operations of its own, or when use-after-release failures are observed often enough to justify static checking.
