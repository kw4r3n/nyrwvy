// Runs core.json's functions through a program that imports it, on the Zig and C# backends, and compares
// the replies with the expected values. Usage: node libraries/core/check/run.mjs [<host executable>]
import { spawn } from "node:child_process"
import { copyFileSync, mkdtempSync, readFileSync, rmSync } from "node:fs"
import { createInterface } from "node:readline"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import assert from "node:assert/strict"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-core-check-"))
copyFileSync(join(here, "../core.json"), join(work, "core.json"))
const cli = spawn(executable, ["create", "--snapshot", join(work, "check.json"), "--dependency", join(work, "core.json")], { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const lines = createInterface({ input: cli.stdout })[Symbol.asyncIterator]()
let n = 0
const raw = async request => {
  cli.stdin.write(JSON.stringify({ id: String(n++), ...request }) + "\n")
  return JSON.parse((await lines.next()).value)
}
const send = async request => {
  const reply = await raw(request)
  if (!reply.ok) throw new Error(`${request.op}: ${JSON.stringify(reply.error)}`)
  return reply.result
}
const string = { kind: "primitive", primitive: "string" }
const int64 = { kind: "primitive", primitive: "int64" }
const bool = { kind: "primitive", primitive: "bool" }
const list = item => ({ kind: "builtinGeneric", generic: "list", arguments: [item] })
const P = name => ({ kind: "parameter", name })
const define = async (displayName, parameters, resultType, body) => {
  const created = await send({ op: "simulateCreateFunctionByName", displayName, exported: true, requiredEffects: [], resultType, body,
    parameters: Object.entries(parameters).map(([name, type]) => ({ displayName: name, type })) })
  await send({ op: "commit", proposalId: created.proposalId })
}
const call = (name, args) => ({ kind: "directCall", function: name, arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const letters = "abcdefghij".split("")

await define("join10", Object.fromEntries(letters.map(name => [name, string])), string, { ...call("concat10", {}), keepUnlisted: true })
await define("join3", { a: string, b: string }, string, { ...call("concat3", { c: { kind: "stringLiteral", value: "!" } }), keepUnlisted: true })
await define("emptyInts", { items: list(int64) }, bool, call("listIsEmpty", { items: P("items") }))
await define("emptyTexts", { items: list(string) }, bool, call("listIsEmpty", { items: P("items") }))
await define("intOr", { items: list(int64), index: int64 }, int64, { ...call("itemOr", { fallback: { kind: "int64Literal", value: "-1" } }), keepUnlisted: true })
await define("textOr", { items: list(string), index: int64 }, string, { ...call("itemOr", { fallback: { kind: "stringLiteral", value: "none" } }), keepUnlisted: true })

// A call is 1 node plus its operands; the inline chain of n strings is 2n-1.
const nodes = name => JSON.parse(readFileSync(join(work, "check.json"), "utf8")).functions.find(f => f.displayName === name).body.nodes.length
assert.equal(nodes("join10"), 11)

const cases = [
  ["join10", letters, "abcdefghij"], ["join3", ["日本", "x"], "日本x!"],
  ["emptyInts", [[]], true], ["emptyInts", [[1]], false], ["emptyTexts", [[]], true], ["emptyTexts", [["a"]], false],
  ["intOr", [[5, 6], 1], 6], ["intOr", [[5, 6], 2], -1], ["intOr", [[5], -1], -1],
  ["textOr", [["a", "b"], 0], "a"], ["textOr", [[], 0], "none"],
]
const plain = value => value?.value !== undefined ? value.value : value
let failed = 0
for (const backend of ["zig", "csharp"]) for (const [functionName, args, expected] of cases) {
  const result = await send({ op: "runPureByName", function: functionName, backend, arguments: args, expected })
  if (result.matches !== true) {
    failed++
    console.error(`${backend} ${functionName}(${JSON.stringify(args)}): ${JSON.stringify(plain(result.actual))}, expected ${JSON.stringify(expected)}`)
  }
}
// A name the caller lacks is an error, not a silent default.
const missing = await raw({ op: "simulateCreateFunctionByName", displayName: "lacking", requiredEffects: [], resultType: string,
  parameters: [{ displayName: "a", type: string }], body: { ...call("concat3", {}), keepUnlisted: true } })
assert.equal(missing.ok, false)
assert.match(missing.error.message, /keepUnlisted: the caller has no parameter 'b' to pass to 'concat3'/)
// A same-named parameter of another type fails the ordinary type check.
const mistyped = await raw({ op: "simulateCreateFunctionByName", displayName: "mistyped", requiredEffects: [], resultType: string,
  parameters: [{ displayName: "a", type: int64 }, { displayName: "b", type: string }, { displayName: "c", type: string }], body: { ...call("concat3", {}), keepUnlisted: true } })
assert.equal(mistyped.ok, false)
assert.equal(mistyped.error.details.diagnostics[0].code, "PureExpressionCallArgumentTypeMismatch")
cli.stdin.end()
rmSync(work, { recursive: true, force: true })
if (failed) process.exit(1)
console.log(`${cases.length * 2} calls agree`)
