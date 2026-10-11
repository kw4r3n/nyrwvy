using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nyrwvy.Generated;

// This host controls scheduling and observes append acknowledgements. It does not
// route events, own the mailbox, repair the log, or implement collector policy.
sealed class CollectorHost : @@HOST@@, IDisposable
{
    public readonly record struct Observation(string Id, string Kind, long Sequence, bool Accepted)
    {
        public string Key => $"{Id}:{Kind}:{Sequence}";
    }

    readonly string scenario;
    readonly int count;
    readonly Barrier joinBarrier;
    readonly CountdownEvent progressOne;
    readonly CountdownEvent exitReady;
    readonly ManualResetEventSlim secondWorkerPaused = new(false);
    readonly ManualResetEventSlim raceReleased = new(false);
    readonly ManualResetEventSlim stopAccepted = new(false);
    readonly ConcurrentQueue<Observation> observations = new();
    public int Opened, Closed, Saved, Ready, Joined, Active, MaxActive;
    public string Report = "";

    public CollectorHost(string scenario, int count)
    {
        this.scenario = scenario;
        this.count = count;
        joinBarrier = new Barrier(count);
        progressOne = new CountdownEvent(count);
        exitReady = new CountdownEvent(count);
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    void Enter()
    {
        Check(Volatile.Read(ref Closed) == 0, "host operation after release");
        int active = Interlocked.Increment(ref Active);
        int observed;
        while (active > (observed = Volatile.Read(ref MaxActive)) &&
               Interlocked.CompareExchange(ref MaxActive, active, observed) != observed) { }
    }

    static void Wait(ManualResetEventSlim gate, CancellationToken token, string label)
    {
        Check(gate.Wait(TimeSpan.FromSeconds(5), token), label + " timed out");
    }

    static void Wait(CountdownEvent gate, CancellationToken token, string label)
    {
        Check(gate.Wait(TimeSpan.FromSeconds(5), token), label + " timed out");
    }

    static void UntilCancellation(CancellationToken token, string label)
    {
        Check(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), label + " was not cancelled");
    }

    public Observation[] Snapshot() => observations.ToArray();

    public NyResult<NyUnit, NyStr> @@OPEN@@()
    {
        Check(Interlocked.Increment(ref Opened) == 1, "resource opened twice");
        return NyResult<NyUnit, NyStr>.Ok(NyUnit.Value);
    }

    public NyUnit @@READY@@()
    {
        Check(Volatile.Read(ref Joined) == 0, "worker started before collector ready");
        Check(Interlocked.Increment(ref Ready) == 1, "collector started twice");
        return NyUnit.Value;
    }

    public NyResult<NyUnit, NyStr> @@PAUSE@@(NyStr identifier, NyStr stage)
    {
        string id = identifier, phase = stage;
        CancellationToken token = NyRuntime.Cancellation.Value;
        Enter();
        try
        {
            if (id == "control")
            {
                if (phase == "cancel") Wait(secondWorkerPaused, token, "individual cancellation gate");
                else if (phase == "stop")
                {
                    Wait(progressOne, token, "all first progress events");
                    // w1 and the graph's Stop append now race. The remaining
                    // producers cannot try their next event until Stop wins.
                    raceReleased.Set();
                }
                else if (phase == "exit") Wait(exitReady, token, "scope exit gate");
                else throw new Exception("unexpected control phase " + phase);
                return NyResult<NyUnit, NyStr>.Ok(NyUnit.Value);
            }

            Check(Volatile.Read(ref Ready) == 1, "producer ran before collector readiness");
            Check(id.Length == 2 && id[0] == 'w' && id[1] >= '1' && id[1] <= '0' + count,
                "unexpected worker ID " + id);
            if (phase == "join")
            {
                Interlocked.Increment(ref Joined);
                Check(joinBarrier.SignalAndWait(TimeSpan.FromSeconds(5), token), "producer overlap barrier timed out");
            }
            else if (phase == "p2")
            {
                if (scenario == "worker-error" && id == "w2")
                    return NyResult<NyUnit, NyStr>.Err("worker failed");
                if (scenario == "host-exception" && id == "w2")
                    throw new InvalidOperationException("worker host failed");
                if (scenario == "cancel-one" && id == "w2")
                {
                    secondWorkerPaused.Set();
                    UntilCancellation(token, "individual worker");
                }
                else if (scenario == "parent-exit")
                {
                    // w1 announces readiness in Observe, after its append has
                    // already committed but before its acknowledgement is recorded.
                    if (id != "w1") exitReady.Signal();
                    UntilCancellation(token, "scope-owned worker");
                }
                else if (scenario == "stop-race")
                    Wait(id == "w1" ? raceReleased : stopAccepted, token, "stop race producer gate");
            }
            else if (phase == "final" && scenario == "stop-race")
                Wait(stopAccepted, token, "post-stop final gate");
            else Check(phase is "p1" or "final", "unexpected worker phase " + phase);

            return token.IsCancellationRequested
                ? NyResult<NyUnit, NyStr>.Err("cancelled")
                : NyResult<NyUnit, NyStr>.Ok(NyUnit.Value);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return NyResult<NyUnit, NyStr>.Err("cancelled");
        }
        finally { Interlocked.Decrement(ref Active); }
    }

