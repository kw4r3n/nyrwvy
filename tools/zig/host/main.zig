//! The Zig session host (docs/decisions/zig-session-host.md): the F# host's JSONL protocol, one
//! response line per request line, and its startup. So far: `open` of an unresolved `.json` or
//! `.nyrec` snapshot (read-only or under its lock), the request envelope and its errors as
//! `AuthoringProtocol.executeLine` gives them, and `status`; every other operation is
//! `UnsupportedOperation`. `ZigHostParityTests` compares it with the F# host. The snapshot is read
//! by the model and snapshot readers, imported as modules (`ZigTools` builds them together).
const std = @import("std");
const model_reader = @import("model_reader");
const snapshot_reader = @import("snapshot_reader");

/// The open snapshot: what `status` reports.
var snapshot_id: []const u8 = "";
var fingerprints: model_reader.Fingerprints = undefined;

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
    if (std.mem.eql(u8, op, "status")) {
        if (try fieldProblem(a, line, parsed.object, "status request", &.{ "id", "op" })) |message| return failure(out, id, "ProtocolError", message);
        try out.writeAll("{\"id\":");
        try writeString(out, id);
        try out.writeAll(",\"ok\":true,\"result\":{\"snapshotId\":");
        try writeString(out, snapshot_id);
        try out.writeAll(",\"snapshotFingerprint\":");
        try writeString(out, fingerprints.snapshot);
        try out.writeAll(",\"semanticFingerprint\":");
        try writeString(out, fingerprints.semantic);
        // `build` names each host's own build; the parity test masks it.
        return out.writeAll(",\"pendingProposalCount\":0,\"build\":\"nyrwvy-zig-host\"}}");
    }
    return failure(out, id, "UnsupportedOperation", try std.fmt.allocPrint(a, "Unsupported operation '{s}'.", .{op}));
}

/// `WireInput.ensureFields` with every allowed field required: duplicate root names first, then
/// the unknown and the missing ones, each sorted.
fn fieldProblem(a: std.mem.Allocator, line: []const u8, root: std.json.ObjectMap, context: []const u8, fields: []const []const u8) !?[]const u8 {
    if (try duplicateRootNames(a, line)) return "JSON object contains duplicate property names.";
    var unknown: std.ArrayList([]const u8) = .empty;
    for (root.keys()) |key| {
        for (fields) |field| {
            if (std.mem.eql(u8, key, field)) break;
        } else try unknown.append(a, key);
    }
    var missing: std.ArrayList([]const u8) = .empty;
    for (fields) |field| if (root.get(field) == null) try missing.append(a, field);
    const ordinal = struct {
        fn less(_: void, x: []const u8, y: []const u8) bool {
            return std.mem.order(u8, x, y) == .lt;
        }
    }.less;
    std.mem.sort([]const u8, unknown.items, {}, ordinal);
    std.mem.sort([]const u8, missing.items, {}, ordinal);
    if (unknown.items.len > 0) return try std.fmt.allocPrint(a, "{s} contains unsupported properties: {s}. Allowed: {s}.", .{ context, try std.mem.join(a, ", ", unknown.items), try std.mem.join(a, ", ", fields) });
    if (missing.items.len > 0) return try std.fmt.allocPrint(a, "{s} is missing required properties: {s}.", .{ context, try std.mem.join(a, ", ", missing.items) });
    return null;
}

/// Whether the request object names a property twice (the parse keeps the last).
fn duplicateRootNames(a: std.mem.Allocator, line: []const u8) !bool {
    var scanner = std.json.Scanner.initCompleteInput(a, line);
    var names: std.StringHashMapUnmanaged(void) = .empty;
    var depth: usize = 0;
    var expecting_name = false;
    while (true) {
        const token = try scanner.nextAlloc(a, .alloc_always);
        switch (token) {
            .end_of_document => return false,
            .object_begin => {
                depth += 1;
                expecting_name = depth == 1;
                continue;
            },
            .array_begin => depth += 1,
            .object_end, .array_end => depth -= 1,
            .allocated_string => |name| if (depth == 1 and expecting_name) {
                if ((try names.fetchPut(a, name, {})) != null) return true;
                expecting_name = false;
                continue;
            },
            else => {},
        }
        expecting_name = depth == 1;
    }
}

/// The startup failure as `WireEncode.startupMessage` words it; the process exits with 2.
fn startupFailure(comptime format: []const u8, args: anytype) noreturn {
    std.debug.print(format ++ "\n", args);
    std.process.exit(2);
}

