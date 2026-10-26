import test from "node:test"
import assert from "node:assert/strict"
import childProcess, { spawn } from "node:child_process"
import fsPromises from "node:fs/promises"
import { syncBuiltinESMExports } from "node:module"
import { randomUUID } from "node:crypto"
import { once } from "node:events"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { AuthoringClient, openAuthoring, closeAuthoring, requestAuthoring } from "../../tools/nyrwvy-agent/authoring.ts"

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

test("typed Float64 values survive transport in arguments, expectations and fake responses without weakening integer guards", { timeout: 5000 }, async () => {
  const client = new AuthoringClient(child(`
    require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
      const request = JSON.parse(line);
      process.stdout.write(JSON.stringify({ id: request.id, ok: true, result: request }) + '\\n');
    });
  `))
  const floating = value => ({ kind: "float64", value })
  const nested = { kind: "product", fields: [{ value: { kind: "list", items: [floating(2 ** 53), floating(2 ** 53 + 2), floating(1e20)] } }] }
  try {
    const request = { op: "runPure", arguments: [nested], expected: nested,
      fake: { responses: [{ kind: "return", value: floating(-0) }, { kind: "return", value: floating("NaN") }] } }
    const result = JSON.parse(await client.request(request)).result
    assert.deepEqual(result.arguments, [nested])
    assert.deepEqual(result.expected, nested)
    assert.equal(result.fake.responses[0].value.value, "-0")
    assert.equal(result.fake.responses[1].value.value, "NaN")
    for (const value of [NaN, Infinity, -Infinity]) {
      await assert.rejects(client.request({ op: "runPure", arguments: [floating(value)] }), /Non-finite Float64/)
    }
    for (const value of [{ kind: "int64", value: 2 ** 53 }, { kind: "bits32", value: 2 ** 53 },
      { kind: "float64", unrelated: 2 ** 53 }, { value: 2 ** 53 }]) {
      await assert.rejects(client.request({ op: "runPure", arguments: [value] }), /exact decimal string/)
    }
    const integer = { kind: "int64", value: "9007199254740993" }
    assert.deepEqual(JSON.parse(await client.request({ op: "runPure", arguments: [integer] })).result.arguments, [integer])
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

test("a missing workspace directory is reported before anything is launched", { timeout: 5000 }, async () => {
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const missing = join(tmpdir(), `nyrwvy-missing-${randomUUID()}`)
  await assert.rejects(openAuthoring("create", "workspace.json", context, missing), error =>
    error.message === `workspace directory does not exist: ${missing}`)
  await assert.rejects(openAuthoring("create", "workspace.json", { ...context, worktree: missing }), /workspace directory does not exist/)
  assert.equal(await closeAuthoring(context), "No active Nyrwvy authoring session.")
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

// Answers each request as `reply` says (nothing when it returns undefined) and reports its op.
const replyingChild = reply => `
  require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
    const request = JSON.parse(line);
    process.send(request.op);
    const text = (${reply})(request);
    if (text !== undefined) process.stdout.write(text);
  });
`

test("a reply line longer than the limit fails the request before it is parsed", { timeout: 20000 }, async t => {
  const { client, closed } = await controlledChild(t, replyingChild(`() => Buffer.alloc(64 * 1024 * 1024 + 1, 120)`))
  await assert.rejects(client.request({ op: "status" }), /reply line longer than 67108864 bytes.*outcome is unknown/)
  await closed
})

test("a reply for another request id fails the transport", { timeout: 5000 }, async t => {
  const { client, closed } = await controlledChild(t, replyingChild(`() => JSON.stringify({ id: "other", ok: true }) + "\\n"`))
  await assert.rejects(client.request({ op: "status" }), /Unexpected AuthoringSession response.*outcome is unknown/)
  await closed
})

test("a request without an answer fails after the request timeout and stops the child", { timeout: 5000 }, async t => {
  const { client, received, closed } = await controlledChild(t, replyingChild(`() => undefined`))
  t.mock.timers.enable({ apis: ["setTimeout"] })
  const response = assert.rejects(client.request({ op: "commit" }), /did not answer within 600 s.*outcome is unknown/)
  await received("commit")
  t.mock.timers.tick(600_000)
  await response
  await closed
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

function mockRespondingSpawn(t, { silentFirst = false, onSpawn = () => {} } = {}) {
  const actualSpawn = childProcess.spawn
  const children = []
  t.mock.method(childProcess, "spawn", () => {
    const silent = silentFirst && children.length === 0
    const runner = actualSpawn(process.execPath, ["-e", `
      if (${silent}) {
        process.on('SIGTERM', () => {});
        setInterval(() => {}, 1000);
      }
      require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
        const request = JSON.parse(line);
        if (${silent}) process.stderr.write(request.op + '\\n');
        else process.stdout.write(JSON.stringify({ id: request.id, ok: true }) + '\\n');
      });
    `], { stdio: ["pipe", "pipe", "pipe"] })
    const entry = { runner, requested: silent ? once(runner.stderr, "data") : undefined,
      closed: new Promise(resolve => runner.once("close", resolve)) }
    children.push(entry)
    onSpawn(entry)
    return runner
  })
  syncBuiltinESMExports()
  t.after(async () => {
    for (const { runner } of children) {
      if (runner.exitCode === null && runner.signalCode === null) childProcess.ChildProcess.prototype.kill.call(runner, "SIGKILL")
    }
    await Promise.all(children.map(entry => entry.closed))
    t.mock.restoreAll()
    syncBuiltinESMExports()
  })
  return children
}

test("concurrent opens of one session start at most one child", { timeout: 5000 }, async t => {
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const children = mockRespondingSpawn(t)
  const results = await Promise.allSettled([
    openAuthoring("create", "first.json", context),
    openAuthoring("create", "second.json", context),
  ])
  assert.deepEqual(results.map(result => result.status).sort(), ["fulfilled", "rejected"])
  assert.match(results.find(result => result.status === "rejected").reason.message, /already open/)
  assert.equal(children.length, 1)
  assert.match(await closeAuthoring(context), /closed/)
  await Promise.all(children.map(entry => entry.closed))
})

test("a close racing an open closes the child that open starts", { timeout: 5000 }, async t => {
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const children = mockRespondingSpawn(t)
  const [opened, closed] = await Promise.allSettled([
    openAuthoring("create", "first.json", context),
    closeAuthoring(context),
  ])
  assert.equal(opened.status, "fulfilled")
  assert.match(closed.value, /closed/)
  assert.equal(children.length, 1)
  await Promise.all(children.map(entry => entry.closed))
  assert.equal(await closeAuthoring(context), "No active Nyrwvy authoring session.")
})

test("close bounds a silent initial status, settles open and close, and permits reopening", { timeout: 20000 }, async t => {
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  let observeSpawn
  const spawned = new Promise(resolve => { observeSpawn = resolve })
  const children = mockRespondingSpawn(t, { silentFirst: true, onSpawn: observeSpawn })
  const opened = Promise.allSettled([openAuthoring("create", "first.json", context)])
  const first = await spawned
  assert.equal((await first.requested)[0].toString(), "status\n")
  const closing = closeAuthoring(context)
  const closed = Promise.allSettled([closing, closeAuthoring(context)])
  // Reopen from close's own completion, without waiting for the old open's cleanup.
  const reopened = closing.then(() => assert.fail("Silent close must fail"), async error => {
    assert.match(error.message, /queue did not drain within 5000ms/)
    const replacement = openAuthoring("create", "second.json", context)
    // Let the old open's finally run while the replacement is still starting.
    await Promise.resolve()
    await assert.rejects(openAuthoring("create", "duplicate-new.json", context), /already open/)
    await replacement
  })
  const reopenedResult = Promise.allSettled([reopened])
  await assert.rejects(openAuthoring("create", "duplicate.json", context), /already open/)
  for (const result of [...await opened, ...await closed]) {
    assert.equal(result.status, "rejected")
    assert.match(result.reason.message, /queue did not drain within 5000ms/)
    assert.match(result.reason.message, /never replayed/)
  }
  await first.closed
  assert.equal(first.runner.signalCode, "SIGKILL")
  assert.equal((await reopenedResult)[0].status, "fulfilled")
  assert.equal(children.length, 2)
  assert.equal(JSON.parse(await requestAuthoring({ op: "status" }, context)).ok, true)
  assert.match(await closeAuthoring(context), /closed/)
  await children[1].closed
  assert.equal(await closeAuthoring(context), "No active Nyrwvy authoring session.")
})

test("a close before spawn waits for the owned child and keeps duplicate opens out", { timeout: 5000 }, async t => {
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const actualStat = fsPromises.stat
  let releaseStat, observeStat, results
  const gate = new Promise(resolve => { releaseStat = resolve })
  const inspecting = new Promise(resolve => { observeStat = resolve })
  // Release a failed test's startup wait before the spawn helper reaps its children.
  t.after(async () => { releaseStat(); await results })
  const children = mockRespondingSpawn(t)
  t.mock.method(fsPromises, "stat", async (...args) => {
    observeStat()
    await gate
    return actualStat(...args)
  })
  syncBuiltinESMExports()
  const opened = openAuthoring("create", "first.json", context)
  await inspecting
  let closeFinished = false
  const closed = closeAuthoring(context).then(value => { closeFinished = true; return value })
  results = Promise.allSettled([opened, closed])
  await assert.rejects(openAuthoring("create", "duplicate.json", context), /already open/)
  assert.equal(children.length, 0)
  assert.equal(closeFinished, false)
  releaseStat()
  const [openResult, closeResult] = await results
  assert.equal(openResult.status, "fulfilled")
  assert.equal(closeResult.status, "fulfilled")
  assert.match(closeResult.value, /closed/)
  assert.equal(children.length, 1)
  await children[0].closed
  assert.equal(await closeAuthoring(context), "No active Nyrwvy authoring session.")
})

test("a close racing a failure before spawn releases the reservation for reopening", { timeout: 5000 }, async t => {
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const children = mockRespondingSpawn(t)
  const [opened, closed] = await Promise.allSettled([
    openAuthoring("create", "unused.json", context, join(tmpdir(), "missing-authoring-" + randomUUID())),
    closeAuthoring(context),
  ])
  assert.equal(opened.status, "rejected")
  assert.match(opened.reason.message, /workspace directory does not exist/)
  assert.equal(closed.status, "fulfilled")
  assert.equal(closed.value, "No active Nyrwvy authoring session.")
  assert.equal(children.length, 0)
  await openAuthoring("create", "second.json", context)
  assert.equal(children.length, 1)
  assert.match(await closeAuthoring(context), /closed/)
})
