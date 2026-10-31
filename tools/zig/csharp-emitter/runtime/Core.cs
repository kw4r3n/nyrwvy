public readonly record struct NyUnit
{
    public static NyUnit Value => default;
}

public readonly record struct NyOption<T>(bool HasValue, T Value)
{
    public static NyOption<T> Some(T value) => new(true, value);
    public static NyOption<T> None() => new(false, default!);
}

public readonly record struct NyResult<TOk, TError>(bool IsOk, TOk OkValue, TError ErrorValue)
{
    public static NyResult<TOk, TError> Ok(TOk value) => new(true, value, default!);
    public static NyResult<TOk, TError> Err(TError error) => new(false, default!, error);
}

public static class NyEquality
{
    public static bool Equal<T>(T left, T right) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(left, right);
}

public static class NyNumber
{
    public static NyOption<long> RoundToInt64(double value) => ToInt64(System.Math.Round(value, System.MidpointRounding.AwayFromZero));
    public static NyOption<long> FloorToInt64(double value) => ToInt64(System.Math.Floor(value));
    public static double FloorModulo(double dividend, double divisor)
    {
        var remainder = dividend % divisor;
        if (remainder != 0.0) { if ((divisor < 0.0) != (remainder < 0.0)) remainder += divisor; }
        else remainder = System.Math.CopySign(0.0, divisor);
        return remainder;
    }
    private static NyOption<long> ToInt64(double integral) =>
    integral >= -9223372036854775808.0 && integral < 9223372036854775808.0 ? NyOption<long>.Some((long)integral) : NyOption<long>.None();
    public static NyOption<long> CheckedAdd(long left, long right) => FitInt64((System.Int128)left + right);
    public static NyOption<long> CheckedSubtract(long left, long right) => FitInt64((System.Int128)left - right);
    public static NyOption<long> CheckedMultiply(long left, long right) => FitInt64((System.Int128)left * right);
    public static double Round(double value, long digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0.0) return value;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var text = System.Math.Abs(value).ToString("R", culture);
        var exponentAt = text.IndexOf('E');
        var exponent = exponentAt < 0 ? 0 : int.Parse(text.Substring(exponentAt + 1), culture);
        var mantissa = exponentAt < 0 ? text : text.Substring(0, exponentAt);
        var pointAt = mantissa.IndexOf('.');
        var significand = pointAt < 0 ? mantissa : mantissa.Remove(pointAt, 1);
        long point = (pointAt < 0 ? mantissa.Length : pointAt) + exponent;
        var leading = 0;
        while (leading < significand.Length - 1 && significand[leading] == '0') leading++;
        significand = significand.Substring(leading);
        point -= leading;
        var keep = point + System.Math.Clamp(digits, -1000L, 1000L);
        if (keep >= significand.Length) return value;
        if (keep < 0) return value < 0 ? -0.0 : 0.0;
        var kept = keep == 0 ? 0L : long.Parse(significand.Substring(0, (int)keep), culture);
        if (significand[(int)keep] >= '5') kept += 1;
        var rounded = double.Parse(kept.ToString(culture) + "E" + (point - keep).ToString(culture), System.Globalization.NumberStyles.AllowExponent, culture);
        return value < 0 ? -rounded : rounded;
    }
    private static NyOption<long> FitInt64(System.Int128 exact) =>
    exact >= long.MinValue && exact <= long.MaxValue ? NyOption<long>.Some((long)exact) : NyOption<long>.None();
}

