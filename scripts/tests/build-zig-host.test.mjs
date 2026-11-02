import test from "node:test"
import assert from "node:assert/strict"
import { execFileSync, spawnSync } from "node:child_process"
import { copyFile, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"

test("build publishes only a successful ReleaseSafe host with the required modules", async t => {
  if (process.platform === "win32") return t.skip("fixture compiler uses a Unix executable script")
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-build-host-"))
  t.after(() => rm(root, { recursive: true, force: true }))
  await mkdir(join(root, "scripts"))
  await copyFile(fileURLToPath(new URL("../build-zig-host.mjs", import.meta.url)), join(root, "scripts/build-zig-host.mjs"))
  const bin = join(root, "tools/zig/host/bin")
  await mkdir(bin, { recursive: true })
  await writeFile(join(bin, "nyrwvy-host"), "old")
  const head = execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim()
  const git = join(root, "git")
  await writeFile(git, `#!${process.execPath}\nprocess.stdout.write(process.env.FAKE_HEAD);\n`, { mode: 0o700 })
  const compiler = join(root, "fake-zig")
  await writeFile(compiler, `#!${process.execPath}\nconst fs = require('node:fs');\nfs.writeFileSync(process.env.CALLS, JSON.stringify({cwd: process.cwd(), args: process.argv.slice(2), info: fs.readFileSync(process.argv.find(x => x.startsWith('-Mbuild_info=')).slice(13), 'utf8')}));\nfs.writeFileSync(process.argv.find(x => x.startsWith('-femit-bin=')).slice(11), 'new');\nprocess.exit(Number(process.env.FAIL));\n`, { mode: 0o700 })
  const run = (fail, fakeHead = head, zig = compiler) => spawnSync(process.execPath, [join(root, "scripts/build-zig-host.mjs")], { env: { ...process.env, NYRWVY_ZIG: zig, CALLS: join(root, "calls"), FAIL: String(fail), FAKE_HEAD: fakeHead, PATH: `${root}:${process.env.PATH}` } })
  assert.notEqual(run(1).status, 0)
  assert.equal(await readFile(join(bin, "nyrwvy-host"), "utf8"), "old")
  assert.deepEqual(await readdir(bin), ["nyrwvy-host"])
  assert.equal(run(0).status, 0)
  assert.equal(await readFile(join(bin, "nyrwvy-host"), "utf8"), "new")
  const stamp = async () => JSON.parse(await readFile(join(bin, "nyrwvy-host.json"), "utf8"))
  assert.equal((await stamp()).commit, head)
  const { cwd, args, info } = JSON.parse(await readFile(join(root, "calls"), "utf8"))
  // The commit is in the stamp, not the binary, so a new commit with the same sources reuses the build.
  const commitless = `pub const build = "nyrwvy-zig-host";\npub const repository_root: ?[]const u8 = ${JSON.stringify(root)};\n`
  assert.equal(info, commitless)
  await rm(join(root, "calls"))
  assert.equal(run(0, "f".repeat(40)).status, 0)
  await assert.rejects(readFile(join(root, "calls")))
  assert.equal((await stamp()).commit, "f".repeat(40))
  await mkdir(join(root, "tools/zig/host"), { recursive: true })
  await writeFile(join(root, "tools/zig/host/main.zig"), "changed")
  assert.equal(cwd, root)
  assert.equal(args[0], "build-exe")
  for (const name of ["model_reader", "snapshot_reader", "zig_emitter", "csharp_emitter", "viewer_asset", "build_info"]) {
    assert.ok(args.includes(name))
    assert.ok(args.some(arg => arg.startsWith(`-M${name}=`)))
  }
  assert.equal(args.filter(arg => arg === "ReleaseSafe").length, 7)
  assert.notEqual(run(0, head, join(root, "missing-zig")).status, 0)
  assert.equal(await readFile(join(bin, "nyrwvy-host"), "utf8"), "new")
  assert.equal(run(0, "invalid").status, 0)
  assert.equal(JSON.parse(await readFile(join(root, "calls"), "utf8")).info, commitless)
  assert.equal((await stamp()).commit, undefined)
  assert.deepEqual(await readdir(bin), ["nyrwvy-host", "nyrwvy-host.json"])
})

test("interrupting a compiler wrapper kills its child before removing temporary output", { timeout: 10000 }, async t => {
  if (process.platform === "win32") return t.skip("fixture uses Unix process groups")
  const { spawn } = await import("node:child_process")
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-build-interrupt-"))
  t.after(() => rm(root, { recursive: true, force: true }))
  await mkdir(join(root, "scripts"))
  await copyFile(fileURLToPath(new URL("../build-zig-host.mjs", import.meta.url)), join(root, "scripts/build-zig-host.mjs"))
  const bin = join(root, "tools/zig/host/bin")
  await mkdir(bin, { recursive: true })
  await writeFile(join(bin, "nyrwvy-host"), "old")
  const compiler = join(root, "wrapper")
  await writeFile(compiler, `#!${process.execPath}\nconst {spawn} = require('node:child_process');\nspawn(${JSON.stringify(process.execPath)}, ['-e', ${JSON.stringify("const fs=require('node:fs');fs.writeFileSync(process.env.READY,'ready');setTimeout(()=>fs.writeFileSync(process.env.LATE,'late'),1000)")}], {stdio:'ignore'});\nsetInterval(()=>{},1000);\n`, { mode: 0o700 })
  const builder = spawn(process.execPath, [join(root, "scripts/build-zig-host.mjs")], {
    env: { ...process.env, NYRWVY_ZIG: compiler, READY: join(root, "ready"), LATE: join(root, "late") }, stdio: "ignore",
  })
  const closed = new Promise(resolve => builder.once("close", resolve))
  for (let i = 0; i < 200; i++) {
    if (await readFile(join(root, "ready"), "utf8").catch(() => "") === "ready") break
    if (i === 199) throw new Error("compiler child did not start")
    await new Promise(resolve => setTimeout(resolve, 10))
  }
  builder.kill("SIGTERM")
  assert.notEqual(await closed, 0)
  await new Promise(resolve => setTimeout(resolve, 1200))
  await assert.rejects(readFile(join(root, "late")), { code: "ENOENT" })
  assert.equal(await readFile(join(bin, "nyrwvy-host"), "utf8"), "old")
  assert.deepEqual(await readdir(bin), ["nyrwvy-host"])
})
