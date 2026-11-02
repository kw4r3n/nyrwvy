
const NyTokens = struct {
    items: []const [:0]const u8,
    index: usize = 1,

    fn next(self: *NyTokens) []const u8 {
        const token = self.items[self.index];
        self.index += 1;
        return token;
    }
};

/// A String token is "s" and the Base64 of its UTF-16LE code units.
fn nyDecodeText(token: []const u8) []const u8 {
    const encoded = token[1..];
    const size = std.base64.standard.Decoder.calcSizeForSlice(encoded) catch @panic("bad String token");
    const units = nyAllocator().alloc(u16, size / 2) catch @panic("out of memory");
    std.base64.standard.Decoder.decode(std.mem.sliceAsBytes(units), encoded) catch @panic("bad String token");
    for (units) |*unit| unit.* = std.mem.littleToNative(u16, unit.*);
    return nyAdopt(u8, std.unicode.utf16LeToUtf8Alloc(nyRaw(), units) catch @panic("bad String token"));
}

fn nyEncodeText(out: *std.Io.Writer, text: []const u8) !void {
    const units = std.unicode.utf8ToUtf16LeAlloc(nyAllocator(), text) catch @panic("bad String");
    const bytes = std.mem.sliceAsBytes(units);
    const encoded = nyAllocator().alloc(u8, std.base64.standard.Encoder.calcSize(bytes.len)) catch @panic("out of memory");
    try out.print("\"s{s}\"", .{std.base64.standard.Encoder.encode(encoded, bytes)});
}

fn nyDecode(comptime T: type, tokens: *NyTokens) T {
    switch (@typeInfo(T)) {
        .int => return std.fmt.parseInt(T, tokens.next(), 10) catch @panic("bad integer token"),
        // A Float64 token is its 16 hexadecimal bit digits.
        .float => return @bitCast(std.fmt.parseInt(u64, tokens.next(), 16) catch @panic("bad Float64 token")),
        .bool => return std.mem.eql(u8, tokens.next(), "1"),
        .optional => |info| {
            if (std.mem.eql(u8, tokens.next(), "1")) return nyDecode(info.child, tokens);
            return null;
        },
        .pointer => |info| {
            if (info.size == .slice and info.child == u8) return nyDecodeText(tokens.next());
            if (info.size == .slice) {
                const count = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad List token");
                const items = nyAlloc(info.child, count);
                for (items) |*item| item.* = nyDecode(info.child, tokens);
                return items;
            }
            return nyBox(info.child, nyDecode(info.child, tokens));
        },
        .@"struct" => |info| {
            if (T == NyUnit) {
                _ = tokens.next();
                return NyUnit{};
            }
            if (comptime @hasDecl(T, "ny_set")) {
                const Item = @typeInfo(@FieldType(T, "items")).pointer.child;
                const list = nyDecode([]const Item, tokens);
                defer nyRelease(list);
                return nySetFromList(Item, list);
            }
            // Bytes travel as the String token of their latin-1 form.
            if (comptime @hasDecl(T, "ny_bytes")) {
                const text = nyDecodeText(tokens.next());
                defer nyRelease(text);
                return nyBytesFromLatin1(text);
            }
            if (comptime @hasDecl(T, "ny_map")) {
                const K = @typeInfo(@FieldType(T, "keys")).pointer.child;
                const V = @typeInfo(@FieldType(T, "values")).pointer.child;
                const count = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad Map token");
                var map = T{ .keys = &.{}, .values = &.{} };
                for (0..count) |_| {
                    const key = nyDecode(K, tokens);
                    map = nyMapInsertOwned(K, V, map, key, nyDecode(V, tokens));
                }
                return map;
            }
            var value: T = undefined;
            inline for (info.field_names, info.field_types) |name, FieldType| @field(value, name) = nyDecode(FieldType, tokens);
            return value;
        },
        .@"union" => |info| {
            if (@hasDecl(T, "ny_ids")) {
                const index = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad variant token");
                inline for (info.field_names, info.field_types, 0..) |name, FieldType, position| {
                    if (position == index) return @unionInit(T, name, nyDecode(FieldType, tokens));
                }
                @panic("bad variant token");
            }
            if (std.mem.eql(u8, tokens.next(), "1")) return .{ .ok = nyDecode(@FieldType(T, "ok"), tokens) };
            return .{ .err = nyDecode(@FieldType(T, "err"), tokens) };
        },
        else => @compileError("nyDecode does not support " ++ @typeName(T)),
    }
}

