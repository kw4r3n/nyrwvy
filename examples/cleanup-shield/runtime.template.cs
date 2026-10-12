using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;
using Nyrwvy.Generated;

sealed class Probe : @@HOST@@
{
    public JsonElement Case;
    public List<string> Steps = new(), Reports = new();
    public readonly ManualResetEventSlim Entered = new(), Released = new();
    public bool Throw;
    public bool MaskObserved;
    public bool Owned, OwnedFault;
    public int Active, Closed;
    public readonly ManualResetEventSlim WorkerEntered = new(), ExitBody = new(), CleanupEntered = new(), CleanupRelease = new();
    public CancellationToken WorkerToken;
    public Probe(JsonElement c) { Case = c; }
    string? Option(string name) => Case.TryGetProperty(name,out var value) ? value.GetString() : null;
    public NyResult<NyStr,NyStr> step(NyStr phase)
    {
        var name = phase.ToString();
        Steps.Add(name);
        MaskObserved = !NyRuntime.CancellationRequested;
        if(Owned && name == "owned")
        {
            WorkerToken = NyRuntime.Cancellation.Value;
            Interlocked.Increment(ref Active); WorkerEntered.Set();
            NyRuntime.WaitForCancellation(); CleanupEntered.Set();
            if(!CleanupRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("cleanup gate");
            Interlocked.Decrement(ref Active);
            if(OwnedFault) throw new InvalidOperationException("owned fault");
            return NyResult<NyStr,NyStr>.Err("stopped");
        }
        if (Throw) throw new InvalidOperationException("raw fault");
        if (name == Option("cancel")) { Entered.Set(); if(!Released.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("effect gate"); }
        return name == Option("fail") || name == Option("rollbackFail") ? NyResult<NyStr,NyStr>.Err(name) :
            NyResult<NyStr,NyStr>.Ok(name == "head" ? Case.GetProperty("patch").GetBoolean() ? "integrated-head" : "source-head" : "");
    }
    public NyUnit report(NyStr message) { lock(Reports) Reports.Add(message.ToString()); return NyUnit.Value; }
    public bool gate()
    {
        if(Owned) { if(!ExitBody.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("body gate"); return false; }
        if(Option("cancel") is null) return false;
        if(!Entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("coordinator gate");
        return true;
    }
    public NyUnit release() { Released.Set(); return NyUnit.Value; }
    public NyResult<NyUnit,NyStr> open() => NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);
    public NyUnit close(NyUnit handle)
    {
        if(Active != 0 || NyRuntime.CancellationRequested) throw new Exception("unshielded or premature resource release");
        Interlocked.Increment(ref Closed); return NyUnit.Value;
    }
    @@METHODS@@
}

static class Program
{
    static void Main(string[] args)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
        foreach(var c in document.RootElement.EnumerateArray())
        foreach(var protect in new[]{true,false})
        {
            var host = new Probe(c);
            var result = NyFunctions.@@RUN@@(host,c.GetProperty("patch").GetBoolean(),protect);
            if(!result.IsOk) throw new Exception("outer error");
            Console.WriteLine(JsonSerializer.Serialize(new {id=c.GetProperty("id").GetString(),protect,steps=host.Steps,reports=host.Reports}));
        }
        if(args.Length > 1) return; // Pinned pre-feature compiler uses only the unshielded paths.
        using var rawCase = JsonDocument.Parse("{\"patch\":true}");
        foreach(var throws in new[]{false,true})
        {
            using var source = new CancellationTokenSource(); source.Cancel();
            NyRuntime.Cancellation.Value = source.Token;
            var host = new Probe(rawCase.RootElement) {Throw=throws};
            Exception? fault = null;
            try { NyFunctions.@@PARENT@@(host,true,true,NyUnit.Value); } catch(Exception error) { fault=error; }
            if(NyRuntime.Cancellation.Value != source.Token || !host.MaskObserved || (throws && fault?.Message != "raw fault") || (!throws && fault is not null)) throw new Exception("restoration contract");
            NyRuntime.Cancellation.Value = CancellationToken.None;
            Console.WriteLine(JsonSerializer.Serialize(new {control=throws?"raw-fault-restores":"already-cancelled-restores",passed=true}));
        }
        using(var source = new CancellationTokenSource())
        {
            source.Cancel(); NyRuntime.Cancellation.Value = source.Token;
            if(NyFunctions.@@MASKPROBE@@().OkValue || NyFunctions.@@CONTEXTSHIELD@@().OkValue.ToString() != "cancelled/clear") throw new Exception("mask or context contract");
            var packet = NyResult<long,bool>.Err(true);
            var answer = NyFunctions.@@GENERICSHIELD@@(packet);
            if(answer.IsOk || !answer.ErrorValue || NyRuntime.Cancellation.Value != source.Token) throw new Exception("generic Err contract");
            NyRuntime.Cancellation.Value = CancellationToken.None;
            Console.WriteLine(JsonSerializer.Serialize(new {control="context-before-mask-generic-err",passed=true}));
        }
        foreach(var fault in new[]{false,true})
        {
            using var source = new CancellationTokenSource();
            var host = new Probe(rawCase.RootElement) {Owned=true,OwnedFault=fault};
            var parent = Task.Run(() => {
                NyRuntime.Cancellation.Value = source.Token;
                try { NyFunctions.@@OWNEDSHIELD@@(host); return "ok"; }
                catch(Exception error) { return error.Message; }
                finally { if(NyRuntime.Cancellation.Value != source.Token) throw new Exception("owned caller restoration"); }
            });
            if(!host.WorkerEntered.Wait(TimeSpan.FromSeconds(10))) throw new Exception("owned entry");
            source.Cancel(); source.Cancel();
            if(host.WorkerToken.IsCancellationRequested || parent.IsCompleted || host.Closed != 0) throw new Exception("parent reached new shield-owned scope");
            host.ExitBody.Set();
            if(!host.CleanupEntered.Wait(TimeSpan.FromSeconds(10))) throw new Exception("cleanup entry");
            if(!host.WorkerToken.IsCancellationRequested || parent.IsCompleted || host.Closed != 0) throw new Exception("scope failed to hold drainage");
            host.CleanupRelease.Set();
            if(!parent.Wait(TimeSpan.FromSeconds(10)) || parent.Result != (fault?"owned fault":"ok") || host.Closed != 1 || host.Active != 0) throw new Exception("owned finish");
            Console.WriteLine(JsonSerializer.Serialize(new {control=fault?"owned-fault-drain-release":"owned-cancel-drain-release",passed=true}));
        }
        using(var source = new CancellationTokenSource())
        {
            var scope = new NyTaskScope(source.Token);
            var host = new Probe(rawCase.RootElement) {Owned=true};
            var parent = Task.Run(() => { NyRuntime.Cancellation.Value = source.Token; return NyFunctions.@@EXISTINGSHIELD@@(host,scope); });
            if(!host.WorkerEntered.Wait(TimeSpan.FromSeconds(10))) throw new Exception("existing entry");
            source.Cancel();
            if(!host.CleanupEntered.Wait(TimeSpan.FromSeconds(10)) || parent.IsCompleted) throw new Exception("existing scope token was detached");
            host.CleanupRelease.Set(); host.ExitBody.Set();
            if(!parent.Wait(TimeSpan.FromSeconds(10))) throw new Exception("existing parent finish");
            scope.Close(false);
            Console.WriteLine(JsonSerializer.Serialize(new {control="existing-scope-retains-parent-token",passed=true}));
        }
    }
}
