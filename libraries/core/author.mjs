// Writes core.json: small functions whose calls take fewer nodes than the shapes they replace. Each uses
// existing nodes only. Usage: node libraries/core/author.mjs [<host executable>]; run it again after a change.
import { spawn } from "node:child_process"
import { rmSync } from "node:fs"
import { hostExchange } from "../host-client.mjs"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const executable = process.argv[2] ?? resolve(here, "../../tools/zig/host/bin/nyrwvy-host")
const snapshot = join(here, "core.json")
rmSync(snapshot, { force: true })
rmSync(snapshot + ".backends.json", { force: true })
const cli = spawn(executable, ["create", "--snapshot", snapshot], { stdio: ["pipe", "pipe", "inherit"], cwd: resolve(here, "../..") })
const exchange = hostExchange(cli)
const send = async request => {
  const reply = await exchange(request)
  if (!reply.ok) throw new Error(`${request.op}: ${JSON.stringify(reply.error)}`)
  return reply.result
}
const define = async (displayName, typeParameters, parameters, resultType, body) => {
  const created = await send({ op: "simulateCreateFunctionByName", displayName, exported: true, typeParameters, requiredEffects: [], resultType, body,
    parameters: Object.entries(parameters).map(([name, type]) => ({ displayName: name, type })) })
  await send({ op: "commit", proposalId: created.proposalId })
}
const string = { kind: "primitive", primitive: "string" }
const int64 = { kind: "primitive", primitive: "int64" }
const bool = { kind: "primitive", primitive: "bool" }
const T = { kind: "typeParameter", name: "T" }
const items = { kind: "builtinGeneric", generic: "list", arguments: [T] }
const P = name => ({ kind: "parameter", name })

// The longest chain in the measured programs joins 10 strings.
for (let count = 3; count <= 10; count++) {
  const names = Array.from({ length: count }, (_, i) => String.fromCharCode(97 + i))
  await define(`concat${count}`, [], Object.fromEntries(names.map(name => [name, string])), string,
    names.slice(1).reduce((left, name) => ({ kind: "stringConcat", left, right: P(name) }), P(names[0])))
}
await define("listIsEmpty", ["T"], { items }, bool, { kind: "valueEqual", left: { kind: "listLength", value: P("items") }, right: { kind: "int64Literal", value: "0" } })
await define("itemOr", ["T"], { items, index: int64, fallback: T }, T,
  { kind: "optionGetOrDefault", option: { kind: "listItemAt", list: P("items"), index: P("index") }, default: P("fallback") })

const float64 = { kind: "primitive", primitive: "float64" }
const E = { kind: "typeParameter", name: "E" }
const optionOf = item => ({ kind: "builtinGeneric", generic: "option", arguments: [item] })
const texts = { kind: "builtinGeneric", generic: "list", arguments: [string] }
const not = value => ({ kind: "boolNot", value })
const atMost = (left, right, less) => not({ kind: less, left: right, right: left })
const length = value => ({ kind: "stringLength", value })
const firstAt = { kind: "patternReference", scopeDepth: 0, slot: "value" }
const firstOf = (body, otherwise) => ({ kind: "matchOption", input: { kind: "stringIndexOf", value: P("text"), substring: P("separator"), from: { kind: "int64Literal", value: "0" } }, someBody: body, noneBody: otherwise })
const pad = { kind: "stringRepeat", text: P("fill"), count: { kind: "int64WrappingSubtract", left: { kind: "int64Max", left: P("width"), right: { kind: "int64Literal", value: "0" } }, right: length(P("text")) } }