// Scripted fakes, as the C# runner's: a script is one ordered queue, or one queue per argument key.
fn NyResponse(comptime R: type) type {
    // Kind: 0 return, 1 throw, 2 await cancellation, 3 delay.
    return struct { kind: u8, value: R = undefined, message: []const u8 = "", delay: i64 = 0, inner: ?*const NyResponse(R) = null };
}

fn NyScript(comptime R: type) type {
    return struct { keyed: bool, keys: []const []const u8, queues: []const []const NyResponse(R), next: []usize };
}

fn nyReadResponse(comptime R: type, tokens: *NyTokens) NyResponse(R) {
    const kind = std.fmt.parseInt(u8, tokens.next(), 10) catch @panic("bad response token");
    return switch (kind) {
        0, 2 => .{ .kind = kind, .value = nyDecode(R, tokens) },
        1 => .{ .kind = 1, .message = nyDecodeText(tokens.next()) },
        3 => blk: {
            const delay = std.fmt.parseInt(i64, tokens.next(), 10) catch @panic("bad delay token");
            const inner = nyAllocator().create(NyResponse(R)) catch @panic("out of memory");
            inner.* = nyReadResponse(R, tokens);
            break :blk .{ .kind = 3, .delay = delay, .inner = inner };
        },
        else => @panic("bad response token"),
    };
}

