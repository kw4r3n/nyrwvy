//! The Zig session host (docs/decisions/zig-session-host.md): the F# host's JSONL protocol, one
//! response line per request line, and its startup. So far: `open` of a `.json` or `.nyrec`
//! snapshot, unresolved or with its dependency sidecar (read-only or under its lock), the request envelope and its errors as
//! `AuthoringProtocol.executeLine` gives them, and `status`; every other operation is
//! `UnsupportedOperation`. `ZigHostParityTests` compares it with the F# host. The snapshot is read
//! by the model and snapshot readers, imported as modules (`ZigTools` builds them together).
const std = @import("std");
const model_reader = @import("model_reader");
const snapshot_reader = @import("snapshot_reader");
const wire = @import("wire.zig");
const query = @import("query.zig");
const ids = @import("ids.zig");

/// The open snapshot: what `status` reports, the model (merged with the dependency, if any) and
/// the selected dependency. All live in `startup`.
var snapshot_id: []const u8 = "";
var snapshot_path: []const u8 = "";
var fingerprints: model_reader.Fingerprints = undefined;
var model_root: model_reader.Value = .json_null;
var canonical_model: ?model_reader.Value = null;
var selection: ?Dependency = null;
var startup: std.mem.Allocator = undefined;

/// The model in canonical order, built on first use.
fn canonical() model_reader.Value {
    if (canonical_model == null) canonical_model = model_reader.canonicalRoot(startup, model_root);
    return canonical_model.?;
}

// `WireInput.requestMaxJsonDepth`: 3 + 3 * 256 + 64.
const max_depth = 835;

const usage = "Usage: host open --snapshot <snapshot-path> [--read-only] [--allow-host-sources]\n";

/// A JSON string as `JsonSerializer` writes it with `JavaScriptEncoder.Default`: printable ASCII
/// except `"&'+<>` and backtick, the short forms \b \t \n \f \r \\, and every other code point as
/// uppercase UTF-16 \uXXXX escapes.
pub fn writeString(out: *std.Io.Writer, text: []const u8) !void {
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
    return failureWith(out, id, code, message, "");
}

