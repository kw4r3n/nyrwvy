import test from "node:test"
import assert from "node:assert/strict"
import childProcess from "node:child_process"
import fs from "node:fs/promises"
import { syncBuiltinESMExports } from "node:module"
import { randomUUID } from "node:crypto"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import {
  startM4AuthoringEvaluation as start,
  requestM4AuthoringEvaluation as request,
  restartM4AuthoringEvaluation as restart,
  finalizeM4AuthoringEvaluation as finalize,
} from "../lib/nyrwvy-m4-authoring-eval-bridge.ts"
import { openAuthoring, closeAuthoring } from "../lib/nyrwvy-authoring.ts"

const healthy = `
  require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
    const request = JSON.parse(line);
    process.stdout.write(JSON.stringify({ id: request.id, ok: true, result: request.op }) + '\\n');
  });
  process.stdin.on('end', () => process.disconnect());
`
const stubborn = `
  process.on('SIGTERM', () => process.send('SIGTERM'));
  process.stdin.on('end', () => process.send('EOF'));
  process.stdin.resume();
  setInterval(() => {}, 1000);
`

async function fixture(t, source = healthy) {
  const root = await fs.mkdtemp(join(tmpdir(), "nyrwvy-m4-lifecycle-"))
  const previous = process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT
  process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = join(root, "results")
  const context = { sessionID: randomUUID(), worktree: root, agent: "nyrwvy-m4-authoring-eval" }
  const children = []
  const spawn = childProcess.spawn
  const controls = { failSpawn: false }
  let resolveSpawned
  const spawned = new Promise(resolve => { resolveSpawned = resolve })
  t.mock.method(childProcess, "spawn", (command, args) => {
    assert.equal(command, "dotnet")
    const snapshot = args[args.indexOf("--snapshot") + 1]
    const child = spawn(process.execPath, ["-e", `${source}\nprocess.send('ready')`], {
      stdio: ["pipe", "pipe", "pipe", "ipc"],
      ...(controls.failSpawn ? { cwd: join(root, "missing-directory") } : {}),
    })
    const entry = { child, snapshot, reaped: false, messages: [] }
    const waiters = []
    child.on("message", message => {
      entry.messages.push(message)
      for (const waiter of waiters.splice(0)) waiter()
    })
    entry.received = async message => {
      while (!entry.messages.includes(message)) await new Promise(resolve => waiters.push(resolve))
    }
    entry.closed = new Promise(resolve => child.once("close", () => { entry.reaped = true; resolve() }))
    children.push(entry)
    resolveSpawned(entry)
    return child
  })
  syncBuiltinESMExports()
  t.after(async () => {
    for (const entry of children) {
      if (!entry.reaped) childProcess.ChildProcess.prototype.kill.call(entry.child, "SIGKILL")
    }
    await Promise.all(children.map(entry => entry.closed))
    t.mock.restoreAll()
    syncBuiltinESMExports()
    if (previous === undefined) delete process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT
    else process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = previous
    for (const path of new Set(children.map(entry => dirname(entry.snapshot)))) {
      await fs.rm(path, { recursive: true, force: true })
    }
    await fs.rm(root, { recursive: true, force: true })
  })
  return { root, context, children, controls, spawned }
}

test("M4 reserves the session before its first await, even with the default UUID result allocator", { timeout: 5000 }, async t => {
  const { context, children } = await fixture(t)
  delete process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT
  const first = start(context)
  await assert.rejects(start(context), /active or starting/)
  await assert.rejects(finalize(context), /still starting/)
  await assert.rejects(request('{"id":"s","op":"status"}', context), /still starting/)
  await first
  assert.equal(children.length, 1)
  await children[0].received("ready")
  const results = await Promise.all([finalize(context), finalize(context)])
  assert.ok(results.every(result => /finalized/.test(result)))
  assert.equal(children[0].reaped, true)
  assert.equal(children[0].child.exitCode, 0)
  await assert.rejects(start(context), /already finalized/)
})

test("M4 sessions remain independent while another session is still starting", { timeout: 5000 }, async t => {
  const { root, context, children } = await fixture(t)
  const mkdir = fs.mkdir
  let release
  const gate = new Promise(resolve => { release = resolve })
  const blockedRoot = join(root, "blocked")
  t.mock.method(fs, "mkdir", async (path, options) => {
    if (path === blockedRoot) await gate
    return mkdir(path, options)
  })
  syncBuiltinESMExports()
  process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = blockedRoot
  const first = start(context)
  try {
    process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = join(root, "independent")
    const independent = { ...context, sessionID: randomUUID() }
    await start(independent)
    await finalize(independent)
    assert.equal(children.length, 1)
    assert.equal(children[0].reaped, true)
  } finally { release() }
  await first
  await finalize(context)
  assert.equal(children.length, 2)
  assert.ok(children.every(entry => entry.reaped))
})

for (const failure of ["configuration", "mkdir", "spawn", "record"]) {
  test(`M4 ${failure} startup failure releases its reservation after cleanup and permits retry`, { timeout: 5000 }, async t => {
    const { root, context, children, controls } = await fixture(t)
    const resultRoot = join(root, "results")
    const transcript = join(resultRoot, "protocol-events.jsonl")
    if (failure === "configuration") process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = "relative"
    if (failure === "mkdir") await fs.writeFile(resultRoot, "not a directory")
    if (failure === "spawn") controls.failSpawn = true
    if (failure === "record") await fs.mkdir(transcript, { recursive: true })
    await assert.rejects(start(context), /absolute|EEXIST|ENOENT|EISDIR/)
    assert.ok(children.every(entry => entry.reaped), "failed start must reap before releasing ownership")
    controls.failSpawn = false
    process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = resultRoot
    if (failure === "mkdir" || failure === "record") await fs.rm(resultRoot, { recursive: true, force: true })
    await start(context)
    await finalize(context)
    assert.ok(children.every(entry => entry.reaped))
  })
}

