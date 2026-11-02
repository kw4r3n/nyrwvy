import { spawnSync } from "node:child_process"
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs"
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
for (const args of commands) {
  if (process.platform === "darwin") args.splice(1, 0, "-lc")
  const result = spawnSync(zig, args, { cwd: root, stdio: "inherit", timeout: 300_000 })
  if (result.error) {
    console.error(`Zig host test failed: ${result.error.message}`)
    process.exit(1)
  }
  if (result.status !== 0) process.exit(result.status ?? 1)
}
