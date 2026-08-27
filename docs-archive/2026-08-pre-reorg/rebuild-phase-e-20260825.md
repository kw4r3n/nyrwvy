# F# Milestone 0 rebuild — Phase E — 2026-08-25

## Status

**Verified for the current Milestone 0 intrinsic, effect, and capability scope.**

The exact semantic-core and test branch tree at `dea036dd544953e8e16936b67a343772dc63aa46` was exercised through pull-request GitHub Actions on 2026-08-25. The PR merge test commit was `3d2855fd28b9c08a1bf5b857dff21cf1d42a1105` against the Phase D rebuild base.

Verification commands:

```shell
dotnet restore NyamNyam.slnx
dotnet build NyamNyam.slnx --configuration Release --no-restore
dotnet test NyamNyam.slnx --configuration Release --no-build --no-restore
```

Environment and result:

```text
.NET SDK: 10.0.400
restore:  success
build:    success, 0 warnings, 0 errors
tests:    29 passed, 0 failed, 0 skipped
```

Documentation-only records are added after this verified source/test tree. No Typed Semantic IR, Execution IR, backend binding, C# generation, viewer, or Nyason parser is included in this verification claim.

## Purpose

Phase E adds only the semantic boundary needed for the Milestone 0 validation/reporting program:

- explicit string predicates required by the chosen validation algorithm;
- explicit effect identities;
- typed external capability contracts;
- explicit capability invocation;
- explicit function effect requirements.

It deliberately does not introduce a generic operator bag, arbitrary host-language calls, asynchronous operations, implicit global I/O, or speculative capability metadata.

## Explicit string intrinsics

The current closed semantic-node taxonomy adds:

```text
StringIsEmpty(valueNodeId) -> Bool
StringContains(valueNodeId, substringNodeId) -> Bool
```

Both operands are validated as `String`. Their result type is fixed as `Bool`.

These are distinct semantic node kinds instead of `UnaryOperation` / `BinaryOperation` nodes carrying operator-name strings. This is intentionally more explicit for AI classification and exhaustive compiler checks.

The set is not a general string library. New intrinsics should be added only when a concrete semantic program requires them.

## Effect identities

Effects are first-class declarations with stable identity:

```text
SemanticEffect
  Id: EffectId
  DisplayName
```

A function explicitly publishes:

```text
RequiredEffects: EffectId[]
```

The validator rejects duplicate or unknown effect requirements.

An effect requirement is a semantic contract, not a cached derived call-graph fact. A function that calls another effectful function must explicitly carry all effects required by the callee. A capability invocation similarly requires its operation's effect to be present in the owning function's declared effect contract.

The validator does **not** silently propagate or insert missing effect requirements.

Extra declared effects are currently permitted. Exact/minimal effect contracts can be revisited once operation support and real Milestone 0 graph evolution provide evidence for whether unused requirements should be rejected or normalized.

## Capability declarations

Phase E introduces typed stable-ID domains for:

- `CapabilityId`;
- `CapabilityOperationId`;
- `CapabilityParameterId`;
- `EffectId`.

A capability declaration has operations, and each operation has its own typed parameter namespace:

```text
SemanticCapability
  Id: CapabilityId
  Operations:
    SemanticCapabilityOperation
      Id: CapabilityOperationId
      Parameters:
        SemanticCapabilityParameter
          Id: CapabilityParameterId
          Type: TypeRef
      ResultType: TypeRef
      FailureType: TypeRef?
      EffectId: EffectId
```

`FailureType = None` represents an operation with no semantic failure value in the current contract. The model does not add exception, retry, cancellation, thread-safety, idempotency, or async metadata because no Milestone 0 validator, planner, backend, or viewer currently needs those facts.

This is the concrete minimal answer to Q-010 for the synchronous Console slice; broader capability contracts remain intentionally open for later evidence.

## Capability values

`TypeRef` now has an explicit capability alternative:

```text
Capability(CapabilityId)
```

A function therefore receives Console as an ordinary typed parameter whose value type explicitly names the capability identity. Capability identity is not reconstructed from the parameter name `console` or any host-language type name.

## Capability invocation is distinct from direct call

External capability use is represented by a dedicated node:

```text
CapabilityInvoke(
    capabilityValueNodeId,
    operationId,
    arguments[]
)
```

Its argument associations use `CapabilityParameterId`, not ordinary `ParameterId`:

