//! The Zig session host (docs/decisions/zig-session-host.md): the F# host's JSONL protocol, one
//! response line per request line, and its startup. So far: `open` of a `.json` or `.nyrec`
//! snapshot, unresolved or with its dependency sidecar (read-only or under its lock), the request envelope and its errors as
//! `AuthoringProtocol.executeLine` gives them, and `status`; every other operation is
//! `UnsupportedOperation`. `ZigHostParityTests` compares it with the F# host. The snapshot is read
//! by the model and snapshot readers, imported as modules (`ZigTools` builds them together).
const std = @import("std");
const build_info = @import("build_info");
const model_reader = @import("model_reader");
const snapshot_reader = @import("snapshot_reader");
const wire = @import("wire.zig");
const query = @import("query.zig");
const ids = @import("ids.zig");
const proposals = @import("proposals.zig");

/// The open snapshot: what `status` reports, the model (merged with the dependency, if any) and
/// the selected dependency. Initial values live in `startup`; commits own retained bytes.
var snapshot_id: []const u8 = "";
var snapshot_path: []const u8 = "";
var fingerprints: model_reader.Fingerprints = undefined;
pub var selection: ?Dependency = null;
var retained_owned = false;
var test_fail_operation: ?[]const u8 = null;
var writable = false;
pub var invalidated = false;
pub var persisted_source: []const u8 = "";
pub var dependency_manifest: ?[]const u8 = null;
pub var host_io: std.Io = undefined;
pub var host_sources_allowed = false;

// `WireInput.requestMaxJsonDepth`: 3 + 3 * 256 + 64.
const max_depth = 835;

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
pub fn tooDeep(line: []const u8) bool {
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

pub fn maskLoneSurrogates(a: std.mem.Allocator, line: []const u8) !Masked {
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

fn execute(a: std.mem.Allocator, out: *std.Io.Writer, given: []const u8, request_id: *?[]const u8) !void {
    // The model reader's helpers allocate from the request's arena, not a freed earlier one.
    model_reader.beginRequest(a);
    const masked = try maskLoneSurrogates(a, given);
    query.masked = masked.line != null;
    const line = masked.line orelse given;
    if (std.mem.trim(u8, line, " \t\r\n").len == 0) return failure(out, null, "ProtocolError", "Empty request line; expected one JSON object.");
    // The F# message carries .NET's parser text after this prefix; the parity test compares the prefix.
    if (tooDeep(line)) return failure(out, null, "ProtocolError", "Invalid JSON: maximum depth exceeded.");
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, a, line, .{ .duplicate_field_behavior = .use_last }) catch |e| switch (e) {
        error.OutOfMemory => return e,
        else => return failure(out, null, "ProtocolError", try std.fmt.allocPrint(a, "Invalid JSON: {s}", .{@errorName(e)})),
    };
    if (parsed != .object) return failure(out, null, "ProtocolError", "Expected a JSON object.");
    if (masked.root_key) return failure(out, null, "SessionError", query.lone_message);
    var problem: []const u8 = "";
    const id = requiredString(a, parsed.object, "id", &problem) catch |e| switch (e) {
        Problem.Protocol => return failure(out, null, "ProtocolError", problem),
        else => return e,
    };
    if (query.masked and std.mem.indexOf(u8, id, query.lone_mark) != null) return failure(out, null, "SessionError", query.lone_message);
    request_id.* = id;
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
    if (std.mem.eql(u8, op, "commit") or std.mem.eql(u8, op, "reject"))
        return publish(a, out, id, op, line, parsed.object);
    if (std.mem.eql(u8, op, "status")) {
        if (try fieldProblem(a, line, parsed.object, "status request", &.{ "id", "op" }, &.{ "id", "op" })) |message| return failure(out, id, "ProtocolError", message);
        try out.writeAll("{\"id\":");
        if (test_fail_operation) |operation| if (std.mem.eql(u8, op, operation)) return error.InjectedFailure;
        try writeString(out, id);
        try out.writeAll(",\"ok\":true,\"result\":{\"snapshotId\":");
        try writeString(out, snapshot_id);
        try out.writeAll(",\"snapshotFingerprint\":");
        try writeString(out, fingerprints.snapshot);
        try out.writeAll(",\"semanticFingerprint\":");
        try writeString(out, fingerprints.semantic);
        // `build` names each host's own build; the parity test masks it.
        return out.print(",\"pendingProposalCount\":{d},\"build\":\"{s}\"}}}}", .{ proposals.count(), build_info.value });
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
            var request: query.Request = .{ .a = a, .line = line, .root = parsed.object, .provenance = if (selection) |d| d.provenance else null };
            try proposals.bindTypedRead(&request);
            try wire.typedDeclarations(a, out, request.generation.?, request.interner.?, request.provenance);
        } else try proposals.listing(a, out, snapshot_id, proposals.currentModel(a));
        return out.writeByte('}');
    };
    const queries = [_]struct { []const u8, *const fn (*query.Request, *std.Io.Writer) anyerror!void }{
        .{ "runPure", @import("run_pure.zig").run },
        .{ "runPureByName", @import("run_pure.zig").run },
        .{ "emitCSharp", @import("emit_csharp.zig").run },
        .{ "verifyChange", @import("change_verification.zig").run },
        .{ "verifyChangeByName", @import("change_verification.zig").run },
        .{ "simulateDependencyUpgrade", @import("dependency_simulation.zig").simulate },
        .{ "simulateDependencyUpgradeByName", @import("dependency_simulation.zig").simulate },
        .{ "previewDependencyUpgrade", @import("dependency_upgrade.zig").preview },
        .{ "previewDependencyUpgrades", @import("dependency_upgrade.zig").preview },
        .{ "search", query.search },
        .{ "references", query.references },
        .{ "unreferenced", query.unreferenced },
        .{ "bindingScopes", query.bindingScopes },
        .{ "readFunction", query.readFunction },
        .{ "viewFunction", query.viewFunction },
        .{ "readProposal", proposals.readProposal },
        .{ "viewProposal", proposals.viewProposal },
        .{ "executionReadiness", query.executionReadiness },
        .{ "simulateCreateType", @import("declarations.zig").createType },
        .{ "simulateCreateTypeByName", @import("declarations.zig").createType },
        .{ "simulateCreateTypesByName", @import("declarations.zig").createType },
        .{ "simulateCreateEffect", @import("declarations.zig").createEffect },
        .{ "simulateCreateCapability", @import("declarations.zig").createCapability },
        .{ "simulateCreateCapabilityByName", @import("declarations.zig").createCapability },
        .{ "simulateCreateFunctionsByName", @import("function_batch.zig").create },
        .{ "simulateBatchByName", @import("mixed_batch.zig").simulate },
        .{ "simulateParameterTypeMigration", @import("migration.zig").simulateRequest },
        .{ "simulateParameterTypeMigrationByName", @import("migration.zig").simulateRequest },
        .{ "simulateResultTypeMigration", @import("migration.zig").simulateRequest },
        .{ "simulateResultTypeMigrationByName", @import("migration.zig").simulateRequest },
        .{ "simulateProductFieldTypeMigration", @import("structural_migration.zig").simulateRequest },
        .{ "simulateProductFieldTypeMigrationByName", @import("structural_migration.zig").simulateRequest },
        .{ "simulateCapabilityOperationSignatureMigration", @import("structural_migration.zig").simulateRequest },
        .{ "simulateCapabilityOperationSignatureMigrationByName", @import("structural_migration.zig").simulateRequest },
        .{ "simulateAddUnionVariantLike", @import("union_evolution.zig").simulateRequest },
        .{ "simulateAddUnionVariantLikeByName", @import("union_evolution.zig").simulateRequest },
        .{ "simulateOperations", @import("operation_engine.zig").simulateRequest },
        .{ "simulateOperationsByName", @import("operation_engine.zig").simulateRequest },
        .{ "simulateCreateFunction", query.simulateCreateFunction },
        .{ "simulateCreateFunctionByName", query.simulateCreateFunctionByName },
        .{ "simulateReplaceBody", query.simulateReplaceBody },
        .{ "simulateReplaceTarget", query.simulateReplaceTarget },
        .{ "simulateReplaceTargetByName", query.simulateReplaceTargetByName },
        .{ "simulateReplaceBodyByName", query.simulateReplaceBodyByName },
    };
    for (queries) |entry| if (std.mem.eql(u8, op, entry[0])) {
        var request: query.Request = .{ .a = a, .line = line, .root = parsed.object, .provenance = if (selection) |d| d.provenance else null, .snapshot_id = snapshot_id, .snapshot_fingerprint = fingerprints.snapshot, .snapshot_path = snapshot_path, .session_invalidated = invalidated };
        try proposals.bindTypedRead(&request);
        const previous_count = proposals.count();
        var succeeded = false;
        defer if (!succeeded or proposals.count() == previous_count) proposals.rollbackRequest(request.interner_checkpoint.?, previous_count);
        var result: std.Io.Writer.Allocating = .init(a);
        entry[1](&request, &result.writer) catch |e| switch (e) {
            error.Rejected => {
                var encoded: std.Io.Writer.Allocating = .init(a);
                try failureWith(&encoded.writer, id, request.rejection.code, request.rejection.message, request.rejection.details);
                if (request.rejection.fields.len == 0) return out.writeAll(encoded.written());
                try out.writeAll(encoded.written()[0 .. encoded.written().len - 2]);
                try out.writeAll(request.rejection.fields);
                return out.writeAll("}}");
            },
            else => return e,
        };
        try success(out, id);
        try out.writeAll(result.written());
        succeeded = true;
        return out.writeByte('}');
    };
    return failure(out, id, "UnsupportedOperation", try std.fmt.allocPrint(a, "Unsupported operation '{s}'.", .{op}));
}

