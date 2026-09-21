# Codex CLI MCP maintenance probe — Run 2

**Status:** Observed result (environment-dependent validation).

Run 1 remains a valid blocked result; this follow-up establishes the qualified success below. See [the other run](codex-cli-mcp-maintenance-probe.md).

## Result

**Primary success on 2026-09-21: configuration-corrected follow-up.** Codex CLI's model opened the project-relative fixture, inspected declarations and graphs, simulated the existing maintenance operation, received its review, explicitly committed that proposal, closed/reopened the snapshot, and inspected the committed state. All expected committed-state checks passed. No authoring helper or direct snapshot/source edit was used.

Discovery qualification: the model independently selected MCP tools, but also searched existing repository documentation and read the protocol implementation to identify the exact maintenance operation. This demonstrates autonomous task completion with local reference material available; it does **not** establish that MCP tool descriptions alone were sufficient.

Run 1 remains a blocked result, not a success retroactively: on 2026-09-21 at 20:44:40–20:45:15 JST, Codex CLI 0.155.0 with the same model, commit and fixture attempted `nyrwvy_open` with `{"mode":"open","snapshot":"planner.snapshot.json"}`. The host returned `MCP tool call requires approval, but approval policy is never`; proposal/commit/reopen were not reached. There was one host approval error, no observed semantic/transport error, no mutation retry or semantic intervention, and both snapshots remained unchanged. That earlier evidence was preserved in the conversation. Its local report file was already empty at the start of this follow-up and was left untouched.

## Conditions and configuration delta

- Host: `codex-cli 0.155.0`, noninteractive `codex exec`, local stdio MCP.
- Requested model: `gpt-6-astra`; JSON events do not independently identify a backend model revision.
- Nyrwvy commit: `ab74ecc435e919da76536ecff98402f2c75ba551`, unchanged from Run 1. The checkout was not advanced to the newer commit mentioned in the planning conversation.
- Start: `2026-09-21T20:58:27+09:00`; end: `2026-09-21T21:00:07+09:00` (Asia/Tokyo).
- Fixture: bytes copied from `9f03d5e7:mini-work-planner/planner.snapshot.json` for Run 1, reused after verifying that the SHA-256 was still the original fixture hash.
- Active project cwd: `/home/kw4/Work/Tools/nyrwvy/artifacts/codex-mcp-probe/workspace`.
- User prompt, fixture, model, CLI cwd, `workspace-write` sandbox, and MCP server command were unchanged. The only deliberate run configuration change was `mcp_servers.nyrwvy.default_tools_approval_mode="approve"`.
- No `--workspace` argument, MCP `cwd` override, global configuration edit, or sandbox bypass.
- Existing Release server assembly from Run 1 was reused. No implementation or test changes.

```toml
[mcp_servers.nyrwvy]
command = "/home/kw4/.local/share/mise/installs/node/26.8.2/bin/node"
args = ["--experimental-strip-types", "/home/kw4/Work/Tools/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
default_tools_approval_mode = "approve"
```

