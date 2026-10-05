using System;
using System.Threading;
using System.Threading.Tasks;
using Nyrwvy.Generated;

static class OneShotRuntimeChecks
{
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static readonly NyOption<long> Forever = NyOption<long>.None();
    static readonly NyOption<long> Poll = NyOption<long>.Some(0L);
    // The registry is private runtime state; inspect only in this fixture, never expose it to graphs.
    static int PendingCount(NyTaskScope owner)
    {
        var field = typeof(NyTaskScope).GetField("_handoffs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var registry = field.GetValue(owner)!;
        return (int)registry.GetType().GetProperty("Count")!.GetValue(registry)!;
    }
    public static void Main()
    {
        ThreadPool.SetMinThreads(32, 32);
        using var watchdog = new Timer(_ => Environment.Exit(99), null, 30000, Timeout.Infinite);
        var longLived = new NyTaskScope(CancellationToken.None);
        var stillPending = longLived.CreateOneShot<long>();
        for (int i = 0; i < 1000; i++)
        {
            var transient = longLived.CreateOneShot<byte[]>();
            Check(PendingCount(longLived) == 2, "only unresolved cells registered before publication");
            Check(transient.Complete(new byte[1024]), "transient publication");
            Check(PendingCount(longLived) == 1, "completed payload no longer retained by pending registry");
        }
        Check(!stillPending.Wait(Poll).HasValue, "unrelated pending cell remains registered");
        longLived.Close(false);
        Check(PendingCount(longLived) == 0 && !stillPending.Complete(1L), "closure empties pending registry");
        var scope = new NyTaskScope(CancellationToken.None);
        var cell = scope.CreateOneShot<long>();
        var observers = new Task<NyOption<long>>[8];
        for (int i = 0; i < observers.Length; i++) observers[i] = Task.Run(() => cell.Wait(Forever));
        Check(cell.Complete(41L), "first publication");
        Check(!cell.Complete(99L), "duplicate refused");
        Check(Task.WaitAll(observers, 5000), "multi-observer join");
        foreach (var observer in observers) Check(observer.Result.HasValue && observer.Result.Value == 41L, "same immutable outcome");
        scope.Close(false);
        Check(cell.Wait(Forever).Value == 41L && !cell.Complete(42L), "completed reads after closure");
        var late = scope.CreateOneShot<long>();
        Check(!late.Wait(Forever).HasValue && !late.Complete(1L), "closed-scope creation");

        var openOwner = new NyTaskScope(CancellationToken.None);
        var timed = openOwner.CreateOneShot<long>();
        Check(!timed.Wait(NyOption<long>.Some(-1L)).HasValue, "negative is poll");
        Check(!timed.Wait(NyOption<long>.Some(1L)).HasValue, "local timeout");
        Check(timed.Complete(7L) && timed.Wait(Poll).Value == 7L, "timeout did not close cell");
        var cancelled = openOwner.CreateOneShot<long>();
        using var waiterCancel = new CancellationTokenSource();
        var waiter = Task.Run(() => { NyRuntime.Cancellation.Value = waiterCancel.Token; return cancelled.Wait(Forever); });
        waiterCancel.Cancel();
        Check(waiter.Wait(5000) && !waiter.Result.HasValue, "waiter cancellation wakes");
        Check(cancelled.Complete(8L) && cancelled.Wait(Poll).Value == 8L, "waiter cancel did not close owner");
        var otherScope = new NyTaskScope(CancellationToken.None);
        otherScope.Close(false);
        Check(cancelled.Wait(Poll).Value == 8L, "foreign scope does not take ownership");
        openOwner.Close(false);
        var ownerA = new NyTaskScope(CancellationToken.None);
        var ownerB = new NyTaskScope(CancellationToken.None);
        var cross = ownerA.CreateOneShot<long>();
        var foreignWaiter = ownerB.Start(() => cross.Wait(Forever));
        ownerB.Close(false);
        Check(!foreignWaiter.Snapshot().Value.HasValue, "foreign wait cancelled with its own task");
        Check(cross.Complete(15L), "closing foreign scope does not close original owner's pending cell");
        ownerA.Close(false);
        Check(cross.Wait(Forever).Value == 15L, "escaped completed cell keeps original ownership and value");


        using var parent = new CancellationTokenSource();
        var owned = new NyTaskScope(parent.Token);
        var pending = owned.CreateOneShot<long>();
        bool finished = false;
        var task = owned.Start(() => { try { return pending.Wait(Forever); } finally { finished = true; } });
        parent.Cancel();
        owned.Close(false);
        Check(finished && task.Running.IsCompleted && !task.Snapshot().Value.HasValue, "parent cancellation closes cells and joins");
        Check(!pending.Complete(1L), "closed pending publication refused");

        var ending = new NyTaskScope(CancellationToken.None);
        var held = ending.CreateOneShot<long>();
        bool ended = false;
        ending.Start(() => { try { return held.Wait(Forever); } finally { ended = true; } });
        int release = 0;
        try { ending.Close(false); Check(ended, "cell closure before scope join"); }
        finally { release++; }
        Check(release == 1, "release after task settlement once");

        for (int iteration = 0; iteration < 100; iteration++)
        {
            var raceOwner = new NyTaskScope(CancellationToken.None);
            var race = raceOwner.CreateOneShot<long>();
            using var barrier = new Barrier(3);
            var first = Task.Run(() => { barrier.SignalAndWait(); return race.Complete(1L); });
            var second = Task.Run(() => { barrier.SignalAndWait(); return race.Complete(2L); });
            barrier.SignalAndWait();
            Check(Task.WaitAll(new Task[] { first, second }, 5000), "publication race join");
            Check(first.Result != second.Result, "exactly one publication wins");
            Check(race.Wait(Poll).Value == (first.Result ? 1L : 2L), "winner remains observable");
            raceOwner.Close(false);

            var closingOwner = new NyTaskScope(CancellationToken.None);
            var closing = closingOwner.CreateOneShot<long>();
            using var closeBarrier = new Barrier(3);
            var publish = Task.Run(() => { closeBarrier.SignalAndWait(); return closing.Complete(3L); });
            var close = Task.Run(() => { closeBarrier.SignalAndWait(); closingOwner.Close(false); });
            closeBarrier.SignalAndWait();
            Check(Task.WaitAll(new Task[] { publish, close }, 5000), "closure race join");
            var observed = closing.Wait(Forever);
            Check(observed.HasValue == publish.Result && (!observed.HasValue || observed.Value == 3L), "publication and owner closure linearize");
            Check(!closing.Complete(4L), "cannot reopen after race");
        }
        Console.WriteLine("PASS: emitted real runtime; bounded pending registry over 1000 publications, multi-observer, timeout/cancel ownership, terminal handles, scope join/release and 200 controlled races.");
    }
}