pub fn readText(a: std.mem.Allocator, path: []const u8) ![]const u8 {
    const bytes = try std.Io.Dir.cwd().readFileAlloc(host_io, path, a, .unlimited);
    const text = if (std.mem.startsWith(u8, bytes, "\xEF\xBB\xBF")) bytes[3..] else bytes;
    return if (std.unicode.utf8ValidateSlice(text)) text else try std.fmt.allocPrint(a, "{f}", .{std.unicode.fmtUtf8(text)});
}

/// Exact persisted source and sidecar guards shared by previews and publication.
pub fn verifyPersisted(a: std.mem.Allocator) !?query.Rejection {
    if (dependency_manifest) |expected| {
        const sidecar = try std.mem.concat(a, u8, &.{ snapshot_path, ".dependency.json" });
        const sidecar_stat = std.Io.Dir.cwd().statFile(host_io, sidecar, .{}) catch null;
        const actual = if (sidecar_stat == null or sidecar_stat.?.kind != .file) "" else readText(a, sidecar) catch |e| switch (e) {
            error.FileNotFound => "",
            else => return .{ .code = "IoFailure", .message = @errorName(e) },
        };
        var unchanged = std.mem.eql(u8, actual, expected);
        // Embedded source paths are provenance only. A legacy pin still reads its authority;
        // transient read failures do not invalidate an otherwise usable session.
        if (unchanged and !selection.?.provenance.members[0].embedded) {
            const current = loadSidecar(host_io, a, sidecar) catch blk: {
                if (std.mem.startsWith(u8, failure_line, "IoFailure: ")) return .{ .code = "IoFailure", .message = failure_line[11..] };
                break :blk null;
            };
            unchanged = current != null and std.mem.eql(u8, current.?.fingerprint, selection.?.fingerprint) and std.mem.eql(u8, current.?.id, selection.?.id);
        }
        if (!unchanged) {
            invalidated = true;
            return .{ .code = "DependencyFailure", .message = "The dependency selection changed outside this session." };
        }
    }
    const stat = std.Io.Dir.cwd().statFile(host_io, snapshot_path, .{}) catch null;
    if (stat == null or stat.?.kind != .file) {
        invalidated = true;
        return .{ .code = "ExternalModification", .message = "The snapshot file changed outside this session." };
    }
    const current = readText(a, snapshot_path) catch |e| switch (e) {
        error.FileNotFound => "",
        else => return .{ .code = "IoFailure", .message = @errorName(e) },
    };
    if (!std.mem.eql(u8, current, persisted_source)) {
        invalidated = true;
        return .{ .code = "ExternalModification", .message = "The snapshot file changed outside this session." };
    }
    return null;
}

