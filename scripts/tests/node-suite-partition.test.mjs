import assert from "node:assert/strict"
import { execFileSync, spawnSync } from "node:child_process"
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import test from "node:test"
import { discoverNodeTests, partitionNodeTests, repositoryRoot, selectNodeTestShard } from "../node-test-suites.mjs"

test("Node ownership discovers every maintained test, including new files, and rejects gaps or overlaps", () => {
  const suites = discoverNodeTests()
  for (const [name, files] of Object.entries(suites)) assert.ok(files.length, `${name} must not be empty`)
  const fixture = mkdtempSync(join(tmpdir(), "nyrwvy-node-discovery-"))
  try {
    execFileSync("git", ["init", "--quiet", fixture])
    writeFileSync(join(fixture, ".gitignore"), "artifacts/\nnode_modules/\n")
    for (const path of [".opencode/tests/smoke.test.mjs", ".opencode/tests-extended/pipeline.test.mjs", "artifacts/stale.test.mjs", "node_modules/vendor/foreign.test.mjs"]) {
      mkdirSync(dirname(join(fixture, path)), { recursive: true })
      writeFileSync(join(fixture, path), "")
    }
    assert.deepEqual(discoverNodeTests(fixture)["fast-contracts"], [".opencode/tests/smoke.test.mjs"])
    assert.deepEqual(discoverNodeTests(fixture).extended, [".opencode/tests-extended/pipeline.test.mjs"])
    writeFileSync(join(fixture, "unassigned.test.mjs"), "")
    assert.throws(() => discoverNodeTests(fixture), /unassigned.test.mjs.*found none/)
    assert.throws(() => partitionNodeTests(["one.test.mjs"], [
      { suite: "fast", matches: () => true }, { suite: "extended", matches: () => true },
    ]), /found fast, extended/)
    assert.throws(() => partitionNodeTests(["one.test.mjs", "one.test.mjs"]), /Duplicate Node test identities/)
  } finally { rmSync(fixture, { recursive: true, force: true }) }
})

test("Node discovery rejects a tracked test file missing from the working tree", () => {
  const fixture = mkdtempSync(join(tmpdir(), "nyrwvy-node-missing-"))
  const git = (...args) => execFileSync("git", ["-C", fixture, ...args])
  try {
    git("init", "--quiet")
    for (const path of [".opencode/tests/kept.test.mjs", "scripts/tests/gone.test.mjs"]) {
      mkdirSync(dirname(join(fixture, path)), { recursive: true })
      writeFileSync(join(fixture, path), "")
    }
    git("add", ".")
    rmSync(join(fixture, "scripts/tests/gone.test.mjs"))
    assert.throws(() => discoverNodeTests(fixture), error =>
      /missing from the working tree: scripts\/tests\/gone\.test\.mjs\./.test(error.message)
      && /git restore -- scripts\/tests\/gone\.test\.mjs/.test(error.message))
    // A staged removal is intended, and the index no longer lists the file.
    git("rm", "--quiet", "--cached", "scripts/tests/gone.test.mjs")
    assert.deepEqual(discoverNodeTests(fixture)["fast-contracts"], [".opencode/tests/kept.test.mjs"])
  } finally { rmSync(fixture, { recursive: true, force: true }) }
})

test("Node shards are disjoint and exhaustive and reject malformed selectors", () => {
  const files = ["a", "b", "c", "d", "e", "f", "g"]
  const shards = [1, 2, 3].map(index => selectNodeTestShard(files, `${index}/3`))
  assert.deepEqual(shards, [["a", "d", "g"], ["b", "e"], ["c", "f"]])
  for (const shard of [undefined, "", "0/3", "4/3", "1/0", "1.5/3", "-1/3", "1/8", "2"]) {
    assert.throws(() => selectNodeTestShard(files, shard), /Shard|exceed/, String(shard))
  }
})

