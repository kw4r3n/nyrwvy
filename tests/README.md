# Test suites

NyamNyam keeps routine validation small and separates broader execution checks from the default suite.

## fast

Run during ordinary development and CI:

```fish
./scripts/test-fast.fish
```

The fast suite contains the normal in-process contract, semantic, workflow, IR, codec, query, and backend-generation tests. It excludes milestone end-to-end tests, evaluation corpus/harness integration, verification integration, and tests that compile and execute generated projects in child `dotnet` processes.

## extended

Run for larger related changes and milestone validation:

```fish
./scripts/test-extended.fish
```

The extended suite contains the heavier end-to-end and evaluation/verification checks excluded from fast. It is intentionally not part of routine CI.

## live / environment-dependent

Model/OpenCode controlled evaluations and environment-dependent preflight checks remain under `scripts/` and are run only when their specific validation boundary is needed. They are not folded into the ordinary xUnit suites.
