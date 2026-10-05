# Examples

Small, self-contained programs that exercise the maintained authoring tools. Each directory's README gives the exact commands; all of them are run from the repository root after `npm ci` and `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`.

- [`function-abort/`](function-abort/README.md): a deterministic script that authors `readValidated(path, files)` through the `nyrwvy_*` tool handlers, reviews and commits, reopens the snapshot and runs three scenarios with `runPure`. Start here.
- [`verification-planner/`](verification-planner/README.md): a four-function pure program kept as a snapshot plus its generated C# runner; its README records how it was authored and maintained through the tools, and `--test --maintained` checks all 1,024 input combinations.
- [`mini-work-planner/`](mini-work-planner/README.md): a next-action planner for one work item, the fixture used by the [Codex CLI MCP maintenance probe](../docs/verification/codex-cli-mcp-maintenance-probe-run-2.md).

The snapshot files are the programs; `runner/Generated.cs` files are projections written by `emitCSharp` and are never edited by hand.
