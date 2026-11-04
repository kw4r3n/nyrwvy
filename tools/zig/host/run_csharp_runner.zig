//! Type-directed C# runner: arguments remain input tokens, never source literals.
const std = @import("std");
const model = @import("model_reader");
const q = @import("query.zig");
const v = @import("run_value.zig");
const infer = @import("infer.zig");
const Type = @import("recipe.zig").TypeRef;
const same = @import("materialize.zig").same;
const Fail = anyerror;

pub fn name(a: std.mem.Allocator, prefix: []const u8, id: []const u8) ![]const u8 {
    const hex = try a.alloc(u8, id.len * 2);
    const digits = "0123456789ABCDEF";
    for (id, 0..) |b, i| {
        hex[i * 2] = digits[b >> 4];
        hex[i * 2 + 1] = digits[b & 15];
    }
    return std.mem.concat(a, u8, &.{ prefix, hex });
}
const Generator = struct {
    r: *q.Request,
    pending: std.ArrayList(Type) = .empty,
    required: std.ArrayList(u8) = .empty,
    serial: usize = 0,
    const Mode = enum(u3) { decode = 1, key = 2, encode = 4 };
    fn fmt(s: *Generator, comptime f: []const u8, args: anytype) ![]const u8 {
        return std.fmt.allocPrint(s.r.a, f, args);
    }
    fn fresh(s: *Generator) ![]const u8 {
        s.serial += 1;
        return s.fmt("v{d}", .{s.serial});
    }
    fn join(s: *Generator, items: []const []const u8) ![]const u8 {
        return std.mem.join(s.r.a, ", ", items);
    }
    fn nm(s: *Generator, prefix: []const u8, id: []const u8) ![]const u8 {
        return name(s.r.a, prefix, id);
    }
    fn ty(s: *Generator, t: Type) Fail![]const u8 {
        return switch (t) {
            .primitive => |p| if (same(p, "string")) "NyStr" else if (same(p, "bool")) "bool" else if (same(p, "int64")) "long" else if (same(p, "float64")) "double" else if (same(p, "bits32")) "uint" else if (same(p, "bytes")) "NyBytes" else "NyUnit",
            .capability => |id| s.nm("NyC_", id),
            .type_parameter => |id| s.nm("T_", id),
            .named => |n| std.mem.concat(s.r.a, u8, &.{ try s.nm("NyT_", n.id), try s.suffix(n.arguments) }),
            .builtin => |b| blk: {
                if (same(b.generic, "exception")) break :blk "System.Runtime.ExceptionServices.ExceptionDispatchInfo";
                if (same(b.generic, "taskScope")) break :blk "NyTaskScope";
                const prefix = if (same(b.generic, "list")) "List" else if (same(b.generic, "option")) "Option" else if (same(b.generic, "result")) "Result" else if (same(b.generic, "set")) "Set" else if (same(b.generic, "map")) "Map" else if (same(b.generic, "task")) "Task" else if (same(b.generic, "oneShot")) "OneShot" else return error.UnsupportedRunnerType;
                break :blk s.fmt("Ny{s}{s}", .{ prefix, try s.suffix(b.arguments) });
            },
            .function => |f| blk: {
                const args = try s.r.a.alloc(Type, f.parameters.len + 1);
                @memcpy(args[0..f.parameters.len], f.parameters);
                args[f.parameters.len] = f.result.*;
                break :blk s.fmt("NyFunction{d}{s}", .{ f.parameters.len, try s.suffix(args) });
            },
        };
    }
    fn suffix(s: *Generator, args: []const Type) Fail![]const u8 {
        if (args.len == 0) return "";
        const parts = try s.r.a.alloc([]const u8, args.len);
        for (args, parts) |t, *p| p.* = try s.ty(t);
        return s.fmt("<{s}>", .{try s.join(parts)});
    }
    fn namedIndex(s: *Generator, t: Type, mode: Mode) !usize {
        for (s.pending.items, 0..) |p, i| if (infer.eqlType(p, t)) {
            s.required.items[i] |= @backingInt(mode);
            return i;
        };
        try s.pending.append(s.r.a, t);
        try s.required.append(s.r.a, @backingInt(mode));
        return s.pending.items.len - 1;
    }
    fn dec(s: *Generator, t: Type) Fail![]const u8 {
        switch (t) {
            .primitive => |p| return if (same(p, "unit")) "input.Unit()" else if (same(p, "string")) "input.String()" else if (same(p, "bool")) "input.Bool()" else if (same(p, "int64")) "input.Long()" else if (same(p, "float64")) "input.Double()" else if (same(p, "bits32")) "input.UInt()" else "NyBytes.FromLatin1(input.String())",
            .named => return s.fmt("RunnerValues.Decode{d}(input)", .{try s.namedIndex(t, .decode)}),
            .builtin => |b| {
                const item = try s.ty(b.arguments[0]);
                const d = try s.dec(b.arguments[0]);
                const full = try s.ty(t);
                if (same(b.generic, "list")) return s.fmt("NyList<{s}>.Create(input.Items<{s}>(() => {s}))", .{ item, item, d });
                if (same(b.generic, "option")) return s.fmt("(input.Bool() ? {s}.Some({s}) : {s}.None())", .{ full, d, full });
                if (same(b.generic, "set")) return s.fmt("{s}.FromList(NyList<{s}>.Create(input.Items<{s}>(() => {s})))", .{ full, item, item, d });
                const d2 = try s.dec(b.arguments[1]);
                if (same(b.generic, "result")) return s.fmt("(input.Bool() ? {s}.Ok({s}) : {s}.Err({s}))", .{ full, d, full, d2 });
                return s.fmt("System.Linq.Enumerable.Aggregate(input.Items<({s}, {s})>(() => ({s}, {s})), {s}.Empty(), (map, entry) => map.Insert(entry.Item1, entry.Item2))", .{ item, try s.ty(b.arguments[1]), d, d2, full });
            },
            else => return "throw new RunnerException(\"A fake cannot return a capability value.\")",
        }
    }
    fn enc(s: *Generator, t: Type, x: []const u8, depth: []const u8) Fail![]const u8 {
        var expr: []const u8 = undefined;
        switch (t) {
            .primitive => |p| {
                expr = if (same(p, "unit")) "\"unit\"" else if (same(p, "string")) try s.fmt("CheckedString({s})", .{x}) else if (same(p, "bytes")) try s.fmt("CheckedString({s}.ToLatin1())", .{x}) else if (same(p, "float64")) try s.fmt("(double.IsNaN({s}) ? 0x7ff8000000000000UL : BitConverter.DoubleToUInt64Bits({s})).ToString(\"x16\", CultureInfo.InvariantCulture)", .{ x, x }) else try s.fmt("(object){s}", .{x});
            },
            .named => expr = try s.fmt("Encode{d}({s}, {s})", .{ try s.namedIndex(t, .encode), x, depth }),
            .builtin => |b| {
                const child_depth = try s.fmt("{s} + 1", .{depth});
                if (same(b.generic, "option")) {
                    expr = try s.fmt("({s}.HasValue ? new object[] {{ {s} }} : Array.Empty<object>())", .{ x, try s.enc(b.arguments[0], try s.fmt("{s}.Value", .{x}), child_depth) });
                } else if (same(b.generic, "result")) {
                    expr = try s.fmt("({s}.IsOk ? new object[] {{ true, {s} }} : new object[] {{ false, {s} }})", .{ x, try s.enc(b.arguments[0], try s.fmt("{s}.OkValue", .{x}), child_depth), try s.enc(b.arguments[1], try s.fmt("{s}.ErrorValue", .{x}), child_depth) });
                } else {
                    const acc = try s.fresh();
                    const item = try s.fresh();
                    var pair: []const u8 = undefined;
                    var parameters: []const u8 = undefined;
                    if (same(b.generic, "map")) {
                        const val = try s.fresh();
                        pair = try s.fmt("new object[] {{ {s}, {s} }}", .{ try s.enc(b.arguments[0], item, child_depth), try s.enc(b.arguments[1], val, child_depth) });
                        parameters = try s.fmt("{s}, {s}, {s}", .{ acc, item, val });
                    } else {
                        pair = try s.enc(b.arguments[0], item, child_depth);
                        parameters = try s.fmt("{s}, {s}", .{ acc, item });
                    }
                    const label = if (same(b.generic, "map")) "Map result exceeds 4096 entries." else if (same(b.generic, "set")) "Set result exceeds 4096 items." else "List result exceeds 4096 items.";
                    expr = try s.fmt("{s}.Fold(new System.Collections.Generic.List<object>(), ({s}) => {{ if ({s}.Count >= 4096) throw new RunnerException(\"{s}\"); {s}.Add({s}); return {s}; }})", .{ x, parameters, acc, label, acc, pair, acc });
                }
                expr = try s.fmt("({s} >= 32 ? throw new RunnerException(\"Result exceeds 32 composite layers.\") : {s})", .{ depth, expr });
            },
            else => return error.UnsupportedRunnerType,
        }
        return s.fmt("Count({s})", .{expr});
    }
    fn key(s: *Generator, t: Type, x: []const u8) Fail![]const u8 {
        switch (t) {
            .primitive => |p| return if (same(p, "unit")) "key.Add(\"u\");" else if (same(p, "string")) s.fmt("key.String({s});", .{x}) else if (same(p, "bytes")) s.fmt("key.String({s}?.ToLatin1());", .{x}) else if (same(p, "float64")) s.fmt("key.Double({s});", .{x}) else if (same(p, "bool")) s.fmt("key.Add({s} ? \"1\" : \"0\");", .{x}) else s.fmt("key.Add((long){s});", .{x}),
            .capability => return "key.Add(\"c\");",
            .named => return s.fmt("RunnerValues.Key{d}(key, {s});", .{ try s.namedIndex(t, .key), x }),
            .builtin => |b| {
                if (same(b.generic, "option")) return s.fmt("if ({s}.HasValue) {{ key.Add(\"1\"); {s} }} else key.Add(\"0\");", .{ x, try s.key(b.arguments[0], try s.fmt("{s}.Value", .{x})) });
                if (same(b.generic, "result")) return s.fmt("if ({s}.IsOk) {{ key.Add(\"1\"); {s} }} else {{ key.Add(\"0\"); {s} }}", .{ x, try s.key(b.arguments[0], try s.fmt("{s}.OkValue", .{x})), try s.key(b.arguments[1], try s.fmt("{s}.ErrorValue", .{x})) });
                const acc = try s.fresh();
                const item = try s.fresh();
                var parameters = try s.fmt("{s}, {s}", .{ acc, item });
                var body = try s.key(b.arguments[0], item);
                if (same(b.generic, "map")) {
                    const val = try s.fresh();
                    parameters = try s.fmt("{s}, {s}, {s}", .{ acc, item, val });
                    body = try std.mem.concat(s.r.a, u8, &.{ body, try s.key(b.arguments[1], val) });
                }
                return s.fmt("if ({s} == null) key.Null(); else {{ key.Add({s}.{s}); {s}.Fold(0, ({s}) => {{ {s} return {s}; }}); }}", .{ x, x, if (same(b.generic, "list")) "Length" else "Size", x, parameters, body, acc });
            },
            else => return error.UnsupportedRunnerType,
        }
    }
    fn fields(s: *Generator, owner: model.Model, args: []const Type, fields_: []const model.Model, x: []const u8, mode: Mode) Fail![]const u8 {
        var out: std.ArrayList([]const u8) = .empty;
        for (fields_) |field| {
            const t = try v.instantiated(s.r, owner, args, field);
            const access = try s.fmt("{s}.{s}", .{ x, try s.nm("f_", q.idOf(field)) });
            try out.append(s.r.a, switch (mode) {
                .decode => try s.fmt("{s}: {s}", .{ try s.nm("f_", q.idOf(field)), try s.dec(t) }),
                .encode => try s.enc(t, access, "depth + 1"),
                .key => try s.key(t, access),
            });
        }
        return std.mem.join(s.r.a, if (mode == .key) " " else ", ", out.items);
    }
    fn namedHelper(s: *Generator, index: usize, mode: Mode, out: *std.Io.Writer) Fail!void {
        const t = s.pending.items[index];
        const n = t.named;
        const owner = v.find(s.r.typedModel().types(), n.id).?;
        const full = try s.ty(t);
        var body: []const u8 = undefined;
        const variants = owner.variants();
        if (owner.data.type_declaration.product) {
            const fields_ = try v.sorted(s.r.a, owner.fields());
            const fs = try s.fields(owner, n.arguments, fields_, "value", mode);
            body = switch (mode) {
                .decode => try s.fmt("new {s}({s})", .{ full, fs }),
                .encode => try s.fmt("new object[] {{ {s} }}", .{fs}),
                .key => try s.fmt("if ((object)value == null) {{ key.Null(); return; }} {s}", .{fs}),
            };
        } else {
            var text: std.Io.Writer.Allocating = .init(s.r.a);
            if (mode == .decode) try text.writer.writeAll("input.Count() switch { ");
            if (mode == .key) try text.writer.writeAll("switch(value) { ");
            for (try v.sorted(s.r.a, variants), 0..) |variant, i| {
                const vn = try std.mem.concat(s.r.a, u8, &.{ try s.nm("NyV_", q.idOf(variant)), try s.suffix(n.arguments) });
                const fields_ = try v.sorted(s.r.a, variant.fields());
                const local = try s.fresh();
                const fs = try s.fields(owner, n.arguments, fields_, local, mode);
                switch (mode) {
                    .decode => try text.writer.print("{d} => ({s})new {s}({s}), ", .{ i, full, vn, fs }),
                    .key => try text.writer.print("case {s} {s}: key.Add({d}); {s} break; ", .{ vn, local, i, fs }),
                    .encode => {
                        var tag: std.Io.Writer.Allocating = .init(s.r.a);
                        try @import("main.zig").writeString(&tag.writer, q.idOf(variant));
                        try text.writer.print("(value is {s} {s} ? new object[] {{ {s}, new object[] {{ {s} }} }} : ", .{ vn, local, tag.written(), fs });
                    },
                }
            }
            switch (mode) {
                .decode => try text.writer.writeAll("_ => throw new InvalidOperationException(\"Invalid runner input.\") }"),
                .key => try text.writer.writeAll("default: key.Null(); break; }"),
                .encode => {
                    try text.writer.writeAll("throw new RunnerException(\"Unknown or null Union variant.\")");
                    for (variants) |_| try text.writer.writeByte(')');
                },
            }
            body = text.written();
        }
        switch (mode) {
            .decode => try out.print("public static {s} Decode{d}(RunnerInput input) => {s};\n", .{ full, index, body }),
            .key => try out.print("public static void Key{d}(RunnerKey key, {s} value) {{ {s} }}\n", .{ index, full, body }),
            .encode => try out.print("object Encode{d}({s} value, int depth) => depth >= 32 ? throw new RunnerException(\"Result exceeds 32 composite layers.\") : {s};\n", .{ index, full, body }),
        }
    }
};

