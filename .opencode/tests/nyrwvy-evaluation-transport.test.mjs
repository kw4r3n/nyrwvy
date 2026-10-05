import test from "node:test"
import assert from "node:assert/strict"
import childProcess from "node:child_process"
import { syncBuiltinESMExports } from "node:module"
import { randomUUID } from "node:crypto"
import { once } from "node:events"
import { writeFileSync } from "node:fs"
import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { createPersistentEvaluationRuntime, validateJsonObjectLine } from "../lib/evaluation-jsonl-runtime.ts"

// The shared runtime behind the frozen structural and ListMap evaluation bridges. Keep the real
// pipe/error events, but replace dotnet and fixture setup with small Node children.
async function evaluation(t, source) {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-eval-transport-"))
  t.mock.method(console, "error", () => undefined)
  const context = { sessionKey: randomUUID(), worktree: root }
  const spawn = childProcess.spawn
  const children = []
  let runner, ready, api
  const mocked = t.mock.method(childProcess, "spawn", (command, args) => {
    const output = args.indexOf("--output")
    if (output !== -1) writeFileSync(args[output + 1], "{}")
    const isRunner = command === "dotnet" && output === -1
    const child = spawn(process.execPath, ["-e", isRunner ? source : "process.stdout.write('test-revision')"], {
      stdio: ["pipe", "pipe", "pipe"],
    })
    children.push({ child, closed: new Promise(resolve => child.once("close", resolve)) })
    if (isRunner) {
      runner = child
      ready = once(child.stderr, "data")
    }
    return child
  })
  syncBuiltinESMExports()
  t.after(async () => {
    // Also settle a missing-response waiter when running this regression against the old implementation.
    runner?.emit("error", new Error("test cleanup"))
    for (const { child } of children) {
      if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL")
    }
    await Promise.all(children.map(entry => entry.closed))
    await api?.finalize().catch(() => undefined)
    mocked.mock.restore()
    syncBuiltinESMExports()
    await rm(root, { recursive: true, force: true })
  })

  const runtime = createPersistentEvaluationRuntime({
    component: "transport-test", protocolVersion: "test", expectedCorpusRevision: "test-revision",
    frozenInputDiffArguments: () => [], resolveArtifactRoot: () => join(root, "artifacts"),
    configuredFixture: () => "test", configuredTaskID: () => "test", runnerProject: "test-runner",
    stalePerturbationArguments: () => [], metadataArguments: () => [],
    validateRequestLine: value => validateJsonObjectLine(value, "request"),
    messages: { alreadyFinalized: "finalized", alreadyActiveOrStarting: "active", requestNoActive: "no session",
      finalizeNoActive: "no session", stillStarting: "starting", unavailable: "unavailable", startFailure: "start failed",
      requestTimeout: "timeout", runnerFailure: "runner failed", finalizationFailed: "finalize failed",
      finalized: "finalized", started: () => "started" },
  })
  api = { start: () => runtime.start(context), request: text => runtime.request(text, context), finalize: () => runtime.finalize(context) }
  await api.start()
  await ready
  return { ...api, runner }
}

test("shared runtime: broken stdin rejects the request instead of crashing the host", { timeout: 10000 }, async t => {
  const api = await evaluation(t, `
    require('node:fs').closeSync(0);
    process.stderr.write('ready');
    setInterval(() => {}, 1000);
  `)
  assert.ok(api.runner.stdin.listenerCount("error") > 0, "stdin needs an error handler as well as a write callback")
  await assert.rejects(api.request('{"op":"status","command":"state"}'))
  await assert.rejects(api.request('{"op":"status","command":"state"}'))
})

test("shared runtime: the final complete response is drained before exit", { timeout: 10000 }, async t => {
  const api = await evaluation(t, `
    require('node:readline').createInterface({ input: process.stdin }).once('line', () => {
      process.stdout.write(JSON.stringify({ result: 'final response' }) + '\\n', () => process.exit(0));
    });
    process.stderr.write('ready');
  `)
  // Child exit can precede stdout drainage; hold the pipe until the exit event to exercise that ordering.
  api.runner.stdout.pause()
  const exited = once(api.runner, "exit")
  const response = api.request('{"op":"status","command":"state"}').then(text => ({ text }), error => ({ error }))
  await exited
  api.runner.stdout.resume()
  const result = await response
  assert.ifError(result.error)
  assert.equal(JSON.parse(result.text).result, "final response")
})

test("shared runtime: exit zero without a response rejects promptly", { timeout: 10000 }, async t => {
  const api = await evaluation(t, `
    require('node:readline').createInterface({ input: process.stdin }).once('line', () => process.exit(0));
    process.stderr.write('ready');
  `)
  const response = api.request('{"op":"status","command":"state"}').then(() => "resolved", () => "rejected")
  let timer
  try {
    assert.equal(await Promise.race([response, new Promise(resolve => {
      timer = setTimeout(() => resolve("still pending"), 1000)
    })]), "rejected")
  } finally { clearTimeout(timer) }
})
