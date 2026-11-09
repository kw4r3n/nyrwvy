// Runs core.json's functions through a program that imports it, on the Zig and C# backends, and compares
// the replies with the expected values. Usage: node libraries/core/check/run.mjs [<host executable>]
import { spawn } from "node:child_process"
import { copyFileSync, mkdtempSync, readFileSync, rmSync } from "node:fs"
import { hostExchange } from "../../host-client.mjs"
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
const exchange = hostExchange(cli)
const raw = async request => {
  return exchange(request)
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

const float64 = { kind: "primitive", primitive: "float64" }
const optionOf = item => ({ kind: "builtinGeneric", generic: "option", arguments: [item] })
// A wrapper with the same parameter names as the core function.
const through = async (name, callee, parameters, resultType) =>
  define(name, parameters, resultType, call(callee, Object.fromEntries(Object.keys(parameters).map(key => [key, P(key)]))))
const textPair = { text: string, separator: string }
await through("notEmpty", "isNotEmpty", { text: string }, bool)
await through("blank", "isBlank", { text: string }, bool)
await through("orEmpty", "orIfEmpty", { text: string, fallback: string }, string)
await through("inInts", "int64InRange", { value: int64, low: int64, high: int64 }, bool)
await through("inFloats", "float64InRange", { value: float64, low: float64, high: float64 }, bool)
await through("clampInt", "int64Clamp", { value: int64, low: int64, high: int64 }, int64)
await through("clampFloat", "float64Clamp", { value: float64, low: float64, high: float64 }, float64)
await through("split", "splitTrimmed", textPair, list(string))
await through("strip", "stripPrefix", { text: string, prefix: string }, string)
await through("before", "beforeFirst", textPair, string)
await through("after", "afterFirst", textPair, string)
await through("parseOr", "parseInt64Or", { text: string, fallback: int64 }, int64)
await through("padStart", "stringPadStart", { text: string, width: int64, fill: string }, string)
await through("padEnd", "stringPadEnd", { text: string, width: int64, fill: string }, string)
await through("lacks", "textsLack", { items: list(string), item: string }, bool)
await define("fallbackList", { value: string }, string, { kind: "stringJoin", separator: { kind: "stringLiteral", value: "," }, list: call("splitTrimmed", { text: P("value"), separator: { kind: "stringLiteral", value: "," } }) })
await define("isBig", { item: int64 }, bool, { kind: "int64LessThan", left: { kind: "int64Literal", value: "2" }, right: P("item") })
await define("findBig", { items: list(int64) }, optionOf(int64), call("find", { items: P("items"), predicate: { kind: "functionReference", function: "isBig" } }))
await define("noneOf", { items: list(int64) }, bool, call("optionIsNone", { option: { kind: "listItemAt", list: P("items"), index: { kind: "int64Literal", value: "0" } } }))
await define("elseOf", { items: list(int64), others: list(int64) }, optionOf(int64), call("optionOrElse", {
  option: { kind: "listItemAt", list: P("items"), index: { kind: "int64Literal", value: "0" } }, other: { kind: "listItemAt", list: P("others"), index: { kind: "int64Literal", value: "0" } } }))
await define("errOf", { items: list(int64) }, bool, call("resultIsErr", { result: { kind: "optionOkOr", option: { kind: "listItemAt", list: P("items"), index: { kind: "int64Literal", value: "0" } }, error: { kind: "stringLiteral", value: "none" } } }))

// A call is 1 node plus its operands; the inline chain of n strings is 2n-1.
const nodes = name => JSON.parse(readFileSync(join(work, "check.json"), "utf8")).functions.find(f => f.displayName === name).body.nodes.length
assert.equal(nodes("join10"), 11)

const cases = [
  ["join10", letters, "abcdefghij"], ["join3", ["日本", "x"], "日本x!"],
  ["emptyInts", [[]], true], ["emptyInts", [[1]], false], ["emptyTexts", [[]], true], ["emptyTexts", [["a"]], false],
  ["intOr", [[5, 6], 1], 6], ["intOr", [[5, 6], 2], -1], ["intOr", [[5], -1], -1],
  ["textOr", [["a", "b"], 0], "a"], ["textOr", [[], 0], "none"],
  ["notEmpty", [""], false], ["notEmpty", [" "], true], ["blank", [" \t\n"], true], ["blank", [""], true], ["blank", [" a "], false],
  ["orEmpty", ["", "x"], "x"], ["orEmpty", [" ", "x"], " "],
  ["inInts", [1, 1, 3], true], ["inInts", [3, 1, 3], true], ["inInts", [0, 1, 3], false], ["inInts", [4, 1, 3], false],
  ["inFloats", [4, 4, 200], true], ["inFloats", [200, 4, 200], true], ["inFloats", [3.5, 4, 200], false], ["inFloats", [200.5, 4, 200], false],
  ["clampInt", [5, 1, 3], 3], ["clampInt", [0, 1, 3], 1], ["clampInt", [2, 1, 3], 2], ["clampFloat", [2.5, 1, 2], 2], ["clampFloat", [0.5, 1, 2], 1],
  ["split", [" a, ,b ,", ","], ["a", "b"]], ["split", ["", ","], []], ["split", ["日本 | 語", "|"], ["日本", "語"]], ["fallbackList", [" Noto , ,Emoji "], "Noto,Emoji"], ["fallbackList", [""], ""],
  ["strip", ["#fff", "#"], "fff"], ["strip", ["fff", "#"], "fff"], ["strip", ["日本語", "日本"], "語"], ["strip", ["a", ""], "a"],
  ["before", ["k=v=w", "="], "k"], ["before", ["k", "="], "k"], ["after", ["k=v=w", "="], "v=w"], ["after", ["k", "="], ""], ["after", ["日本::語", "::"], "語"], ["before", ["k", ""], ""], ["after", ["k", ""], "k"],
  ["parseOr", ["12", 0], 12], ["parseOr", ["-3", 0], -3], ["parseOr", ["x", 7], 7], ["parseOr", [" 1", 7], 7],
  ["padStart", ["7", 3, "0"], "007"], ["padStart", ["1234", 3, "0"], "1234"], ["padStart", ["語", 2, " "], " 語"], ["padEnd", ["7", 3, "."], "7.."], ["padEnd", ["7", -9007199254740991, "."], "7"],
  ["lacks", [["a", "b"], "c"], true], ["lacks", [["a", "b"], "b"], false],
  ["findBig", [[1, 2, 3, 4]], 3], ["findBig", [[1]], null], ["findBig", [[]], null],
  ["noneOf", [[]], true], ["noneOf", [[1]], false], ["elseOf", [[1], [2]], 1], ["elseOf", [[], [2]], 2], ["elseOf", [[], []], null], ["errOf", [[]], true], ["errOf", [[1]], false],
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
