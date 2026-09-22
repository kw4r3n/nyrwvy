import test from "node:test"
import assert from "node:assert/strict"
import childProcess, { spawn } from "node:child_process"
import { syncBuiltinESMExports } from "node:module"
import { randomUUID } from "node:crypto"
import { once } from "node:events"
import { AuthoringClient, openAuthoring, closeAuthoring } from "../lib/nyrwvy-authoring.ts"

function child(source) {
  return spawn(process.execPath, ["-e", source], { stdio: ["pipe", "pipe", "pipe"] })
}

test("authoring transport serializes calls and preserves split JSONL responses", { timeout: 5000 }, async () => {
  const client = new AuthoringClient(child(`
    let count = 0;
    require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
      const request = JSON.parse(line);
      const response = JSON.stringify({ id: request.id, ok: true, result: { count: ++count, op: request.op } });
      process.stdout.write(response.slice(0, 5));
      setImmediate(() => process.stdout.write(response.slice(5) + '\\n'));
    });
  `))
  try {
    const replies = (await Promise.all([client.request({ op: "first" }), client.request({ op: "second" })])).map(JSON.parse)
    assert.deepEqual(replies.map(r => r.result), [{ count: 1, op: "first" }, { count: 2, op: "second" }])
    assert.notEqual(replies[0].id, replies[1].id)
    await assert.rejects(client.request({ op: "literal", value: Number.MAX_SAFE_INTEGER + 1 }), /exact decimal string/)
  } finally { await client.close() }
})

test("stdout EOF while idle invalidates the session before another request", { timeout: 5000 }, async () => {
  const process = child("process.stdout.end(); process.stdin.resume()")
  const client = new AuthoringClient(process)
  try {
    await once(process.stdout, "end")
    const response = client.request({ op: "status" }).then(() => "resolved", () => "rejected")
    let timer
    try {
      assert.equal(await Promise.race([response, new Promise(resolve => {
        timer = setTimeout(() => resolve("still pending"), 200)
      })]), "rejected")
    } finally { clearTimeout(timer) }
  } finally {
    process.kill()
    await client.close().catch(() => undefined)
  }
})

test("cancelling a sent request rejects its queue without replaying mutations", { timeout: 5000 }, async () => {
  const process = child(`
    require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
      process.stderr.write(JSON.parse(line).op + '\\n');
    });
  `)
  const client = new AuthoringClient(process)
  const controller = new AbortController()
  let sent = ""
  process.stderr.on("data", chunk => { sent += chunk })
  try {
    const received = once(process.stderr, "data")
    const results = Promise.allSettled([client.request({ op: "commit" }, controller.signal), client.request({ op: "status" })])
    await received
    controller.abort()
    assert.ok((await results).every(result => result.status === "rejected"))
    assert.equal(sent, "commit\n")
    await assert.rejects(client.request({ op: "status" }), /reopen and inspect/i)
  } finally {
    process.kill()
    await client.close().catch(() => undefined)
  }
})

test("lost responses reject pending and queued calls without retrying mutations", { timeout: 5000 }, async () => {
  const client = new AuthoringClient(child(`
    require('node:readline').createInterface({ input: process.stdin }).once('line', () => process.exit(0));
  `))
  const results = await Promise.allSettled([client.request({ op: "commit" }), client.request({ op: "status" })])
  assert.ok(results.every(result => result.status === "rejected"))
  assert.match(results[0].reason.message, /closed.*response stream|exited/)
  await client.close().catch(() => undefined)
})

test("healthy close drains a final response even when process exit precedes stdout drainage", { timeout: 5000 }, async t => {
  const runner = child(`
    require('node:readline').createInterface({ input: process.stdin }).once('line', line => {
      process.stdout.write(JSON.stringify({ id: JSON.parse(line).id, ok: true, result: 'committed' }) + '\\n', () => process.exit(0));
    });
  `)
  const client = new AuthoringClient(runner)
  const exited = once(runner, "exit")
  const reaped = new Promise(resolve => runner.once("close", resolve))
  t.after(async () => {
    runner.stdout.resume()
    if (!client.isReaped) runner.kill("SIGKILL")
    await reaped
    await client.close().catch(() => undefined)
  })
  runner.stdout.pause()
  const response = client.request({ op: "commit" })
  const closing = client.close()
  await exited
  assert.equal(client.isReaped, false)
  runner.stdout.resume()
  assert.equal(JSON.parse(await response).result, "committed")
  await closing
  assert.equal(client.isReaped, true)
})

async function controlledChild(t, source) {
  const runner = spawn(process.execPath, ["-e", `${source}\nprocess.send('ready')`], {
    stdio: ["pipe", "pipe", "pipe", "ipc"],
  })
  const messages = []
  const waiters = []
  runner.on("message", message => {
    messages.push(message)
    for (const waiter of waiters.splice(0)) waiter()
  })
  async function received(message) {
    while (!messages.includes(message)) await new Promise(resolve => waiters.push(resolve))
  }
  const closed = new Promise(resolve => runner.once("close", resolve))
  const kill = runner.kill.bind(runner)
  const client = new AuthoringClient(runner)
  t.after(async () => {
    if (!client.isReaped) kill("SIGKILL")
    await closed
    await client.close().catch(() => undefined)
  })
  await received("ready")
  return { runner, client, closed, messages, received }
}

const stubbornChild = `
  process.on('SIGTERM', () => process.send('SIGTERM'));
  process.stdin.on('end', () => process.send('EOF'));
  require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
    process.send(JSON.parse(line).op);
  });
  setInterval(() => {}, 1000);
`

