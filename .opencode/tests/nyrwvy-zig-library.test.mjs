import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { randomUUID } from "node:crypto"
import { openAuthoring, requestAuthoring, closeAuthoring } from "../../tools/nyrwvy-agent/authoring.ts"

// Library mode (docs/decisions/zig-backend-memory-ownership.md, first milestone): emit, build with
// `zig build-lib -dynamic`, link a Zig test against the shared library and call the exports.
const int64 = { kind: "primitive", primitive: "int64" }
const string = { kind: "primitive", primitive: "string" }
const list = item => ({ kind: "builtinGeneric", generic: "list", arguments: [item] })
const parameter = name => ({ kind: "parameter", name })

async function request(context, payload) {
  const reply = JSON.parse(await requestAuthoring(payload, context))
  assert.equal(reply.ok, true, JSON.stringify(reply.error))
  return reply.result
}
async function publish(context, payload) {
  const proposal = await request(context, payload)
  await request(context, { op: "commit", proposalId: proposal.proposalId })
}
function zig(args) {
  const result = spawnSync(process.env.NYRWVY_ZIG || "zig", args, { cwd: process.cwd(), encoding: "utf8", timeout: 300_000 })
  assert.equal(result.status, 0, `${args.join(" ")}\n${result.stdout}${result.stderr}`)
  return result.stderr
}

const harness = (narrowFirst) => `const std = @import("std");
extern fn ny_frame(rows: ?[*]const i64, row_lens: ?[*]const usize, row_count: usize, f0: i64, f1: i64, out: ?[*]i64, cap: usize, out_len: *usize, out_row_lens: ?[*]usize, row_cap: usize, out_rows: *usize) c_int;
extern fn ny_greet(name: ?[*]const u8, len: usize, out: ?[*]u8, cap: usize, out_len: *usize) c_int;
extern fn ny_doubled(text: ?[*]const u8, len: usize, out: ?[*]u8, cap: usize, out_len: *usize) c_int;
extern fn ny_arena_capacity() usize;
extern fn ny_last_failure(len: *usize) [*]const u8;
fn frame(rows: []const i64, lens: []const usize, out: []i64, out_lens: []usize, n: *usize, r: *usize) c_int {
    return ny_frame(rows.ptr, lens.ptr, lens.len, ${narrowFirst ? "7, 9" : "9, 7"}, out.ptr, out.len, n, out_lens.ptr, out_lens.len, r);
}
test "exports" {
    var doubled: [16]u8 = undefined;
    var doubled_len: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_doubled("ab", 2, &doubled, doubled.len, &doubled_len));
    try std.testing.expectEqualStrings("abab", doubled[0..doubled_len]);
    var out: [8]i64 = undefined;
    var out_lens: [4]usize = undefined;
    var n: usize = 0;
    var r: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), frame(&.{ 1, 2, 3 }, &.{ 2, 0, 1 }, &out, &out_lens, &n, &r));
    try std.testing.expectEqualSlices(i64, &.{ 1, 2, 3, 7, 9 }, out[0..n]);
    try std.testing.expectEqualSlices(usize, &.{ 2, 0, 1, 2 }, out_lens[0..r]);
    // Too small: status 3 with the needed sizes.
    try std.testing.expectEqual(@as(c_int, 3), frame(&.{ 1, 2, 3 }, &.{ 2, 0, 1 }, out[0..4], &out_lens, &n, &r));
    try std.testing.expectEqual(@as(usize, 5), n);
    try std.testing.expectEqual(@as(usize, 4), r);
    var text: [16]u8 = undefined;
    try std.testing.expectEqual(@as(c_int, 0), ny_greet("日本".ptr, "日本".len, &text, text.len, &n));
    try std.testing.expectEqualStrings("hi 日本", text[0..n]);
    // Text from C is checked: invalid UTF-8 is a fault, not a runtime panic.
    try std.testing.expectEqual(@as(c_int, 1), ny_greet("\xff".ptr, 1, &text, text.len, &n));
    var failure_len: usize = 0;
    try std.testing.expectEqualStrings("a0 is not UTF-8", ny_last_failure(&failure_len)[0..failure_len]);
    // Each call resets the arena, so repeated calls keep its capacity flat.
    const rows = try std.testing.allocator.alloc(i64, 300 * 100);
    defer std.testing.allocator.free(rows);
    @memset(rows, 1);
    var lens: [100]usize = undefined;
    @memset(&lens, 300);
    const big = try std.testing.allocator.alloc(i64, 300 * 100 + 2);
    defer std.testing.allocator.free(big);
    var big_lens: [101]usize = undefined;
    try std.testing.expectEqual(@as(c_int, 0), frame(rows, &lens, big, &big_lens, &n, &r));
    const capacity = ny_arena_capacity();
    for (0..500) |_| try std.testing.expectEqual(@as(c_int, 0), frame(rows, &lens, big, &big_lens, &n, &r));
    try std.testing.expectEqual(capacity, ny_arena_capacity());
}
`