public static class NyText
{
    public static bool LessThan(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            var a = CodePointAt(left, ref i);
            var b = CodePointAt(right, ref j);
            if (a != b) return a < b;
        }
        return i >= left.Length && j < right.Length;
    }
    public static NyOption<long> IndexOf(string text, string value, long from)
    {
        var start = System.Math.Max(0L, from);
        var position = System.Math.Min(start, CodePointLength(text));
        var offset = OffsetOf(text, position);
        if (value.Length == 0) return NyOption<long>.Some(position);
        var found = Find(text, value, offset);
        return found < 0 ? NyOption<long>.None() : NyOption<long>.Some(PositionOf(text, found));
    }
    public static long Span(string text, string set, long from)
    {
        var start = System.Math.Max(0L, from);
        var position = System.Math.Min(start, CodePointLength(text));
        var offset = OffsetOf(text, position);
        if (set.Length == 0) return position;
        var members = CodePointSet(set);
        while (offset < text.Length)
        {
            var next = offset;
            if (!members.Contains(CodePointAt(text, ref next))) break;
            offset = next;
            position++;
        }
        return position;
    }
    public static long SpanBack(string text, string set, long end)
    {
        var stop = System.Math.Max(0L, end);
        var position = System.Math.Min(stop, CodePointLength(text));
        var offset = OffsetOf(text, position);
        if (set.Length == 0) return position;
        var members = CodePointSet(set);
        while (offset > 0)
        {
            var previous = offset >= 2 && char.IsLowSurrogate(text[offset - 1]) && char.IsHighSurrogate(text[offset - 2]) ? offset - 2 : offset - 1;
            var probe = previous;
            if (!members.Contains(CodePointAt(text, ref probe))) break;
            offset = previous;
            position--;
        }
        return position;
    }
    public static NyOption<long> LastIndexOf(string text, string value)
    {
        if (value.Length == 0) return NyOption<long>.Some(CodePointLength(text));
        return LastMatchEndingBy(text, value, text.Length - 1);
    }
    public static NyOption<long> LastIndexOfBefore(string text, string value, long end)
    {
        var stop = System.Math.Max(0L, end);
        var position = System.Math.Min(stop, CodePointLength(text));
        var offset = OffsetOf(text, position);
        if (value.Length == 0) return NyOption<long>.Some(position);
        return LastMatchEndingBy(text, value, offset - 1);
    }
    private static NyOption<long> LastMatchEndingBy(string text, string value, int searchEnd)
    {
        while (searchEnd >= value.Length - 1)
        {
            var found = text.LastIndexOf(value, searchEnd, StringComparison.Ordinal);
            if (found < 0) return NyOption<long>.None();
            if (IsBoundary(text, found) && IsBoundary(text, found + value.Length)) return NyOption<long>.Some(PositionOf(text, found));
            searchEnd = found + value.Length - 2;
        }
        return NyOption<long>.None();
    }
    public static bool Contains(string text, string value) => Find(text, value, 0) >= 0;
    public static bool StartsWith(string text, string prefix) => text.StartsWith(prefix, StringComparison.Ordinal) && IsBoundary(text, prefix.Length);
    public static bool EndsWith(string text, string suffix) => text.EndsWith(suffix, StringComparison.Ordinal) && IsBoundary(text, text.Length - suffix.Length);
    public static NyList<NyStr> Split(string text, string separator)
    {
        if (separator.Length == 0) return NyList<NyStr>.Create(text);
        var pieces = new NyStr[4];
        var count = 0;
        var start = 0;
        for (var found = Find(text, separator, 0); found >= 0; found = Find(text, separator, start))
        {
            if (count == pieces.Length) Array.Resize(ref pieces, count * 2);
            pieces[count++] = text.Substring(start, found - start);
            start = found + separator.Length;
        }
        Array.Resize(ref pieces, count + 1);
        pieces[count] = text.Substring(start);
        return NyList<NyStr>.FromOwnedArray(pieces);
    }
    public static NyList<NyStr> SplitLines(string text, string breaks)
    {
        if (text.Length == 0) return NyList<NyStr>.Empty();
        if (breaks.Length == 0) return NyList<NyStr>.Create(text);
        var members = CodePointSet(breaks);
        var crlf = members.Contains('\r') && members.Contains('\n');
        var lines = new NyStr[4];
        var count = 0;
        var start = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            var next = offset;
            var codePoint = CodePointAt(text, ref next);
            if (members.Contains(codePoint))
            {
                if (count == lines.Length) Array.Resize(ref lines, count * 2);
                lines[count++] = text.Substring(start, offset - start);
                if (crlf && codePoint == '\r' && next < text.Length && text[next] == '\n') next++;
                start = next;
            }
            offset = next;
        }
        if (start < text.Length)
        {
            if (count == lines.Length) Array.Resize(ref lines, count + 1);
            lines[count++] = text.Substring(start);
        }
        Array.Resize(ref lines, count);
        return NyList<NyStr>.FromOwnedArray(lines);
    }
    public static string Replace(string text, string pattern, string replacement)
    {
        if (pattern.Length == 0) return text;
        var found = Find(text, pattern, 0);
        if (found < 0) return text;
        var builder = new System.Text.StringBuilder(text.Length);
        var start = 0;
        for (; found >= 0; found = Find(text, pattern, start))
        {
            builder.Append(text, start, found - start).Append(replacement);
            start = found + pattern.Length;
        }
        return builder.Append(text, start, text.Length - start).ToString();
    }
    public static string Trim(string text) => TrimEnd(TrimStart(text));
    public static string TrimStart(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        return start == 0 ? text : text.Substring(start);
    }
    public static string TrimEnd(string text)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return end == text.Length ? text : text.Substring(0, end);
    }
    public static string ToLower(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var unit = text[index];
            if (Width(text, index) == 2) builder.Append(System.Text.Rune.ToLowerInvariant(new System.Text.Rune(unit, text[++index])).ToString());
            else if (char.IsSurrogate(unit)) builder.Append(unit);
            else builder.Append(unit == '\u0130' ? 'i' : char.ToLowerInvariant(unit));
        }
        return builder.ToString();
    }
    public static string ToUpper(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var unit = text[index];
            if (Width(text, index) == 2) builder.Append(System.Text.Rune.ToUpperInvariant(new System.Text.Rune(unit, text[++index])).ToString());
            else if (char.IsSurrogate(unit)) builder.Append(unit);
            else builder.Append(unit == '\u0131' ? 'I' : unit == '\u00DF' ? '\u1E9E' : char.ToUpperInvariant(unit));
        }
        return builder.ToString();
    }
    public static long Utf8ByteCount(string text) => System.Text.Encoding.UTF8.GetByteCount(text);
    public static string DecodeUtf8Lossy(string text)
    {
        var bytes = new byte[text.Length];
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var unit = text[index];
            if (Width(text, index) == 2) index++;
            bytes[count++] = unit <= '\u00FF' ? (byte)unit : (byte)0xFF;
        }
        return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
    }
    public static string EncodeUtf8(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return string.Create(bytes.Length, bytes, static (span, source) => { for (var index = 0; index < source.Length; index++) span[index] = (char)source[index]; });
    }
    public static NyOption<NyStr> FromCodePoint(long code) =>
    code < 0L || code > 0x10FFFFL || (code >= 0xD800L && code <= 0xDFFFL) ? NyOption<NyStr>.None() : NyOption<NyStr>.Some(char.ConvertFromUtf32((int)code));
    public static NyOption<long> ToCodePoint(string text) =>
    text.Length == 1 ? NyOption<long>.Some(text[0])
    : text.Length == 2 && Width(text, 0) == 2 ? NyOption<long>.Some(char.ConvertToUtf32(text[0], text[1]))
    : NyOption<long>.None();
    public static string FromFloat64(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0.0 ? "inf" : "-inf";
        if (value == 0.0) return double.IsNegative(value) ? "-0.0" : "0.0";
        var shortest = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var negative = shortest[0] == '-';
        var mark = shortest.IndexOf('E');
        var first = negative ? 1 : 0;
        var mantissa = shortest.Substring(first, (mark < 0 ? shortest.Length : mark) - first);
        var exponent = mark < 0 ? 0 : int.Parse(shortest.Substring(mark + 1), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var decpt = (point < 0 ? mantissa.Length : point) + exponent;
        var lead = 0;
        while (digits[lead] == '0') lead++;
        digits = digits.Substring(lead).TrimEnd('0');
        decpt -= lead;
        var builder = new System.Text.StringBuilder(32);
        if (negative) builder.Append('-');
        if (decpt <= -4 || decpt > 16)
        {
            builder.Append(digits[0]);
            if (digits.Length > 1) builder.Append('.').Append(digits, 1, digits.Length - 1);
            var power = decpt - 1;
            builder.Append(power < 0 ? "e-" : "e+").Append(System.Math.Abs(power).ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (decpt <= 0) builder.Append("0.").Append('0', -decpt).Append(digits);
        else if (decpt >= digits.Length) builder.Append(digits).Append('0', decpt - digits.Length).Append(".0");
        else builder.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        return builder.ToString();
    }
    public static NyOption<double> ParseFloat64(string text)
    {
        var index = text.Length > 0 && text[0] == '-' ? 1 : 0;
        if (!SkipDigits(text, ref index)) return NyOption<double>.None();
        if (index < text.Length && text[index] == '.')
        {
            index++;
            if (!SkipDigits(text, ref index)) return NyOption<double>.None();
        }
        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            index++;
            if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
            if (!SkipDigits(text, ref index)) return NyOption<double>.None();
        }
        if (index != text.Length) return NyOption<double>.None();
        return NyOption<double>.Some(double.Parse(text, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture));
    }
    private static bool SkipDigits(string text, ref int index)
    {
        var start = index;
        while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
        return index > start;
    }
    public static NyStr Join(NyList<NyStr> list, string separator)
    {
        var items = list.ToArray();
        var total = 0L;
        for (var i = 0; i < items.Length; i++) total += items[i].Length + (i > 0 ? separator.Length : 0);
        if (total > int.MaxValue) throw new OutOfMemoryException();
        var chars = new char[total];
        var at = 0;
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0) { separator.AsSpan().CopyTo(chars.AsSpan(at)); at += separator.Length; }
            items[i].CopyTo(chars.AsSpan(at));
            at += items[i].Length;
        }
        return new string(chars);
    }
    private static int Find(string text, string value, int offset)
    {
        while (offset <= text.Length - value.Length)
        {
            var found = text.IndexOf(value, offset, StringComparison.Ordinal);
            if (found < 0) return -1;
            if (IsBoundary(text, found) && IsBoundary(text, found + value.Length)) return found;
            offset = found + 1;
        }
        return -1;
    }
    private static bool IsBoundary(string text, int offset) =>
    offset <= 0 || offset >= text.Length || !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
    private static int Width(string text, int index) =>
    char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
    private const int IndexedLength = 256;
    private const int CheckpointShift = 6;
    private sealed class PositionMap
    {
        public readonly long Length;
        public readonly int[]? Checkpoints;
        public PositionMap(long length, int[]? checkpoints) { Length = length; Checkpoints = checkpoints; }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, PositionMap> PositionMaps = new();
    private static bool HasSurrogates(string text) => text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0;
    private static PositionMap MapOf(string text) => PositionMaps.GetValue(text, BuildMap);
    private static PositionMap BuildMap(string text)
    {
        if (!HasSurrogates(text)) return new PositionMap(text.Length, null);
        var checkpoints = new int[(text.Length >> CheckpointShift) + 1];
        var count = 0;
        var position = 0L;
        for (var offset = 0; offset < text.Length; offset += Width(text, offset), position++)
        if ((position & ((1 << CheckpointShift) - 1)) == 0) checkpoints[count++] = offset;
        System.Array.Resize(ref checkpoints, count);
        return new PositionMap(position, checkpoints);
    }
    public static long CodePointLength(string text)
    {
        if (text.Length >= IndexedLength) return MapOf(text).Length;
        return HasSurrogates(text) ? CountCodePoints(text, 0, text.Length) : text.Length;
    }
    public static int OffsetOf(string text, long position)
    {
        if (position <= 0L) return 0;
        if (position >= text.Length) return text.Length;
        var offset = 0;
        var reached = 0L;
        if (text.Length < IndexedLength)
        {
            if (!HasSurrogates(text)) return (int)position;
        }
        else
        {
            var map = MapOf(text);
            if (map.Checkpoints is null) return (int)position;
            if (position >= map.Length) return text.Length;
            var checkpoint = (int)(position >> CheckpointShift);
            offset = map.Checkpoints[checkpoint];
            reached = (long)checkpoint << CheckpointShift;
        }
        for (; reached < position && offset < text.Length; reached++) offset += Width(text, offset);
        return offset;
    }
    public static long PositionOf(string text, int offset)
    {
        if (offset <= 0) return 0L;
        if (offset > text.Length) offset = text.Length;
        var start = 0;
        var position = 0L;
        if (text.Length < IndexedLength)
        {
            if (!HasSurrogates(text)) return offset;
        }
        else
        {
            var map = MapOf(text);
            if (map.Checkpoints is null) return offset;
            if (offset == text.Length) return map.Length;
            var checkpoint = System.Array.BinarySearch(map.Checkpoints, offset);
            if (checkpoint < 0) checkpoint = ~checkpoint - 1;
            start = map.Checkpoints[checkpoint];
            position = (long)checkpoint << CheckpointShift;
        }
        return position + CountCodePoints(text, start, offset);
    }
    private static long CountCodePoints(string text, int start, int end)
    {
        var count = 0L;
        for (var index = start; index < end; index += Width(text, index)) count++;
        return count;
    }
    private static System.Collections.Generic.HashSet<int> CodePointSet(string set)
    {
        var members = new System.Collections.Generic.HashSet<int>();
        for (var index = 0; index < set.Length; ) members.Add(CodePointAt(set, ref index));
        return members;
    }
    private static int CodePointAt(string text, ref int index)
    {
        var unit = text[index];
        if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            var scalar = char.ConvertToUtf32(unit, text[index + 1]);
            index += 2;
            return scalar;
        }
        index++;
        return unit;
    }
}