```text
CapabilityArgumentBinding
  TargetCapabilityParameterId
  ValueNodeId
```

This prevents an internal function parameter edge from being mistaken for a capability-operation parameter edge at the F# type boundary.

`DirectCall` remains the sole authoritative representation of internal function calls. `CapabilityInvoke` is not encoded as a special function ID or stringly named call.

## Capability validation

For every capability invocation, validation proves:

- the operation ID exists;
- the capability value has `TypeRef.Capability` for the capability that owns the operation;
- every operation parameter is bound exactly once;
- no unknown or duplicate capability-parameter target is accepted;
- each argument value has the declared parameter type;
- the owning function declares the operation's effect requirement;
- the node result type is the operation's declared result type.

Capability operation declarations themselves validate:

- operation and parameter stable-ID uniqueness;
- display-name shape;
- parameter, result, and optional failure type references;
- effect reference existence.

## Function-call effect contracts

`DirectCall` validation now checks the callee's declared `RequiredEffects` against the caller's declared `RequiredEffects`.

This is intentionally explicit rather than transitive inference. If:

```text
report_validation requires ConsoleOutput
main calls report_validation
```

then `main` must explicitly state `ConsoleOutput` in its own effect contract.

A dedicated test constructs a type-correct, argument-complete direct call and verifies that the only missing semantic contract is the effect requirement. This keeps effect propagation validation independent from ordinary argument validation.

## Reference JSON codec

The strict JSON reference codec advances to schema version 4.

It now persists and restores:

- effect declarations;
- capability declarations;
- capability operations and their typed parameters;
- capability `TypeRef` values;
- function effect requirements;
- `StringIsEmpty` and `StringContains` nodes;
- `CapabilityInvoke` nodes and capability-parameter argument associations.

Unknown and duplicate properties remain errors. Non-semantic collection ordering is canonicalized. Parameter ordering remains preserved where it currently has declared meaning.

JSON remains a reference/debug/benchmark codec, not the selected long-term canonical persistence format. Nyason remains the dedicated-format candidate.

## Console proof fixture

The Phase E tests include a minimal typed Console contract equivalent to:

```text
Effect ConsoleOutput

Capability Console
  write_line(text: String) -> Unit
  failure: none
  effect: ConsoleOutput

report(message: String, console: Capability<Console>) -> Unit
  requires: ConsoleOutput
```

The function body obtains the capability and message through ordinary parameter-reference nodes and returns a `CapabilityInvoke` of `write_line`.

Tests separately reject:

- missing effect declaration on the function;
- invoking an operation with a value of another capability type;
- capability argument type mismatch;
- incomplete operation-parameter binding;
- missing caller effect propagation;
- incorrect String intrinsic operand types.

The complete effect/capability graph also round-trips through the strict JSON reference codec.

## Exhaustive-consumer evidence

The first Phase E CI compile failed because an existing test-side exhaustive `SemanticNodeKind` match did not yet enumerate the newly added intrinsic/capability node cases.

The semantic core itself compiled. The failure was resolved by extending the explicit match rather than adding a wildcard.

This is desired behavior for an AI-first closed semantic taxonomy: adding a node kind should force consumers that claim exhaustive knowledge of node alternatives to be revisited rather than silently treating the new construct as an unknown generic case.

## Deliberately deferred

Phase E still does not define:

- effect sequencing when multiple effectful nodes are not connected through a returned value;
- arbitrary intrinsic/operator libraries;
- async, cancellation, concurrency, determinism, or idempotency capability metadata;
- backend capability bindings;
- backend execution readiness;
- operations for adding/removing effects or capabilities;
- obligation invalidation/supersession;
- production stable-ID allocation;
- Typed Semantic IR;
- Execution IR;
- C# lowering;
- viewer query payloads;
- Nyason parsing or encoding.

## Next semantic gate

The next step should construct the complete representative Milestone 0 semantic graph using only verified structures:

```text
RawUserInput
ValidUser
ValidationError
validate_user
report_validation
Console.write_line
```

That fixture should exercise product construction/access, union construction/matching, Result construction/matching, conditionals, string intrinsics, direct calls, capability invocation, and effect contracts together.

Once the representative graph validates and round-trips deterministically, it becomes the shared evidence fixture for:

1. the first Nyason encoding comparison;
2. the Typed Semantic IR boundary;
3. later semantic query/viewer payloads;
4. eventual Execution IR and C# backend work.