const skip = process.platform !== "linux" ? "the harness links a Linux .so with -rpath"
  : /\.dll$/i.test(process.env.NYRWVY_HOST_EXECUTABLE ?? "") ? "emitZigLibrary exists only in the Zig session host" : false

const hostSource = `const std = @import("std");
pub fn readText(allocator: std.mem.Allocator, path: []const u8) ![]const u8 {
    if (std.mem.eql(u8, path, "missing")) return error.FileNotFound;
    return std.fmt.allocPrint(allocator, "read {s}", .{path});
}
pub fn env(_: std.mem.Allocator, name: []const u8) ?[]const u8 {
    return if (std.mem.eql(u8, name, "HOME")) "/home" else null;
}
pub fn size(_: std.mem.Allocator, data: []const u8) i64 {
    return @intCast(data.len);
}
pub fn boom(_: std.mem.Allocator) !i64 {
    return error.Boom;
}
`
const hostHarness = `const std = @import("std");
extern fn ny_readOr(path: ?[*]const u8, len: usize, out: ?[*]u8, cap: usize, out_len: *usize) c_int;
extern fn ny_readError(path: ?[*]const u8, len: usize, out: ?[*]u8, cap: usize, out_len: *usize) c_int;
extern fn ny_envOr(name: ?[*]const u8, len: usize, out: ?[*]u8, cap: usize, out_len: *usize) c_int;
extern fn ny_sizeOf(text: ?[*]const u8, len: usize, out: *i64) c_int;
extern fn ny_callBoom(out: *i64) c_int;
extern fn ny_callGone(out: *i64) c_int;
extern fn ny_last_failure(len: *usize) [*]const u8;
fn text(call: anytype, input: []const u8, buffer: []u8) ![]const u8 {
    var n: usize = 0;
    try std.testing.expectEqual(@as(c_int, 0), call(input.ptr, input.len, buffer.ptr, buffer.len, &n));
    return buffer[0..n];
}
test "table-bound capabilities" {
    var buffer: [32]u8 = undefined;
    try std.testing.expectEqualStrings("read a", try text(ny_readOr, "a", &buffer));
    try std.testing.expectEqualStrings("unused", try text(ny_readOr, "missing", &buffer));
    try std.testing.expectEqualStrings("FileNotFound", try text(ny_readError, "missing", &buffer));
    try std.testing.expectEqualStrings("none", try text(ny_readError, "a", &buffer));
    try std.testing.expectEqualStrings("/home", try text(ny_envOr, "HOME", &buffer));
    try std.testing.expectEqualStrings("none", try text(ny_envOr, "NOPE", &buffer));
    var out: i64 = 0;
    try std.testing.expectEqual(@as(c_int, 0), ny_sizeOf("abc", 3, &out));
    try std.testing.expectEqual(@as(i64, 3), out);
    var failure_len: usize = 0;
    try std.testing.expectEqual(@as(c_int, 1), ny_callBoom(&out));
    try std.testing.expectEqualStrings("Boom", ny_last_failure(&failure_len)[0..failure_len]);
    try std.testing.expectEqual(@as(c_int, 1), ny_callGone(&out));
    try std.testing.expectEqualStrings("This capability operation is unavailable on this backend.", ny_last_failure(&failure_len)[0..failure_len]);
}
`

test("the host library runs on Zig against the real OS", { timeout: 300000, skip }, () => {
  const check = spawnSync(process.execPath, ["libraries/host/check/run-zig.mjs"], { cwd: process.cwd(), encoding: "utf8", timeout: 280_000 })
  assert.equal(check.status, 0, check.stdout + check.stderr)
})