/// Scripted responses are static values: the script keeps them for the whole run.
fn nyReadScript(comptime R: type, tokens: *NyTokens) NyScript(R) {
    ny_static_values = true;
    defer ny_static_values = false;
    const keyed = std.mem.eql(u8, tokens.next(), "1");
    const count = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad script token");
    const keys = nyAlloc([]const u8, count);
    const queues = nyAlloc([]const NyResponse(R), count);
    const next = nyAlloc(usize, count);
    for (0..count) |index| {
        next[index] = 0;
        // A key token is "k" and the argument tokens joined by commas.
        if (keyed) keys[index] = tokens.next()[1..];
        const responses = nyAlloc(NyResponse(R), std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad script token"));
        for (responses) |*response| response.* = nyReadResponse(R, tokens);
        queues[index] = responses;
    }
    return .{ .keyed = keyed, .keys = keys, .queues = queues, .next = next };
}

var ny_out: *std.Io.Writer = undefined;
var ny_calls: std.Io.Writer.Allocating = undefined;
var ny_call_count: usize = 0;

fn nyRecordCall(site: usize, arguments: anytype) void {
    nyRecordCallWrite(site, arguments) catch @panic("out of memory");
}

fn nyRecordCallWrite(site: usize, arguments: anytype) !void {
    if (ny_call_count >= 1024) {
        try ny_out.writeAll("[\"limit\",\"The program made more than 1024 capability calls.\"]\n");
        try ny_out.flush();
        std.process.exit(0);
    }
    const w = &ny_calls.writer;
    if (ny_call_count > 0) try w.writeAll(",");
    ny_call_count += 1;
    try w.print("[{d},[", .{site});
    inline for (@typeInfo(@TypeOf(arguments)).@"struct".field_names, 0..) |name, position| {
        if (position > 0) try w.writeAll(",");
        try nyEncode(w, @field(arguments, name));
    }
    try w.writeAll("]]");
}

fn nyToken(w: *std.Io.Writer, first: *bool, comptime format: []const u8, arguments: anytype) !void {
    if (!first.*) try w.writeAll(",");
    first.* = false;
    try w.print(format, arguments);
}

fn nyTextToken(text: []const u8) []const u8 {
    var buffer: std.Io.Writer.Allocating = .init(nyAllocator());
    nyEncodeText(&buffer.writer, text) catch @panic("out of memory");
    const quoted = buffer.written();
    return quoted[1 .. quoted.len - 1];
}

/// Writes a value's input tokens, comma separated, exactly as the authoring session writes an
/// argument, so a call's key compares equal to a keyed case's.
fn nyTokens(w: *std.Io.Writer, first: *bool, value: anytype) std.Io.Writer.Error!void {
    const T = @TypeOf(value);
    switch (@typeInfo(T)) {
        .int => try nyToken(w, first, "{d}", .{value}),
        .bool => try nyToken(w, first, "{s}", .{if (value) "1" else "0"}),
        .float => try nyToken(w, first, "{x:0>16}", .{@as(u64, @bitCast(value))}),
        .optional => {
            if (value) |item| {
                try nyToken(w, first, "1", .{});
                try nyTokens(w, first, item);
            } else try nyToken(w, first, "0", .{});
        },
        .pointer => |info| {
            if (info.size == .slice and info.child == u8) return nyToken(w, first, "{s}", .{nyTextToken(value)});
            if (info.size == .slice) {
                try nyToken(w, first, "{d}", .{value.len});
                for (value) |item| try nyTokens(w, first, item);
                return;
            }
            try nyTokens(w, first, value.*);
        },
        .@"struct" => |info| {
            if (T == NyUnit) return nyToken(w, first, "u", .{});
            if (comptime @hasDecl(T, "ny_bytes")) {
                const text = nyBytesToLatin1(value);
                defer nyRelease(text);
                return nyToken(w, first, "{s}", .{nyTextToken(text)});
            }
            if (comptime @hasDecl(T, "ny_set")) {
                try nyToken(w, first, "{d}", .{value.items.len});
                for (value.items) |item| try nyTokens(w, first, item);
                return;
            }
            if (comptime @hasDecl(T, "ny_map")) {
                try nyToken(w, first, "{d}", .{value.keys.len});
                for (value.keys, value.values) |key, item| {
                    try nyTokens(w, first, key);
                    try nyTokens(w, first, item);
                }
                return;
            }
            // A Product, or a Union variant's fields.
            inline for (info.field_names) |name| try nyTokens(w, first, @field(value, name));
        },
        .@"union" => |info| {
            if (comptime @hasDecl(T, "ny_ids")) {
                const tag = std.meta.activeTag(value);
                inline for (info.field_names, 0..) |name, position| {
                    if (tag == @field(info.tag_type.?, name)) {
                        try nyToken(w, first, "{d}", .{position});
                        try nyTokens(w, first, @field(value, name));
                    }
                }
                return;
            }
            switch (value) {
                .ok => |item| {
                    try nyToken(w, first, "1", .{});
                    try nyTokens(w, first, item);
                },
                .err => |item| {
                    try nyToken(w, first, "0", .{});
                    try nyTokens(w, first, item);
                },
            }
        },
        else => @compileError("nyTokens does not support " ++ @typeName(T)),
    }
}


/// Ends the run with the envelope of a failure no ExceptionCatch handled.
fn nyUncaught() noreturn {
    nyUncaughtWrite() catch {};
    nyLeakCheck();
    std.process.exit(0);
}

// The conformance runner (ReleaseSafe, as runPure builds it) checks that every value was released;
// any other build frees through the general-purpose allocator without checking.
var ny_debug_allocator: std.heap.DebugAllocator(.{ .stack_trace_frames = 0, .backing_allocator_zeroes = false }) = .{ .backing_allocator = .{ .ptr = undefined, .vtable = &ny_page_cache } };

// The checked allocator returns a bucket page as soon as its last slot is freed, so a loop that
// allocates and frees one value would map and fault in a fresh page every time; keep a few.
var ny_cached_pages: [64][*]u8 = undefined;
var ny_cached_count: usize = 0;
const ny_page_cache: std.mem.Allocator.VTable = .{
    .alloc = struct {
        fn alloc(_: *anyopaque, len: usize, alignment: std.mem.Alignment, ret: usize) ?[*]u8 {
            if (len == ny_page_bytes and ny_cached_count > 0 and alignment.toByteUnits() <= ny_page_bytes) {
                ny_cached_count -= 1;
                return ny_cached_pages[ny_cached_count];
            }
            return std.heap.page_allocator.rawAlloc(len, if (len == ny_page_bytes) .fromByteUnits(ny_page_bytes) else alignment, ret);
        }
    }.alloc,
    .resize = std.mem.Allocator.noResize,
    .remap = std.mem.Allocator.noRemap,
    .free = struct {
        fn free(_: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize) void {
            if (memory.len == ny_page_bytes and ny_cached_count < ny_cached_pages.len) {
                ny_cached_pages[ny_cached_count] = memory.ptr;
                ny_cached_count += 1;
            } else std.heap.page_allocator.rawFree(memory, if (memory.len == ny_page_bytes) .fromByteUnits(ny_page_bytes) else alignment, ret);
        }
    }.free,
};
const ny_page_bytes = (std.heap.DebugAllocatorConfig{}).page_size;

fn nyUseValueAllocator() void {
    ny_values = if (@import("builtin").mode == .safe) ny_debug_allocator.allocator() else std.heap.smp_allocator;
}

/// Exit code 86 with "leak: ..." on stderr when a value is still live at the end of the run.
fn nyLeakCheck() void {
    if (@import("builtin").mode != .safe) return;
    const live = ny_debug_allocator.detectLeaks();
    if (live == 0) return;
    ny_out.flush() catch {};
    std.debug.print("leak: {d} values are still allocated after the run.\n", .{live});
    std.process.exit(86);
}

fn nyUncaughtWrite() !void {
    try ny_out.print("[\"exception\",\"{s}\",", .{ny_failure.type_name});
    try nyEncodeText(ny_out, ny_failure.message);
    try ny_out.print(",[],[{s}],{},\"{s}\"]\n", .{ ny_calls.written(), ny_failure.exhausted, if (ny_failure.cancelled) "cancelled" else "fault" });
    try ny_out.flush();
}

/// An entry's result, or the end of the run when the entry failed.
fn nyValue(value: anytype) NyPayload(@TypeOf(value)) {
    return switch (@typeInfo(@TypeOf(value))) {
        .error_union => value catch nyUncaught(),
        else => value,
    };
}

fn nyRespond(comptime R: type, script: *NyScript(R), arguments: anytype) NyError!R {
    var selected: usize = 0;
    if (script.keyed) {
        var key: std.Io.Writer.Allocating = .init(nyAllocator());
        var first = true;
        inline for (@typeInfo(@TypeOf(arguments)).@"struct".field_names) |name| nyTokens(&key.writer, &first, @field(arguments, name)) catch @panic("out of memory");
        selected = for (script.keys, 0..) |candidate, index| {
            if (std.mem.eql(u8, candidate, key.written())) break index;
        } else return nyFail("FakeArgumentsUnmatchedException", "No fake case matches the complete call arguments.", false);
    }
    const position = script.next[selected];
    if (position >= script.queues[selected].len) return nyFail("FakeResponsesExhaustedException", "The fake operation has no scripted response left for this call.", true);
    script.next[selected] += 1;
    var response: *const NyResponse(R) = &script.queues[selected][position];
    while (true) {
        switch (response.kind) {
            0 => return response.value,
            1 => return nyFail("FakeCapabilityException", response.message, false),
            2 => {
                try nyAwaitCancellation();
                return response.value;
            },
            else => {
                // A delay is not interrupted by cancellation; then the inner response applies.
                _ = try nyBlock(nyNever, undefined, response.delay, true);
                response = response.inner.?;
            },
        }
    }
}

fn nyEncode(out: *std.Io.Writer, value: anytype) std.Io.Writer.Error!void {
    const T = @TypeOf(value);
    switch (@typeInfo(T)) {
        .int => try out.print("{d}", .{value}),
        .float => try out.print("\"{x:0>16}\"", .{if (std.math.isNan(value)) @as(u64, 0x7ff8000000000000) else @as(u64, @bitCast(value))}),
        .bool => try out.writeAll(if (value) "true" else "false"),
        .optional => {
            if (value) |item| {
                try out.writeAll("[");
                try nyEncode(out, item);
                try out.writeAll("]");
            } else try out.writeAll("[]");
        },
        .pointer => |info| {
            if (info.size == .slice and info.child == u8) return nyEncodeText(out, value);
            if (info.size == .slice) {
                try out.writeAll("[");
                for (value, 0..) |item, position| {
                    if (position > 0) try out.writeAll(",");
                    try nyEncode(out, item);
                }
                return out.writeAll("]");
            }
            return nyEncode(out, value.*);
        },
        .@"struct" => |info| {
            if (T == NyUnit) return out.writeAll("\"unit\"");
            if (comptime @hasDecl(T, "ny_set")) return nyEncode(out, value.items);
            if (comptime @hasDecl(T, "ny_bytes")) {
                const text = nyBytesToLatin1(value);
                defer nyRelease(text);
                return nyEncodeText(out, text);
            }
            if (comptime @hasDecl(T, "ny_map")) {
                try out.writeAll("[");
                for (value.keys, value.values, 0..) |key, item, position| {
                    if (position > 0) try out.writeAll(",");
                    try out.writeAll("[");
                    try nyEncode(out, key);
                    try out.writeAll(",");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                }
                return out.writeAll("]");
            }
            try out.writeAll("[");
            inline for (info.field_names, 0..) |name, position| {
                if (position > 0) try out.writeAll(",");
                try nyEncode(out, @field(value, name));
            }
            try out.writeAll("]");
        },
        .@"union" => |info| {
            if (@hasDecl(T, "ny_ids")) {
                const tag = std.meta.activeTag(value);
                inline for (info.field_names, 0..) |name, position| {
                    if (tag == @field(info.tag_type.?, name)) {
                        try out.print("[\"{s}\",", .{T.ny_ids[position]});
                        try nyEncode(out, @field(value, name));
                        try out.writeAll("]");
                    }
                }
                return;
            }
            switch (value) {
                .ok => |item| {
                    try out.writeAll("[true,");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                },
                .err => |item| {
                    try out.writeAll("[false,");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                },
            }
        },
        else => @compileError("nyEncode does not support " ++ @typeName(T)),
    }
}
