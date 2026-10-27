namespace Nyrwvy.Core

/// Zig source the backend emits with every program, and the support a runPure runner adds.
[<RequireQualifiedAccess>]
module ZigRuntime =
    let runtime =
        use stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream "runtime.zig"
        use reader = new System.IO.StreamReader(stream)
        reader.ReadToEnd()

    /// Zig support for a runPure runner: reads argument tokens in the order the authoring session
    /// writes them and writes a result in the session's envelope form.
    let runnerSupport = """
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
    return std.unicode.utf16LeToUtf8Alloc(nyAllocator(), units) catch @panic("bad String token");
}

fn nyEncodeText(out: *std.Io.Writer, text: []const u8) !void {
    const units = std.unicode.utf8ToUtf16LeAlloc(nyAllocator(), text) catch @panic("bad String");
    const bytes = std.mem.sliceAsBytes(units);
    const encoded = nyAllocator().alloc(u8, std.base64.standard.Encoder.calcSize(bytes.len)) catch @panic("out of memory");
    try out.print("\"s{s}\"", .{std.base64.standard.Encoder.encode(encoded, bytes)});
}

fn nyDecode(comptime T: type, tokens: *NyTokens) T {
    switch (@typeInfo(T)) {
        .int => return std.fmt.parseInt(i64, tokens.next(), 10) catch @panic("bad Int64 token"),
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
                const items = nyAllocator().alloc(info.child, count) catch @panic("out of memory");
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
                return nySetFromList(Item, nyDecode([]const Item, tokens));
            }
            // Bytes travel as the String token of their latin-1 form.
            if (comptime @hasDecl(T, "ny_bytes")) return nyBytesFromLatin1(nyDecodeText(tokens.next()));
            if (comptime @hasDecl(T, "ny_map")) {
                const K = @typeInfo(@FieldType(T, "keys")).pointer.child;
                const V = @typeInfo(@FieldType(T, "values")).pointer.child;
                const count = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad Map token");
                var map = T{ .keys = &.{}, .values = &.{} };
                for (0..count) |_| {
                    const key = nyDecode(K, tokens);
                    map = nyMapInsert(K, V, map, key, nyDecode(V, tokens));
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

fn nyReadScript(comptime R: type, tokens: *NyTokens) NyScript(R) {
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
fn nyTokens(w: *std.Io.Writer, first: *bool, value: anytype) !void {
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
            if (comptime @hasDecl(T, "ny_bytes")) return nyToken(w, first, "{s}", .{nyTextToken(nyBytesToLatin1(value))});
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
    std.process.exit(0);
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

fn nyEncode(out: *std.Io.Writer, value: anytype) !void {
    const T = @TypeOf(value);
    switch (@typeInfo(T)) {
        .int => try out.print("{d}", .{value}),
        .float => try out.print("\"{x:0>16}\"", .{@as(u64, @bitCast(value))}),
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
            if (comptime @hasDecl(T, "ny_bytes")) return nyEncodeText(out, nyBytesToLatin1(value));
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
"""

    /// The simple (one-to-one) case mapping the C# backend's NyText.ToLower/ToUpper apply: .NET's
    /// invariant mapping per code point, except U+0130 -> i, U+0131 -> I and U+00DF -> U+1E9E.
    let private mapCase (lower: bool) (code: int) =
        if code < 0x10000 then
            let unit = char code
            if lower then (if code = 0x130 then 0x69 else int (System.Char.ToLowerInvariant unit))
            elif code = 0x131 then 0x49
            elif code = 0xDF then 0x1E9E
            else int (System.Char.ToUpperInvariant unit)
        else
            let rune = System.Text.Rune code
            (if lower then System.Text.Rune.ToLowerInvariant rune else System.Text.Rune.ToUpperInvariant rune).Value

    /// Runs of mapped code points { first, last, step, delta }: every step-th code point from first
    /// to last maps to itself plus delta. Runs take consecutive mapped code points, so a step-2 run
    /// skips only unmapped ones, runs never overlap and a binary search over `first` finds the only
    /// candidate.
    let private caseRuns (lower: bool) =
        let mapped =
            [| for code in 0 .. 0x10FFFF do
                 if code < 0xD800 || code > 0xDFFF then
                     let target = mapCase lower code
                     if target <> code then code, target - code |]
        let runs = ResizeArray<string>()
        let mutable index = 0
        while index < mapped.Length do
            let first, delta = mapped[index]
            let mutable last = first
            let mutable step = 1
            if index + 1 < mapped.Length then
                let next, nextDelta = mapped[index + 1]
                if nextDelta = delta && next - first = 2 then step <- 2
            while index + 1 < mapped.Length
                  && fst mapped[index + 1] = last + step
                  && snd mapped[index + 1] = delta do
                index <- index + 1
                last <- last + step
            index <- index + 1
            runs.Add $".{{ {first}, {last}, {step}, {delta} }}"
        System.String.Join(", ", runs)

    /// Zig case mapping, emitted only for a program that maps case.
    let caseMapping =
        lazy
            ("const ny_lower_runs = [_][4]i32{ " + caseRuns true + " };\n"
             + "const ny_upper_runs = [_][4]i32{ " + caseRuns false + " };\n"
             + """
fn nyMapCase(text: []const u8, runs: []const [4]i32) []const u8 {
    var out: std.ArrayList(u8) = .empty;
    var view = std.unicode.Utf8View.initUnchecked(text);
    var points = view.iterator();
    while (points.nextCodepoint()) |point| {
        const code: i32 = point;
        var low: usize = 0;
        var high: usize = runs.len;
        while (low < high) {
            const middle = (low + high) / 2;
            if (runs[middle][0] <= code) low = middle + 1 else high = middle;
        }
        var mapped = code;
        if (low > 0) {
            const run = runs[low - 1];
            if (code <= run[1] and @mod(code - run[0], run[2]) == 0) mapped = code + run[3];
        }
        var buffer: [4]u8 = undefined;
        const size = std.unicode.utf8Encode(@intCast(mapped), &buffer) catch unreachable;
        out.appendSlice(nyAllocator(), buffer[0..size]) catch nyOom();
    }
    return out.items;
}
""")
