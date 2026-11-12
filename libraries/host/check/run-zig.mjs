// Checks host.zig against the real OS: builds a program that imports host.json, emits a Zig library with
// host.zig.json, and calls its exports from a Zig test with a checking allocator. Linux, needs zig and sh.
// Usage: node libraries/host/check/run-zig.mjs [<host executable>] (default: the Zig host, `npm run build:host`)
import { spawn, spawnSync } from "node:child_process"
import { hostExchange, hostSend } from "../../host-client.mjs"
import { copyFileSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-host-zig-check-"))
process.on("exit", () => rmSync(work, { recursive: true, force: true }))
for (const file of ["host.json", "host.zig", "host.zig.json"]) copyFileSync(join(here, "..", file), join(work, file))
const snapshot = join(work, "check.json")
writeFileSync(snapshot + ".backends.json", JSON.stringify({ targets: ["csharp"], zigTables: ["host.zig.json"],
  exports: ["readOr", "readBytesOr", "write", "env", "exitCode", "stdout", "stderr", "runError", "spawned",
    "openShell", "openShellAt", "ptyRead", "ptyWrite", "ptyResize", "ptyForeground", "ptyClose",
    "lockPath", "lockFail", "lockAcquire", "lockRelease", "lockPathOf",
    "rename", "exists", "clockNow", "slept"] }))

const cli = spawn(executable, ["create", "--snapshot", snapshot, "--dependency", join(work, "host.json")], { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const exchange = hostExchange(cli)
const send = hostSend(exchange)
const str = { kind: "primitive", primitive: "string" }
const int = { kind: "primitive", primitive: "int64" }
const P = name => ({ kind: "parameter", name })
const text = value => ({ kind: "stringLiteral", value })
const invoke = (capability, operation, args = {}) => ({ kind: "capabilityInvoke", capabilityValue: P("cap"), capability, operation,
  arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
// A name alone is a String parameter; [name, type] gives its type.
const define = async (displayName, capability, resultType, names, body) => {
  const parameters = [{ displayName: "cap", type: { kind: "capability", name: capability } },
    ...names.map(name => typeof name === "string" ? { displayName: name, type: str } : { displayName: name[0], type: name[1] })]
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
// Ptys.open takes a List and a Map, which have no C form, so this builds them (TERM set by the program).
const pty = { kind: "capability", name: "Pty" }
const generic = (name, ...args) => ({ kind: "builtinGeneric", generic: name, arguments: args })
const bytes = { kind: "primitive", primitive: "bytes" }
await define("openShell", "Ptys", generic("result", pty, str), ["program", "script"], invoke("Ptys", "open", {
  argv: { kind: "listConstruct", itemType: str, items: [P("program"), text("-c"), P("script")] },
  environment: { kind: "mapInsert", map: { kind: "mapEmpty", keyType: str, valueType: str }, key: text("TERM"), value: text("xterm-256color") },
  cols: { kind: "int64Literal", value: "100" }, rows: { kind: "int64Literal", value: "30" } }))
await define("openShellAt", "Ptys", generic("result", pty, str), ["program", "script", "dir"], invoke("Ptys", "openAt", {
  argv: { kind: "listConstruct", itemType: str, items: [P("program"), text("-c"), P("script")] },
  environment: { kind: "mapInsert", map: { kind: "mapEmpty", keyType: str, valueType: str }, key: text("TERM"), value: text("xterm-256color") },
  cols: { kind: "int64Literal", value: "100" }, rows: { kind: "int64Literal", value: "30" },
  cwd: { kind: "optionSome", value: P("dir") } }))
await define("ptyRead", "Pty", generic("option", bytes), [], invoke("Pty", "read"))
await define("ptyWrite", "Pty", { kind: "primitive", primitive: "unit" }, [["data", bytes]], invoke("Pty", "write", { data: P("data") }))
await define("ptyResize", "Pty", { kind: "primitive", primitive: "unit" }, [["cols", int], ["rows", int]], invoke("Pty", "resize", { cols: P("cols"), rows: P("rows") }))
await define("ptyForeground", "Pty", generic("option", str), [], invoke("Pty", "foregroundCommand"))
await define("ptyClose", "Ptys", generic("option", int), [["pty", pty]], invoke("Ptys", "close", { pty: P("pty") }))
// Locks: the UseResource bodies get the Lock handle as their one unbound parameter (here, `cap`).
const lock = { kind: "capability", name: "Lock" }
const strResult = generic("result", str, str)
await define("lockPathBody", "Lock", strResult, [], { kind: "resultOk", errorType: str, value: invoke("Lock", "path") })
await define("lockFailBody", "Lock", strResult, [], { kind: "resultErr", okType: str, error: text("body failed") })
const lockUse = body => ({ kind: "useResource", acquire: invoke("Locks", "acquire", { path: P("path") }),
  releaseCapability: P("cap"), capability: "Locks", releaseOperation: "release", body, context: [] })
await define("lockPath", "Locks", strResult, ["path"], lockUse("lockPathBody"))
await define("lockFail", "Locks", strResult, ["path"], lockUse("lockFailBody"))
await define("lockAcquire", "Locks", generic("result", lock, str), ["path"], invoke("Locks", "acquire", { path: P("path") }))
await define("lockRelease", "Locks", { kind: "primitive", primitive: "unit" }, [["lock", lock]], invoke("Locks", "release", { lock: P("lock") }))
await define("lockPathOf", "Lock", str, [], invoke("Lock", "path"))
await define("rename", "Fs", str, ["from", "to"], orError(invoke("Fs", "rename", { from: P("from"), to: P("to") })))
await define("exists", "Fs", str, ["path"], { kind: "conditional", condition: invoke("Fs", "exists", { path: P("path") }), whenTrue: text("yes"), whenFalse: text("no") })
await define("clockNow", "Clock", int, [], invoke("Clock", "now"))
await define("slept", "Clock", int, [["ms", int]], { kind: "bind", value: invoke("Clock", "sleep", { milliseconds: P("ms") }), body: P("ms") })
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

// Pty.read returns at most 64 KiB, so its buffer is that large.
fn checkRead(pty: ?*NyHandle, chunk: *[64 * 1024]u8) !?[]const u8 {
    var some = false;
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyRead(pty, &some, chunk, chunk.len, &n));
    return if (some) chunk[0..n] else null;
}
var check_chunk: [64 * 1024]u8 = undefined;
var check_output: [4096]u8 = undefined;
fn checkReadUntil(pty: ?*NyHandle, marker: []const u8) ![]const u8 {
    var len: usize = 0;
    while (std.mem.indexOf(u8, check_output[0..len], marker) == null) {
        const chunk = try checkRead(pty, &check_chunk) orelse break;
        @memcpy(check_output[len..][0..chunk.len], chunk);
        len += chunk.len;
    }
    return check_output[0..len];
}
fn checkOpen(program: []const u8, script: []const u8) !?*NyHandle {
    var pty: ?*NyHandle = null;
    const status = ny_openShell(program.ptr, program.len, script.ptr, script.len, &pty);
    if (status == 4) return null;
    try std.testing.expectEqual(@as(c_int, 0), status);
    return pty;
}
fn checkFailure(expected: []const u8, status: c_int) !void {
    try std.testing.expectEqual(@as(c_int, 1), status);
    var len: usize = 0;
    const message = ny_last_failure(&len);
    try std.testing.expectEqualStrings(expected, message[0..len]);
}
fn checkReader(pty: ?*NyHandle, some: *bool) void {
    var chunk: [64 * 1024]u8 = undefined;
    var n: usize = 0;
    // A failed read counts as Some, so only a clean None passes.
    if (ny_ptyRead(pty, some, &chunk, chunk.len, &n) != 0) some.* = true;
}
fn checkSleep(ms: i64) void {
    std.Io.Clock.Duration.sleep(.{ .clock = .awake, .raw = .fromMilliseconds(ms) }, std.testing.io) catch {};
}
test "pty on Zig" {
    ny_values = check_values.allocator();
    const pty = (try checkOpen("sh", "stty size; echo $TERM; read x; stty size; exit 4")).?;
    try std.testing.expect(std.mem.indexOf(u8, try checkReadUntil(pty, "xterm-256color"), "30 100") != null);
    var some = false;
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyForeground(pty, &some, &check_buffer, check_buffer.len, &n));
    try std.testing.expect(some);
    try std.testing.expectEqualStrings("sh", check_buffer[0..n]);
    const fds = try checkCall(ny_stdout, "for f in /proc/self/fd/*; do readlink $f; done");
    try std.testing.expect(std.mem.indexOf(u8, fds, "ptmx") == null and std.mem.indexOf(u8, fds, "/dev/pts") == null);
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyResize(pty, 120, 40));
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyWrite(pty, "\\n", 1));
    try std.testing.expect(std.mem.indexOf(u8, try checkReadUntil(pty, "40 120"), "40 120") != null);
    try std.testing.expectEqual(null, try checkRead(pty, &check_chunk));
    var code: i64 = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyClose(pty, &some, &code));
    try std.testing.expect(some);
    try std.testing.expectEqual(@as(i64, 4), code);
    try checkFailure("Handle is not open", ny_ptyRead(pty, &some, &check_chunk, check_chunk.len, &n));
    try checkFailure("Handle is not open", ny_ptyClose(pty, &some, &code));
    try std.testing.expectEqual(@as(c_int, 1), ny_ptyResize(null, 80, 24));

    // snap's pattern: the loop thread blocks in read while another thread's calls go on and then close.
    const blocked = (try checkOpen("sh", "exec sleep 30")).?;
    var reader_some = true;
    const reader = try std.Thread.spawn(.{}, checkReader, .{ blocked, &reader_some });
    checkSleep(200);
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyResize(blocked, 80, 24));
    try std.testing.expectEqualStrings("unset", try checkCall(ny_env, "NY_HOST_CHECK_ABSENT"));
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyClose(blocked, &some, &code));
    try std.testing.expect(!some);
    reader.join();
    try std.testing.expect(!reader_some);

    try std.testing.expectEqual(null, try checkOpen("nyr-host-no-such-program", ""));
    var len: usize = 0;
    const message = ny_last_failure(&len);
    try std.testing.expectEqualStrings("FileNotFound", message[0..len]);
    const stubborn = (try checkOpen("sh", "trap '' HUP; while :; do sleep 1; done")).?;
    checkSleep(200);
    const start = std.Io.Clock.awake.now(std.testing.io);
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyClose(stubborn, &some, &code));
    try std.testing.expect(!some);
    try std.testing.expect(start.durationTo(std.Io.Clock.awake.now(std.testing.io)).toMilliseconds() < 5000);

    // openAt starts the child in the given directory; a missing one fails the open.
    const at_dir = ${JSON.stringify(work)};
    var at_pty: ?*NyHandle = null;
    try std.testing.expectEqual(@as(c_int, 0), ny_openShellAt("sh".ptr, 2, "pwd".ptr, 3, at_dir.ptr, at_dir.len, &at_pty));
    try std.testing.expect(std.mem.indexOf(u8, try checkReadUntil(at_pty, at_dir), at_dir) != null);
    var at_some = false;
    var at_code: i64 = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_ptyClose(at_pty, &at_some, &at_code));
    try std.testing.expect(at_some and at_code == 0);
    const bad_dir = "/nyr-host-no-such-dir";
    var bad_pty: ?*NyHandle = null;
    try std.testing.expectEqual(@as(c_int, 4), ny_openShellAt("sh".ptr, 2, "pwd".ptr, 3, bad_dir.ptr, bad_dir.len, &bad_pty));
    try std.testing.expectEqual(@as(usize, 0), ny_open_handles());
    try std.testing.expectEqual(@as(usize, 0), check_values.detectLeaks());
}

