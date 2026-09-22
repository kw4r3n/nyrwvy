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

## Node transport and script contracts

The fast wrapper also runs the ordinary-authoring Node tests and the isolated verification-script regression tests:

```shell
node --experimental-strip-types --test .opencode/tests/nyrwvy-authoring-*.test.mjs scripts/tests/*.test.mjs
```

CI additionally verifies the MCP adapter and evaluation bridges. To run those contracts locally after building the Release AuthoringSession, prepare their existing dependencies once:

```shell
npm install --prefix tools/nyrwvy-mcp --package-lock=false --no-audit --no-fund
npm install --prefix .opencode --no-save --package-lock=false --no-audit --no-fund @opencode-ai/plugin@1.18.31
npm --prefix tools/nyrwvy-mcp test
node --experimental-strip-types --test .opencode/tests/nyrwvy-evaluation-transport.test.mjs
```

The evaluation transport tests use small local Node children, not live models or frozen evaluation reruns. The pinned plugin bootstrap is explicit because OpenCode manages its own ignored `.opencode/package.json`; the default fast wrapper does not install or require these additional packages.

## live / environment-dependent

Model/OpenCode controlled evaluations and environment-dependent preflight checks remain under `scripts/` and are run only when their specific validation boundary is needed. They are not folded into the ordinary xUnit suites.
