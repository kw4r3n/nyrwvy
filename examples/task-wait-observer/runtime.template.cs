using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Nyrwvy.Generated;
using Outcome = Nyrwvy.Generated.NyResult<long, bool>;
using States = Nyrwvy.Generated.NyList<Nyrwvy.Generated.NyOption<Nyrwvy.Generated.NyResult<long, bool>>>;
using Observation = Nyrwvy.Generated.NyResult<Nyrwvy.Generated.NyList<Nyrwvy.Generated.NyOption<Nyrwvy.Generated.NyResult<long, bool>>>, bool>;

// Synthetic effects and gates. Graph code owns the scenario's scope, tasks and observation.
sealed class Effects : @@HOST@@
{
    readonly string id;
    readonly CountdownEvent entered = new(2);
    readonly ManualResetEventSlim releaseA = new(false), releaseB = new(false);
    readonly ConcurrentDictionary<string, CancellationToken> tokens = new();
    public readonly ConcurrentQueue<string> Reports = new();
    public int Active, Closed;
    public Effects(string id) { this.id = id; }
    public NyResult<NyStr, NyStr> work(NyStr raw)
    {
        var name = raw.ToString();
        var token = NyRuntime.Cancellation.Value;
        tokens[name] = token;
        Interlocked.Increment(ref Active);
        try
        {
            entered.Signal();
            if (id.StartsWith("settled")) (name == "a" ? releaseA : releaseB).Set();
            (name == "a" ? releaseA : releaseB).Wait(token);
            if (id.StartsWith("fault-") && name == "a") throw new InvalidOperationException("worker fault");
            return NyResult<NyStr, NyStr>.Ok(raw);
        }
        catch (OperationCanceledException e) when (token.IsCancellationRequested && e.CancellationToken == token)
        { return NyResult<NyStr, NyStr>.Ok("stopped"); }
        finally { Interlocked.Decrement(ref Active); }
    }
    public NyUnit gate(NyStr label)
    {
        if (!id.StartsWith("empty")) Program.Check(entered.Wait(TimeSpan.FromSeconds(5)), "worker entry");
        if (id == "any" || id.StartsWith("fault-")) releaseA.Set();
        if (id == "all" || id == "fault-all") { releaseA.Set(); releaseB.Set(); }
        return NyUnit.Value;
    }
    public NyUnit report(NyStr value)
    {
        Program.Check(tokens.Values.All(t => !t.IsCancellationRequested), "observer cancelled a worker");
        Reports.Enqueue(value.ToString());
        return NyUnit.Value;
    }
    public NyResult<NyUnit, NyStr> open() => NyResult<NyUnit, NyStr>.Ok(NyUnit.Value);
    public NyUnit close(NyUnit handle)
    {
        Program.Check(Volatile.Read(ref Active) == 0, "resource released before worker drainage");
        Interlocked.Increment(ref Closed);
        return NyUnit.Value;
    }
    @@METHODS@@
}

static class Program
{
    static readonly NyOption<long> Unbounded = NyOption<long>.None();
    static readonly NyOption<long> Poll = NyOption<long>.Some(0);
    public static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Gate(ManualResetEventSlim gate) => Check(gate.Wait(TimeSpan.FromSeconds(5)), "gate timed out");
    static T Join<T>(Task<T> task) { Check(task.Wait(TimeSpan.FromSeconds(5)), "join timed out"); return task.GetAwaiter().GetResult(); }
    static Observation Observe(NyOneShot<States> cell) => NyFunctions.@@AWAITTASKOBSERVATION@@(cell, Unbounded, true, NyUnit.Value);
    static Observation Monitor(NyList<NyTask<Outcome>> tasks, NyOneShot<States> cell, bool any) => any
        ? NyFunctions.@@OBSERVETASKS_ANY@@(tasks, cell, Unbounded, NyUnit.Value)
        : NyFunctions.@@OBSERVETASKS_ALL@@(tasks, cell, Unbounded, NyUnit.Value);
    static void Row(string id) => Console.WriteLine(JsonSerializer.Serialize(new { id, passed = true }));

    static void DirectWaitControl()
    {
        var scope = new NyTaskScope(default);
        using var entered = new ManualResetEventSlim();
        using var waiterEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        CancellationToken memberToken = default;
        var member = scope.Start(() => { memberToken = NyRuntime.Cancellation.Value; entered.Set(); Gate(release); return Outcome.Ok(7); });
        Gate(entered);
        var waiter = scope.Start(() => { waiterEntered.Set(); return NyTasks.Wait(NyList<NyTask<Outcome>>.Create(member), false, Unbounded); });
        Gate(waiterEntered);
        waiter.Cancel();
        Check(!waiter.Running.IsCompleted && !memberToken.IsCancellationRequested, "direct wait contract changed");
        release.Set();
        Check(Join(waiter.Running).ItemAt(0).Value.Value.OkValue == 7, "direct wait lost completion");
        scope.Close(false);
        Row("direct-wait-cancel-control");
    }

