import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"

// The threaded-state-loop proof (examples/threaded-state-loop): a million-turn tail-recursive loop
// over a threaded state value, measured on the Zig backend. It builds Zig libraries, so it lives in
// the extended suite; one run takes about a minute.
const skip = process.platform !== "linux" ? "the harness reads /proc/self/status to measure RSS" : false

test("the threaded state loop keeps its memory flat over a million turns", { timeout: 300000, skip }, () => {
  const check = spawnSync(process.execPath, ["examples/threaded-state-loop/run.mjs"], { cwd: process.cwd(), encoding: "utf8", timeout: 280_000 })
  assert.equal(check.status, 0, check.stdout + check.stderr)
})
