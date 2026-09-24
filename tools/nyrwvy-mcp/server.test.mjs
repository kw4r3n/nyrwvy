import test from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, mkdir, rm, readFile, writeFile } from "node:fs/promises"
import { watch } from "node:fs"
import { spawn } from "node:child_process"
import { createInterface } from "node:readline"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"
import { Client } from "@modelcontextprotocol/client"
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio"

const serverPath = fileURLToPath(new URL("./server.mjs", import.meta.url))
const bool = value => ({ kind: "boolLiteral", value })
const names = ["open", "inspect", "recipe_reference", "create_type", "create_function", "replace", "commit", "reject", "request", "close"]

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
    assert.equal(tools.find(tool => tool.name === "nyrwvy_recipe_reference").annotations.readOnlyHint, true)

    // Recipe discovery is sessionless and covers the wire-shape mistakes observed in the first external consumer.
    const recipeIndex = await call(first, "recipe_reference")
    assert.ok(recipeIndex.categories.control.includes("conditional"))
    assert.deepEqual((await call(first, "recipe_reference", { kind: "conditional" })).shape,
      { kind: "conditional", condition: "<recipe>", whenTrue: "<recipe>", whenFalse: "<recipe>" })
    assert.deepEqual((await call(first, "recipe_reference", { kind: "fieldAccess" })).shape,
      { kind: "fieldAccess", source: "<recipe>", type: "<product type display name>", field: "<field display name>" })
    assert.deepEqual((await call(first, "recipe_reference", { kind: "matchUnion" })).shape,
      { kind: "matchUnion", type: "<union type display name>", input: "<recipe>", arms: [{ variant: "<variant display name>", body: "<recipe>" }] })
    await failed(first, "open", { mode: "unexpected", snapshot: "workspace.json" })
    await failed(first, "recipe_reference", { kind: "__proto__" })
    await failed(first, "inspect", { view: "status", extra: true })
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
    await failed(first, "request", { op: "status", arguments: { op: "declarations" } })

    // EOF must release the actual AuthoringSession lock without nyrwvy_close.
    await first.close()
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

