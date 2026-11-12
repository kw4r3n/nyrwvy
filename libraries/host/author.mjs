// Adds capabilities and operations to host.json through the authoring session (never by hand).
// Usage: node libraries/host/author.mjs <locks|rest> [<host executable>]
//   locks: create the Lock handle capability and the Locks factory capability (B step 1).
//   rest:  add Fs.rename / Fs.exists and create Clock (B step 2).
// Each phase commits its own proposals; run a phase once.
// Kept as the record of how host.json gained these declarations; do not run it again, because each
// phase would add a second copy of everything it creates.
import { spawn } from "node:child_process"
import { hostExchange, hostSend } from "../host-client.mjs"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../..")
const phase = process.argv[2]
if (phase !== "locks" && phase !== "rest") { console.error("usage: author.mjs <locks|rest>"); process.exit(2) }
const executable = process.argv[3] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const snapshot = join(here, "host.json")
const cli = spawn(executable, ["open", "--snapshot", snapshot], { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const send = hostSend(hostExchange(cli))
const commit = async created => { await send({ op: "commit", proposalId: created.proposalId }); console.log("committed", created.proposalId) }

const str = { kind: "primitive", primitive: "string" }
const int = { kind: "primitive", primitive: "int64" }
const bool = { kind: "primitive", primitive: "bool" }
const unit = { kind: "primitive", primitive: "unit" }
const result = (ok, error) => ({ kind: "builtinGeneric", generic: "result", arguments: [ok, error] })
const capability = name => ({ kind: "capability", name })
const param = (displayName, type) => ({ displayName, type })

if (phase === "locks") {
  await commit(await send({ op: "simulateCreateCapabilityByName", displayName: "Lock", exported: true, operations: [
    { displayName: "path", parameters: [], resultType: str, effect: "System" },
  ] }))
  await commit(await send({ op: "simulateCreateCapabilityByName", displayName: "Locks", exported: true, operations: [
    { displayName: "acquire", parameters: [param("path", str)], resultType: result(capability("Lock"), str), effect: "System" },
    { displayName: "release", parameters: [param("lock", capability("Lock"))], resultType: unit, effect: "System" },
  ] }))
} else {
  const add = async (capabilityName, operation) => commit(await send({ op: "simulateOperationsByName", operations: [
    { kind: "addCapabilityOperation", capability: capabilityName, ...operation, implementations: [] },
  ] }))
  await add("Fs", { displayName: "rename", parameters: [param("from", str), param("to", str)], resultType: result(unit, str), effect: "System", mayWait: true })
  await add("Fs", { displayName: "exists", parameters: [param("path", str)], resultType: bool, effect: "System", mayWait: true })
  await commit(await send({ op: "simulateCreateCapabilityByName", displayName: "Clock", exported: true, operations: [
    { displayName: "now", parameters: [], resultType: int, effect: "System" },
    { displayName: "sleep", parameters: [param("milliseconds", int)], resultType: unit, effect: "System", mayWait: true },
  ] }))
}
cli.stdin.end()
console.log("done", phase)
