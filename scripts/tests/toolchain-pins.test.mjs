import test from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"

const read = path => readFileSync(new URL(`../../${path}`, import.meta.url), "utf8")

test("ci.yml pins the Zig and Bun versions that mise.toml and package.json declare", () => {
  const ci = read(".github/workflows/ci.yml")
  const zig = /^zig = "([^"]+)"/m.exec(read("mise.toml"))?.[1]
  assert.ok(zig, "mise.toml has no zig version")
  const setups = [...ci.matchAll(/uses: mlugg\/setup-zig@[^\n]*\n\s+with:\n\s+version: '([^']+)'/g)]
  assert.ok(setups.length > 0, "no setup-zig step found")
  for (const [, version] of setups) assert.equal(version, zig)
  const bun = /"bun-types": "([^"]+)"/.exec(read("package.json"))?.[1]
  const bunVersions = [...ci.matchAll(/bun-version: '([^']+)'/g)].map(match => match[1])
  assert.ok(bunVersions.length > 0, "no setup-bun step found")
  for (const version of bunVersions) assert.equal(version, bun)
})
