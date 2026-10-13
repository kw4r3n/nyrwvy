sealed class DerivedCancellation : OperationCanceledException { public DerivedCancellation(CancellationToken token):base("derived cancellation",token){} }
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
  using var clear=new CancellationTokenSource();using var pending=new CancellationTokenSource();pending.Cancel();using var other=new CancellationTokenSource();
  foreach(var ambientCancelled in new[]{false,true})
  foreach(var sample in new (string Name,Exception Fault,bool Cancellation)[]{
   ("ordinary-cancel-message",new InvalidOperationException("operation cancelled"),false),
   ("wrapper",new InvalidOperationException("wrapper",new OperationCanceledException("inner cancellation",pending.Token)),false),
   ("oce-none",new OperationCanceledException("no token"),true),
   ("oce-clear",new OperationCanceledException("clear token",clear.Token),true),
   ("oce-cancelled",new OperationCanceledException("cancelled token",pending.Token),true),
   ("oce-other",new OperationCanceledException("other token",other.Token),true),
   ("task-cancelled",new System.Threading.Tasks.TaskCanceledException("task cancellation"),true),
   ("subclass",new DerivedCancellation(other.Token),true)})
  {
   NyRuntime.Cancellation.Value=ambientCancelled?pending.Token:clear.Token;
   var host=new Probe(rawCase.RootElement){InjectedFault=sample.Fault};Exception? observed=null;NyResult<NyStr,NyStr> answer=default!;
   try{answer=NyFunctions.@@SELECTIVECATCH@@(host);}catch(Exception error){observed=error;}
   Check(sample.Cancellation?ReferenceEquals(observed,sample.Fault):observed==null&&!answer.IsOk&&answer.ErrorValue.ToString()==sample.Fault.Message,"selective handler uses original class");
   if(sample.Cancellation)Check(observed!.StackTrace!.Contains("Probe.step"),"selective rethrow retains original host stack");
   Check(NyRuntime.Cancellation.Value==(ambientCancelled?pending.Token:clear.Token),"pure classification leaves ambient token unchanged");
   var captured=System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sample.Fault);
   Check(NyFunctions.@@ISCANCELLATION@@(captured)==sample.Cancellation,"pure predicate classifies original EDI");
   foreach(var mutant in new (string Name,Func<NyResult<NyStr,NyStr>> Run,bool Expected)[]{
    ("inverted",()=>NyFunctions.@@INVERTEDCATCH@@(host),!sample.Cancellation),
    ("always",()=>NyFunctions.@@ALWAYSCANCELCATCH@@(host),true),
    ("never",()=>NyFunctions.@@NEVERCANCELCATCH@@(host),false)})
   {
    Exception? error=null;NyResult<NyStr,NyStr> value=default!;try{value=mutant.Run();}catch(Exception fault){error=fault;}
    Check(mutant.Expected?ReferenceEquals(error,sample.Fault):error==null&&!value.IsOk,"compiled authoring sensitivity "+mutant.Name);
    if(mutant.Expected!=sample.Cancellation)Check((error!=null)!=sample.Cancellation,"production acceptance rejects altered compiled policy");
   }
   NyRuntime.Cancellation.Value=CancellationToken.None;Pass($"classification-{sample.Name}-{(ambientCancelled?"ambient-cancelled":"ambient-clear")}");
  }
  foreach(var typedOk in new[]{false,true})
  {
   var host=new Probe(rawCase.RootElement);var result=typedOk?NyFunctions.@@TYPEDOKCATCH@@(host):NyFunctions.@@TYPEDERRCATCH@@(host);
   Check(result.IsOk==typedOk&&(typedOk?result.OkValue.ToString():result.ErrorValue.ToString())==(typedOk?"typed ok":"typed err")&&host.Reports.Count==0,"Ok and typed Err bypass catch");Pass(typedOk?"typed-ok-bypasses":"typed-err-bypasses");
  }
  foreach(var cancelled in new[]{false,true})
  {
   using var source=new CancellationTokenSource();if(cancelled)source.Cancel();NyRuntime.Cancellation.Value=source.Token;
   var host=new Probe(rawCase.RootElement);var result=NyFunctions.@@ABORTCATCH@@(host);
   Check(!result.IsOk&&result.ErrorValue.ToString()=="typed abort"&&host.Reports.Count==0&&host.Steps.Count==0,"typed abort bypasses handler");
   Check(NyRuntime.Cancellation.Value==source.Token,"abort token restoration");NyRuntime.Cancellation.Value=CancellationToken.None;
   Pass(cancelled?"typed-abort-pending-cancel":"typed-abort");
  }
  foreach(var releaseFault in new[]{false,true})foreach(var oceBody in new[]{false,true})
  {
   using var source=new CancellationTokenSource();source.Cancel();NyRuntime.Cancellation.Value=source.Token;
   Exception original=oceBody?new OperationCanceledException("resource body cancellation"):new InvalidOperationException("resource body fault");
   var host=new Probe(rawCase.RootElement){InjectedFault=original,ReleaseFault=releaseFault};Exception? observed=null;
   try{NyFunctions.@@FAULTRESOURCESHIELD@@(host);}catch(Exception error){observed=error;}
   Check(ReferenceEquals(observed,original)&&host.Closed==1&&NyRuntime.Cancellation.Value==source.Token,"outer release must happen once and preserve original body fault");
   NyRuntime.Cancellation.Value=CancellationToken.None;Pass($"outer-release-{(releaseFault?"fault":"success")}-{(oceBody?"OCE":"ordinary")}");
  }
  foreach(var directCancel in new[]{false,true})foreach(var rollbackFault in new[]{false,true})foreach(var oceBody in new[]{false,true})
  {
   using var source=new CancellationTokenSource();var scope=new NyTaskScope(source.Token);
   Exception original=oceBody?new OperationCanceledException("commit cancellation"):new InvalidOperationException("commit fault");var reverse=new InvalidOperationException("rollback fault");
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
   scope.Close(true);Pass($"nested-drain-release-{(directCancel?"TaskCancel":"parentCancel")}-{(rollbackFault?"rollbackFault":"originalFault")}-{(oceBody?"OCE":"ordinary")}");
  }
  var contextOriginal=new InvalidOperationException("context fault");var contextHost=new Probe(rawCase.RootElement){InjectedFault=contextOriginal};
  Exception? contextObserved=null;try{NyFunctions.@@CONTEXTFAULT@@(contextHost);}catch(Exception error){contextObserved=error;}
  Check(ReferenceEquals(contextObserved,contextOriginal)&&contextHost.Reports.Count==0,"context evaluation is outside boundary");Pass("context-fault-bypasses-handler");
 }
}