// Raw stdio deliberately avoids the SDK client's own kill deadlines: only the
// server under test may terminate/reap its authoring child before we assert exit.
async function abnormalServer(t, phase) {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-mcp-shutdown-"))
  const log = join(root, "child-events.jsonl")
  await writeFile(log, "")
  await writeFile(join(root, "dotnet"), `#!${process.execPath}
    const fs = require('node:fs');
    const recovering = fs.readFileSync(${JSON.stringify(log)}, 'utf8').includes('"kind":"ready"');
    const phase = ${JSON.stringify(phase)};
    const healthy = recovering || phase === 'graceful';
    const record = event => fs.appendFileSync(${JSON.stringify(log)}, JSON.stringify({ ...event, pid: process.pid }) + '\\n');
    const reply = request => {
      record({ kind: 'response', op: request.op });
      process.stdout.write(JSON.stringify({ id: request.id, ok: true, result: {} }) + '\\n');
    };
    let held, release;
    if (phase === 'graceful' && !recovering) {
      release = fs.watch(${JSON.stringify(root)}, () => {
        if (held && fs.existsSync(${JSON.stringify(join(root, "release"))})) {
          reply(held);
          held = undefined;
        }
      });
    }
    process.on('SIGTERM', () => record({ kind: 'SIGTERM' }));
    process.stdin.on('end', () => {
      record({ kind: 'EOF' });
      if (healthy) { clearInterval(alive); release?.close(); }
    });
    require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
      const request = JSON.parse(line);
      record({ kind: 'request', op: request.op });
      if (phase === 'graceful' && !recovering && request.op === 'commit') held = request;
      else if (healthy || (phase !== 'open' && request.op === 'status')) reply(request);
    });
    record({ kind: 'ready' });
    const alive = setInterval(() => {}, 1000);
  `, { mode: 0o700 })
  const server = spawn(process.execPath, ["--experimental-strip-types", serverPath], {
    cwd: root, env: { ...process.env, PATH: `${root}:${process.env.PATH}` }, stdio: ["pipe", "pipe", "pipe"],
  })
  let stderr = ""
  server.stderr.on("data", chunk => { stderr += chunk })
  const pending = new Map()
  let id = 0
  let reaped = false
  const closed = new Promise(resolve => server.once("close", (code, signal) => {
    reaped = true
    for (const resolveReply of pending.values()) resolveReply({ disconnected: true })
    pending.clear()
    resolve({ code, signal })
  }))
  createInterface({ input: server.stdout }).on("line", line => {
    const response = JSON.parse(line)
    pending.get(response.id)?.(response)
    pending.delete(response.id)
  })
  function send(method, params, notification = false) {
    const message = { jsonrpc: "2.0", method, params }
    if (!notification) message.id = ++id
    const result = notification ? undefined : new Promise(resolve => pending.set(message.id, resolve))
    server.stdin.write(`${JSON.stringify(message)}\n`)
    return result
  }
  const events = async () => (await readFile(log, "utf8")).trim().split("\n").filter(Boolean).map(JSON.parse)
  async function received(predicate) {
    let wake
    const changes = watch(root, () => wake?.())
    try {
      while (true) {
        const changed = new Promise(resolve => { wake = resolve })
        if ((await events()).some(predicate)) return
        await changed
      }
    } finally { changes.close() }
  }
  t.after(async () => {
    // On failure, release the child first so its still-running server can reap it.
    if (!reaped) {
      const recorded = await events()
      t.diagnostic(`unfinished server cleanup: ${JSON.stringify(recorded)}`)
      for (const pid of new Set(recorded.map(event => event.pid))) {
        try { process.kill(pid, "SIGKILL") } catch (error) { if (error.code !== "ESRCH") throw error }
      }
      server.stdin.end()
    }
    await closed
    await rm(root, { recursive: true, force: true })
  })
  const initialized = await send("initialize", { protocolVersion: "2025-11-25", capabilities: {}, clientInfo: { name: "shutdown-contract", version: "1" } })
  assert.ok(initialized.result, JSON.stringify(initialized))
  send("notifications/initialized", {}, true)
  const tool = (name, args = {}) => send("tools/call", { name: `nyrwvy_${name}`, arguments: args })
  return { server, tool, received, events, closed, stderr: () => stderr,
    ping: () => send("ping", {}), release: () => writeFile(join(root, "release"), "reply") }
}

test("MCP explicit close bounds its preceding tool queue and permits ordered reopen", { timeout: 30000, concurrency: true }, async t => {
  await Promise.all(["open", "request"].map(phase => t.test(`hung ${phase}`, { timeout: 20000 }, async t => {
    const api = await abnormalServer(t, phase)
    const opening = api.tool("open", { mode: "create", snapshot: "workspace.json" })
    await api.received(event => event.op === "status")
    let blocked = opening
    if (phase === "request") {
      assert.notEqual((await opening).result.isError, true)
      blocked = api.tool("commit", { proposalId: "sent-once" })
      await api.received(event => event.op === "commit")
    }
    const queued = api.tool("inspect", { view: "status" })
    const closing = api.tool("close")
    const reopening = api.tool("open", { mode: "open", snapshot: "workspace.json" })
    const inspecting = api.tool("inspect", { view: "status" })
    // Neither EOF nor a server signal rescues this close; stdin remains open.
    const closed = await closing
    assert.equal(closed.result.isError, true)
    assert.match(closed.result.content[0].text, /outcome is unknown.*never replayed/)
    for (const result of await Promise.all([blocked, queued])) assert.equal(result.result.isError, true)
    assert.notEqual((await reopening).result.isError, true)
    assert.notEqual((await inspecting).result.isError, true)
    const events = await api.events()
    const children = events.filter(event => event.kind === "ready").map(event => event.pid)
    assert.equal(children.length, 2)
    assert.throws(() => process.kill(children[0], 0), { code: "ESRCH" }, "close must reap before releasing the slot for reopen")
    assert.deepEqual(events.filter(event => event.kind === "request" && event.pid === children[0]).map(event => event.op),
      phase === "request" ? ["status", "commit"] : ["status"])
    assert.deepEqual(events.filter(event => event.kind === "request" && event.pid === children[1]).map(event => event.op), ["status", "status"])
    assert.notEqual((await api.tool("close")).result.isError, true)
    api.server.stdin.end()
    assert.equal((await api.closed).code, 0, api.stderr())
  })))
})