    public NyUnit @@OBSERVE@@(NyStr identifier, NyStr eventKind, long sequence, bool accepted)
    {
        string id = identifier, kind = eventKind;
        Enter();
        try
        {
            if (scenario == "parent-exit" && id == "w1" && kind == "progress" && sequence == 1 && accepted)
            {
                exitReady.Signal();
                UntilCancellation(NyRuntime.Cancellation.Value, "committed event acknowledgement");
            }
            // Record even when cancellation arrived after Complete succeeded.
            observations.Enqueue(new Observation(id, kind, sequence, accepted));
            if (accepted && kind == "progress" && sequence == 1) progressOne.Signal();
            if (id == "control" && kind == "stop" && accepted) stopAccepted.Set();
            return NyUnit.Value;
        }
        finally { Interlocked.Decrement(ref Active); }
    }

    public NyUnit @@SAVE@@(NyStr report)
    {
        Check(!NyRuntime.Cancellation.Value.IsCancellationRequested, "save after parent cancellation");
        Check(Volatile.Read(ref Active) == 0, "save before producer settlement");
        Check(Interlocked.Increment(ref Saved) == 1, "report saved twice");
        Report = report;
        return NyUnit.Value;
    }

    public NyUnit @@CLOSE@@(NyUnit handle)
    {
        Check(Volatile.Read(ref Active) == 0, "resource released before host operations settled");
        Check(Interlocked.Increment(ref Closed) == 1, "resource closed twice");
        return NyUnit.Value;
    }

    public void Dispose()
    {
        joinBarrier.Dispose();
        progressOne.Dispose();
        exitReady.Dispose();
        secondWorkerPaused.Dispose();
        raceReleased.Dispose();
        stopAccepted.Dispose();
    }
}

