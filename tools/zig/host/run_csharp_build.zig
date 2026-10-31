//! The standalone Roslyn driver and bounded per-session PE/PDB cache.
const std = @import("std");
const p = @import("run_process.zig");
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
var driver: ?[]const u8 = null;
var identity: []const u8 = "";
var major: i64 = 0;
pub var dotnet: []const u8 = "dotnet";
pub var runtime_version: []const u8 = "";
pub var build_context_fingerprint: []const u8 = "";

pub fn init(a: A, io: std.Io, env: *const std.process.Environ.Map) !void {
    lifetime = a;
    var paths = std.mem.splitScalar(u8, env.get("PATH") orelse "/usr/bin:/bin", ':');
    while (paths.next()) |part| {
        const candidate = try std.fs.path.resolveAlloc(a, &.{ part, "dotnet" });
        if (std.Io.Dir.cwd().access(io, candidate, .{ .execute = true })) |_| {
            dotnet = candidate;
            break;
        } else |_| {}
    }
    session_root = try temporary(a, io, "nyrwvy-csharp-session");
    source_root = try std.fs.path.join(a, &.{ session_root, "compiler-source" });
    _ = try std.Io.Dir.cwd().createDirPathStatus(io, source_root, .fromMode(0o700));
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = try std.fs.path.join(a, &.{ source_root, "Compiler.csproj" }), .data = @embedFile("csharp/Compiler.csproj") });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = try std.fs.path.join(a, &.{ source_root, "Compiler.cs" }), .data = @embedFile("csharp/Compiler.cs") });
}
pub fn temporary(a: A, io: std.Io, prefix: []const u8) ![]const u8 {
    var random: [16]u8 = undefined;
    io.random(&random);
    const path = try std.fmt.allocPrint(a, "/tmp/{s}-{x}", .{ prefix, random });
    _ = try std.Io.Dir.cwd().createDirPathStatus(io, path, .fromMode(0o700));
    return path;
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
    const project = try std.fs.path.join(a, &.{ source_root, "Compiler.csproj" });
    const output = try std.fs.path.join(lifetime, &.{ session_root, "compiler" });
    const result = try p.launch(a, io, &.{ dotnet, "build", project, "--configuration", "Release", "--output", output, "--nologo", "--verbosity", "quiet" }, source_root, "C# compiler driver build", 30);
    switch (result) {
        .failed => |d| return d,
        .completed => |done| if (!done.term.success()) return try p.processFailure(a, done),
    }
    const assembly = try std.fs.path.join(lifetime, &.{ output, "Compiler.dll" });
    const identified = try p.launch(a, io, &.{ dotnet, assembly, "identity" }, output, "C# compiler identification", 30);
    const text = switch (identified) {
        .failed => |d| return d,
        .completed => |done| if (done.term.success()) done.stdout else return try p.processFailure(a, done),
    };
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, lifetime, text, .{}) catch return .{ .code = "RunStartupFailure", .message = "The C# compiler did not return its build identity." };
    const invalid: p.Diagnostic = .{ .code = "RunStartupFailure", .message = "The C# compiler did not return its build identity." };
    if (parsed != .object) return invalid;
    const id = parsed.object.get("identity") orelse return invalid;
    const version = parsed.object.get("runtimeVersion") orelse return invalid;
    const runtime_major = parsed.object.get("major") orelse return invalid;
    const context = parsed.object.get("buildContextFingerprint") orelse return invalid;
    if (id != .string or version != .string or context != .string or runtime_major != .integer or runtime_major.integer < 1) return invalid;
    identity = id.string;
    major = runtime_major.integer;
    runtime_version = version.string;
    build_context_fingerprint = context.string;
    driver = assembly;
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
pub fn build(a: A, io: std.Io, directory: []const u8, sources: []const Source) !Built {
    return compile(a, io, directory, sources, true);
}
pub fn uncached(a: A, io: std.Io, directory: []const u8, sources: []const Source) !Built {
    return compile(a, io, directory, sources, false);
}
fn compile(a: A, io: std.Io, directory: []const u8, sources: []const Source, cached: bool) !Built {
    if (try ensureDriver(a, io)) |d| return .{ .failed = d };
    serial += 1;
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
    for (entries.items) |*entry| if (cached and std.mem.eql(u8, &key, &entry.key)) {
        entry.last = serial;
        return .{ .assembly = try install(a, io, directory, entry.pe, entry.pdb) };
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
    const size = pe.len + pdb.len;
    if (cached and size <= 64 * 1048576) {
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
    return .{ .assembly = pe_path };
}
