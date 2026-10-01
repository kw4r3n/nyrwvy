# Test suites

Nyrwvy keeps routine validation small and separates broader execution checks from the default suite.

## fast

Run during ordinary development and CI:

```fish
./scripts/test-fast.fish
```

The fast suite is built from `Nyrwvy.Core.Tests` only, so routine validation does not compile the extended test project or unrelated solution tools. It contains the normal in-process contract, semantic, workflow, IR, codec, query, and backend-generation tests.

A few broad execution tests remain in their feature files to avoid duplicating fixture/setup code. Mark these methods `[<Fact; Trait("Suite", "Extended")>]`. `fast.runsettings` selects `Suite!=Extended`; `extended.runsettings` selects `Suite=Extended`. Routing never depends on a display name or module-name substring, so renaming a test does not change suite ownership.

## extended

Run for larger related changes and milestone validation:

```fish
./scripts/test-extended.fish
```

`Nyrwvy.Extended.Tests` contains milestone end-to-end tests, evaluation corpus/harness integration, and verification integration. Its test modules carry `[<Trait("Suite", "Extended")>]`, inherited by all their cases. Apply that attribute to new extended modules as well; do not move classification into test names. Use `dotnet test ... --list-tests --settings tests/{fast,extended}.runsettings` to audit discovery without executing expensive cases. The extended suite also includes the broader generated-program execution cases kept beside their fast contract tests. It is intentionally not part of per-push CI; the `Extended` workflow (`.github/workflows/extended.yml`) runs it weekly and on demand.

After changing suite ownership, build the solution in Release and run
`node scripts/check-test-suite-partition.mjs`. This discovery-only audit (also run by the `Extended` workflow)
checks that fast and extended are disjoint and cover every discovered case,
including new modules. It does not execute tests or impose historical test-count
gates. An unclassified extended-project module fails the audit instead of silently
falling outside both maintained entry points.

## Adapter toolchain and typechecking

From the repository root, using a current Node 22 release (22.22.2+ recommended for dependency installation) and npm:

```shell
npm ci
npm run typecheck
npm run test:adapters
```

These commands require neither .NET build outputs nor a global TypeScript, OpenCode, or Bun installation. `package.json` and `package-lock.json` own the adapter development dependencies and the `tools/nyrwvy-mcp` npm workspace. Direct versions are exact pins, including TypeScript 5.9.3, Node 22 type definitions, the OpenCode plugin, and MCP packages. `npm ci` restores the entire dependency graph without resolving new versions. Run it from the root, not with an ad-hoc install into `.opencode`.

The adapter runtime minimum remains Node 22.6.0. Clean-install typechecks and adapter tests also pass on Node 22.6.0 / npm 10.8.2, but that npm installation reports `EBADENGINE`: the pinned plugin depends on `effect@4.0.0-beta.83`, whose `ini@7.0.0` dependency declares Node `^22.22.2 || ^24.15.0 || >=26.0.0`. Use a current Node 22 release, as CI does, to satisfy the full development dependency graph; do not override those dependencies just to suppress the warning.

OpenCode manages its ignored `.opencode/package.json`, lockfile, and dependencies independently. Do not edit, track, or require those generated files for repository checks. The TypeScript `paths` entry resolves the plugin's **real published declarations** from the root-owned installation even when OpenCode has installed another local copy. It does not change runtime module resolution: OpenCode keeps its own installation; a clean checkout's Node/Bun contract tests resolve the root installation. All `node_modules` directories are ignored.

The check boundaries are:

- `tsconfig.json` / `npm run typecheck:adapters`: all maintained non-test TypeScript under `.opencode` and `tools/nyrwvy-agent`, plus the shared `.mjs` implementations and MCP server with `allowJs` + `checkJs`. Only Node ambient types are loaded.
- `tsconfig.bun.json` / `npm run typecheck:bun`: all maintained Bun `.test.ts` files and their imports. It loads the official pinned `bun-types/test` declarations for `bun:test`, not Bun's global Node overrides. These tests use Node-compatible APIs and `bun:test`, not Bun-specific runtime globals. The full Bun 1.4.2 ambient bundle has upstream incompatibilities with Node 22 declarations; loading only the API used here avoids those conflicts without skipping library checks.
- Both configs use strict checking, `noEmit`, `allowImportingTsExtensions`, `verbatimModuleSyntax`, and `erasableSyntaxOnly`. `module: Preserve` / `moduleResolution: Bundler` supports OpenCode's Bun modules (including extensionless imports) without depending on the generated `.opencode` package scope. Nothing is transpiled or bundled; Node tests still execute real `.ts` imports with native stripping.
- Neither config disables library checking or introduces browser globals. `node-web-types.d.ts` supplies OpenCode's missing global `HeadersInit` alias from the actual Node 22 `RequestInit["headers"]` contract; it does not replace any implementation with a declaration stub.

The shared schema builder retains each host's native Zod family: OpenCode plugin 1.18.31 uses Zod 4.1.8, while MCP uses Zod 4.3.6. Only the builder operations used by the tools are modeled. Both registrations check each handler against its native input schema; no cross-version schema casts, dependency overrides, or runtime schema conversion are used. `npm run test:adapters` checks both versions' validation and published JSON schemas, Promise-based OpenCode recipe discovery, configuration coverage, and compiler diagnostics from in-memory implementation/schema mistakes. It never edits the checked sources or starts AuthoringSession.

To execute the Bun unit tests, install Bun **1.4.2** separately (the version pinned in CI), then run:

```shell
npm run test:bun
```

Bun is the test runtime only; npm remains the repository dependency/lockfile manager. No `bun install` or generated `.opencode` manifest is needed.

## Node transport and script contracts

The fast wrapper also runs the ordinary-authoring Node tests and the isolated verification-script regression tests:

```shell
node --experimental-strip-types --test .opencode/tests/nyrwvy-authoring-*.test.mjs scripts/tests/*.test.mjs
```

CI additionally verifies the MCP adapter and evaluation bridges. After `npm ci` and building the Release AuthoringSession, run all Node transport/script contracts with:

```shell
npm run test:node
```

Or select an individual boundary:

```shell
npm test --workspace tools/nyrwvy-mcp
node --experimental-strip-types --test .opencode/tests/nyrwvy-evaluation-transport.test.mjs
```

The ordinary workflow and MCP transport tests require `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` beforehand. The evaluation transport and lifecycle-isolation tests use small local Node children, not live models or frozen evaluation reruns. The default fast wrapper does not install or require the extra tooling dependencies; the dependency-backed adapter/typechecking checks above are explicit separate commands. `npm --prefix tools/nyrwvy-mcp run setup` remains a convenience command, now using the root `npm ci` lockfile before building AuthoringSession.

## live / environment-dependent

Model/OpenCode controlled evaluations and environment-dependent preflight checks remain under `scripts/` and are run only when their specific validation boundary is needed. They are not folded into the ordinary xUnit suites.
