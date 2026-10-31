// One branch per ExecutionInstructionKind; text preserves CSharpBackendEmitter output.
const std = @import("std");
const b = @import("root.zig");
const Value = b.Value;
const Writer = b.Writer;
const Context = b.Context;
const callback = @import("functions.zig").callback;
fn int64Literal(a: b.Allocator, n: i64) ![]const u8 {
    return if (n == std.math.minInt(i64)) "long.MinValue" else b.fmt(a, "{d}L", .{n});
}
fn binary(w: *Writer, target: []const u8, left: []const u8, op: []const u8, right: []const u8, wrap: bool) !void {
    try w.line(try b.cat(w.a, &.{ target, " = ", if (wrap) "unchecked(" else "", left, " ", op, " ", right, if (wrap) ");" else ";" }));
}
pub fn simple(w: *Writer, c: Context, instruction: Value) anyerror!bool {
    const a = w.a;
    const k = instruction;
    const op = b.kind(k);
    const target = try b.valueName(a, b.str(b.get(instruction, "result"), "id"));
    if (b.eq(op, "readPatternBinding")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceValueId")), ";" }));
        return true;
    }
    if (b.eq(op, "stringLiteral")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.escaped(a, b.str(k, "value")), ";" }));
        return true;
    }
    if (b.eq(op, "boolLiteral")) {
        try w.line(try b.cat(a, &.{ target, " = ", if (b.get(k, "value").bool) "true" else "false", ";" }));
        return true;
    }
    if (b.eq(op, "unitLiteral")) {
        try w.line(try b.cat(a, &.{ target, " = NyUnit.Value;" }));
        return true;
    }
    if (b.eq(op, "int64Literal")) {
        try w.line(try b.cat(a, &.{ target, " = ", try int64Literal(a, b.get(k, "value").integer), ";" }));
        return true;
    }
    if (b.eq(op, "float64Literal")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.cat(a, &.{ "BitConverter.UInt64BitsToDouble(0x", b.str(k, "bits"), "UL)" }), ";" }));
        return true;
    }
    if (b.eq(op, "stringIsEmpty")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), ".Length == 0;" }));
        return true;
    }
    if (b.eq(op, "int64ToString")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), ".ToString(System.Globalization.CultureInfo.InvariantCulture);" }));
        return true;
    }
    if (b.eq(op, "int64ToHexString")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "var number = ", try b.valueName(a, b.str(k, "valueId")), ";" }));
        try w.line(try b.cat(a, &.{ target, " = number < 0L ? \"-\" + unchecked((ulong)(-number)).ToString(\"x\", System.Globalization.CultureInfo.InvariantCulture) : number.ToString(\"x\", System.Globalization.CultureInfo.InvariantCulture);" }));
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "stringParseInt64")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "string text = ", try b.valueName(a, b.str(k, "valueId")), ";" }));
        try w.line("var start = text.Length > 0 && text[0] == '-' ? 1 : 0;");
        try w.line("var digits = text.Length > start;");
        try w.line("for (var index = start; digits && index < text.Length; index++) digits = text[index] >= '0' && text[index] <= '9';");
        try w.line(try b.cat(a, &.{ target, " = digits && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? NyOption<long>.Some(parsed) : NyOption<long>.None();" }));
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "stringParseInt64Hex")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "string text = ", try b.valueName(a, b.str(k, "valueId")), ";" }));
        try w.line("var negative = text.Length > 0 && text[0] == '-';");
        try w.line("var start = negative ? 1 : 0;");
        try w.line("var digits = text.Length > start;");
        try w.line("var magnitude = 0UL;");
        try w.line("for (var index = start; digits && index < text.Length; index++)");
        try w.line("{");
        w.push();
        try w.line("var c = text[index];");
        try w.line("var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;");
        try w.line("digits = digit >= 0 && magnitude <= (ulong.MaxValue >> 4);");
        try w.line("if (digits) magnitude = (magnitude << 4) + (ulong)digit;");
        w.pop();
        try w.line("}");
        try w.line(try b.cat(a, &.{ target, " = digits && magnitude <= (negative ? 9223372036854775808UL : 9223372036854775807UL) ? NyOption<long>.Some(negative ? unchecked(-(long)magnitude) : (long)magnitude) : NyOption<long>.None();" }));
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "boolNot")) {
        try w.line(try b.cat(a, &.{ target, " = !", try b.valueName(a, b.str(k, "valueId")), ";" }));
        return true;
    }
    if (b.eq(op, "stringLength")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.CodePointLength(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringToLower")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.ToLower(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringToUpper")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.ToUpper(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringDecodeUtf8Lossy")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.DecodeUtf8Lossy(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringEncodeUtf8")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.EncodeUtf8(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64RoundToInt64")) {
        try w.line(try b.cat(a, &.{ target, " = NyNumber.RoundToInt64(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64FloorToInt64")) {
        try w.line(try b.cat(a, &.{ target, " = NyNumber.FloorToInt64(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringUtf8ByteCount")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.Utf8ByteCount(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64ToString")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.FromFloat64(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringParseFloat64")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.ParseFloat64(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringFromCodePoint")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.FromCodePoint(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringToCodePoint")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.ToCodePoint(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringCodePoints")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "string text = ", try b.valueName(a, b.str(k, "valueId")), ";" }));
        try w.line("var codePoints = new System.Collections.Generic.List<NyStr>(text.Length);");
        try w.line("for (var index = 0; index < text.Length; index++)");
        try w.line("{");
        w.push();
        try w.line("var width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;");
        try w.line("codePoints.Add(text.Substring(index, width));");
        try w.line("index += width - 1;");
        w.pop();
        try w.line("}");
        try w.line(try b.cat(a, &.{ target, " = NyList<NyStr>.FromOwnedArray(codePoints.ToArray());" }));
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "stringSlice")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "string text = ", try b.valueName(a, b.str(k, "valueId")), ";" }));
        try w.line(try b.cat(a, &.{ "var start = System.Math.Max(0L, ", try b.valueName(a, b.str(k, "startId")), ");" }));
        try w.line(try b.cat(a, &.{ "var end = System.Math.Max(0L, ", try b.valueName(a, b.str(k, "endId")), ");" }));
        try w.line("var startOffset = NyText.OffsetOf(text, start);");
        try w.line("var endOffset = end > start ? NyText.OffsetOf(text, end) : startOffset;");
        try w.line(try b.cat(a, &.{ target, " = endOffset > startOffset ? text.Substring(startOffset, endOffset - startOffset) : \"\";" }));
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "stringContains")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.Contains(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "substringId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringIndexOf")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.IndexOf(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "substringId")), ", ", try b.valueName(a, b.str(k, "fromId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringSpan")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.Span(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "setId")), ", ", try b.valueName(a, b.str(k, "fromId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringSpanBack")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.SpanBack(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "setId")), ", ", try b.valueName(a, b.str(k, "endId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringLastIndexOfBefore")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.LastIndexOfBefore(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "substringId")), ", ", try b.valueName(a, b.str(k, "endId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringLastIndexOf")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.LastIndexOf(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "substringId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringStartsWith")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.StartsWith(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "prefixId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringEndsWith")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.EndsWith(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "suffixId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringSplit")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.Split(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "separatorId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringSplitLines")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.SplitLines(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "breaksId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringReplace")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.Replace(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "patternId")), ", ", try b.valueName(a, b.str(k, "replacementId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringTrim")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.Trim(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringTrimStart")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.TrimStart(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringTrimEnd")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.TrimEnd(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringJoin")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.Join(", try b.valueName(a, b.str(k, "listValueId")), ", ", try b.valueName(a, b.str(k, "separatorId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringEqual")) {
        try w.line(try b.cat(a, &.{ target, " = string.Equals(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ", StringComparison.Ordinal);" }));
        return true;
    }
    if (b.eq(op, "stringLessThan")) {
        try w.line(try b.cat(a, &.{ target, " = NyText.LessThan(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "valueEqual")) {
        try w.line(try b.cat(a, &.{ target, " = NyEquality.Equal(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "stringConcat")) {
        try w.line(try b.cat(a, &.{ target, " = NyStr.Concat(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "int64WrappingAdd")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "+", try b.valueName(a, b.str(k, "rightValueId")), true);
        return true;
    }
    if (b.eq(op, "int64WrappingSubtract")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "-", try b.valueName(a, b.str(k, "rightValueId")), true);
        return true;
    }
    if (b.eq(op, "int64WrappingMultiply")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "*", try b.valueName(a, b.str(k, "rightValueId")), true);
        return true;
    }
    if (b.eq(op, "int64FloorDivide")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "var dividend = ", try b.valueName(a, b.str(k, "leftValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "var divisor = ", try b.valueName(a, b.str(k, "rightValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "if (divisor == 0L) ", target, " = 0L;" }));
        try w.line(try b.cat(a, &.{ "else if (divisor == -1L) ", target, " = unchecked(-dividend);" }));
        try w.line("else");
        try w.line("{");
        w.push();
        try w.line("var quotient = dividend / divisor;");
        try w.line("if (dividend % divisor != 0L && (dividend < 0L) != (divisor < 0L)) quotient -= 1L;");
        try w.line(try b.cat(a, &.{ target, " = quotient;" }));
        w.pop();
        try w.line("}");
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "int64FloorModulo")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "var dividend = ", try b.valueName(a, b.str(k, "leftValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "var divisor = ", try b.valueName(a, b.str(k, "rightValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "if (divisor == 0L) ", target, " = dividend;" }));
        try w.line(try b.cat(a, &.{ "else if (divisor == -1L) ", target, " = 0L;" }));
        try w.line("else");
        try w.line("{");
        w.push();
        try w.line("var remainder = dividend % divisor;");
        try w.line("if (remainder != 0L && (remainder < 0L) != (divisor < 0L)) remainder += divisor;");
        try w.line(try b.cat(a, &.{ target, " = remainder;" }));
        w.pop();
        try w.line("}");
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "int64Min")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Min(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "int64Max")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Max(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "int64BitAnd")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "&", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "int64BitOr")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "|", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "int64BitXor")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "^", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "int64ShiftLeft")) {
        const count = try b.valueName(a, b.str(k, "countValueId"));
        try w.line(try b.cat(a, &.{ target, " = ", count, " <= 0L ? ", try b.valueName(a, b.str(k, "valueId")), " : ", count, " >= 64L ? 0L : ", try b.valueName(a, b.str(k, "valueId")), " << (int)", count, ";" }));
        return true;
    }
    if (b.eq(op, "int64ShiftRight")) {
        const count = try b.valueName(a, b.str(k, "countValueId"));
        const value = try b.valueName(a, b.str(k, "valueId"));
        try w.line(try b.cat(a, &.{ target, " = ", count, " <= 0L ? ", value, " : ", count, " >= 64L ? (", value, " < 0L ? -1L : 0L) : ", value, " >> (int)", count, ";" }));
        return true;
    }
    if (b.eq(op, "int64CheckedAdd")) {
        try w.line(try b.cat(a, &.{ target, " = NyNumber.CheckedAdd(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "int64CheckedSubtract")) {
        try w.line(try b.cat(a, &.{ target, " = NyNumber.CheckedSubtract(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "int64CheckedMultiply")) {
        try w.line(try b.cat(a, &.{ target, " = NyNumber.CheckedMultiply(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listRange")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "var first = ", try b.valueName(a, b.str(k, "startValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "var last = ", try b.valueName(a, b.str(k, "endValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "if (last <= first) ", target, " = NyList<long>.Empty();" }));
        try w.line("else");
        try w.line("{");
        w.push();
        try w.line("var count = unchecked((ulong)(last - first));");
        try w.line("if (count > (ulong)Array.MaxLength) throw new OutOfMemoryException();");
        try w.line("var items = new long[(int)count];");
        try w.line("for (var index = 0; index < items.Length; index++) items[index] = first + index;");
        try w.line(try b.cat(a, &.{ target, " = NyList<long>.FromOwnedArray(items);" }));
        w.pop();
        try w.line("}");
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "stringRepeat")) {
        try w.line("{");
        w.push();
        try w.line(try b.cat(a, &.{ "string text = ", try b.valueName(a, b.str(k, "textValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "var count = ", try b.valueName(a, b.str(k, "countValueId")), ";" }));
        try w.line(try b.cat(a, &.{ "if (count <= 0L || text.Length == 0) ", target, " = \"\";" }));
        try w.line("else if (count > int.MaxValue / text.Length) throw new OutOfMemoryException();");
        try w.line(try b.cat(a, &.{ "else ", target, " = new System.Text.StringBuilder(text.Length * (int)count).Insert(0, text, (int)count).ToString();" }));
        w.pop();
        try w.line("}");
        return true;
    }
    if (b.eq(op, "float64Round")) {
        try w.line(try b.cat(a, &.{ target, " = NyNumber.Round(", try b.valueName(a, b.str(k, "valueId")), ", ", try b.valueName(a, b.str(k, "digitsValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "int64Equal")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "==", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "int64LessThan")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "<", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "int64LessThanOrEqual")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "<=", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "int64GreaterThan")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), ">", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "int64GreaterThanOrEqual")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), ">=", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "float64Add")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "+", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "float64Min")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Min(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64Max")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Max(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64Subtract")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "-", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "float64Multiply")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "*", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "float64Divide")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "/", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "float64FloorModulo")) {
        try w.line(try b.cat(a, &.{ target, " = NyNumber.FloorModulo(", try b.valueName(a, b.str(k, "leftValueId")), ", ", try b.valueName(a, b.str(k, "rightValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64Equal")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "==", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "float64LessThan")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "<", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "float64IsNaN")) {
        try w.line(try b.cat(a, &.{ target, " = double.IsNaN(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64IsFinite")) {
        try w.line(try b.cat(a, &.{ target, " = double.IsFinite(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64IsInfinite")) {
        try w.line(try b.cat(a, &.{ target, " = double.IsInfinity(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "int64ToFloat64")) {
        try w.line(try b.cat(a, &.{ target, " = (double)", try b.valueName(a, b.str(k, "valueId")), ";" }));
        return true;
    }
    if (b.eq(op, "int64WrappingAbs")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), " < 0L ? unchecked(-", try b.valueName(a, b.str(k, "valueId")), ") : ", try b.valueName(a, b.str(k, "valueId")), ";" }));
        return true;
    }
    if (b.eq(op, "int64BitNot")) {
        try w.line(try b.cat(a, &.{ target, " = ~", try b.valueName(a, b.str(k, "valueId")), ";" }));
        return true;
    }
    if (b.eq(op, "float64Sqrt")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Sqrt(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64Abs")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Abs(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64Negate")) {
        try w.line(try b.cat(a, &.{ target, " = -", try b.valueName(a, b.str(k, "valueId")), ";" }));
        return true;
    }
    if (b.eq(op, "float64Floor")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Floor(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64Ceiling")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Ceiling(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "float64Truncate")) {
        try w.line(try b.cat(a, &.{ target, " = System.Math.Truncate(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "bits32FromInt64")) {
        try w.line(try b.cat(a, &.{ target, " = unchecked((uint)", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "bits32ToInt64")) {
        try w.line(try b.cat(a, &.{ target, " = (long)", try b.valueName(a, b.str(k, "valueId")), ";" }));
        return true;
    }
    if (b.eq(op, "bits32Not")) {
        try w.line(try b.cat(a, &.{ target, " = ~", try b.valueName(a, b.str(k, "valueId")), ";" }));
        return true;
    }
    if (b.eq(op, "bits32Xor")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "^", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "bits32And")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "&", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "bits32Or")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "|", try b.valueName(a, b.str(k, "rightValueId")), false);
        return true;
    }
    if (b.eq(op, "bits32Add")) {
        try binary(w, target, try b.valueName(a, b.str(k, "leftValueId")), "+", try b.valueName(a, b.str(k, "rightValueId")), true);
        return true;
    }
    if (b.eq(op, "bits32RotateRight")) {
        try w.line(try b.cat(a, &.{ target, " = System.Numerics.BitOperations.RotateRight(", try b.valueName(a, b.str(k, "valueId")), ", (int)(", try b.valueName(a, b.str(k, "amountValueId")), " & 31L));" }));
        return true;
    }
    if (b.eq(op, "bits32ShiftRight")) {
        const amount = try b.valueName(a, b.str(k, "amountValueId"));
        try w.line(try b.cat(a, &.{ target, " = ", amount, " <= 0L ? ", try b.valueName(a, b.str(k, "valueId")), " : ", amount, " >= 32L ? 0u : ", try b.valueName(a, b.str(k, "valueId")), " >> (int)", amount, ";" }));
        return true;
    }
    if (b.eq(op, "bits32ShiftLeft")) {
        const amount = try b.valueName(a, b.str(k, "amountValueId"));
        try w.line(try b.cat(a, &.{ target, " = ", amount, " <= 0L ? ", try b.valueName(a, b.str(k, "valueId")), " : ", amount, " >= 32L ? 0u : ", try b.valueName(a, b.str(k, "valueId")), " << (int)", amount, ";" }));
        return true;
    }
    if (b.eq(op, "bytesEmpty")) {
        try w.line(try b.cat(a, &.{ target, " = NyBytes.Empty();" }));
        return true;
    }
    if (b.eq(op, "bytesFill")) {
        try w.line(try b.cat(a, &.{ target, " = NyBytes.Fill(", try b.valueName(a, b.str(k, "lengthId")), ", ", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "bytesLength")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), ".Length;" }));
        return true;
    }
    if (b.eq(op, "bytesConcat")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftId")), ".Concat(", try b.valueName(a, b.str(k, "rightId")), ");" }));
        return true;
    }
    if (b.eq(op, "bytesSlice")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "bytesId")), ".Slice(", try b.valueName(a, b.str(k, "startId")), ", ", try b.valueName(a, b.str(k, "endId")), ");" }));
        return true;
    }
    if (b.eq(op, "bytesFromLatin1")) {
        try w.line(try b.cat(a, &.{ target, " = NyBytes.FromLatin1(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "bytesToLatin1")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), ".ToLatin1();" }));
        return true;
    }
    if (b.eq(op, "listEmpty")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".Empty();" }));
        return true;
    }
    if (b.eq(op, "setEmpty")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".Empty();" }));
        return true;
    }
    if (b.eq(op, "mapEmpty")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".Empty();" }));
        return true;
    }
    if (b.eq(op, "listLength")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".Length;" }));
        return true;
    }
    if (b.eq(op, "mapValues")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".Values();" }));
        return true;
    }
    if (b.eq(op, "mapKeys")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".Keys();" }));
        return true;
    }
    if (b.eq(op, "setToList")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "setValueId")), ".ToList();" }));
        return true;
    }
    if (b.eq(op, "mapSize")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".Size;" }));
        return true;
    }
    if (b.eq(op, "setFromList")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".FromList(", try b.valueName(a, b.str(k, "listValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setSize")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "setValueId")), ".Size;" }));
        return true;
    }
    if (b.eq(op, "listReverse")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".Reverse();" }));
        return true;
    }
    if (b.eq(op, "listLast")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".Last();" }));
        return true;
    }
    if (b.eq(op, "listDropLast")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".DropLast();" }));
        return true;
    }
    if (b.eq(op, "listItemAt")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".ItemAt(", try b.valueName(a, b.str(k, "indexValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "mapRemove")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".Remove(", try b.valueName(a, b.str(k, "keyValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setInsert")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "setValueId")), ".Insert(", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setRemove")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "setValueId")), ".Remove(", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setUnion")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftSetValueId")), ".Union(", try b.valueName(a, b.str(k, "rightSetValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setIntersection")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftSetValueId")), ".Intersection(", try b.valueName(a, b.str(k, "rightSetValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setDifference")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftSetValueId")), ".Difference(", try b.valueName(a, b.str(k, "rightSetValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setIsSubset")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftSetValueId")), ".IsSubsetOf(", try b.valueName(a, b.str(k, "rightSetValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "mapUnion")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftMapValueId")), ".Union(", try b.valueName(a, b.str(k, "rightMapValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "mapContainsKey")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".ContainsKey(", try b.valueName(a, b.str(k, "keyValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "setContains")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "setValueId")), ".Contains(", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listIndexOf")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".IndexOf(", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listLastIndexOf")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".LastIndexOf(", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listContains")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".Contains(", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "mapGet")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".Get(", try b.valueName(a, b.str(k, "keyValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "optionGetOrDefault")) {
        const option = try b.valueName(a, b.str(k, "optionValueId"));
        try w.line(try b.cat(a, &.{ target, " = ", option, ".HasValue ? ", option, ".Value : ", try b.valueName(a, b.str(k, "defaultValueId")), ";" }));
        return true;
    }
    if (b.eq(op, "resultGetOrDefault")) {
        const result = try b.valueName(a, b.str(k, "resultValueId"));
        try w.line(try b.cat(a, &.{ target, " = ", result, ".IsOk ? ", result, ".OkValue : ", try b.valueName(a, b.str(k, "defaultValueId")), ";" }));
        return true;
    }
    if (b.eq(op, "optionOkOr")) {
        const option = try b.valueName(a, b.str(k, "optionValueId"));
        const resultType = try b.formatType(a, b.get(b.get(instruction, "result"), "type"));
        try w.line(try b.cat(a, &.{ target, " = ", option, ".HasValue ? ", resultType, ".Ok(", option, ".Value) : ", resultType, ".Err(", try b.valueName(a, b.str(k, "errorValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "resultOkValue")) {
        const result = try b.valueName(a, b.str(k, "valueId"));
        const optionType = try b.formatType(a, b.get(b.get(instruction, "result"), "type"));
        try w.line(try b.cat(a, &.{ target, " = ", result, ".IsOk ? ", optionType, ".Some(", result, ".OkValue) : ", optionType, ".None();" }));
        return true;
    }
    if (b.eq(op, "resultErrValue")) {
        const result = try b.valueName(a, b.str(k, "valueId"));
        const optionType = try b.formatType(a, b.get(b.get(instruction, "result"), "type"));
        try w.line(try b.cat(a, &.{ target, " = ", result, ".IsOk ? ", optionType, ".None() : ", optionType, ".Some(", result, ".ErrorValue);" }));
        return true;
    }
    if (b.eq(op, "optionIsSome")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), ".HasValue;" }));
        return true;
    }
    if (b.eq(op, "resultIsOk")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), ".IsOk;" }));
        return true;
    }
    if (b.eq(op, "mapGetOrDefault")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".GetOrDefault(", try b.valueName(a, b.str(k, "keyValueId")), ", ", try b.valueName(a, b.str(k, "defaultValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listSlice")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".Slice(", try b.valueName(a, b.str(k, "startId")), ", ", try b.valueName(a, b.str(k, "endId")), ");" }));
        return true;
    }
    if (b.eq(op, "listSetAt")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".SetAt(", try b.valueName(a, b.str(k, "indexValueId")), ", ", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listInsertAt")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".InsertAt(", try b.valueName(a, b.str(k, "indexValueId")), ", ", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listRemoveAt")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".RemoveAt(", try b.valueName(a, b.str(k, "indexValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "mapInsert")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "mapValueId")), ".Insert(", try b.valueName(a, b.str(k, "keyValueId")), ", ", try b.valueName(a, b.str(k, "valueValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listAppendItem")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "listValueId")), ".AppendItem(", try b.valueName(a, b.str(k, "itemValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listConcat")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftListValueId")), ".Concat(", try b.valueName(a, b.str(k, "rightListValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "listFold")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".Fold(", try b.valueName(a, b.str(k, "seedValueId")), ", ", try callback(c, target, b.str(k, "folderFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listScan")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".Scan(", try b.valueName(a, b.str(k, "seedValueId")), ", ", try callback(c, target, b.str(k, "folderFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "mapFold")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceMapValueId")), ".Fold(", try b.valueName(a, b.str(k, "seedValueId")), ", ", try callback(c, target, b.str(k, "folderFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "setFold")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceSetValueId")), ".Fold(", try b.valueName(a, b.str(k, "seedValueId")), ", ", try callback(c, target, b.str(k, "folderFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "cancellationRequested")) {
        try w.line(try b.cat(a, &.{ target, " = NyRuntime.CancellationRequested;" }));
        return true;
    }
    if (b.eq(op, "exceptionIsCancellation")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "valueId")), ".SourceException is System.OperationCanceledException;" }));
        return true;
    }
    if (b.eq(op, "exceptionMessage")) {
        try w.line(try b.cat(a, &.{ target, " = (NyStr)", try b.valueName(a, b.str(k, "valueId")), ".SourceException.Message;" }));
        return true;
    }
    if (b.eq(op, "exceptionRethrow")) {
        try w.line(try b.cat(a, &.{ try b.valueName(a, b.str(k, "valueId")), ".Throw();" }));
        try w.line(try b.cat(a, &.{ target, " = default!;" }));
        return true;
    }
    if (b.eq(op, "oneShotComplete")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "cellValueId")), ".Complete(", try b.valueName(a, b.str(k, "valueValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "taskCancel")) {
        try w.line(try b.cat(a, &.{ try b.valueName(a, b.str(k, "taskValueId")), ".Cancel();" }));
        try w.line(try b.cat(a, &.{ target, " = NyUnit.Value;" }));
        return true;
    }
    if (b.eq(op, "listMap")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".Map(", try callback(c, target, b.str(k, "mapperFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listZipWith")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "leftListValueId")), ".ZipWith(", try b.valueName(a, b.str(k, "rightListValueId")), ", ", try callback(c, target, b.str(k, "combinerFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listFlatMap")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".FlatMap(", try callback(c, target, b.str(k, "mapperFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "mapFromList")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".FromList(", try b.valueName(a, b.str(k, "sourceListValueId")), ", ", try callback(c, target, b.str(k, "keyOfFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listFilter")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".Filter(", try callback(c, target, b.str(k, "predicateFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "mapFilter")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceMapValueId")), ".Filter(", try callback(c, target, b.str(k, "keepFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "setFilter")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceSetValueId")), ".Filter(", try callback(c, target, b.str(k, "predicateFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "mapMapValues")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceMapValueId")), ".MapValues(", try callback(c, target, b.str(k, "mapperFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "setMap")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceSetValueId")), ".Map(", try callback(c, target, b.str(k, "mapperFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listSortBy")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".SortBy(", try callback(c, target, b.str(k, "lessThanFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listMinBy")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".MinBy(", try callback(c, target, b.str(k, "lessThanFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listMaxBy")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".MaxBy(", try callback(c, target, b.str(k, "lessThanFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listAny")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".Any(", try callback(c, target, b.str(k, "predicateFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listFindIndex")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".FindIndex(", try callback(c, target, b.str(k, "predicateFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "listAll")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceListValueId")), ".All(", try callback(c, target, b.str(k, "predicateFunctionId"), b.arr(k, "context")), ");" }));
        return true;
    }
    if (b.eq(op, "functionReference")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.name(a, "NyF_", b.str(k, "functionId")), ";" }));
        return true;
    }
    if (b.eq(op, "fieldAccess")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "sourceValueId")), ".", try b.name(a, "f_", b.str(k, "fieldId")), ";" }));
        return true;
    }
    if (b.eq(op, "optionNone")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".None();" }));
        return true;
    }
    if (b.eq(op, "optionSome")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".Some(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "resultOk")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".Ok(", try b.valueName(a, b.str(k, "valueId")), ");" }));
        return true;
    }
    if (b.eq(op, "resultErr")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.formatType(a, b.get(b.get(instruction, "result"), "type")), ".Err(", try b.valueName(a, b.str(k, "errorValueId")), ");" }));
        return true;
    }
    if (b.eq(op, "extractOptionSome")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "inputValueId")), ".Value;" }));
        return true;
    }
    if (b.eq(op, "extractResultOk")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "inputValueId")), ".OkValue;" }));
        return true;
    }
    if (b.eq(op, "extractResultErr")) {
        try w.line(try b.cat(a, &.{ target, " = ", try b.valueName(a, b.str(k, "inputValueId")), ".ErrorValue;" }));
        return true;
    }
    return false;
}
