# MCP graph-editing tools

**Status:** Local stdio adapter over the maintained authoring tools.

The same nine [graph-editing tools](agent-graph-tools.md) can be used by MCP clients without OpenCode. The SDK handles MCP discovery, schemas and transport; the shared client calls the existing JSONL AuthoringSession. Semantic validation, graph identity, proposal review and publication remain in the existing implementation. No model, source language or planning service is added.

The official SDK handles both legacy MCP initialization and the 2026-07-28 protocol on the same stdio entry. Probing/tool discovery does not open a snapshot or start AuthoringSession.

## Setup

Requires Node 22.6+ (Node 22 recommended), npm and the .NET 10 SDK. From the repository root, run one setup command:

```sh
npm --prefix tools/nyrwvy-mcp run setup
```

This installs the pinned MCP SDK dependencies and builds the existing Release AuthoringSession. It does not install OpenCode, need model credentials, start a network listener, or modify client configuration. Rebuild AuthoringSession after changing its implementation. The MCP client will launch the server itself.

The server launch command is:

```sh
node --experimental-strip-types /absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs --workspace /absolute/path/my-project
```

Use an absolute server path. `--workspace` is the base directory for relative snapshot paths; when omitted it is the MCP server process's working directory. The .NET assembly is always located relative to the server's repository, independently of the client directory. The workspace directory must already exist. Use an absolute `node` executable path when a graphical client does not inherit the expected PATH; `dotnet` must also be available to the launched server.

## Client configuration

These are documented configuration examples, not claims that each client's UI or model has been live-tested. Paths are placeholders to replace locally. All examples launch the same server.

### Codex CLI

Add to the applicable Codex `config.toml`:

```toml
[mcp_servers.nyrwvy]
command = "node"
args = ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs", "--workspace", "/absolute/path/my-project"]
```

Reference: [Codex MCP configuration](https://developers.openai.com/codex/mcp/).

### Claude Code and other `mcpServers` clients

For Claude Code, add the entry to the project `.mcp.json`, following the client's normal trust/approval workflow:

```json
{
  "mcpServers": {
    "nyrwvy": {
      "command": "node",
      "args": ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs", "--workspace", "/absolute/path/my-project"]
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
      "args": ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs", "--workspace", "/absolute/path/my-project"]
    }
  }
}
```

Reference: [Zed MCP](https://zed.dev/docs/ai/mcp). Tool availability and permission prompts depend on the selected agent/profile. An external agent's MCP forwarding is a client integration concern, not an alternative Nyrwvy semantic path.

## Operation and lifetime

`tools/list` advertises `nyrwvy_open`, `nyrwvy_inspect`, `nyrwvy_create_type`, `nyrwvy_create_function`, `nyrwvy_replace`, `nyrwvy_commit`, `nyrwvy_reject`, `nyrwvy_request`, and `nyrwvy_close`. Their arguments, descriptions and handlers are shared with the OpenCode adapter in `tools/nyrwvy-agent/graph-tools.mjs`.

Start with `nyrwvy_open` arguments such as `{"mode":"create","snapshot":"planner.snapshot.json"}`. Then inspect, propose, read the exact returned review, and explicitly commit/reject. Proposing never auto-commits. Exact stable-ID operations remain accessible via `nyrwvy_request`.

One MCP server process owns at most one active authoring session. Concurrent tool calls, including open/close, are serialized. Different client processes do not share pending proposals. The existing snapshot lock still allows only one writer to a snapshot. Switching clients therefore means closing the previous authoring session and reopening the same committed snapshot, not simultaneous collaborative editing.

Structured JSONL results are returned as MCP `structuredContent` and text content; semantic `ok: false` becomes `isError: true` without dropping its diagnostics. Transport/lifecycle failures are also error tool results. stdout is reserved for MCP; diagnostics use stderr.

Client EOF or normal termination closes the underlying AuthoringSession and releases its lock. Cancellation of a sent request invalidates that session using the existing client behavior. Reopen and inspect committed state before retrying: a lost response does not prove a commit failed. Pending proposals are never restored or replayed automatically.

## Verification and scope

Run the focused deterministic MCP contract after setup:

```sh
npm --prefix tools/nyrwvy-mcp test
```

The contract uses the official MCP client over real subprocess stdio, then the real AuthoringSession. It covers tool discovery and schemas, working-directory independence, explicit publication, error mapping, client isolation, and EOF/reopen. Existing ordinary-tool tests continue to exercise semantic workflows through the compatibility entry point.

Actual Codex/Claude Code/Zed UI discovery, external-agent forwarding, and model choice of tools are separate live boundaries. This adapter does not provide HTTP hosting or make a local stdio server directly available to browser-only/cloud-only clients.

## Maintainer rule

Keep tool definitions and authoring transport host-neutral. A host adapter must reuse them rather than copy semantic operations, invent an alternative snapshot authority, or silently replay a mutation. Add a focused transport contract for a new connection boundary; do not repeat the entire semantic or live-model suite for each host.
