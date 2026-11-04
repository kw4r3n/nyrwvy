// Opt-in discovery audit after `dotnet build Nyrwvy.slnx -c Release`.
// Does not run tests or maintain a second historical list of case names/counts.
import assert from "node:assert/strict"
import { execFileSync } from "node:child_process"
import { fileURLToPath } from "node:url"
import { discoverNodeTests } from "./node-test-suites.mjs"

const cwd = fileURLToPath(new URL("../", import.meta.url))
function discover(project, settings) {
  const output = execFileSync("dotnet", ["test", project, "-c", "Release", "--no-build", "--no-restore", "--list-tests",
    ...(settings ? ["--settings", settings] : [])], { cwd, encoding: "utf8" })
  const names = output.split(/\r?\n/).filter(line => /^ {4}\S/.test(line)).map(line => line.trim())
  assert.ok(names.length, `No tests discovered for ${project} / ${settings ?? "all"}`)
  assert.equal(new Set(names).size, names.length, "Duplicate test identities across projects")
  return new Set(names)
}
assert.ok(process.argv.slice(2).every(arg => arg === "--node-only"), "Usage: node scripts/check-test-suite-partition.mjs [--node-only]")
if (!process.argv.includes("--node-only")) {
  const fast = discover("tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj", "tests/fast.runsettings")
  const extended = discover("Nyrwvy.slnx", "tests/extended.runsettings")
  const all = discover("Nyrwvy.slnx")
  assert.deepEqual([...fast].filter(name => extended.has(name)), [], "Fast and extended suites overlap")
  assert.deepEqual([...new Set([...fast, ...extended])].sort(), [...all].sort(), "Some tests have no suite ownership")
  console.log(`xUnit partition is disjoint and exhaustive: ${fast.size} fast, ${extended.size} extended.`)
}
const node = discoverNodeTests(cwd)
// npm run test:bun globs only .opencode/tests/*.test.ts, so a Bun test elsewhere would run nowhere.
const strayBun = execFileSync("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "*.test.ts"], { cwd, encoding: "utf8" })
  .split("\0").filter(path => path && !/^\.opencode\/tests\/[^/]+\.test\.ts$/.test(path))
assert.deepEqual(strayBun, [], "Bun tests outside .opencode/tests/*.test.ts run in no suite")
console.log(`Node ownership is disjoint and exhaustive: ${Object.entries(node).map(([suite, files]) => `${files.length} ${suite}`).join(", ")}.`)