/// The readers' protocol lines (`ZigSnapshotReader.verdict`) as the codec error they stand for,
/// worded as `WireEncode.describeCodecError`; null when the snapshot is accepted.
fn codecProblem(a: std.mem.Allocator, record: ?[]const u8, lines_text: []const u8) !?[]const u8 {
    const depth = "The graph would nest deeper than the codec's {s} JSON levels (MaximumDepthExceeded).";
    if (record) |line| if (std.mem.startsWith(u8, line, "error ")) {
        const body = line[6..];
        if (std.mem.startsWith(u8, body, "MaximumDepthExceeded: ")) return try std.fmt.allocPrint(a, depth, .{body[22..]});
        return try shapeAt(a, body);
    };
    var lines = std.mem.tokenizeScalar(u8, lines_text, '\n');
    var first_issue: ?[]const u8 = null;
    var issue_count: usize = 0;
    while (lines.next()) |line| {
        if (std.mem.startsWith(u8, line, "skip legacy schema ")) return try std.fmt.allocPrint(a, "Snapshot schema version {s} is a legacy schema, which the Zig host does not read. Migrate it with the last F# host (migrate --snapshot <path>).", .{line[19..]});
        if (std.mem.startsWith(u8, line, "error ")) {
            const body = line[6..];
            const colon = std.mem.indexOf(u8, body, ": ") orelse return body;
            const message = try unescape(a, body[colon + 2 ..]);
            const case = body[0..colon];
            if (std.mem.eql(u8, case, "InvalidJson")) return try std.fmt.allocPrint(a, "The snapshot is not valid JSON: {s}", .{message});
            if (std.mem.eql(u8, case, "InvalidShape")) return try std.fmt.allocPrint(a, "The snapshot does not have the expected shape: {s}", .{message});
            if (std.mem.eql(u8, case, "UnsupportedSchemaVersion")) return try std.fmt.allocPrint(a, "Snapshot schema version {s} is not supported.", .{message});
            if (std.mem.eql(u8, case, "MaximumDepthExceeded")) return try std.fmt.allocPrint(a, depth, .{message});
            return body;
        }
        if (std.mem.startsWith(u8, line, "issue ")) {
            if (first_issue == null) first_issue = try unescape(a, line[6..]);
            issue_count += 1;
        }
    }
    if (first_issue) |issue| {
        if (issue_count == 1) return try std.fmt.allocPrint(a, "The snapshot is invalid: {s}", .{issue});
        return try std.fmt.allocPrint(a, "The snapshot is invalid: {s} ({d} issues; see details)", .{ issue, issue_count });
    }
    // The deferred placement error stands once the model is valid.
    if (record) |line| if (std.mem.startsWith(u8, line, "deferred ")) return try shapeAt(a, line[9..]);
    return null;
}

/// `<line>:<column>: <message>` as `SessionStorage.recordFailure` words a record error.
fn shapeAt(a: std.mem.Allocator, body: []const u8) ![]const u8 {
    const first = std.mem.indexOfScalar(u8, body, ':') orelse return body;
    const second = std.mem.indexOfPos(u8, body, first + 1, ": ") orelse return body;
    return std.fmt.allocPrint(a, "The snapshot does not have the expected shape: line {s}, column {s}: {s}", .{ body[0..first], body[first + 1 .. second], body[second + 2 ..] });
}

/// The model reader writes a backslash as `\\` and a line break as `\n` (and `\r`).
fn unescape(a: std.mem.Allocator, text: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    var i: usize = 0;
    while (i < text.len) : (i += 1) {
        if (text[i] == '\\' and i + 1 < text.len and std.mem.indexOfScalar(u8, "\\nr", text[i + 1]) != null) {
            i += 1;
            try out.append(a, switch (text[i]) {
                'n' => '\n',
                'r' => '\r',
                else => '\\',
            });
        } else try out.append(a, text[i]);
    }
    return out.items;
}

/// `^\.<name>(\.dependency\.json(\.previous)?)?\.[0-9a-f]{32}\.tmp$` (`SessionStorage.sweepTemporaries`).
fn isTemporaryOf(name: []const u8, file: []const u8) bool {
    if (file.len < name.len + 1 + 37 or file[0] != '.' or !std.mem.startsWith(u8, file[1..], name)) return false;
    var rest = file[1 + name.len ..];
    for ([_][]const u8{ ".dependency.json.previous", ".dependency.json" }) |suffix| if (std.mem.startsWith(u8, rest, suffix) and rest.len == suffix.len + 37) {
        rest = rest[suffix.len..];
        break;
    };
    if (rest.len != 37 or rest[0] != '.' or !std.mem.endsWith(u8, rest, ".tmp")) return false;
    for (rest[1..33]) |c| if (!std.ascii.isDigit(c) and !(c >= 'a' and c <= 'f')) return false;
    return true;
}

