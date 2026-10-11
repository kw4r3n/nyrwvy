import test from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, readFile, writeFile, rm } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { execFile } from "node:child_process"
import { promisify } from "node:util"
import { fileURLToPath } from "node:url"
import { loadFixtures, runFixtures } from "../../examples/run-fixtures/client.mjs"

const request = { op: "runPureByName", arguments: { function: "identity", arguments: [{ kind: "string", value: "é e\u0301" }], expected: { kind: "string", value: "é e\u0301" } } }
async function workspace(t) {
  const path = await mkdtemp(join(tmpdir(), "nyrwvy-fixtures-test-"))
  t.after(() => rm(path, { recursive: true, force: true }))
  return path
}
test("saved fixtures keep Unicode and send the ordinary MCP wrapper unchanged, retaining full evidence", async t => {
  const root = await workspace(t), path = join(root, "unicode.json"), transcript = join(root, "transcript.jsonl")
  await writeFile(path, JSON.stringify(request))
  const fixtures = await loadFixtures([path])
  assert.deepEqual(fixtures[0].request, request)
  const full = { ok: true, result: { matches: true, actual: request.arguments.expected, expected: request.arguments.expected, calls: [], detail: "x".repeat(184402) } }
  const response = { content: [{ type: "text", text: JSON.stringify(full) }], structuredContent: full }
  const summaries = []
  const counts = await runFixtures({ async callTool(call) {
    assert.deepEqual(call, { name: "nyrwvy_request", arguments: request }); return response
  } }, fixtures, transcript, line => summaries.push(line))
  assert.deepEqual(counts, { passed: 1, failed: 0, unknown: 0 })
  assert.deepEqual(JSON.parse(await readFile(transcript, "utf8")), { label: "unicode.json", request, response })
  assert.deepEqual(summaries, ["unicode.json: PASSED"])
})
test("mismatches, semantic errors and missing comparison results never count as passes", async t => {
  const root = await workspace(t), transcript = join(root, "transcript.jsonl")
  const replies = [
    { content: [{ type: "text", text: JSON.stringify({ ok: true, result: { matches: false, mismatch: { path: "$.fields.sku" } } }) }] },
    { isError: true, content: [{ type: "text", text: "RunArgumentTypeMismatch" }] },
    { structuredContent: { ok: false, error: { code: "RunBindingNoMatch" } } },
    { structuredContent: { ok: true, result: { actual: {} } } },
  ]
  const counts = await runFixtures({ async callTool() { return replies.shift() } }, Array.from({ length: 4 }, (_, i) => ({ label: String(i), request })), transcript, () => {})
  assert.deepEqual(counts, { passed: 0, failed: 3, unknown: 1 })
  assert.equal((await readFile(transcript, "utf8")).trim().split("\n").length, 4)
})
test("transport interruption is unknown, records the sent request and stops without replay", async t => {
  const root = await workspace(t), transcript = join(root, "transcript.jsonl")
  let calls = 0
  const counts = await runFixtures({ async callTool() { calls++; throw new Error("connection lost") } }, [{ label: "one", request }, { label: "two", request }], transcript, () => {})
  assert.equal(calls, 1)
  assert.deepEqual(counts, { passed: 0, failed: 0, unknown: 1 })
  assert.match(JSON.parse(await readFile(transcript, "utf8")).transportError, /connection lost/)
})
test("only expected-bearing run requests are accepted before any server is started", async t => {
  const root = await workspace(t), path = join(root, "invalid.json")
  for (const invalid of [null, [], { ...request, op: "commit" }, { ...request, id: "manual" },
    { ...request, arguments: {} }, { ...request, arguments: [] },
    ...["hostSources", "id", "op"].map(key => ({ ...request, arguments: { ...request.arguments, [key]: "forbidden" } }))]) {
    await writeFile(path, JSON.stringify(invalid))
    await assert.rejects(loadFixtures([path]), /Invalid fixture/)
  }
  await writeFile(path, "{broken")
  await assert.rejects(loadFixtures([path]), SyntaxError)
  await assert.rejects(loadFixtures([join(root, "absent.json")]), /ENOENT/)
})

test("the real CLI resolves the workspace-owned SDK and crosses MCP before running fixtures", { timeout: 20000 }, async t => {
  const root = await workspace(t), fixture = join(root, "unicode.json"), snapshot = join(root, "workspace.json")
  const log = join(root, "requests.jsonl")
  await writeFile(fixture, JSON.stringify(request))
  await writeFile(snapshot, "unchanged snapshot fixture")
  // Only the compiled semantic process is replaced. The CLI, SDK, stdio MCP
  // server and shared handlers are real, with the repository's npm layout.
  await writeFile(join(root, "dotnet"), `#!${process.execPath}
const fs = require('node:fs');
if (process.argv.includes('--allow-host-sources')) process.exit(9);
require('node:readline').createInterface({input:process.stdin}).on('line', line => {
  const request = JSON.parse(line);
  fs.appendFileSync(${JSON.stringify(log)}, JSON.stringify(request)+'\\n');
  process.stdout.write(JSON.stringify({id:request.id,ok:true,result:request.op==='status'?{}:{matches:true,actual:request.expected,expected:request.expected}})+'\\n');
});
`, { mode: 0o700 })
  const cli = fileURLToPath(new URL("../../examples/run-fixtures/client.mjs", import.meta.url))
  const { stdout } = await promisify(execFile)(process.execPath, [cli, snapshot, fixture], {
    cwd: root, env: { ...process.env, PATH: `${root}:${process.env.PATH}`, NYRWVY_ALLOW_HOST_SOURCES: "1" }, timeout: 15000,
  })
  assert.match(stdout, /unicode.json: PASSED/)
  assert.match(stdout, /1 passed, 0 failed, 0 unknown/)
  const transcript = stdout.match(/^Full requests and responses: (.+)$/m)?.[1]
  assert.ok(transcript)
  t.after(() => rm(join(transcript, ".."), { recursive: true, force: true }))
  const events = (await readFile(transcript, "utf8")).trim().split("\n").map(JSON.parse)
  assert.equal(events[0].response.structuredContent.hostSourcesAllowed, false)
  assert.equal(events[1].response.structuredContent.result.matches, true)
  assert.deepEqual(events[1].request, request)
  const requests = (await readFile(log, "utf8")).trim().split("\n").map(JSON.parse)
  assert.deepEqual(requests.map(item => item.op), ["status", "runPureByName"])
  const { id, op, ...args } = requests[1]
  assert.equal(typeof id, "string")
  assert.deepEqual(args, request.arguments)
  assert.equal(await readFile(snapshot, "utf8"), "unchanged snapshot fixture")
})
