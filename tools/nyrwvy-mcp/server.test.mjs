import test from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, mkdir, rm, readFile, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"
import { Client } from "@modelcontextprotocol/client"
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio"

const serverPath = fileURLToPath(new URL("./server.mjs", import.meta.url))
const bool = value => ({ kind: "boolLiteral", value })
const names = ["open", "inspect", "view", "recipe_reference", "create_type", "create_function", "replace", "commit", "reject", "request", "close"]

async function connect(cwd, workspace, options, extraArgs = []) {
  const client = new Client({ name: "nyrwvy-contract", version: "1.0.0" }, options)
  try {
    await client.connect(new StdioClientTransport({ command: process.execPath,
      args: ["--experimental-strip-types", serverPath, ...(workspace ? ["--workspace", workspace] : []), ...extraArgs], cwd, stderr: "pipe" }))
    return client
  } catch (error) { await client.close().catch(() => undefined); throw error }
}
async function call(client, name, args = {}) {
  const result = await client.callTool({ name: `nyrwvy_${name}`, arguments: args })
  assert.notEqual(result.isError, true, JSON.stringify(result.content))
  const text = result.content.find(block => block.type === "text").text
  const value = result.structuredContent
  if (!value.message) assert.deepEqual(JSON.parse(text), value)
  return value
}
async function failed(client, name, args) {
  const result = await client.callTool({ name: `nyrwvy_${name}`, arguments: args })
  assert.equal(result.isError, true, JSON.stringify(result))
  return result
}

