# nyrwvy-mcp

The MCP stdio server that exposes the shared [graph-editing tools](../nyrwvy-agent/graph-tools.mjs) to Codex CLI, Claude Code, Claude Desktop, Zed and other `mcpServers` clients. It launches the Release build of [`Nyrwvy.AuthoringSession`](../Nyrwvy.AuthoringSession/) with `dotnet`.

Setup, client configuration, path rules and the lifetime contract are in the guide: [MCP graph-editing tools](../../docs/specs/mcp-graph-tools.md).

```sh
npm --prefix tools/nyrwvy-mcp run setup   # npm ci + dotnet build (Release) of AuthoringSession
npm --prefix tools/nyrwvy-mcp test        # the MCP wire and shutdown contracts
node --experimental-strip-types tools/nyrwvy-mcp/server.mjs --help
```