    static void PublicationPrecedence(bool cancelBeforeCheck)
    {
        using var callerToken = new CancellationTokenSource();
        var scope = new NyTaskScope(default);
        var cell = scope.CreateOneShot<States>();
        var packet = States.Create(NyOption<Outcome>.Some(Outcome.Ok(9)));
        Check(cell.Complete(packet), "publication refused");
        NyRuntime.Cancellation.Value = callerToken.Token;
        if (cancelBeforeCheck) callerToken.Cancel();
        var observed = Observe(cell);
        if (!cancelBeforeCheck) callerToken.Cancel();
        Check(observed.IsOk == !cancelBeforeCheck, "explicit final cancellation check precedence");
        if (observed.IsOk) Check(observed.OkValue.ItemAt(0).Value.Value.OkValue == 9, "packet lost");
        // The primitive itself still returns Some: only the graph observer chooses cancellation.
        Check(cell.Wait(Unbounded).HasValue, "OneShot completed-value precedence changed");
        NyRuntime.Cancellation.Value = default;
        scope.Close(false);
        Row(cancelBeforeCheck ? "published-cancel-before-check" : "completed-before-cancel");
    }

    static void EmptyAndEscaped(bool any, bool empty)
    {
        var scope = new NyTaskScope(default);
        var tasks = NyList<NyTask<Outcome>>.Empty();
        if (!empty)
        {
            var first = scope.Start(() => Outcome.Ok(11));
            var second = scope.Start(() => Outcome.Err(false));
            Join(first.Running); Join(second.Running);
            tasks = NyList<NyTask<Outcome>>.Create(second, first);
        }
        var cell = scope.CreateOneShot<States>();
        if (!empty) scope.Close(false); // escaped handles are already settled
        using var callerToken = new CancellationTokenSource();
        callerToken.Cancel();
        NyRuntime.Cancellation.Value = callerToken.Token;
        var packet = Monitor(tasks, cell, any);
        Check(packet.IsOk && packet.OkValue.Length == (empty ? 0 : 2), "monitor ignores its caller cancellation");
        if (empty) Check(!Observe(cell).IsOk, "cancelled caller accepted empty packet");
        else
        {
            Check(!packet.OkValue.ItemAt(0).Value.Value.IsOk, "ordered Err missing");
            Check(packet.OkValue.ItemAt(1).Value.Value.OkValue == 11, "ordered Ok missing");
            Check(!cell.Wait(Poll).HasValue, "closed owner accepted publication");
        }
        NyRuntime.Cancellation.Value = default;
        if (empty) scope.Close(false);
        Row((empty ? "cancelled-empty-" : "escaped-settled-") + (any ? "any" : "all"));
    }

    static void FaultAfterCallerCancel()
    {
        var scope = new NyTaskScope(default);
        using var entered = new ManualResetEventSlim();
        using var releaseFault = new ManualResetEventSlim();
        var member = scope.Start<Outcome>(() => { entered.Set(); Gate(releaseFault); throw new InvalidOperationException("late fault"); });
        Gate(entered);
        var cell = scope.CreateOneShot<States>();
        var monitor = scope.Start(() => Monitor(NyList<NyTask<Outcome>>.Create(member), cell, false));
        var caller = scope.Start(() => Observe(cell));
        caller.Cancel();
        Check(!Join(caller.Running).IsOk, "caller cancellation did not settle");
        Check(!member.Running.IsCompleted && !monitor.Running.IsCompleted, "caller owned monitor/member lifetime");
        releaseFault.Set();
        try { Check(monitor.Running.Wait(TimeSpan.FromSeconds(5)), "monitor failed to settle"); } catch (AggregateException) { }
        try { NyFunctions.@@SUPERVISETASKOBSERVATION@@(monitor, caller); throw new Exception("fault hidden"); }
        catch (InvalidOperationException e) { Check(e.Message == "late fault", "wrong supervision fault"); }
        try { scope.Close(false); throw new Exception("scope fault hidden"); }
        catch (InvalidOperationException e) { Check(e.Message == "late fault", "wrong scope fault"); }
        Check(!cell.Wait(Poll).HasValue, "throwing monitor published data");
        Row("fault-after-caller-cancel");
    }

    static void FaultStartOrder()
    {
        var scope = new NyTaskScope(default);
        var first = scope.Start<Outcome>(() => throw new InvalidOperationException("first"));
        var second = scope.Start<Outcome>(() => throw new InvalidOperationException("second"));
        // WaitAll includes both faults; observation chooses input order. Scope close chooses start order.
        try { Task.WaitAll(first.Running, second.Running); } catch (AggregateException) { }
        var cell = scope.CreateOneShot<States>();
        try { Monitor(NyList<NyTask<Outcome>>.Create(second, first), cell, false); throw new Exception("monitor swallowed fault"); }
        catch (InvalidOperationException e) { Check(e.Message == "second", "snapshot fault order"); }
        try { scope.Close(false); throw new Exception("scope swallowed fault"); }
        catch (InvalidOperationException e) { Check(e.Message == "first", "scope fault start order"); }
        Row("fault-snapshot-and-scope-order");
    }

