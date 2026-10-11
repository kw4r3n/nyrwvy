using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Nyrwvy.Generated;

// Diagnostic-only host. No stream, admission, cursor, or retention state is
// implemented here. The checkpoint keeps graph workers live for GC observation.
sealed class ProbeHost : @@HOST@@, IDisposable
{
    public readonly CountdownEvent Arrived = new(2);
    public readonly ManualResetEventSlim Release = new(false);
    public int Active;
    public NyUnit @@CHECKPOINT@@(long worker, long count)
    {
        Interlocked.Increment(ref Active);
        Arrived.Signal();
        try { if (!Release.Wait(TimeSpan.FromSeconds(10), NyRuntime.Cancellation.Value)) throw new Exception("checkpoint watchdog"); }
        catch (OperationCanceledException) when (NyRuntime.Cancellation.Value.IsCancellationRequested) { }
        finally { Interlocked.Decrement(ref Active); }
        return NyUnit.Value;
    }
    public void Dispose() { Arrived.Dispose(); Release.Dispose(); }
}
static class CursorRetentionChecks
{
    static void Check(bool value,string label) { if (!value) throw new Exception(label); }
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static int Pending(NyTaskScope scope)
    {
        var value=typeof(NyTaskScope).GetField("_handoffs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(scope)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }
    // Separate non-inlined frames prevent this harness from keeping head locals
    // alive accidentally during either positive or negative GC controls.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference Start(ProbeHost host,NyTaskScope scope,long count,long worker)
    {
        var head=NyFunctions.@@NEWCURSOR@@(@@NEWCURSOR_SCOPE@@:scope);
        var weak=new WeakReference(head);
        _=NyFunctions.@@SPAWN@@(@@SPAWN_HOST@@:host,@@SPAWN_SCOPE@@:scope,@@SPAWN_CURSOR@@:head,@@SPAWN_COUNT@@:count,@@SPAWN_WORKER@@:worker);
        return weak;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Inspect(WeakReference weak)
    {
        if (weak.Target is not NyOneShot<@@LINK@@> head) return -1;
        return NyFunctions.@@RETAINEDLENGTH@@(@@RETAINEDLENGTH_CURSOR@@:head,@@RETAINEDLENGTH_COUNT@@:0L);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference UnrootedControl() => new WeakReference(new object());
    static void Run(long count)
    {
        using var host=new ProbeHost();
        var owner=new NyTaskScope(CancellationToken.None);
        var weak=new[]{Start(host,owner,count,1),Start(host,owner,count,2)};
        Check(host.Arrived.Wait(TimeSpan.FromSeconds(10)),"both advancing graph workers must reach checkpoint");
        Check(host.Active==2,"both workers remain live");
        Check(Pending(owner)==2,"only unresolved cursor tails are registered, completed cells are unregistered");
        var rootedControl=new object();
        var rootedWeak=new WeakReference(rootedControl);
        var unrootedWeak=UnrootedControl();
        Collect();
        Check(rootedWeak.IsAlive&&!unrootedWeak.IsAlive,"GC controls: retained object survives and discarded object collects");
        var live=weak.Select(Inspect).ToArray();
        Console.WriteLine($"OBSERVE: count={count}; live-root-lengths={string.Join(',',live)}; unresolved-registry={Pending(owner)}");
        Check(live.All(n=>n==-1),"discarded initial heads must collect while workers and owner remain open");
        Check(host.Active==2&&Pending(owner)==2,"collection must not settle workers or close their owner");
        host.Release.Set();
        owner.Close(bodyThrew:false);
        Check(host.Active==0&&Pending(owner)==0,"task settlement closes pending tails");
        Collect();
        var after=weak.Select(Inspect).ToArray();
        Check(after.All(n=>n==-1),"negative control: discarded initial heads must collect after task settlement");
        GC.KeepAlive(rootedControl);
        GC.KeepAlive(owner); // Even retaining the owner must not retain completed chains now.
        Console.WriteLine($"PASS: released live task captures, {count} completed cells per worker; roots collected while workers and owner open; settlement control passed");
    }
    public static void Main()
    {
        ThreadPool.SetMinThreads(16,16);
        using var watchdog=new Timer(_=>Environment.Exit(99),null,60000,Timeout.Infinite);
        NyRuntime.Clock=null;NyRuntime.Cancellation.Value=CancellationToken.None;
        Run(4);Run(256);
        Console.WriteLine("PASS: cursor-retention regression; unedited emitted graph/runtime");
    }
}
