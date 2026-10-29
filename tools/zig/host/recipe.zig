//! Decoding a request's types and exact-ID expression recipes as the F# host does (`WireInput`,
//! `WireRecipe`, `WireRecipeCommon`, `WireRecipeNames`), with its rejections. A recipe that decodes
//! stays the request's JSON value; the later steps read it from there.
const std = @import("std");
const query = @import("query.zig");

const Json = std.json.Value;
const Allocator = std.mem.Allocator;
const Fail = anyerror;

const Shape = enum { recipe, recipes, id, type, types, int, int64, bool, text, bits, optrecipe, context, args, capargs, fields, mode, slot, selfargs, impls, arms };
const Read = struct { name: []const u8, shape: Shape };
const Kind = struct { context: []const u8, allowed: []const []const u8, required: []const []const u8, reads: []const Read };

var kinds: ?std.StringHashMapUnmanaged(Kind) = null;

fn words(gpa: Allocator, text: []const u8) []const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    var it = std.mem.tokenizeScalar(u8, text, ' ');
    while (it.next()) |word| out.append(gpa, word) catch @panic("out of memory");
    return out.items;
}

fn kindTable() std.StringHashMapUnmanaged(Kind) {
    if (kinds) |table| return table;
    const gpa = std.heap.page_allocator;
    var table: std.StringHashMapUnmanaged(Kind) = .empty;
    var lines = std.mem.tokenizeScalar(u8, @embedFile("recipe_kinds.txt"), '\n');
    while (lines.next()) |line| {
        if (line[0] == '#') continue;
        var parts = std.mem.splitScalar(u8, line, '|');
        const name = parts.next().?;
        const context = parts.next().?;
        const allowed = words(gpa, parts.next().?);
        const required = words(gpa, parts.next().?);
        var reads: std.ArrayList(Read) = .empty;
        for (words(gpa, parts.next().?)) |item| {
            const colon = std.mem.indexOfScalar(u8, item, ':').?;
            reads.append(gpa, .{ .name = item[0..colon], .shape = std.meta.stringToEnum(Shape, item[colon + 1 ..]) orelse std.debug.panic("recipe_kinds.txt: unknown shape '{s}'", .{item}) }) catch @panic("out of memory");
        }
        table.put(gpa, name, .{ .context = context, .allowed = allowed, .required = required, .reads = reads.items }) catch @panic("out of memory");
    }
    kinds = table;
    return table;
}

/// The paths (`/key/index/…` from the request root) of the objects that name a property twice, as
/// the last occurrence of each object shows them; `ensureFields` rejects those when it reaches them.
pub fn duplicatePaths(a: Allocator, line: []const u8) !std.StringHashMapUnmanaged(void) {
    const Frame = struct { object: bool, path: []const u8, names: std.StringHashMapUnmanaged(void) = .empty, key: []const u8 = "", index: usize = 0, duplicate: bool = false, expecting_name: bool = false };
    var out: std.StringHashMapUnmanaged(void) = .empty;
    var frames: std.ArrayList(Frame) = .empty;
    var scanner = std.json.Scanner.initCompleteInput(a, line);
    while (true) {
        const token = try scanner.nextAlloc(a, .alloc_always);
        switch (token) {
            .end_of_document => return out,
            .object_begin, .array_begin => {
                const path = if (frames.items.len == 0) "" else blk: {
                    const parent = &frames.items[frames.items.len - 1];
                    break :blk if (parent.object) try std.fmt.allocPrint(a, "{s}/{s}", .{ parent.path, parent.key }) else try std.fmt.allocPrint(a, "{s}/{d}", .{ parent.path, parent.index });
                };
                try frames.append(a, .{ .object = token == .object_begin, .path = path, .expecting_name = token == .object_begin });
                continue;
            },
            .object_end, .array_end => {
                const frame = frames.pop().?;
                if (frame.object) {
                    if (frame.duplicate) try out.put(a, frame.path, {}) else _ = out.remove(frame.path);
                }
            },
            .allocated_string => |text| {
                const frame = &frames.items[frames.items.len - 1];
                if (frame.object and frame.expecting_name) {
                    if ((try frame.names.fetchPut(a, text, {})) != null) frame.duplicate = true;
                    frame.key = text;
                    frame.expecting_name = false;
                    continue;
                }
            },
            else => {},
        }
        // A value ended: the next string of an object is a name, the next item of an array has the next index.
        if (frames.items.len > 0) {
            const frame = &frames.items[frames.items.len - 1];
            if (frame.object) frame.expecting_name = true else frame.index += 1;
        }
    }
}

