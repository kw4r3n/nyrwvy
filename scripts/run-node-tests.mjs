import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { discoverNodeTests, repositoryRoot } from "./node-test-suites.mjs"

const selected = process.argv.slice(2)
const allowed = new Set(["fast-contracts", "fast-adapters", "fast-mcp", "extended"])
assert.ok(selected.length && new Set(selected).size === selected.length, "Select one or more distinct Node suites")
assert.ok(selected.every(suite => allowed.has(suite)), "Unknown or manual Node suite; use the documented example command for manual tests")
const suites = discoverNodeTests()
const files = selected.flatMap(suite => {
  assert.ok(suites[suite].length, `No Node tests discovered for ${suite}`)
  return suites[suite]
})
const result = spawnSync(process.execPath, ["--experimental-strip-types", "--test", ...files], { cwd: repositoryRoot, stdio: "inherit" })
if (result.error) throw result.error
if (result.signal) {
  process.kill(process.pid, result.signal)
} else {
  process.exitCode = result.status ?? 1
}
