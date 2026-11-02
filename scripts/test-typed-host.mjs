import { spawn } from "node:child_process"
import { existsSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const zig = process.env.NYRWVY_ZIG || "zig"
const modules = [
  ["model_reader", "tools/zig/model-reader/main.zig"],
  ["snapshot_reader", "tools/zig/snapshot-reader/main.zig"],
  ["zig_emitter", "tools/zig/emitter/main.zig"],
  ["csharp_emitter", "tools/zig/csharp-emitter/root.zig"],
  ["viewer_asset", "src/Nyrwvy.Viewer/asset.zig"],
  ["build_info", "tools/zig/host/build_info.zig"],
]
// The emitter's runtime is text embedded into generated programs; its test is that text plus a test block.
const runtimeTest = join(mkdtempSync(join(tmpdir(), "nyrwvy-runtime-")), "runtime.zig")
writeFileSync(runtimeTest, ["runtime.zig", "case_mapping.zig", "runtime_test.zig"].map(name => readFileSync(join(root, "tools/zig/emitter", name), "utf8")).join("\n"))
const commands = [
  ["test", "-O", "ReleaseSafe", runtimeTest],
  ["test", "-O", "ReleaseSafe", "tools/zig/model-reader/main.zig"],
  ["test", "-O", "ReleaseSafe", "tools/zig/emitter/main.zig"],
  // native.zig imports root.zig, so this runs both files' tests.
  ["test", "-O", "ReleaseSafe", "tools/zig/csharp-emitter/native.zig"],
  // Every test in the host's files; no filter list, so a new test cannot be skipped silently.
  ["test", "-O", "ReleaseSafe", "--test-runner", "tools/zig/host/test_runner.zig", "--dep", "host=root",
    ...modules.flatMap(([name]) => ["--dep", name]), "-Mroot=tools/zig/host/main.zig",
    ...modules.flatMap(([name, path]) => ["-O", "ReleaseSafe", `-M${name}=${path}`])],
]
// Zig only discovers tests in files it analyzes, so a host file that main.zig's test block does not
// import has tests that never run; every declared host test must start (the runner exits non-zero on a failure).
const hostDir = join(root, "tools/zig/host")
const declared = existsSync(hostDir) ? readdirSync(hostDir).filter(name => name.endsWith(".zig"))
  .flatMap(name => [...readFileSync(join(hostDir, name), "utf8").matchAll(/^test "((?:[^"\\]|\\.)*)"/gm)].map(match => `${name.slice(0, -4)}.test.${match[1]}...`)) : []
// zig test starts the compiled test program as its own child, so killing zig alone leaves that program
// running (once for hours at full CPU); run each command in its own process group and kill the group.
const groups = new Set()
const killGroup = pid => { try { process.kill(process.platform === "win32" ? pid : -pid, "SIGKILL") } catch {} }
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => { groups.forEach(killGroup); process.exit(1) })
function run(args, host) {
  return new Promise(done => {
    const child = spawn(zig, args, { cwd: root, stdio: ["ignore", "inherit", host ? "pipe" : "inherit"], detached: process.platform !== "win32" })
    groups.add(child.pid)
    let stderr = "", error
    child.stderr?.setEncoding("utf8").on("data", text => { stderr += text })
    const timer = setTimeout(() => { error = new Error("timed out after 300 s"); killGroup(child.pid) }, 300_000)
    child.on("error", failure => { clearTimeout(timer); done({ status: null, stderr, error: failure }) })
    child.on("close", status => { clearTimeout(timer); groups.delete(child.pid); done({ status, stderr, error }) })
  })
}
for (const args of commands) {
  if (process.platform === "darwin") args.splice(1, 0, "-lc")
  const host = args === commands.at(-1)
  const result = await run(args, host)
  if (host) process.stderr.write(result.stderr ?? "")
  if (result.error) {
    console.error(`Zig host test failed: ${result.error.message}`)
    process.exit(1)
  }
  if (result.status !== 0) process.exit(result.status ?? 1)
  const missing = host ? declared.filter(line => !result.stderr.includes(line)) : []
  if (missing.length) {
    console.error(`Host tests that did not run (import their file in tools/zig/host/main.zig's test block):\n${missing.join("\n")}`)
    process.exit(1)
  }
}
