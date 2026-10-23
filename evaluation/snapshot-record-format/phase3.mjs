// Phase 3 of the plan: agent projection comparison on the verification-planner functions.
// Three presentations per function (readFunction focus JSON = control, record form, Nyason v0), each
// with the same referenced declarations; 12 tasks (8 comprehension, 4 edit) with answers derived from
// the model; one `claude -p` call per task x format x model x repetition, no tools; mechanical grading.
//
// From the repository root, after `npm ci`, a Release build of tools/Nyrwvy.AuthoringSession and of
// this harness:
//   node evaluation/snapshot-record-format/phase3.mjs prepare   # presentations + tasks.json (no model calls)
//   node evaluation/snapshot-record-format/phase3.mjs smoke     # 6 calls into results/phase3/smoke, graded, not counted
//   node evaluation/snapshot-record-format/phase3.mjs run       # 216 calls into results/phase3/raw (resumable)
//   node evaluation/snapshot-record-format/phase3.mjs grade     # scores, pass rule, results/phase3/summary.json
import assert from "node:assert/strict"
import { execFile, execFileSync } from "node:child_process"
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"
import { promisify } from "node:util"
import * as z from "zod/v4"
import { createGraphTools } from "../../tools/nyrwvy-agent/graph-tools.mjs"

const root = fileURLToPath(new URL("../../", import.meta.url))
const harness = fileURLToPath(new URL("./", import.meta.url))
const dll = join(harness, "bin/Release/net10.0/Nyrwvy.SnapshotRecordFormat.dll")
const base = join(root, "examples/verification-planner/planner.snapshot.json")
const out = join(harness, "results/phase3")
const formats = ["focus-json", "records", "nyason"]
const models = ["haiku", "sonnet"]
const repetitions = 3
const concurrency = 4
const sh = (cmd, args) => execFileSync(cmd, args, { encoding: "utf8", maxBuffer: 1 << 26 })

// ------------------------------------------------------------------ session helpers
const tools = createGraphTools(z)
let sessionCounter = 0
async function withSession(snapshotPath, body) {
  const context = { sessionID: `phase3-${process.pid}-${sessionCounter++}`, worktree: root }
  const call = async (name, args = {}) => {
    const raw = await tools[name].execute(args, context)
    if (name === "close") return raw
    const response = JSON.parse(raw)
    if (response.ok === false) throw new Error(JSON.stringify(response.error))
    return response.result ?? response
  }
  await call("open", { mode: "open", snapshot: snapshotPath })
  try { return await body(call) } finally { await call("close") }
}
const normalized = json => { const s = JSON.parse(json); delete s.snapshotId; delete s.parentSnapshotId; return JSON.stringify(s) }

// ------------------------------------------------------------------ model helpers
const snapshot = JSON.parse(readFileSync(base, "utf8"))
const uuidRe = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
/** Every ID in the snapshot with its domain (the record form's domain names). */
function idDomains() {
  const map = new Map()
  const graph = (g, fn) => { for (const n of g.nodes) { map.set(n.id, "node"); for (const [k, v] of Object.entries(n)) nested(k, v, fn) } }
  const nested = (k, v, fn) => {
    if (v && typeof v === "object" && Array.isArray(v.nodes)) graph(v, fn)
    else if (k === "arms") for (const a of v) { for (const fb of a.fieldBindings) map.set(fb.binding.id, "binding"); graph(a.body, fn) }
    else if (v && typeof v === "object" && v.body && (v.valueBinding || v.errorBinding)) { map.set((v.valueBinding ?? v.errorBinding).id, "binding"); graph(v.body, fn) }
  }
  for (const t of snapshot.types) { map.set(t.id, "type"); for (const f of t.fields ?? []) map.set(f.id, "field"); for (const v of t.variants ?? []) { map.set(v.id, "variant"); for (const f of v.fields) map.set(f.id, "field") } }
  for (const e of snapshot.effects) map.set(e.id, "effect")
  for (const c of snapshot.capabilities) { map.set(c.id, "capability"); for (const o of c.operations) { map.set(o.id, "operation"); for (const p of o.parameters) map.set(p.id, "capability-parameter") } }
  for (const f of snapshot.functions) { map.set(f.id, "function"); for (const p of f.parameters) map.set(p.id, "parameter"); for (const tp of f.typeParameters ?? []) map.set(tp.typeParameterId, "type-parameter"); graph(f.body, f) }
  for (const o of snapshot.obligations) map.set(o.id, "obligation")
  return map
}
const domains = idDomains()
const fn = name => snapshot.functions.find(f => f.displayName === name)
const type = name => snapshot.types.find(t => t.displayName === name)
const allNodes = g => g.nodes.flatMap(n => [n, ...Object.entries(n).flatMap(([k, v]) =>
  v && typeof v === "object" && Array.isArray(v.nodes) ? allNodes(v)
  : k === "arms" ? v.flatMap(a => allNodes(a.body))
  : v && typeof v === "object" && v.body && (v.valueBinding || v.errorBinding) ? allNodes(v.body) : [])])
