public interface INyTask
{
    System.Threading.Tasks.Task Running { get; }
    void Cancel();
}

public sealed class NyTask<T> : INyTask
{
    private readonly System.Threading.CancellationTokenSource _cancellation;
    internal NyTask(System.Threading.CancellationTokenSource cancellation, System.Threading.Tasks.Task<T> running)
    {
        _cancellation = cancellation;
        Running = running;
    }
    public System.Threading.Tasks.Task<T> Running { get; }
    System.Threading.Tasks.Task INyTask.Running => Running;
    public void Cancel() => _cancellation.Cancel();
    /// The result once finished; a host exception from the task is rethrown to whoever observes it.
    public NyOption<T> Snapshot() =>
        Running.IsCompleted ? NyOption<T>.Some(Running.GetAwaiter().GetResult()) : NyOption<T>.None();
}

public sealed class NyTaskScope
{
    private readonly System.Threading.CancellationTokenSource _cancellation;
    private readonly System.Threading.CancellationTokenRegistration _parentCancellation;
    private readonly NyCancellationPath? _ancestors;
    private readonly NyCancellationPath _path;
    private readonly System.Collections.Generic.List<INyTask> _tasks = new();
    private bool _closed;
    internal object HandoffGate { get; } = new();
    internal bool HandoffsClosed { get; private set; }
    private readonly System.Collections.Generic.HashSet<INyOneShot> _handoffs = new();
    public NyTaskScope(System.Threading.CancellationToken parent)
    {
        _ancestors = NyCancellationPath.Of(parent);
        _cancellation = new System.Threading.CancellationTokenSource();
        _path = new NyCancellationPath(_cancellation.Token, _ancestors);
        _cancellation.Token.Register(CloseHandoffs);
        _parentCancellation = parent.UnsafeRegister(static state => ((System.Threading.CancellationTokenSource)state!).Cancel(), _cancellation);
    }
    public NyOneShot<T> CreateOneShot<T>()
    {
        lock (HandoffGate)
        {
            var cell = new NyOneShot<T>(this, HandoffsClosed);
            if (!HandoffsClosed) _handoffs.Add(cell);
            return cell;
        }
    }
    // Called only by successful Complete while HandoffGate is held. Terminal cells retain
    // their own values; the owner tracks only unresolved cells it still needs to close.
    internal void ForgetCompletedOneShot(INyOneShot cell) => _handoffs.Remove(cell);
    private void CloseHandoffs()
    {
        lock (HandoffGate)
        {
            HandoffsClosed = true;
            foreach (var cell in _handoffs) cell.CloseLocked();
            _handoffs.Clear();
            System.Threading.Monitor.PulseAll(HandoffGate);
        }
    }
    public NyTask<T> Start<T>(Func<T> work)
    {
        // A starter that has seen an ancestor's cancellation must not start a clear task while that
        // ancestor's callbacks are still on their way down to this scope. The scope will be
        // cancelled anyway, so cancelling it now only moves that earlier. Outside the lock: its
        // callbacks take HandoffGate and may start tasks.
        if (!_cancellation.IsCancellationRequested && _ancestors != null && _ancestors.AnyCancellationRequested()) _cancellation.Cancel();
        System.Threading.CancellationTokenSource cancellation;
        NyCancellationPath path;
        lock (_tasks)
        {
            cancellation = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            path = new NyCancellationPath(cancellation.Token, _path);
            if (!_closed)
            {
                var running = NyRuntime.Run(() =>
                {
                    NyRuntime.Cancellation.Value = cancellation.Token;
                    NyRuntime.CancellationPath.Value = path;
                    return work();
                });
                var task = new NyTask<T>(cancellation, running);
                _tasks.Add(task);
                return task;
            }
        }
        // A scope that has completed runs a late start at once, already cancelled, so no task
        // outlives its scope. It runs outside the lock: a worker that starts tasks of this scope
        // from other threads (through ParallelAll) would otherwise wait on it forever.
        cancellation.Cancel();
        var previous = NyRuntime.Cancellation.Value;
        var previousPath = NyRuntime.CancellationPath.Value;
        NyRuntime.Cancellation.Value = cancellation.Token;
        NyRuntime.CancellationPath.Value = path;
        try { return new NyTask<T>(cancellation, System.Threading.Tasks.Task.FromResult(work())); }
        catch (Exception error) { return new NyTask<T>(cancellation, System.Threading.Tasks.Task.FromException<T>(error)); }
        finally { NyRuntime.Cancellation.Value = previous; NyRuntime.CancellationPath.Value = previousPath; }
    }
    /// Cancels and awaits every task, even when a cancellation callback throws. The body's
    /// exception wins, then the earliest-started faulted task, then a cancellation callback.
    public void Close(bool bodyThrew)
    {
        CloseHandoffs();
        INyTask[] tasks;
        lock (_tasks)
        {
            _closed = true;
            tasks = _tasks.ToArray();
        }
        Exception? cancellationError = null;
        try { _cancellation.Cancel(); } catch (Exception error) { cancellationError = error; }
        try { NyRuntime.WaitAll(Array.ConvertAll(tasks, task => task.Running)); }
        // Do not wait for a parent's running callback: it may be waiting for one of our tasks.
        finally { _parentCancellation.Unregister(); }
        if (bodyThrew) return;
        var faulted = Array.Find(tasks, task => task.Running.IsFaulted);
        if (faulted != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(faulted.Running.Exception!.InnerException!).Throw();
        if (cancellationError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
    }
}

public static class NyTasks
{
    /// `limit` is in milliseconds; a negative limit counts as 0. Real time uses the monotonic timer
    /// behind Task.WaitAny/WaitAll; under a virtual clock only scripted delays advance time.
    public static NyList<NyOption<T>> Wait<T>(NyList<NyTask<T>> tasks, bool any, NyOption<long> limit)
    {
        var items = tasks.Fold(new System.Collections.Generic.List<NyTask<T>>(), (acc, task) => { acc.Add(task); return acc; });
        var running = items.ConvertAll(task => (System.Threading.Tasks.Task)task.Running).ToArray();
        long? millis = limit.HasValue ? Math.Max(0L, limit.Value) : null;
        if (running.Length > 0)
        {
            var clock = NyRuntime.Clock;
            if (clock != null)
                clock.Block(any ? () => Array.Exists(running, task => task.IsCompleted) : () => Array.TrueForAll(running, task => task.IsCompleted), millis);
            else
            {
                // One Task wait accepts at most int.MaxValue ms (~24.8 days), so longer limits wait in steps.
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    long left = millis is long total ? Math.Max(0L, total - watch.ElapsedMilliseconds) : -1L;
                    int timeout = left < 0L ? System.Threading.Timeout.Infinite : (int)Math.Min(left, int.MaxValue);
                    bool done;
                    if (any) done = System.Threading.Tasks.Task.WaitAny(running, timeout) >= 0;
                    else { try { done = System.Threading.Tasks.Task.WaitAll(running, timeout); } catch (AggregateException) { done = true; } }
                    if (done || left <= int.MaxValue) break;
                }
            }
        }
        return NyList<NyOption<T>>.Create(items.ConvertAll(task => task.Snapshot()).ToArray());
    }
}
