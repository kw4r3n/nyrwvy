import { McpServer } from "@modelcontextprotocol/server"
import { serveStdio } from "@modelcontextprotocol/server/stdio"
import * as z from "zod/v4"
import { randomUUID } from "node:crypto"
import { readFileSync } from "node:fs"
import { resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { parseArgs } from "node:util"
import { createGraphTools } from "../nyrwvy-agent/graph-tools.mjs"
import { closeAuthoring } from "../nyrwvy-agent/authoring.ts"

const { values } = parseArgs({ options: { workspace: { type: "string" }, "allow-host-sources": { type: "boolean" }, help: { type: "boolean" } } })

if (values.help) {
  console.error("Usage: node --experimental-strip-types tools/nyrwvy-mcp/server.mjs [--workspace /path/to/project] [--allow-host-sources]\nRelative snapshots default to the MCP process current directory, which ordinary CLI clients normally launch in the active project. --workspace is an optional server-level override. nyrwvy_open also accepts a per-open workspace override for hosts that do not expose the project as cwd. The toolchain is located relative to this server.\n--allow-host-sources lets runPure hostSources compile and run arbitrary C# with your permissions through nyrwvy_request; it is off by default and NYRWVY_ALLOW_HOST_SOURCES is ignored here. Do not combine it with auto-approving nyrwvy_request.")
} else {
  const context = { sessionID: randomUUID(), worktree: resolve(values.workspace ?? process.cwd()),
    repositoryRoot: fileURLToPath(new URL("../../", import.meta.url)), allowHostSources: values["allow-host-sources"] === true }
  const lifetime = new AbortController()
  /** @type {ReturnType<typeof serveStdio> | undefined} */
  let handle
  /** @type {Promise<void> | undefined} */
  let shutdownPromise

  /** @type {{ version: string }} */
  const manifest = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf8"))

  function createServer() {
    const server = new McpServer({ name: "nyrwvy", version: manifest.version }, {
      instructions: "Use nyrwvy_open to create or open a persistent semantic graph. Set a client tool-call timeout of at least 900 seconds: a cold runPure can take that long, and a cancelled call ends the host session. Relative snapshot paths normally belong to the active project cwd; pass nyrwvy_open.workspace only when this host starts MCP elsewhere. Use nyrwvy_recipe_reference when an ExpressionRecipe shape is unfamiliar. Inspect structure and proposal reviews before explicitly committing. Never edit snapshot JSON or generated source. Use nyrwvy_request for exact-ID operations. A lost/cancelled response requires reopening and inspecting committed state, not replaying a mutation.",
    })
    /**
     * Explicit unknowns matter: omitted generic slots in JSDoc become any.
     * @type {typeof createGraphTools<z.core.$ZodType<unknown, unknown>>}
     */
    const graphTools = createGraphTools
    const definitions = graphTools(z)
    /**
     * Keep each native schema paired with its handler. Object.entries would
     * erase that relationship into a union of incompatible argument types.
     * @template {z.ZodRawShape} Args
     * @param {string} name
     * @param {import("../nyrwvy-agent/graph-tools.mjs").GraphTool<Args>} definition
     */
    function register(name, definition) {
      server.registerTool(`nyrwvy_${name}`, {
        description: definition.description,
        inputSchema: z.strictObject(definition.args),
        ...((name === "inspect" || name === "recipe_reference") ? { annotations: { readOnlyHint: true } } : {}),
      }, (args, requestContext) => {
        const signal = AbortSignal.any([lifetime.signal, requestContext.mcpReq.signal])
        return Promise.resolve().then(/** @returns {Promise<import("@modelcontextprotocol/server").CallToolResult>} */ async () => {
          signal.throwIfAborted()
          const text = await definition.execute(args, { ...context, abort: signal })
          let value
          try { value = JSON.parse(text) } catch { value = { message: text } }
          return { content: [{ type: "text", text }], structuredContent: value,
            ...(value.ok === false ? { isError: true } : {}) }
        }).catch(/** @returns {import("@modelcontextprotocol/server").CallToolResult} */ error => ({ isError: true, content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }] }))
      })
    }
    register("open", definitions.open)
    register("inspect", definitions.inspect)
    register("view", definitions.view)
    register("recipe_reference", definitions.recipe_reference)
    register("create_type", definitions.create_type)
    register("create_function", definitions.create_function)
    register("create_functions", definitions.create_functions)
    register("batch", definitions.batch)
    register("replace", definitions.replace)
    register("upgrade_dependencies", definitions.upgrade_dependencies)
    register("commit", definitions.commit)
    register("reject", definitions.reject)
    register("request", definitions.request)
    register("close", definitions.close)
    return server
  }

  function shutdown() {
    if (!shutdownPromise) {
      lifetime.abort()
      shutdownPromise = (async () => {
        try { await closeAuthoring(context) }
        finally { await handle?.close() }
      })()
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
