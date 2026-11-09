/// A runtime-made token and the tokens it descends from, nearest first. Cancel() flags a token
/// before its callbacks cancel the linked descendants, so a descendant can still read clear
/// after an ancestor is flagged; the path lets a scope see that ancestor.
internal sealed class NyCancellationPath
{
    internal NyCancellationPath(System.Threading.CancellationToken token, NyCancellationPath? parent) { Token = token; Parent = parent; }
    internal System.Threading.CancellationToken Token { get; }
    internal NyCancellationPath? Parent { get; }
    /// The ambient path when it belongs to `token`; a host token starts a path of its own and
    /// the shield's None has none, so a shield cuts the path as it cuts cancellation.
    internal static NyCancellationPath? Of(System.Threading.CancellationToken token)
    {
        var ambient = NyRuntime.CancellationPath.Value;
        if (ambient != null && ambient.Token == token) return ambient;
        return token.CanBeCanceled ? new NyCancellationPath(token, null) : null;
    }
    internal bool AnyCancellationRequested()
    {
        for (var path = this; path != null; path = path.Parent) if (path.Token.IsCancellationRequested) return true;
        return false;
    }
}

public static class NyRuntime
{
    public static readonly System.Threading.AsyncLocal<System.Threading.CancellationToken> Cancellation = new();
    /// The path of `Cancellation` while it holds a token the runtime made; any other token starts its own.
    internal static readonly System.Threading.AsyncLocal<NyCancellationPath?> CancellationPath = new();
    /// Set once before the entry call by a deterministic runner; null runs on real time.
    public static NyVirtualClock? Clock;
    public static bool CancellationRequested => Cancellation.Value.IsCancellationRequested;
    public static void WaitForCancellation()
    {
        var token = Cancellation.Value;
        if (Clock is { } clock) clock.Block(() => token.IsCancellationRequested, null);
        else token.WaitHandle.WaitOne();
    }
    /// Blocks for a scripted delay; only a runner's fakes use it.
    public static void Delay(long millis)
    {
        if (Clock is { } clock) clock.Block(() => false, Math.Max(0L, millis), isDelay: true);
        else System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(Math.Max(0L, millis)));
    }
    public static System.Threading.Tasks.Task<T> Run<T>(Func<T> work)
    {
        // A dedicated thread: a task blocks (on its turn, or a wait in real time), which would starve the pool.
        if (Clock is not { } clock)
            return System.Threading.Tasks.Task.Factory.StartNew(work, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
        var start = clock.Enter();
        var running = System.Threading.Tasks.Task.Factory.StartNew(() => { clock.Start(start); return work(); },
            System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
        running.ContinueWith(_ => clock.Exit(), System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        return running;
    }
    public static void WaitAll(System.Threading.Tasks.Task[] tasks)
    {
        if (tasks.Length == 0) return;
        if (Clock is { } clock) clock.Block(() => Array.TrueForAll(tasks, task => task.IsCompleted), null);
        else foreach (var task in tasks) { try { task.Wait(); } catch (AggregateException) { } }
    }
}

/// Deterministic scheduling for runners. One thread runs at a time; a started task waits for its turn.
/// When the running thread blocks or finishes, the next one resumes: first an elapsed delay, by
/// (deadline, start order), so everything due at a time has happened before a wait observes it; then
/// a thread that can continue (a new task, a wait whose condition holds or whose limit elapsed),
/// lowest start order first. Otherwise time jumps to the earliest deadline. A run therefore depends
/// only on the program and its scripted delays, never on thread timing or real elapsed time.
public sealed class NyVirtualClock
{
    public static readonly System.Threading.AsyncLocal<long> Order = new();
    private sealed class Waiter
    {
        public required Func<bool> Ready;
        public long? Due;
        public long Order;
        public bool IsDelay;
        public Exception? Failure;
        /// Released when the scheduler picks this waiter, so a turn wakes one thread, not all of them.
        public readonly System.Threading.SemaphoreSlim Turn = new(0);
    }
    private readonly object _gate = new();
    private readonly System.Collections.Generic.List<Waiter> _waiters = new();
    private int _running = 1;
    private long _nextOrder = 1;
    private long _now;
    /// The deadline `millis` after now, saturating at long.MaxValue so that virtual time never
    /// wraps: a limit too large to represent is one that never elapses, not one already past.
    private long Deadline(long millis) => millis >= long.MaxValue - _now ? long.MaxValue : _now + Math.Max(0L, millis);
    /// Registers a task that has not started; it runs once scheduled. Returns its start ticket.
    public object Enter()
    {
        lock (_gate)
        {
            var waiter = new Waiter { Ready = () => true, Order = _nextOrder++ };
            _waiters.Add(waiter);
            return waiter;
        }
    }
    /// Called first on the task's own thread: waits for its turn and takes on its start order.
    public void Start(object ticket)
    {
        var waiter = (Waiter)ticket;
        Order.Value = waiter.Order;
        waiter.Turn.Wait();
        if (waiter.Failure != null) throw waiter.Failure;
    }
    public void Exit() { lock (_gate) { _running--; Schedule(); } }
    /// Blocks until `ready` holds or `millis` of virtual time have passed; true when ready.
    public bool Block(Func<bool> ready, long? millis, bool isDelay = false)
    {
        var waiter = new Waiter { Ready = ready, Order = Order.Value, IsDelay = isDelay };
        lock (_gate)
        {
            waiter.Due = millis is long value ? Deadline(value) : null;
            _waiters.Add(waiter);
            _running--;
            Schedule();
        }
        waiter.Turn.Wait();
        if (waiter.Failure != null) throw waiter.Failure;
        return ready();
    }
    private void Schedule()
    {
        if (_running > 0 || _waiters.Count == 0) return;
        // A failed waiter has no deadline and is no delay, so Earliest picks the lowest start order.
        var next = Earliest(waiter => waiter.Failure != null) ?? Earliest(waiter => waiter.IsDelay && waiter.Due <= _now);
        if (next == null)
        {
            foreach (var waiter in _waiters)
                if ((waiter.Ready() || waiter.Due <= _now) && (next == null || waiter.Order < next.Order)) next = waiter;
        }
        if (next == null)
        {
            next = Earliest(waiter => waiter.Due != null);
            if (next == null)
            {
                // Nothing can ever resume: every waiter fails instead of hanging, released one at a
                // time by start order so that their unwinding never runs on two threads at once.
                foreach (var waiter in _waiters)
                    waiter.Failure = new InvalidOperationException("Every task is blocked and no delay is pending, so the program cannot continue.");
                next = Earliest(waiter => waiter.Failure != null)!;
            }
            else _now = next.Due!.Value;
        }
        _waiters.Remove(next);
        _running++;
        next.Turn.Release();
    }
    /// The matching waiter with the earliest deadline; delays before waits, then start order.
    private Waiter? Earliest(Func<Waiter, bool> matches)
    {
        Waiter? earliest = null;
        foreach (var waiter in _waiters)
            if (matches(waiter) && (earliest == null || (waiter.Due, waiter.IsDelay ? 0 : 1, waiter.Order).CompareTo((earliest.Due, earliest.IsDelay ? 0 : 1, earliest.Order)) < 0))
                earliest = waiter;
        return earliest;
    }
}
