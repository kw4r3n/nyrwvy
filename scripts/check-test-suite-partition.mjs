// Opt-in discovery audit after `dotnet build Nyrwvy.slnx -c Release`.
// Does not run tests or maintain a second historical list of case names/counts.
import assert from "node:assert/strict"
import { execFileSync } from "node:child_process"
import { fileURLToPath } from "node:url"

const cwd = fileURLToPath(new URL("../", import.meta.url))
function discover(project, settings) {
  const output = execFileSync("dotnet", ["test", project, "-c", "Release", "--no-build", "--no-restore", "--list-tests",
    ...(settings ? ["--settings", settings] : [])], { cwd, encoding: "utf8" })
  const names = output.split(/\r?\n/).filter(line => /^ {4}\S/.test(line)).map(line => line.trim())
  assert.ok(names.length, `No tests discovered for ${project} / ${settings ?? "all"}`)
  assert.equal(new Set(names).size, names.length, "Duplicate test identities across projects")
  return new Set(names)
}
const fast = discover("tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj", "tests/fast.runsettings")
const extended = discover("Nyrwvy.slnx", "tests/extended.runsettings")
const all = discover("Nyrwvy.slnx")
assert.deepEqual([...fast].filter(name => extended.has(name)), [], "Fast and extended suites overlap")
assert.deepEqual([...new Set([...fast, ...extended])].sort(), [...all].sort(), "Some tests have no suite ownership")
console.log(`Suite partition is disjoint and exhaustive: ${fast.size} fast, ${extended.size} extended.`)
