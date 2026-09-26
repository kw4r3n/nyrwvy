// A deterministic example using the same tool handlers as OpenCode and MCP.
// No graph IDs, snapshots or generated source are authored by this script.
import assert from "node:assert/strict"
import { appendFile, mkdir, mkdtemp } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve, join } from "node:path"
import { fileURLToPath } from "node:url"
import * as z from "zod/v4"
import { createGraphTools } from "../../tools/nyrwvy-agent/graph-tools.mjs"

const repositoryRoot = fileURLToPath(new URL("../../", import.meta.url))
const output = process.argv[2] ? resolve(process.argv[2]) : await mkdtemp(join(tmpdir(), "nyrwvy-abort-example-"))
await mkdir(output, { recursive: true })
const snapshot = join(output, "workspace.json")
const transcript = join(output, "transcript.jsonl")
const context = { sessionID: `abort-example-${process.pid}`, worktree: repositoryRoot }
const tools = createGraphTools(z)
async function call(name, args = {}) {
  const raw = await tools[name].execute(args, context)
  await appendFile(transcript, JSON.stringify({ tool: `nyrwvy_${name}`, arguments: args, response: raw }) + "\n")
  if (name === "close") return raw
  const response = JSON.parse(raw)
  if (response.ok === false) throw new Error(JSON.stringify(response.error))
  return response.result ?? response
}
async function publish(proposal) {
  assert.ok(proposal.proposalId)
  assert.equal(proposal.review.readiness.kind, "semanticallyReady")
  await call("commit", { proposalId: proposal.proposalId })
}
const primitive = primitive => ({ kind: "primitive", primitive })
const string = primitive("string"), unit = primitive("unit")
const result = ok => ({ kind: "builtinGeneric", generic: "result", arguments: [ok, string] })
const filesType = { kind: "capability", name: "Files" }
const param = name => ({ kind: "parameter", name })
const literal = value => ({ kind: "stringLiteral", value })
const pattern = (slot, scopeDepth = 0) => ({ kind: "patternReference", slot, scopeDepth })
const invoke = (operation, value) => ({ kind: "capabilityInvoke", capabilityValue: param("files"), capability: "Files", operation, arguments: [{ parameter: "value", value }] })
const parameter = (displayName, type) => ({ displayName, type })
try {
  await call("open", { mode: "create", snapshot })
  assert.equal((await call("recipe_reference", { kind: "abort" })).shape.kind, "abort")
  await publish(await call("request", { op: "simulateCreateEffect", arguments: { displayName: "IO" } }))
  const effect = (await call("inspect", { view: "declarations" })).effects.find(e => e.displayName === "IO")
  await publish(await call("request", { op: "simulateCreateCapability", arguments: {
    displayName: "Files",
    operations: [["open", result(string)], ["read", result(string)], ["close", result(unit)], ["audit", unit]].map(([displayName, resultType]) => ({
      displayName, parameters: [parameter("value", string)], resultType, effectId: effect.id,
    })),
  } }))
  const body = await call("create_function", {
    displayName: "readBody", parameters: [parameter("files", filesType), parameter("handle", string)], resultType: result(string), requiredEffects: ["IO"],
    body: { kind: "bind", value: {
      kind: "matchResult", input: invoke("read", param("handle")), okBody: pattern("value"), errBody: { kind: "abort", error: pattern("error") },
    }, body: { kind: "bind", value: invoke("audit", literal("read succeeded")), body: { kind: "resultOk", errorType: string, value: pattern("value", 1) } } },
  })
  assert.equal(body.review.aborts.length, 1)
  assert.ok(body.review.aborts[0].dependentContinuationNodeIds.length)
  await publish(body)
  await publish(await call("create_function", {
    displayName: "readValidated", parameters: [parameter("path", string), parameter("files", filesType)], resultType: result(string), requiredEffects: ["IO"],
    body: { kind: "bind", value: { kind: "conditional", condition: { kind: "stringIsEmpty", value: param("path") },
      whenTrue: { kind: "abort", error: literal("empty path") }, whenFalse: { kind: "unitLiteral" },
    }, body: { kind: "useResource", acquire: invoke("open", param("path")), releaseCapability: param("files"), capability: "Files", releaseOperation: "close", body: "readBody", context: [{ parameter: "files", value: param("files") }] } },
  }))
  const before = await call("inspect", { view: "function", function: "readValidated" })
  await call("close")
  await call("open", { mode: "open", snapshot })
  const after = await call("inspect", { view: "function", function: "readValidated" })
  assert.equal(before.function.id, after.function.id)
  assert.equal(before.function.body.resultNodeId, after.function.body.resultNodeId)
  const cap = (await call("inspect", { view: "declarations" })).capabilities.find(c => c.displayName === "Files")
  const returned = value => ({ kind: "return", value })
  const textResult = (isOk, value) => ({ kind: "result", okKind: "string", errorKind: "string", isOk, value })
  for (const [name, path, readOk, expectedValue, expectedCalls] of [
    ["invalid input", "", false, "empty path", []],
    ["read failure", "notes.txt", false, "read failed", ["open", "read", "close"]],
    ["success", "notes.txt", true, "contents", ["open", "read", "audit", "close"]],
  ]) {
    const responses = {
      open: [returned(textResult(true, "handle"))], read: [returned(textResult(readOk, readOk ? "contents" : "read failed"))],
      audit: [returned({ kind: "unit" })], close: [returned({ kind: "result", okType: unit, errorType: string, isOk: true, value: { kind: "unit" } })],
    }
    const outcome = await call("request", { op: "runPure", arguments: {
      functionId: after.function.id,
      arguments: [{ kind: "string", value: path }, { kind: "fakeCapability", capabilityId: cap.id, operations: cap.operations.map(op => ({ operationId: op.id, responses: responses[op.displayName] })) }],
      expected: textResult(readOk, expectedValue),
    } })
    assert.equal(outcome.matches, true)
    const actualCalls = outcome.calls.map(call => cap.operations.find(op => op.id === call.operationId).displayName)
    assert.deepEqual(actualCalls, expectedCalls)
    console.log(`${name}: ${readOk ? "Ok" : "Err"}(${expectedValue}); calls=${actualCalls.join(" -> ") || "none"}`)
  }
  console.log(`Snapshot and ordinary-tool transcript: ${output}`)
} finally {
  await call("close")
}