/// `details`: the error's `details` object as JSON text, or empty for none.
fn failureWith(out: *std.Io.Writer, id: ?[]const u8, code: []const u8, message: []const u8, details: []const u8) !void {
    try out.writeAll("{\"id\":");
    if (id) |text| try writeString(out, text) else try out.writeAll("null");
    try out.writeAll(",\"ok\":false,\"error\":{\"code\":");
    try writeString(out, code);
    try out.writeAll(",\"message\":");
    try writeString(out, message);
    if (details.len > 0) {
        try out.writeAll(",\"details\":");
        try out.writeAll(details);
    }
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
/// `std.json` refuses a `\uD800`-style escape of a lone surrogate, which .NET parses and refuses only
/// when the string is read. Each such escape becomes `\ufdd0`, of the same length, and the readers
/// answer as F# does (`query.readable`, `query.lookable`); `line` is null when the line has none.
/// `root_key`: a property name of the request object has one, so reading `id` throws.
/// shortcut: a line that also spells U+FDD0 itself keeps the parse failure; mark by path if that matters.
const Masked = struct { line: ?[]u8 = null, root_key: bool = false };

fn maskLoneSurrogates(a: std.mem.Allocator, line: []const u8) !Masked {
    if (std.mem.indexOf(u8, line, "\\u") == null) return .{};
    var masked: Masked = .{};
    var genuine = std.mem.indexOf(u8, line, query.lone_mark) != null;
    var in_string = false;
    var spoiled = false;
    var depth: usize = 0;
    var i: usize = 0;
    while (i < line.len) : (i += 1) {
        const c = line[i];
        if (!in_string) {
            in_string = c == '"';
            spoiled = false;
            if (c == '{' or c == '[') depth += 1;
            if (c == '}' or c == ']') depth -|= 1;
        } else if (c == '"') {
            in_string = false;
            const rest = std.mem.trimStart(u8, line[i + 1 ..], " \t\r\n");
            if (spoiled and rest.len > 0 and rest[0] == ':') {
                if (depth == 1) masked.root_key = true;
            }
        } else if (c == '\\') {
            if (i + 5 >= line.len or line[i + 1] != 'u') {
                i += 1;
                continue;
            }
            const unit = std.fmt.parseInt(u16, line[i + 2 .. i + 6], 16) catch {
                i += 5;
                continue;
            };
            const low = struct {
                fn at(text: []const u8, at_: usize) bool {
                    if (at_ + 5 >= text.len or text[at_] != '\\' or text[at_ + 1] != 'u') return false;
                    const next = std.fmt.parseInt(u16, text[at_ + 2 .. at_ + 6], 16) catch return false;
                    return next >= 0xDC00 and next <= 0xDFFF;
                }
            }.at;
            if (unit >= 0xD800 and unit <= 0xDBFF and low(line, i + 6)) {
                i += 11;
                continue;
            }
            if (unit == 0xFDD0) genuine = true;
            if (unit >= 0xD800 and unit <= 0xDFFF) {
                if (masked.line == null) masked.line = try a.dupe(u8, line);
                @memcpy(masked.line.?[i .. i + 6], "\\ufdd0");
                spoiled = true;
            }
            i += 5;
        }
    }
    return if (genuine) .{} else masked;
}

fn execute(a: std.mem.Allocator, out: *std.Io.Writer, given: []const u8) !void {
    // The model reader's helpers allocate from the request's arena, not a freed earlier one.
    model_reader.useAllocator(a);
    const masked = try maskLoneSurrogates(a, given);
    query.masked = masked.line != null;
    const line = masked.line orelse given;
    if (std.mem.trim(u8, line, " \t\r\n").len == 0) return failure(out, null, "ProtocolError", "Empty request line; expected one JSON object.");
    // The F# message carries .NET's parser text after this prefix; the parity test compares the prefix.
    if (tooDeep(line)) return failure(out, null, "ProtocolError", "Invalid JSON: maximum depth exceeded.");
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, a, line, .{ .duplicate_field_behavior = .use_last }) catch |e|
        return failure(out, null, "ProtocolError", try std.fmt.allocPrint(a, "Invalid JSON: {s}", .{@errorName(e)}));
    if (parsed != .object) return failure(out, null, "ProtocolError", "Expected a JSON object.");
    if (masked.root_key) return failure(out, null, "SessionError", query.lone_message);
    var problem: []const u8 = "";
    const id = requiredString(a, parsed.object, "id", &problem) catch |e| switch (e) {
        Problem.Protocol => return failure(out, null, "ProtocolError", problem),
        else => return e,
    };
    if (query.masked and std.mem.indexOf(u8, id, query.lone_mark) != null) return failure(out, null, "SessionError", query.lone_message);
    const op = requiredString(a, parsed.object, "op", &problem) catch |e| switch (e) {
        Problem.Protocol => return failure(out, id, "ProtocolError", problem),
        else => return e,
    };
    if (query.masked and std.mem.indexOf(u8, op, query.lone_mark) != null) return failure(out, id, "SessionError", query.lone_message);
    // `encodeProposal` reads `detail` (and, for a full review, `structuredReview`) of every request before its operation.
    if (parsed.object.get("detail")) |detail| {
        const text = if (detail == .string) detail.string else "";
        if (query.masked and std.mem.indexOf(u8, text, query.lone_mark) != null) return failure(out, id, "SessionError", query.lone_message);
        const full = std.mem.eql(u8, text, "full");
        if (!full and !std.mem.eql(u8, text, "compact") and !(std.mem.eql(u8, op, "readFunction") and std.mem.eql(u8, text, "outline")))
            return failure(out, id, "ProtocolError", "detail must be \"compact\" or \"full\".");
        if (full) if (parsed.object.get("structuredReview")) |value| if (value != .bool)
            return failure(out, id, "ProtocolError", "Property 'structuredReview' must be a JSON boolean.");
    }
    if (std.mem.eql(u8, op, "status")) {
        if (try fieldProblem(a, line, parsed.object, "status request", &.{ "id", "op" }, &.{ "id", "op" })) |message| return failure(out, id, "ProtocolError", message);
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
    const plain = [_][]const u8{ "dependency", "declarations", "proposals" };
    for (plain) |name| if (std.mem.eql(u8, op, name)) {
        if (try fieldProblem(a, line, parsed.object, try std.fmt.allocPrint(a, "{s} request", .{op}), &.{ "id", "op" }, &.{ "id", "op" })) |message| return failure(out, id, "ProtocolError", message);
        try success(out, id);
        if (std.mem.eql(u8, op, "dependency")) {
            // `PinnedDependency.describe`: an anonymous record, so its fields in name order.
            try out.writeAll("{\"selection\":");
            if (selection) |dependency| {
                const members = dependency.provenance.members;
                if (dependency.set) try out.writeAll("{\"dependencies\":[");
                for (members, 0..) |member, index| {
                    if (index > 0) try out.writeByte(',');
                    try out.writeAll("{\"fingerprint\":");
                    try writeString(out, member.fingerprint);
                    try out.writeAll(",\"path\":");
                    try writeString(out, member.path);
                    try out.writeAll(",\"snapshotId\":");
                    try writeString(out, member.id);
                    try out.writeAll(if (member.embedded) ",\"storage\":\"embedded\"}" else ",\"storage\":\"legacySource\"}");
                }
                if (dependency.set) try out.writeAll("]}");
            } else try out.writeAll("null");
            try out.writeAll("}");
        } else if (std.mem.eql(u8, op, "declarations")) {
            try wire.declarations(a, out, canonical(), if (selection) |d| d.provenance else null);
        } else try out.writeAll("{\"count\":0,\"proposals\":[]}");
        return out.writeByte('}');
    };
    // No proposal exists until simulate is ported: these answer `ProposalNotFound` once their input reads.
    if (std.mem.eql(u8, op, "readProposal") or std.mem.eql(u8, op, "viewProposal")) {
        const read = std.mem.eql(u8, op, "readProposal");
        const allowed: []const []const u8 = if (read) &.{ "id", "op", "proposalId", "path", "offset", "limit", "structuredReview" } else &.{ "id", "op", "proposalId", "output" };
        if (try fieldProblem(a, line, parsed.object, try std.fmt.allocPrint(a, "{s} request", .{op}), allowed, &.{ "id", "op", "proposalId" })) |message| return failure(out, id, "ProtocolError", message);
        const optional = if (read) "path" else "output";
        if (parsed.object.get(optional) != null) _ = requiredString(a, parsed.object, optional, &problem) catch |e| switch (e) {
            Problem.Protocol => return failure(out, id, "ProtocolError", problem),
            else => return e,
        };
        const proposal = requiredString(a, parsed.object, "proposalId", &problem) catch |e| switch (e) {
            Problem.Protocol => return failure(out, id, "ProtocolError", problem),
            else => return e,
        };
        return failure(out, id, "ProposalNotFound", try std.fmt.allocPrint(a, "No pending proposal has the ID '{s}'; it was committed, rejected, or discarded when its session closed.", .{proposal}));
    }
    const queries = [_]struct { []const u8, *const fn (*query.Request, *std.Io.Writer) anyerror!void }{
        .{ "search", query.search },
        .{ "references", query.references },
        .{ "unreferenced", query.unreferenced },
        .{ "bindingScopes", query.bindingScopes },
        .{ "readFunction", query.readFunction },
        .{ "viewFunction", query.viewFunction },
        .{ "executionReadiness", query.executionReadiness },
        .{ "simulateCreateFunction", query.simulateCreateFunction },
    .{ "simulateCreateFunctionByName", query.simulateCreateFunctionByName },
    };
    for (queries) |entry| if (std.mem.eql(u8, op, entry[0])) {
        var request: query.Request = .{ .a = a, .line = line, .root = parsed.object, .model = canonical(), .provenance = if (selection) |d| d.provenance else null, .snapshot_id = snapshot_id, .snapshot_path = snapshot_path };
        var result: std.Io.Writer.Allocating = .init(a);
        entry[1](&request, &result.writer) catch |e| switch (e) {
            error.Rejected => return failureWith(out, id, request.rejection.code, request.rejection.message, request.rejection.details),
            else => return e,
        };
        try success(out, id);
        try out.writeAll(result.written());
        return out.writeByte('}');
    };
    return failure(out, id, "UnsupportedOperation", try std.fmt.allocPrint(a, "Unsupported operation '{s}'.", .{op}));
}

/// `{"id":…,"ok":true,"result":` (the caller writes the result and the closing brace).
fn success(out: *std.Io.Writer, id: []const u8) !void {
    try out.writeAll("{\"id\":");
    try writeString(out, id);
    try out.writeAll(",\"ok\":true,\"result\":");
}

fn fieldProblem(a: std.mem.Allocator, line: []const u8, root: std.json.ObjectMap, context: []const u8, fields: []const []const u8, required: []const []const u8) !?[]const u8 {
    return query.fieldProblem(a, line, &.{}, root, context, fields, required);
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

/// `AuthoringSession.openUsing`: the file, the lock and the sweep (unless read-only), the dependency
/// sidecar or the marker's absence, then the readers' verdict, unresolved or against the
/// dependency. The lock lives until the process ends.
fn open(io: std.Io, a: std.mem.Allocator, given: []const u8, read_only: bool) !void {
    const path = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), given });
    snapshot_path = path;
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
    ids.observe(source);
    const record = std.mem.endsWith(u8, path, ".nyrec");
    const read = openSource(io, a, path, record, source) catch {
        // `openUsing`'s recovery hints: an interrupted replacement or first addition is named.
        const failed = failure_line;
        const previous = try std.mem.concat(a, u8, &.{ path, ".dependency.json.previous" });
        if (exists(io, previous) and matches(io, a, previous, record, source))
            startupFailure("DependencyFailure: The dependency sidecar '{s}.dependency.json' does not match the snapshot, but '{s}' does: a dependency replacement was interrupted. Restore it over the sidecar to recover the previous selection.", .{ path, previous });
        if (exists(io, try std.mem.concat(a, u8, &.{ path, ".dependency.json" })) and (if (readSource(a, record, source, null)) |_| true else |_| false))
            startupFailure("DependencyFailure: The snapshot '{s}' has no dependency, but '{s}.dependency.json' exists: adding a first dependency was interrupted. Delete the sidecar to recover the workspace without it, then add the dependency again.", .{ path, path });
        startupFailure("{s}", .{failed});
    };
    snapshot_id = read.id;
    fingerprints = read.fingerprints.?;
    model_root = read.root;
    startup = a;
}

