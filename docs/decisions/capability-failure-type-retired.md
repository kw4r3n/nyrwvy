# Capability operation `FailureType` retired

- **Status:** Accepted
- **Date:** 2026-10-19
- **Roadmap:** [legacy cutover](../plans/post-m2-roadmap.md#legacy-cutover-planned) ("do first" item)

A capability operation could declare an optional `FailureType`. It was accepted on the wire, type-checked, persisted, fingerprinted and listed by `references`, but no component gave it a meaning: an invoke yields the operation's `ResultType` ([expression authoring](../specs/expression-authoring.md)), program implementations and `runPure` fakes ignore it, and the C# backend rejected any program that contained one (`UnsupportedCapabilityFailure`), used or not. No example or fixture declares one. The one program that needed typed capability failures (order 58, the config-request batch) declared `ResultType = Result<String, FetchFailure>` instead.

## Decision

- **Retire it.** An expected failure of a capability operation is its `Result` result type, which the host interface, program implementations, fakes, `verifyChange` and signature migration already support end to end.
- **Authoring rejects it now.** A creation or `addCapabilityOperation` request that sets `failureType` to anything but null is a protocol error naming the `Result` alternative. Absent or null is accepted, so existing requests keep their shape.
- **Readers keep it until the cutover.** Snapshots that already carry a `FailureType` still decode, validate and keep their fingerprint; the C# backend still rejects them. The [legacy cutover](../plans/post-m2-roadmap.md#legacy-cutover-planned) removes the field from the model, the codecs, the IR and the queries, and its migration command rejects a snapshot that sets it (there is no faithful rewrite, since the field has no meaning to preserve).

## Reason

[Q-010](open-questions.md) keeps metadata only when a component enforces its meaning. The alternatives considered:

- **`Result<ResultType, FailureType>` as the invoke type:** a second spelling of what `ResultType` already expresses, at the cost of a kernel typing change.
- **Abort-style propagation** (an invoke inside a function returning `Result<_, F>` ends it with `Err(f)` on failure): the only meaning that is not redundant, but a new control-flow contract that interacts with `Bind` ordering, callbacks, `UseResource`, `ParallelAll` and `ExceptionCatch`, and nothing needs it.
- **Narrowing the backend rejection to reachable uses:** throwaway work once the field is removed.

## Consequences

- `docs/specs/capability-declaration-authoring.md`, `program-capability-implementations.md`, `csharp-backend.md` and the session contract describe the field as retired.
- The cutover's removal list gains `FailureType`; it leaves the "do first" list.

## Revisit conditions

Revisit abort-style capability failures if a program shows that threading `Result` through every caller of an operation costs more than a propagation rule would, with a design note covering the interactions above.
