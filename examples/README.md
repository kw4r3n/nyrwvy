# Examples

Small, self-contained programs that exercise the maintained authoring tools. Each directory's README gives the exact commands; all of them are run from the repository root after `npm ci` and `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`.

- [`function-abort/`](function-abort/README.md): a deterministic script that authors `readValidated(path, files)` through the `nyrwvy_*` tool handlers, reviews and commits, reopens the snapshot and runs three scenarios with `runPure`. Start here.
- [`run-fixtures/`](run-fixtures/README.md): send saved `runPure`/`runPureByName` request JSON directly through ordinary MCP, with concise case summaries and full evidence saved to disk; includes a RunValue quick reference.
- [`config-request-batch/`](config-request-batch/README.md): a file-backed configured batch with retries, expected failures, host exceptions, cooperative cancellation and resource cleanup; saved cases rerun after reopening and moving a pinned dependency.
- [`scoped-result-handoff/`](scoped-result-handoff/README.md): two independently waiting requests and one receiver that routes responses by ID; scoped one-shot cells, virtual fixtures, saved replay and an unedited real-runtime projection.
- [`member-lifecycle/`](member-lifecycle/README.md): bounded offline Member lifecycle controller, actual Python Team differential oracle, unedited C# runtime checks, and explicit cancellation/fault fidelity gaps.
- [`member-lifecycle-followup/`](member-lifecycle-followup/README.md): no-core cancellable observer and tagged provider-effect adaptations, with raw-fault controls retained.
- [`verification-planner/`](verification-planner/README.md): a four-function pure program kept as a snapshot plus its generated C# runner; its README records how it was authored and maintained through the tools, and `--test --maintained` checks all 1,024 input combinations.
- [`mini-work-planner/`](mini-work-planner/README.md): a next-action planner for one work item, the fixture used by the [Codex CLI MCP maintenance probe](../docs/verification/codex-cli-mcp-maintenance-probe-run-2.md).

The snapshot files are the programs; `runner/Generated.cs` files are projections written by `emitCSharp` and are never edited by hand.
- [`change-verification-batch/`](change-verification-batch/README.md): experimental precommit comparison of real v2 batch formatter changes, explicit intended results and negative controls.
