// Runs the model-cooldown port on the Zig backend against the real OS: emits a Zig library from a
// workspace importing host.json and json.json, and drives its coolModel export from a Zig test.
// Usage: node examples/model-cooldown/run-zig.mjs [<host executable>]
import { spawn, spawnSync } from "node:child_process"
import { hostExchange, hostSend } from "../../libraries/host-client.mjs"
import { copyFileSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { author } from "./author.mjs"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-cooldown-zig-"))
process.on("exit", () => rmSync(work, { recursive: true, force: true }))
for (const file of ["host.json", "host.zig", "host.zig.json"]) copyFileSync(join(repo, "libraries/host", file), join(work, file))
copyFileSync(join(repo, "libraries/json/json.json"), join(work, "json.json"))
const snapshot = join(work, "check.json")
writeFileSync(snapshot + ".backends.json", JSON.stringify({ targets: ["csharp"], zigTables: ["host.zig.json"], exports: ["coolModel"] }))
const cli = spawn(executable, ["create", "--snapshot", snapshot, "--dependency", join(work, "host.json"), "--dependency", join(work, "json.json")],
  { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const send = hostSend(hostExchange(cli))
await author(send)
const emitted = await send({ op: "emitZigLibrary", output: "check.zig" })
cli.stdin.end()

const harness = `
extern "c" fn flock(fd: c_int, operation: c_int) c_int;
var cool_values: std.heap.DebugAllocator(.{}) = .init;
var cool_buffer: [4096]u8 = undefined;
var ta: std.mem.Allocator = undefined;
fn cool(dir: []const u8, model: []const u8, seconds: i64, reason: []const u8) ![]const u8 {
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_coolModel(dir.ptr, dir.len, model.ptr, model.len, seconds, reason.ptr, reason.len, &cool_buffer, cool_buffer.len, &n));
    return cool_buffer[0..n];
}
fn coolNow() i64 { return std.Io.Clock.real.now(std.testing.io).toSeconds(); }
fn coolSeed(dir: []const u8, name: []const u8, text: []const u8) !void {
    try std.Io.Dir.cwd().writeFile(std.testing.io, .{ .sub_path = try std.fs.path.join(ta, &.{ dir, name }), .data = text });
}
fn coolRead(dir: []const u8, name: []const u8) ![]const u8 {
    return std.Io.Dir.cwd().readFileAlloc(std.testing.io, try std.fs.path.join(ta, &.{ dir, name }), ta, .unlimited);
}
fn coolEntry(dir: []const u8, model: []const u8) !?std.json.Value {
    const parsed = try std.json.parseFromSlice(std.json.Value, ta, try coolRead(dir, "model-cooldown.json"), .{});
    return parsed.value.object.get(model);
}
fn coolField(object: std.json.Value, name: []const u8) i64 { return object.object.get(name).?.integer; }
fn coolMkdir(path: []const u8) !void { try std.Io.Dir.cwd().createDirPath(std.testing.io, path); }

test "model cooldown on Zig" {
    ny_values = cool_values.allocator();
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    ta = arena.allocator();
    const root = ${JSON.stringify(work)};
    // From no file: the entry appears with until = now + seconds and since = now.
    const d1 = root ++ "/s1"; try coolMkdir(d1);
    const before = coolNow();
    try std.testing.expectEqualStrings("", try cool(d1, "m1", 600, "first"));
    const after = coolNow();
    const e1 = (try coolEntry(d1, "m1")).?;
    try std.testing.expect(coolField(e1, "until") >= before + 600 and coolField(e1, "until") <= after + 600);
    try std.testing.expect(coolField(e1, "since") >= before and coolField(e1, "since") <= after);
    try std.testing.expectEqualStrings("first", e1.object.get("reason").?.string);
    const raw1 = try coolRead(d1, "model-cooldown.json");
    try std.testing.expect(std.mem.indexOf(u8, raw1, "\\"until\\":") != null);
    try std.testing.expect(std.mem.indexOf(u8, raw1, ".0") == null);
    // A shorter request does not lower until and keeps since; the reason is replaced.
    _ = try cool(d1, "m1", 10, "second");
    const e1b = (try coolEntry(d1, "m1")).?;
    try std.testing.expectEqual(coolField(e1, "until"), coolField(e1b, "until"));
    try std.testing.expectEqual(coolField(e1, "since"), coolField(e1b, "since"));
    try std.testing.expectEqualStrings("second", e1b.object.get("reason").?.string);
    // Control runs in the reason collapse to one space.
    _ = try cool(d1, "m1", 600, "a\\n\\nb\\rc\\x1fd");
    try std.testing.expectEqualStrings("a b c d", (try coolEntry(d1, "m1")).?.object.get("reason").?.string);
    // Removing an entry that is not there writes nothing.
    const d2 = root ++ "/s2"; try coolMkdir(d2);
    try coolSeed(d2, "model-cooldown.json", "{\\"keep\\":{\\"until\\":9999999999,\\"since\\":5,\\"reason\\":\\"x\\"}}");
    const seeded = try coolRead(d2, "model-cooldown.json");
    _ = try cool(d2, "absent", 0, "");
    try std.testing.expectEqualStrings(seeded, try coolRead(d2, "model-cooldown.json"));
    // Removing an entry writes the file without it.
    _ = try cool(d2, "keep", 0, "");
    try std.testing.expect((try coolEntry(d2, "keep")) == null);
    // Expired entries drop on the next write; an active one survives untouched.
    const d3 = root ++ "/s3"; try coolMkdir(d3);
    const past = coolNow() - 100;
    try coolSeed(d3, "model-cooldown.json", try std.fmt.allocPrint(ta,
        "{{\\"old\\":{{\\"until\\":{d},\\"since\\":{d},\\"reason\\":\\"x\\"}},\\"live\\":{{\\"until\\":9999999999,\\"since\\":7,\\"reason\\":\\"y\\"}}}}", .{ past, past }));
    _ = try cool(d3, "new", 60, "n");
    try std.testing.expect((try coolEntry(d3, "old")) == null);
    try std.testing.expectEqual(@as(i64, 7), coolField((try coolEntry(d3, "live")).?, "since"));
    // An expired entry of the same model starts over: its since is now, not the old one.
    _ = try cool(d3, "old", 60, "n");
    try std.testing.expect(coolField((try coolEntry(d3, "old")).?, "since") >= past + 100);
    // Corrupt JSON moves to .bad and starts empty; the result says so.
    const d4 = root ++ "/s4"; try coolMkdir(d4);
    try coolSeed(d4, "model-cooldown.json", "not json{");
    const note = try cool(d4, "m1", 60, "r");
    try std.testing.expect(std.mem.indexOf(u8, note, ".bad") != null);
    try std.testing.expectEqualStrings("not json{", try coolRead(d4, "model-cooldown.json.bad"));
    try std.testing.expect((try coolEntry(d4, "m1")) != null);
    // Valid JSON that is not an object is treated as empty, without a .bad move.
    const d5 = root ++ "/s5"; try coolMkdir(d5);
    try coolSeed(d5, "model-cooldown.json", "[1,2]");
    _ = try cool(d5, "m1", 60, "r");
    try std.testing.expect((try coolEntry(d5, "m1")) != null);
    const bad = std.Io.Dir.cwd().openFile(std.testing.io, d5 ++ "/model-cooldown.json.bad", .{}) catch null;
    try std.testing.expect(bad == null);
    // A held lock is retried until it frees, then the update goes through.
    const d6 = root ++ "/s6"; try coolMkdir(d6);
    const lock_path = try std.fs.path.join(ta, &.{ d6, "model-cooldown.json.lock" });
    const lock_c = try ta.dupeSentinel(u8, lock_path, 0);
    const fd = std.c.open(lock_c.ptr, .{ .ACCMODE = .RDWR, .CREAT = true, .CLOEXEC = true }, @as(std.c.mode_t, 0o666));
    try std.testing.expect(fd >= 0);
    try std.testing.expectEqual(@as(c_int, 0), flock(fd, 2 | 4));
    const thread = try std.Thread.spawn(.{}, coolHold, .{ d6, fd });
    thread.join();
    try std.testing.expect((try coolEntry(d6, "m1")) != null);
    try std.testing.expectEqual(@as(usize, 0), ny_open_handles());
    try std.testing.expectEqual(@as(usize, 0), cool_values.detectLeaks());
}
fn coolHold(dir: []const u8, fd: c_int) void {
    const T = struct { fn call(d: []const u8) void {
        var n: usize = 0;
        const status = ny_coolModel(d.ptr, d.len, "m1", 2, 60, "r", 1, &cool_buffer, cool_buffer.len, &n);
        std.debug.assert(status == 0);
    } };
    const t = std.Thread.spawn(.{}, T.call, .{dir}) catch return;
    std.Io.Clock.Duration.sleep(.{ .clock = .awake, .raw = .fromMilliseconds(400) }, std.testing.io) catch {};
    _ = std.c.close(fd);
    t.join();
}
`
writeFileSync(join(work, "harness.zig"), readFileSync(emitted.path, "utf8") + harness)
const zig = process.env.NYRWVY_ZIG || "zig"
const steps = [["build-lib", "-dynamic", "-lc", "-O", "ReleaseSafe", emitted.path, `-femit-bin=${join(work, "libcheck.so")}`],
  ["test", "-lc", "-O", "ReleaseSafe", join(work, "harness.zig")]]
let status = 0
for (const step of steps) {
  const run = spawnSync(zig, step, { cwd: repo, stdio: "inherit" })
  status = run.status ?? 1
  if (status !== 0) break
}
process.exit(status)
