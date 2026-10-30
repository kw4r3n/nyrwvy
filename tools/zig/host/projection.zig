//! WireProposalProjection.compact; budgets count the result, not the protocol envelope.
const std = @import("std");
const model = @import("model_reader");
const m = @import("materialize.zig");
const Value = model.Value;
const Allocator = std.mem.Allocator;
const max_bytes = 16384;

fn bytes(a: Allocator, value: Value) ![]const u8 {
    var out: std.Io.Writer.Allocating = .init(a);
    try m.write(&out.writer, value);
    return out.written();
}

// FunctionCreation.Batch is a boxed F# list: JSON writes an array, but projection treats it as a scalar.
fn boxedBatch(path: []const u8, value: Value) bool {
    return value == .array and std.mem.endsWith(u8, path, "/batch");
}

const Project = struct {
    a: Allocator,
    depth: usize,
    receipt: bool,
    arrays: usize,
    text: usize,
    root_path: []const u8 = "",
    root_offset: ?usize = null,
    omissions: std.ArrayList(Value) = .empty,

    fn omit(self: *Project, path: []const u8, kind: []const u8, total: usize, shown: usize) !void {
        const a = self.a;
        try self.omissions.append(a, try m.object(a, &.{
            try m.member(a, "path", try m.string(a, path)), try m.member(a, "kind", try m.string(a, kind)),
            try m.member(a, "totalCount", try m.number(a, total)), try m.member(a, "shownCount", try m.number(a, shown)),
        }));
    }

    fn identity(name: []const u8) bool {
        return m.same(name, "id") or std.mem.endsWith(u8, name, "Id") or std.mem.endsWith(u8, name, "Ids") or std.mem.endsWith(u8, name, "Fingerprint");
    }

    fn identityPath(path: []const u8) bool {
        var parts = std.mem.splitBackwardsScalar(u8, path, '/');
        return identity(parts.next().?) or (if (parts.next()) |p| identity(p) else false);
    }

    fn visit(self: *Project, depth: usize, path: []const u8, value: Value) anyerror!Value {
        const a = self.a;
        if (boxedBatch(path, value)) return value;
        if (m.same(path, "/proposalId") or m.same(path, "/detail") or (std.mem.endsWith(u8, path, "/kind") and value == .string and value.string.len <= 64)) return value;
        if (value == .string and !m.same(path, self.root_path) and identityPath(path)) {
            if (value.string.len <= 256) return value;
            try self.omit(path, "stringUtf16", value.string.len, 0);
            return .json_null;
        }
        if (m.same(path, "/review/receipt") and self.root_path.len == 0) {
            if (self.receipt) return value;
            try self.omit(path, "object", value.object.len, 0);
            return .json_null;
        }
        if (depth >= self.depth and value == .object) {
            try self.omit(path, "object", value.object.len, 0);
            return .json_null;
        }
        switch (value) {
            .array => |items| {
                const shown = if (m.same(path, self.root_path) and self.root_offset != null) items.len else @min(items.len, self.arrays);
                if (shown < items.len) try self.omit(path, "array", items.len, shown);
                const out = try a.alloc(Value, shown);
                for (items[0..shown], out, 0..) |v, *o, i| o.* = try self.visit(depth + 1, try std.fmt.allocPrint(a, "{s}/{d}", .{ path, i + (if (m.same(path, self.root_path)) self.root_offset orelse 0 else 0) }), v);
                return .{ .array = out };
            },
            .object => |fields| {
                const out = try a.alloc(model.Member, fields.len);
                for (fields, out) |f, *o| {
                    const name = model.utf8(f.name);
                    const escaped = try std.mem.replaceOwned(u8, a, try std.mem.replaceOwned(u8, a, name, "~", "~0"), "/", "~1");
                    o.* = .{ .name = f.name, .value = try self.visit(depth + 1, try std.fmt.allocPrint(a, "{s}/{s}", .{ path, escaped }), f.value) };
                }
                return .{ .object = out };
            },
            .string => |s| {
                var shown = @min(s.len, self.text);
                if (shown > 0 and shown < s.len and s[shown - 1] >= 0xD800 and s[shown - 1] <= 0xDBFF and s[shown] >= 0xDC00 and s[shown] <= 0xDFFF) shown -= 1;
                if (shown < s.len) try self.omit(path, "stringUtf16", s.len, shown);
                return .{ .string = s[0..shown] };
            },
            else => return value,
        }
    }
};

