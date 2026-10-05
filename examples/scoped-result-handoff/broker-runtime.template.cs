
sealed class BrokerWire : @@WIRE@@
{
    readonly System.Collections.Generic.Queue<(string Id, string Payload)> responses = new();
    readonly string mode;
    readonly object gate = new();
    public readonly System.Threading.ManualResetEventSlim ReadEntered = new(false);
    public int Opened, Closed, Saved, Notes, Reading;
    public string Report = "";
    public BrokerWire(string mode)
    {
        this.mode = mode;
        if (mode is "reverse" or "duplicate" or "one-timeout") responses.Enqueue(("b", "B"));
        if (mode == "duplicate") responses.Enqueue(("b", "replacement"));
        if (mode is "reverse" or "duplicate") responses.Enqueue(("a", "A"));
    }
    public NyResult<NyUnit, NyStr> @@OPEN@@() { System.Threading.Interlocked.Increment(ref Opened); return NyResult<NyUnit, NyStr>.Ok(NyUnit.Value); }
    public NyUnit @@SEND@@(NyStr request) => NyUnit.Value;
    public NyResult<@@RESPONSE@@, NyStr> @@READ@@()
    {
        System.Threading.Interlocked.Increment(ref Reading);
        try
        {
            ReadEntered.Set();
            if (mode == "receiver-host-exception") throw new InvalidOperationException("receiver failed");
            if (mode == "receiver-expected-error") return NyResult<@@RESPONSE@@, NyStr>.Err("receive failed");
            lock (gate)
            {
                if (responses.Count > 0)
                {
                    var value = responses.Dequeue();
                    return NyResult<@@RESPONSE@@, NyStr>.Ok(new @@RESPONSE@@(@@RESPONSE_ID@@: value.Id, @@RESPONSE_PAYLOAD@@: value.Payload));
                }
            }
            NyRuntime.WaitForCancellation();
            return NyResult<@@RESPONSE@@, NyStr>.Err("cancelled");
        }
        finally { System.Threading.Interlocked.Decrement(ref Reading); }
    }
    public NyUnit @@NOTE@@(NyStr message) { System.Threading.Interlocked.Increment(ref Notes); return NyUnit.Value; }
    public NyUnit @@SAVE@@(NyStr report)
    {
        if (NyRuntime.Cancellation.Value.IsCancellationRequested) throw new Exception("report saved after cancellation");
        Report = report;
        System.Threading.Interlocked.Increment(ref Saved);
        return NyUnit.Value;
    }
    public NyUnit @@CLOSE@@(NyUnit handle)
    {
        if (System.Threading.Volatile.Read(ref Reading) != 0) throw new Exception("release before receiver settled");
        System.Threading.Interlocked.Increment(ref Closed);
        return NyUnit.Value;
    }
}
sealed class BrokerControl : @@CONTROL@@
{
    readonly BrokerWire wire;
    public BrokerControl(BrokerWire wire) => this.wire = wire;
    public NyUnit @@PAUSE@@()
    {
        if (!wire.ReadEntered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("receiver did not enter transport");
        return NyUnit.Value;
    }
}
static class BrokerChecks
{
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    public static void Run()
    {
        NyRuntime.Clock = null;
        NyRuntime.Cancellation.Value = System.Threading.CancellationToken.None;
        foreach (string mode in new[] { "reverse", "duplicate", "one-timeout", "receiver-expected-error", "receiver-host-exception", "parent-cancel" })
        {
            var wire = new BrokerWire(mode);
            NyResult<NyStr, NyStr> result = default!;
            bool threw = false;
            try
            {
                result = mode == "parent-cancel"
                    ? NyFunctions.@@RUN_CANCELLED@@(@@CANCEL_WIRE@@: wire, @@CANCEL_CONTROL@@: new BrokerControl(wire))
                    : NyFunctions.@@RUN_BROKER@@(@@BROKER_WIRE@@: wire, @@LIMIT_A@@: mode == "one-timeout" ? 100L : 5000L, @@LIMIT_B@@: 5000L);
            }
            catch (InvalidOperationException error) when (mode == "receiver-host-exception" && error.Message == "receiver failed") { threw = true; }
            Check(wire.Opened == 1 && wire.Closed == 1 && wire.Reading == 0, mode + " scope settlement/release");
            if (mode == "receiver-host-exception") Check(threw && wire.Saved == 0, "receiver fault must not save");
            else if (mode == "parent-cancel") Check(!result.IsOk && result.ErrorValue == "cancelled" && wire.Saved == 0, "parent cancellation must not become timeout/report");
            else
            {
                string expected = mode == "one-timeout" ? "a=timeout;b=B" : mode == "receiver-expected-error" ? "a=receive failed;b=receive failed" : "a=A;b=B";
                Check(result.IsOk && result.OkValue == expected && wire.Report == expected && wire.Saved == 1, mode + " report");
                if (mode == "duplicate") Check(wire.Notes == 1, "duplicate refused by graph");
            }
            Console.WriteLine("PASS: real broker " + mode + "; same outcome and settlement policy as virtual fixtures");
        }
        var owner = new NyTaskScope(System.Threading.CancellationToken.None);
        var completed = owner.CreateOneShot<NyResult<NyStr, NyStr>>();
        Check(completed.Complete(NyResult<NyStr, NyStr>.Ok("A")), "published value before cancellation");
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        NyRuntime.Cancellation.Value = cancelled.Token;
        Check(completed.Wait(NyOption<long>.Some(0L)).HasValue, "completed value remains observable under cancellation");
        var request = NyFunctions.@@REQUEST_WORKER@@(@@REQUEST_CELL@@: completed, @@REQUEST_ID@@: "a", @@REQUEST_LIMIT@@: 1L);
        Check(!request.IsOk && request.ErrorValue == "cancelled", "actual generated request worker checks cancellation even after Some");
        NyRuntime.Cancellation.Value = System.Threading.CancellationToken.None;
        owner.Close(false);
        Console.WriteLine("PASS: completed value does not swallow cancellation in the actual request-worker graph");
    }
}