/// A startup failure on its way to stderr: the line `WireEncode.startupMessage` prints.
const Startup = error{ Startup, OutOfMemory };
var failure_line: []const u8 = "";

fn fail(a: std.mem.Allocator, comptime format: []const u8, args: anytype) Startup {
    failure_line = std.fmt.allocPrint(a, format, args) catch "out of memory";
    return error.Startup;
}

fn exists(io: std.Io, path: []const u8) bool {
    return if (std.Io.Dir.cwd().statFile(io, path, .{})) |_| true else |_| false;
}

/// The snapshot read with its sidecar's dependency, or unresolved without one.
fn openSource(io: std.Io, a: std.mem.Allocator, path: []const u8, record: bool, source: []const u8) Startup!model_reader.DocumentRead {
    const dependency = try loadSidecar(io, a, try std.mem.concat(a, u8, &.{ path, ".dependency.json" }));
    selection = dependency;
    if (dependency == null and bound(a, record, source)) return fail(a, "DependencyFailure: The dependency sidecar '{s}.dependency.json' is missing. Restore the exact sidecar that belongs to this snapshot; pinning a new version cannot recover a lost selection.", .{path});
    return readSource(a, record, source, dependency);
}

/// Whether the snapshot reads against the sidecar at `manifest` (or unresolved when it selects none).
fn matches(io: std.Io, a: std.mem.Allocator, manifest: []const u8, record: bool, source: []const u8) bool {
    const dependency = loadSidecar(io, a, manifest) catch return false;
    return if (readSource(a, record, source, dependency)) |_| true else |_| false;
}

