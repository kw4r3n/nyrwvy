# MCP graph-editing tools

**Status:** Maintained local stdio adapter; one qualified Codex CLI live probe completed.

The same ordinary [graph-editing tools](agent-graph-tools.md) can be used by MCP clients without OpenCode. The SDK handles MCP discovery, schemas and transport; the shared client calls the existing JSONL AuthoringSession. Semantic validation, graph identity, proposal review and publication remain in the existing implementation. No model, source language or planning service is added.

The official SDK handles both legacy MCP initialization and the 2026-07-28 protocol on the same stdio entry. Probing/tool discovery does not open a snapshot or start AuthoringSession.

## Setup

Requires Node 22.6+ (Node 22 recommended), npm and the .NET 10 SDK. From the repository root, run one setup command:

```sh
npm --prefix tools/nyrwvy-mcp run setup
```

This installs the pinned MCP SDK dependencies and builds the existing Release AuthoringSession. It does not install OpenCode, need model credentials, start a network listener, or modify client configuration. Rebuild AuthoringSession after changing its implementation. The MCP client will launch the server itself.

The ordinary server launch command is:

```sh
node --experimental-strip-types /absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs
```

Use an absolute server path. Relative snapshot paths default to the MCP server process's working directory. For local CLI clients that launch stdio servers in the active project, this makes `nyrwvy_open {snapshot:"planner.snapshot.json"}` naturally create or open `./planner.snapshot.json` in that project.

`--workspace /absolute/path/my-project` remains an optional server-level override for a deliberately fixed workspace. In addition, `nyrwvy_open` accepts an optional `workspace` argument so an agent can supply the host-known project directory when a GUI, plugin cache, desktop host, or other MCP launcher starts the server elsewhere. The user therefore does not need to bake a program directory into ordinary MCP registration.

Nyrwvy intentionally does not add a dependency on MCP Roots for this. Roots are deprecated in the 2026-07-28 MCP specification, which recommends passing directories/files through tool parameters, resource URIs, or server configuration for new implementations. The .NET assembly is always located relative to the server's repository, independently of the client project. Use an absolute `node` executable path when a graphical client does not inherit the expected PATH; `dotnet` must also be available to the launched server.

## Client configuration

Codex CLI has one bounded live result, linked below. The other client examples are configuration guidance, not live compatibility claims. Paths are placeholders to replace locally. All examples launch the same server.

### Codex CLI

Add to the applicable Codex `config.toml`:

```toml
[mcp_servers.nyrwvy]
command = "node"
args = ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
```

Launch Codex in the active project (or use `-C`) so project-relative snapshot paths resolve there. This configuration leaves `--workspace` and MCP `cwd` unset, as in the live probe.

Noninteractive `codex exec` may need a server-level approval setting: in Codex CLI 0.155.0, the first probe was blocked with `MCP tool call requires approval, but approval policy is never`. For a run in which Nyrwvy tools are authorized for automatic approval, add `default_tools_approval_mode = "approve"` under `[mcp_servers.nyrwvy]`, or pass:

```sh
-c 'mcp_servers.nyrwvy.default_tools_approval_mode="approve"'
```

That server-scoped correction allowed the follow-up to complete while retaining `workspace-write`; no global approval/sandbox bypass was used. This is an observed configuration requirement for that invocation, not a requirement to auto-approve every Codex session.

