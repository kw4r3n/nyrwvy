import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"

// The libraries' own checks, in a file of their own so the Node runner starts them beside
// nyrwvy-zig-library.test.mjs instead of after it (each takes 15-55 s of Zig and C# builds).
const skip = process.platform !== "linux" ? "the harness links a Linux .so with -rpath" : false

test("the host library runs on Zig against the real OS", { timeout: 300000, skip }, () => {
  const check = spawnSync(process.execPath, ["libraries/host/check/run-zig.mjs"], { cwd: process.cwd(), encoding: "utf8", timeout: 280_000 })
  assert.equal(check.status, 0, check.stdout + check.stderr)
})

test("the core library runs on both backends through an import", { timeout: 300000, skip }, () => {
  const check = spawnSync(process.execPath, ["libraries/core/check/run.mjs"], { cwd: process.cwd(), encoding: "utf8", timeout: 280_000, env: { ...process.env, NYRWVY_ZIG_RUNNER_OPTIMIZE: "Debug" } })
  assert.equal(check.status, 0, check.stdout + check.stderr)
})