/// `SessionStorage.requireSidecarForResolvedSource`: whether the snapshot names a dependency.
fn bound(a: std.mem.Allocator, record: bool, source: []const u8) bool {
    if (record) return std.mem.indexOf(u8, source[0 .. std.mem.indexOfScalar(u8, source, '\n') orelse source.len], " dependency=") != null;
    const root = std.json.parseFromSliceLeaky(std.json.Value, a, source, .{ .duplicate_field_behavior = .use_last }) catch return false;
    return root == .object and root.object.get("dependency") != null;
}

/// A dependency as the local snapshot resolves against it.
const Dependency = struct { canonical: []const u8, id: []const u8, digest: []const u8, fingerprint: []const u8, subjects: []const []const u8, provenance: wire.Provenance, set: bool = false };

/// A source decoded (`SessionStorage.decodeWith`): records first through the snapshot reader, then
/// the model reader, unresolved or against `dependency`; a rejection becomes the startup line.
fn readSource(a: std.mem.Allocator, record: bool, source: []const u8, dependency: ?Dependency) Startup!model_reader.DocumentRead {
    var record_line: ?[]const u8 = null;
    var json: []const u8 = source;
    if (record) {
        const read = snapshot_reader.readRecords(a, source, if (dependency) |d| d.subjects else &.{});
        record_line = read.line;
        json = read.json orelse "";
    }
    const read = if (record_line != null and std.mem.startsWith(u8, record_line.?, "error "))
        model_reader.DocumentRead{ .lines = "" }
    else if (dependency) |d|
        model_reader.readResolvedDocument(a, d.canonical, d.id, d.digest, json)
    else
        model_reader.readDocument(a, json);
    if (codecProblem(a, record_line, read.lines) catch "out of memory") |message| return fail(a, "SnapshotCodecFailure: {s}", .{message});
    if (read.fingerprints == null) return fail(a, "SnapshotCodecFailure: The snapshot could not be fingerprinted.", .{});
    return read;
}

