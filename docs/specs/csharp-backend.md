# C# backend specification

**Status:** Current

The C# backend is a downstream execution backend. It must preserve Nyrwvy semantic and Execution IR contracts without becoming an alternative source of program meaning.

## Input boundary

The backend consumes:

- backend-neutral `ExecutionProgram` data; and
- an explicit backend profile/binding configuration.

It must not consume authoritative semantic `Snapshot` state, JSON/Nyason persistence, raw semantic value graphs, or display names as semantic lookup keys.

The backend does not repeat semantic name resolution or type inference already completed upstream.

## Capability binding

External behavior is bound through stable capability-operation identity. A required operation without an active backend binding is a backend-readiness failure.

Bindings must not be inferred from display names. The profile supports `SystemConsoleWriteLine` for its existing narrow signature and `ExternalImplementation` for a host-supplied implementation of the **generated typed capability interface**. A host must explicitly bind every operation of an externally implemented capability (by `CapabilityOperationId`) and pass its implementation as the function's capability argument. The emitter generates the interface and symbol manifest, but **no adapter or implementation** for that capability. Mixed Console/external bindings on one capability are rejected; unbound operations remain backend-readiness failures. `emitCSharp` accepts `{ "kind": "externalImplementations", "operationIds": ["<stable ID>", ...] }` as an explicit profile. This profile does not load or execute arbitrary source text.

Shared capability use by `ParallelAll` additionally requires the profile's explicit
`ConcurrentCapabilityIds` assertion (wire: `concurrentCapabilityIds` on an
`externalImplementations` profile). Missing support is diagnosed before emission,
including transitive worker calls and resource releases. No implicit serialization
adapter is generated. See the [sharing contract](parallel-capability-sharing.md).

## Generated identifiers

Generated C# identifiers are deterministic backend artifacts, not semantic identity. Their construction must be injective for the identities they encode and must not depend on mutable display names.

## Runtime representation

Backend representation may vary while preserving the upstream semantic contract. The current backend uses .NET-native or generated support types approximately as follows:

- `String` -> `string`;
- `Bool` -> `bool`;
- `Unit` -> generated unit support;
- `Int64` -> 64-bit signed integer representation with operation behavior fixed by semantic node kind;
- `Float64` -> `double`, with literal construction from exact authoritative bits;
- `Option<T>` / `Result<T,E>` -> explicit generated value representations rather than implicit null/exception semantics;
- products/unions -> generated deterministic .NET types;
- `List<T>` -> backend-local representation that preserves semantic ordering and the accepted immutable-value contract;
- capabilities -> generated interfaces, with adapters only for supported backend-provided bindings.

The concrete .NET storage class is not a language-semantic fact. The generated interface uses these same types for parameters and results, including products and `Result<T,E>`. A host implementation must honor the typed/non-null value contract. Expected domain failure can be returned as `Result.Err`; it is not silently inferred from a thrown host exception. `FailureType` metadata remains unsupported by this backend even for external implementations: it is not an exception-conversion policy, and the backend reports `UnsupportedCapabilityFailure` rather than dropping it. Runtime injection failures and unexpected host exceptions remain host/infrastructure failures, not program `Result` values.

## String equality

