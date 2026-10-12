static class Program
{
 static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
 static void Pass(string control)=>Console.WriteLine(JsonSerializer.Serialize(new {control,passed=true}));
 static void Main()
 {
  using var rawCase=JsonDocument.Parse("{\"patch\":true}");
  foreach(var cancelled in new[]{false,true})foreach(var oce in new[]{false,true})
  {
   using var source=new CancellationTokenSource();if(cancelled)source.Cancel();
   NyRuntime.Cancellation.Value=source.Token;
   Exception original=oce?new OperationCanceledException("raw OCE",source.Token):new InvalidOperationException("raw fault");
   var host=new Probe(rawCase.RootElement){InjectedFault=original};
   Exception? observed=null;
   try{NyFunctions.@@FAULTNESTED@@(host,NyUnit.Value);}catch(Exception error){observed=error;}
   Check(ReferenceEquals(original,observed),"rethrow must preserve original exception object including OCE");
   Check(observed!.StackTrace!.Contains("Probe.step"),"rethrow must retain throwing host stack");
   Check(NyRuntime.Cancellation.Value==source.Token&&host.MaskObserved,"nested shield restoration after rethrow");
   Check(host.Reports.SequenceEqual(new[]{"caught"}),"explicit handler once");
   var answer=NyFunctions.@@FAULTOUTER@@(host);
   Check(!answer.IsOk&&answer.ErrorValue.ToString()==original.Message,"outer explicit recovery preserves message");
   Check(host.Reports[^1]==(cancelled?"restored-cancelled":"restored-clear"),"outer handler sees restored caller token");
   NyRuntime.Cancellation.Value=CancellationToken.None;
   Pass($"identity-stack-{(oce?"OCE":"raw")}-{(cancelled?"cancelled":"clear")}");
  }
  foreach(var cancelled in new[]{false,true})
  {
   using var source=new CancellationTokenSource();if(cancelled)source.Cancel();NyRuntime.Cancellation.Value=source.Token;
   var host=new Probe(rawCase.RootElement);var result=NyFunctions.@@ABORTCATCH@@(host);
   Check(!result.IsOk&&result.ErrorValue.ToString()=="typed abort"&&host.Reports.Count==0&&host.Steps.Count==0,"typed abort bypasses handler");
   Check(NyRuntime.Cancellation.Value==source.Token,"abort token restoration");NyRuntime.Cancellation.Value=CancellationToken.None;
   Pass(cancelled?"typed-abort-pending-cancel":"typed-abort");
  }
  foreach(var releaseFault in new[]{false,true})
  {
   using var source=new CancellationTokenSource();source.Cancel();NyRuntime.Cancellation.Value=source.Token;
   var original=new InvalidOperationException("resource body fault");
   var host=new Probe(rawCase.RootElement){InjectedFault=original,ReleaseFault=releaseFault};Exception? observed=null;
   try{NyFunctions.@@FAULTRESOURCESHIELD@@(host);}catch(Exception error){observed=error;}
   Check(ReferenceEquals(observed,original)&&host.Closed==1&&NyRuntime.Cancellation.Value==source.Token,"outer release must happen once and preserve original body fault");
   NyRuntime.Cancellation.Value=CancellationToken.None;Pass(releaseFault?"outer-release-fault-preserves-body":"outer-release-after-rethrow");
  }
  foreach(var directCancel in new[]{false,true})foreach(var rollbackFault in new[]{false,true})
  {
   using var source=new CancellationTokenSource();var scope=new NyTaskScope(source.Token);
   var original=new InvalidOperationException("commit fault");var reverse=new InvalidOperationException("rollback fault");
   var host=new Probe(rawCase.RootElement){InjectedFault=original,Owned=true,CleanupFault=rollbackFault?reverse:null};
   CancellationToken requestToken=default;
   var parent=scope.Start(()=>{requestToken=NyRuntime.Cancellation.Value;try{return NyFunctions.@@FAULTOWNEDSHIELD@@(host);}finally{Check(NyRuntime.Cancellation.Value==requestToken,"request token restoration");}});
   Check(host.WorkerEntered.Wait(TimeSpan.FromSeconds(10)),"cleanup worker entered");
   if(directCancel){NyFunctions.@@DIRECTCANCEL@@(parent);NyFunctions.@@DIRECTCANCEL@@(parent);}else{source.Cancel();source.Cancel();}
   Check(requestToken.IsCancellationRequested&&!host.WorkerToken.IsCancellationRequested&&!parent.Running.IsCompleted&&host.Closed==0,"pending cancellation must not interrupt new shield-owned scope");
   host.ExitBody.Set();Check(host.CleanupEntered.Wait(TimeSpan.FromSeconds(10)),"scope cleanup entered");
   Check(host.WorkerToken.IsCancellationRequested&&!parent.Running.IsCompleted&&host.Closed==0,"drain before release and propagation");
   host.CleanupRelease.Set();Exception? observed=null;
   try{parent.Running.GetAwaiter().GetResult();}catch(Exception error){observed=error;}
   Check(ReferenceEquals(observed,rollbackFault?reverse:original)&&host.Closed==1&&host.Active==0,"rollback exception overrides original and cancellation after drainage/release");
   if(directCancel)Check(!source.IsCancellationRequested,"direct TaskCancel must leave scope parent alive");
   scope.Close(true);Pass($"nested-drain-release-{(directCancel?"TaskCancel":"parentCancel")}-{(rollbackFault?"rollbackFault":"originalFault")}");
  }
  var contextOriginal=new InvalidOperationException("context fault");var contextHost=new Probe(rawCase.RootElement){InjectedFault=contextOriginal};
  Exception? contextObserved=null;try{NyFunctions.@@CONTEXTFAULT@@(contextHost);}catch(Exception error){contextObserved=error;}
  Check(ReferenceEquals(contextObserved,contextOriginal)&&contextHost.Reports.Count==0,"context evaluation is outside boundary");Pass("context-fault-bypasses-handler");
 }
}
