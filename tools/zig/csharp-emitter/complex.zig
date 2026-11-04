const std = @import("std");
const b = @import("root.zig");
const f = @import("functions.zig");
const Value = b.Value;
const Context = b.Context;
const Writer = b.Writer;
fn v(a: b.Allocator, i: Value, key: []const u8) ![]const u8 {
    return b.valueName(a, b.str(i, key));
}
fn line(w: *Writer, parts: []const []const u8) !void {
    try w.line(try b.cat(w.a, parts));
}
pub fn emit(w: *Writer, c: Context, fn_: Value, i: Value) anyerror!void {
    const a = w.a;
    const op = b.kind(i);
    const target = try b.valueName(a, b.str(b.get(i, "result"), "id"));
    const rt = try b.formatType(a, b.get(b.get(i, "result"), "type"));
    const context = b.arr(i, "context");
    if (b.eq(op, "readParameter") or b.eq(op, "readPatternBinding")) {
        try line(w, &.{ target, " = ", try v(a, i, "sourceValueId"), ";" });
        return;
    }
    if (b.eq(op, "bytesGet")) {
        try line(w, &.{ target, " = ", try v(a, i, "bytesId"), ".Get(", try v(a, i, "indexId"), ");" });
        return;
    }
    if (b.eq(op, "listConstruct")) {
        try line(w, &.{ target, " = ", rt, ".Create(", try f.valueList(a, b.arr(i, "itemValueIds")), ");" });
        return;
    }
    if (b.eq(op, "oneShotCreate")) {
        try line(w, &.{ target, " = ", try v(a, i, "scopeValueId"), ".CreateOneShot<", try b.formatType(a, b.get(i, "valueType")), ">();" });
        return;
    }
    if (b.eq(op, "taskWait") or b.eq(op, "oneShotWait")) {
        const limit = if (b.get(i, "limitValueId") == .null) "NyOption<long>.None()" else try v(a, i, "limitValueId");
        if (b.eq(op, "taskWait")) try line(w, &.{ target, " = NyTasks.Wait(", try v(a, i, "tasksValueId"), ", any: ", if (b.eq(b.str(i, "mode"), "any")) "true" else "false", ", limit: ", limit, ");" }) else try line(w, &.{ target, " = ", try v(a, i, "cellValueId"), ".Wait(", limit, ");" });
        return;
    }
    if (b.eq(op, "listEffectFold")) {
        const source = try v(a, i, "sourceListValueId");
        const seed = try v(a, i, "seedValueId");
        const fid = b.str(i, "folderFunctionId");
        const seed_ty = try b.formatType(a, f.valueType(fn_, b.str(i, "seedValueId")));
        if (b.eq(rt, seed_ty)) {
            try line(w, &.{ target, " = ", source, ".Fold(", seed, ", ", try f.callback(c, target, fid, context), ");" });
        } else {
            const state = try b.cat(a, &.{ target, "_state" });
            const item = try b.cat(a, &.{ target, "_item" });
            try line(w, &.{ target, " = ", source, ".FoldWhile(", rt, ".Ok(", seed, "), (", state, ", ", item, ") => ", try f.invocation(c, fid, context, &.{ try b.cat(a, &.{ state, ".OkValue" }), item }), ", ", state, " => ", state, ".IsOk);" });
        }
        return;
    }
    if (b.eq(op, "capabilityConstruct")) {
        try line(w, &.{ target, " = new ", try b.name(a, "NyI_", b.str(i, "originNodeId")), "();" });
        return;
    }
    if (b.eq(op, "functionInvoke")) {
        try line(w, &.{ target, " = ", try v(a, i, "functionValueId"), "(", try f.valueList(a, b.arr(i, "arguments")), ");" });
        return;
    }
    if (b.eq(op, "directCall")) {
        try line(w, &.{ target, " = ", try f.callbackName(a, b.str(i, "functionId")), "(", try f.namedArguments(a, b.arr(i, "arguments"), "targetParameterId", "p_", ": "), ");" });
        return;
    }
    if (b.eq(op, "capabilityInvoke")) {
        const receiver = try v(a, i, "capabilityValueId");
        const method = try b.name(a, "Op_", b.str(i, "operationId"));
        const args = try f.namedArguments(a, b.arr(i, "arguments"), "targetCapabilityParameterId", "cp_", ": ");
        const call = try b.cat(a, &.{ ".", method, "(", args, ")" });
        const t = f.valueType(fn_, b.str(i, "capabilityValueId"));
        if (b.eq(b.kind(t), "capability") and c.hasAdapter(c.capability(b.str(t, "capabilityId")))) {
            const adapter = try b.cat(a, &.{ "a_", target });
            try line(w, &.{ target, " = ", receiver, " is ", try b.name(a, "NyA_", b.str(t, "capabilityId")), " ", adapter, " ? ", adapter, call, " : ", receiver, call, ";" });
        } else try line(w, &.{ target, " = ", receiver, call, ";" });
        return;
    }
    if (b.eq(op, "productConstruct") or b.eq(op, "unionConstruct")) {
        const t = b.get(b.get(i, "result"), "type");
        const ty = if (b.eq(op, "productConstruct")) rt else try b.cat(a, &.{ try b.name(a, "NyV_", b.str(i, "variantId")), try b.typeArgs(a, b.arr(t, "arguments")) });
        try line(w, &.{ target, " = new ", ty, "(", try f.namedArguments(a, b.arr(i, "fields"), "targetFieldId", "f_", ": "), ");" });
        return;
    }
    if (b.eq(op, "productUpdate")) {
        try line(w, &.{ target, " = ", try v(a, i, "sourceValueId"), " with { ", try f.namedArguments(a, b.arr(i, "fields"), "targetFieldId", "f_", " = "), " };" });
        return;
    }
    if (b.eq(op, "extractUnionField")) {
        try line(w, &.{ target, " = ((", try b.name(a, "NyV_", b.str(i, "variantId")), try b.typeArgs(a, b.arr(f.valueType(fn_, b.str(i, "inputValueId")), "arguments")), ")", try v(a, i, "inputValueId"), ").", try b.name(a, "f_", b.str(i, "fieldId")), ";" });
        return;
    }
    if (b.eq(op, "optionMap") or b.eq(op, "optionAndThen") or b.eq(op, "resultMap") or b.eq(op, "resultMapErr") or b.eq(op, "resultAndThen")) {
        const option = std.mem.startsWith(u8, op, "option");
        const source = try v(a, i, if (option) "sourceOptionValueId" else "sourceResultValueId");
        const maperr = b.eq(op, "resultMapErr");
        const bind = std.mem.endsWith(u8, op, "AndThen");
        const fid = b.str(i, if (bind) "binderFunctionId" else "mapperFunctionId");
        const value = try b.cat(a, &.{ source, if (option) ".Value" else if (maperr) ".ErrorValue" else ".OkValue" });
        const invoke = try f.invocation(c, fid, context, &.{value});
        if (option) {
            try line(w, &.{ target, " = ", source, ".HasValue ? ", if (bind) invoke else try b.cat(a, &.{ rt, ".Some(", invoke, ")" }), " : ", rt, ".None();" });
        } else if (maperr) {
            try line(w, &.{ target, " = ", source, ".IsOk ? ", rt, ".Ok(", source, ".OkValue) : ", rt, ".Err(", invoke, ");" });
        } else try line(w, &.{ target, " = ", source, ".IsOk ? ", if (bind) invoke else try b.cat(a, &.{ rt, ".Ok(", invoke, ")" }), " : ", rt, ".Err(", source, ".ErrorValue);" });
        return;
    }
    if (b.eq(op, "exceptionCatch")) {
        // The runner's limit exception lives only in the runner's Program.cs, so match it by name.
        const err = try b.cat(a, &.{ target, "_exception" });
        try line(w, &.{ "try { ", target, " = ", try f.invocation(c, b.str(i, "bodyFunctionId"), b.arr(i, "bodyContext"), &.{"NyUnit.Value"}), "; }" });
        try line(w, &.{ "catch (Exception ", err, ") when (", err, " is not (OutOfMemoryException or InsufficientExecutionStackException) && ", err, ".GetType().Name != \"RunnerException\") { ", target, " = ", try f.invocation(c, b.str(i, "handlerFunctionId"), b.arr(i, "handlerContext"), &.{try b.cat(a, &.{ "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(", err, ")" })}), "; }" });
        return;
    }
    if (b.eq(op, "cancellationShield")) {
        const previous = try b.cat(a, &.{ target, "_cancellation" });
        try w.line("{");
        w.push();
        try line(w, &.{ "var ", previous, " = NyRuntime.Cancellation.Value;" });
        try w.line("NyRuntime.Cancellation.Value = System.Threading.CancellationToken.None;");
        try line(w, &.{ "try { ", target, " = ", try f.invocation(c, b.str(i, "bodyFunctionId"), context, &.{"NyUnit.Value"}), "; }" });
        try line(w, &.{ "finally { NyRuntime.Cancellation.Value = ", previous, "; }" });
        w.pop();
        try w.line("}");
        return;
    }
    if (b.eq(op, "taskScope")) {
        const scope = try b.cat(a, &.{ target, "_scope" });
        const body = try b.cat(a, &.{ target, "_body" });
        try w.line("{");
        w.push();
        try line(w, &.{ "var ", scope, " = new NyTaskScope(NyRuntime.Cancellation.Value);" });
        try line(w, &.{ rt, " ", body, ";" });
        try line(w, &.{ "try { ", body, " = ", try f.invocation(c, b.str(i, "bodyFunctionId"), context, &.{scope}), "; }" });
        try line(w, &.{ "catch { ", scope, ".Close(bodyThrew: true); throw; }" });
        try line(w, &.{ scope, ".Close(bodyThrew: false);" });
        try line(w, &.{ target, " = ", body, ";" });
        w.pop();
        try w.line("}");
        return;
    }
    if (b.eq(op, "taskStart")) {
        try w.line("{");
        w.push();
        for (context, 0..) |arg, index| try line(w, &.{ "var ", try b.fmt(a, "{s}_c{d}", .{ target, index }), " = ", try b.valueName(a, b.str(arg, "valueId")), ";" });
        const item = try b.cat(a, &.{ target, "_item" });
        try line(w, &.{ "var ", item, " = ", try v(a, i, "itemValueId"), ";" });
        try line(w, &.{ target, " = ", try v(a, i, "scopeValueId"), ".Start(() => ", try f.capturedInvocation(c, b.str(i, "workerFunctionId"), context, target, item), ");" });
        w.pop();
        try w.line("}");
        return;
    }
    if (b.eq(op, "useResource")) {
        const acquired = try b.cat(a, &.{ target, "_acquired" });
        const resource = try b.cat(a, &.{ target, "_resource" });
        const body = try b.cat(a, &.{ target, "_body" });
        const released = try b.cat(a, &.{ target, "_released" });
        const release = try b.cat(a, &.{ try v(a, i, "releaseCapabilityValueId"), ".", try b.name(a, "Op_", b.str(i, "releaseOperationId")), "(", resource, ")" });
        try w.line("{");
        w.push();
        try line(w, &.{ "var ", acquired, " = ", try v(a, i, "acquireValueId"), ";" });
        try line(w, &.{ "if (!", acquired, ".IsOk) ", target, " = ", rt, ".Err(", acquired, ".ErrorValue);" });
        try w.line("else");
        try w.line("{");
        w.push();
        try line(w, &.{ "var ", resource, " = ", acquired, ".OkValue;" });
        try line(w, &.{ rt, " ", body, ";" });
        try line(w, &.{ "try { ", body, " = ", try f.invocation(c, b.str(i, "bodyFunctionId"), context, &.{resource}), "; }" });
        try line(w, &.{ "catch { try { ", release, "; } catch { } throw; }" });
        if (b.eq(b.kind(b.get(c.operation(b.str(i, "releaseOperationId")).?, "resultType")), "result")) {
            try line(w, &.{ "var ", released, " = ", release, ";" });
            try line(w, &.{ target, " = ", body, ".IsOk && !", released, ".IsOk ? ", rt, ".Err(", released, ".ErrorValue) : ", body, ";" });
        } else {
            try line(w, &.{ release, ";" });
            try line(w, &.{ target, " = ", body, ";" });
        }
        w.pop();
        try w.line("}");
        w.pop();
        try w.line("}");
        return;
    }
    if (b.eq(op, "parallelAll")) {
        try parallel(w, c, fn_, i, target, rt);
        return;
    }
    return error.UnsupportedInstruction;
}
fn parallel(w: *Writer, c: Context, fn_: Value, i: Value, t: []const u8, rt: []const u8) anyerror!void {
    const a = w.a;
    const item_type = try b.formatType(a, b.get(f.valueType(fn_, b.str(i, "sourceListValueId")), "itemType"));
    const result_type = b.get(b.get(i, "result"), "type");
    const ok = try b.formatType(a, b.get(b.get(result_type, "okType"), "itemType"));
    const worker = try b.cat(a, &.{ "NyResult<", ok, ", ", try b.formatType(a, b.get(result_type, "errorType")), ">" });
    const items = try b.cat(a, &.{ t, "_items" });
    const cts = try b.cat(a, &.{ t, "_cts" });
    const path = try b.cat(a, &.{ t, "_path" });
    const tasks = try b.cat(a, &.{ t, "_tasks" });
    const index = try b.cat(a, &.{ t, "_i" });
    const item = try b.cat(a, &.{ t, "_item" });
    const result = try b.cat(a, &.{ t, "_result" });
    const acc = try b.cat(a, &.{ t, "_acc" });
    const x = try b.cat(a, &.{ t, "_x" });
    const tt = try b.cat(a, &.{ t, "_t" });
    const faulted = try b.cat(a, &.{ t, "_faulted" });
    const failed = try b.cat(a, &.{ t, "_failed" });
    try w.line("{");
    w.push();
    try line(w, &.{ "var ", items, " = ", try v(a, i, "sourceListValueId"), ".Fold(new System.Collections.Generic.List<", item_type, ">(), (", acc, ", ", x, ") => { ", acc, ".Add(", x, "); return ", acc, "; });" });
    try line(w, &.{ "using var ", cts, " = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(NyRuntime.Cancellation.Value);" });
    try line(w, &.{ "var ", path, " = new NyCancellationPath(", cts, ".Token, NyCancellationPath.Of(NyRuntime.Cancellation.Value));" });
    try line(w, &.{ "var ", tasks, " = new System.Threading.Tasks.Task<", worker, ">[", items, ".Count];" });
    try line(w, &.{ "for (var ", index, " = 0; ", index, " < ", items, ".Count; ", index, "++)" });
    try w.line("{");
    w.push();
    try line(w, &.{ "var ", item, " = ", items, "[", index, "];" });
    try line(w, &.{ tasks, "[", index, "] = NyRuntime.Run(() =>" });
    try w.line("{");
    w.push();
    try line(w, &.{ "NyRuntime.Cancellation.Value = ", cts, ".Token;" });
    try line(w, &.{ "NyRuntime.CancellationPath.Value = ", path, ";" });
    try w.line("try");
    try w.line("{");
    w.push();
    try line(w, &.{ "var ", result, " = ", try f.invocation(c, b.str(i, "workerFunctionId"), b.arr(i, "context"), &.{item}), ";" });
    try line(w, &.{ "if (!", result, ".IsOk) ", cts, ".Cancel();" });
    try line(w, &.{ "return ", result, ";" });
    w.pop();
    try w.line("}");
    try line(w, &.{ "catch { ", cts, ".Cancel(); throw; }" });
    w.pop();
    try w.line("});");
    w.pop();
    try w.line("}");
    try line(w, &.{ "NyRuntime.WaitAll(", tasks, ");" });
    try line(w, &.{ "var ", faulted, " = System.Array.Find(", tasks, ", ", tt, " => ", tt, ".IsFaulted);" });
    try line(w, &.{ "if (", faulted, " != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(", faulted, ".Exception!.InnerException!).Throw();" });
    try line(w, &.{ "var ", failed, " = System.Array.Find(", tasks, ", ", tt, " => !", tt, ".Result.IsOk);" });
    try line(w, &.{ t, " = ", failed, " != null ? ", rt, ".Err(", failed, ".Result.ErrorValue) : ", rt, ".Ok(NyList<", ok, ">.Create(System.Array.ConvertAll(", tasks, ", ", tt, " => ", tt, ".Result.OkValue)));" });
    w.pop();
    try w.line("}");
}
