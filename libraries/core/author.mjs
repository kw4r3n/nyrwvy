// Writes core.json: small functions whose calls take fewer nodes than the shapes they replace. Each uses
// existing nodes only. Usage: node libraries/core/author.mjs [<host executable>]; run it again after a change.
import { spawn } from "node:child_process"
import { rmSync } from "node:fs"
import { createInterface } from "node:readline"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const executable = process.argv[2] ?? resolve(here, "../../tools/zig/host/bin/nyrwvy-host")
const snapshot = join(here, "core.json")
rmSync(snapshot, { force: true })
rmSync(snapshot + ".backends.json", { force: true })
const cli = spawn(executable, ["create", "--snapshot", snapshot], { stdio: ["pipe", "pipe", "inherit"], cwd: resolve(here, "../..") })
const lines = createInterface({ input: cli.stdout })[Symbol.asyncIterator]()
let n = 0
const send = async request => {
  cli.stdin.write(JSON.stringify({ id: String(n++), ...request }) + "\n")
  const reply = JSON.parse((await lines.next()).value)
  if (!reply.ok) throw new Error(`${request.op}: ${JSON.stringify(reply.error)}`)
  return reply.result
}
const define = async (displayName, typeParameters, parameters, resultType, body) => {
  const created = await send({ op: "simulateCreateFunctionByName", displayName, exported: true, typeParameters, requiredEffects: [], resultType, body,
    parameters: Object.entries(parameters).map(([name, type]) => ({ displayName: name, type })) })
  await send({ op: "commit", proposalId: created.proposalId })
}
const string = { kind: "primitive", primitive: "string" }
const int64 = { kind: "primitive", primitive: "int64" }
const bool = { kind: "primitive", primitive: "bool" }
const T = { kind: "typeParameter", name: "T" }
const items = { kind: "builtinGeneric", generic: "list", arguments: [T] }
const P = name => ({ kind: "parameter", name })

// The longest chain in the measured programs joins 10 strings.
for (let count = 3; count <= 10; count++) {
  const names = Array.from({ length: count }, (_, i) => String.fromCharCode(97 + i))
  await define(`concat${count}`, [], Object.fromEntries(names.map(name => [name, string])), string,
    names.slice(1).reduce((left, name) => ({ kind: "stringConcat", left, right: P(name) }), P(names[0])))
}
await define("listIsEmpty", ["T"], { items }, bool, { kind: "valueEqual", left: { kind: "listLength", value: P("items") }, right: { kind: "int64Literal", value: "0" } })
await define("itemOr", ["T"], { items, index: int64, fallback: T }, T,
  { kind: "optionGetOrDefault", option: { kind: "listItemAt", list: P("items"), index: P("index") }, default: P("fallback") })
cli.stdin.end()
await new Promise(done => cli.once("close", done))
for (const file of [".backends.json", ".lock"]) rmSync(snapshot + file, { force: true })
