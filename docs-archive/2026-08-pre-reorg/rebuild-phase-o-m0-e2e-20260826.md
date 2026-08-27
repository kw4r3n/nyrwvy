# Verified Phase O — Milestone 0 complete lifecycle

## Scope

Phase O does not add a new semantic construct. It binds the already-verified Milestone 0 layers into one reproducible lifecycle test so the architecture is exercised as a system rather than only as isolated phases.

The lifecycle test covers the 15-step scenario in `docs/milestone-0.md`:

1. construct the representative graph without a source-language parser;
2. persist and reload it canonically without losing stable identity;
3. establish semantic readiness;
4. produce a Focus projection/artifact for the validation function;
5. lower through Typed Semantic IR and Execution IR, generate C#, compile, and run;
6. preview a stable-ID-preserving rename;
7. commit the rename and prove observable behavior is unchanged;
8. preview `function.add_parameter` with an intentionally unresolved call site;
9. produce a Change Review slice, semantic diff, and blocking obligation;
10. reject incomplete publication by default, then publish it only through explicit policy;
11. prove Typed Semantic IR projection is blocked while the obligation is open;
12. resolve the obligation through a semantic operation;
13. prove semantic readiness is restored;
14. generate, compile, and execute the resolved program;
15. reproduce canonical snapshot bytes and generated C# source.

## Important persistence correction discovered by the E2E test

The first E2E attempt compared the reloaded `Snapshot` F# record directly with the in-memory fixture using structural equality. That was too strict for the language semantics because canonical serialization deliberately normalizes non-semantic collection ordering.

The correct persistence invariant is therefore:

- serialize -> deserialize -> serialize is byte-for-byte canonical;
- stable snapshot identity is preserved;
- stable semantic entity identities are preserved;
- non-semantic in-memory list order is not authoritative.

The E2E test was corrected to assert those semantic/canonical facts instead of raw record list ordering. No codec behavior was changed.

## Exact local verification evidence

The exact code head verified locally was:

```text
b74de0d660a39dc327a8c364c7ae937720cf0123
```

Environment:

```text
time:       2026-08-26T01:10:57+09:00
branch:     work/m0-e2e-20260826-0055
.NET SDK:   10.0.111
clean run:  yes (bin/obj removed first)
```

Release build:

```text
0 warnings
0 errors
```

Full suite:

```text
93 passed
0 failed
0 skipped
```

Focused verification:

```text
ExecutionIrTests             6 / 6
ExecutionIrCodecTests        3 / 3
CSharpBackendTests           4 / 4
SemanticQueryTests           7 / 7
ChangeReviewQueryTests       3 / 3
ViewerTests                  6 / 6
ViewerArtifactTests          6 / 6
WorkspaceTests               7 / 7
MilestoneZeroEndToEndTests   1 / 1
```

The uploaded TRX counters for every focused suite report:

- `executed = total`;
- `passed = total`;
- `failed = 0`;
- `error = 0`;
- `aborted = 0`;
- `notExecuted = 0`.

Encoding comparison remained unchanged:

```text
json-bytes=8440
nyason-bytes=16205
nyason-to-json-byte-ratio=1.920
```

## System-level result

The complete representative program now proves the intended authority chain:

```text
persistent semantic graph
    -> semantic query / review projections
    -> validated transaction publication
    -> Typed Semantic IR
    -> Execution IR
    -> deterministic C#
    -> .NET compilation and execution
```

Generated C# remains downstream only. The E2E test executes the generated program before the rename, after the rename, and after obligation resolution, and receives the same expected output each time:

```text
valid user
empty name
invalid email
```

## Phase O conclusion

Phase O demonstrates that the independently verified Milestone 0 components compose correctly across persistence, semantic editing, explicit incompleteness, review, lowering, backend generation, compilation, execution, and determinism.

The remaining work after Phase O is no longer a missing Milestone 0 execution path. It is final exit-criteria auditing, documentation of architectural caveats, and planning the next milestone.