test("M4 retains its start reservation through abnormal-child cleanup", { timeout: 5000 }, async t => {
  const { context, children, spawned } = await fixture(t, stubborn)
  const appendFile = fs.appendFile
  t.mock.method(fs, "appendFile", async (path, value, options) => {
    if (JSON.parse(value).kind === "start") {
      await children[0].received("ready")
      throw new Error("startup record failed")
    }
    return appendFile(path, value, options)
  })
  syncBuiltinESMExports()
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const failed = assert.rejects(start(context), /startup record failed/)
  // EOF is a barrier: the failed start has reached cleanup, not released its slot.
  const entry = await spawned
  await entry.received("EOF")
  await assert.rejects(start(context), /active or starting/)
  await assert.rejects(finalize(context), /still starting/)
  t.mock.timers.tick(5000)
  await entry.received("SIGTERM")
  t.mock.timers.tick(5000)
  await failed
  assert.equal(entry.reaped, true)
  assert.equal(entry.child.signalCode, "SIGKILL")

})

test("M4 finalization follows the current child across a queued restart", { timeout: 5000 }, async t => {
  const { context, children } = await fixture(t)
  await start(context)
  const restarting = restart(context)
  const finalizing = finalize(context)
  const joining = finalize(context)
  await assert.rejects(restart(context), /finalizing/)
  await assert.rejects(request('{"id":"late","op":"status"}', context), /finalizing/)
  await Promise.all([restarting, finalizing, joining])
  assert.equal(children.length, 2)
  assert.ok(children.every(entry => entry.reaped))
  const events = (await fs.readFile(join(dirname(children[1].snapshot), "protocol-events.jsonl"), "utf8"))
    .trim().split("\n").map(JSON.parse)
  assert.deepEqual(events.map(event => event.kind), ["start", "restart", "reopened", "finalize"])
})

test("M4 still reaps when finalization recording fails", { timeout: 5000 }, async t => {
  const { context, children } = await fixture(t)
  await start(context)
  const transcript = join(dirname(children[0].snapshot), "protocol-events.jsonl")
  await fs.rm(transcript)
  await fs.mkdir(transcript)
  await assert.rejects(finalize(context), /EISDIR/)
  assert.equal(children[0].reaped, true)
  await assert.rejects(start(context), /already finalized/)
})

test("M4 finalization escalates ignored EOF and SIGTERM without releasing a running child", { timeout: 5000 }, async t => {
  const { context, children } = await fixture(t, stubborn)
  await start(context)
  const entry = children[0]
  await entry.received("ready")
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const finalizing = assert.rejects(finalize(context), /exited with code signal/)
  await entry.received("EOF")
  await assert.rejects(start(context), /active or starting/)
  t.mock.timers.tick(5000)
  await entry.received("SIGTERM")
  assert.equal(entry.reaped, false)
  t.mock.timers.tick(5000)
  await finalizing
  assert.equal(entry.reaped, true)
  assert.equal(entry.child.signalCode, "SIGKILL")
  await assert.rejects(start(context), /already finalized/)
})

test("M4 does not forget ownership when signalling succeeds but reaping times out", { timeout: 5000 }, async t => {
  const { context, children } = await fixture(t, stubborn)
  await start(context)
  const entry = children[0]
  await entry.received("ready")
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const kill = entry.child.kill.bind(entry.child)
  let observeKill
  const signalled = new Promise(resolve => { observeKill = resolve })
  t.mock.method(entry.child, "kill", signal => {
    if (signal === "SIGKILL") { observeKill(); return true }
    return kill(signal)
  })
  const finalizing = assert.rejects(finalize(context), /5000ms after SIGKILL; child ownership is retained/)
  await entry.received("EOF")
  t.mock.timers.tick(5000)
  await entry.received("SIGTERM")
  t.mock.timers.tick(5000)
  await signalled
  t.mock.timers.tick(5000)
  await finalizing
  assert.equal(entry.reaped, false)
  await assert.rejects(start(context), /active or starting/)
  assert.equal(children.length, 1)
  kill("SIGKILL")
  await entry.closed
  await assert.rejects(finalize(context), /ownership is retained/)
  await assert.rejects(start(context), /already finalized/)
})

test("real M4 AuthoringSession drains requests across restart and releases the snapshot lock on finalization", { timeout: 20000 }, async () => {
  const root = await fs.mkdtemp(join(tmpdir(), "nyrwvy-m4-real-"))
  const previous = process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT
  process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = root
  const context = { sessionID: randomUUID(), worktree: fileURLToPath(new URL("../../", import.meta.url)), agent: "nyrwvy-m4-authoring-eval" }
  const reader = { ...context, sessionID: randomUUID() }
  try {
    await start(context)
    const before = JSON.parse(await request('{"id":"before","op":"status"}', context))
    assert.equal(before.ok, true)
    const results = await Promise.all([
      restart(context), request('{"id":"after","op":"status"}', context), finalize(context),
    ])
    const after = JSON.parse(results[1])
    assert.equal(after.ok, true)
    assert.deepEqual(after.result, before.result)
    const reopened = JSON.parse(await openAuthoring("open", join(root, "workspace.json"), reader))
    assert.deepEqual(reopened.status.result, before.result)
  } finally {
    await finalize(context).catch(() => undefined)
    await closeAuthoring(reader).catch(() => undefined)
    if (previous === undefined) delete process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT
    else process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT = previous
    await fs.rm(root, { recursive: true, force: true })
  }
})
