// Today's representations versus in-place buffers for snap-like workloads.
using System;
using System.Diagnostics;
using Nyrwvy.Generated;

static class Bench
{
    static void Measure(string name, int reps, Func<long> run)
    {
        for (int i = 0; i < 3; i++) run(); // warm-up
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        long sink = 0;
        for (int i = 0; i < reps; i++) sink += run();
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
        Console.WriteLine($"{name,-44} {sw.Elapsed.TotalMilliseconds / reps,9:F3} ms {alloc / reps,12:N0} B/op  ({sink % 7})");
    }

    static void Main()
    {
        const int Cells = 213 * 60, Bytes = 64 * 1024;
        var list = NyList<long>.Empty();
        for (int i = 0; i < Cells; i++) list = list.AppendItem(0);
        var cells = new long[Cells];
        Console.WriteLine($"A. one frame: set every cell of {Cells} (213x60)");
        Measure("A1 List<Int64> SetAt (today)", 200, () => { var l = list; for (int i = 0; i < Cells; i++) l = l.SetAt(i, i); list = l; return l.Length; });
        Measure("A2 long[] in place", 200, () => { for (int i = 0; i < Cells; i++) cells[i] = i; return cells.Length; });
        Measure("A3 long[] copied at entry, then in place", 200, () => { var c = (long[])cells.Clone(); for (int i = 0; i < Cells; i++) c[i] = i; cells = c; return c.Length; });
        Console.WriteLine($"B. build {Bytes} bytes one at a time");
        Measure("B1 List<Int64> AppendItem (today)", 50, () => { var l = NyList<long>.Empty(); for (int i = 0; i < Bytes; i++) l = l.AppendItem(i & 0xFF); return l.Length; });
        Measure("B2 latin-1 String concat (today)", 5, () => { NyStr s = ""; for (int i = 0; i < Bytes; i++) s = NyStr.Concat(s, ((char)(i & 0xFF)).ToString()); return s.Text.Length; });
        var buf = new byte[Bytes];
        Measure("B3 byte[] in place", 50, () => { for (int i = 0; i < Bytes; i++) buf[i] = (byte)i; return buf.Length; });
        Measure("B4 byte[] copied at entry, then in place", 50, () => { var b = (byte[])buf.Clone(); for (int i = 0; i < Bytes; i++) b[i] = (byte)i; buf = b; return b.Length; });
        Console.WriteLine("C. read every byte of a 64 KiB block");
        var big = NyList<long>.Empty(); for (int i = 0; i < Bytes; i++) big = big.AppendItem(i & 0xFF);
        Measure("C1 List<Int64> ItemAt (today)", 50, () => { long s = 0; for (int i = 0; i < Bytes; i++) s += big.ItemAt(i).Value; return s; });
        Measure("C2 byte[] index", 50, () => { long s = 0; for (int i = 0; i < Bytes; i++) s += buf[i]; return s; });
    }
}
