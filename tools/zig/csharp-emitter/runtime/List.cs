public sealed class NyList<T> : IEquatable<NyList<T>>
{
    // A list is a persistent vector, as Clojure's vectors are: a 32-way trie of full leaves holds the
    // first items, a multiple of 32, and a tail holds the last 1 to 32. A new list shares every node of
    // the old one and copies only the nodes on one path, so AppendItem, DropLast, Last, ItemAt and a
    // Slice take O(log32 n) and never copy the list; a push after a pop copies at most the tail.
    // The list's items are the trie positions from _start up to _count. A Slice moves _start forward
    // instead of copying, so dequeuing with Slice(1, n) is O(log32 n); it copies only when the slice
    // would keep fewer than half of the positions up to its end, which bounds what a small slice keeps
    // alive and makes repeated dequeues amortised O(log32 n) each.
    // DropLast and Slice may leave nodes before _start or past _count in the trie. No reader looks
    // outside them, and pushing a leaf copies every trie node on its path, so a trie node is never
    // written after a list containing it exists.
    // A store's Used count only grows. AppendItem writes a tail slot in place only after a
    // compare-exchange moves Used from exactly the list's tail count, so each slot is written once, by
    // the one caller whose list ends at Used, and before any list that contains it is constructed. A
    // caller that loses the race copies the tail. A leaf is a full store (Used is its length), so no
    // append writes into it. Lists reach other threads through task starts and results, which order
    // those writes before the reads.
    private const int Bits = 5;
    private const int Width = 1 << Bits;
    private const int Mask = Width - 1;
    private sealed class Store
    {
        internal readonly T[] Items;
        internal int Used;
        internal Store(T[] items, int used) { Items = items; Used = used; }
    }
    private static readonly NyList<T> EmptyList = new(0, Bits, null, new Store(Array.Empty<T>(), 0));
    // _start and _count are trie positions: the first item and one past the last.
    private readonly int _start;
    private readonly int _count;
    // _shift is Bits times the trie's height above its leaves; the root's children are leaves at Bits.
    private readonly int _shift;
    // Interior nodes are object[Width] whose children are interior nodes or leaf stores; null when empty.
    private readonly object[]? _root;
    private readonly Store _tail;
    private NyList(int count, int shift, object[]? root, Store tail) : this(0, count, shift, root, tail) { }
    private NyList(int start, int count, int shift, object[]? root, Store tail) { _start = start; _count = count; _shift = shift; _root = root; _tail = tail; }
    private int Count => _count - _start;
    private int TailCount => _count == 0 ? 0 : ((_count - 1) & Mask) + 1;
    private int TrieCount => _count - TailCount;
    internal static NyList<T> FromOwnedArray(T[] items) => FromOwned(items, items.Length);
    // A buffer of at most Width items becomes the tail as it is; a longer one is split into leaves.
    private static NyList<T> FromOwned(T[] items, int count) =>
        count == 0 ? EmptyList : items.Length <= Width ? new(count, Bits, null, new Store(items, count)) : FromSpan(new ReadOnlySpan<T>(items, 0, count));
    private static NyList<T> FromSpan(ReadOnlySpan<T> items)
    {
        if (items.Length == 0) return EmptyList;
        var trieCount = (items.Length - 1) & ~Mask;
        object[]? root = null;
        var shift = Bits;
        for (var start = 0; start < trieCount; start += Width) root = PushLeaf(root, ref shift, start, new Store(items.Slice(start, Width).ToArray(), Width));
        return new(items.Length, shift, root, new Store(items.Slice(trieCount).ToArray(), items.Length - trieCount));
    }
    public static NyList<T> Empty() => EmptyList;
    public static NyList<T> Create(params T[] items) => FromSpan(items);
    public long Length => Count;
    private Store LeafFor(int index)
    {
        object node = _root!;
        for (var level = _shift; level > Bits; level -= Bits) node = ((object[])node)[(index >> level) & Mask];
        return (Store)((object[])node)[(index >> Bits) & Mask];
    }
    // Chunk is the leaf or the tail holding the positions from start, a multiple of Width, onward.
    private ReadOnlySpan<T> Chunk(int start) =>
        start >= TrieCount ? new(_tail.Items, 0, TailCount) : new(LeafFor(start).Items, 0, Width);
    // Segment is the rest of the chunk from position at; stepping at to Next(at) walks the items in order.
    private ReadOnlySpan<T> Segment(int at) => Chunk(at & ~Mask)[(at & Mask)..];
    private static int Next(int at) => (at | Mask) + 1;
    private T Get(int at) => at >= TrieCount ? _tail.Items[at - TrieCount] : LeafFor(at).Items[at & Mask];
    private static object[] PushLeaf(object[]? root, ref int shift, int trieCount, Store leaf)
    {
        if (root is null) return NewPath(Bits, leaf);
        if (trieCount == 1 << (shift + Bits))
        {
            var grown = new object[Width];
            grown[0] = root;
            grown[1] = NewPath(shift, leaf);
            shift += Bits;
            return grown;
        }
        return SetLeaf(root, shift, trieCount, leaf);
    }
    private static object[] NewPath(int level, Store leaf)
    {
        var node = new object[Width];
        node[0] = level == Bits ? leaf : NewPath(level - Bits, leaf);
        return node;
    }
    private static object[] SetLeaf(object[] node, int level, int index, Store leaf)
    {
        var copy = (object[])node.Clone();
        var slot = (index >> level) & Mask;
        copy[slot] = level == Bits ? leaf : node[slot] is object[] child ? SetLeaf(child, level - Bits, index, leaf) : NewPath(level - Bits, leaf);
        return copy;
    }
    public NyOption<T> ItemAt(long index)
    {
        if (index < 0L || index >= Count) return NyOption<T>.None();
        return NyOption<T>.Some(Get(_start + (int)index));
    }
    public NyOption<T> Last() => Count > 0 ? NyOption<T>.Some(_tail.Items[TailCount - 1]) : NyOption<T>.None();
    public NyList<T> DropLast() => Count > 1 ? Take(_start, _count - 1) : Empty();
    // Take shares the trie and the store that holds the position before count; start < count <= _count.
    private NyList<T> Take(int start, int count)
    {
        var trieCount = TrieCount;
        if (count > trieCount) return new(start, count, _shift, _root, _tail);
        var leafStart = (count - 1) & ~Mask;
        var leaf = LeafFor(leafStart);
        return leafStart == 0 ? new(start, count, Bits, null, leaf) : new(start, count, _shift, _root, leaf);
    }
    internal T[] ToArray() => CopyRange(_start, _count);
    private T[] CopyRange(int from, int to)
    {
        var result = new T[to - from];
        for (var start = from & ~Mask; start < to; start += Width)
        {
            var chunk = Chunk(start);
            var first = System.Math.Max(from - start, 0);
            var end = System.Math.Min(to - start, chunk.Length);
            chunk[first..end].CopyTo(result.AsSpan(start + first - from));
        }
        return result;
    }
    // Slice clamps both bounds to [0, Length] like StringSlice; end <= start gives the empty list.
    // A slice shares the list's nodes; one that moves the start copies instead when it would keep fewer
    // than half of the positions up to its end.
    public NyList<T> Slice(long start, long end)
    {
        var from = _start + (int)System.Math.Clamp(start, 0L, (long)Count);
        var to = _start + (int)System.Math.Clamp(end, 0L, (long)Count);
        if (to <= from) return Empty();
        if (from == _start && to == _count) return this;
        if (from > _start && (to - from) * 2L < to) return FromOwnedArray(CopyRange(from, to));
        return Take(from, to);
    }
    // SetAt copies the one leaf, or the tail, holding the index and the trie nodes on its path, so it is
    // O(log32 n); an index outside [0, Length) returns the list itself.
    public NyList<T> SetAt(long index, T item)
    {
        if (index < 0L || index >= Count) return this;
        var at = _start + (int)index;
        var trieCount = TrieCount;
        if (at >= trieCount)
        {
            var tailCount = TailCount;
            var next = new T[Width];
            Array.Copy(_tail.Items, next, tailCount);
            next[at - trieCount] = item;
            return new(_start, _count, _shift, _root, new Store(next, tailCount));
        }
        var leaf = (T[])LeafFor(at).Items.Clone();
        leaf[at & Mask] = item;
        return new(_start, _count, _shift, SetLeaf(_root!, _shift, at, new Store(leaf, Width)), _tail);
    }
    // InsertAt clamps the index to [0, Length]; it shares the items before the index (a Slice) and
    // appends the item and then the items from the index, so it is O(log32 n + Length - index).
    public NyList<T> InsertAt(long index, T item)
    {
        var split = (int)System.Math.Clamp(index, 0L, (long)Count);
        return Slice(0L, split).AppendItem(item).AppendFrom(this, _start + split);
    }
    // RemoveAt shares the items before the index and appends the items after it, so it is
    // O(log32 n + Length - index); removing the first or last item is a Slice or DropLast. An index
    // outside [0, Length) returns the list itself.
    public NyList<T> RemoveAt(long index)
    {
        if (index < 0L || index >= Count) return this;
        var split = (int)index;
        if (split == Count - 1) return DropLast();
        if (split == 0) return Slice(1L, Count);
        return Slice(0L, split).AppendFrom(this, _start + split + 1);
    }
    // AppendFrom appends the source's items at trie positions [from, source._count) in order.
    private NyList<T> AppendFrom(NyList<T> source, int from)
    {
        var result = this;
        for (var at = from; at < source._count; at = Next(at))
            foreach (var item in source.Segment(at)) result = result.AppendItem(item);
        return result;
    }
    // Reverse copies the items into a new list in the opposite order; 0 or 1 items return the list itself.
    public NyList<T> Reverse()
    {
        if (Count < 2) return this;
        var items = ToArray();
        Array.Reverse(items);
        return FromOwnedArray(items);
    }
    public NyList<T> AppendItem(T item)
    {
        var tail = _tail;
        var tailCount = TailCount;
        if (tailCount < Width)
        {
            if (tailCount < tail.Items.Length && System.Threading.Interlocked.CompareExchange(ref tail.Used, tailCount + 1, tailCount) == tailCount)
            {
                tail.Items[tailCount] = item;
                return new(_start, _count + 1, _shift, _root, tail);
            }
            var next = new T[Width];
            Array.Copy(tail.Items, next, tailCount);
            next[tailCount] = item;
            return new(_start, _count + 1, _shift, _root, new Store(next, tailCount + 1));
        }
        var shift = _shift;
        var root = PushLeaf(_root, ref shift, _count - Width, tail);
        var fresh = new T[Width];
        fresh[0] = item;
        return new(_start, _count + 1, shift, root, new Store(fresh, 1));
    }
    public NyList<T> Concat(NyList<T> other)
    {
        if (other.Count == 0) return this;
        if (Count == 0) return other;
        var result = this;
        for (var at = other._start; at < other._count; at = Next(at))
            foreach (var item in other.Segment(at)) result = result.AppendItem(item);
        return result;
    }
    public S Fold<S>(S seed, Func<S, T, S> folder)
    {
        var state = seed;
        for (var at = _start; at < _count; at = Next(at))
            foreach (var item in Segment(at)) state = folder(state, item);
        return state;
    }
    // ListEffectFold's Result mode: stops iterating once `proceed` is false (after the first Err).
    public S FoldWhile<S>(S seed, Func<S, T, S> folder, Func<S, bool> proceed)
    {
        var state = seed;
        for (var at = _start; at < _count; at = Next(at))
            foreach (var item in Segment(at))
            {
                if (!proceed(state)) return state;
                state = folder(state, item);
            }
        return state;
    }
    public NyList<U> Map<U>(Func<T, U> mapper)
    {
        var next = new U[Count];
        for (var at = _start; at < _count; at = Next(at))
        {
            var segment = Segment(at);
            for (var i = 0; i < segment.Length; i++) next[at - _start + i] = mapper(segment[i]);
        }
        return NyList<U>.FromOwnedArray(next);
    }
    // ListScan: the seed, then the folder's state after each item in order. One array of Count + 1
    // items is filled in a single pass and becomes the list without another copy.
    public NyList<S> Scan<S>(S seed, Func<S, T, S> folder)
    {
        var next = new S[Count + 1];
        var state = seed;
        next[0] = state;
        for (var at = _start; at < _count; at = Next(at))
        {
            var segment = Segment(at);
            for (var i = 0; i < segment.Length; i++)
            {
                state = folder(state, segment[i]);
                next[at - _start + i + 1] = state;
            }
        }
        return NyList<S>.FromOwnedArray(next);
    }
    // ListZipWith: item i is combiner(this[i], other[i]) for every i below the shorter length, as
    // Python zip; the extra items of the longer list are not visited.
    public NyList<V> ZipWith<U, V>(NyList<U> other, Func<T, U, V> combiner)
    {
        var count = System.Math.Min(Count, other.Count);
        var next = new V[count];
        for (var i = 0; i < count; i++) next[i] = combiner(Get(_start + i), other.Get(other._start + i));
        return NyList<V>.FromOwnedArray(next);
    }
    // ListFlatMap: the mapper's lists concatenated in source order. Each part is copied once into
    // one buffer, so the time is linear in the output length rather than quadratic as repeated Concat.
    public NyList<U> FlatMap<U>(Func<T, NyList<U>> mapper)
    {
        var parts = new NyList<U>[Count];
        var total = 0;
        var index = 0;
        for (var at = _start; at < _count; at = Next(at))
            foreach (var item in Segment(at))
            {
                var part = mapper(item);
                parts[index++] = part;
                total = checked(total + part.Count);
            }
        var next = new U[total];
        var offset = 0;
        foreach (var part in parts)
            for (var at = part._start; at < part._count; at = Next(at))
            {
                var segment = part.Segment(at);
                segment.CopyTo(next.AsSpan(offset));
                offset += segment.Length;
            }
        return NyList<U>.FromOwnedArray(next);
    }
    // ListSortBy: top-down merge sort, left half = length / 2, taking the right head only when
    // lessThan(right, left) holds. The algorithm fixes the result for any callback, so an
    // inconsistent callback still yields a deterministic permutation; a strict order is stable.
    public NyList<T> SortBy(Func<T, T, bool> lessThan)
    {
        var items = ToArray();
        if (items.Length > 1) SortRange(items, new T[items.Length], 0, items.Length, lessThan);
        return FromOwnedArray(items);
    }
    private static void SortRange(T[] items, T[] scratch, int start, int end, Func<T, T, bool> lessThan)
    {
        var length = end - start;
        if (length < 2) return;
        var middle = start + length / 2;
        SortRange(items, scratch, start, middle, lessThan);
        SortRange(items, scratch, middle, end, lessThan);
        int left = start, right = middle, next = start;
        while (left < middle && right < end) scratch[next++] = lessThan(items[right], items[left]) ? items[right++] : items[left++];
        while (left < middle) scratch[next++] = items[left++];
        while (right < end) scratch[next++] = items[right++];
        Array.Copy(scratch, start, items, start, length);
    }
    // ListMinBy: scan left to right from the first item, replacing the best only when
    // lessThan(item, best) holds, so the first of equal items wins for any callback.
    public NyOption<T> MinBy(Func<T, T, bool> lessThan)
    {
        if (Count == 0) return NyOption<T>.None();
        var best = Get(_start);
        for (var at = _start; at < _count; at = Next(at))
        {
            var segment = Segment(at);
            for (var i = at == _start ? 1 : 0; i < segment.Length; i++) if (lessThan(segment[i], best)) best = segment[i];
        }
        return NyOption<T>.Some(best);
    }
    // ListMaxBy: the same scan replacing the best only when lessThan(best, item) holds, so the
    // first of equal items wins, as Python max.
    public NyOption<T> MaxBy(Func<T, T, bool> lessThan)
    {
        if (Count == 0) return NyOption<T>.None();
        var best = Get(_start);
        for (var at = _start; at < _count; at = Next(at))
        {
            var segment = Segment(at);
            for (var i = at == _start ? 1 : 0; i < segment.Length; i++) if (lessThan(best, segment[i])) best = segment[i];
        }
        return NyOption<T>.Some(best);
    }
    public NyList<T> Filter(Func<T, bool> predicate)
    {
        var buffer = new T[Count];
        var count = 0;
        for (var at = _start; at < _count; at = Next(at))
            foreach (var item in Segment(at)) if (predicate(item)) buffer[count++] = item;
        return FromOwned(buffer, count);
    }
    // FindIndex and IndexOf give the zero-based position of the first match, scanning left to right.
    public NyOption<long> FindIndex(Func<T, bool> predicate)
    {
        for (var at = _start; at < _count; at = Next(at))
        {
            var segment = Segment(at);
            for (var i = 0; i < segment.Length; i++) if (predicate(segment[i])) return NyOption<long>.Some(at - _start + i);
        }
        return NyOption<long>.None();
    }
    public NyOption<long> IndexOf(T item) => FindIndex(candidate => NyEquality.Equal(candidate, item));
    public bool Contains(T item) => IndexOf(item).HasValue;
    // LastIndexOf scans right to left, so an item near the end, as on a stack, is found in time proportional to its distance from the end.
    public NyOption<long> LastIndexOf(T item)
    {
        if (Count == 0) return NyOption<long>.None();
        for (var start = TrieCount; start + Width > _start; start -= Width)
        {
            var chunk = Chunk(start);
            var first = System.Math.Max(_start - start, 0);
            for (var i = chunk.Length - 1; i >= first; i--) if (NyEquality.Equal(chunk[i], item)) return NyOption<long>.Some(start + i - _start);
        }
        return NyOption<long>.None();
    }
    public bool Any(Func<T, bool> predicate)
    {
        for (var at = _start; at < _count; at = Next(at))
            foreach (var item in Segment(at)) if (predicate(item)) return true;
        return false;
    }
    public bool All(Func<T, bool> predicate)
    {
        for (var at = _start; at < _count; at = Next(at))
            foreach (var item in Segment(at)) if (!predicate(item)) return false;
        return true;
    }
    // Two lists of one length whose starts are equal modulo Width split into segments at the same items;
    // otherwise the other list's items are looked up one by one.
    public bool Equals(NyList<T>? other)
    {
        if (other is null || other.Count != Count) return false;
        if (ReferenceEquals(other, this)) return true;
        var comparer = System.Collections.Generic.EqualityComparer<T>.Default;
        var offset = other._start - _start;
        var aligned = (offset & Mask) == 0;
        for (var at = _start; at < _count; at = Next(at))
        {
            var mine = Segment(at);
            if (aligned)
            {
                var theirs = other.Segment(at + offset);
                for (var i = 0; i < mine.Length; i++) if (!comparer.Equals(mine[i], theirs[i])) return false;
            }
            else
                for (var i = 0; i < mine.Length; i++) if (!comparer.Equals(mine[i], other.Get(at + offset + i))) return false;
        }
        return true;
    }
    public override bool Equals(object? obj) => obj is NyList<T> other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);
        for (var at = _start; at < _count; at = Next(at))
            foreach (var item in Segment(at)) hash.Add(item);
        return hash.ToHashCode();
    }
}