fn checkRename(from: []const u8, to: []const u8) ![]const u8 {
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_rename(from.ptr, from.len, to.ptr, to.len, &check_buffer, check_buffer.len, &n));
    return check_buffer[0..n];
}
test "rename, exists and clock on Zig" {
    ny_values = check_values.allocator();
    const a = ${JSON.stringify(join(work, "a.txt"))};
    const b = ${JSON.stringify(join(work, "b.txt"))};
    try std.testing.expectEqualStrings("ok", try checkWrite(a, "hello"));
    try std.testing.expectEqualStrings("ok", try checkWrite(b, "old"));
    try std.testing.expectEqualStrings("ok", try checkRename(a, b));
    try std.testing.expectEqualStrings("hello", try checkCall(ny_readOr, b));
    try std.testing.expectEqualStrings("no", try checkCall(ny_exists, a));
    try std.testing.expectEqualStrings("yes", try checkCall(ny_exists, b));
    try std.testing.expectEqualStrings("no", try checkCall(ny_exists, ${JSON.stringify(work)}));
    try std.testing.expectEqualStrings("FileNotFound", try checkRename(a, b));
    var secs: i64 = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_clockNow(&secs));
    const real = std.Io.Clock.real.now(std.testing.io).toSeconds();
    try std.testing.expect(secs >= real - 5 and secs <= real + 5);
    var out: i64 = 0;
    const start = std.Io.Clock.awake.now(std.testing.io);
    try std.testing.expectEqual(@as(c_int, 0), ny_slept(150, &out));
    try std.testing.expectEqual(@as(i64, 150), out);
    try std.testing.expect(start.durationTo(std.Io.Clock.awake.now(std.testing.io)).toMilliseconds() >= 140);
    try std.testing.expectEqual(@as(usize, 0), check_values.detectLeaks());
}

