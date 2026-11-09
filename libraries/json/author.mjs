// Writes json.json: an RFC 8259 parser and serializer for a recursive Json Union, built from existing
// nodes only. Usage: node libraries/json/author.mjs [<host executable>]; run it again after a change.
import { spawn } from "node:child_process"
import { rmSync } from "node:fs"
import { hostExchange } from "../host-client.mjs"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const executable = process.argv[2] ?? resolve(here, "../../tools/zig/host/bin/nyrwvy-host")
const snapshot = join(here, "json.json")
rmSync(snapshot, { force: true })
rmSync(snapshot + ".backends.json", { force: true })
const cli = spawn(executable, ["create", "--snapshot", snapshot], { stdio: ["pipe", "pipe", "inherit"], cwd: resolve(here, "../..") })
const exchange = hostExchange(cli)
const send = async request => {
  const reply = await exchange(request)
  if (!reply.ok) throw new Error(`${request.op} ${request.displayName ?? request.function ?? ""}: ${JSON.stringify(reply.error)}`)
  return reply.result
}
const commit = async created => send({ op: "commit", proposalId: created.proposalId })
const define = async (displayName, parameters, resultType, body, exported = false) => commit(await send({ op: "simulateCreateFunctionByName",
  displayName, exported, requiredEffects: [], resultType, body, parameters: Object.entries(parameters).map(([name, type]) => ({ displayName: name, type })) }))
const replace = async (name, replacement) => commit(await send({ op: "simulateReplaceBodyByName", function: name, replacement }))

const string = { kind: "primitive", primitive: "string" }
const int64 = { kind: "primitive", primitive: "int64" }
const bool = { kind: "primitive", primitive: "bool" }
const float64 = { kind: "primitive", primitive: "float64" }
const named = name => ({ kind: "named", name })
const list = item => ({ kind: "builtinGeneric", generic: "list", arguments: [item] })
const option = item => ({ kind: "builtinGeneric", generic: "option", arguments: [item] })
const result = (ok, error) => ({ kind: "builtinGeneric", generic: "result", arguments: [ok, error] })
const Json = named("Json"), Member = named("JsonMember"), JsonError = named("JsonError"), Parsed = named("JsonParsed"), Decoded = named("JsonString")

