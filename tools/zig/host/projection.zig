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

const Project = struct {
    a: Allocator,
    depth: usize,
    receipt: bool,
    arrays: usize,
    text: usize,
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
        if (m.same(path, "/proposalId") or m.same(path, "/detail") or (std.mem.endsWith(u8, path, "/kind") and value == .string and value.string.len <= 64)) return value;
        if (value == .string and identityPath(path)) {
            if (value.string.len <= 256) return value;
            try self.omit(path, "stringUtf16", value.string.len, 0);
            return .json_null;
        }
        if (m.same(path, "/review/receipt")) {
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
                const shown = @min(items.len, self.arrays);
                if (shown < items.len) try self.omit(path, "array", items.len, shown);
                const out = try a.alloc(Value, shown);
                for (items[0..shown], out, 0..) |v, *o, i| o.* = try self.visit(depth + 1, try std.fmt.allocPrint(a, "{s}/{d}", .{ path, i }), v);
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

fn metadata(a: Allocator, original: usize, omissions: []const Value) !Value {
    const complete: Value = if (omissions.len == 0) .json_true else .json_false;
    return m.object(a, &.{
        try m.member(a, "complete", complete), try m.member(a, "requiresDetailedReview", if (omissions.len == 0) .json_false else .json_true),
        try m.member(a, "maxBytes", try m.number(a, max_bytes)), try m.member(a, "originalBytes", try m.number(a, original)),
        try m.member(a, "omissionCount", try m.number(a, omissions.len)), try m.member(a, "omittedOmissionCount", try m.number(a, omissions.len - @min(32, omissions.len))),
        try m.member(a, "omissions", .{ .array = omissions[0..@min(32, omissions.len)] }), try m.member(a, "readOperation", try m.string(a, "readProposal")),
    });
}

fn envelope(a: Allocator, value: Value, original: usize, omissions: []const Value) ![]const u8 {
    var fields: std.ArrayList(model.Member) = .empty;
    try fields.appendSlice(a, value.object);
    try fields.append(a, try m.member(a, "projection", try metadata(a, original, omissions)));
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