/// `SessionStorage.decodeCompleteDependency`: a dependency source decoded, with no open obligation.
/// `path` and `embedded` are the manifest's (`SourcePath`, `IsEmbedded`).
fn decodeDependency(a: std.mem.Allocator, record: bool, source: []const u8, path: []const u8, embedded: bool) Startup!Dependency {
    const read = try readSource(a, record, source, null);
    if (read.open_obligations) return fail(a, "DependencyFailure: The selected dependency has open obligations; complete it before pinning.", .{});
    const keys = model_reader.declarationKeys(a, read.root);
    const own = try a.alloc(wire.Provenance.Member, 1);
    own[0] = .{ .id = read.id, .fingerprint = read.fingerprints.?.snapshot, .keys = keys.all, .path = path, .embedded = embedded };
    return dependencyOf(read, own, keys.hidden);
}

fn dependencyOf(read: model_reader.DocumentRead, members: []const wire.Provenance.Member, hidden: []const []const u8) Dependency {
    return .{ .canonical = read.canonical, .id = read.id, .digest = read.digest, .fingerprint = read.fingerprints.?.snapshot, .subjects = read.subjects, .provenance = .{ .members = members, .hidden = hidden } };
}

/// `PinnedDependency.loadFrom`: null without a sidecar; one member, or a set of at least two
/// embedded members combined (`DependencyGraph.combine`).
fn loadSidecar(io: std.Io, a: std.mem.Allocator, manifest_path: []const u8) Startup!?Dependency {
    const bytes = std.Io.Dir.cwd().readFileAlloc(io, manifest_path, a, .unlimited) catch |e| switch (e) {
        error.FileNotFound => return null,
        else => return fail(a, "DependencyFailure: {s}", .{@errorName(e)}),
    };
    ids.observe(bytes);
    const invalid = "DependencyFailure: Invalid dependency manifest or source path.";
    const text = if (std.mem.startsWith(u8, bytes, "\xEF\xBB\xBF")) bytes[3..] else bytes;
    const root = std.json.parseFromSliceLeaky(std.json.Value, a, text, .{ .duplicate_field_behavior = .use_last }) catch return fail(a, invalid, .{});
    // `JsonDocument` keeps a repeated name, so its field list no longer matches any manifest form.
    // shortcut: decided before any member loads, where F# reports an earlier member's failure first.
    _ = std.json.parseFromSliceLeaky(std.json.Value, a, text, .{ .duplicate_field_behavior = .@"error" }) catch return fail(a, "DependencyFailure: Invalid dependency manifest.", .{});
    if (root != .object) return fail(a, invalid, .{});
    const set = root.object.get("dependencies");
    if (set == null or root.object.count() != 1) return try loadMember(io, a, root.object);
    if (set.? != .array or set.?.array.items.len < 2) return fail(a, "DependencyFailure: A dependency set lists at least two dependencies.", .{});
    var members: std.ArrayList(Dependency) = .empty;
    for (set.?.array.items) |item| {
        if (item != .object or (item.object.get("snapshotJson") == null and item.object.get("snapshotRecords") == null))
            return fail(a, "DependencyFailure: Every member of a dependency set must embed its content.", .{});
        members.append(a, try loadMember(io, a, item.object)) catch return fail(a, "out of memory", .{});
    }
    return try combineMembers(a, members.items);
}

