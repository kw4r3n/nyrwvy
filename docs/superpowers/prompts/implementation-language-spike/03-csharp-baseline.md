# Setup Session 3 — Reviewed C# Baseline

You are implementing only the C# baseline for a controlled implementation-language experiment.

Read:

- `docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike.md`
- `docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike-automation-addendum.md`
- every file under `experiments/implementation-language-spike/shared/public/`
- the shared expected fixture files

## Scope

Implement Task 4 from the main plan in:

```text
experiments/implementation-language-spike/csharp/
```

The C# baseline must implement the shared version-1 contract exactly and provide the public API, CLI behavior, tests, and deterministic canonical JSON required by the plan.

## Isolation

Do not inspect, search, read, or modify:

```text
experiments/implementation-language-spike/fsharp/
```

Do not infer design from any F# implementation or patch. The experiment requires an independently natural C# implementation of the same shared contract.

## Do not modify

- `shared/public/`;
- `shared/expected/`;
- evaluator requirements;
- maintenance-task requirements;
- experiment scoring rules;
- warning strictness;
- the planned forbidden-feature list.

If the shared contract is internally contradictory, stop and report the exact contradiction. Do not silently reinterpret it.

## C# constraints

- Target exactly `net10.0`.
- Use no third-party NuGet packages.
- Enable nullable reference types.
- Treat warnings as errors.
- Use immutable or privately owned read-only collections at public domain boundaries.
- Represent closed domain states with abstract records and nested sealed records.
- Enumerate every known domain case explicitly in switch expressions or statements.
- Do not use a `default` arm to absorb domain cases.
- Do not use `dynamic`, unchecked casts, reflection, `null!`, warning suppression, or mutable global state.
- Use manual canonical JSON through `System.Text.Json`.
- Preserve function parameter order and call argument order while sorting ID-keyed collections canonically.
- Do not retain caller-owned mutable lists inside immutable domain records.
- Keep existing snapshots immutable.

## Required behavioral evidence

Public tests must demonstrate at least:

- blank stable IDs are rejected;
- the canonical fixture round-trips byte-for-byte;
- function rename preserves `FunctionId` and call targets;
- stale-base rename rejects without mutating the base;
- add-parameter appends exactly one parameter;
- add-parameter creates the required missing-argument obligation;
- add-parameter returns the incomplete state;
- construction-order differences do not change canonical output.

## Completion checks

Run:

```bash
dotnet build experiments/implementation-language-spike/csharp/Nayamnyam.Spike.CSharp.sln
dotnet run --project experiments/implementation-language-spike/csharp/tests/Nayamnyam.Spike.Tests
python experiments/implementation-language-spike/scripts/run_public_checks.py \
  --language csharp \
  --workspace experiments/implementation-language-spike/csharp
```

The last command may be unavailable until integration code exists. When unavailable, report that precisely; do not create a private substitute contract.

Commit only the C# baseline and its tests. Finish with:

- files created;
- public API summary;
- checks run and exact results;
- any shared-contract ambiguity found;
- commit SHAs created by this session.