await define("isNotEmpty", [], { text: string }, bool, not({ kind: "stringIsEmpty", value: P("text") }))
await define("isBlank", [], { text: string }, bool, { kind: "stringIsEmpty", value: { kind: "stringTrim", value: P("text") } })
await define("orIfEmpty", [], { text: string, fallback: string }, string, { kind: "conditional", condition: { kind: "stringIsEmpty", value: P("text") }, whenTrue: P("fallback"), whenFalse: P("text") })
await define("int64InRange", [], { value: int64, low: int64, high: int64 }, bool, { kind: "boolAnd", left: atMost(P("low"), P("value"), "int64LessThan"), right: atMost(P("value"), P("high"), "int64LessThan") })
// False for NaN: every comparison with it is false, so test directly instead of under boolNot.
await define("float64InRange", [], { value: float64, low: float64, high: float64 }, bool, { kind: "boolAnd",
  left: { kind: "boolOr", left: { kind: "float64LessThan", left: P("low"), right: P("value") }, right: { kind: "float64Equal", left: P("low"), right: P("value") } },
  right: { kind: "boolOr", left: { kind: "float64LessThan", left: P("value"), right: P("high") }, right: { kind: "float64Equal", left: P("value"), right: P("high") } } })
await define("int64Clamp", [], { value: int64, low: int64, high: int64 }, int64, { kind: "int64Min", left: { kind: "int64Max", left: P("value"), right: P("low") }, right: P("high") })
await define("float64Clamp", [], { value: float64, low: float64, high: float64 }, float64, { kind: "float64Min", left: { kind: "float64Max", left: P("value"), right: P("low") }, right: P("high") })
await define("trimmed", [], { text: string }, string, { kind: "stringTrim", value: P("text") })
await define("splitTrimmed", [], { text: string, separator: string }, texts, { kind: "listFilter", function: "isNotEmpty",
  source: { kind: "listMap", function: "trimmed", source: { kind: "stringSplit", value: P("text"), separator: P("separator") } } })
await define("stripPrefix", [], { text: string, prefix: string }, string, { kind: "conditional", condition: { kind: "stringStartsWith", value: P("text"), prefix: P("prefix") },
  whenTrue: { kind: "stringSlice", value: P("text"), start: length(P("prefix")), end: length(P("text")) }, whenFalse: P("text") })
await define("beforeFirst", [], { text: string, separator: string }, string, firstOf({ kind: "stringSlice", value: P("text"), start: { kind: "int64Literal", value: "0" }, end: firstAt }, P("text")))
await define("afterFirst", [], { text: string, separator: string }, string, firstOf({ kind: "stringSlice", value: P("text"), start: { kind: "int64WrappingAdd", left: firstAt, right: length(P("separator")) }, end: length(P("text")) }, { kind: "stringLiteral", value: "" }))
await define("parseInt64Or", [], { text: string, fallback: int64 }, int64, { kind: "optionGetOrDefault", option: { kind: "stringParseInt64", value: P("text") }, default: P("fallback") })
await define("stringPadStart", [], { text: string, width: int64, fill: string }, string, { kind: "stringConcat", left: pad, right: P("text") })
await define("stringPadEnd", [], { text: string, width: int64, fill: string }, string, { kind: "stringConcat", left: P("text"), right: pad })
// Not generic: listContains needs an equatable item, and a type parameter never is.
await define("textsLack", [], { items: texts, item: string }, bool, not({ kind: "listContains", list: P("items"), item: P("item") }))
const predicate = { kind: "function", parameters: [T], resultType: bool }
await define("holds", ["T"], { predicate, item: T }, bool, { kind: "functionInvoke", functionValue: P("predicate"), arguments: [P("item")] })
await define("find", ["T"], { items, predicate }, optionOf(T), { kind: "listItemAt", list: P("items"), index: { kind: "optionGetOrDefault", default: { kind: "int64Literal", value: "-1" },
  option: { kind: "listFindIndex", source: P("items"), function: "holds", context: [{ parameter: "predicate", value: P("predicate") }] } } })
await define("optionIsNone", ["T"], { option: optionOf(T) }, bool, not({ kind: "optionIsSome", value: P("option") }))
await define("optionOrElse", ["T"], { option: optionOf(T), other: optionOf(T) }, optionOf(T), { kind: "conditional", condition: { kind: "optionIsSome", value: P("option") }, whenTrue: P("option"), whenFalse: P("other") })
await define("resultIsErr", ["T", "E"], { result: { kind: "builtinGeneric", generic: "result", arguments: [T, E] } }, bool, not({ kind: "resultIsOk", value: P("result") }))
cli.stdin.end()
await new Promise(done => cli.once("close", done))
for (const file of [".backends.json", ".lock"]) rmSync(snapshot + file, { force: true })
