// Author and maintain a complete file-backed program through the ordinary tools.
import assert from "node:assert/strict"
import { appendFile, cp, mkdir, mkdtemp, readFile, readdir, rename, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import * as z from "zod/v4"
import { createGraphTools } from "../../tools/nyrwvy-agent/graph-tools.mjs"

const repositoryRoot = fileURLToPath(new URL("../../", import.meta.url))
const replay = process.argv[2] === "--replay"
const evolve = process.argv[2] === "--v2"
const signature = process.argv[2] === "--signature"
if (replay && !process.argv[3]) throw new Error("Usage: run.mjs --replay <bundle-directory>")
const requestedOutput = replay || evolve || signature ? process.argv[3] : process.argv[2]
const output = requestedOutput ? resolve(requestedOutput) : await mkdtemp(join(tmpdir(), "nyrwvy-file-batch-"))
const original = join(output, "original"), moved = join(output, "moved")
if (!replay) {
  await mkdir(output, { recursive: true })
  assert.deepEqual(await readdir(output), [], "Use an empty output directory")
  await mkdir(original)
}
const context = { sessionID: `file-batch-${process.pid}`, worktree: repositoryRoot, allowHostSources: true }
const tools = createGraphTools(z)
async function exchange(name, args = {}) {
  const raw = await tools[name].execute(args, context)
  await appendFile(join(output, "transcript.jsonl"), JSON.stringify({ tool: `nyrwvy_${name}`, arguments: args, response: raw }) + "\n")
  return name === "close" ? raw : JSON.parse(raw)
}
async function call(name, args = {}) {
  const response = await exchange(name, args)
  if (name === "close") return response
  if (response.ok === false) throw new Error(JSON.stringify(response.error))
  return response.result ?? response
}
async function publish(proposal) {
  assert.equal(proposal.review.readiness.kind, "semanticallyReady")
  await call("commit", { proposalId: proposal.proposalId })
}

const primitive = primitive => ({ kind: "primitive", primitive })
const string = primitive("string"), int64 = primitive("int64"), unit = primitive("unit")
const generic = (generic, ...args) => ({ kind: "builtinGeneric", generic, arguments: args })
const named = name => ({ kind: "named", name })
const capability = name => ({ kind: "capability", name })
const result = ok => generic("result", ok, string)
const summaryType = named("BatchSummary"), configType = named("BatchConfig")
const parameter = (displayName, type) => ({ displayName, type })
const p = name => ({ kind: "parameter", name })
const text = value => ({ kind: "stringLiteral", value })
const integer = value => ({ kind: "int64Literal", value: String(value) })
const pattern = (slot = "value", scopeDepth = 0) => ({ kind: "patternReference", slot, scopeDepth })
const binary = (kind, left, right) => ({ kind, left, right })
const concat = (...items) => items.reduce((left, right) => binary("stringConcat", left, right))
const add = (left, right) => binary("int64WrappingAdd", left, right)
const field = (source, type, field) => ({ kind: "fieldAccess", source, type, field })
const ok = value => ({ kind: "resultOk", value, errorType: string })
const err = (error, okType = summaryType) => ({ kind: "resultErr", error, okType })
const bind = (value, body) => ({ kind: "bind", value, body })
const choose = (condition, whenTrue, whenFalse) => ({ kind: "conditional", condition, whenTrue, whenFalse })
const match = (input, okBody, errBody) => ({ kind: "matchResult", input, okBody, errBody })
const direct = (functionName, values) => ({ kind: "directCall", function: functionName,
  arguments: Object.entries(values).map(([parameter, value]) => ({ parameter, value })) })
const invoke = (operation, values) => ({ kind: "capabilityInvoke", capabilityValue: p("files"), capability: "BatchFiles", operation,
  arguments: Object.entries(values).map(([parameter, value]) => ({ parameter, value })) })
const summary = (succeeded, failed, contents) => ({ kind: "productConstruct", type: "BatchSummary",
  fields: [{ field: "succeeded", value: succeeded }, { field: "failed", value: failed }, { field: "contents", value: contents }] })
async function create(displayName, parameters, resultType, body, effects = ["FileIO"]) {
  await publish(await call("create_function", { displayName, parameters, resultType, requiredEffects: effects, body }))
}

async function author() {
  const library = join(output, "format-library.json")
  await call("open", { mode: "create", snapshot: library })
  await create("normalizeValue", [parameter("value", string)], string, { kind: "stringToUpper", value: p("value") }, [])
  await call("close")
  await call("open", { mode: "create", snapshot: join(original, "batch.json"), dependency: library })
  for (const [displayName, fields] of [
    ["BatchConfig", [parameter("requests", generic("list", string)), parameter("maxAttempts", int64), parameter("output", string)]],
    ["BatchSummary", [parameter("succeeded", int64), parameter("failed", int64), parameter("contents", string)]],
  ]) await publish(await call("create_type", { displayName, typeKind: { kind: "product", fields } }))
  await publish(await call("request", { op: "simulateCreateEffect", arguments: { displayName: "FileIO" } }))
  await publish(await call("request", { op: "simulateCreateCapabilityByName", arguments: {
    displayName: "BatchFiles", operations: [
      ["open", [parameter("root", string)], result(string)],
      ["readConfig", [parameter("handle", string)], result(configType)],
      ["fetch", [parameter("handle", string), parameter("request", string), parameter("attempt", int64)], result(string)],
      ["save", [parameter("handle", string), parameter("path", string), parameter("contents", string)], result(unit)],
      ["close", [parameter("handle", string)], unit],
    ].map(([displayName, parameters, resultType]) => ({ displayName, parameters, resultType, effect: "FileIO" })),
  } }))
  // Test coordination is another explicit host boundary, not a scheduler in the core.
  await publish(await call("request", { op: "simulateCreateCapabilityByName", arguments: {
    displayName: "CancellationControl", operations: [{ displayName: "awaitRequest", parameters: [], resultType: unit, effect: "FileIO" }],
  } }))

  await create("missingLine", [parameter("request", string), parameter("error", string)], string,
    concat(text("error "), p("request"), text(": "), p("error"), text("\n")), [])
  await create("fetchWithRetry", [parameter("files", capability("BatchFiles")), parameter("handle", string),
    parameter("request", string), parameter("attempt", int64), parameter("limit", int64)], result(string),
    choose({ kind: "cancellationRequested" }, err(text("cancelled"), string),
      match(invoke("fetch", { handle: p("handle"), request: p("request"), attempt: p("attempt") }),
        ok(direct("normalizeValue", { value: pattern() })),
        choose(binary("boolAnd", binary("stringEqual", pattern("error"), text("missing")),
          binary("int64LessThan", p("attempt"), p("limit"))),
          { kind: "directCall", function: "fetchWithRetry", keepUnlisted: true,
            arguments: [{ parameter: "attempt", value: add(p("attempt"), integer(1)) }] },
          err(pattern("error"), string)))))
  const acc = name => field(p("accumulator"), "BatchSummary", name)
  await create("collectRequest", [parameter("files", capability("BatchFiles")), parameter("handle", string), parameter("limit", int64),
    parameter("accumulator", summaryType), parameter("request", string)], summaryType,
    choose({ kind: "cancellationRequested" }, p("accumulator"),
      match(direct("fetchWithRetry", { files: p("files"), handle: p("handle"), request: p("request"), attempt: integer(1), limit: p("limit") }),
        summary(add(acc("succeeded"), integer(1)), acc("failed"), concat(acc("contents"), text("ok "), p("request"), text(": "), pattern(), text("\n"))),
        summary(acc("succeeded"), add(acc("failed"), integer(1)), concat(acc("contents"), direct("missingLine", { request: p("request"), error: pattern("error") }))))))
  await create("saveSummary", [parameter("files", capability("BatchFiles")), parameter("handle", string), parameter("path", string), parameter("summary", summaryType)], result(summaryType),
    choose({ kind: "cancellationRequested" }, err(text("cancelled")),
      match(invoke("save", { handle: p("handle"), path: p("path"), contents: field(p("summary"), "BatchSummary", "contents") }),
        ok(p("summary")), err(pattern("error")))))
  await create("processConfig", [parameter("files", capability("BatchFiles")), parameter("handle", string), parameter("config", configType)], result(summaryType),
    choose(binary("int64LessThan", field(p("config"), "BatchConfig", "maxAttempts"), integer(1)), err(text("maxAttempts must be positive")),
      bind({ kind: "listEffectFold", source: field(p("config"), "BatchConfig", "requests"), seed: summary(integer(0), integer(0), text("")),
        function: "collectRequest", context: [{ parameter: "files", value: p("files") }, { parameter: "handle", value: p("handle") },
          { parameter: "limit", value: field(p("config"), "BatchConfig", "maxAttempts") }] },
      direct("saveSummary", { files: p("files"), handle: p("handle"), path: field(p("config"), "BatchConfig", "output"), summary: pattern() }))))
  await create("resourceBody", [parameter("files", capability("BatchFiles")), parameter("handle", string)], result(summaryType),
    match(invoke("readConfig", { handle: p("handle") }),
      direct("processConfig", { files: p("files"), handle: p("handle"), config: pattern() }), err(pattern("error"))))
  await create("runBatch", [parameter("root", string), parameter("files", capability("BatchFiles"))], result(summaryType),
    { kind: "useResource", acquire: invoke("open", { root: p("root") }), releaseCapability: p("files"), capability: "BatchFiles",
      releaseOperation: "close", body: "resourceBody", context: [{ parameter: "files", value: p("files") }] })

  const taskType = generic("task", result(summaryType))
  const list = (itemType, items) => ({ kind: "listConstruct", itemType, items })
  const task = pattern("value", 2)
  const wait = { kind: "taskWait", tasks: list(taskType, [task]), mode: "all" }
  const takeResult = { kind: "matchOption", input: { kind: "listItemAt", list: wait, index: integer(0) },
    someBody: { kind: "matchOption", input: pattern(), someBody: pattern(), noneBody: err(text("task did not finish")) },
    noneBody: err(text("task missing")) }
  await create("cancelScopeBody", [parameter("root", string), parameter("files", capability("BatchFiles")),
    parameter("control", capability("CancellationControl")), parameter("scope", generic("taskScope"))], result(summaryType),
    bind({ kind: "taskStart", scope: p("scope"), worker: "runBatch", item: p("root"), context: [{ parameter: "files", value: p("files") }] },
      bind({ kind: "capabilityInvoke", capabilityValue: p("control"), capability: "CancellationControl", operation: "awaitRequest", arguments: [] },
        bind({ kind: "taskCancel", task: pattern("value", 1) }, takeResult))))
  await create("cancelBatch", [parameter("root", string), parameter("files", capability("BatchFiles")), parameter("control", capability("CancellationControl"))], result(summaryType),
    { kind: "taskScope", body: "cancelScopeBody", context: ["root", "files", "control"].map(parameter => ({ parameter, value: p(parameter) })) })
  return library
}

async function writeHost(directory) {
  const declarations = await call("inspect", { view: "declarations" })
  const artifact = await call("request", { op: "emitCSharp", arguments: { profile: { kind: "externalImplementations",
    operationIds: declarations.capabilities.flatMap(c => c.operations.map(op => op.id)),
    concurrentCapabilityIds: declarations.capabilities.map(c => c.id) } } })
  const symbol = (kind, id) => artifact.symbols[kind].find(item => item.id === id).name
  const files = declarations.capabilities.find(c => c.displayName === "BatchFiles")
  const control = declarations.capabilities.find(c => c.displayName === "CancellationControl")
  const config = declarations.types.find(t => t.displayName === "BatchConfig")
  const failure = declarations.types.find(t => t.displayName === "FetchFailure")
  const failureType = failure ? symbol("types", failure.id) : "NyStr"
  const failureValue = (variant, message) => failure ? `new ${symbol("variants", failure.typeKind.variants.find(v => v.displayName === variant).id)}(${JSON.stringify(message)})` : JSON.stringify(message)
  const substitutions = {
    FETCH_ERROR: failureType, FETCH_MISSING: failureValue("Retryable", "missing"), FETCH_CANCELLED: failureValue("Permanent", "cancelled"), FETCH_PERMANENT: failureValue("Permanent", "denied"),
    FILES: symbol("capabilities", files.id), CONTROL: symbol("capabilities", control.id), CONFIG: symbol("types", config.id),
    CONFIG_ARGUMENTS: config.typeKind.fields.map(f => `${symbol("fields", f.id)}: ` + ({ requests: 'NyList<NyStr>.Create(settings.GetProperty("requests").EnumerateArray().Select(item => (NyStr)item.GetString()!).ToArray())',
      maxAttempts: 'settings.GetProperty("maxAttempts").GetInt64()', output: '(NyStr)settings.GetProperty("output").GetString()!' })[f.displayName]).join(",\n                "),
  }
  for (const op of [...files.operations, ...control.operations]) substitutions[op.displayName.toUpperCase()] = symbol("capabilityOperations", op.id)
  const template = await readFile(new URL("./Host.cs.template", import.meta.url), "utf8")
  const host = template.replace(/@@([A-Z_]+)@@/g, (_, key) => { assert.ok(substitutions[key], key); return substitutions[key] })
  await writeFile(join(directory, "Host.cs"), host)
  // Keep the emitter's unedited projection as evidence; runPure emits it again itself.
  await writeFile(join(directory, "Generated.cs"), artifact.source)
}

const runString = value => ({ kind: "string", value })
const summaryValue = (succeeded, failed, contents) => ({ kind: "product", type: "BatchSummary", fields: [
  { field: "succeeded", value: { kind: "int64", value: String(succeeded) } },
  { field: "failed", value: { kind: "int64", value: String(failed) } }, { field: "contents", value: runString(contents) },
] })
const expected = (isOk, value) => ({ kind: "result", okType: summaryType, errorType: string, isOk, value })
const host = implementation => ({ kind: "hostCapability", capability: "BatchFiles", implementation, supportsConcurrentCalls: true })
const cases = [
  ["success", "runBatch", [host("BatchExample.Files")], expected(true, summaryValue(3, 0, "ok alpha: A\nok beta: B\nok gamma: C\n"))],
  ["retry-and-missing", "runBatch", [host("BatchExample.Files")], expected(true, summaryValue(2, 1, "ok alpha: A\nmissing beta: missing\nok gamma: C\n"))],
  ["invalid-config", "runBatch", [host("BatchExample.Files")], expected(false, runString("maxAttempts must be positive"))],
  ["host-error", "runBatch", [host("BatchExample.Files")], undefined],
  ["cancelled", "cancelBatch", [host("BatchExample.WaitingFiles"), { kind: "hostCapability", capability: "CancellationControl", implementation: "BatchExample.Control", supportsConcurrentCalls: true }], expected(false, runString("cancelled"))],
].map(([name, functionName, bindings, expected]) => ({ id: name, op: "runPureByName", function: functionName,
  arguments: [runString(`fixtures/${name}`), ...bindings], hostSources: ["Host.cs"], ...(expected ? { expected } : {}) }))

async function fixtures(directory) {
  for (const name of cases.map(c => c.id)) {
    const root = join(directory, "fixtures", name)
    await mkdir(join(root, "responses"), { recursive: true })
    const requests = name === "cancelled" ? ["pause"] : ["alpha", "beta", "gamma"]
    await writeFile(join(root, "config.json"), JSON.stringify({ requests, maxAttempts: name === "invalid-config" ? 0 : 2, output: "report.txt" }, null, 2))
    for (const [key, value] of [["alpha", "a"], ["beta", "b"], ["gamma", "c"]]) {
      if (name === "retry-and-missing" && key === "beta") continue
      if (name === "host-error" && key === "beta") { await mkdir(join(root, "responses", `${key}.1.txt`), { recursive: true }); continue }
      const attempt = name === "retry-and-missing" && key === "gamma" ? 2 : 1
      await writeFile(join(root, "responses", `${key}.${attempt}.txt`), value)
    }
  }
}

async function runSaved(directory) {
  const saved = JSON.parse(await readFile(join(directory, "cases.json"), "utf8"))
  for (const savedCase of saved) {
    const root = resolve(directory, savedCase.arguments[0].value)
    await rm(join(root, "trace.txt"), { force: true })
    await rm(join(root, "report.txt"), { force: true })
    const { id: name, op, ...arguments_ } = savedCase
    arguments_.arguments[0] = runString(root)
    const response = await exchange("request", { op, arguments: arguments_ })
    if (name === "host-error") {
      assert.equal(response.ok, false, JSON.stringify(response))
      assert.equal(response.error.code, "RunRuntimeFailure", JSON.stringify(response))
      assert.ok(response.error.calls.some(c => c.operation === "close"))
    } else { assert.equal(response.ok, true, JSON.stringify(response)); assert.equal(response.result.matches, true, JSON.stringify(response)) }
    const trace = (await readFile(join(root, "trace.txt"), "utf8")).trim().split("\n")
    assert.equal(trace[0], "open", name)
    assert.equal(trace.at(-1), "close disposed", name)
    assert.equal(trace.filter(line => line === "close disposed").length, 1, name)
    if (["success", "retry-and-missing", "permanent"].includes(name)) {
      const expectedContents = savedCase.expected.value.fields.find(f => f.field === "contents").value.value
      assert.equal(await readFile(join(root, "report.txt"), "utf8"), expectedContents)
    } else await assert.rejects(readFile(join(root, "report.txt")), { code: "ENOENT" })
    if (name === "retry-and-missing") assert.deepEqual(trace.filter(line => line.startsWith("fetch")), ["fetch alpha 1", "fetch beta 1", "fetch beta 2", "fetch gamma 1", "fetch gamma 2"])
    if (name === "permanent") assert.deepEqual(trace.filter(line => line.startsWith("fetch")), ["fetch alpha 1", "fetch beta 1", "fetch gamma 1"])
    if (name === "cancelled") assert.deepEqual(trace, ["open", "readConfig", "fetch pause 1", "close disposed"])
    console.log(`${name}: ${name === "host-error" ? "host exception" : "expected value"}; cleanup confirmed`)
  }
}

try {
  if (replay) {
    await call("open", { mode: "open", snapshot: join(output, "batch.json") })
    await runSaved(output)
    console.log(`PASS: replayed saved cases. ${output}`)
  } else {
  const library = await author()
  await writeHost(original)
  await fixtures(original)
  // A reviewed one-node maintenance edit; no source or snapshot is hand-edited.
  const before = await call("inspect", { view: "function", function: "missingLine" })
  const target = before.function.body.nodes.find(node => node.kind.kind === "stringLiteral" && node.kind.value === "error ")
  assert.ok(target, JSON.stringify(before.function.body))
  await publish(await call("replace", { function: "missingLine", targetNodeId: target.id, replacement: text("missing ") }))
  const after = await call("inspect", { view: "function", function: "missingLine" })
  assert.equal(after.function.id, before.function.id)
  await writeFile(join(original, "cases.json"), JSON.stringify(cases, null, 2))
  await writeHost(original)
  await call("close")
  await call("open", { mode: "open", snapshot: join(original, "batch.json") })
  await runSaved(original)
  if (signature) {
    const { evolveSignature } = await import("./evolve-signature.mjs")
    await evolveSignature({ directory: original, call, exchange, publish, writeHost, runSaved })
  }
  if (evolve) {
    const { evolveV2 } = await import("./evolve-v2.mjs")
    await evolveV2({ directory: original, library, call, exchange, publish, writeHost, runSaved })
  }
  await call("close")
  await cp(original, moved, { recursive: true, errorOnExist: true, force: false })
  await rename(library, library + ".unavailable")
  await call("open", { mode: "open", snapshot: join(moved, "batch.json") })
  assert.equal((await call("inspect", { view: "function", function: "missingLine" })).function.id, after.function.id)
  await runSaved(moved)
  console.log(`PASS: ordinary authoring, maintenance, saved tests, reopen and moved dependency pin. ${output}`)
  }
} finally { await call("close") }