`StringEqual` emits exact ordinal, case-sensitive .NET string comparison (`StringComparison.Ordinal`). It must not use culture-sensitive comparison, case folding, or a two-way substring check. `StringContains` retains its distinct substring semantics. `StringConcat` emits explicit `string.Concat(left, right)` without inserting separators, formatting values, or normalizing line endings/Unicode. Both input types and the output type are `String`. `Int64ToString` emits `ToString(CultureInfo.InvariantCulture)`. A generic function becomes a generic method named after its stable type parameter identities (`NyF_…<T_…>`). Calls rely on C# inference from the typed arguments, and generic callbacks are emitted as lambdas rather than method groups so that their type arguments can be inferred. `ParallelAll` emits one block: it starts a `Task` per item with a shared `CancellationTokenSource` stored in the ambient `NyRuntime.Cancellation` (`AsyncLocal`), cancels on a worker `Err` or exception, waits for every task, and then rethrows the first faulted task by index, returns the first `Err` by index, or assembles the `Ok` list in order. The `NyRuntime` class (`Cancellation`, `CancellationRequested`, `WaitForCancellation()`, and the `Run`/`WaitAll`/`Delay` helpers every concurrent start and wait goes through) and `NyVirtualClock` are emitted only for programs that use `ParallelAll`, `CancellationRequested` or task scopes, so other generated sources are unchanged; external implementations may read `NyRuntime.Cancellation` to honor cancellation. `NyRuntime.Clock` is null by default, and the helpers then use real threads and time. A deterministic runner such as `runPure` sets it to a `NyVirtualClock` before calling the entry function. `TaskScope` emits a `NyTaskScope` linked to the ambient token, calls the body in a `try` whose `catch` closes the scope and rethrows, and otherwise closes it after the body returns. Closing cancels the scope's token, waits for every task, and rethrows the first faulted task in start order. `TaskStart` emits `scope.Start(() => worker(...))`, which runs the worker on `Task.Run` with its own `CancellationTokenSource` linked to the scope's and stored in `NyRuntime.Cancellation`. `TaskWait` emits `NyTasks.Wait(tasks, any, limit)`, where an absent limit is `NyOption<long>.None()`. With real time it calls `Task.WaitAny`/`Task.WaitAll` with the limit as a millisecond timeout (negative as 0, above `int.MaxValue` clamped), whose timer is monotonic; under a virtual clock it blocks on the clock. It then snapshots each handle. `TaskCancel` cancels the handle's token source. `INyTask`, `NyTask<T>`, `NyTaskScope` and `NyTasks` are emitted, with `NyRuntime`, only for programs whose functions, instructions or declared fields mention the task types. A declared `Task<T>` field is a `NyTask<T>` record member, held by reference. `UseResource` emits one block: it returns an acquisition `Err` directly; otherwise it calls the body in a `try` whose `catch` attempts release, suppressing only a release exception, and rethrows. Then it releases and combines results, with the body `Err` first and a release `Err` second. `ListEffectFold` emits `Fold` with the folder called by parameter name. In short-circuit mode (result type `Result<S, E>` for seed `S`) it folds over `NyResult<S, E>.Ok(seed)` and, once the state is `Err`, returns it without calling the folder again. `StringParseInt64` first checks that the text is an optional `-` followed by ASCII digits, then uses `long.TryParse` with `NumberStyles.AllowLeadingSign` and the invariant culture only to enforce the range, producing `NyOption<long>` without exceptions. `StringLength` and `StringSlice` walk the UTF-16 text once, treating a high surrogate followed by a low surrogate as one code point and any other code unit as one; `StringSlice` clamps both bounds with `Math.Max(0, …)` and the text end, then emits `Substring` over the mapped offsets, or `""` when the end offset is not after the start.

## Value equality

`ValueEqual` emits `NyEquality.Equal(left, right)`, which calls `EqualityComparer<T>.Default.Equals`. Every value type an equatable Nyrwvy type maps to compares structurally under it: `string` is ordinal, `long`, `bool` and `NyUnit` compare by value, `NyOption<T>` and `NyResult<TOk, TError>` are record structs whose generated equality compares every field, declared Products and Union variants are sealed records whose generated equality compares the runtime type (so different variants differ) and every field, and `NyList<T>` implements `IEquatable<NyList<T>>` by length and then item by item with the item type's default comparer (with a matching `GetHashCode`). Float64, delegates, task handles and capabilities never reach it, because validation rejects those types.

## Int64 floor division

