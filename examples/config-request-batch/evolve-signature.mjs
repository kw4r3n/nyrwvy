import assert from "node:assert/strict"
import { cp, mkdir, readFile, writeFile } from "node:fs/promises"
import { join } from "node:path"

const string = { kind: "primitive", primitive: "string" }
const p = name => ({ kind: "parameter", name })
const text = value => ({ kind: "stringLiteral", value })
const pattern = (slot = "value") => ({ kind: "patternReference", slot, scopeDepth: 0 })
const field = variant => ({ kind: "patternReference", slot: "field", scopeDepth: 0, type: "FetchFailure", variant, field: "message" })
const err = error => ({ kind: "resultErr", error, okType: string })
const choose = (condition, whenTrue, whenFalse) => ({ kind: "conditional", condition, whenTrue, whenFalse })
const binary = (kind, left, right) => ({ kind, left, right })
const direct = (functionName, values) => ({ kind: "directCall", function: functionName, arguments: Object.entries(values).map(([parameter, value]) => ({ parameter, value })) })

export async function evolveSignature({ directory, call, exchange, publish, writeHost, runSaved }) {
  const snapshot = join(directory, "batch.nyrec")
  const persisted = () => Promise.all([readFile(snapshot), readFile(snapshot + ".dependency.json")])
  const before = await call("inspect", { view: "declarations" })
  const operation = before.capabilities.find(c => c.displayName === "OpenFile").operations.find(o => o.displayName === "fetch")
  const fn = before.functions.find(f => f.displayName === "fetchWithRetry")
  const runString = value => ({ kind: "string", value })
  const runResult = (isOk, value, errorType = string) => errorType === string
    ? ({ kind: "result", okKind: "string", errorKind: "string", isOk, value: value.value })
    : ({ kind: "result", okType: string, errorType, isOk, value })
  const responses = values => values.map(value => ({ kind: "return", value }))
  async function fakeCase(label, values, expected, attempts) {
    const reply = await call("request", { op: "runPureByName", arguments: {
      function: "fetchWithRetry", arguments: [{ kind: "fakeCapability", capability: "BatchFiles", operations: [] }, { kind: "fakeCapability", capability: "OpenFile", operations: [{ operation: "fetch", responses: responses(values) }] }, runString("request"), { kind: "int64", value: "1" }, { kind: "int64", value: "2" }], expected,
    } })
    assert.equal(reply.matches, true, JSON.stringify(reply))
    assert.equal(reply.calls.filter(c => c.operation === "fetch").length, attempts)
    return { label, values, expected, attempts }
  }
  const baselineFake = await fakeCase("old string missing retries", [runResult(false, runString("missing")), runResult(true, runString("ok"))], runResult(true, runString("OK")), 2)
  await cp(join(directory, "cases.json"), join(directory, "cases.before-signature.json"))
  await publish(await call("create_type", { displayName: "FetchFailure", typeKind: { kind: "union", variants: ["Retryable", "Permanent"].map(displayName => ({ displayName, fields: [{ displayName: "message", type: string }] })) } }))
  const body = choose({ kind: "cancellationRequested" }, err(text("cancelled")), {
    kind: "matchResult",
    input: { kind: "capabilityInvoke", capabilityValue: p("handle"), capability: "OpenFile", operation: "fetch",
      arguments: ["request", "attempt"].map(parameter => ({ parameter, value: p(parameter) })) },
    okBody: { kind: "resultOk", value: direct("normalizeValue", { value: pattern() }), errorType: string },
    errBody: { kind: "matchUnion", type: "FetchFailure", input: pattern("error"), arms: [
      { variant: "Retryable", body: choose(binary("int64LessThan", p("attempt"), p("limit")),
        { kind: "directCall", function: "fetchWithRetry", keepUnlisted: true,
          arguments: [{ parameter: "attempt", value: binary("int64WrappingAdd", p("attempt"), { kind: "int64Literal", value: "1" }) }] }, err(field("Retryable"))) },
      { variant: "Permanent", body: err(field("Permanent")) },
    ] },
  })
  const request = {
    capability: "OpenFile", operation: "fetch",
    parameterTypes: operation.parameters.map(p => ({ parameter: p.displayName, type: p.type })),
    resultType: { kind: "builtinGeneric", generic: "result", arguments: [string, { kind: "named", name: "FetchFailure" }] },
    functionRepairs: [{ function: fn.displayName, parameterTypes: fn.parameters.map(p => ({ parameter: p.displayName, type: p.type })), resultType: fn.resultType, replacement: body }],
  }
  // By-name types must refer to capabilities by name; inspection exposes stable IDs.
  request.functionRepairs[0].parameterTypes[0].type = { kind: "capability", name: "BatchFiles" }
  request.functionRepairs[0].parameterTypes[1].type = { kind: "capability", name: "OpenFile" }
  const evidence = { refusals: [], atomicity: "Graph declaration and repairs publish together; host boundary deployment is explicit." }
  async function refused(arguments_, code) {
    const bytes = await persisted()
    const response = await exchange("request", { op: "simulateCapabilityOperationSignatureMigrationByName", arguments: arguments_ })
    assert.equal(response.ok, false, JSON.stringify(response))
    assert.ok(JSON.stringify(response.error).includes(code), JSON.stringify(response))
    assert.deepEqual(await persisted(), bytes)
    evidence.refusals.push(code)
  }
  await refused({ ...request, functionRepairs: [] }, "RepairMismatch")
  await refused({ ...request, functionRepairs: [{ ...request.functionRepairs[0], replacement: text("invalid") }] }, "RepairInvalid")
  await refused({ ...request, expectedSnapshotId: (await call("inspect", { view: "status" })).snapshotId + "-stale" }, "ExpectedBaseMismatch")
  const pending = await call("request", { op: "simulateCapabilityOperationSignatureMigrationByName", arguments: { ...request, detail: "full" } })
  evidence.review = pending.review
  assert.equal(pending.review.rewrittenFunctions.length, 1)
  assert.equal(pending.review.diff.oldOperation.id, operation.id)
  assert.equal(pending.review.diff.newOperation.id, operation.id)
  const bytes = await persisted()
  await call("close")
  await call("open", { mode: "open", snapshot })
  const lost = await exchange("commit", { proposalId: pending.proposalId })
  assert.equal(lost.ok, false)
  assert.deepEqual(await persisted(), bytes)
  await runSaved(directory)
  await publish(await call("request", { op: "simulateCapabilityOperationSignatureMigrationByName", arguments: request }))
  const after = await call("inspect", { view: "declarations" })
  evidence.identities = after.capabilities.map(c => ({ id: c.id, operations: c.operations.map(o => ({ id: o.id, parameters: o.parameters.map(p => p.id) })) }))
  assert.deepEqual(evidence.identities, before.capabilities.map(c => ({ id: c.id, operations: c.operations.map(o => ({ id: o.id, parameters: o.parameters.map(p => p.id) })) })))
  assert.deepEqual(after.functions.map(f => ({ id: f.id, parameters: f.parameters.map(p => p.id) })), before.functions.map(f => ({ id: f.id, parameters: f.parameters.map(p => p.id) })))
  const failureType = { kind: "named", name: "FetchFailure" }
  const failure = (variant, message) => runResult(false, { kind: "union", type: "FetchFailure", variant, fields: [{ field: "message", value: runString(message) }] }, failureType)
  evidence.explicitBoundaryCases = [baselineFake,
    await fakeCase("typed retryable retries even with a different message", [failure("Retryable", "busy"), runResult(true, runString("ok"), failureType)], runResult(true, runString("OK")), 2),
    await fakeCase("permanent missing does not retry", [failure("Permanent", "missing")], runResult(false, runString("missing")), 1),
    await fakeCase("retryable reaches configured limit", [failure("Retryable", "busy"), failure("Retryable", "busy")], runResult(false, runString("busy")), 2),
  ]
  const savedCases = JSON.parse(await readFile(join(directory, "cases.json"), "utf8"))
  const permanent = structuredClone(savedCases.find(c => c.id === "retry-and-missing"))
  permanent.id = "permanent"
  permanent.arguments[0].value = "fixtures/permanent"
  permanent.expected.value.fields.find(f => f.field === "contents").value.value = "ok alpha: A\nmissing beta: denied\nok gamma: C\n"
  savedCases.push(permanent)
  await writeFile(join(directory, "cases.json"), JSON.stringify(savedCases, null, 2))
  const permanentRoot = join(directory, "fixtures/permanent")
  await mkdir(join(permanentRoot, "responses"), { recursive: true })
  await writeFile(join(permanentRoot, "config.json"), JSON.stringify({ requests: ["alpha", "beta", "gamma"], maxAttempts: 2, output: "report.txt" }))
  await writeFile(join(permanentRoot, "responses/alpha.1.txt"), "a")
  await writeFile(join(permanentRoot, "responses/beta.permanent"), "")
  await writeFile(join(permanentRoot, "responses/gamma.1.txt"), "c")
  await writeHost(directory)
  await call("close")
  await call("open", { mode: "open", snapshot })
  await runSaved(directory)
  await writeFile(join(directory, "signature-evolution.json"), JSON.stringify(evidence, null, 2))
}
