import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtemp, mkdir, readFile, rm, symlink, access, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { openAuthoring, requestAuthoring, closeAuthoring } from "../tools/nyrwvy-agent/authoring.ts"

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const binary = join(repositoryRoot, "tools/zig/host/bin", `nyrwvy-host${process.platform === "win32" ? ".exe" : ""}`)
const parent = await mkdtemp(join(tmpdir(), "nyrwvy-platform-"))
const workspace = join(parent, "workspace 日本語")
const outside = join(parent, "outside")
await mkdir(workspace)
await mkdir(outside)
await writeFile(join(outside, "recipe.json"), JSON.stringify({ kind: "parameter", name: "value" }))
delete process.env.NYRWVY_HOST_EXECUTABLE

function direct(args, input = '{"id":"status","op":"status"}\n', env = process.env) {
  const result = spawnSync(binary, args, { input, env, encoding: "utf8", timeout: 15_000 })
  assert.ifError(result.error)
  return result
}

try {
  const emptyPath = direct(["create", "--snapshot", join(parent, "without-dotnet.json")], undefined, { ...process.env, PATH: "" })
  assert.equal(emptyPath.status, 0, emptyPath.stderr)
  assert.match(JSON.parse(emptyPath.stdout).result.build, /^nyrwvy-zig-host/)
  await symlink(outside, join(workspace, "escape"), process.platform === "win32" ? "junction" : "dir")
  for (const extension of ["json", "nyrec"]) {
    const context = { sessionID: `platform-${extension}`, worktree: workspace, repositoryRoot }
    const path = join(workspace, `app.${extension}`)
    const request = async input => {
      const reply = JSON.parse(await requestAuthoring(input, context))
      assert.equal(reply.ok, true, JSON.stringify(reply))
      return reply.result
    }
    try {
      const opened = JSON.parse(await openAuthoring("create", `app.${extension}`, context))
      assert.equal(opened.status.ok, true, JSON.stringify(opened))
      assert.match(opened.status.result.build, /^nyrwvy-zig-host/)
      const locked = direct(["open", "--snapshot", path])
      assert.notEqual(locked.status, 0)
      assert.match(locked.stderr, /LockUnavailable/)
      const reader = direct(["open", "--snapshot", path, "--read-only"])
      assert.equal(reader.status, 0, reader.stderr)
      assert.equal(JSON.parse(reader.stdout).ok, true)
      const proposed = await request({
        op: "simulateCreateFunctionByName", displayName: "echo日本語",
        parameters: [{ displayName: "value", type: { kind: "primitive", primitive: "string" } }],
        resultType: { kind: "primitive", primitive: "string" }, requiredEffects: [],
        body: { kind: "parameter", name: "value" },
      })
      await request({ op: "commit", proposalId: proposed.proposalId })
      for (const bodyFile of ["../outside/recipe.json", ...(process.platform === "win32" ? ["..\\outside\\recipe.json"] : [])]) {
        const escaped = JSON.parse(await requestAuthoring({ op: "simulateCreateFunctionByName", displayName: "escaped", parameters: [{ displayName: "value", type: { kind: "primitive", primitive: "string" } }], resultType: { kind: "primitive", primitive: "string" }, requiredEffects: [], bodyFile }, context))
        assert.equal(escaped.ok, false)
        assert.match(escaped.error.message, /must stay inside the snapshot's directory/)
      }
      const bytes = await readFile(path)
      const duplicate = direct(["create", "--snapshot", path])
      assert.notEqual(duplicate.status, 0)
      assert.match(duplicate.stderr, /SnapshotFileAlreadyExists/)
      assert.deepEqual(await readFile(path), bytes)
      await request({ op: "viewFunction", function: "echo日本語", output: `views/${extension}.html` })
      assert.match(await readFile(join(workspace, "views", `${extension}.html`), "utf8"), /<!doctype html>/i)
      for (const output of ["escape/escaped.html", "../outside/escaped.html"]) {
        const escaped = JSON.parse(await requestAuthoring({ op: "viewFunction", function: "echo日本語", output }, context))
        assert.equal(escaped.ok, false)
        assert.equal(escaped.error.code, "ViewWriteFailed")
      }
      await assert.rejects(access(join(outside, "escaped.html")), { code: "ENOENT" })
      const source = (await request({ op: "emitCSharp", profile: { kind: "empty" } })).source
      for (const [upper, lower] of [[`Generated-${extension}.cs`, `generated-${extension}.cs`], [`Ächo-${extension}.cs`, `ächo-${extension}.cs`]]) {
        const emitted = JSON.parse(await requestAuthoring({ op: "emitCSharp", profile: { kind: "empty" }, output: upper, facade: lower }, context))
        if (emitted.ok) {
          assert.equal(await readFile(join(workspace, upper), "utf8"), source)
          assert.notDeepEqual(await readFile(join(workspace, upper)), await readFile(join(workspace, lower)))
        } else {
          assert.equal(emitted.error.code, "EmitWriteFailed", JSON.stringify(emitted))
          const remaining = await readFile(join(workspace, upper), "utf8").catch(error => { if (error.code !== "ENOENT") throw error })
          if (remaining !== undefined) assert.equal(remaining, source)
        }
      }
      for (const backend of ["zig", "csharp"]) {
        const result = await request({ op: "runPureByName", function: "echo日本語", backend, arguments: ["日本語 🌱"], expected: "日本語 🌱" })
        assert.equal(result.matches, true)
        assert.equal(result.actual.value, "日本語 🌱")
      }
    } finally { await closeAuthoring(context) }
    const reopened = direct(["open", "--snapshot", path, "--read-only"])
    assert.equal(reopened.status, 0, reopened.stderr)
    assert.equal(JSON.parse(reopened.stdout).ok, true)
  }
  console.log(JSON.stringify({ platform: process.platform, arch: process.arch, formats: ["json", "nyrec"], backends: ["zig", "csharp"], locks: true, confinedWrites: true, unicode: true, nativeWithoutDotnet: true }))
} finally { await rm(parent, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }) }