public sealed class NyStr : IEquatable<NyStr>
{
    private sealed class Store
    {
        internal readonly char[] Chars;
        internal int Used;
        internal Store(char[] chars, int used) { Chars = chars; Used = used; }
    }
    private const int StoreThreshold = 256;
    private static readonly NyStr EmptyString = new(string.Empty);
    private readonly Store? _store;
    private readonly int _length;
    private string? _text;
    private NyStr(string text) { _text = text; _length = text.Length; }
    private NyStr(Store store, int length) { _store = store; _length = length; }
    public static implicit operator NyStr(string text) => text.Length == 0 ? EmptyString : new NyStr(WellFormed(text));
    private static string WellFormed(string text)
    {
        if (text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') < 0) return text;
        char[]? chars = null;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { index++; continue; }
            if (char.IsSurrogate(text[index])) (chars ??= text.ToCharArray())[index] = '\uFFFD';
        }
        return chars is null ? text : new string(chars);
    }
    public static implicit operator string(NyStr value) => value is null ? null! : value.Text;
    public int Length => _length;
    public string Text => _text ??= new string(_store!.Chars, 0, _length);
    private ReadOnlySpan<char> Chars => _text is { } text ? text.AsSpan() : new ReadOnlySpan<char>(_store!.Chars, 0, _length);
    public void CopyTo(Span<char> destination) => Chars.CopyTo(destination);
    public static NyStr Concat(NyStr left, NyStr right)
    {
        if (right._length == 0) return left;
        if (left._length == 0) return right;
        var total = (long)left._length + right._length;
        if (total > Array.MaxLength) throw new OutOfMemoryException();
        var store = left._store;
        if (store != null && total <= store.Chars.Length && System.Threading.Interlocked.CompareExchange(ref store.Used, (int)total, left._length) == left._length)
        {
            right.Chars.CopyTo(store.Chars.AsSpan(left._length));
            return new NyStr(store, (int)total);
        }
        if (total < StoreThreshold) return new NyStr(string.Concat(left.Chars, right.Chars));
        var chars = new char[(int)System.Math.Min(total * 2L, Array.MaxLength)];
        left.Chars.CopyTo(chars);
        right.Chars.CopyTo(chars.AsSpan(left._length));
        return new NyStr(new Store(chars, (int)total), (int)total);
    }
    public bool Equals(NyStr? other) => other is not null && (ReferenceEquals(this, other) || Chars.SequenceEqual(other.Chars));
    public override bool Equals(object? obj) => obj is NyStr other && Equals(other);
    public override int GetHashCode() => string.GetHashCode(Chars);
    public override string ToString() => Text;
    public static bool operator ==(NyStr? left, NyStr? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(NyStr? left, NyStr? right) => !(left == right);
}

public sealed class NyMap<K, V> : IEquatable<NyMap<K, V>> where K : notnull
{
    private readonly System.Collections.Immutable.ImmutableDictionary<K, (long Order, V Value)> entries;
    private readonly System.Collections.Immutable.ImmutableSortedDictionary<long, K> order;
    private readonly long next;
    private NyMap(System.Collections.Immutable.ImmutableDictionary<K, (long Order, V Value)> entries, System.Collections.Immutable.ImmutableSortedDictionary<long, K> order, long next)
    {
        this.entries = entries;
        this.order = order;
        this.next = next;
    }
    private static readonly NyMap<K, V> EmptyMap = new(System.Collections.Immutable.ImmutableDictionary.Create<K, (long Order, V Value)>(System.Collections.Generic.EqualityComparer<K>.Default), System.Collections.Immutable.ImmutableSortedDictionary<long, K>.Empty, 0L);
    public static NyMap<K, V> Empty() => EmptyMap;
    public long Size => entries.Count;
    public bool ContainsKey(K key) => entries.ContainsKey(key);
    public NyOption<V> Get(K key) => entries.TryGetValue(key, out var entry) ? NyOption<V>.Some(entry.Value) : NyOption<V>.None();
    public V GetOrDefault(K key, V fallback) => entries.TryGetValue(key, out var entry) ? entry.Value : fallback;
    public NyMap<K, V> Insert(K key, V value) =>
    entries.TryGetValue(key, out var entry)
    ? new(entries.SetItem(key, (entry.Order, value)), order, next)
    : new(entries.SetItem(key, (next, value)), order.Add(next, key), next + 1L);
    public NyMap<K, V> Union(NyMap<K, V> other) =>
    other.entries.Count == 0 ? this : entries.Count == 0 ? other : other.Fold(this, (map, key, value) => map.Insert(key, value));
    public bool Equals(NyMap<K, V>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (entries.Count != other.entries.Count) return false;
        var values = System.Collections.Generic.EqualityComparer<V>.Default;
        foreach (var pair in entries) if (!other.entries.TryGetValue(pair.Key, out var entry) || !values.Equals(pair.Value.Value, entry.Value)) return false;
        return true;
    }
    public override bool Equals(object? obj) => obj is NyMap<K, V> other && Equals(other);
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var pair in entries) unchecked { hash += System.HashCode.Combine(pair.Key, pair.Value.Value); }
        return hash;
    }
    public NyMap<K, V> Remove(K key) =>
    entries.TryGetValue(key, out var entry) ? new(entries.Remove(key), order.Remove(entry.Order), next) : this;
    public S Fold<S>(S seed, System.Func<S, K, V, S> folder)
    {
        var state = seed;
        foreach (var key in order.Values) state = folder(state, key, entries[key].Value);
        return state;
    }
    public static NyMap<K, V> FromList(NyList<V> items, System.Func<V, K> keyOf) => items.Fold(EmptyMap, (map, item) => map.Insert(keyOf(item), item));
    public NyMap<K, V> Filter(System.Func<K, V, bool> keep) => Fold(this, (map, key, value) => keep(key, value) ? map : map.Remove(key));
    public NyMap<K, U> MapValues<U>(System.Func<K, V, U> mapper)
    {
        var mapped = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<K, (long Order, U Value)>(System.Collections.Generic.EqualityComparer<K>.Default);
        foreach (var key in order.Values)
        {
            var entry = entries[key];
            mapped.Add(key, (entry.Order, mapper(key, entry.Value)));
        }
        return new NyMap<K, U>(mapped.ToImmutable(), order, next);
    }
    public NyList<K> Keys()
    {
        var items = new K[order.Count];
        var index = 0;
        foreach (var key in order.Values) items[index++] = key;
        return NyList<K>.FromOwnedArray(items);
    }
    public NyList<V> Values()
    {
        var items = new V[order.Count];
        var index = 0;
        foreach (var key in order.Values) items[index++] = entries[key].Value;
        return NyList<V>.FromOwnedArray(items);
    }
}

