import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"

// The host library's C# check (libraries/host/check/run.mjs, about 6 s, needs dotnet and the Zig
// session host). The cross-backend lock check (run-cross.mjs) is not here: it is timing-sensitive
// and Node runs test files concurrently.
const skip = process.platform !== "linux" ? "the harness uses the real OS (sh, cat, a pty)" : false

test("the host library runs on C# against the real OS", { timeout: 300000, skip }, () => {
  const check = spawnSync(process.execPath, ["libraries/host/check/run.mjs"], { cwd: process.cwd(), encoding: "utf8", timeout: 280_000 })
  assert.equal(check.status, 0, check.stdout.split("\n").slice(-40).join("\n") + check.stderr)
})
