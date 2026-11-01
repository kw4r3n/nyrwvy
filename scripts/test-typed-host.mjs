import { spawnSync } from "node:child_process"
import { dirname, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const zig = process.env.NYRWVY_ZIG || "zig"
// Every host unit test.
const filters = [
  "C# runner keeps recursive codecs finite and argument values out of source",
  "a child registered after shutdown is killed even after the monitor has finished",
  "binary64 output follows the wire's round-trip decimal convention",
  "bind-c parse and LP64 classifications",
  "bind-c specs decode BOM text and reject numeric constants before publication",
  "bind-c syntax diagnostics retain JsonNode positions and accessed UTF16 strings",
  "both child streams drain beyond pipe capacity without deadlock",
  "child output is bounded while both redirected streams drain",
  "compiler child is bounded even after closing its output streams",
  "compiler diagnostics decode malformed text before a UTF16 prefix reaches the wire",
  "confined recipe paths reject parent traversal on each platform",
  "dependency pair restores a failed snapshot write and preserves recovery on rollback failure",
  "empty facade and Unicode name checks",
  "equatability of an irregularly recursive type terminates",
  "exclusive snapshot creation preserves an existing destination and removes its temporary",
  "facade name collisions and reserved fields",
  "generated output preflight refuses invalid paths without publication",
  "import visibility and external capability roots",
  "live-set replacement commits and rejection stay bounded and retain stale proposals",
  "managed text detects BOMs and replaces malformed maximal subparts",
  "mapping override names preserve extension policy",
  "mapping rejects pointer constants and preserves failure diagnostic precedence",
  "named values preserve wire field order until semantic canonicalization",
  "native executable identity is a stable digest of the running file",
  "nested fake response diagnostics include the operation response and field path",
  "owned children are registered before execution and reaped",
  "owned process trees close inherited output pipes on timeout",
  "pin final reread reports snapshot and manifest I/O failures without publication",
  "plain constructor size checks include nested 257-field values",
  "portable atomic publication preserves an existing target and cleans temporary files",
  "produced recursive values follow the whole envelope JSON depth limit",
  "publication identity distinguishes files and detects filesystem aliases",
  "retained dependency owns typed graph interner and provenance after source arena release",
  "run declarations hide imported private members without changing the model",
  "run values keep exact integers, binary64 signs, and text tokens",
  "runner dispatch name preserves arbitrary UTF-8 stable IDs",
  "scalar decoding preserves negative zero and rejects unsigned negative zero",
  "seeded UUIDv7 payloads and restart guard",
  "signature remap restores parameter and function IDs and generic binding types",
  "startup accepts only exact CLI forms and payloads",
  "sweep removes session directories whose host is gone",
  "temporary directories use the configured root and are distinct",
  "termination kills an owned group before a CPU-bound serving task can unwind",
  "typed declarations match tree encoding with provenance",
  "typed edits copy changed domain records and preserve untouched functions",
  "typed focus and viewer use the generation without a value model",
  "typed generic declaration rewrites group references and fresh type parameters",
  "typed materializer allocates both result bindings before arm nodes and resolves lexical slots",
  "typed materializer creates postorder nodes and fresh parameter references",
  "typed operation adds capability operations and binds native implementations",
  "typed operation decoding resolves member names and constructs native union variants",
  "typed operation transaction renames a declaration and preserves the committed record",
  "typed operations add resolve and retire a missing argument without editing the base",
  "typed operations bind and remove a product field using existing value nodes",
  "typed operations materialize field recipe plans as native graph nodes",
  "typed references and binding scopes do not read a value model",
  "typed removal narrows shared union arms and derives native field bindings",
  "typed search finds visible names and literals",
  "union clone remaps nested results and separate node binding domains while retaining literals",
  "unseeded IDs use the host clock and secure entropy",
  "verification classification ignores display names and keeps failures/concurrency conservative",
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
  ["test", "-O", "ReleaseSafe", "tools/zig/model-reader/main.zig"],
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
