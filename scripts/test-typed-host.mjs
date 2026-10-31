import { spawnSync } from "node:child_process"
import { dirname, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const zig = process.env.NYRWVY_ZIG || "zig"
const filters = [
  "typed host reads",
  "fresh committed and stale proposal",
  "selected typed dependencies retain source member order",
  "operation intermediates",
  "typed operation name lookup",
  "path-copy edits",
  "candidate shape failures",
  "run declarations hide imported private members",
  "mismatch references report visible ownership and sources",
  "seeded UUIDv7 payloads and restart guard",
  "unseeded IDs use the host clock and secure entropy",
  "temporary directories use the configured root and are distinct",
  "confined recipe paths",
]
const modules = [
  ["model_reader", "tools/zig/model-reader/main.zig"],
  ["snapshot_reader", "tools/zig/snapshot-reader/main.zig"],
  ["zig_emitter", "tools/zig/emitter/main.zig"],
  ["csharp_emitter", "tools/zig/csharp-emitter/root.zig"],
  ["viewer_asset", "src/Nyrwvy.Viewer/asset.zig"],
  ["build_info", "tools/zig/host/build_info.zig"],
]
const commands = [
  ["test", "-O", "ReleaseSafe", "tools/zig/model-reader/view.zig"],
  ["test", "-O", "ReleaseSafe", ...filters.flatMap(filter => ["--test-filter", filter]),
    "--test-runner", "tools/zig/host/test_runner.zig", "--dep", "host=root",
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
