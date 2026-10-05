# Tools

Two kinds of programs live here. A user of Nyrwvy needs only the first group.

## Maintained tools

| Directory | Purpose |
| --- | --- |
| [`Nyrwvy.AuthoringSession`](Nyrwvy.AuthoringSession/) | The JSONL authoring process that owns a snapshot: open/create, inspect, simulate, commit, `runPure`, viewer pages. Contract: [agent authoring session](../docs/specs/agent-authoring-session.md). |
| [`nyrwvy-agent`](nyrwvy-agent/) | The host-neutral tool definitions and the stdio transport client shared by the OpenCode adapter (`.opencode/`) and the MCP server. Contract: [agent graph tools](../docs/specs/agent-graph-tools.md). |
| [`nyrwvy-mcp`](nyrwvy-mcp/) | The MCP stdio server for Codex CLI, Claude Code, Claude Desktop, Zed and other `mcpServers` clients. Guide: [MCP graph-editing tools](../docs/specs/mcp-graph-tools.md). |
| [`Nyrwvy.ViewerExport`](Nyrwvy.ViewerExport/) | Command-line export of the human-readable Function Focus HTML from a snapshot ([viewer](../docs/developer/architecture.md#human-review-and-projections)). |

## Evaluation runners (not needed to use Nyrwvy)

| Directory | Purpose |
| --- | --- |
| `Nyrwvy.AgentEval` | M1/M2 controlled-evaluation protocol runner (`evaluation/m2-agent-corpus-v1`). |
| `Nyrwvy.AgentEval.Structural` | M2 structural-editing evaluation runner. |
| `Nyrwvy.AgentEval.ListMapStructural` | M2 List/Map structural evaluation runner (`evaluation/m2-listmap-agent-corpus-v1`). |
| `Nyrwvy.AgentEval.PureExpression` | Exploratory scoped pure control-flow runner (`evaluation/pure-control-flow-exploratory-v1`); driven by `scripts/run-pure-control-flow-exploratory.fish` and `scripts/validate-scoped-pure-control-flow.fish`. |
| `Nyrwvy.EvaluationFixtureExport` | Exports evaluation fixtures from the semantic model. |

All of them are in `Nyrwvy.slnx`, so the solution build compiles them, but their fixtures and model runs are replayed from the [evaluation freeze tags](../docs/operations/evaluation-freeze-tags.md), not from `main`. See the [experiment index](../docs/experiments/README.md) for what is exploratory.
