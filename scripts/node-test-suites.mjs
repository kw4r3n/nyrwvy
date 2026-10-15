// Suite ownership is directory-based; the runner and audit share this discovery.
import assert from "node:assert/strict"
import { execFileSync } from "node:child_process"
import { existsSync } from "node:fs"
import { join } from "node:path"
import { fileURLToPath } from "node:url"

export const repositoryRoot = fileURLToPath(new URL("../", import.meta.url))
export const nodeSuiteRules = [
  { suite: "fast-contracts", matches: path => /^(?:\.opencode\/tests|scripts\/tests)\/[^/]+\.test\.mjs$/.test(path) },
  { suite: "fast-adapters", matches: path => /^tools\/nyrwvy-agent\/.*\.test\.mjs$/.test(path) },
  { suite: "fast-mcp", matches: path => /^tools\/nyrwvy-mcp\/.*\.test\.mjs$/.test(path) },
  { suite: "extended", matches: path => /^\.opencode\/tests-extended\/.*\.test\.mjs$/.test(path) },
  // Example-local tests are explicit manual entry points, not routine suites.
  { suite: "manual-examples", matches: path => /^examples\/.*\.test\.mjs$/.test(path) },
]

export function partitionNodeTests(paths, rules = nodeSuiteRules) {
  assert.equal(new Set(paths).size, paths.length, "Duplicate Node test identities")
  const suites = Object.fromEntries(rules.map(({ suite }) => [suite, []]))
  for (const path of [...paths].sort()) {
    const owners = rules.filter(rule => rule.matches(path))
    assert.equal(owners.length, 1, `${path}: expected one Node suite owner, found ${owners.map(rule => rule.suite).join(", ") || "none"}`)
    suites[owners[0].suite].push(path)
  }
  return suites
}

export function discoverNodeTests(root = repositoryRoot) {
  // Include new untracked tests, exclude ignored/generated artifacts and removed files.
  // Git pathspecs find nested tests too, so new directories cannot disappear silently.
  const paths = execFileSync("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "*.test.mjs"], { cwd: root, encoding: "utf8" })
    .split("\0").filter(path => path && existsSync(join(root, path)))
  return partitionNodeTests([...new Set(paths)])
}