test("library mode binds capabilities to Zig declarations through a table", { timeout: 300000, skip }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "nyrwvy-zig-table-"))
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const snapshot = join(directory, "workspace.json")
  const fsType = { kind: "capability", name: "Fs" }
  const fsParameter = { displayName: "fs", type: fsType }
  const invoke = (operation, arguments_) => ({ kind: "capabilityInvoke", capability: "Fs", operation, arguments: arguments_, capabilityValue: parameter("fs") })
  const textArgument = (name, value) => [{ parameter: name, value: parameter(value) }]
  const literal = value => ({ kind: "stringLiteral", value })
  const resultString = { kind: "builtinGeneric", generic: "result", arguments: [string, string] }
  const optionString = { kind: "builtinGeneric", generic: "option", arguments: [string] }
  const exported = (displayName, resultType, parameters, body) => publish(context, { op: "simulateCreateFunctionByName", displayName, resultType, requiredEffects: ["io"], parameters: [fsParameter, ...parameters], body })
  try {
    await openAuthoring("create", snapshot, context)
    await publish(context, { op: "simulateCreateEffect", displayName: "io" })
    await publish(context, { op: "simulateCreateCapabilityByName", displayName: "Fs", operations: [
      { displayName: "readText", parameters: [{ displayName: "path", type: string }], resultType: resultString, effect: "io" },
      { displayName: "env", parameters: [{ displayName: "name", type: string }], resultType: optionString, effect: "io" },
      { displayName: "size", parameters: [{ displayName: "data", type: { kind: "primitive", primitive: "bytes" } }], resultType: int64, effect: "io" },
      { displayName: "boom", parameters: [], resultType: int64, effect: "io" },
      { displayName: "gone", parameters: [], resultType: int64, effect: "io" }] })
    await exported("readOr", string, [{ displayName: "path", type: string }],
      { kind: "resultGetOrDefault", result: invoke("readText", textArgument("path", "path")), default: literal("unused") })
    await exported("readError", string, [{ displayName: "path", type: string }],
      { kind: "optionGetOrDefault", option: { kind: "resultErrValue", value: invoke("readText", textArgument("path", "path")) }, default: literal("none") })
    await exported("envOr", string, [{ displayName: "name", type: string }],
      { kind: "optionGetOrDefault", option: invoke("env", textArgument("name", "name")), default: literal("none") })
    await exported("sizeOf", int64, [{ displayName: "text", type: string }],
      invoke("size", [{ parameter: "data", value: { kind: "bytesFromLatin1", value: parameter("text") } }]))
    await exported("callBoom", int64, [], invoke("boom", []))
    await exported("callGone", int64, [], invoke("gone", []))

    const sidecar = { targets: ["csharp"], exports: ["callBoom", "callGone", "envOr", "readError", "readOr", "sizeOf"], zigTables: ["host.zig.json"] }
    await writeFile(snapshot + ".backends.json", JSON.stringify(sidecar))
    await writeFile(join(directory, "host.zig"), hostSource)
    const operations = { readText: { declaration: "readText" }, env: { declaration: "env" }, size: { declaration: "size" }, boom: { declaration: "boom" }, gone: { unavailable: true } }
    const table = (changes = {}) => JSON.stringify({ format: "nyrwvy.zig-mapping", version: 1, source: "host.zig", capabilities: { Fs: { operations } }, ...changes })
    const refused = async (text, pattern) => {
      await writeFile(join(directory, "host.zig.json"), text)
      const reply = JSON.parse(await requestAuthoring({ op: "emitZigLibrary" }, context))
      assert.equal(reply.ok, false)
      assert.match(reply.error.message, pattern)
    }
    await refused(table({ capabilities: { Fs: { operations: { readText: operations.readText } } } }), /Fs\.\w+ is neither bound nor marked unavailable/)
    await refused(table({ capabilities: { Nope: { operations: {} } } }), /no capability is named 'Nope'/)
    await refused(table({ source: "../host.zig" }), /at or below the emitted file's directory/)
    await refused(table({ source: "absent.zig" }), /existing Zig file/)
    await refused(table({ capabilities: { Fs: { operations: { ...operations, boom: { declaration: "a.b" } } } } }), /declaration must be an identifier/)

    await writeFile(join(directory, "host.zig.json"), table())
    const emitted = await request(context, { op: "emitZigLibrary", output: "library.zig" })
    const library = join(directory, "liblibrary.so")
    zig(["build-lib", "-dynamic", "-lc", "-O", "ReleaseSafe", emitted.path, `-femit-bin=${library}`])
    await writeFile(join(directory, "harness.zig"), hostHarness)
    zig(["test", join(directory, "harness.zig"), library, "-lc", "-rpath", directory])
  } finally {
    await closeAuthoring(context).catch(() => undefined)
    await rm(directory, { recursive: true, force: true })
  }
})