test("maintained entry points select shared discovery and keep ordinary and MCP coverage in root npm", () => {
  const pkg = JSON.parse(readFileSync(join(repositoryRoot, "package.json"), "utf8"))
  assert.equal(pkg.scripts.test, "npm run test:adapters && npm run test:node")
  assert.equal(pkg.scripts["test:adapters"], "node scripts/run-node-tests.mjs fast-adapters")
  assert.equal(pkg.scripts["test:node"], "node scripts/run-node-tests.mjs fast-contracts fast-mcp")
  assert.equal(pkg.scripts["test:node:extended"], "node scripts/run-node-tests.mjs extended")
  const fast = discoverNodeTests()["fast-contracts"]
  for (const path of ["nyrwvy-authoring-workflow", "nyrwvy-authoring-transport"]) {
    assert.ok(fast.includes(`.opencode/tests/${path}.test.mjs`), `${path} smoke contract stays fast`)
  }
  for (const file of ["scripts/test-fast.fish", "scripts/test-extended.fish", ".github/workflows/ci.yml"]) {
    assert.doesNotMatch(readFileSync(join(repositoryRoot, file), "utf8"), /--test\s+\.opencode\/tests\/nyrwvy-authoring-\*/)
  }
})

test("suite wrappers forward dotnet arguments once and propagate either stage's failure", {
  skip: spawnSync("fish", ["--version"]).error?.code === "ENOENT" ? "fish is required for wrapper contracts" : false,
}, () => {
  const fixture = mkdtempSync(join(tmpdir(), "nyrwvy-suite-wrapper-"))
  const commands = join(fixture, "commands")
  const repo = join(fixture, "repository with spaces")
  const calls = join(fixture, "calls")
  mkdirSync(commands)
  mkdirSync(join(repo, "scripts"), { recursive: true })
  for (const command of ["dotnet", "node", "npm"]) {
    writeFileSync(join(commands, command), `#!/bin/sh\nprintf '%s\\n' '${command}' "$PWD" "$@" >> "$SUITE_CALLS"\nexit "$SUITE_${command.toUpperCase()}_STATUS"\n`, { mode: 0o755 })
  }
  try {
    for (const [name, suite] of [["fast", "fast-contracts"], ["extended", "extended"]]) {
      const wrapper = `test-${name}.fish`
      copyFileSync(join(repositoryRoot, "scripts", wrapper), join(repo, "scripts", wrapper))
      const run = (dotnetStatus, nodeStatus, args = ["--no-build", "--filter", "Category=fixture with spaces"], host = undefined, npmStatus = 0) => {
        writeFileSync(calls, "")
        const result = spawnSync("fish", ["--no-config", join(repo, "scripts", wrapper), ...args], {
          cwd: fixture, encoding: "utf8",
          env: { ...process.env, PATH: `${commands}:${process.env.PATH}`, SUITE_CALLS: calls, SUITE_DOTNET_STATUS: String(dotnetStatus), SUITE_NODE_STATUS: String(nodeStatus), SUITE_NPM_STATUS: String(npmStatus), NYRWVY_HOST_EXECUTABLE: host },
        })
        if (result.error) throw result.error
        return { status: result.status, calls: readFileSync(calls, "utf8").trim().split("\n"), stderr: result.stderr }
      }
      const success = run(0, 0)
      assert.equal(success.status, 0, success.stderr)
      const node = success.calls.indexOf("node")
      assert.ok(node > 0)
      assert.deepEqual(success.calls.slice(-4), ["node", repo, join(repo, "scripts/run-node-tests.mjs"), suite])
      assert.equal(success.calls.filter(value => value === "--no-build").length, 1)
      assert.ok(success.calls.slice(0, node).includes("Category=fixture with spaces"))
      assert.ok(success.calls.includes(join(repo, `tests/${name}.runsettings`)))
      const failedDotnet = run(23, 0)
      assert.equal(failedDotnet.status, 23)
      assert.ok(!failedDotnet.calls.includes("node"))
      assert.equal(run(0, 17).status, 17)
      const builds = run(0, 0, [])
      assert.equal(builds.status, 0)
      assert.ok(builds.calls.includes("npm"))
      assert.ok(builds.calls.includes("build:host"))
      const emptyHost = run(0, 0, [], "")
      assert.equal(emptyHost.status, 0)
      assert.ok(emptyHost.calls.includes("build:host"))
      assert.ok(!run(0, 0, [], "/explicit/host").calls.includes("npm"))
      const failedBuild = run(0, 0, [], undefined, 19)
      assert.equal(failedBuild.status, 19)
      assert.ok(!failedBuild.calls.includes("node"))
    }
  } finally { rmSync(fixture, { recursive: true, force: true }) }
})

