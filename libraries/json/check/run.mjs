// Runs json.json through a program that imports it, on the Zig and C# backends, and compares the replies
// with the expected values. Usage: node libraries/json/check/run.mjs [<host executable>]
import { spawn } from "node:child_process"
import { copyFileSync, mkdtempSync, rmSync } from "node:fs"
import { hostExchange, hostSend } from "../../host-client.mjs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-json-check-"))
process.on("exit", () => rmSync(work, { recursive: true, force: true }))
copyFileSync(join(here, "../json.json"), join(work, "json.json"))
const cli = spawn(executable, ["create", "--snapshot", join(work, "check.json"), "--dependency", join(work, "json.json")], { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const exchange = hostExchange(cli)
const send = hostSend(exchange, request => `${request.op} ${request.displayName ?? ""}`)
const string = { kind: "primitive", primitive: "string" }
const int64 = { kind: "primitive", primitive: "int64" }
const P = name => ({ kind: "parameter", name })
const R = name => ({ kind: "patternReference", name })
const L = value => ({ kind: "stringLiteral", value })
const cat = (...parts) => parts.map(part => typeof part === "string" ? L(part) : part).reduce((left, right) => ({ kind: "stringConcat", left, right }))
const call = (name, args) => ({ kind: "directCall", function: name, arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const someOr = (input, someName, someBody, noneBody) => ({ kind: "matchOption", input, someName, someBody, noneBody })
const define = async (displayName, parameters, body) => {
  const created = await send({ op: "simulateCreateFunctionByName", displayName, exported: true, requiredEffects: [], resultType: string, body,
    parameters: Object.entries(parameters).map(([name, type]) => ({ displayName: name, type })) })
  await send({ op: "commit", proposalId: created.proposalId })
}
const parsed = body => ({ kind: "matchResult", input: call("jsonParse", { text: P("text") }), okName: "v", okBody: body, errName: "e",
  errBody: cat("error ", { kind: "int64ToString", value: { kind: "fieldAccess", source: R("e"), type: "JsonError", field: "position" } }, ": ",
    { kind: "fieldAccess", source: R("e"), type: "JsonError", field: "message" }) })
const Json = { kind: "named", name: "Json" }

await define("roundTrip", { text: string }, parsed(call("jsonSerialize", { value: R("v") })))
// Reads what rrawrlet's Pricing.Rows reads from pricing.json.
await define("textOf", { value: Json, key: string }, someOr(call("jsonField", { value: P("value"), key: P("key") }), "f", someOr(call("jsonText", { value: R("f") }), "s", R("s"), L("notText")), L("missing")))
await define("rateOf", { value: Json, key: string }, someOr(call("jsonField", { value: P("value"), key: P("key") }), "f",
  someOr(call("jsonNumber", { value: R("f") }), "x", { kind: "float64ToString", value: R("x") }, L("notNumber")), L("missing")))
const rate = key => call("rateOf", { value: { kind: "fieldAccess", source: P("member"), type: "JsonMember", field: "value" }, key: L(key) })
await define("modelLine", { member: { kind: "named", name: "JsonMember" } }, cat({ kind: "fieldAccess", source: P("member"), type: "JsonMember", field: "key" }, "=",
  rate("input"), ",", rate("cached"), ",", rate("output"), ",", rate("write")))
const models = { kind: "optionGetOrDefault", default: { kind: "listEmpty", itemType: { kind: "named", name: "JsonMember" } },
  option: someOr(call("jsonField", { value: R("v"), key: L("models") }), "m", call("jsonMembers", { value: R("m") }), { kind: "optionNone", itemType: { kind: "builtinGeneric", generic: "list", arguments: [{ kind: "named", name: "JsonMember" }] } }) }
await define("pricing", { text: string }, parsed(cat(call("textOf", { value: R("v"), key: L("currency") }), " ", call("textOf", { value: R("v"), key: L("unit") }), " ",
  call("textOf", { value: R("v"), key: L("prefer") }), " ", { kind: "stringJoin", separator: L(";"), list: { kind: "listMap", function: "modelLine", source: models } })))
const describe = someOr(call("jsonBool", { value: R("x") }), "b", { kind: "conditional", condition: R("b"), whenTrue: L("bool:true"), whenFalse: L("bool:false") },
  someOr(call("jsonText", { value: R("x") }), "s", cat("text:", R("s")),
    someOr(call("jsonNumber", { value: R("x") }), "f", cat("number:", call("jsonNumberText", { value: R("f") })),
      someOr(call("jsonItems", { value: R("x") }), "a", L("array"), L("object")))))
await define("kindAt", { text: string, index: int64 }, parsed(someOr(call("jsonItem", { value: R("v"), index: P("index") }), "x",
  { kind: "conditional", condition: call("jsonIsNull", { value: R("x") }), whenTrue: L("null"), whenFalse: describe }, L("none"))))
// Builds a long input inside the run, beyond the 64 Ki-unit argument limit, and reports whether it reads back unchanged.
await define("repeated", { open: string, unit: string, count: int64, close: string }, { kind: "bind", name: "t", value: cat(P("open"), { kind: "stringRepeat", text: P("unit"), count: P("count") }, P("close")),
  body: { kind: "bind", name: "r", value: call("roundTrip", { text: R("t") }), body: { kind: "conditional", condition: { kind: "valueEqual", left: R("r"), right: R("t") },
    whenTrue: L("same"), whenFalse: { kind: "stringSlice", value: R("r"), start: { kind: "int64Literal", value: "0" }, end: { kind: "int64Literal", value: "200" } } } } })
await define("quote", { text: string }, call("jsonQuote", { text: P("text") }))

const same = text => [text, text]
const deep = (depth, open, inner, close) => open.repeat(depth) + inner + close.repeat(depth)
const roundTrips = [
  same("null"), [" true ", "true"], same("false"), same("0"), same("-0"), same("\"\""), same("[]"), same("{}"),
  ["[1, 2.5, -3e2, 1E-2, 0.1, 1e21, 123456789012345678, 1.5e-5]", "[1,2.5,-300,0.01,0.1,1e+21,1.2345678901234568e+17,1.5e-05]"],
  same("5e-324"), ["1.7976931348623157e308", "1.7976931348623157e+308"], ["-0.0", "-0"], ["1E+2", "100"],
  [" { \"a\" : [ ] , \"b\" : { } } ", "{\"a\":[],\"b\":{}}"], [" \t\n\r[1]\n", "[1]"], same("{\"a\":1,\"a\":2}"), same("\"日本\""),
  ["\"\\u00e9\\ud83d\\ude00\\n\\\"\\\\\\/\\b\\f\\r\\t\"", "\"é😀\\u000a\\\"\\\\/\\u0008\\u000c\\u000d\\u0009\""],
  same("\"\\u0000\\u001f\""), ["\"\\uABCD\\uDBFF\\uDFFF\\u0041\"", "\"\uABCD\u{10FFFF}A\""], ["\"😀\"", "\"😀\""],
  same("[[1,[true,null]],{\"k\":{\"x\":\"y\"}}]"),
  ["", "error 0: unexpected end of input"], ["   ", "error 3: unexpected end of input"], ["[", "error 1: unexpected end of input"],
  ["[1,]", "error 3: unexpected character"], ["[,1]", "error 1: unexpected character"], ["[1 2]", "error 3: expected , or ]"], ["[1}", "error 2: expected , or ]"],
  ["{\"a\" 1}", "error 5: expected :"], ["{\"a\":1,}", "error 7: expected string key"], ["{a:1}", "error 1: expected string key"], ["{\"a\":1", "error 6: expected , or }"],
  ["01", "error 0: leading zero in number"], ["-01", "error 0: leading zero in number"], ["-", "error 0: invalid number"], ["1.", "error 0: invalid number"],
  [".5", "error 0: unexpected character"], ["+1", "error 0: unexpected character"], ["1e", "error 0: invalid number"], ["1e400", "error 0: number out of range"],
  ["-1x", "error 2: unexpected text after value"], ["\"a\" \"b\"", "error 4: unexpected text after value"],
  ["\"abc", "error 4: unterminated string"], ["\"\\x\"", "error 1: invalid escape"], ["\"\\", "error 1: invalid escape"], ["\"\\u12\"", "error 1: invalid \\u escape"],
  ["\"\\u-123\"", "error 1: invalid \\u escape"], ["\"\\ud800\"", "error 1: unpaired surrogate"], ["\"\\udc00\"", "error 1: unpaired surrogate"],
  ["\"\\ud800\\u0041\"", "error 1: unpaired surrogate"], ["\"a\tb\"", "error 2: control character in string"],
  ["tru", "error 0: unexpected character"], ["nul", "error 0: unexpected character"], ["NaN", "error 0: unexpected character"],
  ["Infinity", "error 0: unexpected character"], ["'a'", "error 0: unexpected character"], ["\ufeff[]", "error 0: unexpected character"],
  // Positions count code points: a UTF-16 count would say 7.
  ["[\"😀\", x]", "error 6: unexpected character"],
  same(deep(512, "[", "", "]")), same(deep(512, "[", "1", "]")), same(deep(512, "{\"a\":", "1", "}")),
  [deep(513, "[", "", "]"), "error 512: nesting too deep"], [deep(513, "{\"a\":", "1", "}"), "error 2560: nesting too deep"],
  same(`{${Array.from({ length: 5000 }, (_, i) => `"k${i}":${i}`).join(",")}}`),
]
const sample = { currency: "USD", unit: "per_million_tokens", prefer: "rates", models: {
  "claude:claude-sonnet-5-5": { input: 3, cached: 0.3, output: 15, write: 3.75 }, "gpt:gpt-5": { input: 1.25, cached: 0.125, output: 10, write: 0 } } }
const cases = [
  ...roundTrips.map(([text, expected]) => ["roundTrip", [text], expected]),
  ["pricing", [JSON.stringify(sample, null, 2)], "USD per_million_tokens rates claude:claude-sonnet-5-5=3.0,0.3,15.0,3.75;gpt:gpt-5=1.25,0.125,10.0,0.0"],
  ["pricing", ["{\"currency\":\"USD\",\"unit\":\"per_million_tokens\",\"models\":{\"m\":{\"input\":\"1\",\"output\":2}}}"], "USD per_million_tokens missing m=notNumber,missing,2.0,missing"],
  ["pricing", ["{\"currency\":\"EUR\",\"unit\":\"u\",\"currency\":\"USD\"}"], "USD u missing "],
  ["pricing", ["{\"currency\":1}"], "notText missing missing "], ["pricing", ["{\"currency\":"], "error 12: unexpected end of input"],
  ...["null", "bool:true", "text:s", "number:2", "array", "object", "none"].map((expected, index) => ["kindAt", ["[null,true,\"s\",2,[],{}]", index], expected]),
  ["kindAt", ["{\"a\":1}", 0], "none"], ["kindAt", ["[1]", -1], "none"],
  ["repeated", ["\"", "a\\\"😀", 200000, "\""], "same"], ["repeated", ["[", "1,", 200000, "1]"], "same"],
  ["repeated", ["{", "\"k\":[],", 100000, "\"k\":{}}"], "same"],
  ["quote", ["a\"b\\c\n\u0001日"], "\"a\\\"b\\\\c\\u000a\\u0001日\""],
]
let failed = 0
for (const backend of ["zig", "csharp"]) for (const [functionName, args, expected] of cases) {
  const started = Date.now()
  const result = await send({ op: "runPureByName", function: functionName, backend, arguments: args, expected }).catch(error => ({ error: error.message }))
  if (process.env.JSON_CHECK_TIMES) console.error(`${Date.now() - started} ms ${backend} ${functionName}(${JSON.stringify(args).slice(0, 40)})`)
  if (result.matches !== true) {
    failed++
    const shown = JSON.stringify(result.actual ?? result).slice(0, 300)
    console.error(`${backend} ${functionName}(${JSON.stringify(args).slice(0, 80)}): ${shown}, expected ${JSON.stringify(expected).slice(0, 80)}`)
  }
}
cli.stdin.end()
if (failed) process.exit(1)
console.log(`${cases.length * 2} calls agree`)