test "locks on Zig" {
    ny_values = check_values.allocator();
    const path = ${JSON.stringify(join(work, "cooldown.lock"))};
    var n: usize = 0;
    // UseResource: acquire, run the body (which reads the handle's path), release.
    try std.testing.expectEqual(@as(c_int, 0), ny_lockPath(path.ptr, path.len, &check_buffer, check_buffer.len, &n));
    try std.testing.expectEqualStrings(path, check_buffer[0..n]);
    try std.testing.expectEqual(@as(usize, 0), ny_open_handles());
    // A body that returns Err still releases, so the same path can be taken again.
    try std.testing.expectEqual(@as(c_int, 4), ny_lockFail(path.ptr, path.len, &check_buffer, check_buffer.len, &n));
    try std.testing.expectEqual(@as(usize, 0), ny_open_handles());
    try std.testing.expectEqual(@as(c_int, 0), ny_lockPath(path.ptr, path.len, &check_buffer, check_buffer.len, &n));
    // A held lock refuses a second acquire without waiting; release frees it.
    var handle: ?*NyHandle = null;
    try std.testing.expectEqual(@as(c_int, 0), ny_lockAcquire(path.ptr, path.len, &handle));
    try std.testing.expect(handle != null);
    var second: ?*NyHandle = null;
    try std.testing.expectEqual(@as(c_int, 4), ny_lockAcquire(path.ptr, path.len, &second));
    var flen: usize = 0;
    const fmsg = ny_last_failure(&flen);
    try std.testing.expectEqualStrings("LockHeld", fmsg[0..flen]);
    try std.testing.expectEqual(@as(c_int, 0), ny_lockPathOf(handle, &check_buffer, check_buffer.len, &n));
    try std.testing.expectEqualStrings(path, check_buffer[0..n]);
    try std.testing.expectEqual(@as(c_int, 0), ny_lockRelease(handle));
    try checkFailure("Handle is not open", ny_lockPathOf(handle, &check_buffer, check_buffer.len, &n));
    try std.testing.expectEqual(@as(usize, 0), ny_open_handles());
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
process.exit(status)
