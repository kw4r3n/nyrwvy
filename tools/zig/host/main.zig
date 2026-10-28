//! The Zig session host (docs/decisions/zig-session-host.md): the F# host's JSONL protocol, one
//! response line per request line. Step 0 is the skeleton: the request envelope and its errors
//! as `AuthoringProtocol.executeLine` gives them, and no operation yet (each is
//! `UnsupportedOperation`). `ZigHostParityTests` compares it with the F# host.
const std = @import("std");

// `WireInput.requestMaxJsonDepth`: 3 + 3 * 256 + 64.
const max_depth = 835;

const usage = "Usage: host open --snapshot <snapshot-path> [--read-only] [--allow-host-sources]\n";

/// A JSON string as `JsonSerializer` writes it with `JavaScriptEncoder.Default`: printable ASCII
/// except `"&'+<>` and backtick, the short forms \b \t \n \f \r \\, and every other code point as
/// uppercase UTF-16 \uXXXX escapes.
fn writeString(out: *std.Io.Writer, text: []const u8) !void {
    try out.writeByte('"');
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.nextCodepoint()) |c| switch (c) {
        8 => try out.writeAll("\\b"),
        9 => try out.writeAll("\\t"),
        10 => try out.writeAll("\\n"),
        12 => try out.writeAll("\\f"),
        13 => try out.writeAll("\\r"),
        '\\' => try out.writeAll("\\\\"),
        else => if (c >= 0x20 and c < 0x7f and std.mem.indexOfScalar(u8, "\"&'+<>`", @intCast(c)) == null)
            try out.writeByte(@intCast(c))
        else if (c < 0x10000)
            try out.print("\\u{X:0>4}", .{c})
        else {
            const v = c - 0x10000;
            try out.print("\\u{X:0>4}\\u{X:0>4}", .{ 0xD800 + (v >> 10), 0xDC00 + (v & 0x3ff) });
        },
    };
    try out.writeByte('"');
}

fn failure(out: *std.Io.Writer, id: ?[]const u8, code: []const u8, message: []const u8) !void {
    try out.writeAll("{\"id\":");
    if (id) |text| try writeString(out, text) else try out.writeAll("null");
    try out.writeAll(",\"ok\":false,\"error\":{\"code\":");
    try writeString(out, code);
    try out.writeAll(",\"message\":");
    try writeString(out, message);
    try out.writeAll("}}");
}

/// Whether `line` nests deeper than `max_depth`, counting brackets outside strings.
fn tooDeep(line: []const u8) bool {
    var depth: usize = 0;
    var in_string = false;
    var i: usize = 0;
    while (i < line.len) : (i += 1) {
        const c = line[i];
        if (in_string) {
            if (c == '\\') i += 1 else if (c == '"') in_string = false;
        } else switch (c) {
            '"' => in_string = true,
            '{', '[' => {
                depth += 1;
                if (depth > max_depth) return true;
            },
            '}', ']' => depth -|= 1,
            else => {},
        }
    }
    return false;
}

const Problem = error{Protocol};

/// `WireInput.requiredString`; the message is left in `problem`.
fn requiredString(a: std.mem.Allocator, root: std.json.ObjectMap, name: []const u8, problem: *[]const u8) ![]const u8 {
    const value = root.get(name) orelse {
        problem.* = try std.fmt.allocPrint(a, "Missing required property '{s}'.", .{name});
        return Problem.Protocol;
    };
    if (value != .string) {
        problem.* = try std.fmt.allocPrint(a, "Property '{s}' must be a JSON string.", .{name});
        return Problem.Protocol;
    }
    return value.string;
}

/// One request line to its response line, as `AuthoringProtocol.executeLine` answers it.
fn execute(a: std.mem.Allocator, out: *std.Io.Writer, line: []const u8) !void {
    if (std.mem.trim(u8, line, " \t\r\n").len == 0) return failure(out, null, "ProtocolError", "Empty request line; expected one JSON object.");
    // The F# message carries .NET's parser text after this prefix; the parity test compares the prefix.
    if (tooDeep(line)) return failure(out, null, "ProtocolError", "Invalid JSON: maximum depth exceeded.");
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, a, line, .{ .duplicate_field_behavior = .use_last }) catch |e|
        return failure(out, null, "ProtocolError", try std.fmt.allocPrint(a, "Invalid JSON: {s}", .{@errorName(e)}));
    if (parsed != .object) return failure(out, null, "ProtocolError", "Expected a JSON object.");
    var problem: []const u8 = "";
    const id = requiredString(a, parsed.object, "id", &problem) catch |e| switch (e) {
        Problem.Protocol => return failure(out, null, "ProtocolError", problem),
        else => return e,
    };
    const op = requiredString(a, parsed.object, "op", &problem) catch |e| switch (e) {
        Problem.Protocol => return failure(out, id, "ProtocolError", problem),
        else => return e,
    };
    return failure(out, id, "UnsupportedOperation", try std.fmt.allocPrint(a, "Unsupported operation '{s}'.", .{op}));
}

pub fn main(init: std.process.Init) !void {
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    var snapshot: ?[]const u8 = null;
    var valid = args.len >= 2 and std.mem.eql(u8, args[1], "open");
    var i: usize = 2;
    while (valid and i < args.len) : (i += 1) {
        if (std.mem.eql(u8, args[i], "--snapshot") and i + 1 < args.len) {
            i += 1;
            snapshot = args[i];
        } else valid = std.mem.eql(u8, args[i], "--read-only") or std.mem.eql(u8, args[i], "--allow-host-sources");
    }
    // shortcut: the snapshot is not opened yet; that is step 1.
    if (!valid or snapshot == null) {
        std.debug.print("{s}", .{usage});
        std.process.exit(2);
    }

    var in_buffer: [1 << 16]u8 = undefined;
    var reader = std.Io.File.stdin().reader(init.io, &in_buffer);
    var line: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
    var out_buffer: [1 << 16]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &out_buffer);
    const out = &writer.interface;
    while (true) {
        line.clearRetainingCapacity();
        _ = try reader.interface.streamDelimiterEnding(&line.writer, '\n');
        const ended = reader.interface.takeByte() == error.EndOfStream;
        if (ended and line.written().len == 0) break;
        // A request's allocations live until its response is written.
        var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
        defer arena.deinit();
        try execute(arena.allocator(), out, std.mem.trimEnd(u8, line.written(), "\r"));
        try out.writeByte('\n');
        try out.flush();
        if (ended) break;
    }
}
