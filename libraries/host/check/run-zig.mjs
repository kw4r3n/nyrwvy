// Checks host.zig against the real OS: builds a program that imports host.json, emits a Zig library with
// host.zig.json, and calls its exports from a Zig test. Linux, needs zig and sh.
// Usage: node libraries/host/check/run-zig.mjs [<host executable>] (default: the Zig host, `npm run build:host`)
import { spawn, spawnSync } from "node:child_process"
import { createInterface } from "node:readline"
import { copyFileSync, mkdtempSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-host-zig-check-"))
for (const file of ["host.json", "host.zig", "host.zig.json"]) copyFileSync(join(here, "..", file), join(work, file))
const snapshot = join(work, "check.json")
writeFileSync(snapshot + ".backends.json", JSON.stringify({ targets: ["csharp"], zigTables: ["host.zig.json"],
  exports: ["readOr", "readBytesOr", "write", "env", "exitCode", "stdout", "stderr", "runError", "spawned"] }))

const cli = spawn(executable, ["create", "--snapshot", snapshot, "--dependency", join(work, "host.json")], { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const lines = createInterface({ input: cli.stdout })[Symbol.asyncIterator]()
let n = 0
const send = async request => {
  cli.stdin.write(JSON.stringify({ id: String(n++), ...request }) + "\n")
  const reply = JSON.parse((await lines.next()).value)
  if (!reply.ok) throw new Error(`${request.op}: ${JSON.stringify(reply.error)}`)
  return reply.result
}
const str = { kind: "primitive", primitive: "string" }
const int = { kind: "primitive", primitive: "int64" }
const P = name => ({ kind: "parameter", name })
const text = value => ({ kind: "stringLiteral", value })
const invoke = (capability, operation, args = {}) => ({ kind: "capabilityInvoke", capabilityValue: P("cap"), capability, operation,
  arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const define = async (displayName, capability, resultType, names, body) => {
  const parameters = [{ displayName: "cap", type: { kind: "capability", name: capability } }, ...names.map(displayName => ({ displayName, type: str }))]
  const created = await send({ op: "simulateCreateFunctionByName", displayName, exported: true, parameters, resultType, requiredEffects: ["System"], body })
  await send({ op: "commit", proposalId: created.proposalId })
}
const shell = { program: text("sh"), args: { kind: "listConstruct", itemType: str, items: [text("-c"), P("script")] } }
const ran = invoke("Process", "run", shell)
const orError = value => ({ kind: "optionGetOrDefault", option: { kind: "resultErrValue", value }, default: text("ok") })
const field = (name, type, fallback, wrap) => ({ kind: "matchResult", okName: "output", errName: "failure", input: ran,
  okBody: wrap({ kind: "fieldAccess", source: { kind: "patternReference", name: "output" }, type: "ProcessOutput", field: name }),
  errBody: fallback })

await define("readOr", "Fs", str, ["path"], { kind: "resultGetOrDefault", result: invoke("Fs", "readText", { path: P("path") }), default: text("missing") })
await define("readBytesOr", "Fs", str, ["path"], { kind: "bytesToLatin1", value: { kind: "resultGetOrDefault", result: invoke("Fs", "readBytes", { path: P("path") }), default: { kind: "bytesEmpty" } } })
await define("write", "Fs", str, ["path", "data"], orError(invoke("Fs", "writeBytes", { path: P("path"), data: { kind: "bytesFromLatin1", value: P("data") } })))
await define("env", "Env", str, ["name"], { kind: "optionGetOrDefault", option: invoke("Env", "get", { name: P("name") }), default: text("unset") })
await define("exitCode", "Process", int, ["script"], field("exitCode", int, { kind: "int64Literal", value: "-99" }, value => value))
await define("stdout", "Process", str, ["script"], field("stdout", str, { kind: "patternReference", name: "failure" }, value => ({ kind: "bytesToLatin1", value })))
await define("stderr", "Process", str, ["script"], field("stderr", str, { kind: "patternReference", name: "failure" }, value => ({ kind: "bytesToLatin1", value })))
await define("runError", "Process", str, ["program"], orError(invoke("Process", "run", { program: P("program"), args: { kind: "listEmpty", itemType: str } })))
await define("spawned", "Process", str, ["script"], orError(invoke("Process", "spawn", shell)))
const emitted = await send({ op: "emitZigLibrary", output: "check.zig" })
cli.stdin.end()

const harness = `const std = @import("std");
const Text = @TypeOf(ny_readOr);
extern fn ny_readOr(?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
extern fn ny_readBytesOr(?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
extern fn ny_env(?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
extern fn ny_stdout(?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
extern fn ny_stderr(?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
extern fn ny_runError(?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
extern fn ny_spawned(?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
extern fn ny_exitCode(?[*]const u8, usize, *i64) c_int;
extern fn ny_write(?[*]const u8, usize, ?[*]const u8, usize, ?[*]u8, usize, *usize) c_int;
var buffer: [256]u8 = undefined;
fn call(function: *const Text, input: []const u8) ![]const u8 {
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), function(input.ptr, input.len, &buffer, buffer.len, &n));
    return buffer[0..n];
}
fn write(path: []const u8, data: []const u8) ![]const u8 {
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_write(path.ptr, path.len, data.ptr, data.len, &buffer, buffer.len, &n));
    return buffer[0..n];
}
test "host library on Zig" {
    const path = ${JSON.stringify(join(work, "file.txt"))};
    try std.testing.expectEqualStrings("ok", try write(path, "\\xc3\\xaf\\xc2\\xbb\\xc2\\xbfhi\\xc3\\xbf"));
    try std.testing.expectEqualStrings("hi\\u{fffd}", try call(ny_readOr, path));
    try std.testing.expectEqualStrings("missing", try call(ny_readOr, path ++ ".absent"));
    try std.testing.expectEqualStrings("FileNotFound", try write("/nonexistent-directory/x", "a"));
    try std.testing.expectEqualStrings("\\xc3\\xaf\\xc2\\xbb\\xc2\\xbfhi\\xc3\\xbf", try call(ny_readBytesOr, path));
    try std.testing.expectEqualStrings("value", try call(ny_env, "NY_HOST_CHECK"));
    try std.testing.expectEqualStrings("unset", try call(ny_env, "NY_HOST_CHECK_ABSENT"));
    var code: i64 = 0;
    const script = "printf out; printf err >&2; printf %s \\"$NY_HOST_CHECK\\"; exit 3";
    try std.testing.expectEqual(@as(c_int, 0), ny_exitCode(script.ptr, script.len, &code));
    try std.testing.expectEqual(@as(i64, 3), code);
    try std.testing.expectEqualStrings("outvalue", try call(ny_stdout, script));
    try std.testing.expectEqualStrings("err", try call(ny_stderr, script));
    try std.testing.expectEqualStrings("FileNotFound", try call(ny_runError, "/nonexistent-program"));
    const marker = ${JSON.stringify(join(work, "spawned"))};
    try std.testing.expectEqualStrings("ok", try call(ny_spawned, "printf done > " ++ marker));
    var tries: usize = 0;
    while (tries < 200) : (tries += 1) {
        if (!std.mem.eql(u8, "missing", try call(ny_readOr, marker))) break;
        try std.Io.Clock.Duration.sleep(.{ .clock = .awake, .raw = .fromMilliseconds(10) }, std.testing.io);
    }
    try std.testing.expectEqualStrings("done", try call(ny_readOr, marker));
}
`
writeFileSync(join(work, "harness.zig"), harness)
const zig = process.env.NYRWVY_ZIG || "zig"
const library = join(work, "libcheck.so")
const steps = [["build-lib", "-dynamic", "-lc", "-O", "ReleaseSafe", emitted.path, `-femit-bin=${library}`],
  ["test", join(work, "harness.zig"), library, "-lc", "-rpath", work]]
let status = 0
for (const step of steps) {
  const run = spawnSync(zig, step, { cwd: repo, stdio: "inherit", env: { ...process.env, NY_HOST_CHECK: "value" } })
  status = run.status ?? 1
  if (status !== 0) break
}
rmSync(work, { recursive: true, force: true })
process.exit(status)
