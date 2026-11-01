# nyrwvy-mcp

The MCP stdio server that exposes the shared [graph-editing tools](../nyrwvy-agent/graph-tools.mjs) to Codex CLI, Claude Code, Claude Desktop, Zed and other `mcpServers` clients. The native Zig host is the default authoring host (Step 8a cutover); `NYRWVY_HOST=fsharp` selects the deprecated F# host, which is kept as reference code and for legacy-schema migration. The typed-read architectural gate is not yet cleared: fresh opens still retain and serve the JSON model tree ([evidence](../zig/host/main.zig#L17)). Build, adapter, lifecycle, copied Rhifill C# smoke, and suite validation passed. `NYRWVY_HOST_EXECUTABLE` keeps an explicit `.dll` legacy escape hatch. See the [verification record](../../docs/decisions/zig-session-host.md#step-8-candidate-verification-2026-10-31-evidence-not-cutover-approval).

Setup, client configuration, path rules and the lifetime contract are in the guide: [MCP graph-editing tools](../../docs/specs/mcp-graph-tools.md).

```sh
npm --prefix tools/nyrwvy-mcp run setup   # npm ci + native host + standalone C# compiler helper
npm run build:host                        # native host only: Node + Zig 0.17 on PATH or NYRWVY_ZIG
npm --prefix tools/nyrwvy-mcp test        # the MCP wire and shutdown contracts
node --experimental-strip-types tools/nyrwvy-mcp/server.mjs --help
```

See the [MCP graph-tools guide](../../docs/specs/mcp-graph-tools.md) for prerequisites and override path rules.