fn metadata(a: Allocator, budget: usize, original: usize, omissions: []const Value) !Value {
    const complete: Value = if (omissions.len == 0) .json_true else .json_false;
    return m.object(a, &.{
        try m.member(a, "complete", complete), try m.member(a, "requiresDetailedReview", if (omissions.len == 0) .json_false else .json_true),
        try m.member(a, "maxBytes", try m.number(a, budget)), try m.member(a, "originalBytes", try m.number(a, original)),
        try m.member(a, "omissionCount", try m.number(a, omissions.len)), try m.member(a, "omittedOmissionCount", try m.number(a, omissions.len - @min(32, omissions.len))),
        try m.member(a, "omissions", .{ .array = omissions[0..@min(32, omissions.len)] }), try m.member(a, "readOperation", try m.string(a, "readProposal")),
    });
}

fn envelope(a: Allocator, value: Value, original: usize, omissions: []const Value) ![]const u8 {
    var fields: std.ArrayList(model.Member) = .empty;
    try fields.appendSlice(a, value.object);
    try fields.append(a, try m.member(a, "projection", try metadata(a, max_bytes, original, omissions)));
    return bytes(a, .{ .object = fields.items });
}

pub fn compact(a: Allocator, raw: []const u8) ![]const u8 {
    const value = try model.parseValue(a, raw);
    const uncut = try envelope(a, value, raw.len, &.{});
    if (uncut.len <= max_bytes) return uncut;
    const Limits = struct { depth: usize, receipt: bool, arrays: usize, text: usize };
    for ([_]Limits{ .{ .depth = 8, .receipt = true, .arrays = 8, .text = 256 }, .{ .depth = 8, .receipt = true, .arrays = 4, .text = 128 }, .{ .depth = 8, .receipt = true, .arrays = 2, .text = 64 }, .{ .depth = 8, .receipt = true, .arrays = 1, .text = 32 }, .{ .depth = 8, .receipt = true, .arrays = 0, .text = 0 }, .{ .depth = 8, .receipt = false, .arrays = 0, .text = 0 }, .{ .depth = 2, .receipt = false, .arrays = 0, .text = 0 }, .{ .depth = 1, .receipt = false, .arrays = 0, .text = 0 } }) |limits| {
        var p: Project = .{ .a = a, .depth = limits.depth, .receipt = limits.receipt, .arrays = limits.arrays, .text = limits.text };
        const projected = try p.visit(0, "", value);
        const result = try envelope(a, projected, raw.len, p.omissions.items);
        if (result.len <= max_bytes) return result;
    }
    unreachable;
}

fn select(r: *@import("query.zig").Request, root: Value, path: []const u8) !Value {
    if (path.len == 0) return root;
    if (path[0] != '/') return r.fail("path must be an RFC 6901 JSON pointer, or empty for the review root.", .{});
    var value = root;
    var scalar = false;
    var parts = std.mem.splitScalar(u8, path[1..], '/');
    while (parts.next()) |part| {
        var i: usize = 0;
        while (i < part.len) : (i += 1) {
            if (part[i] != '~') continue;
            if (i + 1 >= part.len or (part[i + 1] != '0' and part[i + 1] != '1')) return r.fail("Invalid JSON pointer escape.", .{});
            i += 1;
        }
        const key = try std.mem.replaceOwned(u8, r.a, try std.mem.replaceOwned(u8, r.a, part, "~1", "/"), "~0", "~");
        if (scalar) return r.fail("Cannot traverse a scalar at path '{s}'.", .{path});
        switch (value) {
            .object => value = model.property(value, key) orelse return r.fail("No proposal review value at path '{s}'.", .{path}),
            .array => |items| {
                const index = std.fmt.parseInt(usize, key, 10) catch return r.fail("No proposal review item at path '{s}'.", .{path});
                if (index >= items.len or !m.same(key, try std.fmt.allocPrint(r.a, "{d}", .{index}))) return r.fail("No proposal review item at path '{s}'.", .{path});
                value = items[index];
            },
            else => return r.fail("Cannot traverse a scalar at path '{s}'.", .{path}),
        }
        scalar = m.same(key, "batch") and value == .array;
    }
    return value;
}

