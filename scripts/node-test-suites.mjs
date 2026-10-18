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
  // Include new untracked tests and exclude ignored/generated artifacts. Git pathspecs find
  // nested tests too, so new directories cannot disappear silently.
  const paths = [...new Set(execFileSync("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "*.test.mjs"], { cwd: root, encoding: "utf8" })
    .split("\0").filter(Boolean))]
  // A listed path missing from disk is tracked in the index but deleted in the working tree.
  // Dropping it silently would shrink a suite unnoticed, so it is an error; an intended
  // removal is staged with `git rm`, after which the index no longer lists it.
  const missing = paths.filter(path => !existsSync(join(root, path))).sort()
  if (missing.length) {
    throw new Error(`Tracked Node test files are missing from the working tree: ${missing.join(", ")}. `
      + `Restore them with \`git restore -- ${missing.join(" ")}\`, or stage an intended removal with \`git rm\`.`)
  }
  return partitionNodeTests(paths)
}

// Round-robin over the sorted discovery list, so shards stay exhaustive and disjoint
// without a maintained duration table; `index/total` is 1-based.
export function selectNodeTestShard(files, shard) {
  const match = /^([1-9]\d*)\/([1-9]\d*)$/.exec(shard ?? "")
  assert.ok(match, `Shard must be index/total with positive integers, got ${shard}`)
  const [index, total] = [Number(match[1]), Number(match[2])]
  assert.ok(index <= total, `Shard index ${index} exceeds total ${total}`)
  assert.ok(total <= files.length, `${total} shards exceed ${files.length} discovered Node test files`)
  return files.filter((_, position) => position % total === index - 1)
}
