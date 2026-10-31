//! Standalone oracle probe: csharp-emitter IR.json [profile.json] prints the artifact or backend failure.
const std = @import("std");
const backend = @import("root.zig");
pub fn main(init: std.process.Init) !void {
    const a = init.arena.allocator();
    const args = try init.minimal.args.toSlice(a);
    if (args.len < 2 or args.len > 3) return error.InvalidArguments;
    const ir = try std.Io.Dir.cwd().readFileAlloc(init.io, args[1], a, .unlimited);
    const profile_json = if (args.len == 3) try std.Io.Dir.cwd().readFileAlloc(init.io, args[2], a, .unlimited) else "{}";
    const profile = try std.json.parseFromSlice(backend.Value, a, profile_json, .{ .allocate = .alloc_always });
    const result = try backend.emit(a, ir, profile.value);
    const value = switch (result) {
        .artifact => |artifact| try backend.object(a, &.{ "source", "symbols" }, &.{ backend.string(artifact.source), artifact.symbols }),
        .failure => |failure| try backend.object(a, &.{ "code", "message", "details" }, &.{ backend.string(failure.code), backend.string(failure.message), failure.details }),
    };
    const text = try std.json.Stringify.valueAlloc(a, value, .{ .emit_null_optional_fields = true });
    var buffer: [4096]u8 = undefined;
    var out = std.Io.File.stdout().writer(init.io, &buffer);
    try out.interface.writeAll(text);
    try out.interface.writeByte('\n');
    try out.interface.flush();
}