// One real wire contract, not another semantic suite or a model benchmark.
test("MCP exposes shared tools, preserves explicit publication, and releases the graph on client exit", { timeout: 30000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-mcp-"))
  const workspace = join(root, "graph workspace")
  await mkdir(workspace)
  let first, second
  try {
    // Neither process starts in the toolchain repository. The first client starts
    // in the graph project and therefore needs no fixed workspace registration.
    first = await connect(workspace, undefined)
    second = await connect(root, undefined, { versionNegotiation: { mode: { pin: "2026-07-28" } } })
    assert.equal(first.getProtocolEra(), "legacy")
    assert.equal(second.getProtocolEra(), "modern")
    const tools = (await first.listTools()).tools
    assert.deepEqual(tools.map(tool => tool.name).sort(), names.map(name => `nyrwvy_${name}`).sort())
    assert.equal(tools.find(tool => tool.name === "nyrwvy_create_function").inputSchema.properties.body.type, "object")
    assert.ok(!tools.find(tool => tool.name === "nyrwvy_replace").inputSchema.required.includes("targetNodeId"))
    assert.equal(tools.find(tool => tool.name === "nyrwvy_recipe_reference").annotations.readOnlyHint, true)

    // Recipe discovery is sessionless and covers the wire-shape mistakes observed in the first external consumer.
    const recipeIndex = await call(first, "recipe_reference")
    assert.ok(recipeIndex.categories.control.includes("conditional"))
    assert.equal((await call(first, "recipe_reference", { category: "float64" })).signatures.float64Sqrt, "(value: Float64) -> Float64")
    assert.deepEqual((await call(first, "recipe_reference", { kinds: ["fieldAccess", "matchUnion"] })).entries.map(entry => entry.kind), ["fieldAccess", "matchUnion"])
    assert.deepEqual((await call(first, "recipe_reference", { kind: "conditional" })).shape,
      { kind: "conditional", condition: "<recipe>", whenTrue: "<recipe>", whenFalse: "<recipe>" })
    assert.deepEqual((await call(first, "recipe_reference", { kind: "fieldAccess" })).shape,
      { kind: "fieldAccess", source: "<recipe>", type: "<product type display name>", field: "<field display name>" })
    assert.deepEqual((await call(first, "recipe_reference", { kind: "matchUnion" })).shape,
      { kind: "matchUnion", type: "<union type display name>", input: "<recipe>", arms: [{ variant: "<variant display name>", body: "<recipe>" }, { variants: ["<variant>", "<variant>"], body: "<recipe>" }], otherwise: "<recipe>" })
    await failed(first, "open", { mode: "unexpected", snapshot: "workspace.json" })
    await failed(first, "recipe_reference", { kind: "__proto__" })
    await failed(first, "inspect", { view: "status", extra: true })
    await failed(second, "inspect", { view: "status" })
    // A session that cannot start reports its own diagnostic, not a lost response.
    const missing = await failed(first, "open", { mode: "open", snapshot: "missing.json" })
    assert.match(missing.content[0].text, /SnapshotFileNotFound: No snapshot file at .*missing\.json.*create/)
    assert.doesNotMatch(missing.content[0].text, /replay/)
    const noWorkspace = await failed(first, "open", { mode: "create", snapshot: "workspace.json", workspace: join(root, "absent") })
    assert.match(noWorkspace.content[0].text, /^workspace directory does not exist: /)
    const opened = await call(first, "open", { mode: "create", snapshot: "workspace.json" })
    assert.equal(opened.snapshot, join(workspace, "workspace.json"))
    // Host C# sources are server configuration, off by default, and never enabled per request.
    assert.equal(opened.hostSourcesAllowed, false)
    await failed(second, "open", { mode: "open", snapshot: "workspace.json", workspace })

    const type = await call(first, "create_type", { displayName: "Request", typeKind: { kind: "product", fields: [{ displayName: "enabled", type: { kind: "primitive", primitive: "bool" } }] } })
    await call(first, "commit", { proposalId: type.result.proposalId })
    const created = (await call(first, "create_function", { displayName: "value", parameters: [],
      resultType: { kind: "primitive", primitive: "bool" }, requiredEffects: [], body: bool(true) })).result
    const functionId = created.review.createdFunction.id
    // Simulate reviews are compact by default: signature and counts, no per-node graph.
    assert.equal(created.detail, "compact")
    assert.equal(created.review.createdFunction.body.nodeCount, 1)
    assert.equal(created.review.createdFunction.body.nodes, undefined)
    assert.equal((await call(first, "inspect", { view: "declarations" })).result.functions.length, 0)
    await call(first, "commit", { proposalId: created.proposalId })
    const before = (await call(first, "inspect", { view: "function", functionId })).result
    const target = before.function.body.resultNodeId

    const error = await failed(first, "replace", { function: "missing", replacement: bool(false) })
    assert.equal(error.structuredContent.ok, false)
    assert.ok(error.structuredContent.error.code)
    const replacement = (await call(first, "replace", { function: "value", targetNodeId: target, replacement: bool(false), detail: "full" })).result
    assert.equal(replacement.review.proposedFocus.function.body.nodes[0].kind.value, false)
    assert.equal((await call(first, "inspect", { view: "status" })).result.snapshotId, before.snapshotId)
    // verifyChange is opt-in and evaluates the pending candidate without publication.
    const snapshotBytes = await readFile(opened.snapshot)
    const verified = (await call(first, "request", { op: "verifyChange", arguments: {
      proposalId: replacement.proposalId, cases: [{ label: "explicit false result", functionId, arguments: [], expectedCandidate: { kind: "bool", value: false } }], output: "views/verified.html"
    } })).result
    assert.equal(verified.evidence.cases[0].status, "intended-change-tested")
    assert.equal(verified.evidence.cases[0].candidate.matches, true)
    assert.deepEqual(await readFile(opened.snapshot), snapshotBytes)
    assert.match(await readFile(verified.reviewPath, "utf8"), /not dynamic coverage/)
    await call(first, "commit", { proposalId: replacement.proposalId })
    // A human-readable page of the committed function, written relative to the project.
    const page = (await call(first, "view", { function: "value", output: "views/value.html" })).result
    assert.equal(page.path, join(workspace, "views", "value.html"))
    assert.equal(page.functionId, functionId)
    assert.match(await readFile(page.path, "utf8"), /Function Focus/)
    // Output is confined to the snapshot's directory, so an auto-approved model cannot overwrite other files.
    for (const output of ["../escaped.html", join(root, "escaped.html")]) {
      const refused = await failed(first, "view", { function: "value", output })
      assert.equal(refused.structuredContent.error.code, "ViewWriteFailed")
    }
    await assert.rejects(readFile(join(root, "escaped.html")), { code: "ENOENT" })
    const discard = (await call(first, "replace", { function: "value", replacement: bool(true) })).result
    // A person reviews the pending proposal before it is rejected.
    const review = (await call(first, "view", { proposalId: discard.proposalId })).result
    assert.equal(review.kind, "changeReview")
    assert.match(await readFile(review.path, "utf8"), /Change Review — replaceBody/)
    await failed(first, "view", { proposalId: discard.proposalId, function: "value" })
    await call(first, "reject", { proposalId: discard.proposalId })
    await failed(first, "view", { proposalId: discard.proposalId })
    const pending = (await call(first, "replace", { function: "value", replacement: bool(true) })).result
    assert.equal((await call(first, "request", { op: "executionReadiness", arguments: {} })).result.kind, "ready")
    const disabled = await failed(first, "request", { op: "runPure", arguments: { functionId, arguments: [], hostSources: ["Host.cs"] } })
    assert.equal(disabled.structuredContent.error.code, "RunHostSourcesDisabled")
    await failed(first, "request", { op: "status", arguments: { id: "not-tool-owned" } })
    await failed(first, "request", { op: "status", arguments: { op: "declarations" } })

    // EOF must release the actual AuthoringSession lock without nyrwvy_close.
    await first.close()
    await assert.rejects(readFile(join(workspace, "workspace.json.lock")), { code: "ENOENT" }, "a clean close removes the lock file")
    const reopened = await call(second, "open", { mode: "open", snapshot: "workspace.json", workspace })
    assert.equal(reopened.snapshot, opened.snapshot)
    const resumed = (await call(second, "inspect", { view: "function", functionId })).result.function
    assert.equal(resumed.id, functionId)
    assert.equal(resumed.body.nodes[0].kind.value, false)
    await failed(second, "commit", { proposalId: pending.proposalId })
    // Lifecycle calls share the same queue as requests, even when sent without awaiting one another.
    const concurrent = await Promise.all([
      call(second, "close"),
      call(second, "open", { mode: "open", snapshot: "workspace.json", workspace }),
      call(second, "inspect", { view: "function", functionId }),
    ])
    assert.equal(concurrent[1].snapshot, opened.snapshot)
    assert.deepEqual(concurrent[2].result.function, resumed)
  } finally {
    await first?.close().catch(() => undefined)
    await second?.close().catch(() => undefined)
    await rm(root, { recursive: true, force: true })
  }
})

