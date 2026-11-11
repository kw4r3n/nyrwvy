import test from "node:test"
import assert from "node:assert/strict"
import { spawn, execFileSync } from "node:child_process"
import { open, mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { existsSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { setTimeout as delay } from "node:timers/promises"

const host = resolve(process.env.NYRWVY_PARENT_WATCHDOG_HOST || "tools/zig/host/bin/nyrwvy-host")

async function waitFor(check, message, milliseconds = 6000) {
  const until = Date.now() + milliseconds
  while (Date.now() < until) {
    if (await check()) return
    await delay(25)
  }
  assert.fail(message)
}

async function live(pid) {
  try { return !/\) Z /.test(await readFile(`/proc/${pid}/stat`, "utf8")) }
  catch (error) { if (error.code === "ENOENT") return false; throw error }
}

const skip = process.platform !== "linux" ? "the parent watchdog test uses a fifo and /proc"
  : process.env.NYRWVY_HOST_EXECUTABLE && !process.env.NYRWVY_PARENT_WATCHDOG_HOST ? "explicit alternate host selected; run with NYRWVY_PARENT_WATCHDOG_HOST to verify the native host" : false

// The adapters spawn the host detached, so a killed adapter sends it no signal. The host's
// stdin is a fifo this test holds open the whole time, so an end-of-input cannot be what ends
// the host: only the parent-pid watch can.
test("the host exits after its parent is killed, even with its stdin held open", { timeout: 20000, skip }, async () => {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-parent-watchdog-"))
  const fifo = join(root, "stdin.fifo")
  const snapshot = join(root, "session.nyrwvy")
  const pidFile = join(root, "host.pid")
  const intermediaryScript = join(root, "intermediary.cjs")
  execFileSync("mkfifo", [fifo])
  await writeFile(intermediaryScript, `
const fs = require("node:fs")
const { spawn } = require("node:child_process")
const fd = fs.openSync(process.argv[2], "r")
const host = spawn(process.argv[3], ["create", "--snapshot", process.argv[4]], { stdio: [fd, "pipe", "pipe"] })
host.stdout.resume()
host.stderr.resume()
fs.writeFileSync(process.argv[5], String(host.pid))
setInterval(() => {}, 60000)
`)
  const parent = spawn(process.execPath, [intermediaryScript, fifo, host, snapshot, pidFile], { stdio: "ignore" })
  let writer
  try {
    writer = await open(fifo, "w")
    await waitFor(async () => existsSync(pidFile) && existsSync(`${snapshot}.lock`), "the host did not open its session")
    const hostPid = Number(await readFile(pidFile, "utf8"))
    assert.ok(await live(hostPid), "the host is not running")
    parent.kill("SIGKILL")
    await waitFor(async () => !await live(hostPid), "the host kept running after its parent was killed")
  } finally {
    if (writer) await writer.close().catch(() => {})
    parent.kill("SIGKILL")
    await rm(root, { recursive: true, force: true })
  }
})