pub fn replaceAtomically(a: std.mem.Allocator, path: []const u8, content: []const u8) !?query.Rejection {
    return switch (try @import("view_file.zig").replaceAtomic(a, path, content)) {
        .refused => |message| .{ .code = "IoFailure", .message = message },
        .internal => unreachable,
        .written => null,
    };
}

/// Keep the old sidecar recoverable until both renames or the rollback restore consistency.
pub fn replaceDependencyPair(a: std.mem.Allocator, next_source: []const u8, next_manifest: []const u8) !?query.Rejection {
    return replaceDependencyPairUsing(a, next_source, next_manifest, replaceAtomically);
}
fn replaceDependencyPairUsing(a: std.mem.Allocator, next_source: []const u8, next_manifest: []const u8, comptime replace: anytype) !?query.Rejection {
    const sidecar = try std.mem.concat(a, u8, &.{ snapshot_path, ".dependency.json" });
    const previous = try std.mem.concat(a, u8, &.{ sidecar, ".previous" });
    const source = readText(a, snapshot_path) catch |err| return .{ .code = "IoFailure", .message = @errorName(err) };
    const manifest = readText(a, sidecar) catch |err| return .{ .code = "IoFailure", .message = @errorName(err) };
    if (!std.mem.eql(u8, source, persisted_source) or !std.mem.eql(u8, manifest, dependency_manifest.?)) return .{ .code = "ExternalModification", .message = "The snapshot file changed outside this session." };
    if (try replace(a, previous, manifest)) |problem| return problem;
    var consistent = true;
    defer if (consistent) std.Io.Dir.cwd().deleteFile(host_io, previous) catch {};
    if (try replace(a, sidecar, next_manifest)) |problem| return problem;
    consistent = false;
    // A thrown write error needs the same rollback as an ordinary storage rejection.
    const snapshot_problem: ?query.Rejection = replace(a, snapshot_path, next_source) catch |err| .{ .code = "IoFailure", .message = @errorName(err) };
    if (snapshot_problem) |problem| {
        const rollback_problem: ?query.Rejection = replace(a, sidecar, manifest) catch |err| .{ .code = "IoFailure", .message = @errorName(err) };
        if (rollback_problem) |rollback| return .{ .code = "IoFailure", .message = try std.fmt.allocPrint(a, "{s} Restoring the previous dependency selection also failed: {s} The previous sidecar is kept at '{s}'.", .{ problem.message, rollback.message, previous }) };
        consistent = true;
        return problem;
    }
    consistent = true;
    return null;
}

fn commitRejected(a: std.mem.Allocator, out: *std.Io.Writer, id: []const u8, code: []const u8, message: []const u8) !void {
    var details: std.Io.Writer.Allocating = .init(a);
    try details.writer.writeAll("{\"diagnostics\":[{\"code\":");
    try writeString(&details.writer, code);
    try details.writer.writeAll(",\"message\":");
    try writeString(&details.writer, message);
    try details.writer.writeAll("}]}");
    return failureWith(out, id, "CommitRejected", "The reviewed proposal could not be committed.", details.written());
}

