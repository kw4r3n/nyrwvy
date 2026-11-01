import { describe, test } from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, rm, readFile, writeFile } from "node:fs/promises"
import { watch } from "node:fs"
import { spawn } from "node:child_process"
import { createInterface } from "node:readline"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"

const serverPath = fileURLToPath(new URL("./server.mjs", import.meta.url))

// Raw stdio deliberately avoids the SDK client's own kill deadlines: only the
// server under test may terminate/reap its authoring child before we assert exit.
async function abnormalServer(t, phase) {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-mcp-shutdown-"))
  const log = join(root, "child-events.jsonl")
  await writeFile(log, "")
  await writeFile(join(root, "host"), `#!${process.execPath}
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
    cwd: root, env: { ...process.env, NYRWVY_HOST_EXECUTABLE: join(root, "host") }, stdio: ["pipe", "pipe", "pipe"],
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

// Each case waits out the server's real drain and kill deadlines, so they run side by side.
describe("MCP abnormal authoring children", { concurrency: true }, () => {
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

  // The close waits out real 5 s drain/EOF deadlines; give it the same headroom as its siblings for a loaded runner.
  test("MCP explicit close gracefully drains earlier calls without disturbing close/open/inspect order", { timeout: 30000 }, async t => {
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
})
