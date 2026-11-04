//! The standalone Roslyn driver, a bounded per-session PE/PDB cache and its per-user disk copy.
const std = @import("std");
const p = @import("run_process.zig");
const windows = @import("builtin").os.tag == .windows;
const A = std.mem.Allocator;
pub const Source = struct { name: []const u8, text: []const u8 };
pub const Built = union(enum) { assembly: []const u8, failed: p.Diagnostic };
const Entry = struct { key: [32]u8, pe: []u8, pdb: []u8, last: u64 };
var entries: std.ArrayList(Entry) = .empty;
var bytes: usize = 0;
var serial: u64 = 0;
var lifetime: A = undefined;
var source_root: []const u8 = "";
var session_root: []const u8 = "";
var temp_root: []const u8 = "";
var driver: ?[]const u8 = null;
var identity: []const u8 = "";
var major: i64 = 0;
pub var dotnet: []const u8 = "dotnet";
pub var runtime_version: []const u8 = "";
pub var build_context_fingerprint: []const u8 = "";

pub fn init(a: A, io: std.Io, env: *const std.process.Environ.Map) !void {
    lifetime = a;
    const configured_temp = if (windows) nonEmpty(env.get("TEMP")) orelse nonEmpty(env.get("TMP")) orelse return error.TempDirectoryUnavailable else nonEmpty(env.get("TMPDIR")) orelse "/tmp";
    temp_root = try resolveTempRoot(a, try std.process.currentPathAlloc(io, a), configured_temp);
    var paths = std.mem.splitScalar(u8, env.get("PATH") orelse "", std.fs.path.delimiter);
    while (paths.next()) |part| {
        const candidate = try std.fs.path.resolveAlloc(a, &.{ part, if (windows) "dotnet.exe" else "dotnet" });
        if (std.Io.Dir.cwd().access(io, candidate, .{ .execute = true })) |_| {
            dotnet = candidate;
            break;
        } else |_| {}
    }
    if (!windows) sweep(io, temp_root);
    session_root = try temporary(a, io, try std.fmt.allocPrint(a, "{s}{d}", .{ session_prefix, currentPid() }));
    source_root = try std.fs.path.join(a, &.{ session_root, "compiler-source" });
    _ = try std.Io.Dir.cwd().createDirPathStatus(io, source_root, if (windows) .default_dir else .fromMode(0o700));
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = try std.fs.path.join(a, &.{ source_root, "Compiler.csproj" }), .data = driver_project });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = try std.fs.path.join(a, &.{ source_root, "Compiler.cs" }), .data = driver_source });
}
fn resolveTempRoot(a: A, cwd: []const u8, configured: []const u8) ![]const u8 {
    return std.fs.path.resolveAlloc(a, &.{ cwd, configured });
}
fn nonEmpty(value: ?[]const u8) ?[]const u8 {
    const text = value orelse return null;
    return if (text.len == 0) null else text;
}
const driver_project = @embedFile("csharp/Compiler.csproj");
const driver_source = @embedFile("csharp/Compiler.cs");
const session_prefix = "nyrwvy-csharp-session-";
fn currentPid() i64 {
    return if (windows) std.os.windows.GetCurrentProcessId() else if (@import("builtin").os.tag == .linux) std.os.linux.getpid() else std.c.getpid();
}
/// A fresh private directory `<prefix>-<random>` in the session directory (the temp root before it exists),
/// so the startup sweep of a dead host's session also removes its workers' and runs' directories.
pub fn temporary(a: A, io: std.Io, prefix: []const u8) ![]const u8 {
    for (0..16) |_| {
        var random: [16]u8 = undefined;
        try io.randomSecure(&random);
        const name = try std.fmt.allocPrint(a, "{s}-{x}", .{ prefix, random });
        const path = try std.fs.path.join(a, &.{ if (session_root.len > 0) session_root else temp_root, name });
        std.Io.Dir.cwd().createDir(io, path, if (windows) .default_dir else .fromMode(0o700)) catch |err| switch (err) {
            error.PathAlreadyExists => continue,
            else => return err,
        };
        return path;
    }
    return error.TempDirectoryUnavailable;
}
/// Deletes session directories (`nyrwvy-csharp-session-<pid>-…`) whose host no longer runs.
fn sweep(io: std.Io, parent: []const u8) void {
    var dir = std.Io.Dir.cwd().openDir(io, parent, .{ .iterate = true }) catch return;
    defer dir.close(io);
    var entries_of = dir.iterate();
    while (entries_of.next(io) catch return) |entry| {
        if (entry.kind != .directory or !std.mem.startsWith(u8, entry.name, session_prefix)) continue;
        const rest = entry.name[session_prefix.len..];
        const pid = std.fmt.parseInt(std.posix.pid_t, rest[0 .. std.mem.indexOfScalar(u8, rest, '-') orelse continue], 10) catch continue;
        // EPERM is a live process of another user.
        if (pid > 0) std.posix.kill(pid, @fromBackingInt(@intCast(0))) catch |e| if (e == error.ProcessNotFound) dir.deleteTree(io, entry.name) catch {};
    }
}
fn evict(index: usize) void {
    const e = entries.swapRemove(index);
    bytes -= e.pe.len + e.pdb.len;
    std.heap.page_allocator.free(e.pe);
    std.heap.page_allocator.free(e.pdb);
}
pub fn deinit(io: std.Io) void {
    while (entries.items.len > 0) evict(entries.items.len - 1);
    entries.deinit(std.heap.page_allocator);
    if (session_root.len > 0) std.Io.Dir.cwd().deleteTree(io, session_root) catch {};
}
pub fn ensureDriver(a: A, io: std.Io) !?p.Diagnostic {
    if (driver != null) return null;
    // Built once per user under a key of its sources and the dotnet that builds it, then shared.
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(try std.mem.join(a, "\x00", &.{ dotnet, driver_project, driver_source }), &digest, .{});
    const drivers = try std.fs.path.join(a, &.{ p.cache_root, "csharp-driver" });
    const output = try std.fs.path.join(a, &.{ drivers, &std.fmt.bytesToHex(digest, .lower) });
    if (std.Io.Dir.cwd().access(io, try std.fs.path.join(a, &.{ output, "Compiler.dll" }), .{})) |_| {} else |_| {
        _ = try std.Io.Dir.cwd().createDirPathStatus(io, drivers, if (windows) .default_dir else .fromMode(0o700));
        var random: [16]u8 = undefined;
        io.random(&random);
        // Built under a fresh name and renamed, so no session runs a partial driver.
        const partial = try std.fmt.allocPrint(a, "{s}.{x}", .{ output, random });
        // No lingering MSBuild node or compiler server may inherit the output pipes, or reading them never ends.
        defer std.Io.Dir.cwd().deleteTree(io, partial) catch {};
        const project = try std.fs.path.join(a, &.{ source_root, "Compiler.csproj" });
        const result = try p.launch(a, io, &.{ dotnet, "build", project, "--configuration", "Release", "--output", partial, "--nologo", "--verbosity", "quiet", "-nodeReuse:false", "-p:UseSharedCompilation=false" }, source_root, "C# compiler driver build", 300);
        switch (result) {
            .failed => |d| return d,
            .completed => |done| if (!done.term.success()) return try p.processFailure(a, done),
        }
        // A concurrent session may have renamed its own build into place first.
        std.Io.Dir.renameAbsolute(partial, output, io) catch |e| {
            std.Io.Dir.cwd().access(io, try std.fs.path.join(a, &.{ output, "Compiler.dll" }), .{}) catch return e;
        };
    }
    const assembly = try std.fs.path.join(a, &.{ output, "Compiler.dll" });
    // A driver that ran but did not identify itself (say, its runtime is gone) is moved aside and rebuilt
    // next time; a timeout or launch failure may be transient, and other sessions share this directory.
    var stale = false;
    defer if (stale) {
        var random: [16]u8 = undefined;
        io.random(&random);
        if (std.fmt.allocPrint(a, "{s}.stale.{x}", .{ output, random })) |aside| {
            if (std.Io.Dir.renameAbsolute(output, aside, io)) |_| std.Io.Dir.cwd().deleteTree(io, aside) catch {} else |_| {}
        } else |_| {}
    };
    errdefer stale = false;
    const identified = try p.launch(a, io, &.{ dotnet, assembly, "identity" }, output, "C# compiler identification", 30);
    stale = true;
    const text = switch (identified) {
        .failed => |d| {
            stale = false;
            return d;
        },
        .completed => |done| if (done.term.success()) done.stdout else return try p.processFailure(a, done),
    };
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, a, text, .{}) catch return .{ .code = "RunStartupFailure", .message = "The C# compiler did not return its build identity." };
    const invalid: p.Diagnostic = .{ .code = "RunStartupFailure", .message = "The C# compiler did not return its build identity." };
    if (parsed != .object) return invalid;
    const id = parsed.object.get("identity") orelse return invalid;
    const version = parsed.object.get("runtimeVersion") orelse return invalid;
    const runtime_major = parsed.object.get("major") orelse return invalid;
    const context = parsed.object.get("buildContextFingerprint") orelse return invalid;
    if (id != .string or version != .string or context != .string or runtime_major != .integer or runtime_major.integer < 1) return invalid;
    // Only a successful identification outlives the request.
    identity = try lifetime.dupe(u8, id.string);
    major = runtime_major.integer;
    runtime_version = try lifetime.dupe(u8, version.string);
    build_context_fingerprint = try lifetime.dupe(u8, context.string);
    driver = try lifetime.dupe(u8, assembly);
    stale = false;
    return null;
}
fn install(a: A, io: std.Io, directory: []const u8, pe: []const u8, pdb: []const u8) ![]const u8 {
    const path = try std.fs.path.join(a, &.{ directory, "Generated.dll" });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = pe });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = try std.fs.path.join(a, &.{ directory, "Generated.pdb" }), .data = pdb });
    const config = try std.fmt.allocPrint(a, "{{\"runtimeOptions\":{{\"tfm\":\"net{d}.0\",\"framework\":{{\"name\":\"Microsoft.NETCore.App\",\"version\":\"{d}.0.0\"}}}}}}", .{ major, major });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = try std.fs.path.join(a, &.{ directory, "Generated.runtimeconfig.json" }), .data = config });
    return path;
}
/// The identity hashes the driver, Roslyn, every reference assembly, the runtime and the options;
/// the sources carry the generated program and the embedded runner support, so they decide the output.
fn keyOf(sources: []const Source) [32]u8 {
    var hash = std.crypto.hash.sha2.Sha256.init(.{});
    hash.update(identity);
    for (sources) |source| {
        for ([_][]const u8{ source.name, source.text }) |part| {
            var length: [8]u8 = undefined;
            std.mem.writeInt(u64, &length, part.len, .little);
            hash.update(&length);
            hash.update(part);
        }
    }
    var key: [32]u8 = undefined;
    hash.final(&key);
    return key;
}
fn remember(key: [32]u8, pe: []const u8, pdb: []const u8) !void {
    const size = pe.len + pdb.len;
    if (size > 64 * 1048576) return;
    while (entries.items.len >= 32 or bytes + size > 64 * 1048576) {
        var oldest: usize = 0;
        for (entries.items, 0..) |entry, i| if (entry.last < entries.items[oldest].last) {
            oldest = i;
        };
        evict(oldest);
    }
    const owned_pe = try std.heap.page_allocator.dupe(u8, pe);
    errdefer std.heap.page_allocator.free(owned_pe);
    const owned_pdb = try std.heap.page_allocator.dupe(u8, pdb);
    errdefer std.heap.page_allocator.free(owned_pdb);
    try entries.append(std.heap.page_allocator, .{ .key = key, .pe = owned_pe, .pdb = owned_pdb, .last = serial });
    bytes += size;
}
/// Assemblies persist per user across host sessions, like the Zig runners, and are never pruned.
fn storedPath(a: A, key: [32]u8) ![]const u8 {
    return std.fs.path.join(a, &.{ p.cache_root, "csharp-runners", &std.fmt.bytesToHex(key, .lower) });
}
fn load(a: A, io: std.Io, key: [32]u8) !?struct { pe: []u8, pdb: []u8 } {
    const entry = try storedPath(a, key);
    const pe = std.Io.Dir.cwd().readFileAlloc(io, try std.fs.path.join(a, &.{ entry, "Generated.dll" }), a, .unlimited) catch |err| {
        dropMissing(io, entry, err);
        return null;
    };
    const pdb = std.Io.Dir.cwd().readFileAlloc(io, try std.fs.path.join(a, &.{ entry, "Generated.pdb" }), a, .unlimited) catch |err| {
        dropMissing(io, entry, err);
        return null;
    };
    return .{ .pe = pe, .pdb = pdb };
}
/// A half-present entry (a crash before its files reached disk) is a miss; drop it so it is rebuilt.
/// Other read errors may be transient, so the entry is left for the next run.
fn dropMissing(io: std.Io, entry: []const u8, err: anyerror) void {
    if (err == error.FileNotFound) std.Io.Dir.cwd().deleteTree(io, entry) catch {};
}
fn store(a: A, io: std.Io, key: [32]u8, pe: []const u8, pdb: []const u8) !void {
    const entry = try storedPath(a, key);
    _ = try std.Io.Dir.cwd().createDirPathStatus(io, std.fs.path.dirname(entry).?, if (windows) .default_dir else .fromMode(0o700));
    var random: [16]u8 = undefined;
    io.random(&random);
    // Written under a fresh name and renamed, so a concurrent host never reads a partial entry.
    const partial = try std.fmt.allocPrint(a, "{s}.{x}", .{ entry, random });
    defer std.Io.Dir.cwd().deleteTree(io, partial) catch {};
    try std.Io.Dir.cwd().createDir(io, partial, if (windows) .default_dir else .fromMode(0o700));
    // Synced before the rename, so a crash cannot leave a renamed entry with truncated files.
    for ([_]struct { []const u8, []const u8 }{ .{ "Generated.dll", pe }, .{ "Generated.pdb", pdb } }) |item| {
        const file = try std.Io.Dir.cwd().createFile(io, try std.fs.path.join(a, &.{ partial, item[0] }), .{});
        defer file.close(io);
        try file.writeStreamingAll(io, item[1]);
        try file.sync(io);
    }
    // A concurrent host may have renamed the same entry into place first; either copy is identical.
    try std.Io.Dir.renameAbsolute(partial, entry, io);
}
pub fn build(a: A, io: std.Io, directory: []const u8, sources: []const Source) !Built {
    return compile(a, io, directory, sources, true);
}
pub fn uncached(a: A, io: std.Io, directory: []const u8, sources: []const Source) !Built {
    return compile(a, io, directory, sources, false);
}
fn compile(a: A, io: std.Io, directory: []const u8, sources: []const Source, cached: bool) !Built {
    if (try ensureDriver(a, io)) |d| return .{ .failed = d };
    serial += 1;
    const key = keyOf(sources);
    for (entries.items) |*entry| if (cached and std.mem.eql(u8, &key, &entry.key)) {
        entry.last = serial;
        return .{ .assembly = try install(a, io, directory, entry.pe, entry.pdb) };
    };
    if (cached) if (try load(a, io, key)) |stored| {
        try remember(key, stored.pe, stored.pdb);
        return .{ .assembly = try install(a, io, directory, stored.pe, stored.pdb) };
    };
    var manifest: std.Io.Writer.Allocating = .init(a);
    try manifest.writer.writeByte('[');
    for (sources, 0..) |source, i| {
        if (i > 0) try manifest.writer.writeByte(',');
        try manifest.writer.writeAll("{\"name\":");
        try @import("root").writeString(&manifest.writer, source.name);
        try manifest.writer.writeAll(",\"text\":");
        try @import("root").writeString(&manifest.writer, source.text);
        try manifest.writer.writeByte('}');
    }
    try manifest.writer.writeByte(']');
    const path = try std.fs.path.join(a, &.{ directory, "sources.json" });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = manifest.written() });
    switch (try p.launch(a, io, &.{ dotnet, driver.?, "compile", path, directory }, directory, "build", 30)) {
        .failed => |d| return .{ .failed = d },
        .completed => |done| if (!done.term.success()) {
            if (done.term == .exited and done.term.exited == 2 and done.stdout.len == 0 and done.stderr.len == 0) return .{ .failed = .{ .code = "RunTimeout", .message = "The build exceeded its time limit of 30 s." } };
            if (done.term == .exited and done.term.exited == 1) {
                const text = @import("managed_text.zig");
                const diagnostics = try std.mem.concat(a, u8, &.{ "\n", try text.decode(a, done.stdout) });
                // The driver is bounded, and operator-provided dotnet wrappers need the same boundary.
                return .{ .failed = .{ .code = "RunProcessFailure", .message = try text.prefix(a, diagnostics, 4096) } };
            }
            return .{ .failed = try p.processFailure(a, done) };
        },
    }
    const pe_path = try std.fs.path.join(a, &.{ directory, "Generated.dll" });
    const pdb_path = try std.fs.path.join(a, &.{ directory, "Generated.pdb" });
    const pe = try std.Io.Dir.cwd().readFileAlloc(io, pe_path, a, .unlimited);
    const pdb = try std.Io.Dir.cwd().readFileAlloc(io, pdb_path, a, .unlimited);
    if (cached) {
        try remember(key, pe, pdb);
        store(a, io, key, pe, pdb) catch {};
    }
    return .{ .assembly = pe_path };
}

