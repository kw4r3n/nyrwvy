import { McpServer } from "@modelcontextprotocol/server"
import { serveStdio } from "@modelcontextprotocol/server/stdio"
import * as z from "zod/v4"
import { randomUUID } from "node:crypto"
import { resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { parseArgs } from "node:util"
import { createGraphTools } from "../nyrwvy-agent/graph-tools.mjs"
import { closeAuthoring } from "../nyrwvy-agent/authoring.ts"

const { values } = parseArgs({ options: { workspace: { type: "string" }, help: { type: "boolean" } } })

if (values.help) {
  console.error("Usage: node --experimental-strip-types tools/nyrwvy-mcp/server.mjs [--workspace /path/to/project]\nRelative snapshots default to the MCP process current directory, which ordinary CLI clients normally launch in the active project. --workspace is an optional server-level override. nyrwvy_open also accepts a per-open workspace override for hosts that do not expose the project as cwd. The toolchain is located relative to this server.")
} else {
  const context = { sessionID: randomUUID(), worktree: resolve(values.workspace ?? process.cwd()),
    repositoryRoot: fileURLToPath(new URL("../../", import.meta.url)) }
  const lifetime = new AbortController()
  let handle
  let tail = Promise.resolve()
  let shutdownPromise

  function createServer() {
    const server = new McpServer({ name: "nyrwvy", version: "0.1.0" }, {
      instructions: "Use nyrwvy_open to create or open a persistent semantic graph. Relative snapshot paths normally belong to the active project cwd; pass nyrwvy_open.workspace only when this host starts MCP elsewhere. Inspect structure and proposal reviews before explicitly committing. Never edit snapshot JSON or generated source. Use nyrwvy_request for exact-ID operations. A lost/cancelled response requires reopening and inspecting committed state, not replaying a mutation.",
    })
    for (const [name, definition] of Object.entries(createGraphTools(z))) {
      server.registerTool(`nyrwvy_${name}`, {
        description: definition.description,
        inputSchema: z.object(definition.args).strict(),
        ...(name === "inspect" ? { annotations: { readOnlyHint: true } } : {}),
      }, (args, requestContext) => {
        // Serialize lifecycle calls too: MCP clients may issue tools concurrently.
        const operation = tail.then(async () => {
          const signal = AbortSignal.any([lifetime.signal, requestContext.mcpReq.signal])
          signal.throwIfAborted()
          const text = await definition.execute(args, { ...context, abort: signal })
          let value
          try { value = JSON.parse(text) } catch { value = { message: text } }
          return { content: [{ type: "text", text }], structuredContent: value,
            ...(value.ok === false ? { isError: true } : {}) }
        })
        tail = operation.then(() => undefined, () => undefined)
        return operation.catch(error => ({ isError: true, content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }] }))
      })
    }
    return server
  }

  function shutdown() {
    if (!shutdownPromise) {
      lifetime.abort()
      shutdownPromise = tail.then(async () => {
        try { await closeAuthoring(context) }
        finally { await handle?.close() }
      })
    }
    return shutdownPromise
  }
  const finish = () => { void shutdown().catch(error => { console.error(error); process.exitCode = 1 }) }
  process.stdin.once("end", finish)
  process.once("SIGINT", finish)
  process.once("SIGTERM", finish)
  // The SDK owns legacy/modern negotiation. Probing must not open a graph.
  handle = await serveStdio(createServer, { onerror: error => console.error(error) })
  if (lifetime.signal.aborted) await handle.close()
}
