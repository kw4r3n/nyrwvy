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

pub const Input = struct { value: std.json.Value, source: []const u8 };

pub fn jsonInput(d: *recipe.Decoder, inline_name: []const u8, file_name: []const u8) anyerror!Input {
    const r = d.r;
    const a = r.a;
    const inline_value = r.root.getPtr(inline_name);
    const file = r.root.get(file_name);
    if (inline_value != null and file == null) return .{ .value = inline_value.?.*, .source = try recipe.rawProperty(a, r.line, inline_name) };
    if (inline_value != null or file == null) return r.fail("Pass exactly one of {s} and {s}.", .{ inline_name, file_name });
    if (file.? != .string) return r.fail("{s} must be a file path.", .{file_name});
    const requested = try query.readable(r, file.?.string);
    if (query.isBlank(try std.unicode.utf8ToUtf16LeAlloc(a, requested))) return r.fail("{s} must be a file path.", .{file_name});
    return load(d, requested, file_name, inline_name);
}

/// Shared confined JSON reader; an empty inline name keeps decoder paths at the file root.
pub fn load(d: *recipe.Decoder, requested: []const u8, file_name: []const u8, inline_name: []const u8) anyerror!Input {
    const r = d.r;
    const a = r.a;
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
    const wrapped = if (inline_name.len == 0) json else try std.fmt.allocPrint(a, "{{\"{s}\":{s}}}", .{ inline_name, json });
    d.duplicates = try recipe.duplicatePaths(a, wrapped);
    query.masked = query.masked or masked.line != null;
    return .{ .value = parsed, .source = json };
}

pub fn input(d: *recipe.Decoder, inline_name: []const u8, file_name: []const u8) anyerror!*const recipe.Node {
    const path = try std.fmt.allocPrint(d.r.a, "/{s}", .{inline_name});
    var value = try jsonInput(d, inline_name, file_name);
    try recipe.resolveNames(d, path, &value.value, value.source);
    return d.recipe(path, value.value);
}

/// Preserve the original item spelling for duplicate-property and surrogate checks.
pub fn rawArrayItems(a: std.mem.Allocator, source: []const u8) ![]const []const u8 {
    var scanner = std.json.Scanner.initCompleteInput(a, source);
    defer scanner.deinit();
    var depth: usize = 0;
    var start: usize = 0;
    var items: std.ArrayList([]const u8) = .empty;
    while (true) {
        const before = scanner.cursor;
        const token = try scanner.nextAlloc(a, .alloc_always);
        if (token == .end_of_document) return items.items;
        if (depth == 1 and token != .array_end) start = before;
        switch (token) {
            .object_begin, .array_begin => depth += 1,
            .object_end, .array_end => depth -= 1,
            else => {},
        }
        if (depth == 1 and token != .array_begin) try items.append(a, std.mem.trim(u8, source[start..scanner.cursor], " \t\r\n,"));
    }
}