test "temporary directories use the configured root and are distinct" {
    var dir = std.testing.tmpDir(.{});
    defer dir.cleanup();
    const a = std.testing.allocator;
    const path = try dir.dir.realPathFileAlloc(std.testing.io, ".", a);
    defer a.free(path);
    const cwd = try std.process.currentPathAlloc(std.testing.io, a);
    defer a.free(cwd);
    const relative = try std.fs.path.relativeAlloc(a, cwd, null, cwd, path);
    defer a.free(relative);
    const resolved = try resolveTempRoot(a, cwd, relative);
    defer a.free(resolved);
    try std.testing.expect(std.fs.path.isAbsolute(resolved));
    try std.testing.expectEqualStrings(path, resolved);
    const previous = temp_root;
    temp_root = resolved;
    defer temp_root = previous;
    var arena: std.heap.ArenaAllocator = .init(a);
    defer arena.deinit();
    const first = try temporary(arena.allocator(), std.testing.io, "nyrwvy-test");
    const second = try temporary(arena.allocator(), std.testing.io, "nyrwvy-test");
    try std.testing.expect(!std.mem.eql(u8, first, second));
    try std.testing.expectEqualStrings(path, std.fs.path.dirname(first).?);
    try std.Io.Dir.cwd().access(std.testing.io, first, .{});
    try std.Io.Dir.cwd().access(std.testing.io, second, .{});
}

