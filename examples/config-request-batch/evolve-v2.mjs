// A bounded maintenance exercise, using the existing upgrade and migration APIs.
import assert from "node:assert/strict"
import { cp, readFile, rename, writeFile } from "node:fs/promises"
import { join } from "node:path"

const string = { kind: "primitive", primitive: "string" }
const int64 = { kind: "primitive", primitive: "int64" }
const strings = { kind: "builtinGeneric", generic: "list", arguments: [string] }
const text = value => ({ kind: "stringLiteral", value })
const integer = value => ({ kind: "int64Literal", value: String(value) })
const pattern = (slot = "value") => ({ kind: "patternReference", slot, scopeDepth: 0 })
const binary = (kind, left, right) => ({ kind, left, right })
const concat = (...items) => items.reduce((left, right) => binary("stringConcat", left, right))
const choose = (condition, whenTrue, whenFalse) => ({ kind: "conditional", condition, whenTrue, whenFalse })

export async function evolveV2({ directory, library, call, exchange, publish, writeHost, runSaved }) {
  const snapshot = join(directory, "batch.json"), casesPath = join(directory, "cases.json")
  const evidence = { stages: [], refusals: [], atomicity: "Dependency upgrade and local field migration are separate publications." }
  const pair = () => Promise.all([readFile(snapshot), readFile(snapshot + ".dependency.json")])
  const inspect = functionName => call("inspect", { view: "function", function: functionName })
  const open = () => call("open", { mode: "open", snapshot })
  async function stage(name) {
    evidence.stages.push({ name, ...(await call("inspect", { view: "status" })) })
    console.log(`v2: ${name}`)
  }
  async function unchanged(before, label) { assert.deepEqual(await pair(), before, label) }
  async function refused(op, args, code) {
    const before = await pair()
    const response = await exchange("request", { op, arguments: args })
    assert.equal(response.ok, false, JSON.stringify(response))
    assert.ok(JSON.stringify(response.error).includes(code), JSON.stringify(response.error))
    await unchanged(before, code)
    evidence.refusals.push({ operation: op, expectedCode: code, error: response.error })
  }
  async function refusedUpgrade(source, fingerprint, code) {
    const before = await pair()
    await assert.rejects(call("open", { mode: "upgrade", snapshot, dependency: source, expectedFingerprint: fingerprint }), error => {
      assert.match(error.message, new RegExp(code))
      evidence.refusals.push({ operation: "upgrade", expectedCode: code, message: error.message })
      return true
    })
    await unchanged(before, code)
  }
  function identities(declarations) {
    return Object.fromEntries(declarations.functions.map(fn => [fn.displayName, { id: fn.id, parameters: fn.parameters.map(p => p.id) }]))
  }
  function labels(read) { return read.function.body.nodes.filter(n => n.kind.kind === "stringLiteral").map(n => n.kind.value) }

  await cp(casesPath, join(directory, "cases.v1.json"))
  const baseline = await call("inspect", { view: "declarations" })
  const originalIdentities = identities(baseline)
  const originalStatus = await call("inspect", { view: "status" })
  const imported = baseline.functions.find(fn => fn.displayName === "normalizeValue")
  const libraryV1 = library + ".v1.json", incompatible = library + ".incompatible.json"
  await call("close")
  await cp(library, libraryV1)
  // Revise the original upstream path. The consumer must continue using its v1 pin.
  await call("open", { mode: "open", snapshot: library })
  await publish(await call("replace", { function: "normalizeValue", replacement: concat(text("v2:"), { kind: "stringToUpper", value: { kind: "parameter", name: "value" } }) }))
  assert.equal((await inspect("normalizeValue")).function.id, imported.id)
  await call("close")
  await cp(library, incompatible)
  await call("open", { mode: "open", snapshot: incompatible })
  // A valid standalone v3 whose changed result type is incompatible with this consumer.
  await publish(await call("request", { op: "simulateResultTypeMigration", arguments: {
    targetFunctionId: imported.id, newResultType: { kind: "builtinGeneric", generic: "result", arguments: [string, string] },
    targetReplacement: { kind: "resultOk", value: text("incompatible"), errorType: string }, callerRewrites: [],
  } }))
  await call("close")
  await open()
  await runSaved(directory)
  await stage("old pin still runs v1 after upstream edits")

  // A local edit precedes the upgrade and must survive every later step.
  const beforeEdit = await inspect("missingLine")
  const label = beforeEdit.function.body.nodes.find(n => n.kind.kind === "stringLiteral" && n.kind.value === "missing ")
  assert.ok(label)
  await publish(await call("replace", { function: "missingLine", targetNodeId: label.id, replacement: text("skipped ") }))
  const savedCases = JSON.parse(await readFile(casesPath, "utf8"))
  const retry = savedCases.find(c => c.id === "retry-and-missing")
  const contents = c => c.expected.value.fields.find(f => f.field === "contents").value
  contents(retry).value = "ok alpha: A\nskipped beta: missing\nok gamma: C\n"
  await writeFile(casesPath, JSON.stringify(savedCases, null, 2))
  await cp(casesPath, join(directory, "cases.v1-local.json"))
  const preview = await call("request", { op: "previewDependencyUpgrade", arguments: { source: library } })
  assert.equal(preview.compatible, true)
  assert.ok(preview.changed.some(item => item.id === imported.id))
  const breaking = await call("request", { op: "previewDependencyUpgrade", arguments: { source: incompatible } })
  assert.equal(breaking.compatible, false)
  assert.ok(breaking.issues.length)
  await call("close")
  await refusedUpgrade(incompatible, breaking.newFingerprint, "WorkspaceValidationFailure")
  // Change the actual reviewed source, not just the supplied fingerprint.
  await call("open", { mode: "open", snapshot: library })
  await publish(await call("create_function", { displayName: "libraryVersion", parameters: [], resultType: string,
    requiredEffects: [], body: text("v2") }))
  await call("close")
  await refusedUpgrade(library, preview.newFingerprint, "DependencyFailure")
  await open()
  assert.ok(labels(await inspect("missingLine")).includes("skipped "))
  await runSaved(directory)
  await stage("refused upgrades preserve v1 pin and committed local edit")
  const reviewed = await call("request", { op: "previewDependencyUpgrade", arguments: { source: library } })
  assert.equal(reviewed.compatible, true)
  assert.notEqual(reviewed.newFingerprint, preview.newFingerprint)
  await call("close")
  await call("open", { mode: "upgrade", snapshot, dependency: library, expectedFingerprint: reviewed.newFingerprint })
  const upgraded = await call("inspect", { view: "declarations" })
  for (const [name, identity] of Object.entries(originalIdentities)) assert.deepEqual(identities(upgraded)[name], identity, name)
  const afterEdit = await inspect("missingLine")
  assert.ok(afterEdit.function.body.nodes.some(n => n.id === label.id && n.kind.value === "skipped "))
  assert.deepEqual(afterEdit.function.body.nodes.filter(n => n.id !== label.id), beforeEdit.function.body.nodes.filter(n => n.id !== label.id))
  for (const c of savedCases.filter(c => ["success", "retry-and-missing"].includes(c.id))) {
    contents(c).value = c.id === "success" ? "ok alpha: v2:A\nok beta: v2:B\nok gamma: v2:C\n" : "ok alpha: v2:A\nskipped beta: missing\nok gamma: v2:C\n"
  }
  await writeFile(casesPath, JSON.stringify(savedCases, null, 2))
  await writeHost(directory)
  await runSaved(directory)
  await stage("compatible upgrade published independently; old summary type still works")

  // The maintained migration protocol uses exact IDs. Bind only the two affected
  // bodies using IDs obtained from declarations; this is not a general translator.
  const fn = name => upgraded.functions.find(f => f.displayName === name)
  const type = name => upgraded.types.find(t => t.displayName === name)
  const fieldId = (owner, name) => type(owner).typeKind.fields.find(f => f.displayName === name).id
  const paramId = (owner, name) => fn(owner).parameters.find(p => p.displayName === name).id
  const p = (owner, name) => ({ kind: "parameter", parameterId: paramId(owner, name) })
  const field = (source, owner, name) => ({ kind: "fieldAccess", source, fieldId: fieldId(owner, name) })
  const invoke = (name, values) => ({ kind: "directCall", functionId: fn(name).id,
    arguments: Object.entries(values).map(([key, value]) => ({ parameterId: paramId(name, key), value })) })
  const summaryType = { kind: "named", typeId: type("BatchSummary").id }
  const summary = (succeeded, failed, contents) => ({ kind: "productConstruct", typeId: type("BatchSummary").id,
    fields: Object.entries({ succeeded, failed, contents }).map(([name, value]) => ({ fieldId: fieldId("BatchSummary", name), value })) })
  const err = error => ({ kind: "resultErr", error, okType: summaryType })
  const collect = "collectRequest", process = "processConfig"
  const acc = name => field(p(collect, "accumulator"), "BatchSummary", name)
  const collectBody = choose({ kind: "cancellationRequested" }, p(collect, "accumulator"),
    { kind: "matchResult", input: invoke("fetchWithRetry", { files: p(collect, "files"), handle: p(collect, "handle"),
      request: p(collect, "request"), attempt: integer(1), limit: p(collect, "limit") }),
      okBody: summary(binary("int64WrappingAdd", acc("succeeded"), integer(1)), acc("failed"), concat(acc("contents"), text("ok "), p(collect, "request"), text(": "), pattern(), text("\n"))),
      errBody: summary(acc("succeeded"), { kind: "listAppendItem", list: acc("failed"), item: concat(p(collect, "request"), text(": "), pattern("error")) },
        concat(acc("contents"), invoke("missingLine", { request: p(collect, "request"), error: pattern("error") }))) })
  const config = name => field(p(process, "config"), "BatchConfig", name)
  const processBody = choose(binary("int64LessThan", config("maxAttempts"), integer(1)), err(text("maxAttempts must be positive")),
    { kind: "bind", value: { kind: "listEffectFold", source: config("requests"), seed: summary(integer(0), { kind: "listConstruct", itemType: string, items: [] }, text("")),
      functionId: fn(collect).id, context: [ ["files", p(process, "files")], ["handle", p(process, "handle")], ["limit", config("maxAttempts")] ].map(([name, value]) => ({ parameterId: paramId(collect, name), value })) },
      body: invoke("saveSummary", { files: p(process, "files"), handle: p(process, "handle"), path: config("output"), summary: pattern() }) })
  const migration = { fieldId: fieldId("BatchSummary", "failed"), newFieldType: strings,
    functionRewrites: [ { functionId: fn(collect).id, replacement: collectBody }, { functionId: fn(process).id, replacement: processBody } ], detail: "full" }
  const unaffected = await Promise.all(["runBatch", "saveSummary", "cancelBatch"].map(async name => [name, (await inspect(name)).function]))
  await refused("simulateProductFieldTypeMigration", { ...migration, functionRewrites: migration.functionRewrites.slice(0, 1) }, "FieldTypeMigrationRewriteMismatch")
  await refused("simulateProductFieldTypeMigration", { ...migration, expectedSnapshotId: originalStatus.snapshotId }, "FieldTypeMigrationExpectedBaseMismatch")
  await refused("simulateProductFieldTypeMigration", { ...migration, functionRewrites: [migration.functionRewrites[0], { functionId: fn(process).id, replacement: text("wrong result type") }] }, "FieldTypeMigrationRewriteInvalid")
  const persisted = await pair()
  const pending = await call("request", { op: "simulateProductFieldTypeMigration", arguments: migration })
  assert.equal(pending.review.readiness.kind, "semanticallyReady")
  await unchanged(persisted, "uncommitted proposal")
  await call("close")
  await open()
  await unchanged(persisted, "close/reopen discards only proposal")
  await refused("commit", { proposalId: pending.proposalId }, "ProposalNotFound")
  const reopened = await call("inspect", { view: "declarations" })
  assert.deepEqual(reopened.types.find(t => t.id === type("BatchSummary").id).typeKind.fields.find(f => f.id === migration.fieldId).type, int64)
  await runSaved(directory)
  await stage("missing/stale/invalid rewrites refused; pending migration discarded on reopen")
  const proposal = await call("request", { op: "simulateProductFieldTypeMigration", arguments: migration })
  assert.equal(proposal.review.kind, "productFieldTypeMigration")
  assert.deepEqual(proposal.review.diff.fieldChange, { typeId: type("BatchSummary").id, fieldId: migration.fieldId, oldType: int64, newType: strings })
  assert.deepEqual(new Set(proposal.review.rewrittenFunctions.map(f => f.id)), new Set([fn(collect).id, fn(process).id]))
  await publish(proposal)
  const migrated = await call("inspect", { view: "declarations" })
  assert.deepEqual(identities(migrated), identities(upgraded))
  const migratedSummary = migrated.types.find(t => t.id === type("BatchSummary").id)
  assert.deepEqual(migratedSummary.typeKind.fields.map(f => f.id), type("BatchSummary").typeKind.fields.map(f => f.id))
  assert.deepEqual(migratedSummary.typeKind.fields.find(f => f.id === migration.fieldId).type, strings)
  assert.deepEqual((await inspect("missingLine")).function, afterEdit.function)
  for (const [name, original] of unaffected) assert.deepEqual((await inspect(name)).function, original, name)
  for (const c of savedCases.filter(c => ["success", "retry-and-missing"].includes(c.id))) {
    c.expected.value.fields.find(f => f.field === "failed").value = { kind: "list", itemKind: "string", items: c.id === "success" ? [] : ["beta: missing"] }
  }
  await writeFile(casesPath, JSON.stringify(savedCases, null, 2))
  await writeHost(directory)
  await call("close")
  await open()
  await runSaved(directory)
  await stage("field migration published independently; v2 details and cleanup survive reopen")
  await writeFile(join(directory, "evolution.json"), JSON.stringify({ ...evidence, identities: identities(migrated), fieldId: migration.fieldId,
    localLabelNodeId: label.id, unchangedFunctions: unaffected.map(([name]) => name) }, null, 2))
  await rename(libraryV1, libraryV1 + ".unavailable")
  await rename(incompatible, incompatible + ".unavailable")
  console.log("PASS: v2 dependency upgrade, local field migration and refusal/recovery checks.")
}