pub const Decoder = struct {
    r: *query.Request,
    duplicates: std.StringHashMapUnmanaged(void),

    fn fail(self: *Decoder, comptime format: []const u8, args: anytype) Fail {
        return self.r.fail(format, args);
    }

    fn at(self: *Decoder, path: []const u8, key: anytype) ![]const u8 {
        return switch (@TypeOf(key)) {
            usize => std.fmt.allocPrint(self.r.a, "{s}/{d}", .{ path, key }),
            else => std.fmt.allocPrint(self.r.a, "{s}/{s}", .{ path, key }),
        };
    }

    fn object(self: *Decoder, value: Json) Fail!std.json.ObjectMap {
        if (value != .object) return self.fail("Expected a JSON object.", .{});
        try query.lookable(self.r, value.object);
        return value.object;
    }

    /// `WireInput.ensureFields`.
    pub fn ensureFields(self: *Decoder, path: []const u8, value: Json, context: []const u8, allowed: []const []const u8, required: []const []const u8) Fail!void {
        const map = try self.object(value);
        if (self.duplicates.contains(path)) return self.fail("JSON object contains duplicate property names.", .{});
        const a = self.r.a;
        var unknown: std.ArrayList([]const u8) = .empty;
        for (map.keys()) |key| {
            for (allowed) |field| {
                if (std.mem.eql(u8, key, field)) break;
            } else try unknown.append(a, key);
        }
        var missing: std.ArrayList([]const u8) = .empty;
        for (required) |field| if (map.get(field) == null) try missing.append(a, field);
        const ordinal = struct {
            fn less(_: void, x: []const u8, y: []const u8) bool {
                return std.mem.order(u8, x, y) == .lt;
            }
        }.less;
        std.mem.sort([]const u8, unknown.items, {}, ordinal);
        std.mem.sort([]const u8, missing.items, {}, ordinal);
        if (unknown.items.len > 0) return self.fail("{s} contains unsupported properties: {s}. Allowed: {s}.", .{ context, try std.mem.join(a, ", ", unknown.items), try std.mem.join(a, ", ", allowed) });
        if (missing.items.len > 0) return self.fail("{s} is missing required properties: {s}.", .{ context, try std.mem.join(a, ", ", missing.items) });
    }

    /// `WireInput.property`.
    pub fn property(self: *Decoder, value: Json, name: []const u8) Fail!Json {
        const map = try self.object(value);
        return map.get(name) orelse self.fail("Missing required property '{s}'.", .{name});
    }

    fn tryProperty(self: *Decoder, value: Json, name: []const u8) Fail!?Json {
        const map = try self.object(value);
        return map.get(name);
    }

    pub fn requiredString(self: *Decoder, value: Json, name: []const u8) Fail![]const u8 {
        const v = try self.property(value, name);
        if (v != .string) return self.fail("Property '{s}' must be a JSON string.", .{name});
        return query.readable(self.r, v.string);
    }

    /// `WireInput.stableId`: `StableId.tryCreate`'s two errors.
    pub fn stableId(self: *Decoder, label: []const u8, text: []const u8) Fail![]const u8 {
        var it = std.unicode.Utf8View.initUnchecked(text).iterator();
        var blank = true;
        while (it.nextCodepoint()) |c| switch (c) {
            0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => {},
            else => blank = false,
        };
        if (blank) return self.fail("{s} is not a valid stable ID: BlankValue.", .{label});
        if (std.mem.indexOfAny(u8, text, "\r\n") != null) return self.fail("{s} is not a valid stable ID: ContainsLineBreak.", .{label});
        return text;
    }

    fn id(self: *Decoder, value: Json, name: []const u8) Fail!void {
        _ = try self.stableId(name, try self.requiredString(value, name));
    }

    pub fn array(self: *Decoder, value: Json, name: []const u8) Fail![]const Json {
        const v = try self.property(value, name);
        if (v != .array) return self.fail("Property '{s}' must be a JSON array.", .{name});
        return v.array.items;
    }

    pub fn optionalBool(self: *Decoder, value: Json, name: []const u8) Fail!void {
        const v = (try self.tryProperty(value, name)) orelse return;
        if (v != .bool) return self.fail("Property '{s}' must be a JSON boolean.", .{name});
    }

    fn requiredInt(self: *Decoder, value: Json, name: []const u8) Fail!void {
        const v = try self.property(value, name);
        const ok = v == .integer and v.integer >= std.math.minInt(i32) and v.integer <= std.math.maxInt(i32);
        if (!ok) return self.fail("Property '{s}' must be a 32-bit integer.", .{name});
    }

    /// `WireInput.requiredInt64`: a JSON integer, or a string `Int64.TryParse` takes with a leading sign only.
    fn requiredInt64(self: *Decoder, value: Json, name: []const u8) Fail!void {
        const v = try self.property(value, name);
        const ok = switch (v) {
            .integer => true,
            .string => |given| blk: {
                // `Int64.TryParse` also ignores trailing NUL characters.
                const text = std.mem.trimEnd(u8, try query.readable(self.r, given), "\x00");
                const digits = if (text.len > 0 and (text[0] == '-' or text[0] == '+')) text[1..] else text;
                if (digits.len == 0) break :blk false;
                for (digits) |c| if (!std.ascii.isDigit(c)) break :blk false;
                _ = std.fmt.parseInt(i64, text, 10) catch break :blk false;
                break :blk true;
            },
            else => false,
        };
        if (!ok) return self.fail("Property '{s}' must be a 64-bit integer or exact decimal integer string.", .{name});
    }

    /// `WireInput.typeRef`.
    pub fn typeRef(self: *Decoder, path: []const u8, value: Json) Fail!void {
        const kind = try self.requiredString(value, "kind");
        const eql = std.mem.eql;
        if (eql(u8, kind, "function")) {
            try self.ensureFields(path, value, "Function type", &.{ "kind", "parameters", "resultType" }, &.{ "kind", "parameters", "resultType" });
            for (try self.array(value, "parameters"), 0..) |item, i| try self.typeRef(try self.at(try self.at(path, "parameters"), i), item);
            try self.typeRef(try self.at(path, "resultType"), try self.property(value, "resultType"));
        } else if (eql(u8, kind, "primitive")) {
            try self.ensureFields(path, value, "primitive type", &.{ "kind", "primitive" }, &.{ "kind", "primitive" });
            const primitive = try self.requiredString(value, "primitive");
            for ([_][]const u8{ "string", "bool", "unit", "int64", "float64", "bits32", "bytes" }) |known| {
                if (eql(u8, known, primitive)) break;
            } else return self.fail("Unsupported primitive type '{s}'.", .{primitive});
        } else if (eql(u8, kind, "named")) {
            try self.ensureFields(path, value, "named type", &.{ "kind", "typeId", "arguments" }, &.{ "kind", "typeId" });
            try self.id(value, "typeId");
            try self.typeArguments(path, value, "arguments");
        } else if (eql(u8, kind, "capability")) {
            try self.ensureFields(path, value, "capability type", &.{ "kind", "capabilityId" }, &.{ "kind", "capabilityId" });
            try self.id(value, "capabilityId");
        } else if (eql(u8, kind, "typeParameter") and (try self.tryProperty(value, "name")) != null) {
            try self.ensureFields(path, value, "type parameter", &.{ "kind", "name" }, &.{ "kind", "name" });
            const name = try self.requiredString(value, "name");
            if (try isBlank(self.r.a, name)) return self.fail("Type parameter names must not be blank.", .{});
            try self.id(value, "name");
        } else if (eql(u8, kind, "typeParameter")) {
            try self.ensureFields(path, value, "type parameter", &.{ "kind", "typeParameterId", "displayName" }, &.{ "kind", "typeParameterId" });
            try self.id(value, "typeParameterId");
        } else if (eql(u8, kind, "builtinGeneric")) {
            try self.ensureFields(path, value, "builtin generic type", &.{ "kind", "generic", "arguments" }, &.{ "kind", "generic", "arguments" });
            const generic = try self.requiredString(value, "generic");
            for ([_][]const u8{ "option", "result", "map", "list", "set", "oneShot", "task", "exception", "taskScope" }) |known| {
                if (eql(u8, known, generic)) break;
            } else return self.fail("Unsupported builtin generic '{s}'.", .{generic});
            for (try self.array(value, "arguments"), 0..) |item, i| try self.typeRef(try self.at(try self.at(path, "arguments"), i), item);
        } else return self.fail("Unsupported type kind '{s}'.", .{kind});
    }

    fn typeArguments(self: *Decoder, path: []const u8, value: Json, name: []const u8) Fail!void {
        if ((try self.tryProperty(value, name)) == null) return;
        for (try self.array(value, name), 0..) |item, i| try self.typeRef(try self.at(try self.at(path, name), i), item);
    }

    /// Bindings of one target each: call arguments, capability arguments, field values.
    fn targets(self: *Decoder, path: []const u8, value: Json, name: []const u8, context: []const u8, target: []const u8) Fail!void {
        for (try self.array(value, name), 0..) |item, i| {
            const item_path = try self.at(try self.at(path, name), i);
            try self.ensureFields(item_path, item, context, &.{ target, "value" }, &.{ target, "value" });
            try self.id(item, target);
            try self.recipe(try self.at(item_path, "value"), try self.property(item, "value"));
        }
    }

    /// `WireRecipe.recipeIn` (after `WireRecipeNames.resolve`) of an exact-ID recipe.
    pub fn recipe(self: *Decoder, path: []const u8, value: Json) Fail!void {
        const kind_name = try self.requiredString(value, "kind");
        const kind = kindTable().get(kind_name) orelse return self.fail("Unsupported expression recipe kind '{s}'.", .{kind_name});
        try self.ensureFields(path, value, kind.context, kind.allowed, kind.required);
        for (kind.reads) |read| {
            const name = read.name;
            const here = try self.at(path, name);
            switch (read.shape) {
                .recipe => try self.recipe(here, try self.property(value, name)),
                .recipes => for (try self.array(value, name), 0..) |item, i| try self.recipe(try self.at(here, i), item),
                .optrecipe => if (try self.tryProperty(value, name)) |item| try self.recipe(here, item),
                .id => try self.id(value, name),
                .type => try self.typeRef(here, try self.property(value, name)),
                .types => try self.typeArguments(path, value, name),
                .int => try self.requiredInt(value, name),
                .int64 => try self.requiredInt64(value, name),
                .bool => {
                    const v = try self.property(value, name);
                    if (v != .bool) return self.fail("Property '{s}' must be a JSON boolean.", .{name});
                },
                .text => {
                    const v = try self.property(value, name);
                    if (v != .string) return self.fail("A String run value must be a JSON string.", .{});
                    if (query.masked and std.mem.indexOf(u8, v.string, query.lone_mark) != null)
                        return self.fail("A String must not contain an unpaired surrogate escape; a String is a sequence of Unicode scalar values.", .{});
                },
                .bits => {
                    const bits = try self.requiredString(value, name);
                    // `String.Length`: UTF-16 units.
                    if (try std.unicode.calcUtf16LeLen(bits) != 16) return self.fail("Invalid Float64 bits: Float64 bits must contain exactly 16 hexadecimal digits..", .{});
                    // `UInt64.TryParse` ignores trailing NUL characters, which the length above counts.
                    const digits = std.mem.trimEnd(u8, bits, "\x00");
                    _ = std.fmt.parseInt(u64, digits, 16) catch return self.fail("Invalid Float64 bits: Float64 bits contain invalid hexadecimal digits..", .{});
                    for (digits) |c| if (!std.ascii.isHex(c)) return self.fail("Invalid Float64 bits: Float64 bits contain invalid hexadecimal digits..", .{});
                },
                .context => if ((try self.tryProperty(value, name)) != null) try self.targets(path, value, name, "call argument", "parameterId"),
                .args => try self.targets(path, value, name, "call argument", "parameterId"),
                .capargs => try self.targets(path, value, name, "capability argument", "capabilityParameterId"),
                .fields => try self.targets(path, value, name, "field recipe", "fieldId"),
                .mode => {
                    const mode = try self.requiredString(value, "mode");
                    if (!std.mem.eql(u8, mode, "any") and !std.mem.eql(u8, mode, "all")) return self.fail("Unsupported task wait mode '{s}'; expected 'any' or 'all'.", .{mode});
                },
                .slot => {
                    const slot = try self.requiredString(value, "slot");
                    if (std.mem.eql(u8, slot, "field")) {
                        const by_id, const by_name = .{ (try self.tryProperty(value, "fieldId")) != null, (try self.tryProperty(value, "field")) != null };
                        if (by_id and !by_name) {
                            try self.id(value, "fieldId");
                        } else if (by_name and !by_id) {
                            const field = try self.requiredString(value, "field");
                            return self.fail("Pattern reference by field name '{s}' is available only in an addUnionVariant arm body; use fieldId.", .{field});
                        } else return self.fail("A field pattern reference takes exactly one of fieldId and field.", .{});
                    } else if (!std.mem.eql(u8, slot, "value") and !std.mem.eql(u8, slot, "error")) return self.fail("Unsupported pattern slot '{s}'.", .{slot});
                },
                .selfargs => for (try self.array(value, "arguments"), 0..) |item, i| {
                    const item_path = try self.at(try self.at(path, "arguments"), i);
                    try self.ensureFields(item_path, item, "self-call argument", &.{ "slot", "value" }, &.{ "slot", "value" });
                    try self.requiredInt(item, "slot");
                    try self.recipe(try self.at(item_path, "value"), try self.property(item, "value"));
                },
                .impls => for (try self.array(value, "implementations"), 0..) |item, i| {
                    try self.ensureFields(try self.at(try self.at(path, "implementations"), i), item, "Capability implementation", &.{ "operationId", "functionId" }, &.{ "operationId", "functionId" });
                    try self.id(item, "operationId");
                    try self.id(item, "functionId");
                },
                .arms => {
                    for (try self.array(value, "arms"), 0..) |arm, i| {
                        const arm_path = try self.at(try self.at(path, "arms"), i);
                        try self.ensureFields(arm_path, arm, "union arm", &.{ "variantId", "variantIds", "body" }, &.{"body"});
                        const single, const plural = .{ (try self.tryProperty(arm, "variantId")) != null, (try self.tryProperty(arm, "variantIds")) != null };
                        if (single and !plural) {
                            _ = try self.stableId("variantId", try self.requiredString(arm, "variantId"));
                        } else if (plural and !single) {
                            const items = try self.array(arm, "variantIds");
                            if (items.len == 0) return self.fail("Property 'variantIds' must name at least one variant.", .{});
                            for (items) |item| {
                                if (item != .string) return self.fail("Property 'variantIds' must contain only strings.", .{});
                                _ = try self.stableId("variantId", try query.readable(self.r, item.string));
                            }
                        } else return self.fail("A union arm requires exactly one of 'variantId' and 'variantIds'.", .{});
                        try self.recipe(try self.at(arm_path, "body"), try self.property(arm, "body"));
                    }
                    if (try self.tryProperty(value, "otherwise")) |otherwise| try self.recipe(try self.at(path, "otherwise"), otherwise);
                },
            }
        }
    }
};