test("MCP creates and reopens a pinned dependency with relative paths and read-only imports", { timeout: 30000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-mcp-dependency-"))
  const workspace = join(root, "graph workspace")
  await mkdir(workspace)
  let client
  try {
    client = await connect(root)
    const tools = (await client.listTools()).tools
    const openSchema = tools.find(tool => tool.name === "nyrwvy_open").inputSchema
    assert.equal(openSchema.properties.dependency.type, "string")
    assert.ok(!openSchema.required.includes("dependency"))
    await call(client, "open", { mode: "create", snapshot: "policy.json", workspace })
    const policy = (await call(client, "create_function", { displayName: "policy", parameters: [],
      resultType: { kind: "primitive", primitive: "bool" }, requiredEffects: [], body: bool(true) })).result
    await call(client, "commit", { proposalId: policy.proposalId })
    await call(client, "close")
    const source = await readFile(join(workspace, "policy.json"), "utf8")

    // Invalid combinations must not start a process, create files or reserve a slot.
    await failed(client, "open", { mode: "open", snapshot: "planner.json", workspace, dependency: "policy.json" })
    await failed(client, "open", { mode: "create", snapshot: "planner.json", workspace, dependency: " " })
    await assert.rejects(readFile(join(workspace, "planner.json")), { code: "ENOENT" })
    await call(client, "open", { mode: "create", snapshot: "planner.json", workspace, dependency: "policy.json" })
    const selection = (await call(client, "request", { op: "dependency", arguments: {} })).result
    assert.equal(selection.selection.path, join(workspace, "policy.json"))
    assert.equal(selection.selection.storage, "embedded")
    assert.equal(selection.selection.snapshotJson, undefined)
    const before = (await call(client, "inspect", { view: "status" })).result
    const importedEdit = await failed(client, "replace", { function: "policy", replacement: bool(false) })
    assert.match(JSON.stringify(importedEdit.structuredContent), /ImportedDeclarationModified/)
    await failed(client, "create_function", { displayName: "policy", parameters: [],
      resultType: { kind: "primitive", primitive: "bool" }, requiredEffects: [], body: bool(false) })
    assert.deepEqual((await call(client, "inspect", { view: "status" })).result, before)

    const local = (await call(client, "create_function", { displayName: "plan", parameters: [],
      resultType: { kind: "primitive", primitive: "bool" }, requiredEffects: [],
      body: { kind: "directCall", function: "policy", arguments: [] } })).result
    await call(client, "commit", { proposalId: local.proposalId })
    const localFile = join(workspace, "planner.json")
    const stored = JSON.parse(await readFile(localFile, "utf8"))
    assert.equal(stored.functions.length, 1)
    assert.equal(stored.functions[0].id, local.review.createdFunction.id)
    assert.equal(await readFile(join(workspace, "policy.json"), "utf8"), source)
    await call(client, "close")
    await call(client, "open", { mode: "open", snapshot: "policy.json", workspace })
    const upstream = (await call(client, "create_function", { displayName: "new_upstream", parameters: [],
      resultType: { kind: "primitive", primitive: "bool" }, requiredEffects: [], body: bool(false) })).result
    await call(client, "commit", { proposalId: upstream.proposalId })
    await call(client, "close")
    await call(client, "open", { mode: "open", snapshot: "planner.json", workspace })
    assert.equal((await call(client, "inspect", { view: "declarations" })).result.functions.length, 2)
    assert.equal((await call(client, "request", { op: "executionReadiness", arguments: {} })).result.kind, "ready")
    await call(client, "close")

    // Exercise the reported null-path crash through the real process boundary.
    const manifestFile = localFile + ".dependency.json"
    // Reproduce the old source-only format and recover through the real pin CLI
    // mode exposed by MCP. A different upstream version cannot be substituted.
    const { snapshotJson, ...legacyManifest } = JSON.parse(await readFile(manifestFile, "utf8"))
    assert.equal(typeof snapshotJson, "string")
    await writeFile(manifestFile, JSON.stringify(legacyManifest))
    await writeFile(join(workspace, "original-policy.json"), source)
    const localBytes = await readFile(localFile, "utf8")
    await failed(client, "open", { mode: "open", snapshot: "planner.json", workspace })
    await failed(client, "open", { mode: "pin", snapshot: "planner.json", workspace, dependency: "policy.json" })
    await call(client, "open", { mode: "pin", snapshot: "planner.json", workspace, dependency: "original-policy.json" })
    assert.equal((await call(client, "inspect", { view: "declarations" })).result.functions.length, 2)
    assert.equal(await readFile(localFile, "utf8"), localBytes)
    await call(client, "close")
    const manifest = await readFile(manifestFile, "utf8")
    await writeFile(manifestFile, JSON.stringify({ ...JSON.parse(manifest), path: null }))
    const invalid = await failed(client, "open", { mode: "open", snapshot: "planner.json", workspace })
    assert.match(JSON.stringify(invalid.content), /DependencyFailure/)
    await writeFile(manifestFile, manifest)
    await call(client, "open", { mode: "open", snapshot: "planner.json", workspace })
  } finally {
    await client?.close().catch(() => undefined)
    await rm(root, { recursive: true, force: true })
  }
})

test("MCP --allow-host-sources starts the session with host sources enabled", { timeout: 30000 }, async () => {
  const workspace = await mkdtemp(join(tmpdir(), "nyrwvy-mcp-host-"))
  let client
  try {
    client = await connect(workspace, undefined, undefined, ["--allow-host-sources"])
    const opened = await call(client, "open", { mode: "create", snapshot: "workspace.json" })
    assert.equal(opened.hostSourcesAllowed, true)
    const created = (await call(client, "create_function", { displayName: "value", parameters: [],
      resultType: { kind: "primitive", primitive: "bool" }, requiredEffects: [], body: bool(true) })).result
    await call(client, "commit", { proposalId: created.proposalId })
    // Past the gate, request validation applies: sources without a hostCapability binding are invalid.
    const invalid = await failed(client, "request", { op: "runPure",
      arguments: { functionId: created.review.createdFunction.id, arguments: [], hostSources: ["Host.cs"] } })
    assert.equal(invalid.structuredContent.error.code, "RunHostSourceInvalid")
  } finally {
    await client?.close()
    await rm(workspace, { recursive: true, force: true })
  }
})
