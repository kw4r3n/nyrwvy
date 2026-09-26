# Program-defined capability implementations — 2026-09-26

**Status:** Observed result

The implementation, tests and this record are introduced together on top of
`6b70cc0` (the roadmap/history separation). The contract is
[program capability implementations](../specs/program-capability-implementations.md).

The maintained [CapabilityImplementationTests](../../tests/Nyrwvy.Core.Tests/CapabilityImplementationTests.fs)
created two implementations of one Capability using ordinary symbolic recipes,
reviewed publication and reopen. The same consumer returned 10 or 20 according
to the selected implementation. A two-argument operation returned `LR` with
operation arguments supplied in reverse wire order, preserving declaration-order
correspondence with differently named function parameters. Targeted replacement
switched the implementation while preserving function and target-node IDs.

A pinned consumer still returned 20 after its source snapshot was removed. Two
parallel workers sharing a program-defined value both returned 10. Neither run
needed host sources or fake responses. Entry reachability kept every mapped
function, including the otherwise unused operation implementation.

Fast checks covered canonical JSON/Nyason round trips and mapping-order
normalization, explicit new schemas and downgrade rejection, focused reads and
HTML mapping visibility, missing/duplicate operations, signature mismatch,
generic or transitively impure implementations, undeclared effects, unknown
functions and later incompatible edits/deletion. Prior snapshot encodings remain
unchanged when no construction node is present.

## Commands and results

- `dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~CapabilityImplementationTests'`: **4 passed**, including two generated-execution cases marked Extended.
- `dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj -c Release --no-restore --settings tests/fast.runsettings`: **475 passed**. The recipe-authority inventory now includes the new typed recipe; IR-version assertions match schemas 7 and 6.
- `npm run typecheck`: both adapter and Bun configurations passed.
- `npm run test:adapters`: **8 passed**, including discovery of every recipe in both tool hosts.
- `npm run test:node`: **41 Node contracts and 11 MCP tests passed**.
- Changed documentation's relative links and `git diff --check`: passed.

This is deterministic contract and integration evidence, not a new live-model
evaluation. It does not establish stateful/effectful implementations, resumable
handlers, or raw capability values across the common runner's result protocol.