test("library mode exports listed functions through the C ABI with bounded memory",{ timeout: 300000, skip }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "nyrwvy-zig-library-"))
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const snapshot = join(directory, "workspace.json")
  try {
    await openAuthoring("create", snapshot, context)
    await publish(context, { op: "simulateCreateTypeByName", displayName: "Grid", typeKind: { kind: "product", fields: [
      { displayName: "narrow", type: int64 }, { displayName: "wide", type: int64 }] } })
    const field = name => ({ kind: "fieldAccess", source: parameter("grid"), type: "Grid", field: name })
    await publish(context, { op: "simulateCreateFunctionByName", displayName: "frame", resultType: list(list(int64)), requiredEffects: [],
      parameters: [{ displayName: "rows", type: list(list(int64)) }, { displayName: "grid", type: { kind: "named", name: "Grid" } }],
      body: { kind: "listAppendItem", list: parameter("rows"), item: { kind: "listConstruct", itemType: int64, items: [field("narrow"), field("wide")] } } })
    await publish(context, { op: "simulateCreateFunctionByName", displayName: "greet", resultType: string, requiredEffects: [],
      parameters: [{ displayName: "name", type: string }],
      body: { kind: "stringConcat", left: { kind: "stringLiteral", value: "hi " }, right: parameter("name") } })
    // A program capability implementation works without a runner's fakes.
    await publish(context, { op: "simulateCreateEffect", displayName: "io" })
    await publish(context, { op: "simulateCreateCapabilityByName", displayName: "Text", operations: [
      { displayName: "double", parameters: [{ displayName: "text", type: string }], resultType: string, effect: "io" }] })
    await publish(context, { op: "simulateCreateFunctionByName", displayName: "twice", resultType: string, requiredEffects: [],
      parameters: [{ displayName: "text", type: string }],
      body: { kind: "stringConcat", left: parameter("text"), right: parameter("text") } })
    await publish(context, { op: "simulateCreateFunctionByName", displayName: "doubled", resultType: string, requiredEffects: ["io"],
      parameters: [{ displayName: "text", type: string }],
      body: { kind: "capabilityInvoke", capability: "Text", operation: "double", arguments: [{ parameter: "text", value: parameter("text") }],
        capabilityValue: { kind: "capabilityConstruct", capability: "Text", implementations: [{ operation: "double", function: "twice" }] } } })

    const missing = JSON.parse(await requestAuthoring({ op: "emitZigLibrary" }, context))
    assert.equal(missing.ok, false)
    assert.match(missing.error.message, /exports/)
    await writeFile(snapshot + ".backends.json", JSON.stringify({ targets: ["csharp"], exports: ["doubled", "frame", "greet"] }))
    const emitted = await request(context, { op: "emitZigLibrary", output: "frame.zig" })
    assert.deepEqual(emitted.exports.map(item => item.symbol), ["ny_doubled", "ny_frame", "ny_greet"])
    const source = await readFile(emitted.path, "utf8")
    assert.ok(source.startsWith("// Generated by the Nyrwvy Zig backend."))
    assert.ok(!source.includes("pub fn main"))

    // The ABI flattens Grid's fields in field-ID order.
    const grid = (await request(context, { op: "declarations" })).types.find(type => type.displayName === "Grid")
    const ids = grid.typeKind.fields.map(field => [field.id, field.displayName]).sort(([x], [y]) => x < y ? -1 : 1)
    assert.ok(source.includes(`/// a1 fields in this order: ${ids.map(([id]) => id).join(", ")}`))
    const library = join(directory, "libframe.so")
    zig(["build-lib", "-dynamic", "-lc", "-O", "ReleaseSafe", emitted.path, `-femit-bin=${library}`])
    await writeFile(join(directory, "harness.zig"), harness(ids[0][1] === "narrow"))
    zig(["test", join(directory, "harness.zig"), library, "-lc", "-rpath", directory])
  } finally {
    await closeAuthoring(context).catch(() => undefined)
    await rm(directory, { recursive: true, force: true })
  }
})
