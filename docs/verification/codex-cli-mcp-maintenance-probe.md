# Codex CLI MCP maintenance probe — 2026-09-21

**Status:** Observed result (environment-dependent validation).

Run 2 is the configuration-corrected follow-up; this record preserves the initial blocked result. See [the other run](codex-cli-mcp-maintenance-probe-run-2.md).

## Result

**Valid blocked probe; primary success not demonstrated.** One live Codex CLI invocation discovered and selected a Nyrwvy MCP tool, but the host refused its first call before the snapshot was opened:

```text
MCP tool call requires approval, but approval policy is never
```

The observed boundary is Codex's MCP approval gate in this noninteractive invocation. This is not evidence of a Nyrwvy semantic, schema, transport, or relative-path resolution failure. In particular, submitting a relative path does not establish that the server resolved it successfully. No implementation was changed and no second live trial was run.

## Execution conditions

- Started: 2026-09-21T20:44:40+09:00 (Asia/Tokyo); completion observed by 20:45:15+09:00.
- Host: Codex CLI `0.155.0`, `codex exec`, local stdio MCP.
- Model requested: `gpt-6-astra`; the JSON event stream does not independently identify the backend revision.
- Nyrwvy commit: `ab74ecc435e919da76536ecff98402f2c75ba551`.
- Fixture source: `9f03d5e7:mini-work-planner/planner.snapshot.json`.
- Disposable active project: `/home/kw4/Work/Tools/nyrwvy/artifacts/codex-mcp-probe/workspace`.
- Fixture path given to the model: `planner.snapshot.json`.
- Setup: existing `npm --prefix tools/nyrwvy-mcp run setup`; Release build succeeded with zero warnings/errors. No new helper, harness, or tests.
- Existing unrelated working-tree change: `verification-planner/transcript.jsonl`, untouched.

Effective MCP configuration, supplied through CLI overrides:

```toml
[mcp_servers.nyrwvy]
command = "/home/kw4/.local/share/mise/installs/node/26.8.2/bin/node"
args = ["--experimental-strip-types", "/home/kw4/Work/Tools/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
```

Neither `--workspace` nor MCP `cwd` was configured. CLI options were `--ignore-user-config --ephemeral --skip-git-repo-check`, `-C` set to the disposable project above, `-m gpt-6-astra`, `-s workspace-write`, `--json`, and `-o` for the temporary final response. The prompt was supplied on stdin. Approval policy was not explicitly overridden; the host reported `never`. User configuration was ignored to avoid unrelated user MCP servers. Normal authentication remained available. CLI exit status was 0, which indicates completion of the model turn, not task success.

## First user prompt (verbatim)

```text
この project の planner.snapshot.json は Mini Work Planner の既知の pre-maintenance state です。
Mini Work Planner の ExecutionState に fieldless variant TimedOut を追加し、既存の Failed と同じ behavior を持たせる。既存の semantic identities は維持すること。Nyrwvy MCP tools を使って変更を inspect、review、commit し、close/reopen 後に committed state を確認すること。snapshot JSON は直接読んだり編集したりせず、generated source も直接編集しないこと。snapshot は project-relative path で開くこと。
```

No operation name, stable ID, or tool-call sequence was supplied in the prompt.

## Minimal live evidence

The host emitted this completed call event (only the envelope ID is omitted):

```json
{
  "type": "mcp_tool_call",
  "server": "nyrwvy",
  "tool": "nyrwvy_open",
  "arguments": {"mode": "open", "snapshot": "planner.snapshot.json"},
  "result": null,
  "error": {"message": "MCP tool call requires approval, but approval policy is never"},
  "status": "failed"
}
```

The model then used the shell only to print cwd and list Markdown/AGENTS files, explicitly excluding the snapshot and generated directories. It received the expected project cwd and no matching files (`rg` exit 1). It stopped and reported that approval prevented open and that maintenance/review/commit/reopen had not been performed. No snapshot contents were read or edited directly by the model.

| Observation | Result |
| --- | --- |
| Host exposes tool / model independently selects it | Observed: `nyrwvy_open` call; no raw `tools/list` frame retained |
| Nyrwvy tools used | One attempted `nyrwvy_open`; no successfully executed call |
| Project-relative open | Submitted; blocked by host before a server result |
| Declarations / graph inspection | Not reached |
| Simulate / returned review / explicit commit | Not reached; no proposal or commit result |
| Close / reopen / committed inspection | Not reached |
| Preserved ExecutionState TypeId | Unverified; no inspection result |
| Preserved next_action FunctionId | Unverified; no inspection result |
| Generated TimedOut VariantId / system ownership | No variant generated |
| TimedOut exists / fieldless / Failed behavior | Unverified; maintenance not executed |
| Exhaustive match repair / expected count 1 | No review; observed count unavailable, not zero |
| Final readiness | Unverified; no readiness result |
| Semantic errors | 0 observed; semantic operations not reached |
| MCP transport errors | 0 observed; no successful semantic exchange to assess |
| Host approval errors | 1, exact message above |
| Mutation retry / tool retry / trial restart | 0 / 0 / 0 |
| Semantic user intervention | None |
| Host permission / trust UI intervention | None; noninteractive policy prevented approval |

Host stderr also contained one local state-db warning: `state db discrepancy during find_thread_path_by_id_str_in_subdir: falling_back`. It was not reported as an MCP transport error and did not prevent model execution or tool selection.

SHA-256 before and after the invocation was identical for both files:

- Disposable fixture: `a640270f38afedafcec6194b4df527a3491084dde2041ac7987a8f04f1cfcf98`.
- Original current Mini Work Planner snapshot: `0867ba57020d0fb7a79a412556b9e7b8754a0ef2c71c09023615f1939b299e63`.

## Bounded follow-up

First resolve the observed host configuration boundary: use a Codex session in which normal MCP approval can actually be granted, recording that as host permission handling. Do not infer an adapter bug, change semantic operations, or add a second host from this result. The precise reason this tool requires approval in this CLI configuration was not independently diagnosed beyond the explicit host error. A subsequent live execution would need to be identified as a configuration-corrected follow-up, not silently counted as this run succeeding.

No new deterministic tests or fast-suite expansion are warranted by a host approval rejection with no code change. The compact evidence above is retained; temporary event output and the final response are removed rather than committing a full transcript or model reasoning.
