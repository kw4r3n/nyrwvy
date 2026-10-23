// Diff-locality row of the phase 2 plan: four authoring operations through the real
// Nyrwvy.AuthoringSession tool handlers on the verification-planner snapshot, each from the same
// base; before/after pairs written in record form, pretty-printed JSON (jq .) and Nyason v0, then
// `git diff --numstat` per form, the compact JSON's changed bytes, and the record-line locality check.
// Run from the repository root after `npm ci`, a Release build of tools/Nyrwvy.AuthoringSession and
// `dotnet build -c Release` of this harness:  node evaluation/snapshot-record-format/locality.mjs [work-dir]
import assert from "node:assert/strict"
import { execFileSync } from "node:child_process"
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import * as z from "zod/v4"
import { createGraphTools } from "../../tools/nyrwvy-agent/graph-tools.mjs"

const root = fileURLToPath(new URL("../../", import.meta.url))
const harness = fileURLToPath(new URL("./", import.meta.url))
const dll = join(harness, "bin/Release/net10.0/Nyrwvy.SnapshotRecordFormat.dll")
const base = join(root, "examples/verification-planner/planner.snapshot.json")
const work = process.argv[2] ? resolve(process.argv[2]) : mkdtempSync(join(tmpdir(), "record-locality-"))
const tools = createGraphTools(z)
const context = { sessionID: `locality-${process.pid}`, worktree: root }
async function call(name, args = {}) {
  const raw = await tools[name].execute(args, context)
  if (name === "close") return raw
  const response = JSON.parse(raw)
  if (response.ok === false) throw new Error(JSON.stringify(response.error))
  return response.result ?? response
}
async function publish(proposal) {
  assert.ok(proposal.proposalId, JSON.stringify(proposal).slice(0, 400))
  await call("commit", { proposalId: proposal.proposalId })
}
const param = name => ({ kind: "parameter", name })
const access = (field, source = param("input")) => ({ kind: "fieldAccess", source, type: "ChangeRequest", field })

// Each operation: a name, the authoring calls, and the IDs it touches (declarations and nodes
// named in the request). A record line is local when its subject or its `in=` owner is one of
// these, or is owned (transitively) by one of them.
const operations = [
  { name: "rename-function", async run(ids) {
      await publish(await call("request", { op: "simulateOperations", arguments: { operations: [{ kind: "renameFunction", functionId: ids.fn("requires_live"), displayName: "requires_live_run" }] } }))
      return [ids.fn("requires_live")]
    } },
  { name: "add-node", async run(ids) {
      // The when-true literal of requires_live becomes boolNot(false): one node replaced by two.
      const fn = await call("inspect", { view: "function", function: "requires_live" })
      const target = findNode(fn, n => n.kind.kind === "boolLiteral" && n.kind.value === true)
      await publish(await call("replace", { function: "requires_live", targetNodeId: target, replacement: { kind: "boolNot", value: { kind: "boolLiteral", value: false } } }))
      return [ids.fn("requires_live")]
    } },
  { name: "add-union-variant", async run(ids) {
      const fn = await call("inspect", { view: "function", function: "area_requires_extended" })
      const match = findNode(fn, n => n.kind.kind === "matchUnion")
      await publish(await call("request", { op: "simulateOperations", arguments: { operations: [{ kind: "addUnionVariant", typeId: ids.type("ChangeArea"), displayName: "Tooling", fields: [], arms: [{ matchNodeId: match, body: { kind: "boolLiteral", value: false } }] }] } }))
      return [ids.type("ChangeArea"), ids.fn("area_requires_extended"), match]
    } },
  { name: "migrate-field-type", async run(ids) {
      // ChangeRequest.environmentDependent: bool -> int64; requires_live is its only reader.
      await publish(await call("request", { op: "simulateProductFieldTypeMigrationByName", arguments: {
        type: "ChangeRequest", field: "environmentDependent", newFieldType: { kind: "primitive", primitive: "int64" },
        functionRewrites: [{ function: "requires_live", replacement: { kind: "conditional", condition: access("agentInteraction"), whenTrue: { kind: "boolLiteral", value: true },
          whenFalse: { kind: "int64LessThan", left: { kind: "int64Literal", value: "0" }, right: access("environmentDependent") } } }] } }))
      return [ids.type("ChangeRequest"), ids.fn("requires_live")]
    } },
]

function findNode(view, predicate) {
  const hits = []
  const walk = v => {
    if (Array.isArray(v)) v.forEach(walk)
    else if (v && typeof v === "object") { if (typeof v.id === "string" && v.kind && typeof v.kind === "object" && predicate(v)) hits.push(v.id); Object.values(v).forEach(walk) }
  }
  walk(view)
  assert.equal(hits.length, 1, `expected one node, found ${hits.length}`)
  return hits[0]
}

