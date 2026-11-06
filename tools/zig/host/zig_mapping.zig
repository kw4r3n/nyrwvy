//! `<graph>.zig.json` tables: capability operations bound to declarations of a Zig source file, for
//! library mode. Display names are resolved here once; the emitter sees stable IDs.
const std = @import("std");
const q = @import("query.zig");
const recipe = @import("recipe.zig");
const model = @import("model_reader");
const emitter = @import("zig_emitter");
const host = @import("root");
const eql = std.mem.eql;

/// The path `@import` uses from the emitted file. Zig refuses an import above the root source's directory.
fn importPath(r: *q.Request, table: []const u8, source: []const u8, output: ?[]const u8) ![]const u8 {
    const a = r.a;
    const directory = std.fs.path.dirname(r.snapshot_path) orelse "/";
    const full = try std.fs.path.resolveAlloc(a, &.{ directory, source });
    const emitted = try std.fs.path.resolveAlloc(a, &.{ directory, output orelse "library.zig" });
    const from = std.fs.path.dirname(emitted) orelse "/";
    const relative = try std.fs.path.relativeAlloc(a, from, null, from, full);
    if (std.fs.path.isAbsolute(relative) or eql(u8, relative, "..") or std.mem.startsWith(u8, relative, "../") or std.mem.startsWith(u8, relative, "..\\")) return r.fail("mapping table {s}: source must lie at or below the emitted file's directory.", .{table});
    // Zig reads any other import as a module name.
    if (!std.mem.endsWith(u8, relative, ".zig")) return r.fail("mapping table {s}: source must name an existing Zig file.", .{table});
    for (relative) |c| if (c == '"' or c < ' ') return r.fail("mapping table {s}: source has a character an import cannot hold.", .{table});
    const info = std.Io.Dir.cwd().statFile(host.host_io, full, .{}) catch return r.fail("mapping table {s}: source must name an existing Zig file.", .{table});
    if (info.kind != .file) return r.fail("mapping table {s}: source must name an existing Zig file.", .{table});
    return std.mem.replaceOwned(u8, a, relative, "\\", "/");
}

fn isIdentifier(name: []const u8) bool {
    if (name.len == 0 or std.ascii.isDigit(name[0])) return false;
    for (name) |c| if (!std.ascii.isAlphanumeric(c) and c != '_') return false;
    return true;
}

fn operationEntry(d: *recipe.Decoder, table: []const u8, path: []const u8, name: []const u8, value: std.json.Value) !?[]const u8 {
    const r = d.r;
    const context = try std.fmt.allocPrint(r.a, "mapping table {s}: {s}", .{ table, name });
    try d.ensureFields(path, value, context, &.{ "declaration", "unavailable" }, &.{});
    if (value.object.contains("unavailable")) {
        try d.optionalBool(value, "unavailable");
        if (!value.object.get("unavailable").?.bool or value.object.contains("declaration")) return r.fail("{s}: unavailable must be true and names no declaration.", .{context});
        return null;
    }
    const declaration = try d.requiredString(value, "declaration");
    if (!isIdentifier(declaration)) return r.fail("{s}: declaration must be an identifier.", .{context});
    return declaration;
}

pub fn bindings(r: *q.Request, tables: []const []const u8, output: ?[]const u8) ![]const emitter.HostBinding {
    const a = r.a;
    var out: std.ArrayList(emitter.HostBinding) = .empty;
    const capabilities = try q.typedVisible(r, "capability");
    for (tables) |table| {
        var d: recipe.Decoder = .{ .r = r, .duplicates = .empty };
        const value = (try @import("recipe_file.zig").load(&d, table, "zigTables", "")).value;
        const context = try std.fmt.allocPrint(a, "mapping table {s}", .{table});
        try d.ensureFields("", value, context, &.{ "format", "version", "source", "capabilities" }, &.{ "format", "version", "source", "capabilities" });
        if (!eql(u8, try d.requiredString(value, "format"), "nyrwvy.zig-mapping")) return r.fail("{s}: format must be 'nyrwvy.zig-mapping'.", .{context});
        const version = value.object.get("version").?;
        if (version != .integer or version.integer != 1) return r.fail("{s}: only version 1 is supported.", .{context});
        const source = try importPath(r, table, try d.requiredString(value, "source"), output);
        const listed = value.object.get("capabilities").?;
        if (listed != .object) return r.fail("{s}: capabilities must be an object.", .{context});
        for (listed.object.keys(), listed.object.values()) |name, item| {
            const at = try std.fmt.allocPrint(a, "/capabilities/{s}", .{name});
            try d.ensureFields(at, item, try std.fmt.allocPrint(a, "{s}: capability {s}", .{ context, name }), &.{"operations"}, &.{"operations"});
            const entries = item.object.get("operations").?;
            if (entries != .object) return r.fail("{s}: {s}.operations must be an object.", .{ context, name });
            var found: ?model.Model = null;
            for (capabilities) |candidate| if (eql(u8, q.nameOf(candidate), name)) {
                if (found != null) return r.fail("{s}: several capabilities are named '{s}'.", .{ context, name });
                found = candidate;
            };
            const capability = found orelse return r.fail("{s}: no capability is named '{s}'.", .{ context, name });
            const operations = capability.operations();
            for (entries.object.keys()) |key| {
                for (operations) |operation| {
                    if (eql(u8, q.nameOf(operation), key)) break;
                } else return r.fail("{s}: capability {s} has no operation '{s}'.", .{ context, name, key });
            }
            for (operations) |operation| {
                const label = q.nameOf(operation);
                const entry = entries.object.get(label) orelse return r.fail("{s}: {s}.{s} is neither bound nor marked unavailable.", .{ context, name, label });
                for (out.items) |prior| if (eql(u8, prior.operation, q.idOf(operation))) return r.fail("{s}: {s}.{s} is bound twice.", .{ context, name, label });
                try out.append(a, .{
                    .capability = q.idOf(capability),
                    .operation = q.idOf(operation),
                    .source = source,
                    .declaration = try operationEntry(&d, table, try std.fmt.allocPrint(a, "{s}/operations/{s}", .{ at, label }), try std.fmt.allocPrint(a, "{s}.{s}", .{ name, label }), entry),
                });
            }
        }
    }
    return out.items;
}