The server-level setting is documented in the [official MCP configuration reference](https://learn.chatgpt.com/docs/extend/mcp?surface=cli). The local `codex mcp get` command accepted the overrides, although its JSON display omitted approval-mode fields. The successful live call, rather than that display alone, establishes that this run passed the approval gate. A preflight attempt to use `--strict-config` with `codex mcp` was rejected as unsupported; it did not invoke a model or MCP operation.

Exact model invocation (paths for ephemeral output differ from Run 1):

```sh
codex exec --ignore-user-config --ephemeral --skip-git-repo-check \
  -C /home/kw4/Work/Tools/nyrwvy/artifacts/codex-mcp-probe/workspace \
  -m gpt-6-astra -s workspace-write \
  -c 'mcp_servers.nyrwvy.command="/home/kw4/.local/share/mise/installs/node/26.8.2/bin/node"' \
  -c 'mcp_servers.nyrwvy.args=["--experimental-strip-types","/home/kw4/Work/Tools/nyrwvy/tools/nyrwvy-mcp/server.mjs"]' \
  -c 'mcp_servers.nyrwvy.default_tools_approval_mode="approve"' \
  --json \
  -o /home/kw4/Work/Tools/nyrwvy/artifacts/codex-mcp-probe/run-2/final.txt \
  - < artifacts/codex-mcp-probe/prompt.txt \
  > artifacts/codex-mcp-probe/run-2/events.jsonl \
  2> artifacts/codex-mcp-probe/run-2/stderr.txt
```

This command was launched from the Nyrwvy repository root. Normal authentication was retained; user config was ignored. The CLI process exited 0.

## First user prompt (verbatim, identical to Run 1)

```text
この project の planner.snapshot.json は Mini Work Planner の既知の pre-maintenance state です。
Mini Work Planner の ExecutionState に fieldless variant TimedOut を追加し、既存の Failed と同じ behavior を持たせる。既存の semantic identities は維持すること。Nyrwvy MCP tools を使って変更を inspect、review、commit し、close/reopen 後に committed state を確認すること。snapshot JSON は直接読んだり編集したりせず、generated source も直接編集しないこと。snapshot は project-relative path で開くこと。
```

No operation name, stable ID, or tool-call sequence was supplied. No follow-up prompt or semantic hint was sent during the run.

## Observed tool use

Fifteen Nyrwvy calls completed successfully: `nyrwvy_open` ×2, `nyrwvy_inspect` ×9, `nyrwvy_request` ×2, `nyrwvy_commit` ×1, `nyrwvy_close` ×1. Discovery is evidenced by the model's calls, not a retained raw `tools/list` transport trace.

| Order | Model-selected action | Result |
| --- | --- | --- |
| 1 | Open `planner.snapshot.json` in `open` mode | Returned absolute path inside the active disposable project |
| 2 | Inspect declarations and `next_action`; later repeat declarations and inspect `can_retry`, `action_label` | Found ExecutionState, existing Failed variant, stable declaration IDs and behavior |
| 3 | Request `simulateAddFieldlessUnionVariantLike` | Returned proposal and structured review; repair count 1 |
| 4 | Commit the exact returned proposal ID | Published reviewed snapshot, `semanticallyReady` |
| 5 | Close | Authoring session closed |
| 6 | Reopen the same project-relative path | Published snapshot ID and reviewed fingerprints matched |
| 7 | Inspect all three functions and declarations | TimedOut and preserved identities confirmed |
| 8 | Request `executionReadiness` | `ready`, 3 functions, source snapshot matched commit |

The simulate request was:

```json
{
  "op": "simulateAddFieldlessUnionVariantLike",
  "arguments": {
    "typeId": "01a0b47d-8ca6-7384-a7f5-58eb9d8fef8c",
    "sourceVariantId": "01a0b47d-8ca6-7def-a939-26cd254d9fe1",
    "displayName": "TimedOut",
    "structuredReview": true
  }
}
```

The tool returned the review before the separate commit call. No extra review RPC was used. The review kind was `fieldlessUnionVariantEvolution`, with `repairedFunctionIds` containing only `next_action` and `repairedMatchCount: 1`, reporting the exhaustive-match repair of the existing maintenance contract. The model committed that returned proposal ID, and its final response stated that the change had been reviewed. No private reasoning is retained or inferred.

## Committed-state evidence

| Check | Observed value |
| --- | --- |
| ExecutionState TypeId before and after reopen | `01a0b47d-8ca6-7384-a7f5-58eb9d8fef8c` |
| next_action FunctionId before and after reopen | `01a0b47e-adcf-7f06-803c-4161bf607760` |
| Existing Failed VariantId | `01a0b47d-8ca6-7def-a939-26cd254d9fe1` |
| Generated TimedOut VariantId | `01a0c3d5-bf3e-7c57-a57f-0c4ed40234f8` |
| TimedOut payload | `fields: []` in review and reopened declarations |
| System ownership | Model supplied no new VariantId; server returned `addedVariantId`, subsequently present after reopen |
| Proposal / committed transaction | `01a0c3d5-bf45-7523-9202-576f00495f10` |
| Published / reopened snapshot | `01a0c3d5-bf45-79de-b73b-4fe2d62c697e` |
| Review repair count | `1` |
| Review and commit readiness | `semanticallyReady` |
| Reopened execution readiness | `ready`, `functionCount: 3` |
| Reopened function obligations | Empty for all three inspected functions |

Review and reopen both reported:

```json
{
  "snapshotFingerprint": "sha256:bf5ff58419d4d17bf9caaf1bd346e3f3958582b40da600704416ae979ace44de",
  "semanticFingerprint": "sha256:847188629b9834ab9c57db1c172d157788f08c119cf6da94f661ff7c505e9658"
}
```

One-off checks of the returned MCP data, performed after the model completed, established:

- Removing only the added TimedOut variant from the reopened declarations yields the exact original declarations.
- Removing only the added TimedOut arm from reopened `next_action` yields the exact original function graph, including existing node IDs.
- `can_retry` and `action_label` function declarations and bodies are unchanged.
- Expanding Failed and TimedOut arm graphs from their result nodes, resolving graph-local node references, yields equal operation trees. The new nodes have system-generated identities.

Thus TimedOut copies Failed behavior: when prerequisites are Ready, `can_retry(attempts, maxAttempts)` selects Retry or NeedsHuman; existing outer prerequisite behavior remains unchanged. This is graph-level confirmation using the maintained contract, not a new runtime behavior test suite. No snapshot JSON was parsed for these checks and no helper participated in authoring.

File SHA-256 checks (distinct from semantic snapshot fingerprints):

- Fixture before: `a640270f38afedafcec6194b4df527a3491084dde2041ac7987a8f04f1cfcf98`.
- Fixture after MCP commit: `69583c9d970ecc2796ab8410516de0511007ae8396767ff5612e8605b059cc3d`.
- Original Mini Work Planner snapshot before and after: `0867ba57020d0fb7a79a412556b9e7b8754a0ef2c71c09023615f1939b299e63`.

## Errors, retries, and intervention

- Semantic errors: **0**; all returned semantic envelopes were successful.
- MCP transport errors: **0**; all 15 calls completed with no host error.
- Host approval rejections: **0** in Run 2; **1** in Run 1.
- Mutation retries: **0**; one simulate and one explicit commit. No failed MCP call was retried.
- Semantic user intervention: **none**.
- Host permission handling: one server-scoped approval configuration correction before Run 2; no interactive trust/permission UI operation and no global sandbox change.
- Trial count: one blocked initial invocation plus one explicitly identified configuration-corrected follow-up; no repeated success-rate trials.
- Host warning: one `state db discrepancy during find_thread_path_by_id_str_in_subdir: falling_back` message on stderr, also seen in Run 1. It did not prevent task completion.

The model made six read-only shell calls to locate/read existing references. It read `docs/specs/agent-authoring-session.md`, `docs/specs/type-declaration-authoring.md`, a protocol dispatch excerpt in `tools/Nyrwvy.AuthoringSession/Protocol.fs`, and the maintenance contract in `docs/reference/language-subset.md`, with broader Markdown/F# searches. It did not directly read snapshot JSON or edit generated source.

Two shell calls had nonzero exit codes, separate from semantic/transport errors: an initial project-local file search had no matches (`rg` exit 1), and a later search guessed the nonexistent `src/Nyrwvy.AuthoringSession` directory (`rg` exit 2, `No such file or directory`). The model widened its read-only search and found the actual protocol file under `tools`. This is observed discovery friction, not a mutation retry. No new authoring helper was written or run.

No code repair, new deterministic test, fast-suite expansion, other-host matrix, or further live run was performed. The existing unrelated `verification-planner/transcript.jsonl` edit and empty Run 1 report were left untouched. Only this compact report is retained as durable evidence; temporary full event output and final response are removed after extraction.
