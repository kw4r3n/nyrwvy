// Authors the model-cooldown program into a session the caller opened with host.json and json.json as
// dependencies: the locked read-modify-write of model-cooldown.json, ported from rrawrlet's
// Paths.Cooldown (Host.cs). Shared by run.mjs (C#) and run-zig.mjs; the snapshot stays in a temp dir.
const prim = name => ({ kind: "primitive", primitive: name })
const str = prim("string"), int = prim("int64"), boolT = prim("bool"), unit = prim("unit")
const named = name => ({ kind: "named", name })
const Json = named("Json"), JsonMember = named("JsonMember"), LoadResult = named("LoadResult"), CleanState = named("CleanState")
const generic = (name, ...args) => ({ kind: "builtinGeneric", generic: name, arguments: args })
const resultOf = ok => generic("result", ok, str)
const optionOf = t => generic("option", t)
const listOf = t => generic("list", t)
const cap = name => ({ kind: "capability", name })

const P = name => ({ kind: "parameter", name })
const R = name => ({ kind: "patternReference", name })
const L = value => ({ kind: "stringLiteral", value })
const I = value => ({ kind: "int64Literal", value: String(value) })
const TRUE = { kind: "boolLiteral", value: true }, FALSE = { kind: "boolLiteral", value: false }
const call = (fn, args = {}) => ({ kind: "directCall", function: fn, arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const invoke = (capParam, capability, operation, args = {}) => ({ kind: "capabilityInvoke", capabilityValue: P(capParam), capability, operation,
  arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const ok = value => ({ kind: "resultOk", errorType: str, value })
const errAs = (okType, error) => ({ kind: "resultErr", okType, error })
const some = value => ({ kind: "optionSome", value })
const none = itemType => ({ kind: "optionNone", itemType })
const cat = (...parts) => parts.map(part => typeof part === "string" ? L(part) : part).reduce((left, right) => ({ kind: "stringConcat", left, right }))
const field = (source, type, name) => ({ kind: "fieldAccess", source, type, field: name })
const product = (type, fields) => ({ kind: "productConstruct", type, fields: Object.entries(fields).map(([field, value]) => ({ field, value })) })
const jsonValue = (variant, fields = {}) => ({ kind: "unionConstruct", type: "Json", variant, fields: Object.entries(fields).map(([field, value]) => ({ field, value })) })
const bind = (name, value, body) => ({ kind: "bind", name, value, body })
const onOk = (input, okBody, okType) => ({ kind: "matchResult", input, okName: "u", okBody, errName: "e", errBody: errAs(okType, R("e")) })
const onSome = (input, someName, someBody, noneBody) => ({ kind: "matchOption", input, someName, someBody, noneBody })
const eq = (left, right) => ({ kind: "valueEqual", left, right })
const lt = (left, right) => ({ kind: "int64LessThan", left, right })

export async function author(send) {
  const commit = async created => { await send({ op: "commit", proposalId: created.proposalId }) }
  await commit(await send({ op: "simulateCreateTypesByName", types: [
    { displayName: "LoadResult", typeKind: { kind: "product", fields: [
      { displayName: "entries", type: Json }, { displayName: "moved", type: boolT } ] } },
    { displayName: "CleanState", typeKind: { kind: "product", fields: [
      { displayName: "text", type: str }, { displayName: "inRun", type: boolT } ] } },
  ] }))
  const define = async (displayName, parameters, resultType, requiredEffects, body, exported = false) =>
    commit(await send({ op: "simulateCreateFunctionByName", displayName, exported, resultType, requiredEffects, body,
      parameters: Object.entries(parameters).map(([displayName, type]) => ({ displayName, type })) }))

  // A Json number read as an Int64 only when it is exactly integral.
  await define("jsonInt", { value: Json }, optionOf(int), [],
    onSome(call("jsonNumber", { value: P("value") }), "f",
      onSome({ kind: "float64RoundToInt64", value: R("f") }, "r",
        { kind: "conditional", condition: { kind: "float64Equal", left: { kind: "int64ToFloat64", value: R("r") }, right: R("f") },
          whenTrue: some(R("r")), whenFalse: none(int) },
        none(int)),
      none(int)))
  await define("fieldInt", { entry: Json, key: str }, optionOf(int), [],
    onSome(call("jsonField", { value: P("entry"), key: P("key") }), "j", call("jsonInt", { value: R("j") }), none(int)))
  await define("untilOrZero", { entry: Json }, int, [],
    { kind: "optionGetOrDefault", option: call("fieldInt", { entry: P("entry"), key: L("until") }), default: I(0) })
  // listFilter predicates: keep entries whose until is still ahead; drop the model being updated.
  await define("keepMember", { member: JsonMember, now: int }, boolT, [],
    lt(P("now"), call("untilOrZero", { entry: field(P("member"), "JsonMember", "value") })))
  await define("notModel", { member: JsonMember, model: str }, boolT, [],
    { kind: "boolNot", value: eq(field(P("member"), "JsonMember", "key"), P("model")) })
  // The original's reason cleanup: every run of U+001F, CR, LF becomes one space.
  const isCtrl = { kind: "boolOr", left: { kind: "boolOr", left: eq(P("cp"), L("")), right: eq(P("cp"), L("\r")) }, right: eq(P("cp"), L("\n")) }
  await define("cleanStep", { state: CleanState, cp: str }, CleanState, [],
    { kind: "conditional", condition: isCtrl,
      whenTrue: { kind: "conditional", condition: field(P("state"), "CleanState", "inRun"),
        whenTrue: P("state"),
        whenFalse: product("CleanState", { text: cat(field(P("state"), "CleanState", "text"), " "), inRun: TRUE }) },
      whenFalse: product("CleanState", { text: cat(field(P("state"), "CleanState", "text"), P("cp")), inRun: FALSE }) })
  await define("cleanReason", { reason: str }, str, [],
    field({ kind: "listFold", source: { kind: "stringCodePoints", value: P("reason") },
      seed: product("CleanState", { text: L(""), inRun: FALSE }), function: "cleanStep", context: [] }, "CleanState", "text"))
  await define("emptyObject", {}, Json, [], jsonValue("Object", { members: { kind: "listEmpty", itemType: JsonMember } }))
  await define("objectOrEmpty", { value: Json }, Json, [],
    onSome(call("jsonMembers", { value: P("value") }), "ms", jsonValue("Object", { members: R("ms") }), call("emptyObject")))
  await define("membersOf", { entries: Json }, listOf(JsonMember), [],
    { kind: "optionGetOrDefault", option: call("jsonMembers", { value: P("entries") }), default: { kind: "listEmpty", itemType: JsonMember } })

  const loaded = entries => ok(product("LoadResult", { entries, moved: FALSE }))
  await define("parseLoaded", { fs: cap("Fs"), file: str, text: str }, resultOf(LoadResult), ["System"],
    { kind: "matchResult", input: call("jsonParse", { text: P("text") }),
      okName: "json", okBody: ok(product("LoadResult", { entries: call("objectOrEmpty", { value: R("json") }), moved: FALSE })),
      errName: "parseError", errBody: onOk(invoke("fs", "Fs", "rename", { from: P("file"), to: cat(P("file"), ".bad") }),
        ok(product("LoadResult", { entries: call("emptyObject"), moved: TRUE })), LoadResult) })
  await define("loadEntries", { fs: cap("Fs"), file: str }, resultOf(LoadResult), ["System"],
    { kind: "conditional", condition: invoke("fs", "Fs", "exists", { path: P("file") }),
      whenTrue: { kind: "matchResult", input: invoke("fs", "Fs", "readText", { path: P("file") }),
        okName: "text", okBody: call("parseLoaded", { fs: P("fs"), file: P("file"), text: R("text") }),
        errName: "e", errBody: errAs(LoadResult, R("e")) },
      whenFalse: loaded(call("emptyObject")) })
  await define("writeEntries", { fs: cap("Fs"), file: str, members: listOf(JsonMember) }, resultOf(unit), ["System"],
    bind("bytes", { kind: "bytesFromLatin1", value: { kind: "stringEncodeUtf8", value: call("jsonSerialize", { value: jsonValue("Object", { members: P("members") }) }) } },
      onOk(invoke("fs", "Fs", "writeBytes", { path: cat(P("file"), ".tmp"), data: R("bytes") }),
        invoke("fs", "Fs", "rename", { from: cat(P("file"), ".tmp"), to: P("file") }), unit)))
  // Up to 101 attempts, 50 ms apart, as the original's Hold; the retry loop is a tail self call.
  await define("acquireLoop", { locks: cap("Locks"), clock: cap("Clock"), path: str, left: int }, resultOf(cap("Lock")), ["System"],
    { kind: "matchResult", input: invoke("locks", "Locks", "acquire", { path: P("path") }),
      okName: "lock", okBody: ok(R("lock")),
      errName: "e", errBody: { kind: "conditional", condition: lt(P("left"), I(1)),
        whenTrue: errAs(cap("Lock"), R("e")),
        whenFalse: bind("waited", invoke("clock", "Clock", "sleep", { milliseconds: I(50) }),
          call("acquireLoop", { locks: P("locks"), clock: P("clock"), path: P("path"),
            left: { kind: "int64WrappingSubtract", left: P("left"), right: I(1) } })) } })
  await define("finishWrite", { fs: cap("Fs"), file: str, members: listOf(JsonMember), note: str }, resultOf(str), ["System"],
    onOk(call("writeEntries", { fs: P("fs"), file: P("file"), members: P("members") }), ok(P("note")), str))

  const sweep = source => ({ kind: "listFilter", source, function: "keepMember", context: [{ parameter: "now", value: R("now") }] })
  const member = (key, value) => product("JsonMember", { key: L(key), value })
  const write = members => call("finishWrite", { fs: P("fs"), file: R("file"), members, note: R("note") })
  const removal = onSome(R("existing"), "entry", write(sweep(R("base"))), ok(R("note")))
  const newEntry = jsonValue("Object", { members: { kind: "listConstruct", itemType: JsonMember, items: [
    member("until", jsonValue("Number", { value: { kind: "int64ToFloat64", value: R("until") } })),
    member("since", jsonValue("Number", { value: { kind: "int64ToFloat64", value: R("since") } })),
    member("reason", jsonValue("Text", { value: call("cleanReason", { reason: P("reason") }) })) ] } })
  const upsert = bind("oldUntil", onSome(R("existing"), "entry", call("untilOrZero", { entry: R("entry") }), I(0)),
    bind("until", { kind: "int64Max", left: { kind: "int64WrappingAdd", left: R("now"), right: P("seconds") }, right: R("oldUntil") },
    bind("since", { kind: "conditional", condition: lt(R("now"), R("oldUntil")),
      whenTrue: { kind: "optionGetOrDefault", default: R("now"),
        option: onSome(R("existing"), "entry", call("fieldInt", { entry: R("entry"), key: L("since") }), none(int)) },
      whenFalse: R("now") },
    bind("entry", newEntry,
    write(sweep({ kind: "listAppendItem", list: R("base"),
      item: product("JsonMember", { key: P("model"), value: R("entry") }) }))))))
  const branch = { kind: "conditional", condition: lt(P("seconds"), I(1)), whenTrue: removal, whenFalse: upsert }
  const body = bind("file", cat(P("dir"), "/model-cooldown.json"),
    onOk(call("loadEntries", { fs: P("fs"), file: R("file") }),
      bind("now", invoke("clock", "Clock", "now"),
      bind("entries", field(R("u"), "LoadResult", "entries"),
      bind("note", { kind: "conditional", condition: field(R("u"), "LoadResult", "moved"),
        whenTrue: L("corrupt file moved to model-cooldown.json.bad"), whenFalse: L("") },
      bind("existing", call("jsonField", { value: R("entries"), key: P("model") }),
      bind("base", { kind: "listFilter", source: call("membersOf", { entries: R("entries") }), function: "notModel",
        context: [{ parameter: "model", value: P("model") }] },
      branch))))),
      str))
  await define("coolBody", { fs: cap("Fs"), clock: cap("Clock"), dir: str, model: str, seconds: int, reason: str, lock: cap("Lock") },
    resultOf(str), ["System"], body)
  await define("coolModel", { fs: cap("Fs"), clock: cap("Clock"), locks: cap("Locks"), dir: str, model: str, seconds: int, reason: str },
    resultOf(str), ["System"],
    { kind: "useResource",
      acquire: call("acquireLoop", { locks: P("locks"), clock: P("clock"), path: cat(P("dir"), "/model-cooldown.json.lock"), left: I(100) }),
      releaseCapability: P("locks"), capability: "Locks", releaseOperation: "release", body: "coolBody",
      context: ["fs", "clock", "dir", "model", "seconds", "reason"].map(parameter => ({ parameter, value: P(parameter) })) }, true)
}