test("typed host gate selects both Zig suites and stops after a failure", () => {
  const fixture = mkdtempSync(join(tmpdir(), "nyrwvy-typed-host-"))
  const repo = join(fixture, "repository with spaces")
  const calls = join(fixture, "calls")
  const preload = join(fixture, "fake zig.cjs")
  mkdirSync(join(repo, "scripts"), { recursive: true })
  for (const file of ["test-typed-host.mjs", "test-typed-host.fish"]) copyFileSync(join(repositoryRoot, "scripts", file), join(repo, "scripts", file))
  writeFileSync(preload, `const fs = require('node:fs');\nif (!process.argv[1]?.endsWith('test-typed-host.mjs')) {\n  fs.appendFileSync(process.env.TYPED_CALLS, JSON.stringify({cwd: process.cwd(), args: ['test', ...process.argv.slice(2)]}) + '\\n');\n  process.exit(Number(process.env.TYPED_FAIL) === fs.readFileSync(process.env.TYPED_CALLS, 'utf8').trim().split('\\n').length ? 23 : 0);\n}\n`)
  const run = (fail, fish = false) => {
    writeFileSync(calls, "")
    const command = fish ? "fish" : process.execPath
    const args = fish ? ["--no-config", join(repo, "scripts/test-typed-host.fish")] : [join(repo, "scripts/test-typed-host.mjs")]
    const result = spawnSync(command, args, {
      cwd: fixture, encoding: "utf8", env: { ...process.env, NODE_OPTIONS: `--require="${preload}"`, NYRWVY_ZIG: process.execPath, TYPED_CALLS: calls, TYPED_FAIL: String(fail) },
    })
    if (result.error) throw result.error
    return { ...result, calls: readFileSync(calls, "utf8").trim().split("\n").filter(Boolean).map(line => JSON.parse(line)) }
  }
  try {
    const success = run(0)
    assert.equal(success.status, 0, success.stderr)
    assert.equal(success.calls.length, 2)
    assert.ok(success.calls.every(call => call.cwd === repo))
    const linkerArgs = process.platform === "darwin" ? ["-lc"] : []
    assert.deepEqual(success.calls[0].args, ["test", ...linkerArgs, "-O", "ReleaseSafe", "tools/zig/model-reader/view.zig"])
    const args = success.calls[1].args
    assert.deepEqual(args.slice(0, 31 + linkerArgs.length), ["test", ...linkerArgs, "-O", "ReleaseSafe", "--test-filter", "typed host reads", "--test-filter", "fresh committed and stale proposal", "--test-filter", "selected typed dependencies retain source member order", "--test-filter", "operation intermediates", "--test-filter", "typed operation name lookup", "--test-filter", "path-copy edits", "--test-filter", "candidate shape failures", "--test-filter", "run declarations hide imported private members", "--test-filter", "mismatch references report visible ownership and sources", "--test-filter", "seeded UUIDv7 payloads and restart guard", "--test-filter", "unseeded IDs use the host clock and secure entropy", "--test-filter", "temporary directories use the configured root and are distinct", "--test-filter", "confined recipe paths", "--test-runner", "tools/zig/host/test_runner.zig"])
    for (const arg of ["host=root", "-Mroot=tools/zig/host/main.zig", "-Mbuild_info=tools/zig/host/build_info.zig"]) assert.ok(args.includes(arg))
    const failure = run(1)
    assert.equal(failure.status, 23)
    assert.equal(failure.calls.length, 1)
    const secondFailure = run(2)
    assert.equal(secondFailure.status, 23)
    assert.equal(secondFailure.calls.length, 2)
    if (!spawnSync("fish", ["--version"]).error) assert.deepEqual(run(0, true).calls, success.calls)
  } finally { rmSync(fixture, { recursive: true, force: true }) }
})