`Int64FloorDivide` and `Int64FloorModulo` emit an explicit block rather than C# `/` and `%`, which truncate toward zero and throw on a zero divisor and on `long.MinValue / -1`. The block returns quotient `0` and remainder `left` for divisor `0`, the `unchecked` negation of `left` and remainder `0` for divisor `-1`, and otherwise adjusts the truncated result: the quotient decreases by one and the remainder adds the divisor when the remainder is nonzero and its sign differs from the divisor's. The generated code therefore never throws and matches the floor semantics in the [language subset](../reference/language-subset.md#int64).

`ListItemAt` emits `list.ItemAt(index)`, which returns `NyOption<T>.Some(item)` when `0 <= index < Length` and `NyOption<T>.None()` otherwise, without exceptions.

## Float64 literal emission

Float64 source construction uses exact bits rather than decimal source text, conceptually:

```text
BitConverter.UInt64BitsToDouble(0x<16 hex digits>UL)
```

This preserves signed zero and exact binary64 encodings and respects the semantic NaN canonicalization performed upstream.

## Control flow

The emitter preserves Execution IR behavior rather than reconstructing high-level source structure or relying on C# surface inference.

Execution functions lower deterministically to generated methods/blocks, with explicit transfer of execution values and explicit branch/return behavior corresponding to the IR.

`BoolNot` emits `!operand`. `BoolAnd` and `BoolOr` reach the emitter as Execution IR branches, not as C# `&&`/`||`: the right operand's block runs only when the left operand does not decide the result, so short-circuit evaluation follows from the branch structure.

## Determinism

For identical supported Execution IR and backend profile, generated C# must be deterministic. Declaration order, symbol allocation, control-flow emission, and runtime support generation must not depend on hash-map traversal or unrelated environment state.

Generated source uses canonical LF (`\n`) line endings, including its final newline, on every host operating system. String-literal contents are not normalized: CR, LF, CRLF, and other characters retain their exact values through C# literal escaping.

## Authority boundary

Generated C# is a build artifact.

- it is not an editable upstream source;
- it does not define semantic identity;
- it does not define Nyrwvy numeric or collection semantics;
- it does not become a persistence format;
- backend limitations do not justify silently changing the semantic graph.

If a semantically valid program cannot be represented or bound by the selected backend, return a backend-readiness/emission diagnostic. See the [failure and obligation policy](failure-obligation-policy.md).

## Historical design evidence

The original Milestone 0 backend design gate and implementation chronology remain in the [pre-reorganization archive](../../docs-archive/2026-08-pre-reorg/csharp-backend-design-20260825.md). This maintained specification intentionally excludes historical CI state and milestone-specific verification instructions.

## Pure function values

Function types lower to invariant `NyFunctionN<..., TResult>` delegates, emitted
only for the arities used by the program. Named monomorphic references lower to
method groups; invocation calls the typed delegate. References remain in entry
reachability, so selecting one entry retains the referenced implementation and
its transitive dependencies. These representations implement the
[pure function-value contract](pure-function-values.md); delegate identity and
captures are not new semantic capabilities.

## Program-defined capabilities

[CapabilityConstruct](program-capability-implementations.md) emits a stateless
class implementing the existing capability interface. Its methods call the
mapped generated Nyrwvy functions with positional semantic correspondence
expressed as named C# arguments. The profile's existing interface binding and
parallel-sharing checks remain in force; internally constructed values require
no host implementation source in the common runner. Typed Semantic IR schema 7
and Execution IR schema 6 carry the operation/function mapping.

## Typed function abort

The backend consumes the explicit Execution IR `abort` terminator (schema 7)
and emits a local typed Err return. Structured branches with no normal completion
do not create merge values. No host exception or hidden default value represents
an abort. Existing UseResource and ParallelAll cleanup/failure policies apply at
the callee/worker Result boundary. See [typed function abort](typed-function-abort.md).
Programs without an abort terminator retain Execution IR serialization schema 6.