pub fn source(r: *q.Request, input: *v.Input, concurrent: bool) Fail![]const u8 {
    const original = r.generation;
    r.generation = input.declarations.data.snapshot;
    defer r.generation = original;
    var s: Generator = .{ .r = r };
    var classes: std.Io.Writer.Allocating = .init(r.a);
    var arms: std.Io.Writer.Allocating = .init(r.a);
    var args: std.ArrayList([]const u8) = .empty;
    var site: usize = 0;
    for (input.fn_value.?.parameters(), input.arguments, 0..) |parameter, arg, index| {
        if (arg.kind != .fake and arg.kind != .host) {
            try args.append(r.a, try s.dec(try infer.typeOf(r.a, parameter.declaredType())));
            continue;
        }
        const cap = v.find(r.typedModel().capabilities(), arg.type.capability).?;
        const interface = try s.nm("NyC_", arg.type.capability);
        const host = arg.kind == .host;
        const label = if (host) "Host" else "Fake";
        try args.append(r.a, try s.fmt("new {s}{d}({s})", .{ label, index, if (host) "" else "input" }));
        try classes.writer.print("sealed class {s}{d} : {s}\n{{\n", .{ label, index, interface });
        if (host) try classes.writer.print("readonly {s} inner = new {s}();\n", .{ interface, arg.text });
        var init: std.Io.Writer.Allocating = .init(r.a);
        for (try v.sorted(r.a, cap.operations())) |op| {
            const op_name = try s.nm("Op_", q.idOf(op));
            const rt = try infer.typeOf(r.a, op.resultType());
            const result_type = try s.ty(rt);
            var ps: std.ArrayList([]const u8) = .empty;
            var names: std.ArrayList([]const u8) = .empty;
            var objects: std.ArrayList([]const u8) = .empty;
            var call_encoded: std.ArrayList([]const u8) = .empty;
            var keys: std.Io.Writer.Allocating = .init(r.a);
            for (op.parameters(), 0..) |p, pi| {
                const t = try infer.typeOf(r.a, p.declaredType());
                const pn = try s.nm("cp_", q.idOf(p));
                try ps.append(r.a, try s.fmt("{s} {s}", .{ try s.ty(t), pn }));
                try names.append(r.a, pn);
                try objects.append(r.a, try s.fmt("(object){s}", .{pn}));
                if (try v.supported(r, t, 0)) try call_encoded.append(r.a, try s.enc(t, try s.fmt("(({s})arguments[{d}])", .{ try s.ty(t), pi }), "0"));
            }
            for (try v.sorted(r.a, op.parameters())) |p| try keys.writer.writeAll(try s.key(try infer.typeOf(r.a, p.declaredType()), try s.nm("cp_", q.idOf(p))));
            try arms.writer.print("{d} => new object[] {{ {d}, new object[] {{ {s} }} }},\n", .{ site, site, try s.join(call_encoded.items) });
            if (!host) {
                try classes.writer.print("readonly FakeScript<{s}> {s}Script;\n", .{ result_type, op_name });
                try init.writer.print("{s}Script = new FakeScript<{s}>(input, () => {s});\n", .{ op_name, result_type, if (try v.supported(r, rt, 0)) try s.dec(rt) else "throw new RunnerException(\"A fake cannot return a capability value.\")" });
            }
            try classes.writer.print("public {s} {s}({s}) {{\n", .{ result_type, op_name, try s.join(ps.items) });
            if (host) {
                try classes.writer.print("FakeLog.Record({d}, new object[] {{ {s} }}); return inner.{s}({s});\n", .{ site, try s.join(objects.items), op_name, try s.join(names.items) });
            } else {
                try classes.writer.print("FakeResponse<{s}> response; lock (FakeLog.Calls) {{ FakeLog.Record({d}, new object[] {{ {s} }}); response = {s}Script.Select({s}Script.Keyed ? RunnerKey.Of({s}Script.KeyLimit, key => {{ {s} }}) : null); }}\nwhile (true) {{ switch (response.Kind) {{ case 0: return response.Value; case 1: throw new FakeCapabilityException(response.Message);\n", .{ result_type, site, try s.join(objects.items), op_name, op_name, op_name, keys.written() });
                if (concurrent) try classes.writer.writeAll("case 2: NyRuntime.WaitForCancellation(); return response.Value; case 3: NyRuntime.Delay(response.Delay); response = response.Inner!; break;\n");
                try classes.writer.writeAll("case 4: throw new FakeArgumentsUnmatchedException(); default: throw new FakeResponsesExhaustedException(); } }\n");
            }
            try classes.writer.writeAll("}\n");
            site += 1;
        }
        if (!host) try classes.writer.print("public Fake{d}(RunnerInput input) {{ {s} }}\n", .{ index, init.written() });
        try classes.writer.writeAll("}\n");
    }
    const encoded = try s.enc(try infer.typeOf(r.a, input.fn_value.?.resultType()), "result", "0");
    var encoders: std.Io.Writer.Allocating = .init(r.a);
    var helpers: std.Io.Writer.Allocating = .init(r.a);
    inline for (.{ Generator.Mode.encode, Generator.Mode.decode, Generator.Mode.key }) |mode| {
        var next: usize = 0;
        while (next < s.pending.items.len) : (next += 1) {
            if (s.required.items[next] & @backingInt(mode) != 0) try s.namedHelper(next, mode, if (mode == .encode) &encoders.writer else &helpers.writer);
        }
    }
    const checks_text = std.mem.indexOf(u8, encoded, "CheckedString(") != null or std.mem.indexOf(u8, arms.written(), "CheckedString(") != null or std.mem.indexOf(u8, encoders.written(), "CheckedString(") != null;
    var out: std.Io.Writer.Allocating = .init(r.a);
    try out.writer.writeAll("using System; using System.Globalization; using System.Linq; using System.Text; using System.Text.Json; using Nyrwvy.Generated;\n");
    // PR_SET_PDEATHSIG fires when the forking thread exits; the host spawns from its serving task, which lives as long as the host.
    try out.writer.writeAll("if (OperatingSystem.IsLinux()) { RunnerProcess.prctl(4, 0, 0, 0, 0); RunnerProcess.prctl(1, 9, 0, 0, 0); }\nvar input = new RunnerInput(System.IO.File.ReadAllText(args[0]));\nvar options = new JsonSerializerOptions { MaxDepth = 256 };\nint remaining = 8192;\n");
    if (checks_text) try out.writer.writeAll("long textRemaining = 65536;\n");
    try out.writer.writeAll("try {\n");
    if (concurrent) try out.writer.writeAll("NyRuntime.Clock = new NyVirtualClock();\n");
    try out.writer.print("var result = NyFunctions.{s}({s});\nConsole.WriteLine(JsonSerializer.Serialize(new object[] {{ \"value\", {s}, EncodeCalls() }}, options));\n", .{ try s.nm("NyF_", input.function_id), try s.join(args.items), encoded });
    try out.writer.writeAll("} catch (RunnerException error) { Console.WriteLine(JsonSerializer.Serialize(new object[] { \"limit\", error.Message })); }\ncatch (Exception error) {\nvar frames = (new System.Diagnostics.StackTrace(error, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>()).Select(frame => frame.GetMethod()?.Name ?? \"\").ToArray();\ntry { Console.WriteLine(JsonSerializer.Serialize(new object[] { \"exception\", error.GetType().FullName ?? \"\", RunnerText.ToToken(error.Message), frames, EncodeCalls(), error is FakeResponsesExhaustedException, error is OutOfMemoryException or InsufficientExecutionStackException ? \"fatal\" : error is OperationCanceledException ? \"cancelled\" : \"fault\" }, options)); } catch (RunnerException limit) { Console.WriteLine(JsonSerializer.Serialize(new object[] { \"limit\", limit.Message })); } }\nobject[] EncodeCalls() { remaining = 8192;\n");
    if (checks_text) try out.writer.writeAll("textRemaining = 65536;\n");
    try out.writer.writeAll("return FakeLog.Calls.Select(call => EncodeCall(call.Site, call.Arguments)).ToArray(); }\nobject EncodeCall(int site, object[] arguments) => site switch {\n");
    try out.writer.writeAll(arms.written());
    try out.writer.writeAll("_ => throw new RunnerException(\"Unknown capability call site.\"), };\nobject Count(object value) { if (--remaining < 0) throw new RunnerException(\"Result exceeds 8192 value nodes.\"); return value; }\n");
    if (checks_text) try out.writer.writeAll("string CheckedString(string value) { if (value == null) throw new RunnerException(\"Null String result.\"); textRemaining -= value.Length; if (textRemaining < 0) throw new RunnerException(\"Result exceeds 65536 String code units.\"); return RunnerText.ToToken(value); }\n");
    try out.writer.writeAll(encoders.written());
    try out.writer.writeAll(@embedFile("csharp/RunnerTail.cs"));
    try out.writer.writeAll(@embedFile("csharp/RunnerSupport.cs"));
    try out.writer.print("static class RunnerValues {{\n{s}}}\n{s}", .{ helpers.written(), classes.written() });
    return out.written();
}