fn publish(a: std.mem.Allocator, out: *std.Io.Writer, id: []const u8, op: []const u8, line: []const u8, root: std.json.ObjectMap) !void {
    const commit = std.mem.eql(u8, op, "commit");
    const allowed: []const []const u8 = if (commit) &.{ "id", "op", "proposalId", "allowIncomplete" } else &.{ "id", "op", "proposalId" };
    if (try fieldProblem(a, line, root, try std.fmt.allocPrint(a, "{s} request", .{op}), allowed, &.{ "id", "op", "proposalId" })) |message|
        return failure(out, id, "ProtocolError", message);
    var allow_incomplete = false;
    if (commit) if (root.get("allowIncomplete")) |value| {
        if (value != .bool) return failure(out, id, "ProtocolError", "Property 'allowIncomplete' must be a JSON boolean.");
        allow_incomplete = value.bool;
    };
    var problem: []const u8 = "";
    const wanted = requiredString(a, root, "proposalId", &problem) catch |e| switch (e) {
        Problem.Protocol => return failure(out, id, "ProtocolError", problem),
        else => return e,
    };
    if (query.masked and std.mem.indexOf(u8, wanted, query.lone_mark) != null) return failure(out, id, "SessionError", query.lone_message);
    if (invalidated) return failure(out, id, "SessionInvalidated", "The session was invalidated by external modification.");
    if (commit and !writable) return failure(out, id, "ReadOnlySession", "This session was opened read-only and cannot commit. Reopen it without readOnly to commit.");
    const p = proposals.find(wanted) orelse return failure(out, id, "ProposalNotFound", try std.fmt.allocPrint(a, "No pending proposal has the ID '{s}'; it was committed, rejected, or discarded when its session closed.", .{wanted}));
    if (!commit) {
        try proposals.prepareReject(wanted);
        defer proposals.cancelInstall();
        try success(out, id);
        try out.writeAll("{\"transactionId\":");
        try writeString(out, wanted);
        try out.writeAll(",\"retainedSnapshotId\":");
        try writeString(out, snapshot_id);
        try out.writeAll("}}");
        proposals.reject(wanted);
        return;
    }
    if (p.dependency) |dependency| {
        if (selection == null) return failure(out, id, "DependencyFailure", "This workspace has no dependency selection.");
        var request: query.Request = .{ .a = a, .line = line, .root = root, .provenance = selection.?.provenance, .snapshot_id = snapshot_id, .snapshot_fingerprint = fingerprints.snapshot, .snapshot_path = snapshot_path, .session_invalidated = invalidated };
        try proposals.bindTypedRead(&request);
        const previous_count = proposals.count();
        defer proposals.rollbackRequest(request.interner_checkpoint.?, previous_count);
        @import("dependency_simulation.zig").replan(&request, dependency) catch |err| switch (err) {
            error.Rejected => return failureWith(out, id, request.rejection.code, request.rejection.message, request.rejection.details),
            else => return err,
        };
    }
    const declaration = if (p.declaration) |d| if (!std.mem.eql(u8, d.group, "functions")) p.declaration else null else null;
    const union_evolution = if (p.migration) |migration| std.mem.eql(u8, migration.kind, "unionVariantEvolution") else false;
    if (!std.mem.eql(u8, snapshot_id, p.base_id)) return commitRejected(a, out, id, "StaleBaseSnapshot", if (declaration) |d| try std.fmt.allocPrint(a, "The workspace advanced beyond the reviewed {s}-declaration proposal.", .{d.subject}) else if (p.operations) "The transaction base snapshot does not match the supplied snapshot." else if (union_evolution) "The workspace advanced beyond the reviewed Union-variant evolution proposal." else "The workspace advanced beyond the reviewed structural proposal.");
    const validated = try proposals.validate(a, p);
    const proof = try model_reader.parseValue(a, p.receipt);
    if (!std.mem.eql(u8, fingerprints.snapshot, model_reader.utf8(model_reader.decoded(model_reader.property(proof, "baseSnapshotFingerprint").?))))
        return commitRejected(a, out, id, "PreviewReceiptBaseMismatch", if (p.operations) "The current base snapshot fingerprint differs from the reviewed preview receipt." else "The current base content differs from the reviewed base.");
    const next_fingerprints = validated.fingerprints orelse return commitRejected(a, out, id, "FingerprintFailure", if (declaration != null or union_evolution) "The proposed snapshot fingerprint could not be recomputed." else "The proposed fingerprint could not be recomputed.");
    if (!std.mem.eql(u8, next_fingerprints.snapshot, model_reader.utf8(model_reader.decoded(model_reader.property(proof, "proposedSnapshotFingerprint").?))) or
        !std.mem.eql(u8, next_fingerprints.semantic, model_reader.utf8(model_reader.decoded(model_reader.property(proof, "proposedSemanticFingerprint").?))))
        return commitRejected(a, out, id, "PreviewReceiptResultMismatch", if (declaration) |d| try std.fmt.allocPrint(a, "The proposed {s} declaration differs from the reviewed result.", .{d.subject}) else if (p.operations) "Re-simulation produced a proposed state different from the reviewed preview receipt." else if (union_evolution) "The proposed Union-variant evolution differs from the reviewed result." else "The proposed state differs from the reviewed receipt.");
    const obligations = try proposals.openObligations(a, p);
    if (!allow_incomplete and obligations.len > 0) return commitRejected(a, out, id, "BlockingObligations", try std.fmt.allocPrint(a, "The proposed snapshot has blocking obligations: {s}.{s}", .{ try std.mem.join(a, ", ", obligations), if (p.operations) @as([]const u8, " Use AllowBlockingObligations explicitly to publish it.") else "" }));
    // Ordinary commits leave the manifest, backend targets and recovery copies unchanged.
    if (try verifyPersisted(a)) |changed| return failureWith(out, id, changed.code, changed.message, changed.details);
    const selected_dependency: ?Dependency = if (p.dependency) |d| d.selected else selection;
    const written = try proposals.persistedDocument(a, p, selected_dependency);
    const encoded = if (std.mem.endsWith(u8, snapshot_path, ".nyrec")) try snapshot_reader.recordsOf(a, written) else written;
    // Allocate all retained state before publication, leaving no fallible install after rename.
    const retained = std.heap.page_allocator;
    const retained_source = try retained.dupe(u8, encoded);
    var installed = false;
    defer if (!installed) retained.free(retained_source);
    const retained_id = try retained.dupe(u8, validated.id);
    defer if (!installed) retained.free(retained_id);
    const retained_snapshot = try retained.dupe(u8, next_fingerprints.snapshot);
    defer if (!installed) retained.free(retained_snapshot);
    const retained_semantic = try retained.dupe(u8, next_fingerprints.semantic);
    defer if (!installed) retained.free(retained_semantic);
    const retained_fingerprints: model_reader.Fingerprints = .{ .snapshot = retained_snapshot, .semantic = retained_semantic, .canonical = "", .normalized = "" };
    // Encode the entire reply before publication too: buffering can fail with OOM.
    try success(out, id);
    try out.writeAll("{\"transactionId\":");
    try writeString(out, wanted);
    try out.writeAll(",\"publishedSnapshotId\":");
    try writeString(out, retained_id);
    try out.writeAll(",\"readiness\":");
    try proposals.writeReadiness(out, proposals.typedModel(a, p));
    try out.writeAll("}}");
    try proposals.prepareInstall(wanted);
    defer proposals.cancelInstall();
    const storage_problem = if (p.dependency) |d| try replaceDependencyPair(a, encoded, d.manifest) else try replaceAtomically(a, snapshot_path, encoded);
    if (storage_problem) |storage_error| {
        out.end = 0; // Discard the pre-encoded success reply.
        return failureWith(out, id, storage_error.code, storage_error.message, storage_error.details);
    }
    if (p.dependency) |d| {
        selection = d.selected;
        dependency_manifest = d.manifest;
    }
    freeRetained();
    snapshot_id = retained_id;
    fingerprints = retained_fingerprints;
    persisted_source = retained_source;
    retained_owned = true;
    installed = true;
    proposals.install(wanted);
}

