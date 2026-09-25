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

`StringEqual` emits exact ordinal, case-sensitive .NET string comparison (`StringComparison.Ordinal`). It must not use culture-sensitive comparison, case folding, or a two-way substring check. `StringContains` retains its distinct substring semantics. `StringConcat` emits explicit `string.Concat(left, right)` without inserting separators, formatting values, or normalizing line endings/Unicode. Both input types and the output type are `String`. `Int64ToString` emits `ToString(CultureInfo.InvariantCulture)`. `ListEffectFold` emits `Fold` with the folder called by parameter name. In short-circuit mode (result type `Result<S, E>` for seed `S`) it folds over `NyResult<S, E>.Ok(seed)` and, once the state is `Err`, returns it without calling the folder again. `StringParseInt64` first checks that the text is an optional `-` followed by ASCII digits, then uses `long.TryParse` with `NumberStyles.AllowLeadingSign` and the invariant culture only to enforce the range, producing `NyOption<long>` without exceptions.

## Float64 literal emission

Float64 source construction uses exact bits rather than decimal source text, conceptually:

```text
BitConverter.UInt64BitsToDouble(0x<16 hex digits>UL)
```

This preserves signed zero and exact binary64 encodings and respects the semantic NaN canonicalization performed upstream.

## Control flow

The emitter preserves Execution IR behavior rather than reconstructing high-level source structure or relying on C# surface inference.

Execution functions lower deterministically to generated methods/blocks, with explicit transfer of execution values and explicit branch/return behavior corresponding to the IR.

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
