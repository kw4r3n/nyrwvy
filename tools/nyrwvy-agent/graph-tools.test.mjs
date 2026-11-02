import test from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, writeFile, chmod, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { tool } from "@opencode-ai/plugin"
import * as z from "zod/v4"
import { createGraphTools } from "./graph-tools.mjs"
import { recipeCategories, recipeKinds } from "./recipe-reference.mjs"
import * as openCodeTools from "../../.opencode/tools/nyrwvy.ts"

const context = { sessionID: "schema-contract", worktree: process.cwd() }
const samples = {
  open: { valid: { mode: "create", snapshot: "workspace.json", dependency: "policy.json" }, invalid: { mode: "create", snapshot: "workspace.json", dependency: 42 } },
  inspect: { valid: { view: "function", function: "Identity" }, invalid: { view: "unknown" } },
  view: { valid: { proposalId: "proposal", output: "review.html" }, invalid: { proposalId: 42 } },
  recipe_reference: { valid: { kinds: ["conditional", "matchUnion"] }, invalid: { kinds: [] } },
  create_type: { valid: { displayName: "Item", typeKind: {} }, invalid: { displayName: "Item", typeKind: [] } },
  create_function: {
    valid: { displayName: "Identity", parameters: [{ displayName: "value", type: {} }], resultType: {}, requiredEffects: [], body: {} },
    invalid: { displayName: "Identity", parameters: [{ displayName: 42, type: {} }], resultType: {}, requiredEffects: [], body: {} },
  },
  create_functions: { valid: { functions: [{ displayName: "Identity", body: {} }] }, invalid: { functions: [42] } },
  batch: { valid: { steps: [{ kind: "createFunction", displayName: "Identity", body: {} }] }, invalid: { steps: [42] } },
  replace: { valid: { function: "Identity", replacement: {} }, invalid: { function: "Identity", replacement: [] } },
  upgrade_dependencies: { valid: { selection: { sources: ["new.json"], replaces: ["old"] }, repairs: {} }, invalid: { selection: {}, repairs: [] } },
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
    assert.throws(() => definitions.inspect.execute({ view: "proposals", target: { kind: "type", type: "Shape" } }, context), /view=references/)
    assert.throws(() => definitions.inspect.execute({ view: "references" }, context), /requires target/)
    assert.throws(() => definitions.inspect.execute({ view: "search" }, context), /requires text/)
    assert.throws(() => definitions.inspect.execute({ view: "scopes", function: "f" }, context), /requires nodeId/)
    assert.throws(() => definitions.inspect.execute({ view: "status", nodeId: "n" }, context), /nodeId requires view=scopes/)
    assert.throws(() => definitions.inspect.execute({ view: "status", detail: "full" }, context), /detail requires view=function/)
    assert.throws(() => definitions.inspect.execute({ view: "references", target: { kind: "type", id: "caller-owned" } }, context), /tool-owned/)
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

test("open warns only when the session build names another commit than the loaded tool code", async () => {
  const { staleToolWarning } = await import("./authoring.ts")
  const a = "a".repeat(40), b = "b".repeat(40)
  assert.equal(staleToolWarning(a, `1.0.0+${a}`), undefined)
  assert.match(staleToolWarning(a, `1.0.0+${b}`), /runs build bbbbbbbbbbbb, but this server loaded .* from aaaaaaaaaaaa\. Reconnect the MCP server/)
  assert.equal(staleToolWarning(undefined, `1.0.0+${b}`), undefined)
  assert.equal(staleToolWarning(a, "1.0.0"), undefined)
  assert.equal(staleToolWarning(a, undefined), undefined)
  assert.equal(staleToolWarning(a, "nyrwvy-zig-host"), undefined) // The Zig host binary is keyed by its sources, so it is never stale.
})

test("an explicitly selected host receives session arguments and its build drives the warning", { timeout: 10_000 }, async () => {
  const { openAuthoring, closeAuthoring } = await import("./authoring.ts")
  const dir = await mkdtemp(join(tmpdir(), "nyrwvy-host-select-"))
  const executable = join(dir, "host")
  const previous = { host: process.env.NYRWVY_HOST, executable: process.env.NYRWVY_HOST_EXECUTABLE }
  const context = { sessionID: "selected-host", worktree: dir }
  try {
    await writeFile(executable, `#!${process.execPath}
const args = process.argv.slice(2)
const input = require("node:readline").createInterface({ input: process.stdin })
const alive = setInterval(() => {}, 1_000)
input.on("line", line => {
  const request = JSON.parse(line)
  console.log(JSON.stringify({ id: request.id, ok: true, result: {
    build: process.env.FAKE_BUILD ?? "nyrwvy-zig-host+${"0".repeat(40)}", args,
  } }))
}).on("close", () => clearInterval(alive))
`)
    await chmod(executable, 0o700)
    process.env.NYRWVY_HOST = "zig"
    process.env.NYRWVY_HOST_EXECUTABLE = executable
    const opened = JSON.parse(await openAuthoring("read", "snapshot.nyrec", context))
    assert.deepEqual(opened.status.result.args, ["open", "--snapshot", join(dir, "snapshot.nyrec"), "--read-only"])
    assert.match(opened.warning, /runs build 000000000000/)
    await closeAuthoring(context)
    // A host from `npm run build:host` names its commit in the stamp beside it, not in its build.
    process.env.FAKE_BUILD = "nyrwvy-zig-host"
    await writeFile(`${executable}.json`, JSON.stringify({ sources: "x", commit: "1".repeat(40) }))
    const stampedOpen = JSON.parse(await openAuthoring("read", "snapshot.nyrec", context))
    assert.equal(stampedOpen.status.result.build, `nyrwvy-zig-host+${"1".repeat(40)}`)
    assert.match(stampedOpen.warning, /runs build 111111111111/)
    await closeAuthoring(context)
    process.env.NYRWVY_HOST_EXECUTABLE = join(dir, "missing")
    await assert.rejects(openAuthoring("read", "snapshot.nyrec", context), /Host executable could not be started: .*missing/)
  } finally {
    try {
      await closeAuthoring(context)
    } finally {
      delete process.env.FAKE_BUILD
      for (const [name, value] of [["NYRWVY_HOST", previous.host], ["NYRWVY_HOST_EXECUTABLE", previous.executable]]) {
        if (value === undefined) delete process.env[name]
        else process.env[name] = value
      }
      await rm(dir, { recursive: true, force: true })
    }
  }
})