test("MCP explicit close gracefully drains earlier calls without disturbing close/open/inspect order", { timeout: 10000 }, async t => {
  const api = await abnormalServer(t, "graceful")
  assert.notEqual((await api.tool("open", { mode: "create", snapshot: "workspace.json" })).result.isError, true)
  const committing = api.tool("commit", { proposalId: "sent-once" })
  await api.received(event => event.op === "commit")
  const before = api.tool("inspect", { view: "status" })
  const closing = api.tool("close")
  const reopening = api.tool("open", { mode: "open", snapshot: "workspace.json" })
  const after = api.tool("inspect", { view: "status" })
  // Ping bypasses the tool queue and acknowledges that the close was received.
  await api.ping()
  await api.release()
  for (const response of await Promise.all([committing, before, closing, reopening, after])) {
    assert.notEqual(response.result.isError, true, JSON.stringify(response))
  }
  const events = await api.events()
  const children = events.filter(event => event.kind === "ready").map(event => event.pid)
  assert.equal(children.length, 2)
  assert.throws(() => process.kill(children[0], 0), { code: "ESRCH" })
  assert.deepEqual(events.filter(event => event.pid === children[0]).map(event => [event.kind, event.op]), [
    ["ready", undefined], ["request", "status"], ["response", "status"],
    ["request", "commit"], ["response", "commit"], ["request", "status"], ["response", "status"], ["EOF", undefined],
  ])
  assert.deepEqual(events.filter(event => event.kind === "request" && event.pid === children[1]).map(event => event.op), ["status", "status"])
  assert.notEqual((await api.tool("close")).result.isError, true)
  api.server.stdin.end()
  assert.equal((await api.closed).code, 0, api.stderr())
})

test("MCP shutdown bounds abnormal authoring children end to end", { timeout: 30000, concurrency: true }, async t => {
  await Promise.all([
    { phase: "idle", shutdown: "EOF" },
    { phase: "request", shutdown: "SIGTERM" },
    { phase: "open", shutdown: "EOF" },
    { phase: "close", shutdown: "SIGTERM" },
  ].map(({ phase, shutdown }) => t.test(`${shutdown} during ${phase}`, { timeout: 25000 }, async t => {
    const api = await abnormalServer(t, phase)
    const opening = api.tool("open", { mode: "create", snapshot: "workspace.json" })
    await api.received(event => event.op === "status")
    const calls = [opening]
    if (phase !== "open") {
      assert.notEqual((await opening).result?.isError, true)
      if (phase === "request") {
        calls.push(api.tool("commit", { proposalId: "sent-once" }), api.tool("inspect", { view: "status" }))
        await api.received(event => event.op === "commit")
      }
      if (phase === "close") {
        calls.push(api.tool("close"))
        await api.received(event => event.kind === "EOF")
      }
    }
    if (shutdown === "EOF") api.server.stdin.end()
    else api.server.kill("SIGTERM")
    const exit = await api.closed
    assert.equal(exit.signal, null, api.stderr())
    assert.ok(exit.code === 0 || exit.code === 1, api.stderr())
    const events = await api.events()
    assert.equal(events.filter(event => event.kind === "ready").length, 1)
    assert.ok(events.some(event => event.kind === "SIGTERM"), "the child installed and ignored its SIGTERM handler")
    const pid = events.find(event => event.kind === "ready").pid
    assert.throws(() => process.kill(pid, 0), { code: "ESRCH" }, "server exit must follow child reaping, not merely signalling")
    assert.deepEqual(events.filter(event => event.kind === "request").map(event => event.op),
      phase === "request" ? ["status", "commit"] : ["status"])
    const results = await Promise.all(calls)
    if (phase === "request") {
      assert.equal(results[1].result.isError, true)
      assert.match(results[1].result.content[0].text, /outcome is unknown.*never replayed/)
      assert.equal(results[2].result.isError, true)
    }
  })))
})