fn freeRetained() void {
    if (!retained_owned) return;
    const a = std.heap.page_allocator;
    a.free(persisted_source);
    a.free(snapshot_id);
    a.free(fingerprints.snapshot);
    a.free(fingerprints.semantic);
    retained_owned = false;
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
pub fn startupFailure(comptime format: []const u8, args: anytype) noreturn {
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
pub fn open(io: std.Io, a: std.mem.Allocator, given: []const u8, read_only: bool, pin: bool, source_override: ?[]const u8, sweep: bool) !void {
    const path = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), given });
    snapshot_path = path;
    writable = !read_only;
    const stat = std.Io.Dir.cwd().statFile(io, path, .{}) catch null;
    if (stat == null or stat.?.kind != .file) startupFailure("SnapshotFileNotFound: No snapshot file at {s}. Use create (MCP nyrwvy_open mode \"create\") to start a new one.", .{path});
    if (!read_only) try acquireSnapshotLock(io, a, path, sweep);
    const bytes = std.Io.Dir.cwd().readFileAlloc(io, path, a, .unlimited) catch |e| startupFailure("IoFailure: {s}", .{@errorName(e)});
    // `File.ReadAllText`: a UTF-8 byte order mark is dropped, ill-formed bytes become U+FFFD.
    const unmarked = if (std.mem.startsWith(u8, bytes, "\xEF\xBB\xBF")) bytes[3..] else bytes;
    const source: []const u8 = if (std.unicode.utf8ValidateSlice(unmarked)) unmarked else try std.fmt.allocPrint(a, "{f}", .{std.unicode.fmtUtf8(unmarked)});
    persisted_source = source;
    ids.observe(source);
    const record = std.mem.endsWith(u8, path, ".nyrec");
    const read = openSource(io, a, path, record, source, source_override) catch {
        // `openUsing`'s recovery hints: an interrupted replacement or first addition is named.
        const failed = failure_line;
        const previous = try std.mem.concat(a, u8, &.{ path, ".dependency.json.previous" });
        if (!pin and sweep and exists(io, previous) and matches(io, a, previous, record, source))
            startupFailure("DependencyFailure: The dependency sidecar '{s}.dependency.json' does not match the snapshot, but '{s}' does: a dependency replacement was interrupted. Restore it over the sidecar to recover the previous selection.", .{ path, previous });
        if (!pin and sweep and exists(io, try std.mem.concat(a, u8, &.{ path, ".dependency.json" })) and (if (readSource(a, record, source, null)) |_| true else |_| false))
            startupFailure("DependencyFailure: The snapshot '{s}' has no dependency, but '{s}.dependency.json' exists: adding a first dependency was interrupted. Delete the sidecar to recover the workspace without it, then add the dependency again.", .{ path, path });
        startupFailure("{s}", .{failed});
    };
    if (selection != null) dependency_manifest = try readText(a, try std.mem.concat(a, u8, &.{ path, ".dependency.json" }));
    if (pin) try @import("startup_cli.zig").persistPin(a, path, source_override);
    try install(a, path, source, read, selection);
    writable = !read_only;
}

pub fn install(a: std.mem.Allocator, path: []const u8, source: []const u8, read: model_reader.DocumentRead, selected: ?Dependency) !void {
    snapshot_path = path;
    writable = true;
    persisted_source = source;
    snapshot_id = read.id;
    fingerprints = read.fingerprints.?;
    try proposals.initializeSnapshot(read.generation.?, read.interner.?);
    selection = selected;
    _ = a;
}

pub fn acquireSnapshotLock(io: std.Io, a: std.mem.Allocator, path: []const u8, sweep: bool) !void {
    var link: [std.fs.max_path_bytes]u8 = undefined;
    if (std.Io.Dir.cwd().readLink(io, path, &link)) |_| {
        startupFailure("IoFailure: The snapshot path {s} is a symbolic link; open the file it points to instead.", .{path});
    } else |_| {}
    _ = std.Io.Dir.cwd().createFile(io, try std.mem.concat(a, u8, &.{ path, ".lock" }), .{ .read = true, .truncate = false, .lock = .exclusive, .lock_nonblocking = true }) catch |e| switch (e) {
        error.WouldBlock => startupFailure("LockUnavailable: {s} is open in another authoring session. Close that session first.", .{path}),
        else => startupFailure("IoFailure: {s}", .{@errorName(e)}),
    };
    if (!sweep) return;
    const directory_path = std.fs.path.dirname(path) orelse "/";
    if (std.Io.Dir.cwd().openDir(io, directory_path, .{ .iterate = true })) |directory| {
        var entries = directory.iterate();
        while (entries.next(io) catch null) |entry| {
            // `Directory.EnumerateFiles` also lists links to files and entries of unknown type.
            if (entry.kind != .directory and isTemporaryOf(std.fs.path.basename(path), entry.name)) directory.deleteFile(io, entry.name) catch {};
        }
    } else |_| {}
}

/// A startup failure on its way to stderr: the line `WireEncode.startupMessage` prints.
const Startup = error{ Startup, OutOfMemory };
pub var failure_line: []const u8 = "";
pub var failure_details: []const u8 = "";

fn fail(a: std.mem.Allocator, comptime format: []const u8, args: anytype) Startup {
    failure_details = "";
    failure_line = std.fmt.allocPrint(a, format, args) catch "out of memory";
    return error.Startup;
}

pub fn exists(io: std.Io, path: []const u8) bool {
    return if (std.Io.Dir.cwd().statFile(io, path, .{})) |_| true else |_| false;
}