test "sweep removes session directories whose host is gone" {
    // The host sweeps only on POSIX (no signal-0 liveness probe on Windows).
    if (windows) return error.SkipZigTest;
    const io = std.testing.io;
    var parent = std.testing.tmpDir(.{});
    defer parent.cleanup();
    const path = try parent.dir.realPathFileAlloc(io, ".", std.testing.allocator);
    defer std.testing.allocator.free(path);
    var buffer: [64]u8 = undefined;
    const live = try std.fmt.bufPrint(&buffer, session_prefix ++ "{d}-a", .{currentPid()});
    const gone = session_prefix ++ "2147483646-b";
    for ([_][]const u8{ live, gone, "nyrwvy-other-1-c" }) |name| try parent.dir.createDir(io, name, .default_dir);
    sweep(io, path);
    try parent.dir.access(io, live, .{});
    try parent.dir.access(io, "nyrwvy-other-1-c", .{});
    try std.testing.expectError(error.FileNotFound, parent.dir.access(io, gone, .{}));
}

test "stored assemblies outlive the session and a changed source misses them" {
    const io = std.testing.io;
    var dir = std.testing.tmpDir(.{});
    defer dir.cleanup();
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const previous = p.cache_root;
    p.cache_root = try dir.dir.realPathFileAlloc(io, ".", a);
    defer p.cache_root = previous;
    const first = keyOf(&.{.{ .name = "Program.cs", .text = "class A {}" }});
    const changed = keyOf(&.{.{ .name = "Program.cs", .text = "class B {}" }});
    try std.testing.expect(!std.mem.eql(u8, &first, &changed));
    try std.testing.expect(try load(a, io, first) == null);
    try store(a, io, first, "pe", "pdb");
    // A second host storing the same entry loses the rename and keeps the first copy.
    store(a, io, first, "pe", "pdb") catch {};
    const hit = (try load(a, io, first)).?;
    try std.testing.expectEqualStrings("pe", hit.pe);
    try std.testing.expectEqualStrings("pdb", hit.pdb);
    try std.testing.expect(try load(a, io, changed) == null);
    var stored = try dir.dir.openDir(io, "csharp-runners", .{ .iterate = true });
    defer stored.close(io);
    var names = stored.iterate();
    var count: usize = 0;
    while (try names.next(io)) |_| count += 1;
    try std.testing.expectEqual(@as(usize, 1), count);
}