Reference: [Codex MCP configuration](https://developers.openai.com/codex/mcp/).

### Claude Code and other `mcpServers` clients

For Claude Code, add the entry to the project `.mcp.json`, following the client's normal trust/approval workflow:

```json
{
  "mcpServers": {
    "nyrwvy": {
      "command": "node",
      "args": ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
    }
  }
}
```

Clients with an `mcpServers` configuration use the same command/args; the settings file's location is client-specific. Reference: [Claude Code MCP](https://code.claude.com/docs/en/mcp).

### Zed

Add to Zed settings:

```json
{
  "context_servers": {
    "nyrwvy": {
      "command": "node",
      "args": ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
    }
  }
}
```

Reference: [Zed MCP](https://zed.dev/docs/ai/mcp). Tool availability and permission prompts depend on the selected agent/profile. An external agent's MCP forwarding is a client integration concern, not an alternative Nyrwvy semantic path.

## Operation and lifetime

`tools/list` advertises `nyrwvy_open`, `nyrwvy_inspect`, `nyrwvy_recipe_reference`, `nyrwvy_create_type`, `nyrwvy_create_function`, `nyrwvy_replace`, `nyrwvy_commit`, `nyrwvy_reject`, `nyrwvy_request`, and `nyrwvy_close`. Their arguments, descriptions and handlers are shared with the OpenCode adapter in `tools/nyrwvy-agent/graph-tools.mjs`.

`nyrwvy_recipe_reference` is read-only and sessionless, so it may be called before `nyrwvy_open` when a function-body or replacement recipe shape is unfamiliar. Start an authoring session with `nyrwvy_open` arguments such as `{"mode":"create","snapshot":"planner.snapshot.json"}`. This normally resolves against the active project cwd. If the host launched MCP elsewhere but the agent knows the project directory, use `{"mode":"create","snapshot":"planner.snapshot.json","workspace":"/absolute/path/to/project"}` for that open only. Then inspect, propose, read the exact returned review, and explicitly commit/reject. Proposing never auto-commits. Exact stable-ID operations remain accessible via `nyrwvy_request`.

One MCP server process owns at most one active authoring session. Concurrent tool calls, including open/close, are serialized. Different client processes do not share pending proposals. The existing snapshot lock still allows only one writer to a snapshot. Switching clients therefore means closing the previous authoring session and reopening the same committed snapshot, not simultaneous collaborative editing.

Structured JSONL results are returned as MCP `structuredContent` and text content; semantic `ok: false` becomes `isError: true` without dropping its diagnostics. Transport/lifecycle failures are also error tool results. stdout is reserved for MCP; diagnostics use stderr.

Client EOF or normal termination closes the underlying AuthoringSession and releases its lock. Cancellation of a sent request invalidates that session using the existing client behavior. Reopen and inspect committed state before retrying: a lost response does not prove a commit failed. Pending proposals are never restored or replayed automatically.

## Verification and scope

Run the focused deterministic MCP contract after setup:

```sh
npm --prefix tools/nyrwvy-mcp test
```

The contract uses the official MCP client over real subprocess stdio, then the real AuthoringSession. It covers tool discovery and schemas, sessionless recipe reference reads, automatic cwd-relative project opening, per-open workspace fallback from an unrelated cwd, explicit publication, error mapping, client isolation, and EOF/reopen. Existing ordinary-tool tests continue to exercise semantic workflows through the compatibility entry point.

On 2026-09-21, Codex CLI 0.155.0 with `gpt-6-astra` completed the known TimedOut maintenance task through this MCP surface on commit `ab74ecc4`: project-relative open, inspect, simulate with returned review, explicit commit, close/reopen, and committed-state inspection. Existing TypeId/FunctionId values were preserved, the new VariantId was system-owned, repaired match count was 1, and final execution readiness was `ready`. The follow-up had no semantic/transport errors, mutation retries, or semantic user intervention. See [Run 1: approval-blocked](../verification/codex-cli-mcp-maintenance-probe.md) and [Run 2: configuration-corrected success](../verification/codex-cli-mcp-maintenance-probe-run-2.md).

The model also consulted local repository documentation and `Protocol.fs` to discover the exact maintenance operation. The supported claim is **Codex CLI + local repository context + Nyrwvy MCP tools**; tool descriptions alone were not proven sufficient.

A later independent `nyrwvy-ai-task-router` consumer was authored without reading the Nyrwvy repository docs or implementation, but it first tried an unsupported `help` request and then incurred 10 function-recipe `ProtocolError` iterations while discovering names such as `conditional`, `whenTrue`/`whenFalse`, `fieldAccess.source`, and `matchUnion.input`. That second observed friction motivated `nyrwvy_recipe_reference`: an on-demand shared reference rather than a new semantic operation or an always-expanded recursive schema. Its deterministic MCP contract is repository-side; one bounded local consumer-authoring follow-up remains the environment-dependent check for whether the live trial-and-error is materially reduced.

Claude Code/Zed model use, interactive UI discovery, and external-agent forwarding remain unverified live boundaries. This adapter does not provide HTTP hosting or make a local stdio server directly available to browser-only/cloud-only clients. No additional live run or host matrix is scheduled without a relevant implementation change or a new decision-relevant observation.

## Maintainer rule

Keep tool definitions and authoring transport host-neutral. A host adapter must reuse them rather than copy semantic operations, invent an alternative snapshot authority, or silently replay a mutation. Add a focused transport contract for a new connection boundary; do not repeat the entire semantic or live-model suite for each host.
