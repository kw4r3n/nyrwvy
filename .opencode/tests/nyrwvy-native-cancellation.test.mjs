import test from "node:test"
import assert from "node:assert/strict"
import { spawn } from "node:child_process"
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { createInterface } from "node:readline"
import { setTimeout as delay } from "node:timers/promises"

const host = resolve(process.env.NYRWVY_NATIVE_CANCELLATION_HOST || "tools/zig/host/bin/nyrwvy-host")

async function waitFor(check, message, milliseconds = 3000) {
  const until = Date.now() + milliseconds
  while (Date.now() < until) {
    if (await check()) return
    await delay(10)
  }
  assert.fail(message)
}

async function live(pid) {
  try { return !/\) Z /.test(await readFile(`/proc/${pid}/stat`, "utf8")) }
  catch (error) { if (error.code === "ENOENT") return false; throw error }
}

const skip = process.platform !== "linux" ? "native cancellation uses Linux process groups"
  : process.env.NYRWVY_HOST_EXECUTABLE && !process.env.NYRWVY_NATIVE_CANCELLATION_HOST ? "explicit alternate host selected; run with NYRWVY_NATIVE_CANCELLATION_HOST to verify the native host" : false

for (const signal of ["SIGTERM", "SIGINT"]) for (const phase of ["compiler", "hostSources", "worker-idle", "worker-busy"]) {
  test(`native ${signal} reaps owned groups during ${phase}`, { timeout: 15000, skip }, async t => {
    const root = await mkdtemp(join(tmpdir(), "nyrwvy-cancellation-"))
    const marker = join(root, "children.json")
    const wrapper = join(root, "dotnet")
    // Exercise the native process boundary without a managed build or a real long-running compiler.
    await writeFile(wrapper, `#!${process.execPath}
const fs = require('node:fs');
const { spawn } = require('node:child_process');
const { join } = require('node:path');
const args = process.argv.slice(2);
function hang() {
  process.on('SIGTERM', () => {});
  const child = spawn('/bin/sleep', ['60'], {stdio:'inherit'});
  fs.appendFileSync(${JSON.stringify(marker)}, JSON.stringify([process.pid, child.pid]) + '\\n');
  setInterval(() => {}, 60000);
}
if (args[0] === 'build') {
  if (${JSON.stringify(phase)} === 'compiler') hang();
  else fs.mkdirSync(args[args.indexOf('--output') + 1], {recursive:true});
} else if (args[1] === 'identity') {
  console.log(JSON.stringify({identity:'probe', runtimeVersion:'10.0.0', major:10, buildContextFingerprint:'probe'}));
} else if (args[1] === 'compile') {
  for (const suffix of ['dll','pdb']) fs.writeFileSync(join(args[3], 'Generated.' + suffix), 'probe');
} else if (args[0].includes('nyrwvy-csharp-worker-')) {
  hang();
  console.log('["ready"]');
  require('node:readline').createInterface({input:process.stdin}).on('line', () => {
    if (${JSON.stringify(phase)} !== 'worker-busy') console.log(JSON.stringify(['ok', '["value",7,[]]']));
  });
} else hang();
`, { mode: 0o700 })
    await writeFile(join(root, "Host.cs"), "// Cancellation probe host source.\n")
    const child = spawn(host, ["create", "--snapshot", join(root, "workspace.json"), "--allow-host-sources"], {
      cwd: root, env: { ...process.env, PATH: `${root}:${process.env.PATH}` }, stdio: ["pipe", "pipe", "pipe"],
    })
    let errors = ""
    child.stderr.setEncoding("utf8").on("data", text => { errors += text })
    let result
    const closed = new Promise((accept, reject) => { child.once("error", reject); child.once("close", (code, signal) => { result = { code, signal }; accept(result) }) })
    const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]()
    const readPids = async () => (await readFile(marker, "utf8")).trim().split("\n").flatMap(line => JSON.parse(line))
    let pids = []
    t.after(async () => {
      child.kill("SIGKILL")
      if (!pids.length) try { pids = await readPids() } catch {}
      for (const pid of pids) try { process.kill(pid, "SIGKILL") } catch {}
      await closed
      await rm(root, { recursive: true, force: true })
    })
    let nextId = 0
    const send = payload => child.stdin.write(JSON.stringify({ id: String(++nextId), ...payload }) + "\n")
    const request = async payload => {
      send(payload)
      const next = await lines.next()
      assert.equal(next.done, false, errors)
      const reply = JSON.parse(next.value)
      assert.equal(reply.ok, true, JSON.stringify(reply))
      return reply.result
    }
    const publish = async payload => { const proposal = await request(payload); await request({ op: "commit", proposalId: proposal.proposalId }) }
    const int64 = { kind: "primitive", primitive: "int64" }
    if (phase === "hostSources") {
      await publish({ op: "simulateCreateEffect", displayName: "Io" })
      await publish({ op: "simulateCreateCapabilityByName", displayName: "Probe", operations: [{ displayName: "get", parameters: [], resultType: int64, effect: "Io" }] })
    }
    await publish({ op: "simulateCreateFunctionByName", displayName: "probe", exported: true,
      parameters: phase === "hostSources" ? [{ displayName: "host", type: { kind: "capability", name: "Probe" } }] : [], resultType: int64,
      requiredEffects: phase === "hostSources" ? ["Io"] : [], body: phase === "hostSources"
        ? { kind: "capabilityInvoke", capabilityValue: { kind: "parameter", name: "host" }, capability: "Probe", operation: "get", arguments: [] }
        : { kind: "int64Literal", value: 7 } })
    const run = { op: "runPureByName", function: "probe", backend: "csharp", arguments: [], ...(phase === "hostSources"
      ? { hostSources: ["Host.cs"], arguments: [{ kind: "hostCapability", capability: "Probe", implementation: "ProbeHost" }] } : {}) }
    if (phase === "hostSources") {
      await publish({ op: "simulateCreateFunctionByName", displayName: "pureProbe", parameters: [], resultType: int64,
        requiredEffects: [], body: { kind: "int64Literal", value: 7 } })
      await request({ op: "runPureByName", function: "pureProbe", backend: "csharp", arguments: [] })
    }
    if (phase === "worker-idle") await request(run)
    else send(run)
    await waitFor(async () => { try { pids = await readPids(); return pids.length === (phase === "hostSources" ? 4 : 2) } catch { return false } }, `No ${phase} child started: ${errors}`)
    for (const pid of pids) assert.equal(await live(pid), true)
    const started = Date.now()
    child.kill(signal)
    await waitFor(() => result !== undefined, `Host did not stop within adapter's five-second grace period: ${errors}`, 4500)
    assert.equal(result.signal, null, errors)
    assert.equal(result.code, 0, errors)
    await waitFor(async () => (await Promise.all(pids.map(live))).every(value => !value), `Live ${phase} descendants survived ${signal}`)
    assert.equal(Date.now() - started < 5000, true)
    // The immediate child was waited for, rather than left as an orphan zombie.
    for (const pid of pids.filter((_, index) => index % 2 === 0)) await assert.rejects(readFile(`/proc/${pid}/stat`), { code: "ENOENT" })
  })
}
