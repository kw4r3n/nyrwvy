import test from "node:test"
import assert from "node:assert/strict"
import { spawn, spawnSync } from "node:child_process"
import { existsSync } from "node:fs"
import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { fileURLToPath } from "node:url"

const host = fileURLToPath(new URL("../../tools/zig/host/bin/nyrwvy-host", import.meta.url))

/** Opens `snapshot` read-only and returns the startup failure and how long it took. */
function openReadOnly(snapshot) {
  const started = Date.now()
  const result = spawnSync(host, ["open", "--snapshot", snapshot, "--read-only"], { input: "", encoding: "utf8", timeout: 20_000 })
  return { status: result.status, stderr: result.stderr, elapsed: Date.now() - started }
}

test("a read-only open retries a failed read only while a writer holds the workspace lock", async t => {
  if (process.platform === "win32" || spawnSync("flock", ["--version"]).error) return t.skip("needs flock(1)")
  if (!existsSync(host)) return t.skip("run npm run build:host first")
  const directory = await mkdtemp(join(tmpdir(), "nyrwvy-read-only-lock-"))
  t.after(() => rm(directory, { recursive: true, force: true }))
  const snapshot = join(directory, "app.json")
  await writeFile(snapshot, "{bad")
  await writeFile(`${snapshot}.lock`, "")

  const free = openReadOnly(snapshot)
  assert.notEqual(free.status, 0)
  assert.match(free.stderr, /not valid JSON/)
  assert.ok(free.elapsed < 1_000, `no writer, so no retry: took ${free.elapsed} ms`)

  const holder = spawn("flock", [`${snapshot}.lock`, "sleep", "1.5"], { stdio: "ignore" })
  t.after(() => holder.kill())
  await new Promise(resolve => setTimeout(resolve, 200))
  const held = openReadOnly(snapshot)
  assert.notEqual(held.status, 0)
  assert.match(held.stderr, /not valid JSON/)
  assert.ok(held.elapsed >= 1_000, `a held lock is waited out: took ${held.elapsed} ms`)
})