/// `AuthoringSession.openUsing` for an unresolved source: the file, the lock and the sweep (unless
/// read-only), the dependency marker, then the readers' verdict. The lock lives until the process ends.
fn open(io: std.Io, a: std.mem.Allocator, given: []const u8, read_only: bool) !void {
    const path = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), given });
    const stat = std.Io.Dir.cwd().statFile(io, path, .{}) catch null;
    if (stat == null or stat.?.kind != .file) startupFailure("SnapshotFileNotFound: No snapshot file at {s}. Use create (MCP nyrwvy_open mode \"create\") to start a new one.", .{path});
    if (!read_only) {
        var link: [std.fs.max_path_bytes]u8 = undefined;
        if (std.Io.Dir.cwd().readLink(io, path, &link)) |_| {
            startupFailure("IoFailure: The snapshot path {s} is a symbolic link; open the file it points to instead.", .{path});
        } else |_| {}
        _ = std.Io.Dir.cwd().createFile(io, try std.mem.concat(a, u8, &.{ path, ".lock" }), .{ .read = true, .truncate = false, .lock = .exclusive, .lock_nonblocking = true }) catch |e| switch (e) {
            error.WouldBlock => startupFailure("LockUnavailable: {s} is open in another authoring session. Close that session first.", .{path}),
            else => startupFailure("IoFailure: {s}", .{@errorName(e)}),
        };
        const directory_path = std.fs.path.dirname(path) orelse "/";
        if (std.Io.Dir.cwd().openDir(io, directory_path, .{ .iterate = true })) |directory| {
            var entries = directory.iterate();
            while (entries.next(io) catch null) |entry| {
                // `Directory.EnumerateFiles` also lists links to files and entries of unknown type.
                if (entry.kind != .directory and isTemporaryOf(std.fs.path.basename(path), entry.name)) directory.deleteFile(io, entry.name) catch {};
            }
        } else |_| {}
    }
    const bytes = std.Io.Dir.cwd().readFileAlloc(io, path, a, .unlimited) catch |e| startupFailure("IoFailure: {s}", .{@errorName(e)});
    // `File.ReadAllText`: a UTF-8 byte order mark is dropped, ill-formed bytes become U+FFFD.
    const unmarked = if (std.mem.startsWith(u8, bytes, "\xEF\xBB\xBF")) bytes[3..] else bytes;
    const source: []const u8 = if (std.unicode.utf8ValidateSlice(unmarked)) unmarked else try std.fmt.allocPrint(a, "{f}", .{std.unicode.fmtUtf8(unmarked)});
    const record = std.mem.endsWith(u8, path, ".nyrec");
    const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
    if (std.Io.Dir.cwd().statFile(io, sidecar, .{})) |_| {
        // shortcut: resolved snapshots are step 1c; until then the Zig host refuses them.
        startupFailure("DependencyFailure: The Zig host does not read dependency sidecars yet.", .{});
    } else |_| {}
    const bound = if (record)
        std.mem.indexOf(u8, source[0 .. std.mem.indexOfScalar(u8, source, '\n') orelse source.len], " dependency=") != null
    else if (std.json.parseFromSliceLeaky(std.json.Value, a, source, .{ .duplicate_field_behavior = .use_last })) |root| root == .object and root.object.get("dependency") != null else |_| false;
    if (bound) startupFailure("DependencyFailure: The dependency sidecar '{s}.dependency.json' is missing. Restore the exact sidecar that belongs to this snapshot; pinning a new version cannot recover a lost selection.", .{path});

    var record_line: ?[]const u8 = null;
    var json: []const u8 = source;
    if (record) {
        const read = snapshot_reader.readRecords(a, source);
        record_line = read.line;
        json = read.json orelse "";
    }
    const read = if (record_line != null and std.mem.startsWith(u8, record_line.?, "error ")) model_reader.DocumentRead{ .lines = "", .fingerprints = null } else model_reader.readDocument(a, json);
    if (try codecProblem(a, record_line, read.lines)) |message| startupFailure("SnapshotCodecFailure: {s}", .{message});
    fingerprints = read.fingerprints orelse startupFailure("SnapshotCodecFailure: The snapshot could not be fingerprinted.", .{});
    const root = try std.json.parseFromSliceLeaky(std.json.Value, a, json, .{ .duplicate_field_behavior = .use_last });
    snapshot_id = root.object.get("snapshotId").?.string;
}

pub fn main(init: std.process.Init) !void {
    const startup = init.arena.allocator();
    const all = try init.minimal.args.toSlice(startup);
    // `CommandLine.parse`: `--allow-host-sources` anywhere, then exactly one of the open forms.
    var args: std.ArrayList([]const u8) = .empty;
    for (all[1..]) |arg| if (!std.mem.eql(u8, arg, "--allow-host-sources")) try args.append(startup, arg);
    const forms = args.items.len >= 3 and std.mem.eql(u8, args.items[0], "open") and std.mem.eql(u8, args.items[1], "--snapshot");
    const read_only = forms and args.items.len == 4 and std.mem.eql(u8, args.items[3], "--read-only");
    if (!forms or !(args.items.len == 3 or read_only)) {
        std.debug.print("{s}", .{usage});
        std.process.exit(2);
    }
    try open(init.io, startup, args.items[2], read_only);

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