fn isBlank(a: Allocator, text: []const u8) !bool {
    _ = a;
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.nextCodepoint()) |c| switch (c) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => {},
        else => return false,
    };
    return true;
}

// ---------------------------------------------------------------- named pattern references

/// `WireRecipeNames.resolve`: before decoding, a `patternReference` by `name` becomes the
/// `scopeDepth` and `slot` of the innermost binder in the same recipe that declares the name, and
/// the binder names are dropped. Frames are pushed as inference pushes them.
pub fn resolveNames(d: *Decoder, value: *Json, raw: []const u8) Fail!void {
    // Only a recipe whose text names a binder or a reference is rewritten (`resolve`'s guard).
    if (std.mem.indexOf(u8, raw, "Name\"") == null and std.mem.indexOf(u8, raw, "\"name\"") == null) return;
    var frames: std.ArrayList(?Binder) = .empty;
    try visit(d, &frames, value);
    // `ToJsonString` then reads every string of the copy again.
    if (query.masked and std.mem.indexOf(u8, raw, "\\ufdd0") != null) _ = try query.readable(d.r, query.lone_mark);
}

const Binder = struct { name: []const u8, slot: []const u8 };

fn takeName(d: *Decoder, map: *std.json.ObjectMap, key: []const u8) Fail!?[]const u8 {
    const entry = map.fetchOrderedRemove(key) orelse return null;
    if (entry.value != .string) return d.fail("Property '{s}' must be a binding name string.", .{key});
    return try query.readable(d.r, entry.value.string);
}