/// The snapshot read with its sidecar's dependency, or unresolved without one.
fn openSource(io: std.Io, a: std.mem.Allocator, path: []const u8, record: bool, source: []const u8, source_override: ?[]const u8) Startup!model_reader.DocumentRead {
    const dependency = try loadSidecarWithSource(io, a, try std.mem.concat(a, u8, &.{ path, ".dependency.json" }), source_override);
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
pub fn bound(_: std.mem.Allocator, record: bool, source: []const u8) bool {
    var scratch = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer scratch.deinit();
    if (record) return std.mem.indexOf(u8, source[0 .. std.mem.indexOfScalar(u8, source, '\n') orelse source.len], " dependency=") != null;
    const root = std.json.parseFromSliceLeaky(std.json.Value, scratch.allocator(), source, .{ .duplicate_field_behavior = .use_last }) catch return false;
    return root == .object and root.object.get("dependency") != null;
}

/// A dependency as the local snapshot resolves against it.
// The owning arena owns the typed records: startup selections live in startup; preview selections
// remain request-local and must never be installed without copying into retained storage.
pub const Dependency = struct {
    generation: ?*const model_reader.session_model.Generation = null,
    interner: ?*const model_reader.session_model.Interner = null,
    canonical: []const u8,
    id: []const u8,
    digest: []const u8,
    fingerprint: []const u8,
    subjects: []const []const u8,
    provenance: wire.Provenance,
    set: bool = false,
    pub fn typedModel(self: Dependency, a: std.mem.Allocator) model_reader.Model {
        return model_reader.typedRoot(a, self.interner.?, self.generation.?);
    }
};

/// A source decoded (`SessionStorage.decodeWith`): records first through the snapshot reader, then
/// the model reader, unresolved or against `dependency`; a rejection becomes the startup line.
pub fn readSource(a: std.mem.Allocator, record: bool, source: []const u8, dependency: ?Dependency) Startup!model_reader.DocumentRead {
    var scratch = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer scratch.deinit();
    var record_line: ?[]const u8 = null;
    var json: []const u8 = source;
    if (record) {
        const read = snapshot_reader.readRecords(scratch.allocator(), source, if (dependency) |d| d.subjects else &.{});
        record_line = read.line;
        json = read.json orelse "";
    }
    const read = if (record_line != null and std.mem.startsWith(u8, record_line.?, "error "))
        model_reader.DocumentRead{ .lines = "" }
    else if (dependency) |d|
        model_reader.readResolvedTypedDocument(a, d.generation.?, d.interner.?, d.id, d.digest, json)
    else
        model_reader.readTypedDocument(a, json);
    if (codecProblem(a, record_line, read.lines) catch "out of memory") |message| {
        const rejected = fail(a, "SnapshotCodecFailure: {s}", .{message});
        if (read.validation_issues.len > 0) {
            failure_details = codecDetails(a, read) catch return error.OutOfMemory;
        }
        return rejected;
    }
    if (read.fingerprints == null) return fail(a, "SnapshotCodecFailure: The snapshot could not be fingerprinted.", .{});
    return read;
}

fn codecDetails(a: std.mem.Allocator, read: model_reader.DocumentRead) ![]const u8 {
    var details: std.Io.Writer.Allocating = .init(a);
    try details.writer.writeAll("{\"issues\":[");
    for (read.validation_issues, 0..) |issue, index| {
        if (index > 0) try details.writer.writeByte(',');
        try details.writer.writeAll("{\"code\":");
        try writeString(&details.writer, issue.code);
        try details.writer.writeAll(",\"message\":");
        try writeString(&details.writer, model_reader.utf8(issue.message));
        try details.writer.writeByte('}');
    }
    try details.writer.writeAll("]}");
    return details.written();
}

/// `SessionStorage.decodeCompleteDependency`: a dependency source decoded, with no open obligation.
/// `path` and `embedded` are the manifest's (`SourcePath`, `IsEmbedded`).
pub fn decodeDependency(a: std.mem.Allocator, record: bool, source: []const u8, path: []const u8, embedded: bool) Startup!Dependency {
    const read = try readSource(a, record, source, null);
    if (read.open_obligations) return fail(a, "DependencyFailure: The selected dependency has open obligations; complete it before pinning.", .{});
    return dependencyFromRead(a, read, path, embedded);
}

pub fn dependencyFromRead(a: std.mem.Allocator, read: model_reader.DocumentRead, path: []const u8, embedded: bool) Startup!Dependency {
    const keys = model_reader.typedDeclarationKeys(a, model_reader.typedRoot(a, read.interner.?, read.generation.?));
    const own = try a.alloc(wire.Provenance.Member, 1);
    own[0] = .{ .id = read.id, .fingerprint = read.fingerprints.?.snapshot, .keys = keys.all, .path = try a.dupe(u8, path), .embedded = embedded };
    return dependencyOf(read, own, keys.hidden);
}

fn dependencyOf(read: model_reader.DocumentRead, members: []const wire.Provenance.Member, hidden: []const []const u8) Dependency {
    return .{ .generation = read.generation, .interner = read.interner, .canonical = read.canonical, .id = read.id, .digest = read.digest, .fingerprint = read.fingerprints.?.snapshot, .subjects = read.subjects, .provenance = .{ .members = members, .hidden = hidden } };
}

/// `PinnedDependency.loadFrom`: null without a sidecar; one member, or a set of at least two
/// embedded members combined (`DependencyGraph.combine`).
pub fn loadSidecar(io: std.Io, a: std.mem.Allocator, manifest_path: []const u8) Startup!?Dependency {
    return loadSidecarWithSource(io, a, manifest_path, null);
}

fn loadSidecarWithSource(io: std.Io, a: std.mem.Allocator, manifest_path: []const u8, source_override: ?[]const u8) Startup!?Dependency {
    var scratch = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer scratch.deinit();
    const bytes = std.Io.Dir.cwd().readFileAlloc(io, manifest_path, scratch.allocator(), .unlimited) catch |e| switch (e) {
        error.FileNotFound => return null,
        else => return fail(a, "DependencyFailure: {s}", .{@errorName(e)}),
    };
    ids.observe(bytes);
    const invalid = "DependencyFailure: Invalid dependency manifest or source path.";
    const text = if (std.mem.startsWith(u8, bytes, "\xEF\xBB\xBF")) bytes[3..] else bytes;
    const root = std.json.parseFromSliceLeaky(std.json.Value, scratch.allocator(), text, .{ .duplicate_field_behavior = .use_last }) catch return fail(a, invalid, .{});
    // `JsonDocument` keeps a repeated name, so its field list no longer matches any manifest form.
    // shortcut: decided before any member loads, where F# reports an earlier member's failure first.
    _ = std.json.parseFromSliceLeaky(std.json.Value, scratch.allocator(), text, .{ .duplicate_field_behavior = .@"error" }) catch return fail(a, "DependencyFailure: Invalid dependency manifest.", .{});
    if (root != .object) return fail(a, invalid, .{});
    const set = root.object.get("dependencies");
    if (set == null or root.object.count() != 1) return try loadMemberWithSource(io, a, root.object, source_override);
    if (source_override != null) return fail(a, "DependencyFailure: Pin with a source applies only to a single legacy dependency.", .{});
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
pub fn loadMember(io: std.Io, a: std.mem.Allocator, manifest: std.json.ObjectMap) Startup!Dependency {
    return loadMemberWithSource(io, a, manifest, null);
}

fn loadMemberWithSource(io: std.Io, a: std.mem.Allocator, manifest: std.json.ObjectMap, source_override: ?[]const u8) Startup!Dependency {
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
    const dependency = if (embedded_key != null and source_override == null) blk: {
        const key = embedded_key.?;
        const content = switch (manifest.get(key).?) {
            .string => |text| text,
            .null => "",
            else => return fail(a, invalid, .{}),
        };
        if (std.mem.trim(u8, content, " \t\r\n").len == 0) return fail(a, "DependencyFailure: The embedded dependency is missing.", .{});
        break :blk try decodeDependency(a, std.mem.eql(u8, key, "snapshotRecords"), content, values[0].?, true);
    } else blk: {
        const source_path = if (source_override) |given| try std.fs.path.resolveAlloc(a, &.{ (std.process.currentPathAlloc(io, a) catch |err| return fail(a, "IoFailure: {s}", .{@errorName(err)})), given }) else values[0].?;
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
pub fn combineMembers(a: std.mem.Allocator, members: []const Dependency) Startup!Dependency {
    var fingerprints_sorted: std.ArrayList([]const u8) = .empty;
    const tm = model_reader.session_model;
    var inputs: std.ArrayList(tm.dependencies.Member) = .empty;
    for (members) |member| {
        fingerprints_sorted.append(a, member.fingerprint) catch return fail(a, "out of memory", .{});
        inputs.append(a, .{ .generation = member.generation.?, .interner = member.interner.? }) catch return fail(a, "out of memory", .{});
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
    const interner = try a.create(tm.Interner);
    interner.* = .{ .gpa = a };
    switch (tm.dependencies.combine(a, a, interner, inputs.items, std.unicode.utf8ToUtf16LeAlloc(a, id) catch return error.OutOfMemory) catch return fail(a, "DependencyFailure: Could not combine dependency declarations.", .{})) {
        .conflict => |conflict| {
            var providers: std.ArrayList(u8) = .empty;
            for (conflict.names, members) |name, member| if (name) |display| {
                if (providers.items.len > 0) providers.appendSlice(a, "; ") catch {};
                providers.print(a, "'{s}' from providing pin '{s}' (fingerprint {s})", .{ display, member.id, member.fingerprint }) catch {};
            };
            const context = if (providers.items.len == 0) "" else std.fmt.allocPrint(a, " Providing declarations: {s}.", .{providers.items}) catch "";
            return fail(a, "DependencyFailure: Dependencies declare the {s} '{s}' with different content; colliding imported identities are never merged.{s}", .{ conflict.kind, conflict.id, context });
        },
        .generation => |combined| {
            const generation = try a.create(tm.Generation);
            generation.* = combined;
            const validation = model_reader.validateTyped(a, interner, generation);
            if (!validation.valid) return fail(a, "SnapshotCodecFailure: {s}", .{validation.lines});
            const print = model_reader.session_writer.fingerprints(a, interner, generation) catch return fail(a, "SnapshotCodecFailure: The snapshot could not be fingerprinted.", .{});
            var canonical: std.Io.Writer.Allocating = .init(a);
            model_reader.session_writer.writeDocument(&canonical.writer, interner, generation, model_reader.currentVersion()) catch return error.OutOfMemory;
            var canonical_digest: [32]u8 = undefined;
            std.crypto.hash.sha2.Sha256.hash(print.canonical, &canonical_digest, .{});
            var sources: std.ArrayList(wire.Provenance.Member) = .empty;
            for (members) |member| try sources.append(a, member.provenance.members[0]);
            const keys = model_reader.typedDeclarationKeys(a, model_reader.typedRoot(a, interner, generation));
            return .{ .generation = generation, .interner = interner, .canonical = canonical.written(), .id = id, .digest = try std.fmt.allocPrint(a, "{x}", .{canonical_digest}), .fingerprint = print.snapshot, .subjects = tm.dependencies.subjectsOf(a, generation, interner) catch return error.OutOfMemory, .provenance = .{ .members = sources.items, .hidden = keys.hidden }, .set = true };
        },
    }
}

pub fn main(init: std.process.Init) !void {
    ids.init(init.environ_map.get("NYRWVY_TEST_ID_SEED"));
    test_fail_operation = init.environ_map.get("NYRWVY_TEST_HOST_FAIL_OPERATION");
    const process_arena = init.arena.allocator();
    host_io = init.io;
    const all = try init.minimal.args.toSlice(process_arena);
    for (all[1..]) |arg| if (std.mem.eql(u8, arg, "--allow-host-sources")) {
        host_sources_allowed = true;
    };
    // `CommandLine.parse`: `--allow-host-sources` anywhere, then exactly one of the open forms.
    var args: std.ArrayList([]const u8) = .empty;
    for (all[1..]) |arg| if (!std.mem.eql(u8, arg, "--allow-host-sources")) try args.append(process_arena, arg);
    if (args.items.len > 0 and std.mem.eql(u8, args.items[0], "bind-c")) {
        var output_buffer: [4096]u8 = undefined;
        var output = std.Io.File.stdout().writer(init.io, &output_buffer);
        var error_buffer: [4096]u8 = undefined;
        var errors = std.Io.File.stderr().writer(init.io, &error_buffer);
        const code = try @import("bind_c.zig").run(process_arena, init.io, args.items[1..], &output.interface, &errors.interface);
        try output.interface.flush();
        try errors.interface.flush();
        if (code != 0) std.process.exit(code);
        return;
    }
    const command = try @import("startup_cli.zig").parse(process_arena, args.items) orelse {
        std.debug.print("{s}\n", .{@import("startup_cli.zig").usage});
        std.process.exit(2);
    };
    try @import("run_process.zig").init(process_arena, init.io, init.environ_map);
    try @import("run_csharp.zig").init(process_arena, init.io, init.environ_map);
    defer @import("run_csharp.zig").deinit(init.io);
    @import("startup_cli.zig").start(init.io, process_arena, command) catch |err| switch (err) {
        error.Startup => startupFailure("{s}", .{failure_line}),
        else => startupFailure("IoFailure: {s}", .{@errorName(err)}),
    };

    var in_buffer: [1 << 16]u8 = undefined;
    var reader = std.Io.File.stdin().reader(init.io, &in_buffer);
    var line: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
    var out_buffer: [1 << 16]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &out_buffer);
    const out = &writer.interface;
    defer proposals.deinit();
    defer freeRetained();
    while (true) {
        line.clearRetainingCapacity();
        _ = try reader.interface.streamDelimiterEnding(&line.writer, '\n');
        const ended = reader.interface.takeByte() == error.EndOfStream;
        if (ended and line.written().len == 0) break;
        // A request's allocations live until its response is written.
        var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
        defer arena.deinit();
        var response: std.Io.Writer.Allocating = .init(arena.allocator());
        var request_id: ?[]const u8 = null;
        const previous_proposals = proposals.count();
        execute(arena.allocator(), &response.writer, std.mem.trimEnd(u8, line.written(), "\r"), &request_id) catch {
            proposals.discardAfter(previous_proposals);
            response.clearRetainingCapacity();
            try failure(out, request_id, "InternalError", "Unexpected internal failure; this request was not applied and the session is unchanged.");
        };
        try out.writeAll(response.written());
        try out.writeByte('\n');
        try out.flush();
        if (ended) break;
    }
}

test "dependency pair restores a failed snapshot write and preserves recovery on rollback failure" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var temporary = std.testing.tmpDir(.{});
    defer temporary.cleanup();
    host_io = std.testing.io;
    const directory = try temporary.dir.realPathFileAlloc(host_io, ".", a);
    snapshot_path = try std.fs.path.join(a, &.{ directory, "app.json" });
    const sidecar = try std.mem.concat(a, u8, &.{ snapshot_path, ".dependency.json" });
    const previous = try std.mem.concat(a, u8, &.{ sidecar, ".previous" });
    persisted_source = "old-source";
    dependency_manifest = "old-manifest";
    selection = .{ .canonical = "", .id = "", .digest = "", .fingerprint = "", .subjects = &.{}, .provenance = .{ .members = &.{.{ .id = "", .fingerprint = "", .keys = &.{}, .path = "", .embedded = true }}, .hidden = &.{} } };
    defer {
        snapshot_path = "";
        persisted_source = "";
        dependency_manifest = null;
        selection = null;
        invalidated = false;
    }
    const Fault = struct {
        var rollback_failure = false;
        var sidecar_failure = false;
        var snapshot_error = false;
        fn replace(allocator: std.mem.Allocator, path: []const u8, content: []const u8) !?query.Rejection {
            if (std.mem.eql(u8, path, snapshot_path)) {
                const manifest_path = try std.mem.concat(allocator, u8, &.{ snapshot_path, ".dependency.json" });
                try std.testing.expectEqualStrings("new-manifest", try readText(allocator, manifest_path));
                if (snapshot_error) return error.InjectedFailure;
                return .{ .code = "IoFailure", .message = "snapshot write failed" };
            }
            if (std.mem.endsWith(u8, path, ".dependency.json")) {
                if (sidecar_failure) return .{ .code = "IoFailure", .message = "sidecar write failed" };
                if (rollback_failure and std.mem.eql(u8, content, "old-manifest")) return .{ .code = "IoFailure", .message = "rollback failed" };
            }
            return replaceAtomically(allocator, path, content);
        }
    };
    for ([_]u8{ 0, 1, 2, 3 }) |phase| {
        invalidated = false;
        Fault.sidecar_failure = phase == 0;
        Fault.rollback_failure = phase == 2;
        Fault.snapshot_error = phase == 3;
        try std.testing.expect((try replaceAtomically(a, snapshot_path, persisted_source)) == null);
        try std.testing.expect((try replaceAtomically(a, sidecar, dependency_manifest.?)) == null);
        const problem = (try replaceDependencyPairUsing(a, "new-source", "new-manifest", Fault.replace)).?;
        try std.testing.expectEqualStrings("IoFailure", problem.code);
        try std.testing.expectEqualStrings("old-source", try readText(a, snapshot_path));
        if (phase == 2) {
            try std.testing.expect(std.mem.indexOf(u8, problem.message, "Restoring the previous dependency selection also failed: rollback failed") != null);
            try std.testing.expectEqualStrings("old-manifest", try readText(a, previous));
            try std.testing.expectEqualStrings("new-manifest", try readText(a, sidecar));
            try std.testing.expectEqualStrings("DependencyFailure", (try verifyPersisted(a)).?.code);
            try std.testing.expect(invalidated);
        } else {
            try std.testing.expectEqualStrings(if (phase == 0) "sidecar write failed" else if (phase == 3) "InjectedFailure" else "snapshot write failed", problem.message);
            try std.testing.expectEqualStrings("old-manifest", try readText(a, sidecar));
            try std.testing.expectError(error.FileNotFound, readText(a, previous));
            try std.testing.expect((try verifyPersisted(a)) == null);
            try std.testing.expect(!invalidated);
        }
    }
}