/// `PinnedDependency.loadMember`: the embedded content or the file at `path`, whose fingerprint
/// and snapshot ID must be the recorded ones.
fn loadMember(io: std.Io, a: std.mem.Allocator, manifest: std.json.ObjectMap) Startup!Dependency {
    const invalid = "DependencyFailure: Invalid dependency manifest or source path.";
    var fields: std.ArrayList([]const u8) = .empty;
    for (manifest.keys()) |key| fields.append(a, key) catch return fail(a, "out of memory", .{});
    std.mem.sort([]const u8, fields.items, {}, struct {
        fn less(_: void, x: []const u8, y: []const u8) bool {
            return std.mem.order(u8, x, y) == .lt;
        }
    }.less);
    var embedded_key: ?[]const u8 = null;
    for (fields.items) |name| if (std.mem.eql(u8, name, "snapshotJson") or std.mem.eql(u8, name, "snapshotRecords")) {
        embedded_key = name;
        break;
    };
    const expected: []const []const u8 = if (embedded_key) |key| &.{ "fingerprint", "path", "snapshotId", key } else &.{ "fingerprint", "path", "snapshotId" };
    var same = fields.items.len == expected.len;
    if (same) for (fields.items, expected) |x, y| {
        same = same and std.mem.eql(u8, x, y);
    };
    // A set member's key may sort before the others; F# compares the sorted list with the key last.
    if (!same) return fail(a, "DependencyFailure: Invalid dependency manifest.", .{});
    const strings = [_][]const u8{ "path", "fingerprint", "snapshotId" };
    var values: [3]?[]const u8 = undefined;
    for (strings, &values) |name, *value| value.* = switch (manifest.get(name).?) {
        .string => |text| text,
        .null => null,
        else => return fail(a, invalid, .{}),
    };
    for (values) |value| if (value == null or std.mem.trim(u8, value.?, " \t\r\n").len == 0)
        return fail(a, "DependencyFailure: Dependency manifest fields must be non-blank strings.", .{});
    const dependency = if (embedded_key) |key| blk: {
        const content = switch (manifest.get(key).?) {
            .string => |text| text,
            .null => "",
            else => return fail(a, invalid, .{}),
        };
        if (std.mem.trim(u8, content, " \t\r\n").len == 0) return fail(a, "DependencyFailure: The embedded dependency is missing.", .{});
        break :blk try decodeDependency(a, std.mem.eql(u8, key, "snapshotRecords"), content, values[0].?, true);
    } else blk: {
        const source_path = values[0].?;
        if (!std.fs.path.isAbsolute(source_path) or !exists(io, source_path))
            return fail(a, "DependencyFailure: The selected dependency is unavailable. Use pin with an exact copy of the original version to recover a legacy selection.", .{});
        const bytes = std.Io.Dir.cwd().readFileAlloc(io, source_path, a, .unlimited) catch |e| return fail(a, "IoFailure: {s}", .{@errorName(e)});
        const unmarked = if (std.mem.startsWith(u8, bytes, "\xEF\xBB\xBF")) bytes[3..] else bytes;
        const content: []const u8 = if (std.unicode.utf8ValidateSlice(unmarked)) unmarked else std.fmt.allocPrint(a, "{f}", .{std.unicode.fmtUtf8(unmarked)}) catch return fail(a, "out of memory", .{});
        ids.observe(content);
        const record = std.mem.endsWith(u8, source_path, ".nyrec");
        if (bound(a, record, content)) return fail(a, "DependencyFailure: The dependency sidecar '{s}.dependency.json' is missing. Restore the exact sidecar that belongs to this snapshot; pinning a new version cannot recover a lost selection.", .{source_path});
        break :blk try decodeDependency(a, record, content, source_path, false);
    };
    if (!std.mem.eql(u8, dependency.fingerprint, values[1].?) or !std.mem.eql(u8, dependency.id, values[2].?))
        return fail(a, "DependencyFailure: The selected dependency version changed. Pin only accepts the exact previously selected content, not an upgrade.", .{});
    return dependency;
}

