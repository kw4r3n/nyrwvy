// Runs the examples whose runPure cases the Zig backend supports on that backend: each example
// authors its bundle once (C#, the default target), then its saved cases are replayed after the
// workspace's `<snapshot>.backends.json` names Zig. Bundles live in a temporary directory.
// cleanup-shield, exception-boundary and task-cancellation-outcome replay on Zig inside their own
// extended Node tests, which already author their bundles. config-request-batch stays C#-only (owner decision).
import { spawnSync } from "node:child_process"
import { mkdir, mkdtemp, readdir, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const examples = [
  "bounded-stream", "function-abort",
  "dynamic-worker-collector", "bounded-worker-stream", "scoped-result-handoff",
]
const replayArgs = (example, directory) => ["--replay", directory]
// function-abort has no replay mode and accepts a non-empty directory, so its sidecar is written first.
const presetOnly = new Set(["function-abort"])
const only = process.argv.slice(2)
const parent = await mkdtemp(join(tmpdir(), "nyrwvy-examples-zig-"))
const failed = []
async function targetZig(directory) {
  const sidecars = (await readdir(directory)).filter(name => name.endsWith(".backends.json"))
  if (!sidecars.length) throw new Error(`${directory}: no backends sidecar to retarget`)
  for (const name of sidecars) await writeFile(join(directory, name), '{"targets": ["zig"]}\n')
}
function run(example, args) {
  const result = spawnSync(process.execPath, [`examples/${example}/run.mjs`, ...args], { cwd: root, encoding: "utf8", timeout: 600_000 })
  if (result.status === 0) return true
  console.error(`FAIL: ${example} ${args.join(" ")}\n${result.error ?? ""}${result.stdout.slice(-2000)}${result.stderr.slice(-4000)}`)
  return false
}
try {
  for (const example of examples.filter(name => !only.length || only.includes(name))) {
    const directory = join(parent, example)
    let passed
    if (presetOnly.has(example)) {
      await mkdir(directory)
      await writeFile(join(directory, "workspace.json.backends.json"), '{"targets": ["zig"]}\n')
      passed = run(example, [directory])
    } else passed = run(example, [directory]) && await targetZig(directory).then(() => true) && run(example, replayArgs(example, directory))
    console.log(`${passed ? "PASS" : "FAIL"}: ${example} on Zig`)
    if (!passed) failed.push(example)
  }
} finally {
  await rm(parent, { recursive: true, force: true })
}
if (failed.length) {
  console.error(`Examples failing on Zig: ${failed.join(", ")}`)
  process.exit(1)
}
