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

Bindings must not be inferred from display names.

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
- capabilities -> generated interfaces plus explicit adapters for active backend bindings.

The concrete .NET storage class is not a language-semantic fact.

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
