// Checks host.zig against the real OS: builds a program that imports host.json, emits a Zig library with
// host.zig.json, and calls its exports from a Zig test with a checking allocator. Linux, needs zig and sh.
// Usage: node libraries/host/check/run-zig.mjs [<host executable>] (default: the Zig host, `npm run build:host`)
import { spawn, spawnSync } from "node:child_process"
import { createInterface } from "node:readline"
import { copyFileSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
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

// Appended to the emitted source, so it calls the exports directly and can set a checking allocator.
const harness = `
const CheckText = @TypeOf(ny_readOr);
var check_values: std.heap.DebugAllocator(.{}) = .init;
var check_buffer: [256]u8 = undefined;
fn checkCall(function: *const CheckText, input: []const u8) ![]const u8 {
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), function(input.ptr, input.len, &check_buffer, check_buffer.len, &n));
    return check_buffer[0..n];
}
fn checkWrite(path: []const u8, data: []const u8) ![]const u8 {
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_write(path.ptr, path.len, data.ptr, data.len, &check_buffer, check_buffer.len, &n));
    return check_buffer[0..n];
}
test "host library on Zig" {
    ny_values = check_values.allocator();
    const path = ${JSON.stringify(join(work, "file.txt"))};
    try std.testing.expectEqualStrings("ok", try checkWrite(path, "\\xc3\\xaf\\xc2\\xbb\\xc2\\xbfhi\\xc3\\xbf"));
    try std.testing.expectEqualStrings("hi\\u{fffd}", try checkCall(ny_readOr, path));
    try std.testing.expectEqualStrings("missing", try checkCall(ny_readOr, path ++ ".absent"));
    try std.testing.expectEqualStrings("FileNotFound", try checkWrite("/nonexistent-directory/x", "a"));
    try std.testing.expectEqualStrings("\\xc3\\xaf\\xc2\\xbb\\xc2\\xbfhi\\xc3\\xbf", try checkCall(ny_readBytesOr, path));
    try std.testing.expectEqualStrings("value", try checkCall(ny_env, "NY_HOST_CHECK"));
    try std.testing.expectEqualStrings("unset", try checkCall(ny_env, "NY_HOST_CHECK_ABSENT"));
    var code: i64 = 0;
    const script = "printf out; printf err >&2; printf %s \\"$NY_HOST_CHECK\\"; exit 3";
    try std.testing.expectEqual(@as(c_int, 0), ny_exitCode(script.ptr, script.len, &code));
    try std.testing.expectEqual(@as(i64, 3), code);
    try std.testing.expectEqualStrings("outvalue", try checkCall(ny_stdout, script));
    try std.testing.expectEqualStrings("err", try checkCall(ny_stderr, script));
    try std.testing.expectEqualStrings("FileNotFound", try checkCall(ny_runError, "/nonexistent-program"));
    const marker = ${JSON.stringify(join(work, "spawned"))};
    try std.testing.expectEqualStrings("ok", try checkCall(ny_spawned, "printf done > " ++ marker));
    var tries: usize = 0;
    while (tries < 200) : (tries += 1) {
        // The shell creates the file before writing to it, so wait for the content, not the file.
        if (std.mem.eql(u8, "done", try checkCall(ny_readOr, marker))) break;
        try std.Io.Clock.Duration.sleep(.{ .clock = .awake, .raw = .fromMilliseconds(10) }, std.testing.io);
    }
    try std.testing.expectEqualStrings("done", try checkCall(ny_readOr, marker));
    try std.testing.expectEqual(@as(usize, 0), check_values.detectLeaks());
}
`
writeFileSync(join(work, "harness.zig"), readFileSync(emitted.path, "utf8") + harness)
const zig = process.env.NYRWVY_ZIG || "zig"
const steps = [["build-lib", "-dynamic", "-lc", "-O", "ReleaseSafe", emitted.path, `-femit-bin=${join(work, "libcheck.so")}`],
  ["test", "-lc", "-O", "ReleaseSafe", join(work, "harness.zig")]]
let status = 0
for (const step of steps) {
  const run = spawnSync(zig, step, { cwd: repo, stdio: "inherit", env: { ...process.env, NY_HOST_CHECK: "value" } })
  status = run.status ?? 1
  if (status !== 0) break
}
rmSync(work, { recursive: true, force: true })
process.exit(status)
