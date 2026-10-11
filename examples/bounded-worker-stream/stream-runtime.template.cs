using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nyrwvy.Generated;

// This host supplies cancellable fixture I/O and observation barriers only.
// Admission, cursors, acknowledgements, routing, Stop and task ownership all
// remain in the unedited generated graph. No host queue transports records.
sealed record Observation(string Id, string Kind, long Sequence, bool Accepted, long Steps);
sealed record Record(string Id, long Sequence, string Payload);
sealed class StreamHost : @@HOST@@, IDisposable
{
    public readonly object Gate = new();
    public readonly string Scenario;
    public readonly string? GcWorker;
    public readonly long GcSequence;
    public readonly List<Record> Reads = new();
    public readonly List<Record> WriteAttempts = new();
    public readonly List<Record> Writes = new();
    public readonly List<Observation> Observations = new();
    public readonly List<string> Events = new();
    public bool ReleaseRead, ReleaseWrite, Checkpoint, WriteEntered, ReadOneEntered, ReadOneBlocked;
    public int Active, OpenCount, CloseCount, FinishCount, MaximumBacklog;
    public bool Closed;
    public string? FinishOutcome;
    const int WatchdogMilliseconds = 15000;

    public StreamHost(string scenario, string? gcWorker = null, long gcSequence = -1)
    { Scenario = scenario; GcWorker = gcWorker; GcSequence = gcSequence; }

    void Enter(string operation)
    {
        lock (Gate)
        {
            StreamChecks.Check(!Closed, $"host operation after close: {operation}");
            Active++;
            Events.Add("enter:" + operation);
            Monitor.PulseAll(Gate);
        }
    }
    void Exit(string operation)
    {
        lock (Gate) { Active--; Events.Add("exit:" + operation); Monitor.PulseAll(Gate); }
    }
    // Predicates are inspected under Gate. This is an I/O test barrier, never a
    // stream mailbox. Cancellation wakes it without requiring a watchdog.
    public void WaitFor(Func<bool> predicate, string label, CancellationToken token = default)
    {
        using var wake = token.Register(() => { lock (Gate) Monitor.PulseAll(Gate); });
        var watch = Stopwatch.StartNew();
        lock (Gate)
        {
            while (!predicate())
            {
                token.ThrowIfCancellationRequested();
                var left = WatchdogMilliseconds - watch.ElapsedMilliseconds;
                if (left <= 0) throw new TimeoutException("fixture watchdog: " + label);
                Monitor.Wait(Gate, (int)Math.Min(left, 100));
            }
            token.ThrowIfCancellationRequested();
        }
    }
    public bool Accepted(string id, long sequence) =>
        Observations.Any(x => x.Id == id && x.Kind == "data" && x.Sequence == sequence && x.Accepted);
    bool Stopped(string id) => Observations.Any(x => x.Id == id && x.Kind == "stop" && x.Accepted);
    bool RejectedOne() => Observations.Any(x => x.Id == "w1" && x.Kind == "data" && x.Sequence == 1 && !x.Accepted);
    public void ReleaseAll()
    {
        lock (Gate) { ReleaseRead = true; ReleaseWrite = true; Monitor.PulseAll(Gate); }
    }

