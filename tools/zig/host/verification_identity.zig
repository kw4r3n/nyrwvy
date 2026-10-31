const std = @import("std");
const q = @import("query.zig");
const compiler = @import("run_csharp_build.zig");
const emitter = @import("csharp_emitter");
const root = @import("root");

var executable_digest: ?[32]u8 = null;

fn executableDigest(io: std.Io) ![32]u8 {
    const file = try std.process.openExecutable(io, .{});
    defer file.close(io);
    var reader_buffer: [4096]u8 = undefined;
    var reader = file.reader(io, &reader_buffer);
    var buffer: [65536]u8 = undefined;
    var hash = std.crypto.hash.sha2.Sha256.init(.{});
    while (true) {
        const size = try reader.interface.readSliceShort(&buffer);
        hash.update(buffer[0..size]);
        if (size < buffer.len) break;
    }
    var result: [32]u8 = undefined;
    hash.final(&result);
    return result;
}

pub fn get(r: *q.Request) !std.json.Value {
    if (try compiler.ensureDriver(r.a, root.host_io)) |failure| {
        r.rejection = .{ .code = failure.code, .message = failure.message };
        return error.Rejected;
    }
    if (executable_digest == null) executable_digest = try executableDigest(root.host_io);
    const hex = std.fmt.bytesToHex(executable_digest.?, .upper);
    const identity = try std.mem.concat(r.a, u8, &.{ "zig-sha256:", &hex });
    // These components are linked into one executable, rather than four managed modules.
    return emitter.object(r.a, &.{ "version", "observationPolicy", "runtimeVersion", "buildContextFingerprint", "authoringModule", "semanticsModule", "executionModule", "backendModule" }, &.{
        emitter.string("nyrwvy-change-verification-v5"),
        emitter.string("canonical-result-and-ordered-fake-calls-v4; structured-run-diagnostics; canonical-explicit-expectations; sequential-only; mapped-recognized-candidate-only-failures-counterexample; other-failures-unknown; explicit-result-changes-only"),
        emitter.string(compiler.runtime_version),
        emitter.string(compiler.build_context_fingerprint),
        emitter.string(identity),
        emitter.string(identity),
        emitter.string(identity),
        emitter.string(identity),
    });
}

test "native executable identity is a stable digest of the running file" {
    const first = try executableDigest(std.testing.io);
    const second = try executableDigest(std.testing.io);
    try std.testing.expectEqualSlices(u8, &first, &second);
    try std.testing.expect(!std.mem.allEqual(u8, &first, 0));
}