/// `PinnedDependency.combine`: the members under the import collision policy, as one dependency
/// whose ID derives from the members' fingerprints (`DependencyGraph.combinedId`).
fn combineMembers(a: std.mem.Allocator, members: []const Dependency) Startup!Dependency {
    var fingerprints_sorted: std.ArrayList([]const u8) = .empty;
    var texts: std.ArrayList([]const u8) = .empty;
    for (members) |member| {
        fingerprints_sorted.append(a, member.fingerprint) catch return fail(a, "out of memory", .{});
        texts.append(a, member.canonical) catch return fail(a, "out of memory", .{});
    }
    std.mem.sort([]const u8, fingerprints_sorted.items, {}, struct {
        fn less(_: void, x: []const u8, y: []const u8) bool {
            return std.mem.order(u8, x, y) == .lt;
        }
    }.less);
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(std.mem.join(a, "\n", fingerprints_sorted.items) catch return fail(a, "out of memory", .{}), &digest, .{});
    // `Guid(bytes).ToString("D")`: the first three groups are little-endian.
    const d = digest;
    const id = std.fmt.allocPrint(a, "dependency-set-{x:0>2}{x:0>2}{x:0>2}{x:0>2}-{x:0>2}{x:0>2}-{x:0>2}{x:0>2}-{x:0>2}{x:0>2}-{x}", .{ d[3], d[2], d[1], d[0], d[5], d[4], d[7], d[6], d[8], d[9], d[10..16] }) catch return fail(a, "out of memory", .{});
    switch (model_reader.combine(a, texts.items, id)) {
        .conflict => |conflict| {
            var providers: std.ArrayList(u8) = .empty;
            for (conflict.names, members) |name, member| if (name) |display| {
                if (providers.items.len > 0) providers.appendSlice(a, "; ") catch {};
                providers.print(a, "'{s}' from providing pin '{s}' (fingerprint {s})", .{ display, member.id, member.fingerprint }) catch {};
            };
            const context = if (providers.items.len == 0) "" else std.fmt.allocPrint(a, " Providing declarations: {s}.", .{providers.items}) catch "";
            return fail(a, "DependencyFailure: Dependencies declare the {s} '{s}' with different content; colliding imported identities are never merged.{s}", .{ conflict.kind, conflict.id, context });
        },
        .text => |combined| {
            const read = try readSource(a, false, combined, null);
            var sources: std.ArrayList(wire.Provenance.Member) = .empty;
            for (members) |member| try sources.append(a, member.provenance.members[0]);
            var dependency = dependencyOf(read, sources.items, model_reader.declarationKeys(a, read.root).hidden);
            dependency.set = true;
            return dependency;
        },
    }
}

pub fn main(init: std.process.Init) !void {
    ids.init(init.environ_map.get("NYRWVY_TEST_ID_SEED"));
    const process_arena = init.arena.allocator();
    const all = try init.minimal.args.toSlice(process_arena);
    // `CommandLine.parse`: `--allow-host-sources` anywhere, then exactly one of the open forms.
    var args: std.ArrayList([]const u8) = .empty;
    for (all[1..]) |arg| if (!std.mem.eql(u8, arg, "--allow-host-sources")) try args.append(process_arena, arg);
    const forms = args.items.len >= 3 and std.mem.eql(u8, args.items[0], "open") and std.mem.eql(u8, args.items[1], "--snapshot");
    const read_only = forms and args.items.len == 4 and std.mem.eql(u8, args.items[3], "--read-only");
    if (!forms or !(args.items.len == 3 or read_only)) {
        std.debug.print("{s}", .{usage});
        std.process.exit(2);
    }
    try open(init.io, process_arena, args.items[2], read_only);

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
