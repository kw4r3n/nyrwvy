// Scope ownership and publication share one gate; this gate never enters the scheduler.
internal interface INyOneShot { void CloseLocked(); }
public sealed class NyOneShot<T> : INyOneShot
{
    private readonly NyTaskScope _owner;
    private int _state; // 0 pending, 1 completed, 2 closed
    private T _value = default!;
    internal NyOneShot(NyTaskScope owner, bool closed) { _owner = owner; _state = closed ? 2 : 0; }
    void INyOneShot.CloseLocked() { if (_state == 0) _state = 2; }
    public bool Complete(T value)
    {
        lock (_owner.HandoffGate)
        {
            if (_state != 0 || _owner.HandoffsClosed) return false;
            _value = value;
            _state = 1;
            _owner.ForgetCompletedOneShot(this);
            System.Threading.Monitor.PulseAll(_owner.HandoffGate);
            return true;
        }
    }
    private bool Ready() { lock (_owner.HandoffGate) return _state != 0; }
    public NyOption<T> Wait(NyOption<long> limit)
    {
        long? millis = limit.HasValue ? Math.Max(0L, limit.Value) : null;
        var token = NyRuntime.Cancellation.Value;
        if (NyRuntime.Clock is { } clock)
        {
            clock.Block(() => Ready() || token.IsCancellationRequested, millis);
            lock (_owner.HandoffGate) return _state == 1 ? NyOption<T>.Some(_value) : NyOption<T>.None();
        }
        // Register and dispose outside the gate: disposing while a callback waits for it can deadlock.
        using var wake = token.Register(() => { lock (_owner.HandoffGate) System.Threading.Monitor.PulseAll(_owner.HandoffGate); });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        lock (_owner.HandoffGate)
        {
            while (_state == 0 && !token.IsCancellationRequested)
            {
                long left = millis is long total ? Math.Max(0L, total - watch.ElapsedMilliseconds) : -1L;
                if (left == 0L) break;
                System.Threading.Monitor.Wait(_owner.HandoffGate, left < 0L ? System.Threading.Timeout.Infinite : (int)Math.Min(left, int.MaxValue));
            }
            return _state == 1 ? NyOption<T>.Some(_value) : NyOption<T>.None();
        }
    }
}