const nodesOf = f => allNodes(f.body).sort((a, b) => a.id.localeCompare(b.id))
const node = (f, kind, index = 0) => nodesOf(f).filter(n => n.kind === kind)[index]

// ------------------------------------------------------------------ presentations
function referencedDeclarations(recordBlock) {
  const ids = new Set()
  const owners = new Map() // field/variant id -> owning type id, from the full record text
  for (const line of fullRecords.split("\n")) {
    const p = line.split(" ")
    if ((p[0] === "field" || p[0] === "variant") && p[2]?.startsWith("in=")) owners.set(p[1].split(":")[1], p[2].slice(3).split(":")[1])
  }
  for (const line of recordBlock.split("\n")) for (const m of line.matchAll(/(type|effect|capability|field|variant|operation):([0-9a-f-]{36})/g)) {
    if (m[1] === "field") { const t = owners.get(m[2]); const tt = domains.get(t) === "variant" ? owners.get(t) : t; ids.add(tt) }
    else if (m[1] === "variant") ids.add(owners.get(m[2]))
    else if (m[1] === "operation") ids.add(snapshot.capabilities.find(c => c.operations.some(o => o.id === m[2])).id)
    else ids.add(m[2])
  }
  return [...ids]
}
let fullRecords, fullNyason
const blocksOf = text => text.split("\n\n").slice(1).map(b => b.replace(/\n$/, ""))
function nyasonDeclaration(id) {
  const lines = fullNyason.split("\n")
  for (let i = 0; i < lines.length; i++) {
    if (/^      [a-z-]+ \{$/.test(lines[i])) {
      let j = i + 1
      while (lines[j] !== "      }") j++
      if (lines[i + 1].trim() === `id = ${id}`) return lines.slice(i, j + 1).join("\n")
      i = j
    }
  }
  throw new Error(`nyason declaration ${id} not found`)
}
async function prepare() {
  mkdirSync(join(out, "presentations"), { recursive: true })
  fullRecords = sh("dotnet", [dll, "encode", base])
  fullNyason = sh("dotnet", [dll, "nyason", base])
  writeFileSync(join(out, "presentations/planner.records.txt"), fullRecords)
  writeFileSync(join(out, "presentations/planner.nyason"), fullNyason)
  const focus = {}
  const work = mkdtempSync(join(tmpdir(), "phase3-prepare-"))
  copyFileSync(base, join(work, "planner.json"))
  await withSession(join(work, "planner.json"), async call => {
    for (const f of snapshot.functions) focus[f.id] = await call("inspect", { view: "function", functionId: f.id })
  })
  const presentations = {}
  for (const f of snapshot.functions) {
    const block = blocksOf(fullRecords).find(b => b.startsWith(`function function:${f.id} `))
    const refs = referencedDeclarations(block)
    const tag = id => `${domains.get(id)}:${id}`
    const refBlocks = refs.map(id => blocksOf(fullRecords).find(b => b.split(" ")[1] === tag(id)))
    const declJson = { types: snapshot.types.filter(t => refs.includes(t.id)), effects: snapshot.effects.filter(e => refs.includes(e.id)), capabilities: snapshot.capabilities.filter(c => refs.includes(c.id)) }
    const header = fullRecords.split("\n")[0]
    presentations[f.id] = {
      "focus-json": JSON.stringify({ function: focus[f.id], referencedDeclarations: declJson }),
      records: [header, ...refBlocks, block].join("\n\n") + "\n",
      nyason: ["types = [", ...refs.filter(id => domains.get(id) === "type").map(id => nyasonDeclaration(tag(id))), "]", "effects = [", ...refs.filter(id => domains.get(id) === "effect").map(id => nyasonDeclaration(tag(id))), "]",
        "capabilities = [", ...refs.filter(id => domains.get(id) === "capability").map(id => nyasonDeclaration(tag(id))), "]", "functions = [", nyasonDeclaration(`function:${f.id}`), "]"].join("\n") + "\n",
    }
    for (const [form, text] of Object.entries(presentations[f.id])) writeFileSync(join(out, "presentations", `${f.displayName}.${form === "focus-json" ? "focus.json" : form === "records" ? "records.txt" : "nyason"}`), text)
  }
  // Tasks: expected answers derived from the model.
  const rl = fn("requires_live"), pv = fn("plan_verification"), are = fn("area_requires_extended"), vl = fn("verification_label")
  const arm = (f, variantName) => { const m = node(f, "matchUnion"); const vid = snapshot.types.flatMap(t => t.variants ?? []).find(v => v.displayName === variantName).id; return { m, body: m.arms.find(a => a.variantId === vid || a.variantIds?.includes(vid)).body.resultNodeId, vid } }
  const cond = node(rl, "conditional"), call = node(pv, "directCall"), listAny = node(pv, "listAny"), fa = node(rl, "fieldAccess"), pref = node(pv, "parameterReference")
  const a1 = arm(are, "Documentation"), a2 = arm(vl, "FastOnly")
  const tasks = [
    { id: "C1", kind: "comprehension", function: rl.id, question: `Which node feeds the condition input of the conditional node ${cond.id}? Answer with that node's ID.`, expected: cond.conditionNodeId, domain: "node" },
    { id: "C2", kind: "comprehension", function: pv.id, question: `Which function does the direct-call node ${call.id} call? Answer with that function's ID.`, expected: call.targetFunctionId, domain: "function" },
    { id: "C3", kind: "comprehension", function: are.id, question: `In the match-union node ${a1.m.id}, which node is the body result of the arm that handles variant ${a1.vid} ("Documentation")? Answer with that node's ID.`, expected: a1.body, domain: "node" },
    { id: "C4", kind: "comprehension", function: pv.id, question: `Which effects does the function plan_verification (${pv.id}) require? Answer with the list of effect IDs (an empty list if none).`, expected: pv.requiredEffects, domain: "effect" },
    { id: "C5", kind: "comprehension", function: vl.id, question: `In the match-union node ${a2.m.id}, which node is the body result of the arm that handles variant ${a2.vid} ("FastOnly")? Answer with that node's ID.`, expected: a2.body, domain: "node" },
    { id: "C6", kind: "comprehension", function: rl.id, question: `Which field does the field-access node ${fa.id} read? Answer with that field's ID.`, expected: fa.fieldId, domain: "field" },
    { id: "C7", kind: "comprehension", function: pv.id, question: `Which node feeds the source-list input of the list-any node ${listAny.id}? Answer with that node's ID.`, expected: listAny.sourceListNodeId, domain: "node" },
    { id: "C8", kind: "comprehension", function: pv.id, question: `Which parameter does the parameter-reference node ${pref.id} refer to? Answer with that parameter's ID.`, expected: pref.parameterId, domain: "parameter" },
    { id: "E1", kind: "edit", function: rl.id, question: `Rename the function requires_live to "requires_live_run".`, gold: { op: "simulateOperations", operations: [{ kind: "renameFunction", functionId: rl.id, displayName: "requires_live_run" }] } },
    { id: "E2", kind: "edit", function: vl.id, question: `Rename the variant FastOnly of the Union type VerificationLevel to "FastTestsOnly".`, gold: { op: "simulateOperations", operations: [{ kind: "renameVariant", variantId: a2.vid, displayName: "FastTestsOnly" }] } },
    { id: "E3", kind: "edit", function: vl.id, question: `Rename the parameter "input" of the function verification_label to "level".`, gold: { op: "simulateOperations", operations: [{ kind: "renameParameter", parameterId: vl.parameters[0].id, displayName: "level" }] } },
    { id: "E4", kind: "edit", function: rl.id, question: `Rename the field knownRegression of the Product type ChangeRequest to "knownRegressionRisk".`, gold: { op: "simulateOperations", operations: [{ kind: "renameField", fieldId: type("ChangeRequest").fields.find(f => f.displayName === "knownRegression").id, displayName: "knownRegressionRisk" }] } },
  ]
  // The intended change of each edit: the gold request committed on a copy, normalized.
  for (const t of tasks.filter(t => t.kind === "edit")) t.expectedSnapshot = normalized(await applyRequest(t.gold))
  writeFileSync(join(out, "tasks.json"), JSON.stringify({ base: "examples/verification-planner/planner.snapshot.json", tasks }, null, 2) + "\n")
  console.log(`wrote ${tasks.length} tasks and ${Object.keys(presentations).length * 3} presentations under ${out}`)
}
/** Runs a request through a session on a fresh copy of the planner and commits it; returns the new JSON. */
async function applyRequest(request) {
  const work = mkdtempSync(join(tmpdir(), "phase3-apply-"))
  const path = join(work, "planner.json")
  copyFileSync(base, path)
  const { op, ...args } = request
  await withSession(path, async call => {
    const proposal = await call("request", { op, arguments: args })
    assert.ok(proposal.proposalId, "no proposal")
    await call("commit", { proposalId: proposal.proposalId })
  })
  return readFileSync(path, "utf8")
}

// ------------------------------------------------------------------ prompts and calls
const opShapes = `Operation requests (docs/specs/agent-authoring-session.md): a request is {"op":"simulateOperations","operations":[<operation>, ...]}. Rename operations use exact IDs: {"kind":"renameFunction","functionId":"<FunctionId>","displayName":"<new name>"}, {"kind":"renameType","typeId":"<TypeId>","displayName":"<new name>"}, {"kind":"renameField","fieldId":"<FieldId of a Product field or a Union variant field>","displayName":"<new name>"}, {"kind":"renameVariant","variantId":"<VariantId>","displayName":"<new name>"}, {"kind":"renameParameter","parameterId":"<ParameterId>","displayName":"<new name>"}. IDs are the bare UUIDs as they appear in the snapshot, without any domain prefix. A rename changes only the display name; every stable ID is kept.`
const formatNames = { "focus-json": "readFunction focus JSON (the session's structured function graph) with the referenced declarations as snapshot JSON", records: "record form (one record per line: kind, subject ID, then key=value pairs; in= names the owner)", nyason: "Nyason v0 (nested tagged records)" }
function prompt(task, format) {
  const presentation = readPresentation(task.function, format)
  const answerFormat = task.kind === "edit"
    ? `${opShapes}\n\nReply with exactly one JSON object: the operation request for this change, and nothing else.`
    : `Reply with exactly one JSON object {"answer": <ID string, or a list of ID strings when the question asks for a list>} and nothing else. IDs are bare UUIDs without any domain prefix.`
  return `Below is one function of a Nyrwvy semantic-graph snapshot, presented in the ${formatNames[format]}, together with the declarations it references.\n\n<presentation>\n${presentation}</presentation>\n\nTask: ${task.question}\n\n${answerFormat}`
}
function readPresentation(functionId, format) {
  const name = snapshot.functions.find(f => f.id === functionId).displayName
  return readFileSync(join(out, "presentations", `${name}.${format === "focus-json" ? "focus.json" : format === "records" ? "records.txt" : "nyason"}`), "utf8")
}
const execFileAsync = promisify(execFile)
async function callModel(model, text) {
  const args = ["-p", "--strict-mcp-config", "--tools", "", "--disallowed-tools", "advisor", "--system-prompt", "You answer with exactly one JSON object and nothing else.", "--model", model, "--output-format", "json", text]
  const { stdout } = await execFileAsync("claude", args, { maxBuffer: 1 << 26, cwd: tmpdir() })
  return JSON.parse(stdout)
}
async function runCalls(dir, jobs) {
  mkdirSync(dir, { recursive: true })
  let next = 0, done = 0
  const worker = async () => {
    while (next < jobs.length) {
      const job = jobs[next++]
      const file = join(dir, `${job.task.id}-${job.format}-${job.model}-${job.rep}.json`)
      if (existsSync(file)) { done++; continue }
      const text = prompt(job.task, job.format)
      const started = Date.now()
      let response
      try { response = await callModel(job.model, text) } catch (error) { response = { error: String(error.stderr ?? error.message) } }
      writeFileSync(file, JSON.stringify({ task: job.task.id, format: job.format, model: job.model, rep: job.rep, promptChars: text.length, elapsedMs: Date.now() - started, response }, null, 2))
      console.log(`${++done}/${jobs.length} ${job.task.id} ${job.format} ${job.model} #${job.rep}`)
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, jobs.length) }, worker))
}
const loadTasks = () => JSON.parse(readFileSync(join(out, "tasks.json"), "utf8")).tasks
function jobsFor(tasks, reps) { return tasks.flatMap(task => formats.flatMap(format => models.flatMap(model => Array.from({ length: reps }, (_, i) => ({ task, format, model, rep: i + 1 }))))) }