const P = name => ({ kind: "parameter", name })
const R = name => ({ kind: "patternReference", name })
const L = value => ({ kind: "stringLiteral", value })
const I = value => ({ kind: "int64Literal", value: String(value) })
const add = (left, right) => ({ kind: "int64WrappingAdd", left, right: typeof right === "number" ? I(right) : right })
const lt = (left, right) => ({ kind: "int64LessThan", left, right })
const eq = (left, right) => ({ kind: "valueEqual", left, right: typeof right === "string" ? L(right) : right })
const and = (left, right) => ({ kind: "boolAnd", left, right })
const not = value => ({ kind: "boolNot", value })
const iff = (condition, whenTrue, whenFalse) => ({ kind: "conditional", condition, whenTrue, whenFalse })
const bind = (name, value, body) => ({ kind: "bind", name, value, body })
const cat = (...parts) => parts.map(part => typeof part === "string" ? L(part) : part).reduce((left, right) => ({ kind: "stringConcat", left, right }))
const len = value => ({ kind: "stringLength", value })
const slice = (value, start, end) => ({ kind: "stringSlice", value, start, end })
const span = (value, set, from) => ({ kind: "stringSpan", value, set: L(set), from })
const call = (name, args, keepUnlisted = false) => ({ kind: "directCall", function: name, ...(keepUnlisted ? { keepUnlisted } : {}), arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const field = (source, type, name) => ({ kind: "fieldAccess", source, type, field: name })
const product = (type, fields) => ({ kind: "productConstruct", type, fields: Object.entries(fields).map(([name, value]) => ({ field: name, value })) })
const json = (variant, fields = {}) => ({ kind: "unionConstruct", type: "Json", variant, fields: Object.entries(fields).map(([name, value]) => ({ field: name, value })) })
const ok = (errorType, value) => ({ kind: "resultOk", errorType, value })
const fail = (okType, position, message) => ({ kind: "resultErr", okType, error: product("JsonError", { position, message: L(message) }) })
const pass = okType => ({ kind: "resultErr", okType, error: R("e") })
const onOk = (input, okName, okBody, okType) => ({ kind: "matchResult", input, okName, okBody, errName: "e", errBody: pass(okType) })
const someOr = (input, someName, someBody, noneBody) => ({ kind: "matchOption", input, someName, someBody, noneBody })
const none = itemType => ({ kind: "optionNone", itemType })
const some = value => ({ kind: "optionSome", value })
const arm = (variant, fieldName, body) => ({ variant, body: fieldName ? bind(fieldName, { kind: "patternReference", scopeDepth: 0, slot: "field", type: "Json", variant, field: fieldName }, body) : body })
const matchJson = (input, arms, otherwise) => ({ kind: "matchUnion", type: "Json", input, arms, ...(otherwise ? { otherwise } : {}) })
// The parser scans the input String by code-point position.
const at = position => ({ kind: "stringSlice", value: P("chars"), start: position, end: add(position, 1) })
const piece = (start, end) => ({ kind: "stringSlice", value: P("chars"), start, end })
const charsSpan = (set, position) => span(P("chars"), set, position)
const ws = position => charsSpan(" \t\n\r", position)
const parsed = (value, next) => ok(JsonError, product("JsonParsed", { value, next }))
const hexDigits = "0123456789abcdefABCDEF"
const float64Zero = { kind: "float64Literal", bits: "0000000000000000" }
const finite = value => ({ kind: "float64Equal", left: { kind: "float64Subtract", left: value, right: value }, right: float64Zero })
// Nesting recurses (one parser frame pair per level); the cap keeps a deep input an Err instead of a stack overflow.
const maxDepth = 512

await commit(await send({ op: "simulateCreateTypesByName", types: [
  { displayName: "Json", exported: true, typeKind: { kind: "union", variants: [
    { displayName: "Null", fields: [] },
    { displayName: "Boolean", fields: [{ displayName: "value", type: bool }] },
    { displayName: "Number", fields: [{ displayName: "value", type: float64 }] },
    { displayName: "Text", fields: [{ displayName: "value", type: string }] },
    { displayName: "Array", fields: [{ displayName: "items", type: list(Json) }] },
    { displayName: "Object", fields: [{ displayName: "members", type: list(Member) }] }] } },
  { displayName: "JsonMember", exported: true, typeKind: { kind: "product", fields: [{ displayName: "key", type: string }, { displayName: "value", type: Json }] } }] }))
await commit(await send({ op: "simulateCreateTypeByName", displayName: "JsonError", exported: true, typeKind: { kind: "product", fields: [
  { displayName: "position", type: int64 }, { displayName: "message", type: string }] } }))
await commit(await send({ op: "simulateCreateTypeByName", displayName: "JsonParsed", typeKind: { kind: "product", fields: [{ displayName: "value", type: Json }, { displayName: "next", type: int64 }] } }))
await commit(await send({ op: "simulateCreateTypeByName", displayName: "JsonString", typeKind: { kind: "product", fields: [{ displayName: "text", type: string }, { displayName: "next", type: int64 }] } }))

const parseResult = result(Parsed, JsonError), stringResult = result(Decoded, JsonError)
const cursor = { chars: string, position: int64 }
// Mutual recursion needs an existing function: a placeholder now, the real body once its callees exist.
await define("jsonParseValue", { ...cursor, depth: int64 }, parseResult, fail(Parsed, P("position"), "placeholder"))

// Four hex digits at position, or None; StringParseInt64Hex alone would also take a sign.
await define("jsonHex4", cursor, option(int64), bind("digits", piece(P("position"), add(P("position"), 4)),
  iff(eq(span(R("digits"), hexDigits, I(0)), I(4)), { kind: "stringParseInt64Hex", value: R("digits") }, none(int64))))
const inRange = (value, low, high) => and(not(lt(value, I(low))), not(lt(I(high), value)))
const decoded = (code, next) => someOr({ kind: "stringFromCodePoint", value: code }, "char", ok(JsonError, product("JsonString", { text: R("char"), next })), fail(Decoded, P("position"), "invalid \\u escape"))
// A \u escape at position (its backslash), joining a surrogate pair into one code point.
await define("jsonUnicode", cursor, stringResult, someOr(call("jsonHex4", { chars: P("chars"), position: add(P("position"), 2) }), "high",
  iff(inRange(R("high"), 0xD800, 0xDBFF),
    someOr(iff(eq(piece(add(P("position"), 6), add(P("position"), 8)), "\\u"), call("jsonHex4", { chars: P("chars"), position: add(P("position"), 8) }), none(int64)), "low",
      iff(inRange(R("low"), 0xDC00, 0xDFFF),
        decoded(add(I(0x10000), add({ kind: "int64ShiftLeft", value: add(R("high"), -0xD800), count: I(10) }, add(R("low"), -0xDC00))), add(P("position"), 12)),
        fail(Decoded, P("position"), "unpaired surrogate")),
      fail(Decoded, P("position"), "unpaired surrogate")),
    iff(inRange(R("high"), 0xDC00, 0xDFFF), fail(Decoded, P("position"), "unpaired surrogate"), decoded(R("high"), add(P("position"), 6)))),
  fail(Decoded, P("position"), "invalid \\u escape")))

// The string body from position (after the opening quote), one code point per tail self call.
const loop = (position, text) => call("jsonParseString", { position, decoded: text }, true)
await define("jsonParseString", { ...cursor, decoded: string }, stringResult, bind("c", at(P("position")),
  iff(eq(R("c"), ""), fail(Decoded, P("position"), "unterminated string"),
  iff(eq(R("c"), "\""), ok(JsonError, product("JsonString", { text: P("decoded"), next: add(P("position"), 1) })),
  iff(eq(R("c"), "\\"), bind("e", at(add(P("position"), 1)),
    iff(eq(R("e"), "u"), onOk(call("jsonUnicode", { chars: P("chars"), position: P("position") }), "u", loop(field(R("u"), "JsonString", "next"), cat(P("decoded"), field(R("u"), "JsonString", "text"))), Decoded),
      someOr(iff(eq(R("e"), ""), none(int64), { kind: "stringIndexOf", value: L("\"\\/bfnrt"), substring: R("e"), from: I(0) }), "k",
        loop(add(P("position"), 2), cat(P("decoded"), slice(L("\"\\/\b\f\n\r\t"), R("k"), add(R("k"), 1)))),
        fail(Decoded, P("position"), "invalid escape")))),
  iff(lt({ kind: "optionGetOrDefault", option: { kind: "stringToCodePoint", value: R("c") }, default: I(0) }, I(0x20)), fail(Decoded, P("position"), "control character in string"),
    loop(add(P("position"), 1), cat(P("decoded"), R("c")))))))))

// The longest run of number characters, read by StringParseFloat64 (the JSON grammar but for leading zeros).
await define("jsonParseNumber", cursor, parseResult, bind("end", charsSpan("0123456789+-.eE", P("position")),
  bind("token", piece(P("position"), R("end")),
  bind("digits", iff({ kind: "stringStartsWith", value: R("token"), prefix: L("-") }, slice(R("token"), I(1), len(R("token"))), R("token")),
  iff(and({ kind: "stringStartsWith", value: R("digits"), prefix: L("0") }, lt(I(1), span(R("digits"), "0123456789", I(0)))), fail(Parsed, P("position"), "leading zero in number"),
    someOr({ kind: "stringParseFloat64", value: R("token") }, "f",
      iff(finite(R("f")), parsed(json("Number", { value: R("f") }), R("end")), fail(Parsed, P("position"), "number out of range")),
      fail(Parsed, P("position"), "invalid number")))))))

// Items after "[" or ",", one per tail self call; a nested value recurses through jsonParseValue.
await define("jsonParseArray", { ...cursor, depth: int64, items: list(Json) }, parseResult, bind("p", ws(P("position")),
  iff(and(eq(at(R("p")), "]"), eq({ kind: "listLength", value: P("items") }, I(0))), parsed(json("Array", { items: P("items") }), add(R("p"), 1)),
    onOk(call("jsonParseValue", { chars: P("chars"), position: R("p"), depth: P("depth") }), "v",
      bind("q", ws(field(R("v"), "JsonParsed", "next")), bind("more", { kind: "listAppendItem", list: P("items"), item: field(R("v"), "JsonParsed", "value") },
        iff(eq(at(R("q")), ","), call("jsonParseArray", { position: add(R("q"), 1), items: R("more") }, true),
        iff(eq(at(R("q")), "]"), parsed(json("Array", { items: R("more") }), add(R("q"), 1)), fail(Parsed, R("q"), "expected , or ]"))))), Parsed))))

// Members after "{" or ",", one per tail self call.
await define("jsonParseObject", { ...cursor, depth: int64, members: list(Member) }, parseResult, bind("p", ws(P("position")),
  iff(and(eq(at(R("p")), "}"), eq({ kind: "listLength", value: P("members") }, I(0))), parsed(json("Object", { members: P("members") }), add(R("p"), 1)),
  iff(not(eq(at(R("p")), "\"")), fail(Parsed, R("p"), "expected string key"),
    onOk(call("jsonParseString", { chars: P("chars"), position: add(R("p"), 1), decoded: L("") }), "k",
      bind("q", ws(field(R("k"), "JsonString", "next")),
      iff(not(eq(at(R("q")), ":")), fail(Parsed, R("q"), "expected :"),
        onOk(call("jsonParseValue", { chars: P("chars"), position: add(R("q"), 1), depth: P("depth") }), "v",
          bind("r", ws(field(R("v"), "JsonParsed", "next")), bind("more", { kind: "listAppendItem", list: P("members"),
            item: product("JsonMember", { key: field(R("k"), "JsonString", "text"), value: field(R("v"), "JsonParsed", "value") }) },
            iff(eq(at(R("r")), ","), call("jsonParseObject", { position: add(R("r"), 1), members: R("more") }, true),
            iff(eq(at(R("r")), "}"), parsed(json("Object", { members: R("more") }), add(R("r"), 1)), fail(Parsed, R("r"), "expected , or }"))))), Parsed))), Parsed)))))

const nested = parse => iff(lt(P("depth"), I(maxDepth)), parse, fail(Parsed, R("p"), "nesting too deep"))
const word = (text, value) => iff(eq(piece(R("p"), add(R("p"), text.length)), text), parsed(value, add(R("p"), text.length)), fail(Parsed, R("p"), "unexpected character"))
await replace("jsonParseValue", bind("p", ws(P("position")), bind("c", at(R("p")),
  iff(eq(R("c"), "{"), nested(call("jsonParseObject", { chars: P("chars"), position: add(R("p"), 1), depth: add(P("depth"), 1), members: { kind: "listEmpty", itemType: Member } })),
  iff(eq(R("c"), "["), nested(call("jsonParseArray", { chars: P("chars"), position: add(R("p"), 1), depth: add(P("depth"), 1), items: { kind: "listEmpty", itemType: Json } })),
  iff(eq(R("c"), "\""), onOk(call("jsonParseString", { chars: P("chars"), position: add(R("p"), 1), decoded: L("") }), "s",
      parsed(json("Text", { value: field(R("s"), "JsonString", "text") }), field(R("s"), "JsonString", "next")), Parsed),
  iff(eq(R("c"), "t"), word("true", json("Boolean", { value: { kind: "boolLiteral", value: true } })),
  iff(eq(R("c"), "f"), word("false", json("Boolean", { value: { kind: "boolLiteral", value: false } })),
  iff(eq(R("c"), "n"), word("null", json("Null")),
  iff(eq(R("c"), ""), fail(Parsed, R("p"), "unexpected end of input"),
  iff({ kind: "stringContains", value: L("-0123456789"), substring: R("c") }, call("jsonParseNumber", { chars: P("chars"), position: R("p") }),
    fail(Parsed, R("p"), "unexpected character"))))))))))))

await define("jsonParse", { text: string }, result(Json, JsonError), bind("chars", P("text"),
  onOk(call("jsonParseValue", { chars: R("chars"), position: I(0), depth: I(0) }), "v",
  bind("end", span(R("chars"), " \t\n\r", field(R("v"), "JsonParsed", "next")),
    iff(eq(R("end"), len(R("chars"))), ok(JsonError, field(R("v"), "JsonParsed", "value")), fail(Json, R("end"), "unexpected text after value"))), Json)), true)

// Serializing. Only `"`, `\` and controls below U+0020 are escaped, the controls as \u00XX.
await define("jsonEscape", { c: string }, string, iff(eq(P("c"), "\""), L("\\\""), iff(eq(P("c"), "\\"), L("\\\\"),
  bind("code", { kind: "optionGetOrDefault", option: { kind: "stringToCodePoint", value: P("c") }, default: I(0x20) },
    iff(lt(R("code"), I(0x20)), bind("hex", { kind: "int64ToHexString", value: R("code") }, cat("\\u", { kind: "stringRepeat", text: L("0"), count: { kind: "int64WrappingSubtract", left: I(4), right: len(R("hex")) } }, R("hex"))), P("c"))))))
await define("jsonQuote", { text: string }, string, cat("\"", { kind: "stringJoin", separator: L(""), list: { kind: "listMap", function: "jsonEscape", source: { kind: "stringCodePoints", value: P("text") } } }, "\""), true)
// Float64ToString without a trailing ".0", so integers read as integers; null for NaN and the infinities, which JSON lacks.
await define("jsonNumberText", { value: float64 }, string, iff(finite(P("value")), bind("s", { kind: "float64ToString", value: P("value") },
  iff({ kind: "stringEndsWith", value: R("s"), suffix: L(".0") }, slice(R("s"), I(0), add(len(R("s")), -2)), R("s"))), L("null")), true)
await define("jsonSerialize", { value: Json }, string, L("placeholder"), true)
await define("jsonSerializeMember", { member: Member }, string, cat(call("jsonQuote", { text: field(P("member"), "JsonMember", "key") }), ":", call("jsonSerialize", { value: field(P("member"), "JsonMember", "value") })))
const joined = (open, source, fn, close) => cat(open, { kind: "stringJoin", separator: L(","), list: { kind: "listMap", function: fn, source } }, close)
await replace("jsonSerialize", matchJson(P("value"), [
  arm("Null", null, L("null")),
  arm("Boolean", "value", iff(R("value"), L("true"), L("false"))),
  arm("Number", "value", call("jsonNumberText", { value: R("value") })),
  arm("Text", "value", call("jsonQuote", { text: R("value") })),
  arm("Array", "items", joined("[", R("items"), "jsonSerialize", "]")),
  arm("Object", "members", joined("{", R("members"), "jsonSerializeMember", "}"))]))

// Accessors: None when the value is another variant.
await define("jsonMemberHasKey", { member: Member, key: string }, bool, eq(field(P("member"), "JsonMember", "key"), P("key")))
await define("jsonMembers", { value: Json }, option(list(Member)), matchJson(P("value"), [arm("Object", "members", some(R("members")))], none(list(Member))), true)
await define("jsonItems", { value: Json }, option(list(Json)), matchJson(P("value"), [arm("Array", "items", some(R("items")))], none(list(Json))), true)
// The last member with the key wins, as in System.Text.Json and JavaScript.
await define("jsonField", { value: Json, key: string }, option(Json), someOr(call("jsonMembers", { value: P("value") }), "all", bind("members", { kind: "listReverse", value: R("all") },
  someOr({ kind: "listFindIndex", source: R("members"), function: "jsonMemberHasKey", context: [{ parameter: "key", value: P("key") }] }, "i",
    someOr({ kind: "listItemAt", list: R("members"), index: R("i") }, "m", some(field(R("m"), "JsonMember", "value")), none(Json)), none(Json))), none(Json)), true)
await define("jsonItem", { value: Json, index: int64 }, option(Json), someOr(call("jsonItems", { value: P("value") }), "items", { kind: "listItemAt", list: R("items"), index: P("index") }, none(Json)), true)
await define("jsonText", { value: Json }, option(string), matchJson(P("value"), [arm("Text", "value", some(R("value")))], none(string)), true)
await define("jsonNumber", { value: Json }, option(float64), matchJson(P("value"), [arm("Number", "value", some(R("value")))], none(float64)), true)
await define("jsonBool", { value: Json }, option(bool), matchJson(P("value"), [arm("Boolean", "value", some(R("value")))], none(bool)), true)
await define("jsonIsNull", { value: Json }, bool, matchJson(P("value"), [arm("Null", null, { kind: "boolLiteral", value: true })], { kind: "boolLiteral", value: false }), true)
cli.stdin.end()
await new Promise(done => cli.once("close", done))
for (const file of [".backends.json", ".lock"]) rmSync(snapshot + file, { force: true })