    static void FaultPollPrecedence()
    {
        var scope = new NyTaskScope(default);
        var fault = scope.Start<Outcome>(() => throw new InvalidOperationException("polled fault"));
        try { Check(fault.Running.Wait(TimeSpan.FromSeconds(5)), "fault failed to settle"); } catch (AggregateException) { }
        using var peerEntered = new ManualResetEventSlim();
        var peer = scope.Start(() => { peerEntered.Set(); NyRuntime.WaitForCancellation(); return Outcome.Ok(17); });
        Gate(peerEntered);
        var cell = scope.CreateOneShot<States>();
        using var callerToken = new CancellationTokenSource();
        callerToken.Cancel(); NyRuntime.Cancellation.Value = callerToken.Token;
        var tasks = NyList<NyTask<Outcome>>.Create(peer, fault);
        foreach (var any in new[] { true, false })
        {
            try
            {
                if (any) NyFunctions.@@OBSERVETASKS_ANY@@(tasks, cell, Poll, NyUnit.Value);
                else NyFunctions.@@OBSERVETASKS_ALL@@(tasks, cell, Poll, NyUnit.Value);
                throw new Exception("cancel/timeout masked settled fault");
            }
            catch (InvalidOperationException e) { Check(e.Message == "polled fault", "wrong polled fault"); }
        }
        Check(!peer.Running.IsCompleted, "fault observation cancelled a peer");
        NyRuntime.Cancellation.Value = default;
        try { scope.Close(false); throw new Exception("scope lost polled fault"); }
        catch (InvalidOperationException e) { Check(e.Message == "polled fault", "wrong scope fault"); }
        Row("fault-beats-cancel-and-zero-limit");
    }

    static void ParentDrain()
    {
        using var parent = new CancellationTokenSource();
        var scope = new NyTaskScope(parent.Token);
        using var entered = new ManualResetEventSlim();
        using var cleanupEntered = new ManualResetEventSlim();
        using var cleanupRelease = new ManualResetEventSlim();
        var member = scope.Start(() => { entered.Set(); NyRuntime.WaitForCancellation(); cleanupEntered.Set(); Gate(cleanupRelease); return Outcome.Ok(13); });
        Gate(entered);
        var cell = scope.CreateOneShot<States>();
        var monitor = scope.Start(() => Monitor(NyList<NyTask<Outcome>>.Create(member), cell, false));
        var caller = scope.Start(() => Observe(cell));
        parent.Cancel(); Gate(cleanupEntered);
        Check(!Join(caller.Running).IsOk, "parent did not wake caller");
        var closing = Task.Run(() => scope.Close(false));
        Check(!closing.IsCompleted && !member.Running.IsCompleted && !monitor.Running.IsCompleted, "join bypassed cleanup");
        cleanupRelease.Set();
        Check(closing.Wait(TimeSpan.FromSeconds(5)), "scope failed to drain");
        Check(member.Running.IsCompleted && monitor.Running.IsCompleted, "tasks survived scope");
        Check(!cell.Wait(Poll).HasValue, "closed parent accepted packet");
        Row("parent-cancel-drain");
    }

    public static void Main(string[] args)
    {
        ThreadPool.SetMinThreads(16, 16);
        using var data = JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));
        foreach (var c in data.RootElement.EnumerateArray())
        {
            string id = c.GetProperty("id").GetString()!;
            var effects = new Effects(id);
            bool fault = false;
            try
            {
                var limit = c.TryGetProperty("limit", out var n) ? NyOption<long>.Some(n.GetInt64()) : Unbounded;
                var policy = c.TryGetProperty("policy", out var q) ? q.GetString()! : "observe";
                NyFunctions.@@RUN@@(effects, (NyStr)c.GetProperty("mode").GetString()!, limit, (NyStr)policy);
            }
            catch (InvalidOperationException e) { Check(e.Message == "worker fault", "wrong fault"); fault = true; }
            Console.WriteLine(JsonSerializer.Serialize(new { id, fault, closed = effects.Closed, reports = effects.Reports.ToArray() }));
        }
        DirectWaitControl();
        PublicationPrecedence(true); PublicationPrecedence(false);
        EmptyAndEscaped(true, true); EmptyAndEscaped(false, true);
        EmptyAndEscaped(true, false); EmptyAndEscaped(false, false);
        FaultAfterCallerCancel(); FaultStartOrder(); FaultPollPrecedence(); ParentDrain();
    }
}