static class CollectorRuntimeChecks
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
    }

    static string[] Parse(string report) => report.Split(';', StringSplitOptions.RemoveEmptyEntries);
    static string[] Sorted(IEnumerable<string> values) => values.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    static string[] Full(string id) => new[] { $"{id}:register:0", $"{id}:progress:1", $"{id}:progress:2", $"{id}:final:3" };

    static void RunCase(string scenario, int count)
    {
        using var host = new CollectorHost(scenario, count);
        string mode = scenario is "cancel-one" or "stop-race" or "parent-exit" ? scenario : "normal";
        NyResult<NyStr, NyStr> result = default!;
        bool threw = false;
        try
        {
            result = NyFunctions.@@RUN@@(@@RUN_HOST@@: host, @@RUN_COUNT@@: (long)count, @@RUN_MODE@@: mode);
        }
        catch (InvalidOperationException error) when (scenario == "host-exception" && error.Message == "worker host failed")
        {
            threw = true;
        }
        Check(host.Opened == 1 && host.Closed == 1 && host.Active == 0, scenario + " scope settlement and exactly-once release");
        Check(host.Ready == 1 && host.Joined == count && host.MaxActive >= count,
            scenario + " collector readiness and real overlapping dynamic producers");
        if (scenario == "host-exception")
        {
            Check(threw && host.Saved == 0, "host exception propagates and does not save");
            Console.WriteLine("PASS: host-exception; scope fault propagated, all host operations settled, close once");
            return;
        }

        string report;
        if (scenario == "parent-exit")
        {
            string error = result.IsOk ? "" : (string)result.ErrorValue;
            Check(!result.IsOk && error.StartsWith("cancelled|", StringComparison.Ordinal) && host.Saved == 0,
                "early parent exit returns cancellation with accepted prefix and does not save");
            report = error["cancelled|".Length..];
        }
        else
        {
            Check(result.IsOk, scenario + " should return an aggregate success");
            report = result.OkValue;
            Check(host.Saved == 1 && host.Report == report, scenario + " saves exact report once");
        }

        var observed = host.Snapshot();
        var data = observed.Where(x => x.Kind != "stop").ToArray();
        var accepted = data.Where(x => x.Accepted).Select(x => x.Key).ToArray();
        var events = Parse(report);
        var workerIds = Enumerable.Range(1, count).Select(i => "w" + i).ToHashSet(StringComparer.Ordinal);
        Check(data.All(x => workerIds.Contains(x.Id)), scenario + " only scheduled worker IDs appear");
        // Observation calls run after CAS and can themselves be reordered. Only
        // the immutable mailbox determines total order; compare acceptance as a multiset.
        Check(Sorted(events).SequenceEqual(Sorted(accepted)), scenario + " exact accepted-event multiset (no loss or invention)");
        Check(events.Distinct(StringComparer.Ordinal).Count() == events.Length, scenario + " no duplicate event");
        Check(data.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() == data.Length,
            scenario + " exactly one acknowledgement per attempted logical event");
        Check(events.All(x => x.StartsWith("w", StringComparison.Ordinal)), scenario + " Stop absent from data report");

        for (int i = 1; i <= count; i++)
        {
            string id = "w" + i;
            var own = events.Where(x => x.StartsWith(id + ":", StringComparison.Ordinal)).ToArray();
            string[] expected;
            if (scenario == "worker-error" && i == 2)
                expected = new[] { id + ":register:0", id + ":progress:1", id + ":error:2" };
            else if (scenario == "parent-exit" || (scenario == "cancel-one" && i == 2))
                expected = Full(id)[..2];
            else if (scenario == "stop-race")
            {
                Check(own.Length is 2 or 3, "stop leaves a legal accepted worker prefix");
                Check(i == 1 || own.Length == 2, "gated producer cannot append after Stop");
                expected = Full(id)[..own.Length];
            }
            else expected = Full(id);
            Check(own.SequenceEqual(expected), scenario + " ordered registered worker lifecycle for " + id);
        }

        var stops = observed.Where(x => x.Kind == "stop").ToArray();
        Check(stops.All(x => x.Id == "control" && x.Sequence == 0), "well-formed Stop acknowledgements");
        Check(stops.Count(x => x.Accepted) == (scenario == "parent-exit" ? 0 : 1), scenario + " one accepted Stop unless owner exits");
        if (scenario == "stop-race")
            Check(data.Count(x => !x.Accepted) == count, "each live producer gets one post-Stop rejection");
        if (scenario is "normal" or "worker-error")
            Check(data.All(x => x.Accepted), scenario + " no unexplained rejected event");
        Console.WriteLine($"PASS: {scenario}, {count} producers; {events.Length} accepted events, exact log, ordered lifecycles, close once");
    }

    public static void Main()
    {
        ThreadPool.SetMinThreads(64, 64);
        using var watchdog = new Timer(_ => Environment.Exit(99), null, 60000, Timeout.Infinite);
        NyRuntime.Clock = null;
        NyRuntime.Cancellation.Value = CancellationToken.None;
        foreach (int count in new[] { 2, 3, 4 }) RunCase("normal", count);
        RunCase("worker-error", 4);
        RunCase("cancel-one", 4);
        // Repeated real races, with deterministic post-Stop rejection in every run.
        for (int i = 0; i < 20; i++) RunCase("stop-race", 4);
        RunCase("parent-exit", 4);
        RunCase("host-exception", 4);
        Console.WriteLine("PASS: emitted graph/runtime collector, dynamic 2–4 producers, failure isolation, individual cancellation, Stop races and post-settlement cancellation reconciliation.");
    }
}