test("healthy close drains in-flight and queued responses before EOF, and concurrent closes join", { timeout: 5000 }, async t => {
  const { runner, client, received, messages } = await controlledChild(t, `
    let held;
    const reply = request => process.stdout.write(JSON.stringify({ id: request.id, ok: true, result: request.op }) + '\\n');
    require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
      const request = JSON.parse(line);
      process.send(request.op);
      if (request.op === 'commit') held = request;
      else reply(request);
    });
    process.on('message', () => reply(held));
    process.stdin.on('end', () => { process.send('EOF'); process.disconnect(); });
  `)
  const responses = Promise.all([client.request({ op: "commit" }), client.request({ op: "status" })])
  await received("commit")
  const closing = client.close()
  assert.equal(client.close(), closing)
  await assert.rejects(client.request({ op: "too late" }), /not sent/)
  runner.send("release response")
  assert.deepEqual((await responses).map(text => JSON.parse(text).result), ["commit", "status"])
  await closing
  assert.equal(client.isReaped, true)
  assert.equal(runner.exitCode, 0)
  assert.deepEqual(messages, ["ready", "commit", "status", "EOF"])
})

test("close bounds a hung request queue, settles all callers, and never replays a sent mutation", { timeout: 5000 }, async t => {
  const { runner, client, received, messages } = await controlledChild(t, stubbornChild)
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const responses = Promise.allSettled([client.request({ op: "commit" }), client.request({ op: "status" })])
  await received("commit")
  const closing = assert.rejects(client.close(), /queue did not drain within 5000ms/)
  t.mock.timers.tick(5000)
  const results = await responses
  assert.ok(results.every(result => result.status === "rejected"))
  for (const result of results) {
    assert.match(result.reason.message, /outcome is unknown/)
    assert.match(result.reason.message, /never replayed/)
  }
  await received("SIGTERM")
  t.mock.timers.tick(5000)
  await closing
  assert.equal(client.isReaped, true)
  assert.equal(runner.signalCode, "SIGKILL")
  assert.equal(messages.filter(message => message === "commit").length, 1)
  assert.ok(!messages.includes("status"))
})

test("close escalates EOF and ignored SIGTERM and waits for the child's close event", { timeout: 5000 }, async t => {
  const { runner, client, received } = await controlledChild(t, stubbornChild)
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const closing = assert.rejects(client.close(), /5000ms after stdin EOF/)
  await received("EOF")
  t.mock.timers.tick(5000)
  await received("SIGTERM")
  assert.equal(client.isReaped, false)
  t.mock.timers.tick(5000)
  await closing
  assert.equal(client.isReaped, true)
  assert.equal(runner.signalCode, "SIGKILL")
})

test("transport failure reaps a SIGTERM-ignoring child even before explicit close", { timeout: 5000 }, async t => {
  const { runner, client, received, closed } = await controlledChild(t, stubbornChild)
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const abort = new AbortController()
  const response = assert.rejects(client.request({ op: "commit" }, abort.signal), /cancelled after sending.*outcome is unknown/)
  await received("commit")
  abort.abort()
  await response
  await received("SIGTERM")
  t.mock.timers.tick(5000)
  await closed
  assert.equal(runner.signalCode, "SIGKILL")
  await assert.rejects(client.close(), /cancelled after sending/)
})

test("an unreaped child keeps its authoring slot even when the SIGKILL wait expires", { timeout: 5000 }, async t => {
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const actualSpawn = childProcess.spawn
  const children = []
  t.mock.method(childProcess, "spawn", () => {
    const stubborn = children.length === 0
    const runner = actualSpawn(process.execPath, ["-e", `
      process.on('SIGTERM', () => process.send('SIGTERM'));
      process.stdin.on('end', () => {
        process.send('EOF');
        if (!${stubborn}) process.disconnect();
      });
      require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
        process.stdout.write(JSON.stringify({ id: JSON.parse(line).id, ok: true }) + '\\n');
      });
      if (${stubborn}) setInterval(() => {}, 1000);
      process.send('ready');
    `], { stdio: ["pipe", "pipe", "pipe", "ipc"] })
    children.push({ runner, ready: once(runner, "message"), closed: new Promise(resolve => runner.once("close", resolve)) })
    return runner
  })
  syncBuiltinESMExports()
  t.after(async () => {
    for (const { runner } of children) {
      if (runner.exitCode === null && runner.signalCode === null) childProcess.ChildProcess.prototype.kill.call(runner, "SIGKILL")
    }
    await Promise.all(children.map(entry => entry.closed))
    await closeAuthoring(context).catch(() => undefined)
    t.mock.restoreAll()
    syncBuiltinESMExports()
  })
  await openAuthoring("create", "unused-fixture.json", context)
  const { runner, ready, closed } = children[0]
  await ready
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const kill = runner.kill.bind(runner)
  let observeKill
  const signalled = new Promise(resolve => { observeKill = resolve })
  t.mock.method(runner, "kill", signal => {
    if (signal === "SIGKILL") { observeKill(); return true }
    return kill(signal)
  })
  const eof = once(runner, "message")
  const closing = assert.rejects(closeAuthoring(context), /5000ms after SIGKILL; child ownership is retained/)
  assert.equal((await eof)[0], "EOF")
  const term = once(runner, "message")
  t.mock.timers.tick(5000)
  await term
  t.mock.timers.tick(5000)
  await signalled
  t.mock.timers.tick(5000)
  await closing
  await assert.rejects(openAuthoring("create", "unused-fixture.json", context), /already open/)
  assert.equal(children.length, 1)
  kill("SIGKILL")
  await closed
  await assert.rejects(closeAuthoring(context), /ownership is retained/)
  await openAuthoring("create", "unused-fixture.json", context)
  assert.equal(children.length, 2)
  await closeAuthoring(context)
})