test "C# runner keeps recursive codecs finite and argument values out of source" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const declarations = try model.parseValue(a,
        \\{"snapshotId":"s","types":[{"id":"Node","displayName":"Node","kind":"product","fields":[{"id":"next","displayName":"next","type":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"named","typeId":"Node"}]}}]}],"functions":[{"id":"echo","displayName":"echo","parameters":[{"id":"arg","displayName":"arg","type":{"kind":"named","typeId":"Node"}}],"resultType":{"kind":"named","typeId":"Node"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}]}
    );
    var interner: model.session_model.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try model.session_model.load(a, &interner, declarations, null);
    const typed = model.typedRoot(a, &interner, &generation);
    var r: q.Request = .{ .a = a, .line = "", .root = .empty, .generation = &generation, .interner = &interner, .provenance = null };
    var args = [_]v.RunValue{.{ .kind = .product, .type = .{ .named = .{ .id = "Node", .arguments = &.{} } }, .text = "NEVER_COMPILE_ARGUMENT_TEXT" }};
    var input: v.Input = .{ .function_id = "echo", .declarations = typed, .fn_value = typed.functions()[0], .arguments = &args, .expected = null, .host_sources = &.{} };
    const text = try source(&r, &input, false);
    try std.testing.expect(std.mem.indexOf(u8, text, "NEVER_COMPILE_ARGUMENT_TEXT") == null);
    try std.testing.expect(std.mem.indexOf(u8, text, "NyFunctions.NyF_6563686F(RunnerValues.Decode0(input))") != null);
    try std.testing.expect(std.mem.indexOf(u8, text, "public static NyT_4E6F6465 Decode0") != null);
    try std.testing.expect(std.mem.indexOf(u8, text, "object Encode0(NyT_4E6F6465 value, int depth)") != null);
    try std.testing.expect(std.mem.indexOf(u8, text, "depth >= 32") != null);
    try std.testing.expect(text.len < 20000);
}
