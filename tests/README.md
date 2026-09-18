# Test suites

Nyrwvy keeps routine validation small and separates broader execution checks from the default suite.

## fast

Run during ordinary development and CI:

```fish
./scripts/test-fast.fish
```

The fast suite is built from `Nyrwvy.Core.Tests` only, so routine validation does not compile the extended test project or unrelated solution tools. It contains the normal in-process contract, semantic, workflow, IR, codec, query, and backend-generation tests.

A few broad execution tests remain in their feature files to avoid duplicating fixture/setup code, but `fast.runsettings` excludes the cases that compile and execute generated projects in child `dotnet` processes.

## extended

Run for larger related changes and milestone validation:

```fish
./scripts/test-extended.fish
```

`Nyrwvy.Extended.Tests` contains milestone end-to-end tests, evaluation corpus/harness integration, and verification integration. The extended suite also includes the broader generated-program execution cases kept beside their fast contract tests. It is intentionally not part of routine CI.

## live / environment-dependent

Model/OpenCode controlled evaluations and environment-dependent preflight checks remain under `scripts/` and are run only when their specific validation boundary is needed. They are not folded into the ordinary xUnit suites.
