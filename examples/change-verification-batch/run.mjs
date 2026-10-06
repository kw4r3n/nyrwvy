// Applies real graph proposals to a copied, existing config-request-batch/v2 bundle.
import assert from "node:assert/strict"
import { createHash } from "node:crypto"
import { execFile } from "node:child_process"
import { appendFile, cp, mkdir, readFile, readdir, writeFile } from "node:fs/promises"
import { join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { promisify } from "node:util"
import * as z from "zod/v4"
import { createGraphTools } from "../../tools/nyrwvy-agent/graph-tools.mjs"

assert.ok(process.argv[2] && process.argv[3], "Usage: run.mjs <v2-bundle> <empty-output>")
const baselineBundle = resolve(process.argv[2]), output = resolve(process.argv[3])
const repositoryRoot = fileURLToPath(new URL("../../", import.meta.url))
const requirementsText = await readFile(new URL("./requirements.json", import.meta.url), "utf8")
const requirements = JSON.parse(requirementsText)
const digest = text => createHash("sha256").update(text).digest("hex")
await mkdir(output, { recursive: true })
assert.deepEqual(await readdir(output), [], "Use an empty output directory")
await writeFile(join(output, "requirements.json"), requirementsText)
const bundle = join(output, "working")
await cp(baselineBundle, bundle, { recursive: true })
const snapshot = join(bundle, "batch.json"), sidecar = snapshot + ".dependency.json"
const pair = () => Promise.all([readFile(snapshot), readFile(sidecar)])
const tools = createGraphTools(z)
const context = { sessionID: `practical-verification-${process.pid}`, worktree: repositoryRoot, allowHostSources: true }
const evidence = { requirementsDigest: digest(requirementsText), runnerDigest: digest(await readFile(new URL(import.meta.url), "utf8")), timings: [], stages: [], manualDecisions: [
  "Use pure existing formatter because full batch uses host/cancellation runtime.",
  "Preserve all formatter behavior for refactor; request CR/LF escaping only for improvement.",
  "Keep nine sampled and two held-out inputs unchanged across candidates.",
  "Preserve literal backslashes and Unicode; no reversible encoding claim.",
  "Treat negative controls as deliberate regressions, not historical bugs.",
  "Check host/cleanup behavior through existing saved tests, never label host equivalence verified."
] }
async function timed(label, action) {
  const start = performance.now()
  try { return await action() } finally { evidence.timings.push({ label, milliseconds: Math.round(performance.now() - start) }) }
}
async function call(name, args = {}) {
  const raw = await tools[name].execute(args, context)
  await appendFile(join(output, "transcript.jsonl"), JSON.stringify({ tool: name, arguments: args, response: raw }) + "\n")
  if (name === "close") return raw
  const response = JSON.parse(raw)
  assert.notEqual(response.ok, false, JSON.stringify(response))
  return response.result ?? response
}
const request = (op, args) => call("request", { op, arguments: args })
const str = value => ({ kind: "string", value })
const text = value => ({ kind: "stringLiteral", value })
const parameter = name => ({ kind: "parameter", name })
const type = { kind: "primitive", primitive: "string" }
const joinText = list => ({ kind: "stringJoin", separator: text(""), list: { kind: "listConstruct", itemType: type, items: list } })
const line = (request = parameter("request"), error = parameter("error"), prefix = "skipped ") => joinText([text(prefix), request, text(": "), error, text("\n")])
const escapeCR = value => ({ kind: "stringJoin", separator: text("\\r"), list: { kind: "stringSplit", value, separator: text("\r") } })
const escape = value => ({ kind: "stringJoin", separator: text("\\n"), list: {
  kind: "stringSplit", separator: text("\n"), value: { kind: "stringJoin", separator: text("\\r"), list: { kind: "stringSplit", value, separator: text("\r") } }
} })
// Independent expected strings are derived from the frozen requirements, before any proposal.
const expected = ({ request, error }, improved) => {
  const normalize = value => improved ? value.replaceAll("\r", "\\r").replaceAll("\n", "\\n") : value
  return `skipped ${normalize(request)}: ${normalize(error)}\n`
}
const casesFor = (inputs, improved, explicit) => inputs.map((input, index) => ({
  label: `${improved ? "CR/LF report requirement" : "preserve skipped report"} ${index + 1}`,
  input, expected: expected(input, improved), explicit
}))
const baselineCases = casesFor(requirements.sampledInputs, false, false)
const intendedCases = casesFor(requirements.sampledInputs, true, true)
const challenges = casesFor(requirements.heldOutInputs, true, false)
const hostInput = requirements.sampledInputs.find(item => item.request.includes("\n") && item.error === "missing")
assert.ok(hostInput)
await writeFile(join(output, "cases.host-report.json"), JSON.stringify({ input: hostInput, before: expected(hostInput, false), intended: expected(hostInput, true) }, null, 2))
await writeFile(join(output, "cases.before.json"), JSON.stringify(baselineCases, null, 2))
await writeFile(join(output, "cases.intended.json"), JSON.stringify(intendedCases, null, 2))
await writeFile(join(output, "cases.held-out.json"), JSON.stringify(challenges, null, 2))
const execute = promisify(execFile)
async function ordinaryReplay(directory, label, shouldPass = true) {
  return timed(label, async () => {
    try {
      const { stdout, stderr } = await execute(process.execPath, ["examples/config-request-batch/run.mjs", "--replay", directory], { cwd: repositoryRoot, timeout: 120000 })
      await writeFile(join(output, `${label}.log`), stdout + stderr)
      assert.equal(shouldPass, true, "Negative control unexpectedly passed ordinary saved cases")
      return { passed: true, cases: 5 }
    } catch (error) {
      if (shouldPass) throw error
      await writeFile(join(output, `${label}.log`), (error.stdout ?? "") + (error.stderr ?? ""))
      assert.match(error.stderr ?? "", /AssertionError/, "Expect semantic assertion failure, not transport/build failure")
      return { passed: false, reason: "existing saved expected report assertion" }
    }
  })
}
let functionId
async function verify(proposal, cases, label) {
  const before = await pair()
  const result = await timed(label, () => request("verifyChange", { proposalId: proposal.proposalId, output: `reviews/${label}.html`, cases: cases.map(item => ({
    label: item.label, functionId, arguments: [str(item.input.request), str(item.input.error)], ...(item.explicit ? { expectedCandidate: str(item.expected) } : {})
  })) }))
  assert.deepEqual(await pair(), before, "Candidate evaluation modified committed files")
  for (const item of result.evidence.cases) {
    assert.deepEqual(item.baseline.calls, [])
    assert.deepEqual(item.candidate.calls, [])
  }
  return result
}
async function ordinaryFormatter(cases) {
  for (const item of cases) {
    const result = await request("runPureByName", { function: "missingLine", arguments: [str(item.input.request), str(item.input.error)], expected: str(item.expected) })
    assert.equal(result.matches, true)
  }
}
async function ordinaryNewlineReport(improved, label) {
  return timed(label, async () => {
    const root = join(bundle, "fixtures", label)
    await mkdir(join(root, "responses"), { recursive: true })
    await writeFile(join(root, "config.json"), JSON.stringify({ requests: [hostInput.request], maxAttempts: 1, output: "report.txt" }))
    const contents = expected(hostInput, improved)
    const summary = { kind: "product", type: "BatchSummary", fields: [
      { field: "succeeded", value: { kind: "int64", value: "0" } },
      { field: "failed", value: { kind: "list", itemKind: "string", items: [`${hostInput.request}: missing`] } },
      { field: "contents", value: str(contents) }
    ] }
    const before = await pair()
    const result = await request("runPureByName", { function: "runBatch", arguments: [str(root), { kind: "hostCapability", capability: "BatchFiles", implementation: "BatchExample.Files", supportsConcurrentCalls: true }], hostSources: ["Host.cs"], expected: { kind: "result", okType: { kind: "named", name: "BatchSummary" }, errorType: type, isOk: true, value: summary } })
    assert.equal(result.matches, true)
    assert.equal(await readFile(join(root, "report.txt"), "utf8"), contents)
    const trace = (await readFile(join(root, "trace.txt"), "utf8")).trim().split("\n")
    assert.equal(trace.filter(item => item === "close disposed").length, 1)
    assert.equal(trace.at(-1), "close disposed")
    assert.deepEqual(await pair(), before)
    return { passed: true, physicalLFCount: contents.split("\n").length - 1, result }
  })
}
async function saveStage(name) {
  await call("close")
  const directory = join(output, name)
  await cp(bundle, directory, { recursive: true })
  await call("open", { mode: "open", snapshot })
  const declarations = await call("inspect", { view: "declarations" })
  const projection = await request("emitCSharp", { profile: { kind: "externalImplementations", operationIds: declarations.capabilities.flatMap(c => c.operations.map(op => op.id)), concurrentCapabilityIds: declarations.capabilities.map(c => c.id) } })
  await writeFile(join(directory, "Generated.cs"), projection.source)
  await writeFile(join(bundle, "Generated.cs"), projection.source)
  await writeFile(join(directory, "symbols.json"), JSON.stringify(projection.symbols, null, 2))
  await writeFile(join(bundle, "symbols.json"), JSON.stringify(projection.symbols, null, 2))
  return directory
}
try {
  await call("open", { mode: "open", snapshot })
  const before = await call("inspect", { view: "function", function: "missingLine" })
  functionId = before.function.id
  const pinned = await readFile(sidecar)
  await timed("baseline-frozen-formatter-oracles", () => ordinaryFormatter(baselineCases))
  const baseline = await saveStage("baseline")
  evidence.baselineSavedCases = await ordinaryReplay(baseline, "ordinary-baseline")
  evidence.baselineNewlineReport = await ordinaryNewlineReport(false, "host-newline-before")
  const refactor = await call("replace", { function: "missingLine", replacement: line() })
  const refactorResult = await verify(refactor, baselineCases, "refactor")
  assert.ok(refactorResult.evidence.cases.every(item => item.status === "tested-agreement"))
  await call("commit", { proposalId: refactor.proposalId })
  const after = await call("inspect", { view: "function", function: "missingLine" })
  assert.equal(after.function.id, functionId)
  assert.deepEqual(after.function.parameters, before.function.parameters)
  assert.deepEqual(after.function.resultType, before.function.resultType)
  assert.deepEqual(after.function.requiredEffects, before.function.requiredEffects)
  assert.ok(after.function.body.nodes.length < before.function.body.nodes.length)
  assert.deepEqual(await readFile(sidecar), pinned)
  await timed("ordinary-refactor-formatter-oracles", () => ordinaryFormatter(baselineCases))
  const refactored = await saveStage("refactored")
  const ordinaryRefactor = await ordinaryReplay(refactored, "ordinary-refactor")
  const refactorNewlineReport = await ordinaryNewlineReport(false, "host-newline-refactor")
  evidence.stages.push({ name: "real refactor", evidence: refactorResult, nodeCounts: { before: before.function.body.nodes.length, after: after.function.body.nodes.length }, ordinary: ordinaryRefactor, newlineHost: refactorNewlineReport })
  const improvedBody = line(escape(parameter("request")), escape(parameter("error")))
  const improvement = await call("replace", { function: "missingLine", replacement: improvedBody })
  const preservationFeedback = await verify(improvement, baselineCases, "improvement-preservation-control")
  assert.ok(preservationFeedback.evidence.cases.some(item => item.status === "counterexample"))
  const improvementFeedback = await verify(improvement, intendedCases, "improvement-explicit-intent")
  assert.ok(improvementFeedback.evidence.cases.every(item => item.status === "intended-change-tested"))
  const declarations = await call("inspect", { view: "declarations" })
  const savedHostCase = JSON.parse(await readFile(join(bundle, "cases.json"), "utf8"))[0]
  const wholeProgram = await request("verifyChange", { proposalId: improvement.proposalId, output: "reviews/whole-program-unknown.html", cases: [{ label: "real host program comparison remains unsupported", functionId: declarations.functions.find(fn => fn.displayName === "runBatch").id, arguments: savedHostCase.arguments.map(value => value.kind !== "hostCapability" ? value : { kind: value.kind, capabilityId: declarations.capabilities.find(cap => cap.displayName === value.capability).id, implementation: value.implementation, supportsConcurrentCalls: value.supportsConcurrentCalls }) }] })
  assert.equal(wholeProgram.evidence.cases[0].status, "unknown")
  await call("commit", { proposalId: improvement.proposalId })
  assert.deepEqual(await readFile(sidecar), pinned)
  await timed("ordinary-improvement-formatter-oracles", () => ordinaryFormatter(intendedCases))
  await timed("ordinary-held-out-formatter-oracles", () => ordinaryFormatter(challenges))
  const improved = await saveStage("improved")
  const ordinaryImprovement = await ordinaryReplay(improved, "ordinary-improvement")
  const improvementNewlineReport = await ordinaryNewlineReport(true, "host-newline-improved")
  assert.equal(improvementNewlineReport.physicalLFCount, 1)
  evidence.stages.push({ name: "real report improvement", preservationFeedback, explicitIntent: improvementFeedback, wholeProgram, ordinary: ordinaryImprovement, newlineHost: improvementNewlineReport })
  // All negative controls are rejected in the real bundle. Ordinary precommit execution
  // is deliberately unavailable, so an isolated copied control bundle is committed for replay.
  for (const [name, replacement, cases] of [
    ["negative-prefix", line(escape(parameter("request")), escape(parameter("error")), "missing "), intendedCases],
    ["negative-error-LF", line(escape(parameter("request")), escapeCR(parameter("error"))), intendedCases]
  ]) {
    const proposal = await call("replace", { function: "missingLine", replacement })
    const result = await verify(proposal, cases, name)
    assert.ok(result.evidence.cases.some(item => item.status === "counterexample"))
    if (name === "negative-error-LF") assert.equal(result.evidence.cases.filter(item => item.status === "counterexample").length, 2)
    await call("request", { op: "reject", arguments: { proposalId: proposal.proposalId } })
    evidence.stages.push({ name, synthetic: true, evidence: result, structuralReadiness: proposal.review.readiness })
  }
  const gapBody = { kind: "conditional", condition: { kind: "stringEqual", left: parameter("request"), right: text("delta-outside") }, whenTrue: text("wrong\n"), whenFalse: improvedBody }
  const gap = await call("replace", { function: "missingLine", replacement: gapBody })
  const sampled = await verify(gap, intendedCases, "coverage-gap-sampled")
  assert.ok(sampled.evidence.cases.every(item => item.status === "intended-change-tested"))
  const challenge = await verify(gap, challenges, "coverage-gap-held-out")
  assert.equal(challenge.evidence.cases[0].status, "counterexample")
  await request("reject", { proposalId: gap.proposalId })
  const controlDirectory = join(output, "negative-control-only")
  await call("close")
  await cp(improved, controlDirectory, { recursive: true })
  await call("open", { mode: "open", snapshot: join(controlDirectory, "batch.json") })
  const committedGap = await call("replace", { function: "missingLine", replacement: gapBody })
  await call("commit", { proposalId: committedGap.proposalId })
  await call("close")
  const ordinaryGap = await ordinaryReplay(controlDirectory, "ordinary-coverage-gap")
  await call("open", { mode: "open", snapshot: join(controlDirectory, "batch.json") })
  const prefix = await call("replace", { function: "missingLine", replacement: line(escape(parameter("request")), escape(parameter("error")), "missing ") })
  await call("commit", { proposalId: prefix.proposalId })
  await call("close")
  const ordinaryPrefix = await ordinaryReplay(controlDirectory, "ordinary-negative-prefix", false)
  evidence.stages.push({ name: "coverage-gap control", synthetic: true, sampled, heldOut: challenge, ordinary: ordinaryGap })
  evidence.ordinaryNegativePrefix = ordinaryPrefix
  evidence.burden = { sampledCases: baselineCases.length, heldOutCases: challenges.length, primaryBehaviorRequirements: 2, selectedObservationCategories: requirements.observations.length, recordedManualDecisions: evidence.manualDecisions.length, fixtureProgramsAdded: 0, newHostScenarios: 1, frameworkDependenciesAdded: 0, timingScope: "Measured command walls on this machine, including compilation and process launch; not a productivity or application-speed benchmark." }
  await writeFile(join(output, "results.json"), JSON.stringify(evidence, null, 2))
  console.log(JSON.stringify({ output, stages: evidence.stages.map(stage => stage.name), burden: evidence.burden, timings: evidence.timings }, null, 2))
} finally { await call("close") }
