import test from "node:test"
import assert from "node:assert/strict"
import { mkdir, mkdtemp, rm, stat } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { randomUUID } from "node:crypto"
import { openAuthoring, requestAuthoring, closeAuthoring } from "../lib/nyrwvy-authoring.ts"

// Deterministic tool-client tests, not a second authoring path or live-model evaluation.
const primitive = primitive => ({ kind: "primitive", primitive })
const named = name => ({ kind: "named", name })
const parameter = name => ({ kind: "parameter", name })
const bool = value => ({ kind: "boolLiteral", value })
const conditional = (condition, whenTrue, whenFalse) => ({ kind: "conditional", condition, whenTrue, whenFalse })
const construct = variant => ({ kind: "unionConstruct", type: "VerificationLevel", variant, fields: [] })
const field = field => ({ kind: "fieldAccess", source: parameter("input"), type: "ChangeRequest", field })
const match = (type, arms) => ({ kind: "matchUnion", type, input: parameter("input"), arms })
const union = (displayName, variants) => ({ op: "simulateCreateTypeByName", displayName,
  typeKind: { kind: "union", variants: variants.map(displayName => ({ displayName, fields: [] })) } })
const fn = (displayName, input, resultType, body) => ({ op: "simulateCreateFunctionByName", displayName,
  parameters: [{ displayName: "input", type: input }], resultType, requiredEffects: [], body })

async function request(context, payload) {
  const reply = JSON.parse(await requestAuthoring(payload, context))
  assert.equal(reply.ok, true, JSON.stringify(reply.error))
  return reply.result
}
async function publish(context, payload) {
  const proposal = await request(context, payload)
  assert.ok(proposal.proposalId)
  assert.equal(proposal.review.readiness.kind, "semanticallyReady")
  await request(context, { op: "commit", proposalId: proposal.proposalId })
  return proposal
}
async function workspace(run) {
  const directory = await mkdtemp(join(tmpdir(), "nyrwvy-tools-"))
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const snapshot = join(directory, "workspace.json")
  try {
    await openAuthoring("create", snapshot, context)
    await run(context, snapshot)
  } finally {
    await closeAuthoring(context).catch(() => undefined)
    await rm(directory, { recursive: true, force: true })
  }
}
function arm(graph, variantId) {
  const match = graph.nodes.find(node => node.kind.kind === "matchUnion")
  assert.ok(match)
  return match.kind.arms.find(arm => arm.variantId === variantId).body
}
function resultNode(graph) { return graph.nodes.find(node => node.id === graph.resultNodeId) }

test("a cancelled open does not create a snapshot or reserve a session", { timeout: 10000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "nyrwvy-cancelled-open-"))
  const snapshot = join(directory, "workspace.json")
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  try {
    await assert.rejects(openAuthoring("create", snapshot, { ...context, abort: AbortSignal.abort() }), /cancelled|aborted/i)
    await assert.rejects(stat(snapshot), { code: "ENOENT" })
    await openAuthoring("create", snapshot, context)
    assert.ok((await request(context, { op: "status" })).snapshotId)
  } finally {
    await closeAuthoring(context).catch(() => undefined)
    await rm(directory, { recursive: true, force: true })
  }
})

test("authoring sessions are isolated by both worktree and session ID", { timeout: 15000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-isolated-tools-"))
  const sessionID = randomUUID()
  const contexts = [
    { sessionID, worktree: join(root, "first"), repositoryRoot: process.cwd() },
    { sessionID, worktree: join(root, "second"), repositoryRoot: process.cwd() },
    { sessionID: randomUUID(), worktree: join(root, "first"), repositoryRoot: process.cwd() },
  ]
  try {
    await Promise.all([mkdir(contexts[0].worktree), mkdir(contexts[1].worktree)])
    await Promise.all(contexts.map((context, index) => openAuthoring("create", `${index}.json`, context)))
    const snapshots = await Promise.all(contexts.map(context => request(context, { op: "status" })))
    assert.equal(new Set(snapshots.map(status => status.snapshotId)).size, 3)
    await closeAuthoring(contexts[0])
    assert.deepEqual(await request(contexts[1], { op: "status" }), snapshots[1])
    assert.deepEqual(await request(contexts[2], { op: "status" }), snapshots[2])
  } finally {
    await Promise.all(contexts.map(context => closeAuthoring(context).catch(() => undefined)))
    await rm(root, { recursive: true, force: true })
  }
})

