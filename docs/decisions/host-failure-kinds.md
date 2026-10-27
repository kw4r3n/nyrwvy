# Host failure kinds

- **Status:** Accepted
- **Date:** 2026-10-27
- **Plan:** [backend-neutral semantics and generated library bindings](../plans/multi-backend-and-bindings.md#2-host-failure-exceptions-and-cancellation), §2

The failure contract was written in .NET terms: `ExceptionCatch` caught a `System.Exception`, `ExceptionIsCancellation` tested for `OperationCanceledException`, rethrow kept a dispatch stack, and allocation failure was a catchable host failure reported as `System.OutOfMemoryException`. A Zig or JavaScript backend has none of these, and a catchable allocation failure would make every allocating pure operation fallible in Zig.

## Decision

- **A host failure is a per-task abort with a kind and a message.** The kinds are `Cancelled` and `Fault`. Propagation is unchanged: `UseResource` releases run, `CancellationShield` restores its caller's token, and the innermost `ExceptionCatch` handles it. `ExceptionIsCancellation` is true exactly for `Cancelled`. Each backend realizes this its own way; C# keeps .NET exceptions, with `Cancelled` as `OperationCanceledException` and its subclasses.
- **Allocation failure and stack overflow are fatal.** They end the run; no `ExceptionCatch` handles them, and releases are not guaranteed to run. The C# backend emits `catch (Exception e) when (e is not (OutOfMemoryException or InsufficientExecutionStackException))`; a .NET stack overflow was never catchable.
- **`ExceptionRethrow` guarantees the same kind and message.** A diagnostic stack is kept where the backend can (C# rethrows the original object).
- **`runPure` reports `failureKind`** (`fault`, `cancelled`, `fatal`) on `RunRuntimeFailure`. `exceptionType` stays as the C# backend's diagnostic; other backends will fill it with their own names or leave it out when the runPure backend parameter arrives.
- **External implementations observe the task's cancellation through their backend's binding.** C# keeps the ambient `NyRuntime.Cancellation` for now. Passing the token as an explicit argument, as the plan proposes, belongs to each backend's binding ABI and is done with the mapping tables (plan §8) and the Zig backend, so hand-written C# hosts are not changed twice.

## Reason

Every backend can implement the kinds with its own mechanism (exceptions, `throw`, error unions). Making allocation failure fatal keeps pure code infallible everywhere; the owner's position is that programs are written so allocation failure does not happen.

## Consequences

- A program that recovered from an over-large `ListRange` or `StringRepeat` with `ExceptionCatch` now stops instead; none is known.
- `ChangeVerification` still classifies by `exceptionType` (its tests build failures with `Kind = "fault"` even for the fatal types); it moves to `failureKind` with the runPure backend parameter.

## Revisit conditions

Revisit if a real program must recover from allocation failure (for example a server that rejects one over-large request), which would need an explicit bounded-allocation operation rather than a catchable failure.
