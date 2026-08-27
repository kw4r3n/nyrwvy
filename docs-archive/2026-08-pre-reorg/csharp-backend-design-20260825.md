# C# backend design gate — 2026-08-25

## Boundary

The first C# backend consumes `ExecutionProgram` and an explicit backend profile only.

Forbidden inputs include:

- semantic `Snapshot`;
- JSON or Nyason persistence;
- `SemanticNode` / `ValueGraph`;
- `TypedSemanticProgram`;
- display names as semantic lookup keys.

The emitter must not repeat semantic name resolution or type inference.

## Backend profile

External behavior is bound by stable capability-operation identity.

Milestone 0 needs one binding kind:

```text
CapabilityOperationId -> SystemConsoleWriteLine
```

A used capability operation without an active binding is a backend-readiness error. Binding is never inferred from an operation display name.

## Generated identifiers

Generated C# identifiers are deterministic backend artifacts, not semantic identity.

The initial profile encodes stable IDs and execution-local IDs injectively as UTF-8 hexadecimal bytes with a domain prefix. This is intentionally verbose but avoids name-sanitization collisions and avoids depending on display names.

## Runtime representation

The first backend uses backend-specific representations without changing semantic or execution IR:

- `String` -> `string`
- `Bool` -> `bool`
- `Unit` -> generated `NyUnit`
- `Option<T>` -> generated `NyOption<T>`
- `Result<T,E>` -> generated `NyResult<T,E>`
- product -> generated sealed record
- union -> generated abstract record plus one sealed record per variant
- capability -> generated interface

Result/Option remain explicit values. Exceptions/null are not used as implicit replacements for language Result/Option semantics.

## Control flow

The C# emitter preserves Execution IR literally instead of reconstructing high-level source structure.

Each `ExecutionFunction` becomes a static C# method:

1. declare deterministic execution-value locals;
2. jump to the entry label;
3. emit one label per execution block;
4. emit instructions in exact list order;
5. implement block-parameter transfer before `goto`;
6. emit terminators as `return`, `goto`, conditional `goto`, or explicit union/Option/Result tests.

This label-oriented form is deliberately straightforward. Pretty generated C# is not a project objective.

## Console binding

The generated capability is an interface. `CapabilityInvoke` calls that interface; it never directly calls `System.Console`.

When the backend profile activates `SystemConsoleWriteLine`, the artifact also contains a concrete adapter implementation whose operation method delegates to `System.Console.WriteLine`.

This keeps the external binding visible and replaceable.

## Determinism

For identical `ExecutionProgram` and backend profile:

- symbol allocation is deterministic;
- declaration/function/block emission order is deterministic;
- generated source is byte-for-byte identical.

## Verification

GitHub Actions capacity is currently exhausted, so Phase J uses the repository-local verification script as its compiler/test boundary. The script will be extended to explicitly re-run C# backend tests and the generated-code integration test.

## Integration proof

The Milestone 0 backend test should:

1. build the representative semantic graph;
2. project Typed Semantic IR;
3. lower Execution IR;
4. emit C#;
5. create a temporary package-free .NET project;
6. compile generated C# with the installed SDK;
7. run a small generated test harness for valid, empty-name, and invalid-email inputs;
8. assert expected Console output;
9. verify missing Console binding fails before source generation;
10. verify repeated emission is byte-identical.
