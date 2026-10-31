public sealed class NyBytes : IEquatable<NyBytes>
{
    private static readonly NyBytes EmptyBytes = new(Array.Empty<byte>(), 0);
    private byte[] _items;
    private int _length;
    private object? _owner;
    private NyBytes(byte[] items, int length, object? owner = null) { _items = items; _length = length; _owner = owner; }
    public void ReleaseOwner() => _owner = null;
    private NyBytes Shared() { _owner = null; return this; }
    public static NyBytes Empty() => EmptyBytes;
    public static NyBytes FromOwnedArray(byte[] items) => items.Length == 0 ? EmptyBytes : new NyBytes(items, items.Length);
    public long Length => _length;
    public ReadOnlySpan<byte> Span => new(_items, 0, _length);
    private static byte[] Allocate(long length) => length > Array.MaxLength ? throw new OutOfMemoryException() : new byte[length];
    public static NyBytes Fill(long length, long value)
    {
        if (length <= 0L) return EmptyBytes;
        var items = Allocate(length);
        Array.Fill(items, unchecked((byte)value));
        return new NyBytes(items, items.Length);
    }
    public NyOption<long> Get(long index) => index >= 0L && index < _length ? NyOption<long>.Some(_items[index]) : NyOption<long>.None();
    public NyBytes Set(long index, long value, object? owner, bool inPlace)
    {
        if (index < 0L || index >= _length) return inPlace ? this : Shared();
        if (inPlace && owner is not null && ReferenceEquals(_owner, owner)) { _items[index] = unchecked((byte)value); return this; }
        var items = Span.ToArray();
        items[index] = unchecked((byte)value);
        return new NyBytes(items, items.Length, owner);
    }
    public NyBytes Append(long value, object? owner, bool inPlace)
    {
        if (inPlace && owner is not null && ReferenceEquals(_owner, owner))
        {
            if (_length == _items.Length) Array.Resize(ref _items, (int)Math.Min(Array.MaxLength, Math.Max(16L, 2L * _length)));
            if (_length == _items.Length) throw new OutOfMemoryException();
            _items[_length++] = unchecked((byte)value);
            return this;
        }
        // Room to grow, so the appends that follow in the same invocation write in place.
        var items = Allocate(Math.Max(_length + 1L, Math.Min(Array.MaxLength, Math.Max(16L, 2L * (_length + 1L)))));
        Span.CopyTo(items);
        items[_length] = unchecked((byte)value);
        return new NyBytes(items, _length + 1, owner);
    }
    public NyBytes Concat(NyBytes other)
    {
        if (other._length == 0) return Shared();
        if (_length == 0) return other.Shared();
        var items = Allocate((long)_length + other._length);
        Span.CopyTo(items);
        other.Span.CopyTo(items.AsSpan(_length));
        return new NyBytes(items, items.Length);
    }
    public NyBytes Slice(long start, long end)
    {
        var from = Math.Clamp(start, 0L, _length);
        var to = Math.Clamp(end, 0L, _length);
        if (to <= from) return EmptyBytes;
        if (from == 0L && to == _length) return Shared();
        return FromOwnedArray(Span.Slice((int)from, (int)(to - from)).ToArray());
    }
    // One byte per code point, as NyText.DecodeUtf8Lossy reads: a surrogate pair or any unit above U+00FF is 0xFF.
    public static NyBytes FromLatin1(string text)
    {
        var items = new byte[text.Length];
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var unit = text[index];
            if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) index++;
            items[count++] = unit <= '\u00FF' ? (byte)unit : (byte)0xFF;
        }
        return count == 0 ? EmptyBytes : new NyBytes(items, count);
    }
    public string ToLatin1() => string.Create(_length, this, static (span, bytes) => { var source = bytes.Span; for (var index = 0; index < source.Length; index++) span[index] = (char)source[index]; });
    public string ToHex() => Convert.ToHexStringLower(Span);
    public bool Equals(NyBytes? other) => other is not null && (ReferenceEquals(this, other) || Span.SequenceEqual(other.Span));
    public override bool Equals(object? obj) => obj is NyBytes other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Span);
        return hash.ToHashCode();
    }
    public override string ToString() => ToHex();
    public static bool operator ==(NyBytes? left, NyBytes? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(NyBytes? left, NyBytes? right) => !(left == right);
}