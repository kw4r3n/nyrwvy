# Source ownership and evaluation compatibility (#211)

**Status:** Observed result

Implemented after `a369e85`, independently of compiler/build-time work in #109.

## Ownership changes

- Production sources now live in Semantics, Execution, Backend.CSharp and Viewer
  directories, matching their owning assemblies and local ordered compile items.
- 38 source files were moved byte-for-byte. The remaining old structural workflow
  was partitioned without changing its implementation: shared structural
  contracts and internal transaction helpers belong to Semantics; the Float64
  compatibility recipe/workflow belongs to `Nyrwvy.AgentEval.Structural`.
- The ListMap compatibility workflow belongs to
  `Nyrwvy.AgentEval.ListMapStructural`. Production authoring uses the maintained
  expression workflow. No production assembly references either evaluation tool.
- The shared namespace remains `Nyrwvy.Core`. Maintained production type ownership
  and signatures are unchanged. Evaluation-only types retain source names but
  now require references to their downstream tool assemblies and recompilation;
  no binary type-forwarder promise is made.
- Trusted evaluation assemblies have friend access to the same internal helpers;
  publication/receipt rules were not duplicated or made public.

Historical frozen revision/path allowlists were intentionally not rewritten.
Their old paths identify original implementation inputs, not today's relocated
code. Current regression suites exercise the new placement. No historical
record was modified and no live-model evaluation was run.

The [architecture guide](../developer/architecture.md#physical-source-ownership)
records the maintained ownership and compatibility boundaries. A new assembly
regression checks shared contracts versus evaluation ownership; the new
[source ownership contract](../../scripts/tests/source-ownership.test.mjs)
checks all four production project compile lists against their physical files.

## Verification of the complete follow-up branch

- Release solution build: zero warnings/errors.
- Maintained fast entry point: 384 xUnit tests and 29 Node script/transport tests.
- Extended entry point: 36 xUnit tests (6 core execution + 30 extended).
- Discovery audit: 384 fast / 36 extended, disjoint and exhaustive.
- Node authoring/evaluation/script tests: 41 passed; real MCP tests: 11 passed.
- Shared adapter/schema/typechecking contracts: 8 passed.
- Bun evaluation adapters: 31 passed.
- Both maintained TypeScript configurations typecheck.

Commands: `dotnet build Nyrwvy.slnx -c Release --no-restore`,
`fish scripts/test-fast.fish --no-build --no-restore`,
`fish scripts/test-extended.fish --no-build --no-restore`,
`node scripts/check-test-suite-partition.mjs`, `npm run test:node`,
`npm run test:adapters`, `npm run test:bun`, `npm run typecheck`.

Related evidence: [SDK/suite routing](build-and-suite-review-follow-ups.md),
[retained dependency pins](retained-dependency-pins.md), and
[scoped computation reuse](scoped-snapshot-computations.md).