fn pageHeader(a: Allocator, id: []const u8, current: bool, structured: bool, path: []const u8, kind: []const u8, offset: usize, total: usize, count: usize, value: Value, original: usize, omissions: []const Value) ![]const u8 {
    return bytes(a, try m.object(a, &.{
        try m.member(a, "proposalId", try m.string(a, id)),
        try m.member(a, "baseIsCurrent", if (current) .json_true else .json_false),
        try m.member(a, "structuredReview", if (structured) .json_true else .json_false),
        try m.member(a, "path", try m.string(a, path)),
        try m.member(a, "kind", try m.string(a, kind)),
        try m.member(a, "offset", try m.number(a, offset)),
        try m.member(a, "totalCount", try m.number(a, total)),
        try m.member(a, "returnedCount", try m.number(a, count)),
        try m.member(a, "nextOffset", if (offset + count < total) try m.number(a, offset + count) else .json_null),
        try m.member(a, "value", value),
        try m.member(a, "projection", try metadata(a, 8192, original, omissions)),
    }));
}

fn pair(text: []const u16, at: usize) bool {
    return at > 0 and at < text.len and text[at - 1] >= 0xD800 and text[at - 1] <= 0xDBFF and text[at] >= 0xDC00 and text[at] <= 0xDFFF;
}

pub fn page(r: *@import("query.zig").Request, out: *std.Io.Writer, id: []const u8, current: bool, structured: bool, raw: []const u8, path: []const u8, offset_arg: i32, limit: i32) !void {
    const source = try select(r, try model.parseValue(r.a, raw), path);
    const scalar = boxedBatch(path, source);
    const total = if (scalar) @as(usize, 1) else switch (source) { .array => |v| v.len, .object => |v| v.len, .string => |v| v.len, else => @as(usize, 1) };
    const kind: []const u8 = if (scalar) "scalar" else switch (source) { .array => "array", .object => "object", .string => "stringUtf16", else => "scalar" };
    if (offset_arg < 0 or @as(usize, @intCast(offset_arg)) > total) return r.fail("offset must be within the selected value's totalCount.", .{});
    if (limit < 1 or limit > @as(i32, if (source == .string) 1024 else 20)) return r.fail("limit must be 1..20 items/properties, or 1..1024 UTF-16 code units for a String.", .{});
    const offset: usize = @intCast(offset_arg);
    var count = @min(@as(usize, @intCast(limit)), total - offset);
    if (source == .string) {
        if (pair(source.string, offset)) return r.fail("offset splits a surrogate pair; use the returned nextOffset.", .{});
        if (count > 0 and pair(source.string, offset + count)) {
            if (count == 1) return r.fail("limit would split a surrogate pair; use at least 2 UTF-16 code units.", .{});
            count -= 1;
        }
    }
    while (true) {
        const value: Value = if (scalar) (if (count == 0) .json_null else source) else switch (source) {
            .array => |v| .{ .array = v[offset..][0..count] },
            .object => |v| .{ .object = v[offset..][0..count] },
            .string => |v| .{ .string = v[offset..][0..count] },
            else => if (count == 0) .json_null else source,
        };
        const original = (try bytes(r.a, value)).len;
        const direct = try pageHeader(r.a, id, current, structured, path, kind, offset, total, count, value, original, &.{});
        if (direct.len <= 8192) return out.writeAll(direct);
        for ([_][2]usize{ .{ 4, 128 }, .{ 2, 64 }, .{ 1, 32 }, .{ 0, 0 } }) |limits| {
            var p: Project = .{ .a = r.a, .depth = 8, .receipt = false, .arrays = limits[0], .text = limits[1], .root_path = path, .root_offset = if (source == .array and !scalar) offset else null };
            const projected = try p.visit(0, path, value);
            const result = try pageHeader(r.a, id, current, structured, path, kind, offset, total, count, projected, original, p.omissions.items);
            if (result.len <= 8192) return out.writeAll(result);
        }
        if (count <= 1) {
            r.rejection = .{ .code = "InternalError", .message = "Unexpected internal failure; this request was not applied and the session is unchanged." };
            return error.Rejected;
        }
        count = @max(1, count / 2);
    }
}