public sealed class NySet<T> : IEquatable<NySet<T>> where T : notnull
{
    private readonly System.Collections.Immutable.ImmutableDictionary<T, long> items;
    private readonly System.Collections.Immutable.ImmutableSortedDictionary<long, T> order;
    private readonly long next;
    private NySet(System.Collections.Immutable.ImmutableDictionary<T, long> items, System.Collections.Immutable.ImmutableSortedDictionary<long, T> order, long next)
    {
        this.items = items;
        this.order = order;
        this.next = next;
    }
    private static readonly NySet<T> EmptySet = new(System.Collections.Immutable.ImmutableDictionary.Create<T, long>(System.Collections.Generic.EqualityComparer<T>.Default), System.Collections.Immutable.ImmutableSortedDictionary<long, T>.Empty, 0L);
    public static NySet<T> Empty() => EmptySet;
    public static NySet<T> FromList(NyList<T> source) => source.Fold(EmptySet, (set, item) => set.Insert(item));
    public long Size => items.Count;
    public bool Contains(T item) => items.ContainsKey(item);
    public NySet<T> Insert(T item) => items.ContainsKey(item) ? this : new(items.Add(item, next), order.Add(next, item), next + 1L);
    public NySet<T> Remove(T item) => items.TryGetValue(item, out var number) ? new(items.Remove(item), order.Remove(number), next) : this;
    public NySet<T> Union(NySet<T> other) => other.Fold(this, (set, item) => set.Insert(item));
    public NySet<T> Filter(System.Func<T, bool> predicate) => Fold(this, (set, item) => predicate(item) ? set : set.Remove(item));
    public NySet<U> Map<U>(System.Func<T, U> mapper) where U : notnull
    {
        var result = NySet<U>.Empty();
        foreach (var item in order.Values) result = result.Insert(mapper(item));
        return result;
    }
    public NySet<T> Intersection(NySet<T> other) => Fold(this, (set, item) => other.Contains(item) ? set : set.Remove(item));
    public NySet<T> Difference(NySet<T> other) => other.Size < Size ? other.Fold(this, (set, item) => set.Remove(item)) : Fold(this, (set, item) => other.Contains(item) ? set.Remove(item) : set);
    public bool IsSubsetOf(NySet<T> other)
    {
        if (items.Count > other.items.Count) return false;
        foreach (var item in items.Keys) if (!other.items.ContainsKey(item)) return false;
        return true;
    }
    public bool Equals(NySet<T>? other) => other is not null && (ReferenceEquals(this, other) || (items.Count == other.items.Count && IsSubsetOf(other)));
    public override bool Equals(object? obj) => obj is NySet<T> other && Equals(other);
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var item in items.Keys) unchecked { hash += System.Collections.Generic.EqualityComparer<T>.Default.GetHashCode(item); }
        return hash;
    }
    public S Fold<S>(S seed, System.Func<S, T, S> folder)
    {
        var state = seed;
        foreach (var item in order.Values) state = folder(state, item);
        return state;
    }
    public NyList<T> ToList()
    {
        var result = new T[order.Count];
        var index = 0;
        foreach (var item in order.Values) result[index++] = item;
        return NyList<T>.FromOwnedArray(result);
    }
}