// ------------------------------------------------------------------ grading
function parseAnswer(text) {
  if (typeof text !== "string") return null
  const stripped = text.trim().replace(/^```(?:json)?\s*/i, "").replace(/\s*```$/, "")
  try { return JSON.parse(stripped) } catch { const m = stripped.match(/\{[\s\S]*\}/); if (!m) return null; try { return JSON.parse(m[0]) } catch { return null } }
}
const bare = v => typeof v === "string" ? v.trim().toLowerCase().replace(/^[a-z-]+:/, "") : v
/** correct | wrong | wrong-id-domain | malformed, plus a note. */
async function grade(task, raw) {
  const r = raw.response
  if (!r || r.error || r.is_error) return { outcome: "malformed", note: `call failed: ${(r?.error ?? r?.result ?? "").toString().slice(0, 120)}` }
  const answer = parseAnswer(r.result)
  if (answer === null || typeof answer !== "object") return { outcome: "malformed", note: "no JSON object" }
  const idCheck = (value, domain) => {
    const id = bare(value)
    if (typeof id !== "string" || !uuidRe.test(id)) return "malformed"
    if (!domains.has(id)) return "wrong-id-domain"
    if (domains.get(id) !== domain) return "wrong-id-domain"
    return null
  }
  if (task.kind === "comprehension") {
    if (!("answer" in answer)) return { outcome: "malformed", note: "no answer key" }
    const expected = task.expected
    if (Array.isArray(expected)) {
      const given = Array.isArray(answer.answer) ? answer.answer.map(bare) : answer.answer === null || answer.answer === "" ? [] : [bare(answer.answer)]
      if (JSON.stringify([...given].sort()) === JSON.stringify([...expected].sort())) return { outcome: "correct" }
      const bad = given.map(g => idCheck(g, task.domain)).find(Boolean)
      return { outcome: bad ?? "wrong", note: `got ${JSON.stringify(answer.answer)}` }
    }
    if (bare(answer.answer) === expected) return { outcome: "correct" }
    return { outcome: idCheck(answer.answer, task.domain) ?? "wrong", note: `got ${JSON.stringify(answer.answer)}` }
  }
  // Edit: shape, then ID domains, then simulate + commit on a copy and compare with the intended change.
  if (answer.op !== "simulateOperations" || !Array.isArray(answer.operations) || answer.operations.length === 0) return { outcome: "malformed", note: `shape ${JSON.stringify(answer).slice(0, 120)}` }
  const idKeys = { functionId: "function", typeId: "type", fieldId: "field", variantId: "variant", parameterId: "parameter" }
  for (const op of answer.operations) for (const [k, domain] of Object.entries(idKeys)) if (k in op) { const bad = idCheck(op[k], domain); if (bad) return { outcome: bad, note: `${k}=${JSON.stringify(op[k])}` } }
  try {
    const result = normalized(await applyRequest(answer))
    return result === task.expectedSnapshot ? { outcome: "correct" } : { outcome: "wrong", note: "committed change differs from the intended one" }
  } catch (error) { return { outcome: "wrong", note: `session rejected: ${String(error.message).slice(0, 160)}` } }
}
async function gradeDir(dir, tasks) {
  const rows = []
  for (const file of readdirSync(dir).filter(f => f.endsWith(".json")).sort()) {
    const raw = JSON.parse(readFileSync(join(dir, file), "utf8"))
    const task = tasks.find(t => t.id === raw.task)
    const g = await grade(task, raw)
    const u = raw.response?.usage ?? {}
    rows.push({ file, task: raw.task, kind: task.kind, format: raw.format, model: raw.model, rep: raw.rep, outcome: g.outcome, note: g.note ?? "",
      inputTokens: (u.input_tokens ?? 0) + (u.cache_creation_input_tokens ?? 0) + (u.cache_read_input_tokens ?? 0), outputTokens: u.output_tokens ?? 0,
      toolUses: Math.max(0, (raw.response?.num_turns ?? 1) - 1), result: String(raw.response?.result ?? "").slice(0, 300) })
  }
  return rows
}
function summarize(rows) {
  const cells = {}
  for (const format of formats) for (const model of models) {
    const r = rows.filter(x => x.format === format && x.model === model)
    const n = r.length, count = o => r.filter(x => x.outcome === o).length
    cells[`${format}/${model}`] = { calls: n, correct: count("correct"), wrong: count("wrong"), wrongIdDomain: count("wrong-id-domain"), malformed: count("malformed"),
      inputTokensMean: n ? Math.round(r.reduce((s, x) => s + x.inputTokens, 0) / n) : 0, outputTokensMean: n ? Math.round(r.reduce((s, x) => s + x.outputTokens, 0) / n) : 0, toolUses: r.reduce((s, x) => s + x.toolUses, 0) }
  }
  // Pass rule: the record form is adopted only if it is at least as correct as the focus JSON for both
  // models and has a lower combined wrong-ID-domain + malformed rate (combined over both models).
  const correctOk = models.every(m => cells[`records/${m}`].correct >= cells[`focus-json/${m}`].correct)
  const bad = f => models.reduce((s, m) => s + cells[`${f}/${m}`].wrongIdDomain + cells[`${f}/${m}`].malformed, 0) / Math.max(1, models.reduce((s, m) => s + cells[`${f}/${m}`].calls, 0))
  const rateOk = bad("records") < bad("focus-json")
  return { cells, pass: { correctAtLeastFocusJsonBothModels: correctOk, recordsBadRate: bad("records"), focusJsonBadRate: bad("focus-json"), lowerBadRate: rateOk, adoptRecordProjection: correctOk && rateOk } }
}
function printSummary(summary) {
  console.log("format/model        calls correct wrong wrong-id malformed in-tokens out-tokens tool-uses")
  for (const [k, c] of Object.entries(summary.cells)) console.log(`${k.padEnd(19)} ${String(c.calls).padStart(5)} ${String(c.correct).padStart(7)} ${String(c.wrong).padStart(5)} ${String(c.wrongIdDomain).padStart(8)} ${String(c.malformed).padStart(9)} ${String(c.inputTokensMean).padStart(9)} ${String(c.outputTokensMean).padStart(10)} ${String(c.toolUses).padStart(9)}`)
  console.log(`pass rule: records correct >= focus JSON for both models: ${summary.pass.correctAtLeastFocusJsonBothModels}; records wrong-id+malformed rate ${summary.pass.recordsBadRate.toFixed(3)} < focus JSON ${summary.pass.focusJsonBadRate.toFixed(3)}: ${summary.pass.lowerBadRate}; adopt record projection: ${summary.pass.adoptRecordProjection}`)
}

