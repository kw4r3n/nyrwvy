# F# Milestone 0 rebuild — Phase J C#/.NET backend — 2026-08-25

## Status

**Verified through a clean local compiler/test and generated-program execution boundary.**

The exact verified code head was:

```text
90df91e54b512426479485dfd88d773067b0baa0
```

Verification environment and result:

```text
.NET SDK: 10.0.111
clean:     bin/ and obj/ removed before restore
restore:   success
build:     success, 0 warnings, 0 errors
full test: 63 passed, 0 failed, 0 skipped
ExecutionIrTests:       6 passed
ExecutionIrCodecTests:  3 passed
CSharpBackendTests:     4 passed
```

The Phase J integration test emits C# from `ExecutionProgram`, writes it into a separate package-free `net10.0` project, builds that project, executes the generated assembly, and verifies this observable output:

```text
valid user
empty name
invalid email
```

GitHub Actions capacity was exhausted during this work, so the clean local run and TRX results are the accepted verification evidence.

## Hard backend boundary

The C# backend consumes only:

```text
ExecutionProgram
+
CSharpBackendProfile
```

It does not consume or inspect:

- `Snapshot`;
- JSON or Nyason persistence;
- semantic `ValueGraph` / `SemanticNode`;
- `TypedSemanticProgram`;
- display names as semantic lookup keys.

The emitter therefore cannot silently redo semantic name resolution or type inference.

## Generated identity

Generated C# identifiers are backend artifacts, not semantic identity.

Stable semantic IDs are encoded injectively into deterministic C# identifiers. Human display names are intentionally not used for semantic lookup or symbol uniqueness.

The tests explicitly verify that generated source does not recover identifiers from names such as `validate_user`, `report_validation`, or `RawUserInput`.

## Runtime representations

The first backend uses explicit C# runtime shapes for the M0 subset:

- `String` -> `string`
- `Bool` -> `bool`
- `Unit` -> `NyUnit`
- `Option<T>` -> `NyOption<T>`
- `Result<T,E>` -> `NyResult<T,E>`
- product -> generated sealed record
- union -> generated abstract record plus generated sealed variant records
- capability -> generated interface

Result and Option remain explicit values; null/exceptions are not substituted for their language semantics.

## Control flow

Execution IR control flow is preserved directly.

Each execution function is emitted as a static C# method with:

1. deterministic execution-value locals;
2. explicit entry jump;
3. one label per execution block;
4. instructions in exact Execution IR order;
5. explicit block-parameter transfer;
6. `return`, `goto`, branch, or match tests for terminators.

The backend does not reconstruct a new high-level control-flow model from semantic graphs.

## Capability binding

External behavior is bound by stable `CapabilityOperationId` through `CSharpBackendProfile`.

M0 supports:

```text
CapabilityOperationId -> SystemConsoleWriteLine
```

A used capability operation without an explicit binding fails before source generation.

The generated semantic capability remains an interface. A generated adapter implements that interface and delegates the configured operation to `System.Console.WriteLine`. Semantic capability invocation never becomes an implicit global Console call upstream.

Partial concrete capability adapters are rejected: if a capability is materialized as a concrete backend adapter, all operations required by that adapter must have explicit compatible bindings.

## Determinism

The tests require repeated emission from identical `ExecutionProgram` plus backend profile to produce identical `CSharpArtifact` values.

Symbol allocation, declaration order, function order, block order, and instruction emission are deterministic.

## M0 architectural result

Phase J completes the first executable path:

```text
Persistent Semantic Graph
    -> Typed Semantic IR
    -> Execution IR
    -> deterministic generated C#
    -> .NET compilation
    -> execution
```

Generated C# remains downstream and non-authoritative.

## Next gate

After the executable path was verified, the next missing M0 surface was observability rather than execution:

- AI-facing focused queries;
- caller/callee/dependency/use projections;
- open-obligation visibility;
- Change Review projection;
- then a read-only viewer consuming those projections.
