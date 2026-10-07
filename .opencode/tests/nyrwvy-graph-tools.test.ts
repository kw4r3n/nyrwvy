import { expect, test } from "bun:test"
import { tool } from "@opencode-ai/plugin"
import { existsSync } from "node:fs"
import * as tools from "../tools/nyrwvy.ts"

const context = {
  sessionID: "bun-graph-tools",
  messageID: "schema-contract",
  agent: "build",
  directory: process.cwd(),
  worktree: process.cwd(),
  abort: new AbortController().signal,
  metadata() {},
  async ask() {},
}

test("ordinary OpenCode graph tools load under Bun with native plugin schemas", () => {
  const localPlugin = new URL("../node_modules/@opencode-ai/plugin/dist/index.js", import.meta.url)
  const rootPlugin = new URL("../../node_modules/@opencode-ai/plugin/dist/index.js", import.meta.url)
  expect(import.meta.resolve("@opencode-ai/plugin")).toBe((existsSync(localPlugin) ? localPlugin : rootPlugin).href)
  expect(Object.keys(tools).sort()).toEqual([
    "close", "commit", "create_function", "create_type", "inspect", "open", "recipe_reference", "reject", "replace", "request", "upgrade_dependencies", "view",
  ])
  const input = tool.schema.strictObject(tools.open.args)
  expect(input.parse({ mode: "create", snapshot: "workspace.json" })).toEqual({ mode: "create", snapshot: "workspace.json" })
  expect(input.safeParse({ mode: "invalid", snapshot: "workspace.json" }).success).toBe(false)
  expect(input.safeParse({ mode: "create", snapshot: [] }).success).toBe(false)
})

test("OpenCode recipe discovery returns a Promise without opening an authoring process", async () => {
  const result = tools.recipe_reference.execute({ kind: "conditional" }, context)
  expect(result).toBeInstanceOf(Promise)
  const text = await result
  if (typeof text !== "string") throw new Error("Recipe discovery must return its JSON text directly.")
  expect(JSON.parse(text)).toMatchObject({
    kind: "conditional",
    shape: { kind: "conditional", condition: "<recipe>", whenTrue: "<recipe>", whenFalse: "<recipe>" },
  })
})

test("OpenCode handlers preserve selector and tool-owned request guards", async () => {
  expect(() => tools.inspect.execute({ view: "status", function: "Identity" }, context)).toThrow("view=function")
  expect(() => tools.request.execute({ op: "status", arguments: { id: "caller-owned" } }, context)).toThrow("tool-owned")
  expect(await tools.close.execute({}, context)).toBe("No active Nyrwvy authoring session.")
})
