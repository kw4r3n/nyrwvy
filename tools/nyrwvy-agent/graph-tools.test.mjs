import test from "node:test"
import assert from "node:assert/strict"
import { tool } from "@opencode-ai/plugin"
import * as z from "zod/v4"
import { createGraphTools } from "./graph-tools.mjs"
import { recipeCategories, recipeKinds } from "./recipe-reference.mjs"
import * as openCodeTools from "../../.opencode/tools/nyrwvy.ts"

const context = { sessionID: "schema-contract", worktree: process.cwd() }
const samples = {
  open: { valid: { mode: "create", snapshot: "workspace.json", dependency: "policy.json" }, invalid: { mode: "create", snapshot: "workspace.json", dependency: 42 } },
  inspect: { valid: { view: "function", function: "Identity" }, invalid: { view: "unknown" } },
  recipe_reference: { valid: { kinds: ["conditional", "matchUnion"] }, invalid: { kinds: [] } },
  create_type: { valid: { displayName: "Item", typeKind: {} }, invalid: { displayName: "Item", typeKind: [] } },
  create_function: {
    valid: { displayName: "Identity", parameters: [{ displayName: "value", type: {} }], resultType: {}, requiredEffects: [], body: {} },
    invalid: { displayName: "Identity", parameters: [{ displayName: 42, type: {} }], resultType: {}, requiredEffects: [], body: {} },
  },
  replace: { valid: { function: "Identity", replacement: {} }, invalid: { function: "Identity", replacement: [] } },
  commit: { valid: { proposalId: "proposal" }, invalid: { proposalId: 42 } },
  reject: { valid: { proposalId: "proposal" }, invalid: {} },
  request: { valid: { op: "status", arguments: {} }, invalid: { op: "status", arguments: [] } },
  close: { valid: {}, invalid: { extra: true } },
}

for (const [host, schema, minor] of [["OpenCode", tool.schema, 1], ["MCP", z, 3]]) {
  test(`${host} keeps native Zod schemas and validates every shared tool shape`, () => {
    assert.equal(schema.core.version.minor, minor)
    const definitions = createGraphTools(schema)
    assert.deepEqual(Object.keys(definitions), Object.keys(samples))
    for (const [name, definition] of Object.entries(definitions)) {
      for (const argument of Object.values(definition.args)) {
        assert.ok(argument instanceof schema.ZodType, `${name} must retain the host's schema instance`)
        assert.equal(argument._zod.version.minor, minor)
      }
      const parser = schema.strictObject(definition.args)
      assert.deepEqual(parser.parse(samples[name].valid), samples[name].valid, name)
      assert.equal(parser.safeParse(samples[name].invalid).success, false, name)
    }
  })

  test(`${host} recipe discovery is asynchronous, complete, and sessionless`, async () => {
    const definitions = createGraphTools(schema)
    const result = definitions.recipe_reference.execute({}, context)
    assert.ok(result instanceof Promise)
    const index = JSON.parse(await result)
    assert.deepEqual(Object.values(index.categories).flat(), recipeKinds)
    assert.deepEqual(Object.keys(index.categories), recipeCategories)
    for (const kind of recipeKinds) {
      const entry = JSON.parse(await definitions.recipe_reference.execute({ kind }, context))
      assert.equal(entry.kind, kind)
      assert.ok(entry.shape || entry.shapes)
    }
    // Every kind has a one-line signature in its category, and several shapes come back in one call.
    const signed = []
    for (const category of recipeCategories) {
      const page = JSON.parse(await definitions.recipe_reference.execute({ category }, context))
      assert.equal(page.category, category)
      assert.deepEqual(Object.keys(page.signatures), index.categories[category])
      for (const signature of Object.values(page.signatures)) assert.match(signature, /^\(.*\) -> \S/)
      signed.push(...Object.keys(page.signatures))
    }
    assert.deepEqual(signed, recipeKinds)
    const several = JSON.parse(await definitions.recipe_reference.execute({ kinds: ["listReverse", "conditional", "listReverse"] }, context))
    assert.deepEqual(several.entries.map(item => item.kind), ["listReverse", "conditional"])
    assert.deepEqual(several.entries[1].shape, { kind: "conditional", condition: "<recipe>", whenTrue: "<recipe>", whenFalse: "<recipe>" })
    await assert.rejects(definitions.recipe_reference.execute({ kind: "conditional", category: "control" }, context), /at most one/)
    await assert.rejects(definitions.recipe_reference.execute({ kinds: ["conditional", "noSuchKind"] }, context), /Unknown expression recipe kind 'noSuchKind'/)
    assert.throws(() => definitions.inspect.execute({ view: "status", function: "Identity" }, context), /view=function/)
    assert.throws(() => definitions.request.execute({ op: "status", arguments: { id: "caller-owned" } }, context), /tool-owned/)
    assert.throws(() => definitions.request.execute({ op: "status", arguments: { op: "commit" } }, context), /Pass op separately/)
    await assert.rejects(definitions.open.execute({ mode: "open", snapshot: "workspace.json", dependency: "policy.json" }, context), /requires mode=create/)
    await assert.rejects(definitions.open.execute({ mode: "create", snapshot: "workspace.json", dependency: " " }, context), /non-blank/)
    assert.equal(await definitions.close.execute({}, context), "No active Nyrwvy authoring session.")
  })
}

test("both native Zod versions publish the same strict input schemas", () => {
  const oldDefinitions = createGraphTools(tool.schema)
  const newDefinitions = createGraphTools(z)
  for (const name of Object.keys(samples)) {
    assert.deepEqual(
      tool.schema.toJSONSchema(tool.schema.strictObject(oldDefinitions[name].args)),
      z.toJSONSchema(z.strictObject(newDefinitions[name].args)),
      name,
    )
  }
})

test("the OpenCode entry point exports all tools and awaits recipe discovery", async () => {
  assert.deepEqual(Object.keys(openCodeTools).sort(), Object.keys(samples).sort())
  const result = openCodeTools.recipe_reference.execute({ kind: "conditional" }, context)
  assert.ok(result instanceof Promise)
  assert.deepEqual(JSON.parse(await result).shape, {
    kind: "conditional", condition: "<recipe>", whenTrue: "<recipe>", whenFalse: "<recipe>",
  })
})