// ------------------------------------------------------------------ main
const command = process.argv[2]
if (command === "prepare") await prepare()
else if (command === "smoke") {
  const tasks = loadTasks()
  const jobs = [["C1", "focus-json", "haiku"], ["E1", "records", "haiku"], ["C3", "nyason", "haiku"], ["E2", "focus-json", "sonnet"], ["C6", "records", "sonnet"], ["E4", "nyason", "sonnet"]]
    .map(([id, format, model]) => ({ task: tasks.find(t => t.id === id), format, model, rep: 0 }))
  await runCalls(join(out, "smoke"), jobs)
  const rows = await gradeDir(join(out, "smoke"), tasks)
  for (const r of rows) console.log(`${r.task} ${r.format} ${r.model}: ${r.outcome} ${r.note} | ${r.result.replace(/\s+/g, " ").slice(0, 140)}`)
} else if (command === "run") await runCalls(join(out, "raw"), jobsFor(loadTasks(), repetitions))
else if (command === "grade") {
  const tasks = loadTasks()
  const rows = await gradeDir(join(out, "raw"), tasks)
  const summary = summarize(rows)
  writeFileSync(join(out, "summary.json"), JSON.stringify({ calls: rows.length, summary, rows }, null, 2) + "\n")
  printSummary(summary)
  console.log(`${rows.length} graded calls; summary.json written`)
} else { console.error("usage: phase3.mjs prepare | smoke | run | grade"); process.exit(2) }