    public NyResult<NyUnit, NyStr> @@OPEN@@()
    {
        Enter("open");
        try { lock (Gate) OpenCount++; return NyResult<NyUnit, NyStr>.Ok(NyUnit.Value); }
        finally { Exit("open"); }
    }
    public NyUnit @@CLOSE@@(NyUnit handle)
    {
        lock (Gate)
        {
            StreamChecks.Check(Active == 0, "resource close must follow every in-flight host operation");
            StreamChecks.Check(!Closed && CloseCount == 0, "resource closes exactly once");
            Closed = true; CloseCount++; Events.Add("close"); Monitor.PulseAll(Gate);
        }
        return NyUnit.Value;
    }
    public NyResult<NyStr, NyStr> @@READ@@(NyStr rawId, long sequence)
    {
        string id = rawId;
        var operation = $"read:{id}:{sequence}";
        Enter(operation);
        try
        {
            var token = NyRuntime.Cancellation.Value;
            token.ThrowIfCancellationRequested();
            lock (Gate)
            {
                Reads.Add(new Record(id, sequence, $"{id}:{sequence}"));
                if (Scenario == "stop-first" && id == "w1" && sequence == 1)
                {
                    ReadOneEntered = true;
                    ReadOneBlocked = !Stopped("w1");
                }
                Monitor.PulseAll(Gate);
            }
            if (Scenario == "idle-input" && id == "w1" && sequence == 0)
                WaitFor(() => ReleaseRead, "release idle source", token);
            if (Scenario == "stop-first" && id == "w1" && sequence == 1)
                WaitFor(() => Stopped("w1"), "Stop wins before blocked source returns", token);
            if (Scenario == "source-error" && id == "w2" && sequence == 0)
                return NyResult<NyStr, NyStr>.Err("source failed");
            return NyResult<NyStr, NyStr>.Ok($"{id}:{sequence}");
        }
        catch (OperationCanceledException) when (NyRuntime.Cancellation.Value.IsCancellationRequested)
        { return NyResult<NyStr, NyStr>.Err("cancelled"); }
        finally { Exit(operation); }
    }
    public NyResult<NyUnit, NyStr> @@WRITE@@(NyStr rawId, long sequence, NyStr rawPayload)
    {
        string id = rawId, payload = rawPayload;
        var operation = $"write:{id}:{sequence}";
        Enter(operation);
        try
        {
            var token = NyRuntime.Cancellation.Value;
            lock (Gate) { WriteAttempts.Add(new Record(id, sequence, payload)); WriteEntered = true; Monitor.PulseAll(Gate); }
            // Observe follows CAS. A fast collector may arrive here first, so
            // order instrumentation before recording a successful sink result.
            WaitFor(() => Accepted(id, sequence), "matching accepted observation", token);
            if ((Scenario is "slow-sink" or "sink-error" or "sink-exception") && id == "w1" && sequence == 0)
            {
                WaitFor(() => Accepted("w1", 0) && Accepted("w2", 0), "two accepted records before sink release", token);
                lock (Gate) { Checkpoint = true; Monitor.PulseAll(Gate); }
                WaitFor(() => ReleaseWrite, "release slow sink", token);
            }
            if (Scenario == "stop-first" && id == "w1" && sequence == 0)
                WaitFor(() => Accepted("w1", 0) && Accepted("w2", 0), "both producers admitted before Stop threshold", token);
            if (Scenario == "send-first" && id == "w2" && sequence == 0)
                WaitFor(() => Accepted("w1", 1), "next left Data wins before Stop", token);
            if (Scenario == "parent-exit" || Scenario == "parent-token-cancel")
                WaitFor(() => false, "parent cancellation releases sink", token);
            if (GcWorker == id && GcSequence == sequence)
            {
                lock (Gate) { Checkpoint = true; Monitor.PulseAll(Gate); }
                WaitFor(() => ReleaseWrite, "release live-GC sink", token);
            }
            if (Scenario == "sink-error" && id == "w1" && sequence == 0)
                return NyResult<NyUnit, NyStr>.Err("sink failed");
            if (Scenario == "sink-exception" && id == "w1" && sequence == 0)
                throw new InvalidOperationException("sink exception");
            token.ThrowIfCancellationRequested();
            lock (Gate) { Writes.Add(new Record(id, sequence, payload)); Monitor.PulseAll(Gate); }
            return NyResult<NyUnit, NyStr>.Ok(NyUnit.Value);
        }
        catch (OperationCanceledException) when (NyRuntime.Cancellation.Value.IsCancellationRequested)
        { return NyResult<NyUnit, NyStr>.Err("cancelled"); }
        finally { Exit(operation); }
    }
    public NyUnit @@OBSERVE@@(NyStr rawId, NyStr rawKind, long sequence, bool accepted, long steps)
    {
        string id = rawId, kind = rawKind;
        var operation = $"observe:{id}:{kind}:{sequence}:{accepted}";
        Enter(operation);
        try
        {
            var token = NyRuntime.Cancellation.Value;
            // Stop is already published when this callback starts. Keep its
            // diagnostic notification pending until the source is in read(1),
            // then release that read to exercise rejection against the Stop.
            if (Scenario == "stop-first" && id == "w1" && kind == "stop" && accepted)
                WaitFor(() => ReadOneEntered, "left producer enters its blocked second read", token);
            lock (Gate)
            {
                Observations.Add(new Observation(id, kind, sequence, accepted, steps));
                if (kind == "data" && accepted)
                {
                    var backlog = Observations.Count(x => x.Kind == "data" && x.Accepted) - Writes.Count;
                    MaximumBacklog = Math.Max(MaximumBacklog, backlog);
                    StreamChecks.Check(backlog >= 0 && backlog <= 2, "accepted/sink-pending backlog is at most two");
                    foreach (var worker in new[] { "w1", "w2" })
                        StreamChecks.Check(Observations.Count(x => x.Id == worker && x.Kind == "data" && x.Accepted) - Writes.Count(x => x.Id == worker) <= 1,
                            "each producer has at most one accepted/sink-pending record");
                }
                Monitor.PulseAll(Gate);
            }
            // Keep the scope open until the blocked producer has really lost
            // its append. Merely observing Stop would not prove rejection.
            if (Scenario == "stop-first" && id == "w2" && kind == "stop")
                WaitFor(RejectedOne, "blocked left producer reports a rejected Data append", token);
            return NyUnit.Value;
        }
        finally { Exit(operation); }
    }
    public NyUnit @@WAITABORT@@()
    {
        Enter("waitAbort");
        try
        {
            WaitFor(() => Accepted("w1", 0) && Accepted("w2", 0) && WriteEntered,
                "both workers live before explicit parent exit", NyRuntime.Cancellation.Value);
            return NyUnit.Value;
        }
        finally { Exit("waitAbort"); }
    }
    public NyUnit @@FINISH@@(NyStr rawOutcome)
    {
        string outcome = rawOutcome;
        Enter("finish");
        try { lock (Gate) { FinishCount++; FinishOutcome = outcome; } return NyUnit.Value; }
        finally { Exit("finish"); }
    }
    public void Dispose() { }
}

