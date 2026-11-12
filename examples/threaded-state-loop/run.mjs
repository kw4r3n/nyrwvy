// The threaded-state-loop proof: checks step's saved cases with runPure on both backends, then emits
// the Zig library and measures a long tail-recursive loop (allocations, peak live bytes, the runtime
// recording arena's capacity, RSS) for each event code at 100k and 1M turns. The measurements table
// is written as measurements.tsv in the temp dir and printed at the end.
// Usage: node examples/threaded-state-loop/run.mjs [<host executable>]
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
const work = mkdtempSync(join(tmpdir(), "nyr-threaded-state-"))
process.on("exit", () => rmSync(work, { recursive: true, force: true }))
for (const file of ["events.zig", "events.zig.json"]) copyFileSync(join(here, file), join(work, file))
const snapshot = join(work, "check.json")
writeFileSync(snapshot + ".backends.json", JSON.stringify({ targets: ["csharp"], zigTables: ["events.zig.json"], exports: ["run"] }))
const cli = spawn(executable, ["create", "--snapshot", snapshot], { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const send = hostSend(hostExchange(cli))
await author(send)

// step's saved cases on initialState(10): the checksum is 7998, every code but 0 adds exactly one and
// turns becomes 1 — and each code changes exactly its own place, read back by the step* probes (the
// updated index is 1, because the turn count becomes 1 before the place is chosen).
const probes = [
  ["stepCounter", code => code === 1 ? 11 : 10],
  ["stepArr", code => code === 2 ? 12 : 11],
  ["stepTable", code => code === 3 ? 12 : 11],
  ["stepInner", code => code === 4 ? 11 : 10],
  ["stepLeaf", code => code === 5 ? 12 : 11],
  ["stepPhaseN", code => code === 6 ? 11 : 10],
  ["stepPhaseB", code => code === 6 ? 1 : 0],
]
let failed = 0
for (const backend of ["zig", "csharp"]) for (let code = 0; code <= 6; code++) {
  const cases = [["stepChecksum", code === 0 ? 7998 : 7999], ["stepTurns", 1],
    ...probes.map(([fn, expected]) => [fn, expected(code)])]
  for (const [fn, expected] of cases) {
    const result = await send({ op: "runPureByName", function: fn, backend, arguments: [code], expected })
    if (result.matches !== true) { failed++; console.error(`${backend} ${fn}(${code}): ${JSON.stringify(result.actual ?? result).slice(0, 200)}, expected ${expected}`) }
  }
}
if (failed) { console.error(`${failed} saved cases failed`); process.exit(1) }
console.log("step saved cases agree on both backends")

const emitted = await send({ op: "emitZigLibrary", output: "check.zig" })
cli.stdin.end()

const harness = `
extern "c" fn fopen(path: [*:0]const u8, mode: [*:0]const u8) ?*anyopaque;
extern "c" fn fprintf(stream: ?*anyopaque, format: [*:0]const u8, ...) c_int;
extern "c" fn fread(ptr: [*]u8, size: usize, nmemb: usize, stream: ?*anyopaque) usize;
extern "c" fn fclose(stream: ?*anyopaque) c_int;
/// The process's resident set size in KiB, from /proc/self/status (Linux).
fn rssKb() !usize {
    const file = fopen("/proc/self/status", "r") orelse return error.NoStatusFile;
    defer _ = fclose(file);
    var buf: [8192]u8 = undefined;
    const n = fread(&buf, 1, buf.len, file);
    const text = buf[0..n];
    const at = std.mem.indexOf(u8, text, "VmRSS:") orelse return error.NoRssLine;
    const rest = text[at + "VmRSS:".len ..];
    var i: usize = 0;
    while (i < rest.len and (rest[i] == ' ' or rest[i] == '\\t')) i += 1;
    var value: usize = 0;
    while (i < rest.len and rest[i] >= '0' and rest[i] <= '9') : (i += 1) value = value * 10 + (rest[i] - '0');
    return value;
}
/// Wraps the value allocator and counts allocations and live bytes (raw lengths).
const Counting = struct {
    child: std.mem.Allocator,
    allocations: usize = 0,
    live: usize = 0,
    peak: usize = 0,
    fn allocator(self: *Counting) std.mem.Allocator { return .{ .ptr = self, .vtable = &vtable }; }
    fn of(ctx: *anyopaque) *Counting { return @ptrCast(@alignCast(ctx)); }
    fn grow(self: *Counting, len: usize) void { self.live += len; if (self.live > self.peak) self.peak = self.live; }
    fn shrink(self: *Counting, len: usize) void { self.live -= len; }
    const vtable: std.mem.Allocator.VTable = .{
        .alloc = struct { fn alloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret: usize) ?[*]u8 {
            const self = of(ctx);
            const p = self.child.rawAlloc(len, alignment, ret) orelse return null;
            self.allocations += 1; self.grow(len);
            return p;
        } }.alloc,
        .resize = struct { fn resize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret: usize) bool {
            const self = of(ctx);
            if (!self.child.rawResize(memory, alignment, new_len, ret)) return false;
            if (new_len > memory.len) self.grow(new_len - memory.len) else self.shrink(memory.len - new_len);
            return true;
        } }.resize,
        .remap = struct { fn remap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret: usize) ?[*]u8 {
            const self = of(ctx);
            const p = self.child.rawRemap(memory, alignment, new_len, ret) orelse return null;
            if (new_len > memory.len) self.grow(new_len - memory.len) else self.shrink(memory.len - new_len);
            return p;
        } }.remap,
        .free = struct { fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize) void {
            const self = of(ctx);
            self.shrink(memory.len);
            self.child.rawFree(memory, alignment, ret);
        } }.free,
    };
};
const Run = struct { allocations: usize, peak: usize, arena: usize, rss: usize };
const events_mod = @import("events.zig");
fn measure(code: i64, turns: i64) !Run {
    var debug: std.heap.DebugAllocator(.{}) = .init;
    var counting = Counting{ .child = debug.allocator() };
    // ny_values is replaced before every run, not only before the first export call: no value
    // outlives a run (the leak check below sees to that), so each run is counted whole.
    ny_values = counting.allocator();
    events_mod.eventsScript(code, turns);
    var out: i64 = 0;
    const status = ny_run(10, &out);
    if (status != 0) {
        var flen: usize = 0;
        const fptr = ny_last_failure(&flen);
        std.debug.print("run(code {d}, turns {d}) failed: status {d}, failure: {s}\\n", .{ code, turns, status, fptr[0..flen] });
    }
    try std.testing.expectEqual(@as(c_int, 0), status);
    try std.testing.expectEqual(if (code == 0) @as(i64, 7998) else 7998 + turns, out);
    const leaks = debug.detectLeaks();
    try std.testing.expectEqual(@as(usize, 0), leaks);
    const allocations = counting.allocations;
    const peak = counting.peak;
    const arena = ny_arena_capacity();
    // Release the checking allocator's own bookkeeping before reading RSS: over fourteen runs its
    // metadata would otherwise accumulate in the process and inflate the figure.
    try std.testing.expectEqual(std.heap.Check.ok, debug.deinit());
    return .{ .allocations = allocations, .peak = peak, .arena = arena, .rss = try rssKb() };
}
test "threaded state loop" {
    var runs: [7][2]Run = undefined;
    for (0..7) |c| {
        const code: i64 = @intCast(c);
        for ([_]i64{ 100_000, 1_000_000 }, 0..) |turns, i| runs[c][i] = try measure(code, turns);
    }
    var per_turn: [7]usize = undefined;
    for (0..7) |c| per_turn[c] = (runs[c][1].allocations - runs[c][0].allocations) / 900_000;
    // The table is written before the assertions, so a failing run still shows its numbers
    // (run.mjs prints it either way).
    const file = fopen(${JSON.stringify(join(work, "measurements.tsv"))}, "w") orelse return error.MeasurementsFile;
    _ = fprintf(file, "code\\tturns\\tallocations\\tallocations_per_turn\\tpeak_bytes\\tarena_capacity\\trss_kb\\n");
    for (0..7) |c| {
        for (0..2) |i| {
            const turns: usize = if (i == 0) 100_000 else 1_000_000;
            _ = fprintf(file, "%d\\t%d\\t%d\\t%d\\t%d\\t%d\\t%d\\n", @as(i64, @intCast(c)), turns, runs[c][i].allocations, per_turn[c], runs[c][i].peak, runs[c][i].arena, runs[c][i].rss);
        }
    }
    _ = fclose(file);
    for (0..7) |c| {
        try std.testing.expect((runs[c][1].allocations - runs[c][0].allocations) % 900_000 == 0);
        // Flat in the turn count: the same peak and recording-arena capacity at both scales.
        try std.testing.expectEqual(runs[c][0].peak, runs[c][1].peak);
        try std.testing.expectEqual(runs[c][0].arena, runs[c][1].arena);
        // RSS is a process figure, not a value figure, so it gets a slack instead of equality: the
        // first reading of a run sits about 128 kB low (pages not yet touched), and the allocator
        // keeps page-level slack. 1 MiB is far above that noise and far below what per-turn
        // retention would add over 900,000 extra turns (one retained state box per turn would be
        // gigabytes).
        try std.testing.expect(runs[c][1].rss <= runs[c][0].rss + 1024);
    }
    // The scalar, list and map turns allocate exactly as much per turn as the no-op turn: since the
    // emitter fix (c0206086 — unify repeated argument reads within a block, join blocks entered by
    // a simple jump) the state's list and map fields update in place in this shape too. Before the
    // fix they measured one allocation per turn.
    try std.testing.expectEqual(per_turn[0], per_turn[1]);
    try std.testing.expectEqual(per_turn[0], per_turn[2]);
    try std.testing.expectEqual(per_turn[0], per_turn[3]);
    // Codes 4, 5 and 6 are recorded as the known copy costs (decision record
    // docs/decisions/threaded-state.md): a nested record's box, a tree's root-to-leaf path, a new
    // union variant's box.
}
`
writeFileSync(join(work, "harness.zig"), readFileSync(emitted.path, "utf8") + harness)
const zig = process.env.NYRWVY_ZIG || "zig"
const run = spawnSync(zig, ["test", "-lc", "-O", "ReleaseSafe", join(work, "harness.zig")], { cwd: repo, stdio: "inherit" })
const status = run.status ?? 1
// The measurements are shown on failure too: they are the evidence the assertions judged.
try { console.log(readFileSync(join(work, "measurements.tsv"), "utf8")) } catch { /* the harness failed before writing them */ }
process.exit(status)
