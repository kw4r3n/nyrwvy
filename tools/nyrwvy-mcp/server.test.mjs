import test from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, mkdir, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"
import { Client } from "@modelcontextprotocol/client"
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio"

const serverPath = fileURLToPath(new URL("./server.mjs", import.meta.url))
const bool = value => ({ kind: "boolLiteral", value })
const names = ["open", "inspect", "create_type", "create_function", "replace", "commit", "reject", "request", "close"]

async function connect(cwd, workspace, options) {
  const client = new Client({ name: "nyrwvy-contract", version: "1.0.0" }, options)
  try {
    await client.connect(new StdioClientTransport({ command: process.execPath,
      args: ["--experimental-strip-types", serverPath, ...(workspace ? ["--workspace", workspace] : [])], cwd, stderr: "pipe" }))
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
    await failed(first, "open", { mode: "unexpected", snapshot: "workspace.json" })
    await failed(second, "inspect", { view: "status" })
    const opened = await call(first, "open", { mode: "create", snapshot: "workspace.json" })
    assert.equal(opened.snapshot, join(workspace, "workspace.json"))
    await failed(second, "open", { mode: "open", snapshot: "workspace.json", workspace })

    const type = await call(first, "create_type", { displayName: "Request", typeKind: { kind: "product", fields: [{ displayName: "enabled", type: { kind: "primitive", primitive: "bool" } }] } })
    await call(first, "commit", { proposalId: type.result.proposalId })
    const created = (await call(first, "create_function", { displayName: "value", parameters: [],
      resultType: { kind: "primitive", primitive: "bool" }, requiredEffects: [], body: bool(true) })).result
    const functionId = created.review.createdFunction.id
    assert.equal((await call(first, "inspect", { view: "declarations" })).result.functions.length, 0)
    await call(first, "commit", { proposalId: created.proposalId })
    const before = (await call(first, "inspect", { view: "function", functionId })).result
    const target = before.function.body.resultNodeId

    const error = await failed(first, "replace", { function: "missing", replacement: bool(false) })
    assert.equal(error.structuredContent.ok, false)
    assert.ok(error.structuredContent.error.code)
    const replacement = (await call(first, "replace", { function: "value", targetNodeId: target, replacement: bool(false) })).result
    assert.equal(replacement.review.proposedFocus.function.body.nodes[0].kind.value, false)
    assert.equal((await call(first, "inspect", { view: "status" })).result.snapshotId, before.snapshotId)
    await call(first, "commit", { proposalId: replacement.proposalId })
    const discard = (await call(first, "replace", { function: "value", replacement: bool(true) })).result
    await call(first, "reject", { proposalId: discard.proposalId })
    const pending = (await call(first, "replace", { function: "value", replacement: bool(true) })).result
    assert.equal((await call(first, "request", { op: "executionReadiness", arguments: {} })).result.kind, "ready")
    await failed(first, "request", { op: "status", arguments: { id: "not-tool-owned" } })

    // EOF must release the actual AuthoringSession lock without nyrwvy_close.
    await first.close()
    const reopened = await call(second, "open", { mode: "open", snapshot: "workspace.json", workspace })
    assert.equal(reopened.snapshot, opened.snapshot)
    const resumed = (await call(second, "inspect", { view: "function", functionId })).result.function
    assert.equal(resumed.id, functionId)
    assert.equal(resumed.body.nodes[0].kind.value, false)
    await failed(second, "commit", { proposalId: pending.proposalId })
    await call(second, "close")
    await call(second, "open", { mode: "open", snapshot: "workspace.json", workspace })
  } finally {
    await first?.close().catch(() => undefined)
    await second?.close().catch(() => undefined)
    await rm(root, { recursive: true, force: true })
  }
})
