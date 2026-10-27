# Tools

These are the maintained tools for using Nyrwvy.

## Maintained tools

| Directory | Purpose |
| --- | --- |
| [`Nyrwvy.AuthoringSession`](Nyrwvy.AuthoringSession/) | The JSONL authoring process that owns a snapshot: open/create, inspect, simulate, commit, `runPure`, viewer pages; and the `migrate` command that rewrites a snapshot to the current schema, or with `--to <path.nyrec>` converts it to the record form ([snapshot migration](../docs/specs/agent-authoring-session.md#snapshot-migration)); and `bind-c`, which proposes capabilities and a [C# mapping table](../docs/decisions/csharp-mapping-tables.md) for C functions of a header. A `.nyrec` path is persisted as records, any other path as strict JSON ([persistence format](../docs/specs/agent-authoring-session.md#persistence-format)). Contract: [agent authoring session](../docs/specs/agent-authoring-session.md). |
| [`nyrwvy-agent`](nyrwvy-agent/) | The host-neutral tool definitions and the stdio transport client shared by the OpenCode adapter (`.opencode/`) and the MCP server. Contract: [agent graph tools](../docs/specs/agent-graph-tools.md). |
| [`nyrwvy-mcp`](nyrwvy-mcp/) | The MCP stdio server for Codex CLI, Claude Code, Claude Desktop, Zed and other `mcpServers` clients. Guide: [MCP graph-editing tools](../docs/specs/mcp-graph-tools.md). |
| [`Nyrwvy.ViewerExport`](Nyrwvy.ViewerExport/) | Command-line export of the human-readable Function Focus HTML from a snapshot (`--format json|record`; [viewer](../docs/developer/architecture.md#human-review-and-projections)). |

## Evaluation runners

The evaluation-only runners (`Nyrwvy.AgentEval`, `Nyrwvy.AgentEval.Structural`, `Nyrwvy.AgentEval.ListMapStructural`, `Nyrwvy.AgentEval.PureExpression` and `Nyrwvy.EvaluationFixtureExport`) were removed from `main` on 2026-10-18; they remain reproducible from the [evaluation freeze tags](../docs/operations/evaluation-freeze-tags.md) and from commit `b3a958a`, the last `main` commit that contained them. See the [experiment index](../docs/experiments/README.md) for what is exploratory.
