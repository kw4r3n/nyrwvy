//! Protocol.recipeInput / confinedJsonFile. File parsing deliberately does not observe IDs:
//! F# uses JsonDocument.Parse(stream), not SessionStorage.readText, at this read site.
const std = @import("std");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const root = @import("root");
const limit = 1048576;

fn ioFailure(r: *query.Request, name: []const u8) anyerror {
    return r.fail("{s} cannot be read as a JSON file inside the snapshot's directory.", .{name});
}

pub fn input(d: *recipe.Decoder, inline_name: []const u8, file_name: []const u8) anyerror!*const recipe.Node {
    const r = d.r;
    const a = r.a;
    const path = try std.fmt.allocPrint(a, "/{s}", .{inline_name});
    const inline_value = r.root.getPtr(inline_name);
    const file = r.root.get(file_name);
    if (inline_value != null and file == null) {
        try recipe.resolveNames(d, path, inline_value.?, try recipe.rawProperty(a, r.line, inline_name));
        return d.recipe(path, inline_value.?.*);
    }
    if (inline_value != null or file == null) return r.fail("Pass exactly one of {s} and {s}.", .{ inline_name, file_name });
    if (file.? != .string) return r.fail("{s} must be a file path.", .{file_name});
    const requested = try query.readable(r, file.?.string);
    if (query.isBlank(try std.unicode.utf8ToUtf16LeAlloc(a, requested))) return r.fail("{s} must be a file path.", .{file_name});
    if (std.mem.indexOfScalar(u8, requested, 0) != null) return ioFailure(r, file_name);
    const directory = std.fs.path.dirname(r.snapshot_path) orelse "/";
    const full = std.fs.path.resolveAlloc(a, &.{ directory, requested }) catch return ioFailure(r, file_name);
    const relative = std.fs.path.relativeAlloc(a, "/", null, directory, full) catch return ioFailure(r, file_name);
    if (std.fs.path.isAbsolute(relative) or std.mem.eql(u8, relative, "..") or std.mem.startsWith(u8, relative, "../") or std.mem.eql(u8, full, directory)) return r.fail("{s} must stay inside the snapshot's directory.", .{file_name});
    if (try @import("view_file.zig").linkedComponent(a, directory, full) != null) return r.fail("{s} must not pass through a symbolic link or reparse point.", .{file_name});
    const info = std.Io.Dir.cwd().statFile(root.host_io, full, .{}) catch return r.fail("{s} must name an existing JSON file.", .{file_name});
    if (info.kind != .file) return r.fail("{s} must name an existing JSON file.", .{file_name});
    if (info.size > limit) return r.fail("{s} exceeds 1 MiB.", .{file_name});
    // Read at most one byte over the limit, including a file that grows after the stat.
    const bytes = std.Io.Dir.cwd().readFileAlloc(root.host_io, full, a, .limited(limit + 1)) catch |err| {
        if (std.mem.eql(u8, @errorName(err), "StreamTooLong") or std.mem.eql(u8, @errorName(err), "FileTooBig")) return r.fail("{s} exceeds 1 MiB.", .{file_name});
        return ioFailure(r, file_name);
    };
    if (bytes.len > limit) return r.fail("{s} exceeds 1 MiB.", .{file_name});
    const source = if (std.mem.startsWith(u8, bytes, "\xef\xbb\xbf")) bytes[3..] else bytes;
    const masked = try root.maskLoneSurrogates(a, source);
    const json = masked.line orelse source;
    if (root.tooDeep(json)) return r.fail("{s} must contain valid JSON.", .{file_name});
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, a, json, .{ .duplicate_field_behavior = .use_last }) catch return r.fail("{s} must contain valid JSON.", .{file_name});
    if (bytes.len > limit) return r.fail("{s} exceeds 1 MiB.", .{file_name});
    // Decoder paths and duplicate scans are relative to the same recipe root as inline inputs.
    const wrapped = try std.fmt.allocPrint(a, "{{\"{s}\":{s}}}", .{ inline_name, json });
    d.duplicates = try recipe.duplicatePaths(a, wrapped);
    query.masked = query.masked or masked.line != null;
    var value = parsed;
    try recipe.resolveNames(d, path, &value, json);
    return d.recipe(path, value);
}