const sh = (cmd, args, options = {}) => execFileSync(cmd, args, { encoding: "utf8", ...options })
const forms = {
  records: path => sh("dotnet", [dll, "encode", path]),
  json: path => sh("jq", [".", path]),
  nyason: path => sh("dotnet", [dll, "nyason", path]),
}
function changedBytes(a, b) {
  let prefix = 0
  while (prefix < a.length && prefix < b.length && a[prefix] === b[prefix]) prefix++
  let suffix = 0
  while (suffix < a.length - prefix && suffix < b.length - prefix && a[a.length - 1 - suffix] === b[b.length - 1 - suffix]) suffix++
  return { before: a.length - prefix - suffix, after: b.length - prefix - suffix }
}
function numstat(dir, name) {
  const [added, deleted] = sh("git", ["diff", "--numstat", "--", name], { cwd: dir }).trim().split("\t").map(Number)
  return { added: added || 0, deleted: deleted || 0 }
}
// Changed record lines (from `git diff -U0`) that are not the header and not owned by a touched ID.
function localityCheck(dir, before, after, touched) {
  const owners = new Map() // subject base id -> owner base id, over both texts
  for (const text of [before, after]) for (const line of text.split("\n")) {
    const parts = line.split(" ")
    if (parts.length < 2 || parts[0] === "snapshot") continue
    const subject = parts[1].split("/")[0].split(":")[1]
    const owner = parts.find(p => p.startsWith("in="))?.slice(3).split("/")[0].split(":")[1]
    owners.set(subject, owner)
  }
  const local = id => { let seen = new Set(); while (id && !seen.has(id)) { if (touched.has(id)) return true; seen.add(id); id = owners.get(id) } return false }
  const diff = sh("git", ["diff", "-U0", "--", "snapshot.records.txt"], { cwd: dir })
  const changed = diff.split("\n").filter(l => /^[+-]/.test(l) && !/^(\+\+\+|---)/.test(l)).map(l => l.slice(1))
  const nonLocal = changed.filter(l => {
    const parts = l.split(" ")
    if (parts[0] === "snapshot" || l === "") return false
    const subject = parts[1].split("/")[0].split(":")[1]
    const owner = parts.find(p => p.startsWith("in="))?.slice(3).split("/")[0].split(":")[1]
    return !(local(subject) || local(owner))
  })
  return { changedLines: changed.length, nonLocal }
}

mkdirSync(work, { recursive: true })
const results = []
for (const op of operations) {
  const dir = join(work, op.name)
  mkdirSync(dir, { recursive: true })
  const snapshot = join(dir, "snapshot.json")
  copyFileSync(base, snapshot)
  const beforeJson = readFileSync(snapshot, "utf8")
  await call("open", { mode: "open", snapshot })
  const declarations = await call("inspect", { view: "declarations" })
  const ids = {
    fn: name => { const f = declarations.functions.find(f => f.displayName === name); assert.ok(f, name); return f.id ?? f.functionId },
    type: name => { const t = declarations.types.find(t => t.displayName === name); assert.ok(t, name); return t.id ?? t.typeId },
  }
  const before = {}
  for (const [form, render] of Object.entries(forms)) { before[form] = render(snapshot); writeFileSync(join(dir, `snapshot.${form === "json" ? "pretty.json" : form === "records" ? "records.txt" : "nyason"}`), before[form]) }
  sh("git", ["init", "-q"], { cwd: dir }); sh("git", ["add", "-A"], { cwd: dir }); sh("git", ["-c", "user.email=a@b", "-c", "user.name=locality", "commit", "-qm", "before"], { cwd: dir })
  let touched
  try { touched = new Set(await op.run(ids)) } finally { await call("close") }
  const afterJson = readFileSync(snapshot, "utf8")
  assert.notEqual(afterJson, beforeJson, `${op.name}: snapshot unchanged`)
  const after = {}
  for (const [form, render] of Object.entries(forms)) { after[form] = render(snapshot); writeFileSync(join(dir, `snapshot.${form === "json" ? "pretty.json" : form === "records" ? "records.txt" : "nyason"}`), after[form]) }
  const lines = { records: numstat(dir, "snapshot.records.txt"), json: numstat(dir, "snapshot.pretty.json"), nyason: numstat(dir, "snapshot.nyason") }
  const locality = localityCheck(dir, before.records, after.records, touched)
  const jsonBytes = changedBytes(beforeJson, afterJson)
  const recordLines = lines.records.added + lines.records.deleted, jsonLines = lines.json.added + lines.json.deleted
  results.push({ operation: op.name, touched: [...touched], lines, jsonChangedBytes: jsonBytes, recordLinesChanged: locality.changedLines, nonLocalRecordLines: locality.nonLocal,
    pass: recordLines <= jsonLines && locality.nonLocal.length === 0, dir })
}
console.log("operation            records(+/-)   json(+/-)   nyason(+/-)   json-bytes(before/after)  non-local  pass")
for (const r of results) {
  const f = x => `${x.added}/${x.deleted}`
  console.log(`${r.operation.padEnd(20)} ${f(r.lines.records).padStart(12)} ${f(r.lines.json).padStart(11)} ${f(r.lines.nyason).padStart(13)}   ${`${r.jsonChangedBytes.before}/${r.jsonChangedBytes.after}`.padStart(22)}  ${String(r.nonLocalRecordLines.length).padStart(9)}  ${r.pass ? "PASS" : "FAIL"}`)
  for (const l of r.nonLocalRecordLines) console.log(`  non-local: ${l.slice(0, 160)}`)
}
mkdirSync(join(harness, "results"), { recursive: true })
writeFileSync(join(harness, "results/diff-locality.json"), JSON.stringify({ base: "examples/verification-planner/planner.snapshot.json", results }, null, 2) + "\n")
console.log(`work directory: ${work}`)
process.exit(results.every(r => r.pass) ? 0 : 1)
