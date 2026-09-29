namespace Nyrwvy.Core

open System
open System.Globalization
open System.Text

[<RequireQualifiedAccess>]
module CSharpBackend =
    type private SourceWriter() =
        let builder = StringBuilder()
        let mutable indent = 0

        member _.Line(text: string) =
            if text.Length > 0 then
                builder.Append(' ', indent * 4) |> ignore
            builder.Append(text).Append('\n') |> ignore

        member _.Push() = indent <- indent + 1
        member _.Pop() = indent <- indent - 1
        override _.ToString() = builder.ToString()

    let private escapeStringLiteral (value: string) =
        let builder = StringBuilder()
        builder.Append('"') |> ignore
        for character in value do
            match character with
            | '"' -> builder.Append("\\\"") |> ignore
            | '\\' -> builder.Append("\\\\") |> ignore
            | '\r' -> builder.Append("\\r") |> ignore
            | '\n' -> builder.Append("\\n") |> ignore
            | '\t' -> builder.Append("\\t") |> ignore
            | character when int character >= 0x20 && int character <= 0x7e ->
                builder.Append(character) |> ignore
            | character ->
                builder.Append("\\u") |> ignore
                builder.Append((int character).ToString("X4")) |> ignore
        builder.Append('"') |> ignore
        builder.ToString()

    let private renderInt64Literal (value: int64) =
        if value = Int64.MinValue then
            "long.MinValue"
        else
            value.ToString(CultureInfo.InvariantCulture) + "L"

    let private renderFloat64Literal bits =
        "BitConverter.UInt64BitsToDouble(0x" + Float64Bits.toHex bits + "UL)"

    let rec private formatType (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> "string"
        | TypeRef.Primitive PrimitiveType.Bool -> "bool"
        | TypeRef.Primitive PrimitiveType.Unit -> "NyUnit"
        | TypeRef.Primitive PrimitiveType.Int64 -> "long"
        | TypeRef.Primitive PrimitiveType.Float64 -> "double"
        | TypeRef.Named typeId -> CSharpBackendNames.typeName typeId
        | TypeRef.Capability capabilityId -> CSharpBackendNames.capabilityName capabilityId
        | TypeRef.Function(arguments, result) ->
            "NyFunction" + string arguments.Length + "<" + (arguments @ [ result ] |> List.map formatType |> String.concat ", ") + ">"
        | TypeRef.TypeParameter(typeParameterId, _) -> CSharpBackendNames.typeParameterName typeParameterId
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            "NyOption<" + formatType itemType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            "NyResult<" + formatType okType + ", " + formatType errorType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            "NyList<" + formatType itemType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ resultType ]) ->
            "NyTask<" + formatType resultType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> "NyTaskScope"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) ->
            invalidOp $"C# backend received Task with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) ->
            invalidOp $"C# backend received TaskScope with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"C# backend received Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"C# backend received Result with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"C# backend received List with {arguments.Length} type arguments."

    let private emitRuntime (writer: SourceWriter) =
        writer.Line("public readonly record struct NyUnit")
        writer.Line("{")
        writer.Push()
        writer.Line("public static NyUnit Value => default;")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        writer.Line("public readonly record struct NyOption<T>(bool HasValue, T Value)")
        writer.Line("{")
        writer.Push()
        writer.Line("public static NyOption<T> Some(T value) => new(true, value);")
        writer.Line("public static NyOption<T> None() => new(false, default!);")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        writer.Line("public readonly record struct NyResult<TOk, TError>(bool IsOk, TOk OkValue, TError ErrorValue)")
        writer.Line("{")
        writer.Push()
        writer.Line("public static NyResult<TOk, TError> Ok(TOk value) => new(true, value, default!);")
        writer.Line("public static NyResult<TOk, TError> Err(TError error) => new(false, default!, error);")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        // ValueEqual is structural through every emitted value type: records and record structs
        // compare their fields with EqualityComparer<T>.Default (and records their runtime type,
        // i.e. the union variant), and NyList compares length and items in order. String uses
        // ordinal equality. Validation keeps Float64, delegates and tasks out of ValueEqual.
        writer.Line("public static class NyEquality")
        writer.Line("{")
        writer.Push()
        writer.Line("public static bool Equal<T>(T left, T right) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(left, right);")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        // StringLessThan orders by Unicode code point, as StringLength and StringSlice count: a
        // surrogate pair decodes to its scalar value (above every BMP code unit) and an unpaired
        // surrogate code unit stands for itself. Ordinal UTF-16 comparison would put U+10000 and
        // above before U+E000..U+FFFF, so string.CompareOrdinal is not used.
        writer.Line("public static class NyText")
        writer.Line("{")
        writer.Push()
        writer.Line("public static bool LessThan(string left, string right)")
        writer.Line("{")
        writer.Push()
        writer.Line("int i = 0, j = 0;")
        writer.Line("while (i < left.Length && j < right.Length)")
        writer.Line("{")
        writer.Push()
        writer.Line("var a = CodePointAt(left, ref i);")
        writer.Line("var b = CodePointAt(right, ref j);")
        writer.Line("if (a != b) return a < b;")
        writer.Pop()
        writer.Line("}")
        writer.Line("return i >= left.Length && j < right.Length;")
        writer.Pop()
        writer.Line("}")
        // Search in code-point positions: an ordinal UTF-16 match counts only when it starts and ends on a
        // code-point boundary, so it never splits a surrogate pair, and its offset converts to code points.
        writer.Line("public static NyOption<long> IndexOf(string text, string value, long from)")
        writer.Line("{")
        writer.Push()
        writer.Line("var start = System.Math.Max(0L, from);")
        writer.Line("var offset = 0;")
        writer.Line("var position = 0L;")
        writer.Line("while (position < start && offset < text.Length)")
        writer.Line("{")
        writer.Push()
        writer.Line("offset += Width(text, offset);")
        writer.Line("position++;")
        writer.Pop()
        writer.Line("}")
        writer.Line("if (value.Length == 0) return NyOption<long>.Some(position);")
        writer.Line("var found = Find(text, value, offset);")
        writer.Line("return found < 0 ? NyOption<long>.None() : NyOption<long>.Some(position + CountCodePoints(text, offset, found));")
        writer.Pop()
        writer.Line("}")
        writer.Line("public static NyOption<long> LastIndexOf(string text, string value)")
        writer.Line("{")
        writer.Push()
        writer.Line("if (value.Length == 0) return NyOption<long>.Some(CountCodePoints(text, 0, text.Length));")
        writer.Line("var searchEnd = text.Length - 1;")
        writer.Line("while (searchEnd >= value.Length - 1)")
        writer.Line("{")
        writer.Push()
        writer.Line("var found = text.LastIndexOf(value, searchEnd, StringComparison.Ordinal);")
        writer.Line("if (found < 0) return NyOption<long>.None();")
        writer.Line("if (IsBoundary(text, found) && IsBoundary(text, found + value.Length)) return NyOption<long>.Some(CountCodePoints(text, 0, found));")
        writer.Line("searchEnd = found + value.Length - 2;")
        writer.Pop()
        writer.Line("}")
        writer.Line("return NyOption<long>.None();")
        writer.Pop()
        writer.Line("}")
        writer.Line("public static bool StartsWith(string text, string prefix) => text.StartsWith(prefix, StringComparison.Ordinal) && IsBoundary(text, prefix.Length);")
        writer.Line("public static bool EndsWith(string text, string suffix) => text.EndsWith(suffix, StringComparison.Ordinal) && IsBoundary(text, text.Length - suffix.Length);")
        writer.Line("public static NyList<string> Split(string text, string separator)")
        writer.Line("{")
        writer.Push()
        writer.Line("if (separator.Length == 0) return NyList<string>.Create(text);")
        writer.Line("var pieces = new string[4];")
        writer.Line("var count = 0;")
        writer.Line("var start = 0;")
        writer.Line("for (var found = Find(text, separator, 0); found >= 0; found = Find(text, separator, start))")
        writer.Line("{")
        writer.Push()
        writer.Line("if (count == pieces.Length) Array.Resize(ref pieces, count * 2);")
        writer.Line("pieces[count++] = text.Substring(start, found - start);")
        writer.Line("start = found + separator.Length;")
        writer.Pop()
        writer.Line("}")
        writer.Line("Array.Resize(ref pieces, count + 1);")
        writer.Line("pieces[count] = text.Substring(start);")
        writer.Line("return NyList<string>.FromOwnedArray(pieces);")
        writer.Pop()
        writer.Line("}")
        writer.Line("private static int Find(string text, string value, int offset)")
        writer.Line("{")
        writer.Push()
        writer.Line("while (offset <= text.Length - value.Length)")
        writer.Line("{")
        writer.Push()
        writer.Line("var found = text.IndexOf(value, offset, StringComparison.Ordinal);")
        writer.Line("if (found < 0) return -1;")
        writer.Line("if (IsBoundary(text, found) && IsBoundary(text, found + value.Length)) return found;")
        writer.Line("offset = found + 1;")
        writer.Pop()
        writer.Line("}")
        writer.Line("return -1;")
        writer.Pop()
        writer.Line("}")
        writer.Line("private static bool IsBoundary(string text, int offset) =>")
        writer.Line("offset <= 0 || offset >= text.Length || !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));")
        writer.Line("private static int Width(string text, int index) =>")
        writer.Line("char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;")
        writer.Line("private static long CountCodePoints(string text, int start, int end)")
        writer.Line("{")
        writer.Push()
        writer.Line("var count = 0L;")
        writer.Line("for (var index = start; index < end; index += Width(text, index)) count++;")
        writer.Line("return count;")
        writer.Pop()
        writer.Line("}")
        writer.Line("private static int CodePointAt(string text, ref int index)")
        writer.Line("{")
        writer.Push()
        writer.Line("var unit = text[index];")
        writer.Line("if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))")
        writer.Line("{")
        writer.Push()
        writer.Line("var scalar = char.ConvertToUtf32(unit, text[index + 1]);")
        writer.Line("index += 2;")
        writer.Line("return scalar;")
        writer.Pop()
        writer.Line("}")
        writer.Line("index++;")
        writer.Line("return unit;")
        writer.Pop()
        writer.Line("}")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        writer.Line("public sealed class NyList<T> : IEquatable<NyList<T>>")
        writer.Line("{")
        writer.Push()
        writer.Line("private readonly T[] _items;")
        writer.Line("private NyList(T[] items) => _items = items;")
        writer.Line("internal static NyList<T> FromOwnedArray(T[] items) => new(items);")
        writer.Line("public static NyList<T> Empty() => new(Array.Empty<T>());")
        writer.Line("public static NyList<T> Create(params T[] items) => new((T[])items.Clone());")
        writer.Line("public long Length => _items.LongLength;")
        writer.Line("public NyOption<T> ItemAt(long index) => index >= 0L && index < _items.LongLength ? NyOption<T>.Some(_items[index]) : NyOption<T>.None();")
        writer.Line("public NyList<T> AppendItem(T item)")
        writer.Line("{")
        writer.Push()
        writer.Line("var next = new T[_items.Length + 1];")
        writer.Line("Array.Copy(_items, next, _items.Length);")
        writer.Line("next[_items.Length] = item;")
        writer.Line("return new NyList<T>(next);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public NyList<T> Concat(NyList<T> other)")
        writer.Line("{")
        writer.Push()
        writer.Line("var next = new T[_items.Length + other._items.Length];")
        writer.Line("Array.Copy(_items, 0, next, 0, _items.Length);")
        writer.Line("Array.Copy(other._items, 0, next, _items.Length, other._items.Length);")
        writer.Line("return new NyList<T>(next);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public S Fold<S>(S seed, Func<S, T, S> folder)")
        writer.Line("{")
        writer.Push()
        writer.Line("var state = seed;")
        writer.Line("for (var i = 0; i < _items.Length; i++) state = folder(state, _items[i]);")
        writer.Line("return state;")
        writer.Pop()
        writer.Line("}")
        writer.Line("public NyList<U> Map<U>(Func<T, U> mapper)")
        writer.Line("{")
        writer.Push()
        writer.Line("var next = new U[_items.Length];")
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Push()
        writer.Line("next[i] = mapper(_items[i]);")
        writer.Pop()
        writer.Line("return NyList<U>.FromOwnedArray(next);")
        writer.Pop()
        writer.Line("}")
        // ListSortBy: top-down merge sort, left half = length / 2, taking the right head only when
        // lessThan(right, left) holds. The algorithm fixes the result for any callback, so an
        // inconsistent callback still yields a deterministic permutation; a strict order is stable.
        writer.Line("public NyList<T> SortBy(Func<T, T, bool> lessThan)")
        writer.Line("{")
        writer.Push()
        writer.Line("var items = (T[])_items.Clone();")
        writer.Line("if (items.Length > 1) SortRange(items, new T[items.Length], 0, items.Length, lessThan);")
        writer.Line("return new NyList<T>(items);")
        writer.Pop()
        writer.Line("}")
        writer.Line("private static void SortRange(T[] items, T[] scratch, int start, int end, Func<T, T, bool> lessThan)")
        writer.Line("{")
        writer.Push()
        writer.Line("var length = end - start;")
        writer.Line("if (length < 2) return;")
        writer.Line("var middle = start + length / 2;")
        writer.Line("SortRange(items, scratch, start, middle, lessThan);")
        writer.Line("SortRange(items, scratch, middle, end, lessThan);")
        writer.Line("int left = start, right = middle, next = start;")
        writer.Line("while (left < middle && right < end) scratch[next++] = lessThan(items[right], items[left]) ? items[right++] : items[left++];")
        writer.Line("while (left < middle) scratch[next++] = items[left++];")
        writer.Line("while (right < end) scratch[next++] = items[right++];")
        writer.Line("Array.Copy(scratch, start, items, start, length);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public NyList<T> Filter(Func<T, bool> predicate)")
        writer.Line("{")
        writer.Push()
        writer.Line("var buffer = new T[_items.Length];")
        writer.Line("var count = 0;")
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Line("{")
        writer.Push()
        writer.Line("if (!predicate(_items[i])) continue;")
        writer.Line("buffer[count++] = _items[i];")
        writer.Pop()
        writer.Line("}")
        writer.Line("var next = new T[count];")
        writer.Line("Array.Copy(buffer, next, count);")
        writer.Line("return new NyList<T>(next);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public bool Any(Func<T, bool> predicate)")
        writer.Line("{")
        writer.Push()
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Push()
        writer.Line("if (predicate(_items[i])) return true;")
        writer.Pop()
        writer.Line("return false;")
        writer.Pop()
        writer.Line("}")
        writer.Line("public bool All(Func<T, bool> predicate)")
        writer.Line("{")
        writer.Push()
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Push()
        writer.Line("if (!predicate(_items[i])) return false;")
        writer.Pop()
        writer.Line("return true;")
        writer.Pop()
        writer.Line("}")
        writer.Line("public bool Equals(NyList<T>? other)")
        writer.Line("{")
        writer.Push()
        writer.Line("if (other is null || other._items.Length != _items.Length) return false;")
        writer.Line("var comparer = System.Collections.Generic.EqualityComparer<T>.Default;")
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Push()
        writer.Line("if (!comparer.Equals(_items[i], other._items[i])) return false;")
        writer.Pop()
        writer.Line("return true;")
        writer.Pop()
        writer.Line("}")
        writer.Line("public override bool Equals(object? obj) => obj is NyList<T> other && Equals(other);")
        writer.Line("public override int GetHashCode()")
        writer.Line("{")
        writer.Push()
        writer.Line("var hash = new HashCode();")
        writer.Line("hash.Add(_items.Length);")
        writer.Line("for (var i = 0; i < _items.Length; i++) hash.Add(_items[i]);")
        writer.Line("return hash.ToHashCode();")
        writer.Pop()
        writer.Line("}")
        writer.Pop()
        writer.Line("}")
        writer.Line("")

    let private fieldParameter (field: ExecutionField) =
        formatType field.Type + " " + CSharpBackendNames.fieldName field.Id

    /// A task scope owns the tasks started in it. Each task has its own token linked to the scope's,
    /// which is linked to the token of whoever created the scope, so cancellation only flows down.
    let private taskRuntime =
        [ "public interface INyTask"
          "{"
          "    System.Threading.Tasks.Task Running { get; }"
          "    void Cancel();"
          "}"
          ""
          "public sealed class NyTask<T> : INyTask"
          "{"
          "    private readonly System.Threading.CancellationTokenSource _cancellation;"
          "    internal NyTask(System.Threading.CancellationTokenSource cancellation, System.Threading.Tasks.Task<T> running)"
          "    {"
          "        _cancellation = cancellation;"
          "        Running = running;"
          "    }"
          "    public System.Threading.Tasks.Task<T> Running { get; }"
          "    System.Threading.Tasks.Task INyTask.Running => Running;"
          "    public void Cancel() => _cancellation.Cancel();"
          "    /// The result once finished; a host exception from the task is rethrown to whoever observes it."
          "    public NyOption<T> Snapshot() =>"
          "        Running.IsCompleted ? NyOption<T>.Some(Running.GetAwaiter().GetResult()) : NyOption<T>.None();"
          "}"
          ""
          "public sealed class NyTaskScope"
          "{"
          "    private readonly System.Threading.CancellationTokenSource _cancellation;"
          "    private readonly System.Collections.Generic.List<INyTask> _tasks = new();"
          "    private bool _closed;"
          "    public NyTaskScope(System.Threading.CancellationToken parent) =>"
          "        _cancellation = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(parent);"
          "    public NyTask<T> Start<T>(Func<T> work)"
          "    {"
          "        lock (_tasks)"
          "        {"
          "            var cancellation = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);"
          "            if (_closed)"
          "            {"
          "                // A scope that has completed runs a late start at once, already cancelled, so no"
          "                // task outlives its scope."
          "                cancellation.Cancel();"
          "                var previous = NyRuntime.Cancellation.Value;"
          "                NyRuntime.Cancellation.Value = cancellation.Token;"
          "                try { return new NyTask<T>(cancellation, System.Threading.Tasks.Task.FromResult(work())); }"
          "                catch (Exception error) { return new NyTask<T>(cancellation, System.Threading.Tasks.Task.FromException<T>(error)); }"
          "                finally { NyRuntime.Cancellation.Value = previous; }"
          "            }"
          "            var running = NyRuntime.Run(() =>"
          "            {"
          "                NyRuntime.Cancellation.Value = cancellation.Token;"
          "                return work();"
          "            });"
          "            var task = new NyTask<T>(cancellation, running);"
          "            _tasks.Add(task);"
          "            return task;"
          "        }"
          "    }"
          "    /// Cancels and awaits every task. Unless the body itself threw, rethrows the host exception of"
          "    /// the earliest-started task that raised one."
          "    public void Close(bool bodyThrew)"
          "    {"
          "        INyTask[] tasks;"
          "        lock (_tasks)"
          "        {"
          "            _closed = true;"
          "            tasks = _tasks.ToArray();"
          "        }"
          "        _cancellation.Cancel();"
          "        NyRuntime.WaitAll(Array.ConvertAll(tasks, task => task.Running));"
          "        if (bodyThrew) return;"
          "        var faulted = Array.Find(tasks, task => task.Running.IsFaulted);"
          "        if (faulted != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(faulted.Running.Exception!.InnerException!).Throw();"
          "    }"
          "}"
          ""
          "public static class NyTasks"
          "{"
          "    /// `limit` is in milliseconds; a negative limit counts as 0. Real time uses the monotonic timer"
          "    /// behind Task.WaitAny/WaitAll; under a virtual clock only scripted delays advance time."
          "    public static NyList<NyOption<T>> Wait<T>(NyList<NyTask<T>> tasks, bool any, NyOption<long> limit)"
          "    {"
          "        var items = tasks.Fold(new System.Collections.Generic.List<NyTask<T>>(), (acc, task) => { acc.Add(task); return acc; });"
          "        var running = items.ConvertAll(task => (System.Threading.Tasks.Task)task.Running).ToArray();"
          "        long? millis = limit.HasValue ? Math.Max(0L, limit.Value) : null;"
          "        if (running.Length > 0)"
          "        {"
          "            var clock = NyRuntime.Clock;"
          "            if (clock != null)"
          "                clock.Block(any ? () => Array.Exists(running, task => task.IsCompleted) : () => Array.TrueForAll(running, task => task.IsCompleted), millis);"
          "            else"
          "            {"
          "                // One Task wait accepts at most int.MaxValue ms (~24.8 days), so longer limits wait in steps."
          "                var watch = System.Diagnostics.Stopwatch.StartNew();"
          "                while (true)"
          "                {"
          "                    long left = millis is long total ? Math.Max(0L, total - watch.ElapsedMilliseconds) : -1L;"
          "                    int timeout = left < 0L ? System.Threading.Timeout.Infinite : (int)Math.Min(left, int.MaxValue);"
          "                    bool done;"
          "                    if (any) done = System.Threading.Tasks.Task.WaitAny(running, timeout) >= 0;"
          "                    else { try { done = System.Threading.Tasks.Task.WaitAll(running, timeout); } catch (AggregateException) { done = true; } }"
          "                    if (done || left <= int.MaxValue) break;"
          "                }"
          "            }"
          "        }"
          "        return NyList<NyOption<T>>.Create(items.ConvertAll(task => task.Snapshot()).ToArray());"
          "    }"
          "}"
          "" ]

    /// Every blocking point of generated concurrency goes through NyRuntime, so a runner can install a
    /// virtual clock. Without one (the default) waits use real threads and the monotonic system timer.
    let private concurrencyRuntime =
        [ "public static class NyRuntime"
          "{"
          "    public static readonly System.Threading.AsyncLocal<System.Threading.CancellationToken> Cancellation = new();"
          "    /// Set once before the entry call by a deterministic runner; null runs on real time."
          "    public static NyVirtualClock? Clock;"
          "    public static bool CancellationRequested => Cancellation.Value.IsCancellationRequested;"
          "    public static void WaitForCancellation()"
          "    {"
          "        var token = Cancellation.Value;"
          "        if (Clock is { } clock) clock.Block(() => token.IsCancellationRequested, null);"
          "        else token.WaitHandle.WaitOne();"
          "    }"
          "    /// Blocks for a scripted delay; only a runner's fakes use it."
          "    public static void Delay(long millis)"
          "    {"
          "        if (Clock is { } clock) clock.Block(() => false, Math.Max(0L, millis), isDelay: true);"
          "        else System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(Math.Max(0L, millis)));"
          "    }"
          "    public static System.Threading.Tasks.Task<T> Run<T>(Func<T> work)"
          "    {"
          "        if (Clock is not { } clock) return System.Threading.Tasks.Task.Run(work);"
          "        var start = clock.Enter();"
          "        // A dedicated thread: every virtual task waits for its turn, which would starve the pool."
          "        var running = System.Threading.Tasks.Task.Factory.StartNew(() => { clock.Start(start); return work(); },"
          "            System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);"
          "        running.ContinueWith(_ => clock.Exit(), System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);"
          "        return running;"
          "    }"
          "    public static void WaitAll(System.Threading.Tasks.Task[] tasks)"
          "    {"
          "        if (tasks.Length == 0) return;"
          "        if (Clock is { } clock) clock.Block(() => Array.TrueForAll(tasks, task => task.IsCompleted), null);"
          "        else foreach (var task in tasks) { try { task.Wait(); } catch (AggregateException) { } }"
          "    }"
          "}"
          ""
          "/// Deterministic scheduling for runners. One thread runs at a time; a started task waits for its turn."
          "/// When the running thread blocks or finishes, the next one resumes: first an elapsed delay, by"
          "/// (deadline, start order), so everything due at a time has happened before a wait observes it; then"
          "/// a thread that can continue (a new task, a wait whose condition holds or whose limit elapsed),"
          "/// lowest start order first. Otherwise time jumps to the earliest deadline. A run therefore depends"
          "/// only on the program and its scripted delays, never on thread timing or real elapsed time."
          "public sealed class NyVirtualClock"
          "{"
          "    public static readonly System.Threading.AsyncLocal<long> Order = new();"
          "    private sealed class Waiter"
          "    {"
          "        public required Func<bool> Ready;"
          "        public long? Due;"
          "        public long Order;"
          "        public bool IsDelay;"
          "        public bool Released;"
          "        public Exception? Failure;"
          "    }"
          "    private readonly object _gate = new();"
          "    private readonly System.Collections.Generic.List<Waiter> _waiters = new();"
          "    private int _running = 1;"
          "    private long _nextOrder = 1;"
          "    private long _now;"
          "    /// The deadline `millis` after now, saturating at long.MaxValue so that virtual time never"
          "    /// wraps: a limit too large to represent is one that never elapses, not one already past."
          "    private long Deadline(long millis) => millis >= long.MaxValue - _now ? long.MaxValue : _now + Math.Max(0L, millis);"
          "    /// Registers a task that has not started; it runs once scheduled. Returns its start ticket."
          "    public object Enter()"
          "    {"
          "        lock (_gate)"
          "        {"
          "            var waiter = new Waiter { Ready = () => true, Order = _nextOrder++ };"
          "            _waiters.Add(waiter);"
          "            return waiter;"
          "        }"
          "    }"
          "    /// Called first on the task's own thread: waits for its turn and takes on its start order."
          "    public void Start(object ticket)"
          "    {"
          "        var waiter = (Waiter)ticket;"
          "        Order.Value = waiter.Order;"
          "        lock (_gate)"
          "        {"
          "            while (!waiter.Released) System.Threading.Monitor.Wait(_gate);"
          "            if (waiter.Failure != null) throw waiter.Failure;"
          "        }"
          "    }"
          "    public void Exit() { lock (_gate) { _running--; Schedule(); } }"
          "    /// Blocks until `ready` holds or `millis` of virtual time have passed; true when ready."
          "    public bool Block(Func<bool> ready, long? millis, bool isDelay = false)"
          "    {"
          "        lock (_gate)"
          "        {"
          "            var waiter = new Waiter { Ready = ready, Due = millis is long value ? Deadline(value) : null, Order = Order.Value, IsDelay = isDelay };"
          "            _waiters.Add(waiter);"
          "            _running--;"
          "            Schedule();"
          "            while (!waiter.Released) System.Threading.Monitor.Wait(_gate);"
          "            if (waiter.Failure != null) throw waiter.Failure;"
          "            return ready();"
          "        }"
          "    }"
          "    private void Schedule()"
          "    {"
          "        if (_running > 0 || _waiters.Count == 0) return;"
          "        var next = Earliest(waiter => waiter.IsDelay && waiter.Due <= _now);"
          "        if (next == null)"
          "        {"
          "            foreach (var waiter in _waiters)"
          "                if ((waiter.Ready() || waiter.Due <= _now) && (next == null || waiter.Order < next.Order)) next = waiter;"
          "        }"
          "        if (next == null)"
          "        {"
          "            next = Earliest(waiter => waiter.Due != null);"
          "            if (next == null)"
          "            {"
          "                // Nothing can ever resume: fail every waiter instead of hanging."
          "                foreach (var waiter in _waiters)"
          "                {"
          "                    waiter.Failure = new InvalidOperationException(\"Every task is blocked and no delay is pending, so the program cannot continue.\");"
          "                    waiter.Released = true;"
          "                    _running++;"
          "                }"
          "                _waiters.Clear();"
          "                System.Threading.Monitor.PulseAll(_gate);"
          "                return;"
          "            }"
          "            _now = next.Due!.Value;"
          "        }"
          "        _waiters.Remove(next);"
          "        next.Released = true;"
          "        _running++;"
          "        System.Threading.Monitor.PulseAll(_gate);"
          "    }"
          "    /// The matching waiter with the earliest deadline; delays before waits, then start order."
          "    private Waiter? Earliest(Func<Waiter, bool> matches)"
          "    {"
          "        Waiter? earliest = null;"
          "        foreach (var waiter in _waiters)"
          "            if (matches(waiter) && (earliest == null || (waiter.Due, waiter.IsDelay ? 0 : 1, waiter.Order).CompareTo((earliest.Due, earliest.IsDelay ? 0 : 1, earliest.Order)) < 0))"
          "                earliest = waiter;"
          "        return earliest;"
          "    }"
          "}"
          "" ]

    let private usesTaskTypes (program: ExecutionProgram) =
        // A declared field may hold a Task handle, so its record needs NyTask even when no function
        // of the program mentions tasks.
        program.Types
        |> List.exists (fun typeEntity ->
            match typeEntity.Kind with
            | ExecutionTypeKind.Product fields -> fields |> List.exists (fun field -> TaskTypes.mentions field.Type)
            | ExecutionTypeKind.Union variants ->
                variants |> List.exists (fun variant -> variant.Fields |> List.exists (fun field -> TaskTypes.mentions field.Type)))
        || program.Functions
        |> List.exists (fun functionEntity ->
            TaskTypes.mentions functionEntity.ResultType
            || functionEntity.Parameters |> List.exists (fun parameter -> TaskTypes.mentions parameter.Value.Type)
            || functionEntity.Blocks
               |> List.exists (fun block ->
                   block.Instructions
                   |> List.exists (fun instruction ->
                       TaskTypes.mentions instruction.Result.Type
                       || (match instruction.Kind with
                           | ExecutionInstructionKind.TaskScope _
                           | ExecutionInstructionKind.TaskStart _
                           | ExecutionInstructionKind.TaskWait _
                           | ExecutionInstructionKind.TaskCancel _ -> true
                           | _ -> false))))

    /// Whether the generated source contains NyRuntime and NyVirtualClock, so a runner can install a
    /// virtual clock without inspecting the source text.
    let usesConcurrencyRuntime (program: ExecutionProgram) =
        usesTaskTypes program
        || program.Functions
           |> List.exists (fun functionEntity ->
               functionEntity.Blocks
               |> List.exists (fun block ->
                   block.Instructions
                   |> List.exists (fun instruction ->
                       match instruction.Kind with
                       | ExecutionInstructionKind.ParallelAll _
                       | ExecutionInstructionKind.CancellationRequested -> true
                       | _ -> false)))

    /// Ambient cancellation for ParallelAll workers and tasks, emitted only for programs that use
    /// concurrency so other generated programs are unchanged. External implementations read the same token.
    let private emitConcurrencyRuntime (writer: SourceWriter) (program: ExecutionProgram) =
        let usesTasks = usesTaskTypes program
        if usesConcurrencyRuntime program then
            for line in concurrencyRuntime do
                writer.Line(line)
        if usesTasks then
            for line in taskRuntime do
                writer.Line(line)

    let private emitFunctionTypes (writer: SourceWriter) (program: ExecutionProgram) =
        let rec arities = function
            | TypeRef.Function(arguments, result) -> arguments.Length :: List.collect arities (arguments @ [ result ])
            | TypeRef.BuiltinGeneric(_, arguments) -> List.collect arities arguments
            | _ -> []
        let declaredTypes = program.Types |> List.collect (fun t ->
            match t.Kind with
            | ExecutionTypeKind.Product fields -> fields |> List.map (fun f -> f.Type)
            | ExecutionTypeKind.Union variants -> variants |> List.collect (fun v -> v.Fields |> List.map (fun f -> f.Type)))
        let capabilityTypes = program.Capabilities |> List.collect (fun c -> c.Operations |> List.collect (fun op -> op.ResultType :: List.map (fun (p: ExecutionCapabilityParameter) -> p.Type) op.Parameters))
        let functionTypes = program.Functions |> List.collect (fun fn ->
            fn.ResultType :: (fn.Parameters |> List.map (fun p -> p.Value.Type))
            @ (fn.Blocks |> List.collect (fun block -> List.map (fun (p: ExecutionValue) -> p.Type) block.Parameters @ List.map (fun i -> i.Result.Type) block.Instructions)))
        let requiredArities = declaredTypes @ capabilityTypes @ functionTypes |> List.collect arities |> List.distinct |> List.sort
        for count in requiredArities do
            let types = [ for index in 0 .. count - 1 -> "T" + string index ]
            let generics = String.concat ", " (types @ [ "TResult" ])
            let arguments = types |> List.mapi (fun index t -> t + " arg" + string index) |> String.concat ", "
            writer.Line("public delegate TResult NyFunction" + string count + "<" + generics + ">(" + arguments + ");")
        if not (List.isEmpty requiredArities) then writer.Line("")

    let private emitTypes (writer: SourceWriter) (program: ExecutionProgram) =
        for typeEntity in program.Types |> List.sortBy (fun item -> StableId.value item.Id) do
            let typeName = CSharpBackendNames.typeName typeEntity.Id
            match typeEntity.Kind with
            | ExecutionTypeKind.Product fields ->
                let parameters =
                    fields
                    |> List.sortBy (fun field -> StableId.value field.Id)
                    |> List.map fieldParameter
                    |> String.concat ", "
                writer.Line("public sealed record " + typeName + "(" + parameters + ");")
            | ExecutionTypeKind.Union variants ->
                writer.Line("public abstract record " + typeName + ";")
                for variant in variants |> List.sortBy (fun item -> StableId.value item.Id) do
                    let parameters =
                        variant.Fields
                        |> List.sortBy (fun field -> StableId.value field.Id)
                        |> List.map fieldParameter
                        |> String.concat ", "
                    let variantName = CSharpBackendNames.variantName variant.Id
                    writer.Line("public sealed record " + variantName + "(" + parameters + ") : " + typeName + ";")
            writer.Line("")

    let private capabilityParameter (parameter: ExecutionCapabilityParameter) =
        formatType parameter.Type + " " + CSharpBackendNames.capabilityParameterName parameter.Id

    let private emitCapabilities (writer: SourceWriter) (program: ExecutionProgram) =
        for capability in program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) do
            let capabilityName = CSharpBackendNames.capabilityName capability.Id
            writer.Line("public interface " + capabilityName)
            writer.Line("{")
            writer.Push()
            for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
                let parameters =
                    operation.Parameters
                    |> List.map capabilityParameter
                    |> String.concat ", "
                let methodName = CSharpBackendNames.capabilityOperationName operation.Id
                writer.Line(formatType operation.ResultType + " " + methodName + "(" + parameters + ");")
            writer.Pop()
            writer.Line("}")
            writer.Line("")

    let private hasCapabilityAdapter
        (context: CSharpBackendValidation.Context)
        (capability: ExecutionCapability) =
        let allBound =
            capability.Operations
            |> List.forall (fun operation -> Map.containsKey operation.Id context.BindingsByOperation)
        let anyBound =
            capability.Operations
            |> List.exists (fun operation -> Map.containsKey operation.Id context.BindingsByOperation)
        allBound
        && anyBound
        && (capability.Operations
            |> List.forall (fun operation ->
                Map.find operation.Id context.BindingsByOperation = CSharpCapabilityBindingKind.SystemConsoleWriteLine))

    let private emitAdapters
        (writer: SourceWriter)
        (context: CSharpBackendValidation.Context)
        (program: ExecutionProgram) =
        for capability in program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) do
            if hasCapabilityAdapter context capability then
                let capabilityName = CSharpBackendNames.capabilityName capability.Id
                let adapterName = CSharpBackendNames.capabilityAdapterName capability.Id
                writer.Line("public sealed class " + adapterName + " : " + capabilityName)
                writer.Line("{")
                writer.Push()
                for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
                    let parameters =
                        operation.Parameters
                        |> List.map capabilityParameter
                        |> String.concat ", "
                    let methodName = CSharpBackendNames.capabilityOperationName operation.Id
                    writer.Line("public " + formatType operation.ResultType + " " + methodName + "(" + parameters + ")")
                    writer.Line("{")
                    writer.Push()
                    match Map.find operation.Id context.BindingsByOperation with
                    | CSharpCapabilityBindingKind.SystemConsoleWriteLine ->
                        let parameter = List.exactlyOne operation.Parameters
                        let parameterName = CSharpBackendNames.capabilityParameterName parameter.Id
                        writer.Line("System.Console.WriteLine(" + parameterName + ");")
                        writer.Line("return NyUnit.Value;")
                    | CSharpCapabilityBindingKind.ExternalImplementation ->
                        invalidOp "An external implementation does not generate an adapter."
                    writer.Pop()
                    writer.Line("}")
                writer.Pop()
                writer.Line("}")
                writer.Line("")

    let private emitProgramImplementations (writer: SourceWriter) (context: CSharpBackendValidation.Context) (program: ExecutionProgram) =
        for instruction in program.Functions |> List.collect ExecutionCallGraph.instructions |> List.sortBy (fun item -> StableId.value item.OriginNodeId) do
            match instruction.Kind with
            | ExecutionInstructionKind.CapabilityConstruct(capabilityId, implementations) ->
                let capability = Map.find capabilityId context.CapabilitiesById
                let implementationName = CSharpBackendNames.capabilityImplementationName instruction.OriginNodeId
                writer.Line("internal sealed class " + implementationName + " : " + CSharpBackendNames.capabilityName capabilityId)
                writer.Line("{")
                writer.Push()
                for operation in capability.Operations |> List.sortBy (fun operation -> StableId.value operation.Id) do
                    let functionId = implementations |> List.find (fun (id, _) -> id = operation.Id) |> snd
                    let fn = Map.find functionId context.FunctionsById
                    let parameters = operation.Parameters |> List.map capabilityParameter |> String.concat ", "
                    // Declaration order defines the correspondence. Name each target parameter
                    // explicitly, independently of the operation parameter identities.
                    let arguments = List.zip fn.Parameters operation.Parameters |> List.map (fun (parameter, supplied) ->
                        CSharpBackendNames.parameterName parameter.SemanticParameterId + ": " + CSharpBackendNames.capabilityParameterName supplied.Id) |> String.concat ", "
                    writer.Line("public " + formatType operation.ResultType + " " + CSharpBackendNames.capabilityOperationName operation.Id + "(" + parameters + ") => "
                                + CSharpBackendNames.functionContainerName + "." + CSharpBackendNames.functionName functionId + "(" + arguments + ");")
                writer.Pop()
                writer.Line("}")
                writer.Line("")
            | _ -> ()

    let private valueName (valueId: ExecutionValueId) =
        CSharpBackendNames.executionValueName valueId

    let private callbackName (functionId: FunctionId) =
        CSharpBackendNames.functionContainerName + "." + CSharpBackendNames.functionName functionId

    let private renderNamedCallArguments (arguments: ExecutionCallArgument list) =
        arguments
        |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
        |> List.map (fun argument ->
            CSharpBackendNames.parameterName argument.TargetParameterId
            + ": "
            + valueName argument.ValueId)
        |> String.concat ", "

    let private renderCapabilityArguments (arguments: ExecutionCapabilityArgument list) =
        arguments
        |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
        |> List.map (fun argument ->
            CSharpBackendNames.capabilityParameterName argument.TargetCapabilityParameterId
            + ": "
            + valueName argument.ValueId)
        |> String.concat ", "

    let private renderFieldArguments (fields: ExecutionFieldValue list) =
        fields
        |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
        |> List.map (fun field ->
            CSharpBackendNames.fieldName field.TargetFieldId
            + ": "
            + valueName field.ValueId)
        |> String.concat ", "

    let private renderListItems (itemValueIds: ExecutionValueId list) =
        itemValueIds |> List.map valueName |> String.concat ", "

    let private emitBinaryExpression
        (writer: SourceWriter)
        target
        leftValueId
        operatorText
        rightValueId
        wrap =
        let expression = valueName leftValueId + " " + operatorText + " " + valueName rightValueId
        if wrap then
            writer.Line(target + " = unchecked(" + expression + ");")
        else
            writer.Line(target + " = " + expression + ";")

    /// A callback with context becomes a lambda that passes context values and the iteration values
    /// by parameter name; without context the method group is emitted unchanged.
    /// Calls a callback with its context values and the given iteration values, all by parameter name.
    let private renderInvocation (parametersOf: FunctionId -> (ParameterId * TypeRef) list) (functionId: FunctionId) (context: ExecutionCallArgument list) (iterationValues: string list) =
        let bound = context |> List.map (fun argument -> argument.TargetParameterId) |> Set.ofList
        let iteration = parametersOf functionId |> List.map fst |> List.filter (fun parameterId -> not (Set.contains parameterId bound))
        let arguments =
            (context |> List.map (fun argument -> CSharpBackendNames.parameterName argument.TargetParameterId + ": " + valueName argument.ValueId))
            @ (List.map2 (fun parameterId value -> CSharpBackendNames.parameterName parameterId + ": " + value) iteration iterationValues)
        callbackName functionId + "(" + String.concat ", " arguments + ")"

    let private renderCallback (parametersOf: FunctionId -> (ParameterId * TypeRef) list) (target: string) (functionId: FunctionId) (context: ExecutionCallArgument list) =
        let generic = parametersOf functionId |> List.exists (fun (_, parameterType) -> not (List.isEmpty (TypeParameters.occurring parameterType)))
        // A generic method group cannot be inferred by C#; a lambda lets the call infer its type arguments.
        if List.isEmpty context && not generic then callbackName functionId
        else
            let unbound = (parametersOf functionId).Length - context.Length
            let names = List.init unbound (fun index -> target + "_it" + string index)
            "(" + String.concat ", " names + ") => " + renderInvocation parametersOf functionId context names

    let private emitInstruction
        (writer: SourceWriter)
        (parametersOf: FunctionId -> (ParameterId * TypeRef) list)
        (valueTypeOf: ExecutionValueId -> TypeRef)
        (operationResultOf: CapabilityOperationId -> TypeRef)
        (instruction: ExecutionInstruction) =
        let target = valueName instruction.Result.Id
        let callback = renderCallback parametersOf target
        match instruction.Kind with
        | ExecutionInstructionKind.ReadParameter sourceValueId
        | ExecutionInstructionKind.ReadPatternBinding sourceValueId ->
            writer.Line(target + " = " + valueName sourceValueId + ";")
        | ExecutionInstructionKind.StringLiteral value ->
            writer.Line(target + " = " + escapeStringLiteral value + ";")
        | ExecutionInstructionKind.BoolLiteral value ->
            writer.Line(target + " = " + (if value then "true" else "false") + ";")
        | ExecutionInstructionKind.UnitLiteral ->
            writer.Line(target + " = NyUnit.Value;")
        | ExecutionInstructionKind.Int64Literal value ->
            writer.Line(target + " = " + renderInt64Literal value + ";")
        | ExecutionInstructionKind.Float64Literal bits ->
            writer.Line(target + " = " + renderFloat64Literal bits + ";")
        | ExecutionInstructionKind.StringIsEmpty sourceValueId ->
            writer.Line(target + " = " + valueName sourceValueId + ".Length == 0;")
        | ExecutionInstructionKind.Int64ToString sourceValueId ->
            writer.Line(target + " = " + valueName sourceValueId + ".ToString(System.Globalization.CultureInfo.InvariantCulture);")
        | ExecutionInstructionKind.StringParseInt64 sourceValueId ->
            // Only '-'? ASCII digits are accepted; TryParse then enforces the Int64 range.
            writer.Line("{")
            writer.Push()
            writer.Line("var text = " + valueName sourceValueId + ";")
            writer.Line("var start = text.Length > 0 && text[0] == '-' ? 1 : 0;")
            writer.Line("var digits = text.Length > start;")
            writer.Line("for (var index = start; digits && index < text.Length; index++) digits = text[index] >= '0' && text[index] <= '9';")
            writer.Line(target + " = digits && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? NyOption<long>.Some(parsed) : NyOption<long>.None();")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.BoolNot sourceValueId ->
            writer.Line(target + " = !" + valueName sourceValueId + ";")
        | ExecutionInstructionKind.StringLength sourceValueId ->
            // Code points: a surrogate pair counts once, an unpaired surrogate code unit counts once.
            writer.Line("{")
            writer.Push()
            writer.Line("var text = " + valueName sourceValueId + ";")
            writer.Line("var count = 0L;")
            writer.Line("for (var index = 0; index < text.Length; index++)")
            writer.Line("{")
            writer.Push()
            writer.Line("if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) index++;")
            writer.Line("count++;")
            writer.Pop()
            writer.Line("}")
            writer.Line(target + " = count;")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringCodePoints sourceValueId ->
            // One String per code point, walked once: a surrogate pair stays together and an unpaired
            // surrogate code unit is its own element, as StringLength counts it.
            writer.Line("{")
            writer.Push()
            writer.Line("var text = " + valueName sourceValueId + ";")
            writer.Line("var codePoints = new System.Collections.Generic.List<string>(text.Length);")
            writer.Line("for (var index = 0; index < text.Length; index++)")
            writer.Line("{")
            writer.Push()
            writer.Line("var width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;")
            writer.Line("codePoints.Add(text.Substring(index, width));")
            writer.Line("index += width - 1;")
            writer.Pop()
            writer.Line("}")
            writer.Line(target + " = NyList<string>.FromOwnedArray(codePoints.ToArray());")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringSlice(sourceValueId, startId, endId) ->
            // Clamp both code-point bounds to [0, length], then map them to UTF-16 offsets without splitting a pair.
            writer.Line("{")
            writer.Push()
            writer.Line("var text = " + valueName sourceValueId + ";")
            writer.Line("var start = System.Math.Max(0L, " + valueName startId + ");")
            writer.Line("var end = System.Math.Max(0L, " + valueName endId + ");")
            writer.Line("var startOffset = text.Length;")
            writer.Line("var endOffset = text.Length;")
            writer.Line("var position = 0L;")
            writer.Line("for (var index = 0; ; position++)")
            writer.Line("{")
            writer.Push()
            writer.Line("if (position == start) startOffset = index;")
            writer.Line("if (position == end) endOffset = index;")
            writer.Line("if (index == text.Length) break;")
            writer.Line("index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;")
            writer.Pop()
            writer.Line("}")
            writer.Line(target + " = endOffset > startOffset ? text.Substring(startOffset, endOffset - startOffset) : \"\";")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringContains(sourceValueId, substringId) ->
            writer.Line(target + " = " + valueName sourceValueId + ".Contains(" + valueName substringId + ", StringComparison.Ordinal);")
        | ExecutionInstructionKind.StringIndexOf(sourceValueId, substringId, fromId) ->
            writer.Line(target + " = NyText.IndexOf(" + valueName sourceValueId + ", " + valueName substringId + ", " + valueName fromId + ");")
        | ExecutionInstructionKind.StringLastIndexOf(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.LastIndexOf(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringStartsWith(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.StartsWith(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringEndsWith(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.EndsWith(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringSplit(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.Split(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringEqual(leftValueId, rightValueId) ->
            writer.Line(target + " = string.Equals(" + valueName leftValueId + ", " + valueName rightValueId + ", StringComparison.Ordinal);")
        | ExecutionInstructionKind.StringLessThan(leftValueId, rightValueId) ->
            writer.Line(target + " = NyText.LessThan(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.ValueEqual(leftValueId, rightValueId) ->
            writer.Line(target + " = NyEquality.Equal(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.StringConcat(leftValueId, rightValueId) ->
            writer.Line(target + " = string.Concat(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Int64WrappingAdd(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "+" rightValueId true
        | ExecutionInstructionKind.Int64WrappingSubtract(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "-" rightValueId true
        | ExecutionInstructionKind.Int64WrappingMultiply(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "*" rightValueId true
        | ExecutionInstructionKind.Int64FloorDivide(leftValueId, rightValueId) ->
            // Floor division that never throws: divisor 0 yields 0 and MinValue / -1 wraps to MinValue.
            writer.Line("{")
            writer.Push()
            writer.Line("var dividend = " + valueName leftValueId + ";")
            writer.Line("var divisor = " + valueName rightValueId + ";")
            writer.Line("if (divisor == 0L) " + target + " = 0L;")
            writer.Line("else if (divisor == -1L) " + target + " = unchecked(-dividend);")
            writer.Line("else")
            writer.Line("{")
            writer.Push()
            writer.Line("var quotient = dividend / divisor;")
            writer.Line("if (dividend % divisor != 0L && (dividend < 0L) != (divisor < 0L)) quotient -= 1L;")
            writer.Line(target + " = quotient;")
            writer.Pop()
            writer.Line("}")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.Int64FloorModulo(leftValueId, rightValueId) ->
            // Floor modulo with the divisor's sign that never throws: divisor 0 yields the dividend and x % -1 is 0.
            writer.Line("{")
            writer.Push()
            writer.Line("var dividend = " + valueName leftValueId + ";")
            writer.Line("var divisor = " + valueName rightValueId + ";")
            writer.Line("if (divisor == 0L) " + target + " = dividend;")
            writer.Line("else if (divisor == -1L) " + target + " = 0L;")
            writer.Line("else")
            writer.Line("{")
            writer.Push()
            writer.Line("var remainder = dividend % divisor;")
            writer.Line("if (remainder != 0L && (remainder < 0L) != (divisor < 0L)) remainder += divisor;")
            writer.Line(target + " = remainder;")
            writer.Pop()
            writer.Line("}")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.Int64Equal(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "==" rightValueId false
        | ExecutionInstructionKind.Int64LessThan(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "<" rightValueId false
        | ExecutionInstructionKind.Int64LessThanOrEqual(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "<=" rightValueId false
        | ExecutionInstructionKind.Int64GreaterThan(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId ">" rightValueId false
        | ExecutionInstructionKind.Int64GreaterThanOrEqual(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId ">=" rightValueId false
        | ExecutionInstructionKind.Float64Add(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "+" rightValueId false
        | ExecutionInstructionKind.Float64Subtract(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "-" rightValueId false
        | ExecutionInstructionKind.Float64Multiply(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "*" rightValueId false
        | ExecutionInstructionKind.Float64Divide(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "/" rightValueId false
        | ExecutionInstructionKind.Float64Equal(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "==" rightValueId false
        | ExecutionInstructionKind.Float64LessThan(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "<" rightValueId false
        | ExecutionInstructionKind.Float64IsNaN valueId ->
            writer.Line(target + " = double.IsNaN(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64IsFinite valueId ->
            writer.Line(target + " = double.IsFinite(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64IsInfinite valueId ->
            writer.Line(target + " = double.IsInfinity(" + valueName valueId + ");")
        | ExecutionInstructionKind.Int64ToFloat64 valueId ->
            // .NET converts long to double with IEEE 754 round to nearest, ties to even.
            writer.Line(target + " = (double)" + valueName valueId + ";")
        | ExecutionInstructionKind.ListEmpty ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Empty();")
        | ExecutionInstructionKind.ListConstruct itemValueIds ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Create(" + renderListItems itemValueIds + ");")
        | ExecutionInstructionKind.ListLength listValueId ->
            writer.Line(target + " = " + valueName listValueId + ".Length;")
        | ExecutionInstructionKind.ListItemAt(listValueId, indexValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".ItemAt(" + valueName indexValueId + ");")
        | ExecutionInstructionKind.ListAppendItem(listValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".AppendItem(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListConcat(leftListValueId, rightListValueId) ->
            writer.Line(target + " = " + valueName leftListValueId + ".Concat(" + valueName rightListValueId + ");")
        | ExecutionInstructionKind.ListFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
        | ExecutionInstructionKind.ListEffectFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            if instruction.Result.Type = valueTypeOf seedValueId then
                writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
            else
                // Result accumulator: after the first Err the folder, and so its effects, are not called again.
                let state, item = target + "_state", target + "_item"
                let resultType = formatType instruction.Result.Type
                let step = renderInvocation parametersOf folderFunctionId context [ state + ".OkValue"; item ]
                writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + resultType + ".Ok(" + valueName seedValueId + "), (" + state + ", " + item + ") => " + state + ".IsOk ? " + step + " : " + state + ");")
        | ExecutionInstructionKind.UseResource(acquireValueId, releaseCapabilityValueId, releaseOperationId, bodyFunctionId, context) ->
            // Release always runs after acquisition succeeded: on Ok and Err exits, and before a host
            // exception from the body propagates. A body Err takes precedence over a release Err.
            let resultType = formatType instruction.Result.Type
            let acquired, resource, body, released = target + "_acquired", target + "_resource", target + "_body", target + "_released"
            let release = valueName releaseCapabilityValueId + "." + CSharpBackendNames.capabilityOperationName releaseOperationId + "(" + resource + ")"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + acquired + " = " + valueName acquireValueId + ";")
            writer.Line("if (!" + acquired + ".IsOk) " + target + " = " + resultType + ".Err(" + acquired + ".ErrorValue);")
            writer.Line("else")
            writer.Line("{")
            writer.Push()
            writer.Line("var " + resource + " = " + acquired + ".OkValue;")
            writer.Line(resultType + " " + body + ";")
            writer.Line("try { " + body + " = " + renderInvocation parametersOf bodyFunctionId context [ resource ] + "; }")
            writer.Line("catch { try { " + release + "; } catch { } throw; }")
            match operationResultOf releaseOperationId with
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, _) ->
                writer.Line("var " + released + " = " + release + ";")
                writer.Line(target + " = " + body + ".IsOk && !" + released + ".IsOk ? " + resultType + ".Err(" + released + ".ErrorValue) : " + body + ";")
            | _ ->
                writer.Line(release + ";")
                writer.Line(target + " = " + body + ";")
            writer.Pop()
            writer.Line("}")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.CancellationRequested ->
            writer.Line(target + " = NyRuntime.CancellationRequested;")
        | ExecutionInstructionKind.TaskScope(bodyFunctionId, context) ->
            // Every exit of the body, including a host exception, cancels and awaits the scope's tasks.
            let scope, body = target + "_scope", target + "_body"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + scope + " = new NyTaskScope(NyRuntime.Cancellation.Value);")
            writer.Line(formatType instruction.Result.Type + " " + body + ";")
            writer.Line("try { " + body + " = " + renderInvocation parametersOf bodyFunctionId context [ scope ] + "; }")
            writer.Line("catch { " + scope + ".Close(bodyThrew: true); throw; }")
            writer.Line(scope + ".Close(bodyThrew: false);")
            writer.Line(target + " = " + body + ";")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.TaskStart(scopeValueId, workerFunctionId, context, itemValueId) ->
            writer.Line(target + " = " + valueName scopeValueId + ".Start(() => " + renderInvocation parametersOf workerFunctionId context [ valueName itemValueId ] + ");")
        | ExecutionInstructionKind.TaskWait(tasksValueId, mode, limitValueId) ->
            let any = match mode with TaskWaitMode.Any -> "true" | TaskWaitMode.All -> "false"
            let limit = limitValueId |> Option.map valueName |> Option.defaultValue "NyOption<long>.None()"
            writer.Line(target + " = NyTasks.Wait(" + valueName tasksValueId + ", any: " + any + ", limit: " + limit + ");")
        | ExecutionInstructionKind.TaskCancel taskValueId ->
            writer.Line(valueName taskValueId + ".Cancel();")
            writer.Line(target + " = NyUnit.Value;")
        | ExecutionInstructionKind.ParallelAll(sourceListValueId, workerFunctionId, context) ->
            // Structured: every worker is awaited before the node completes. The first Err or host
            // exception cancels the shared token; faults win, then the lowest-index Err, else all Ok.
            // Link the ambient token so cancellation also reaches workers of nested ParallelAll nodes.
            let itemType =
                match valueTypeOf sourceListValueId with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) -> formatType item
                | other -> invalidOp $"ParallelAll source must be a List, got {other}."
            let workerResult, okType =
                match instruction.Result.Type with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ ok ]); error ]) ->
                    "NyResult<" + formatType ok + ", " + formatType error + ">", formatType ok
                | other -> invalidOp $"ParallelAll result must be Result<List<U>, E>, got {other}."
            let resultType = formatType instruction.Result.Type
            let name suffix = target + "_" + suffix
            let items, cts, tasks, index, item, result = name "items", name "cts", name "tasks", name "i", name "item", name "result"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + items + " = " + valueName sourceListValueId + ".Fold(new System.Collections.Generic.List<" + itemType + ">(), (" + name "acc" + ", " + name "x" + ") => { " + name "acc" + ".Add(" + name "x" + "); return " + name "acc" + "; });")
            writer.Line("using var " + cts + " = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(NyRuntime.Cancellation.Value);")
            writer.Line("var " + tasks + " = new System.Threading.Tasks.Task<" + workerResult + ">[" + items + ".Count];")
            writer.Line("for (var " + index + " = 0; " + index + " < " + items + ".Count; " + index + "++)")
            writer.Line("{")
            writer.Push()
            writer.Line("var " + item + " = " + items + "[" + index + "];")
            writer.Line(tasks + "[" + index + "] = NyRuntime.Run(() =>")
            writer.Line("{")
            writer.Push()
            writer.Line("NyRuntime.Cancellation.Value = " + cts + ".Token;")
            writer.Line("try")
            writer.Line("{")
            writer.Push()
            writer.Line("var " + result + " = " + renderInvocation parametersOf workerFunctionId context [ item ] + ";")
            writer.Line("if (!" + result + ".IsOk) " + cts + ".Cancel();")
            writer.Line("return " + result + ";")
            writer.Pop()
            writer.Line("}")
            writer.Line("catch { " + cts + ".Cancel(); throw; }")
            writer.Pop()
            writer.Line("});")
            writer.Pop()
            writer.Line("}")
            writer.Line("NyRuntime.WaitAll(" + tasks + ");")
            writer.Line("var " + name "faulted" + " = System.Array.Find(" + tasks + ", " + name "t" + " => " + name "t" + ".IsFaulted);")
            writer.Line("if (" + name "faulted" + " != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(" + name "faulted" + ".Exception!.InnerException!).Throw();")
            writer.Line("var " + name "failed" + " = System.Array.Find(" + tasks + ", " + name "t" + " => !" + name "t" + ".Result.IsOk);")
            writer.Line(target + " = " + name "failed" + " != null ? " + resultType + ".Err(" + name "failed" + ".Result.ErrorValue) : " + resultType + ".Ok(NyList<" + okType + ">.Create(System.Array.ConvertAll(" + tasks + ", " + name "t" + " => " + name "t" + ".Result.OkValue)));")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.ListMap(sourceListValueId, mapperFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Map(" + callback mapperFunctionId context + ");")
        | ExecutionInstructionKind.ListFilter(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Filter(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.ListSortBy(sourceListValueId, lessThanFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".SortBy(" + callback lessThanFunctionId context + ");")
        | ExecutionInstructionKind.ListAny(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Any(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.ListAll(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".All(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.CapabilityConstruct _ ->
            writer.Line(target + " = new " + CSharpBackendNames.capabilityImplementationName instruction.OriginNodeId + "();")
        | ExecutionInstructionKind.FunctionReference functionId ->
            writer.Line(target + " = " + CSharpBackendNames.functionName functionId + ";")
        | ExecutionInstructionKind.FunctionInvoke(value, arguments) ->
            writer.Line(target + " = " + valueName value + "(" + (arguments |> List.map valueName |> String.concat ", ") + ");")
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            let functionName = CSharpBackendNames.functionName functionId
            writer.Line(target + " = NyFunctions." + functionName + "(" + renderNamedCallArguments arguments + ");")
        | ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, operationId, arguments) ->
            let methodName = CSharpBackendNames.capabilityOperationName operationId
            writer.Line(target + " = " + valueName capabilityValueId + "." + methodName + "(" + renderCapabilityArguments arguments + ");")
        | ExecutionInstructionKind.ProductConstruct fields ->
            match instruction.Result.Type with
            | TypeRef.Named typeId ->
                writer.Line(target + " = new " + CSharpBackendNames.typeName typeId + "(" + renderFieldArguments fields + ");")
            | _ -> invalidOp "ProductConstruct result does not carry a named product type."
        | ExecutionInstructionKind.FieldAccess(sourceValueId, fieldId) ->
            writer.Line(target + " = " + valueName sourceValueId + "." + CSharpBackendNames.fieldName fieldId + ";")
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) ->
            writer.Line(target + " = new " + CSharpBackendNames.variantName variantId + "(" + renderFieldArguments fields + ");")
        | ExecutionInstructionKind.OptionNone ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".None();")
        | ExecutionInstructionKind.OptionSome sourceValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Some(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.ResultOk sourceValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Ok(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.ResultErr errorValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Err(" + valueName errorValueId + ");")
        | ExecutionInstructionKind.ExtractUnionField(inputValueId, variantId, fieldId) ->
            writer.Line(target + " = ((" + CSharpBackendNames.variantName variantId + ")" + valueName inputValueId + ")." + CSharpBackendNames.fieldName fieldId + ";")
        | ExecutionInstructionKind.ExtractOptionSome inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".Value;")
        | ExecutionInstructionKind.ExtractResultOk inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".OkValue;")
        | ExecutionInstructionKind.ExtractResultErr inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".ErrorValue;")

    let private emitTerminator
        (resultType: TypeRef)
        (writer: SourceWriter)
        (block: ExecutionBlock)
        (terminator: ExecutionTerminator) =
        match terminator with
        | ExecutionTerminator.Abort(_, errorValueId) ->
            writer.Line("return " + formatType resultType + ".Err(" + valueName errorValueId + ");")
        | ExecutionTerminator.Return resultValueId ->
            writer.Line("return " + valueName resultValueId + ";")
        | ExecutionTerminator.Jump(targetBlockId, arguments) ->
            for index, argument in arguments |> List.indexed do
                let temporary = CSharpBackendNames.jumpTemporaryName block.Id index
                writer.Line(temporary + " = " + valueName argument.ValueId + ";")
            for index, argument in arguments |> List.indexed do
                let temporary = CSharpBackendNames.jumpTemporaryName block.Id index
                writer.Line(valueName argument.TargetValueId + " = " + temporary + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel targetBlockId + ";")
        | ExecutionTerminator.Branch(conditionValueId, whenTrue, whenFalse) ->
            writer.Line("if (" + valueName conditionValueId + ") goto " + CSharpBackendNames.executionBlockLabel whenTrue + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel whenFalse + ";")
        | ExecutionTerminator.MatchUnion(inputValueId, targets) ->
            match targets |> List.sortBy (fun target -> StableId.value target.VariantId) with
            | [] -> writer.Line("throw new InvalidOperationException(\"Execution IR union match has no targets.\");")
            | orderedTargets ->
                for index, target in orderedTargets |> List.indexed do
                    let prefix = if index = 0 then "if" else "else if"
                    writer.Line(prefix + " (" + valueName inputValueId + " is " + CSharpBackendNames.variantName target.VariantId + ") goto " + CSharpBackendNames.executionBlockLabel target.BlockId + ";")
                writer.Line("else throw new InvalidOperationException(\"Execution IR union value did not match a declared variant.\");")
        | ExecutionTerminator.MatchOption(inputValueId, someBlock, noneBlock) ->
            writer.Line("if (" + valueName inputValueId + ".HasValue) goto " + CSharpBackendNames.executionBlockLabel someBlock + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel noneBlock + ";")
        | ExecutionTerminator.MatchResult(inputValueId, okBlock, errBlock) ->
            writer.Line("if (" + valueName inputValueId + ".IsOk) goto " + CSharpBackendNames.executionBlockLabel okBlock + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel errBlock + ";")

    let private emitFunction
        (writer: SourceWriter)
        (parametersOf: FunctionId -> (ParameterId * TypeRef) list)
        (operationResultOf: CapabilityOperationId -> TypeRef)
        (functionEntity: ExecutionFunction) =
        let parameters =
            functionEntity.Parameters
            |> List.map (fun parameter ->
                formatType parameter.Value.Type
                + " "
                + CSharpBackendNames.parameterName parameter.SemanticParameterId)
            |> String.concat ", "

        let typeParameters =
            functionEntity.Parameters
            |> List.collect (fun parameter -> TypeParameters.occurring parameter.Value.Type)
            |> List.distinctBy fst
            |> List.map (fst >> CSharpBackendNames.typeParameterName)
        let generic = if List.isEmpty typeParameters then "" else "<" + String.concat ", " typeParameters + ">"
        writer.Line("public static " + formatType functionEntity.ResultType + " " + CSharpBackendNames.functionName functionEntity.SemanticFunctionId + generic + "(" + parameters + ")")
        writer.Line("{")
        writer.Push()

        let parameterValues =
            functionEntity.Parameters
            |> List.map (fun parameter -> parameter.Value)

        let parameterValueIds = parameterValues |> List.map (fun value -> value.Id) |> Set.ofList

        let bodyValues =
            functionEntity.Blocks
            |> List.collect (fun block ->
                block.Parameters
                @ (block.Instructions |> List.map (fun instruction -> instruction.Result)))
            |> List.filter (fun value -> not (Set.contains value.Id parameterValueIds))
            |> List.distinctBy (fun value -> value.Id)
            |> List.sortBy (fun value -> ExecutionValueId.value value.Id)

        let allExecutionValues =
            parameterValues @ bodyValues
            |> List.distinctBy (fun value -> value.Id)
            |> List.sortBy (fun value -> ExecutionValueId.value value.Id)

        for value in allExecutionValues do
            writer.Line(formatType value.Type + " " + valueName value.Id + " = default!;")

        let valueTypes =
            allExecutionValues
            |> List.map (fun value -> value.Id, value.Type)
            |> Map.ofList

        let jumpTemporaries =
            functionEntity.Blocks
            |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)
            |> List.collect (fun block ->
                match block.Terminator with
                | ExecutionTerminator.Jump(_, arguments) ->
                    arguments
                    |> List.indexed
                    |> List.map (fun (index, argument) ->
                        block.Id,
                        index,
                        Map.find argument.TargetValueId valueTypes)
                | _ -> [])

        for sourceBlockId, index, valueType in jumpTemporaries do
            writer.Line(formatType valueType + " " + CSharpBackendNames.jumpTemporaryName sourceBlockId index + " = default!;")

        for parameter in functionEntity.Parameters do
            writer.Line(valueName parameter.Value.Id + " = " + CSharpBackendNames.parameterName parameter.SemanticParameterId + ";")

        writer.Line("goto " + CSharpBackendNames.executionBlockLabel functionEntity.EntryBlockId + ";")
        writer.Line("")

        for block in functionEntity.Blocks |> List.sortBy (fun item -> ExecutionBlockId.value item.Id) do
            writer.Pop()
            writer.Line(CSharpBackendNames.executionBlockLabel block.Id + ":")
            writer.Push()
            for instruction in block.Instructions do
                emitInstruction writer parametersOf (fun valueId -> Map.find valueId valueTypes) operationResultOf instruction
            emitTerminator functionEntity.ResultType writer block block.Terminator
            writer.Line("")

        writer.Pop()
        writer.Line("}")
        writer.Line("")

    let private emitFunctions (writer: SourceWriter) (program: ExecutionProgram) =
        writer.Line("public static class " + CSharpBackendNames.functionContainerName)
        writer.Line("{")
        writer.Push()
        let parameters =
            program.Functions
            |> List.map (fun item -> item.SemanticFunctionId, item.Parameters |> List.map (fun parameter -> parameter.SemanticParameterId, parameter.Value.Type))
            |> Map.ofList
        let operationResults =
            program.Capabilities
            |> List.collect (fun capability -> capability.Operations |> List.map (fun operation -> operation.Id, operation.ResultType))
            |> Map.ofList
        for functionEntity in program.Functions |> List.sortBy (fun item -> StableId.value item.SemanticFunctionId) do
            emitFunction writer (fun functionId -> Map.find functionId parameters) (fun operationId -> Map.find operationId operationResults) functionEntity
        writer.Pop()
        writer.Line("}")

    let private symbol (id: 'Id) name : CSharpSymbol<'Id> =
        { Id = id; Name = name }

    let private symbolManifest
        (context: CSharpBackendValidation.Context)
        (program: ExecutionProgram) =
        let types = program.Types |> List.sortBy (fun item -> StableId.value item.Id)
        let fields =
            types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | ExecutionTypeKind.Product items -> items
                | ExecutionTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields))
            |> List.distinctBy (fun item -> StableId.value item.Id)
            |> List.sortBy (fun item -> StableId.value item.Id)
        let variants =
            types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | ExecutionTypeKind.Product _ -> []
                | ExecutionTypeKind.Union items -> items)
            |> List.sortBy (fun item -> StableId.value item.Id)
        let functions = program.Functions |> List.sortBy (fun item -> StableId.value item.SemanticFunctionId)
        let parameters =
            functions
            |> List.collect (fun functionEntity -> functionEntity.Parameters)
            |> List.sortBy (fun item -> StableId.value item.SemanticParameterId)
        let capabilities = program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id)
        let operations =
            capabilities
            |> List.collect (fun capability -> capability.Operations)
            |> List.sortBy (fun item -> StableId.value item.Id)
        let capabilityParameters =
            operations
            |> List.collect (fun operation -> operation.Parameters)
            |> List.sortBy (fun item -> StableId.value item.Id)

        { Namespace = CSharpBackendNames.generatedNamespace
          FunctionContainer = CSharpBackendNames.functionContainerName
          Types = types |> List.map (fun item -> symbol item.Id (CSharpBackendNames.typeName item.Id))
          Fields = fields |> List.map (fun item -> symbol item.Id (CSharpBackendNames.fieldName item.Id))
          Variants = variants |> List.map (fun item -> symbol item.Id (CSharpBackendNames.variantName item.Id))
          Functions =
            functions
            |> List.map (fun item ->
                symbol item.SemanticFunctionId (CSharpBackendNames.functionName item.SemanticFunctionId))
          Parameters =
            parameters
            |> List.map (fun item ->
                symbol item.SemanticParameterId (CSharpBackendNames.parameterName item.SemanticParameterId))
          Capabilities =
            capabilities
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityName item.Id))
          CapabilityOperations =
            operations
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityOperationName item.Id))
          CapabilityParameters =
            capabilityParameters
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityParameterName item.Id))
          CapabilityAdapters =
            capabilities
            |> List.filter (hasCapabilityAdapter context)
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityAdapterName item.Id)) }

    let emit
        (program: ExecutionProgram)
        (profile: CSharpBackendProfile)
        : Result<CSharpArtifact, CSharpBackendError> =
        match CSharpBackendValidation.prepare program profile with
        | Error error -> Error error
        | Ok context ->
            let writer = SourceWriter()
            writer.Line("// Generated by Nyrwvy. Do not edit as authoritative source.")
            writer.Line("#nullable enable")
            writer.Line("#pragma warning disable CS0219 // Preserve Execution IR assignments even when their values are unused.")
            writer.Line("using System;")
            writer.Line("")
            writer.Line("namespace " + CSharpBackendNames.generatedNamespace + ";")
            writer.Line("")
            emitRuntime writer
            emitConcurrencyRuntime writer program
            emitFunctionTypes writer program
            emitTypes writer program
            emitCapabilities writer program
            emitAdapters writer context program
            emitProgramImplementations writer context program
            emitFunctions writer program
            writer.Line("#pragma warning restore CS0219")
            Ok
                { Source = writer.ToString()
                  Symbols = symbolManifest context program }