static class StreamChecks
{
    public static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static NyResult<NyStr, NyStr> RunGraph(StreamHost host, long left, long right, long stop = -1, string mode = "normal") =>
        NyFunctions.@@RUN@@(@@RUN_HOST@@: host, @@RUN_LEFTCOUNT@@: left, @@RUN_RIGHTCOUNT@@: right,
            @@RUN_STOPAFTER@@: stop, @@RUN_MODE@@: mode);
    static string Key(Record item) => $"{item.Id}:{item.Sequence}";
    static string Key(Observation item) => $"{item.Id}:{item.Sequence}";
    static void Closed(StreamHost host, bool finish)
    {
        lock (host.Gate)
        {
            Check(host.OpenCount == 1 && host.CloseCount == 1 && host.Closed, "open and close once");
            Check(host.Active == 0 && host.Events.Last() == "close", "close is after all host operations have settled");
            Check(host.FinishCount == (finish ? 1 : 0), "finish only on successful graph completion");
        }
    }
    static void Appends(StreamHost host, long? normalDataCount = null)
    {
        var accepted = host.Observations.Where(x => x.Accepted).ToArray();
        Check(accepted.All(x => x.Steps >= 1 && x.Steps <= 2), "successful append steps stay within one or two");
        if (normalDataCount.HasValue)
        {
            Check(accepted.Length == normalDataCount.Value + 2, "normal Data plus two Done records");
            Check(accepted.Sum(x => x.Steps) == normalDataCount.Value + 2, "normal append work is N + 2");
            Check(host.Observations.All(x => x.Accepted), "normal execution has no rejected append");
        }
    }
    static void ExactDelivery(StreamHost host, long left, long right, long stop = -1)
    {
        var accepted = host.Observations.Where(x => x.Accepted && x.Kind == "data").ToArray();
        Check(accepted.Select(Key).Order().SequenceEqual(host.Writes.Select(Key).Order()), "successful sink writes equal the accepted Data multiset");
        Check(host.Writes.Select(Key).Distinct().Count() == host.Writes.Count, "no duplicate sink write");
        foreach (var worker in new[] { "w1", "w2" })
        {
            var written = host.Writes.Where(x => x.Id == worker).ToArray();
            Check(written.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(0, written.Length).Select(x => (long)x)), "per-worker order is a complete prefix");
        }
        Check(host.Writes.All(x => x.Payload == Key(x)), "sink payloads preserve worker routing");
        Check(host.MaximumBacklog <= 2, "bounded accepted backlog");
        if (stop < 0) Check(host.Writes.Count == left + right, "all normal input delivered");
        else Check(host.Writes.Count >= stop && host.Writes.Count <= stop + 2, "Stop drains only the accepted bounded prefix");
        Appends(host, stop < 0 ? left + right : null);
    }
    static Task<NyResult<NyStr, NyStr>> StartRun(StreamHost host, long left, long right, long stop, string mode, CancellationToken token) =>
        Task.Run(() =>
        {
            NyRuntime.Cancellation.Value = token;
            return RunGraph(host, left, right, stop, mode);
        });
    static T Await<T>(Task<T> task)
    {
        // The process watchdog also catches synchronous generated-runtime waits.
        Check(Task.WhenAny(task, Task.Delay(20000)).GetAwaiter().GetResult() == task, "new fixture task settlement watchdog");
        return task.GetAwaiter().GetResult();
    }
    static void Stable(StreamHost host, Action assertion)
    {
        // A short bounded observation window tests a stalled fixture, not speed.
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 150)
        {
            lock (host.Gate) assertion();
            Thread.Sleep(5);
        }
    }
    static void Functional(string scenario, long left = 4, long right = 4, long stop = -1)
    {
        using var host = new StreamHost(scenario);
        using var parent = new CancellationTokenSource();
        var run = StartRun(host, left, right, stop, scenario == "parent-exit" ? "parent-exit" : "normal", parent.Token);
        NyResult<NyStr, NyStr> result = default;
        Exception? fault = null;
        try
        {
            if (scenario == "idle-input")
            {
                host.WaitFor(() => host.Reads.Count == 2 && host.Accepted("w2", 0), "idle source checkpoint");
                Stable(host, () =>
                {
                    Check(host.Reads.Count == 2 && host.Writes.Count == 0 && host.WriteAttempts.Count == 0,
                        "round-robin waits at idle input without reading a third record");
                    Check(host.Observations.Count(x => x.Kind == "data" && x.Accepted) == 1, "idle source leaves one accepted record bounded");
                    Check(!run.IsCompleted, "idle stream must remain live");
                });
                host.ReleaseAll();
            }
            if (scenario is "slow-sink" or "sink-error" or "sink-exception")
            {
                host.WaitFor(() => host.Checkpoint, "slow sink checkpoint");
                Stable(host, () =>
                {
                    Check(host.Reads.Count == 2 && host.Writes.Count == 0 && host.WriteAttempts.Count == 1,
                        "held sink admits exactly two records and prevents a third read");
                    Check(host.Observations.Count(x => x.Kind == "data" && x.Accepted) == 2, "two accepted records while first sink is held");
                    Check(!run.IsCompleted, "slow sink stream must remain live");
                });
                host.ReleaseAll();
            }
            if (scenario == "parent-token-cancel")
            {
                host.WaitFor(() => host.Accepted("w1", 0) && host.Accepted("w2", 0) && host.WriteEntered, "external parent token checkpoint");
                parent.Cancel();
            }
            try { result = Await(run); } catch (Exception error) { fault = error; }
        }
        finally
        {
            host.ReleaseAll();
            if (!run.IsCompleted) { parent.Cancel(); try { _ = Await(run); } catch { } }
        }
        var failure = scenario is "sink-error" or "source-error" or "parent-exit" or "parent-token-cancel";
        if (scenario == "sink-exception")
            Check(fault is not null && fault.ToString().Contains("sink exception", StringComparison.Ordinal), "sink host exception propagates");
        else
        {
            if (fault is not null) throw new Exception("unexpected host/runtime exception in " + scenario, fault);
            Check(result.IsOk == !failure, "expected Result shape for " + scenario);
            var expected = scenario switch
            {
                "sink-error" => "sink failed", "source-error" => "source failed",
                "parent-exit" or "parent-token-cancel" => "cancelled", _ => stop >= 0 ? "stopped" : "done"
            };
            Check((string)(result.IsOk ? result.OkValue : result.ErrorValue) == expected, "expected Result value for " + scenario);
        }
        Closed(host, !failure && scenario != "sink-exception");
        Appends(host);
        if (!failure && scenario != "sink-exception")
        {
            ExactDelivery(host, left, right, stop);
            Check(host.FinishOutcome == (stop >= 0 ? "stopped" : "done"), "finish sees the graph outcome");
        }
        if (scenario == "stop-first")
        {
            Check(host.ReadOneEntered && host.ReadOneBlocked, "Stop-first really held the next producer read");
            Check(host.Observations.Any(x => x.Id == "w1" && x.Kind == "data" && x.Sequence == 1 && !x.Accepted), "Stop-first blocked producer actually rejects its next Data");
            Check(host.Observations.Any(x => x.Id == "w1" && x.Kind == "stop" && x.Accepted && x.Steps == 1), "Stop-first inserts before next left Data");
        }
        if (scenario == "send-first")
        {
            Check(host.Observations.Any(x => x.Id == "w1" && x.Kind == "stop" && x.Accepted && x.Steps == 2), "Send-first Stop walks the already accepted next Data");
            Check(host.Writes.Any(x => x.Id == "w1" && x.Sequence == 1), "Send-first drains the accepted racing record");
        }
        Console.WriteLine($"PASS: {scenario}; accepted={host.Observations.Count(x => x.Kind == "data" && x.Accepted)}; writes={host.Writes.Count}; max-backlog={host.MaximumBacklog}; append-steps={host.Observations.Where(x => x.Accepted).Sum(x => x.Steps)}; close-after-settlement=once");
    }

    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static int Pending(NyTaskScope owner)
    {
        // Read-only diagnostic inspection under the runtime's own registry gate.
        var gate = typeof(NyTaskScope).GetProperty("HandoffGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lock (gate)
        {
            var value = typeof(NyTaskScope).GetField("_handoffs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static NyList<NyTask<NyResult<NyStr, NyStr>>> Launch(StreamHost host, NyTaskScope owner, long leftCount, long rightCount, out WeakReference[] weak, out NyOneShot<NyUnit> startGate)
    {
        startGate = NyFunctions.@@NEWGATE@@(@@NEWGATE_SCOPE@@: owner);
        var left = NyFunctions.@@NEWCURSOR@@(@@NEWCURSOR_SCOPE@@: owner);
        var right = NyFunctions.@@NEWCURSOR@@(@@NEWCURSOR_SCOPE@@: owner);
        weak = new[] { new WeakReference(left), new WeakReference(right) };
        // Return tasks only, never either head or the initial collector state.
        return NyFunctions.@@LAUNCHWITHHEADS@@(@@LAUNCHWITHHEADS_HOST@@: host, @@LAUNCHWITHHEADS_SCOPE@@: owner, @@LAUNCHWITHHEADS_GATE@@: startGate,
            @@LAUNCHWITHHEADS_LEFT@@: left, @@LAUNCHWITHHEADS_RIGHT@@: right,
            @@LAUNCHWITHHEADS_LEFTCOUNT@@: leftCount, @@LAUNCHWITHHEADS_RIGHTCOUNT@@: rightCount, @@LAUNCHWITHHEADS_STOPAFTER@@: -1L);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference UnrootedControl() => new(new object());
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool[] Alive(WeakReference[] weak) => weak.Select(x => x.IsAlive).ToArray();
    static void LiveGc(string label, long left, long right, string worker, long sequence)
    {
        using var host = new StreamHost(label, worker, sequence);
        var owner = new NyTaskScope(CancellationToken.None);
        Check(host.@@OPEN@@().IsOk, "GC fixture resource opens");
        var tasks = Launch(host, owner, left, right, out var weak, out var startGate);
        // No producer can advance while a launcher frame still owns its head.
        Check(startGate.Complete(NyUnit.Value), "start gate completes after launcher frame returns");
        // The coordinator receives only task handles after Launch has returned.
        var coordinator = Task.Run(() => NyFunctions.@@AWAITTASKS@@(@@AWAITTASKS_TASKS@@: tasks));
        var settled = false;
        try
        {
            host.WaitFor(() => host.Checkpoint, "complete graph advances to live-GC checkpoint");
            Check(tasks.Length == 3, "complete stream owns exactly three fixed worker tasks");
            Check(!coordinator.IsCompleted, "coordinator and owner remain live at GC");
            lock (host.Gate)
            {
                Check(host.Writes.Count(x => x.Id == "w1") >= Math.Min(left, sequence), "left cursor has advanced before GC");
                Check(host.Writes.Count(x => x.Id == "w2") >= sequence, "right cursor has advanced before GC");
                Check(host.Active > 0 && !host.Closed, "real host sink stays blocked while owner is open");
            }
            var rootedControl = new object();
            var rootedWeak = new WeakReference(rootedControl);
            var unrootedWeak = UnrootedControl();
            Collect();
            Check(rootedWeak.IsAlive && !unrootedWeak.IsAlive, "positive and negative GC controls");
            var alive = Alive(weak);
            var pending = Pending(owner);
            Console.WriteLine($"OBSERVE: {label}; left={left}; right={right}; checkpoint={worker}:{sequence}; original-heads-alive={string.Join(',', alive)}; unresolved-registry={pending}; owner-tasks={tasks.Length}");
            Check(pending > 0 && pending <= 6, "live full-graph pending registry has a constant bound of six");
            Check(alive.All(x => !x), "discarded original heads must collect while the full graph, owner and tasks remain live");
            Check(!coordinator.IsCompleted && !host.ReleaseWrite && !host.Closed, "GC must not finish or close the live graph");
            host.ReleaseAll();
            var result = Await(coordinator);
            Check(result.IsOk && (string)result.OkValue == "done", "full graph finishes after live-GC gate release");
            owner.Close(bodyThrew: false);
            settled = true;
            host.@@CLOSE@@(NyUnit.Value);
            Check(Pending(owner) == 0, "owner closure empties its unresolved registry");
            Check(host.Active == 0 && tasks.Fold(true, (all, task) => all && task.Running.IsCompleted), "all three owned tasks settle before resource close");
            ExactDelivery(host, left, right);
            Closed(host, false); // This probe invokes launch/await directly, below finish.
            Collect();
            Check(Alive(weak).All(x => !x), "discarded heads collect after settlement while owner remains retained");
            GC.KeepAlive(rootedControl);
            GC.KeepAlive(tasks);
            GC.KeepAlive(owner);
            Console.WriteLine($"PASS: {label}; original-heads-collected-live; owner-tasks=3; pending-live={pending}; pending-closed=0; exact-delivery={left + right}");
        }
        finally
        {
            host.ReleaseAll();
            if (!settled) owner.Close(bodyThrew: true);
            GC.KeepAlive(tasks);
            GC.KeepAlive(owner);
        }
    }
    public static int Main()
    {
        ThreadPool.SetMinThreads(32, 32);
        // This watchdog covers only this newly created test executable.
        using var watchdog = new Timer(_ => { Console.Error.WriteLine("FAIL: runtime test process watchdog"); Environment.Exit(99); }, null, 180000, Timeout.Infinite);
        NyRuntime.Clock = null;
        NyRuntime.Cancellation.Value = CancellationToken.None;
        try
        {
            Functional("normal-small", 4, 4);
            Functional("normal-large", 256, 256);
            Functional("early-done", 1, 256);
            Functional("idle-input");
            Functional("slow-sink");
            Functional("stop-first", stop: 1);
            Functional("send-first", stop: 2);
            Functional("sink-error");
            Functional("sink-exception");
            Functional("source-error");
            Functional("parent-exit");
            Functional("parent-token-cancel");
            LiveGc("live-gc-normal-small", 4, 4, "w1", 2);
            LiveGc("live-gc-normal-large", 256, 256, "w1", 128);
            LiveGc("live-gc-early-done", 1, 256, "w2", 128);
            Console.WriteLine("PASS: bounded-worker-stream; unedited emitted graph/runtime; real threads and live full-graph GC");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
}
