# Ordered expression binding verification — 2026-09-08

**Status:** Observed result

This record covers the implementation of the [Bind design](../decisions/effectful-expression-ordering.md) following Issue #141, built on design commit `a81f7ab`. The [maintained specification](../specs/expression-authoring.md) defines the current contract.

## Local verification

```shell
dotnet build Nyrwvy.slnx --configuration Release --no-restore --verbosity quiet
dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj --configuration Release --no-restore --settings tests/fast.runsettings --verbosity quiet
dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj --configuration Release --no-build --no-restore --filter 'DisplayName~generated C# executes Bind' --verbosity quiet
```

- Release solution build: passed, 0 warnings, 0 errors.
- Fast suite: 239 passed, 0 failed, 0 skipped, including nine Bind contract tests.
- Targeted generated-C# execution: 1 passed, 0 failed, 0 skipped. The generated project also treats compiler warnings as errors.
- Changed Markdown links and code fences checked; `git diff --check` passed.

The generated execution test authors and commits observable provider/message functions, persists/restores the target snapshot, lowers it, and runs emitted C# against a recording Console capability. It verifies the returned capability by reference identity and records:

```text
acquire,message,payload,second
acquire,second,message,payload
skipped
```

This establishes once-only acquisition, sequencing of unused Unit results, message evaluation before invocation, reversed requested order, and no effects in the unselected body. It is deterministic contract evidence, not a live-agent evaluation.

Fast coverage additionally checks lexical depth across empty match frames, initializer access to an outer binding, rejection of self/out-of-scope references and invalid slots/types/identities/cycles, retained ambiguous-effect rejection, storage-order invariance, durable binding identity, review/viewer scope information, fingerprints, predeclared effects, recipe bounds, and stale publication/retry.

## Compatibility and verification boundaries

Snapshot JSON advances to schema 9, Nyason to `nayamnyam.snapshot.v5`, Typed Semantic IR serialization to schema 6, and both fingerprint domains to v6. Execution IR stays at schema 5 and uses existing block parameters/jumps. Old snapshot JSON 8 and Nyason v4 reject explicitly; no implicit migration is supplied. Frozen evaluation protocols and corpora are unchanged.

The solution build also exposed two stale compile links in `Nyrwvy.EvaluationFixtureExport.fsproj`. They now point to the existing corpus modules in `Nyrwvy.Extended.Tests`, where those files already reside. This repairs the exporter build without restructuring fixture ownership or the CI dependency graph.

The generated execution case is selected by the existing extended settings and excluded from the fast settings. No new CI job or model corpus was added. The full extended/model suites were not rerun; the solution build, fast suite, and targeted backend test cover this change's stated claim.
