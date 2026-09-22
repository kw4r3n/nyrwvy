import test from "node:test"
import assert from "node:assert/strict"
import { spawn } from "node:child_process"
import { once } from "node:events"
import { AuthoringClient } from "../lib/nyrwvy-authoring.ts"

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
    await assert.rejects(client.request({ op: "status" }), /reopen and inspect/)
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