fn kindOf(map: std.json.ObjectMap) []const u8 {
    const kind = map.get("kind") orelse return "";
    return if (kind == .string) kind.string else "";
}

fn visitChild(d: *Decoder, frames: *std.ArrayList(?Binder), map: *std.json.ObjectMap, key: []const u8, frame: ?Binder) Fail!void {
    const child = map.getPtr(key) orelse return;
    if (child.* == .null) return;
    try frames.insert(d.r.a, 0, frame);
    defer _ = frames.orderedRemove(0);
    try visit(d, frames, child);
}

fn visitRest(d: *Decoder, frames: *std.ArrayList(?Binder), map: *std.json.ObjectMap, skip: []const []const u8) Fail!void {
    for (map.keys(), map.values()) |key, *child| {
        for (skip) |s| {
            if (std.mem.eql(u8, s, key)) break;
        } else if (child.* != .null) try visit(d, frames, child);
    }
}

fn visit(d: *Decoder, frames: *std.ArrayList(?Binder), value: *Json) Fail!void {
    const a = d.r.a;
    switch (value.*) {
        .array => |*items| for (items.items) |*item| try visit(d, frames, item),
        .object => |*map| {
            const kind = kindOf(map.*);
            const eql = std.mem.eql;
            if (eql(u8, kind, "patternReference")) {
                const name = (try takeName(d, map, "name")) orelse return;
                if (map.get("scopeDepth") != null or map.get("slot") != null) return d.fail("Pattern reference '{s}' gives both a name and scopeDepth or slot; use one form.", .{name});
                for (frames.items, 0..) |frame, depth| if (frame) |f| if (eql(u8, f.name, name)) {
                    try map.put(a, "scopeDepth", .{ .integer = @intCast(depth) });
                    try map.put(a, "slot", .{ .string = f.slot });
                    return;
                };
                var listed: std.ArrayList([]const u8) = .empty;
                for (frames.items) |frame| if (frame) |f| {
                    for (listed.items) |seen| {
                        if (eql(u8, seen, f.name)) break;
                    } else try listed.append(a, f.name);
                };
                return d.fail("No binder in this recipe names '{s}' (names in scope: {s}). A binding outside the recipe still needs scopeDepth; nyrwvy_inspect view scopes (op bindingScopes) lists the bindings visible at a node with their scopeDepth.", .{ name, if (listed.items.len == 0) "none" else try std.mem.join(a, ", ", listed.items) });
            } else if (eql(u8, kind, "bind")) {
                const name = try takeName(d, map, "name");
                try visitRest(d, frames, map, &.{"body"});
                try visitChild(d, frames, map, "body", if (name) |n| .{ .name = n, .slot = "value" } else null);
            } else if (eql(u8, kind, "matchOption")) {
                const name = try takeName(d, map, "someName");
                try visitRest(d, frames, map, &.{ "someBody", "noneBody" });
                try visitChild(d, frames, map, "someBody", if (name) |n| .{ .name = n, .slot = "value" } else null);
                try visitChild(d, frames, map, "noneBody", null);
            } else if (eql(u8, kind, "matchResult")) {
                const ok_name = try takeName(d, map, "okName");
                const err_name = try takeName(d, map, "errName");
                try visitRest(d, frames, map, &.{ "okBody", "errBody" });
                try visitChild(d, frames, map, "okBody", if (ok_name) |n| .{ .name = n, .slot = "value" } else null);
                try visitChild(d, frames, map, "errBody", if (err_name) |n| .{ .name = n, .slot = "error" } else null);
            } else if (eql(u8, kind, "matchUnion")) {
                try visitRest(d, frames, map, &.{ "arms", "otherwise" });
                if (map.getPtr("arms")) |arms| {
                    if (arms.* == .array) {
                        for (arms.array.items) |*arm| {
                            if (arm.* != .object) {
                                try visit(d, frames, arm);
                                continue;
                            }
                            // In key order; only the body is a new frame.
                            for (arm.object.keys(), arm.object.values()) |key, *child| {
                                if (child.* == .null) continue;
                                if (std.mem.eql(u8, key, "body")) {
                                    try frames.insert(a, 0, null);
                                    defer _ = frames.orderedRemove(0);
                                    try visit(d, frames, child);
                                } else try visit(d, frames, child);
                            }
                        }
                    } else if (arms.* != .null) try visit(d, frames, arms);
                }
                try visitChild(d, frames, map, "otherwise", null);
            } else try visitRest(d, frames, map, &.{});
        },
        else => {},
    }
}

/// The raw text of the request root's `key` value, as the line spells it.
pub fn rawProperty(a: Allocator, line: []const u8, key: []const u8) ![]const u8 {
    var scanner = std.json.Scanner.initCompleteInput(a, line);
    var depth: usize = 0;
    // At the root object: whether the next string is a name, and where the wanted value starts.
    var name_next = false;
    var start: ?usize = null;
    while (true) {
        const token = try scanner.nextAlloc(a, .alloc_always);
        var ended = false;
        switch (token) {
            .end_of_document => return "",
            .object_begin, .array_begin => {
                depth += 1;
                name_next = depth == 1;
            },
            .object_end, .array_end => {
                depth -= 1;
                ended = depth == 1;
            },
            .allocated_string => |text| {
                if (depth == 1 and name_next) {
                    name_next = false;
                    if (start == null and std.mem.eql(u8, text, key)) start = scanner.cursor;
                } else ended = depth == 1;
            },
            else => ended = depth == 1,
        }
        if (ended) {
            if (start) |s| return std.mem.trim(u8, line[s..scanner.cursor], " \t\r\n:");
            name_next = true;
        }
    }
}
