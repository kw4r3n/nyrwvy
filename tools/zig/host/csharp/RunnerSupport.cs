sealed class RunnerInput
{
    readonly string[] tokens;
    int next;
    public RunnerInput(string text) { tokens = text.Length == 0 ? Array.Empty<string>() : text.Split(' '); }
    string Next() => next < tokens.Length ? tokens[next++] : throw new InvalidOperationException("Runner input ended early.");
    public NyUnit Unit() { Next(); return NyUnit.Value; }
    public bool Bool() => Next() == "1";
    public int Count() => int.Parse(Next(), NumberStyles.None, CultureInfo.InvariantCulture);
    public long Long() => long.Parse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    public uint UInt() => uint.Parse(Next(), NumberStyles.None, CultureInfo.InvariantCulture);
    public double Double() => BitConverter.UInt64BitsToDouble(ulong.Parse(Next(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    public string String() => RunnerText.FromToken(Next());
    public string Key() => Next().Substring(1);
    public T[] Items<T>(Func<T> read) { var items = new T[Count()]; for (int index = 0; index < items.Length; index++) items[index] = read(); return items; }
}
static class RunnerText
{
    public static string FromToken(string token) => new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(Convert.FromBase64String(token.Substring(1)).AsSpan()));
    public static string ToToken(string text) => "s" + Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan()));
}
sealed class RunnerKeyOverflow() : Exception("The key exceeds every case key.");
sealed class RunnerKey
{
    readonly StringBuilder text = new();
    readonly int limit;
    bool first = true;
    RunnerKey(int limit) { this.limit = limit; }
    public static string? Of(int limit, Action<RunnerKey> write) { var key = new RunnerKey(limit); try { write(key); } catch (RunnerKeyOverflow) { return null; } return key.text.ToString(); }
    public void Add(string token) { if (!first) text.Append(','); first = false; text.Append(token); if (text.Length > limit) throw new RunnerKeyOverflow(); }
    public void Add(long value) => Add(value.ToString(CultureInfo.InvariantCulture));
    public void Null() => Add("null");
    public void String(string? value) { if (value == null) Null(); else if (value.Length > limit) throw new RunnerKeyOverflow(); else Add(RunnerText.ToToken(value)); }
    public void Double(double value) => Add((double.IsNaN(value) ? 0x7ff8000000000000UL : BitConverter.DoubleToUInt64Bits(value)).ToString("x16", CultureInfo.InvariantCulture));
}
sealed class FakeResponse<T>
{
    public static readonly FakeResponse<T> Unmatched = new() { Kind = 4 };
    public static readonly FakeResponse<T> Exhausted = new() { Kind = 5 };
    public int Kind;
    public T Value = default!;
    public string Message = "";
    public long Delay;
    public FakeResponse<T>? Inner;
    public static FakeResponse<T> Read(RunnerInput input, Func<T> read)
    {
        var response = new FakeResponse<T> { Kind = input.Count() };
        switch (response.Kind)
        {
            case 0: case 2: response.Value = read(); break;
            case 1: response.Message = input.String(); break;
            case 3: response.Delay = input.Long(); response.Inner = Read(input, read); break;
            default: throw new InvalidOperationException("Invalid runner input.");
        }
        return response;
    }
}
sealed class FakeScript<T>
{
    public readonly bool Keyed;
    public readonly int KeyLimit;
    readonly string[] keys;
    readonly FakeResponse<T>[][] queues;
    readonly int[] next;
    public FakeScript(RunnerInput input, Func<T> read)
    {
        Keyed = input.Bool();
        int count = input.Count();
        keys = new string[count];
        queues = new FakeResponse<T>[count][];
        next = new int[count];
        for (int index = 0; index < count; index++)
        {
            if (Keyed) { keys[index] = input.Key(); KeyLimit = Math.Max(KeyLimit, keys[index].Length); }
            queues[index] = input.Items(() => FakeResponse<T>.Read(input, read));
        }
    }
    public FakeResponse<T> Select(string? key)
    {
        int selected = Keyed ? (key == null ? -1 : Array.IndexOf(keys, key)) : 0;
        if (selected < 0) return FakeResponse<T>.Unmatched;
        int local = next[selected]++;
        return local < queues[selected].Length ? queues[selected][local] : FakeResponse<T>.Exhausted;
    }
}