test("ordinary tool bridge authors Verification Planner and maintains one branch across reopen", { timeout: 30000 }, async () => {
  await workspace(async (context, snapshot) => {
    const areas = ["Documentation", "Semantics", "AuthoringProtocol", "Backend", "Persistence", "EvaluationHarness", "CI"]
    const levels = ["FastOnly", "FastExtended", "FastLive", "FastExtendedLive"]
    await publish(context, union("ChangeArea", areas))
    await publish(context, { op: "simulateCreateTypeByName", displayName: "ChangeRequest", typeKind: { kind: "product", fields: [
      { displayName: "areas", type: { kind: "builtinGeneric", generic: "list", arguments: [named("ChangeArea")] } },
      ...["agentInteraction", "environmentDependent", "knownRegression"].map(displayName => ({ displayName, type: primitive("bool") })),
    ] } })
    await publish(context, union("VerificationLevel", levels))
    await publish(context, fn("area_requires_extended", named("ChangeArea"), primitive("bool"), match("ChangeArea",
      areas.map(variant => ({ variant, body: bool(["Semantics", "Backend", "Persistence"].includes(variant)) })))))
    await publish(context, fn("requires_live", named("ChangeRequest"), primitive("bool"),
      conditional(field("agentInteraction"), bool(true), field("environmentDependent"))))
    const live = { kind: "directCall", function: "requires_live", arguments: [{ parameter: "input", value: parameter("input") }] }
    const extended = { kind: "listAny", source: field("areas"), function: "area_requires_extended" }
    await publish(context, fn("plan_verification", named("ChangeRequest"), named("VerificationLevel"), conditional(extended,
      conditional(live, construct("FastExtendedLive"), construct("FastExtended")),
      conditional(live, construct("FastLive"), construct("FastOnly")))))
    const labels = ["fast tests のみ", "fast + extended", "fast + live", "fast + extended + live"]
    await publish(context, fn("verification_label", named("VerificationLevel"), primitive("string"), match("VerificationLevel",
      levels.map((variant, index) => ({ variant, body: { kind: "stringLiteral", value: labels[index] } })))))

    const declarations = await request(context, { op: "declarations" })
    assert.equal(declarations.types.length, 3)
    assert.equal(declarations.functions.length, 4)
    const variantId = declarations.types.find(t => t.displayName === "ChangeArea").typeKind.variants.find(v => v.displayName === "EvaluationHarness").id
    const before = await request(context, { op: "readFunction", function: "area_requires_extended" })
    assert.equal(before.callers.length, 1)
    const target = resultNode(arm(before.function.body, variantId))
    assert.equal(target.kind.value, false)
    const payload = { op: "simulateReplaceTargetByName", function: "area_requires_extended", targetNodeId: target.id, replacement: bool(true) }
    // The default review is compact: impact facts and a function summary, no per-node graph.
    const compact = await request(context, payload)
    assert.equal(compact.detail, "compact")
    assert.equal(compact.review.proposedFocus.function.body.nodes, undefined)
    assert.equal(compact.review.proposedFocus.function.body.nodeCount, compact.review.baseFocus.function.body.nodeCount)
    assert.ok(compact.review.proposedFocus.function.body.nodeCount > before.function.body.nodes.length)
    assert.equal(compact.review.proposedFocus.callers.length, 1)
    assert.equal(compact.review.diff.targetNodeId, target.id)
    await request(context, { op: "reject", proposalId: compact.proposalId })
    const proposal = await request(context, { ...payload, detail: "full" })
    assert.equal(proposal.detail, "full")
    const stale = await request(context, payload)
    assert.equal(resultNode(arm(proposal.review.baseFocus.function.body, variantId)).kind.value, false)
    assert.equal(resultNode(arm(proposal.review.proposedFocus.function.body, variantId)).kind.value, true)
    assert.equal((await request(context, { op: "status" })).snapshotId, before.snapshotId)
    await request(context, { op: "commit", proposalId: proposal.proposalId })

    const staleReply = JSON.parse(await requestAuthoring({ op: "commit", proposalId: stale.proposalId }, context))
    assert.equal(staleReply.ok, false)
    assert.ok(staleReply.error.details.diagnostics.some(d => d.code === "StaleBaseSnapshot"))
    const after = await request(context, { op: "readFunction", functionId: before.function.id })
    assert.equal(after.function.id, before.function.id)
    assert.deepEqual(after.function.parameters, before.function.parameters)
    assert.equal(after.function.body.resultNodeId, before.function.body.resultNodeId)
    const beforeMatch = resultNode(before.function.body).kind
    const afterMatch = resultNode(after.function.body).kind
    assert.deepEqual(afterMatch.arms.filter(a => a.variantId !== variantId), beforeMatch.arms.filter(a => a.variantId !== variantId))
    assert.equal(resultNode(arm(after.function.body, variantId)).kind.value, true)

    const discarded = await request(context, { op: "simulateReplaceBodyByName", function: "area_requires_extended", replacement: bool(false), detail: "full" })
    assert.equal(discarded.review.proposedFunction.body.nodes[0].kind.value, false)
    await request(context, { op: "reject", proposalId: discarded.proposalId })
    assert.equal((await request(context, { op: "status" })).snapshotId, after.snapshotId)
    assert.equal((await request(context, { op: "executionReadiness" })).kind, "ready")
    const artifact = await request(context, { op: "emitCSharp", profile: { kind: "empty" } })
    assert.equal(artifact.symbols.functions.length, 4)

    await closeAuthoring(context)
    await openAuthoring("open", snapshot, context)
    const resumed = await request(context, { op: "readFunction", functionId: before.function.id })
    assert.equal(resumed.snapshotId, after.snapshotId)
    assert.deepEqual(resumed.function, after.function)
    assert.deepEqual(resumed.callers, after.callers)
  })
})

test("structured reads preserve exact literals and duplicate display names are refused on every creation route", { timeout: 15000 }, async () => {
  await workspace(async context => {
    const payload = { op: "simulateCreateFunctionByName", displayName: "exact", parameters: [], resultType: primitive("int64"),
      requiredEffects: [], body: { kind: "int64Literal", value: "9223372036854775807" }, detail: "full" }
    const first = await publish(context, payload)
    const firstId = first.review.createdFunction.id
    assert.equal(resultNode(first.review.createdFunction.body).kind.value, "9223372036854775807")
    const beforeDuplicate = await request(context, { op: "status" })
    const duplicate = JSON.parse(await requestAuthoring(payload, context))
    assert.equal(duplicate.ok, false)
    assert.match(duplicate.error.message, /already exists/)
    assert.deepEqual(await request(context, { op: "status" }), beforeDuplicate)
    assert.equal((await request(context, { op: "declarations" })).functions.length, 1)
    // Identity-based creation refuses the duplicate too, so a retried create cannot
    // make the name ambiguous for later by-name requests.
    const exactDuplicate = JSON.parse(await requestAuthoring({ ...payload, op: "simulateCreateFunction" }, context))
    assert.equal(exactDuplicate.ok, false)
    assert.equal(exactDuplicate.error.details.diagnostics[0].code, "FunctionCreationDuplicateDisplayName")
    const before = await request(context, { op: "status" })
    assert.deepEqual(before, beforeDuplicate)
    const byName = await request(context, { op: "readFunction", function: "exact" })
    assert.equal(byName.function.id, firstId)
    const exact = await request(context, { op: "readFunction", functionId: firstId })
    assert.equal(resultNode(exact.function.body).kind.value, "9223372036854775807")
    assert.deepEqual(await request(context, { op: "status" }), before)
    const legacy = await request(context, { op: "focusFunction", functionId: firstId })
    assert.equal(typeof legacy.function.body.nodes[0].kind, "string")
  })
})

test("structured review and read retain nested branch scopes across reopen", { timeout: 15000 }, async () => {
  await workspace(async (context, snapshot) => {
    let body = bool(false)
    // 46 recipe nodes, depth 16. Persistence is within its existing JSON depth;
    // the richer read/review envelopes exceed the serializer's default 64 levels.
    for (let depth = 0; depth < 15; depth++) body = conditional(bool(false), bool(true), body)
    const proposal = await publish(context, { op: "simulateCreateFunctionByName", displayName: "nested", parameters: [],
      resultType: primitive("bool"), requiredEffects: [], body, detail: "full" })
    const id = proposal.review.createdFunction.id
    let graph = proposal.review.createdFunction.body
    for (let depth = 0; depth < 15; depth++) {
      const root = resultNode(graph).kind
      assert.equal(root.kind, "conditional")
      graph = root.whenFalse
    }
    assert.equal(resultNode(graph).kind.value, false)
    await closeAuthoring(context)
    await openAuthoring("open", snapshot, context)
    assert.deepEqual((await request(context, { op: "readFunction", functionId: id })).function, proposal.review.createdFunction)
  })
})
