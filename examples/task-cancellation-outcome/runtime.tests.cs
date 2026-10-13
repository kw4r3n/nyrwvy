using System;
using System.Threading;
using System.Text.Json;
using Nyrwvy.Generated;
using R = Nyrwvy.Generated.NyResult<Nyrwvy.Generated.NyStr, Nyrwvy.Generated.NyStr>;
using T = Nyrwvy.Generated.NyTask<Nyrwvy.Generated.NyResult<Nyrwvy.Generated.NyStr, Nyrwvy.Generated.NyStr>>;

sealed class Probe : @@HOST@@
{
 public Exception? Fault, Thrown, CleanupFault, ReleaseFault;
 public bool Gated=false, ThrowOnCancellation=false, TypedError=false, HoldCleanup=false;
 public int Active, Cleaned, Closed;
 public CancellationToken PrimaryToken, PeerToken;
 public readonly ManualResetEventSlim Entered=new(false), Go=new(false), PeerEntered=new(false), PeerGo=new(false), CleanupEntered=new(false), CleanupGo=new(false);
 @@METHODS@@
 public R work(NyStr name)
 {
  Interlocked.Increment(ref Active);
  try{
   if(name.ToString()=="peer"){
    PeerToken=NyRuntime.Cancellation.Value;PeerEntered.Set();Program.Await(PeerGo,"peer release");return R.Ok(((NyStr)"peer-ok"));
   }
   PrimaryToken=NyRuntime.Cancellation.Value;Entered.Set();
   if(Gated)Program.Await(Go,"worker release");
   try{
    if(Fault is not null)throw Fault;
    if(ThrowOnCancellation)PrimaryToken.ThrowIfCancellationRequested();
   }catch(Exception error){Thrown=error;throw;}
   return TypedError?R.Err(((NyStr)"existing-error")):R.Ok(((NyStr)"success"));
  }finally{Interlocked.Decrement(ref Active);}
 }
 public NyUnit cleanup(){Interlocked.Increment(ref Cleaned);CleanupEntered.Set();if(HoldCleanup)Program.Await(CleanupGo,"cleanup release");if(CleanupFault is not null)throw CleanupFault;return NyUnit.Value;}
 public NyResult<NyUnit,NyStr> open()=>NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);
 public NyUnit close(NyUnit handle){Program.Check(Active==0,"release follows worker drainage");Interlocked.Increment(ref Closed);if(ReleaseFault is not null)throw ReleaseFault;return NyUnit.Value;}
}
static class Calls { @@CALLS@@ }
sealed class DerivedCancellation : OperationCanceledException { public DerivedCancellation():base("derived"){} }
static class Program
{
 public static void Check(bool value,string message){if(!value)throw new Exception(message);}
 public static void Await(ManualResetEventSlim gate,string message)=>Check(gate.Wait(TimeSpan.FromSeconds(10)),"gate timeout: "+message);
 static Exception? Capture(Action action){try{action();return null;}catch(Exception error){return error;}}
 static Exception? Join(T child)=>Capture(()=>child.Running.GetAwaiter().GetResult());
 static void Identity(Exception? observed,Exception original,string location){Check(ReferenceEquals(observed,original),location+" original object");Check(observed!.StackTrace!.Contains("Probe.work"),location+" throwing stack");}
 static void Pass(string control,string? assertionFailure=null)=>Console.WriteLine(JsonSerializer.Serialize(new {control,passed=true,assertionFailure}));
 static void SettledErr(T child,Exception? joinError){Check(joinError is null&&child.Running.IsCompletedSuccessfully&&!child.Running.IsFaulted&&!child.Running.IsCanceled,"worker must settle successfully with a typed Result");var state=child.Snapshot();Check(state.HasValue&&!state.Value.IsOk&&state.Value.ErrorValue.ToString()=="stopped-by-program","chosen typed Err");}
 static T Start(string policy,Probe host,NyTaskScope scope)=>policy switch {
  "adapted"=>Calls.start_adaptedWorker(host,scope,((NyStr)"primary")),
  "inverted"=>Calls.start_invertedWorker(host,scope,((NyStr)"primary")),
  _=>Calls.start_rawWorker(host,scope,((NyStr)"primary"))
 };
 static void Boundaries()
 {
  foreach(var policy in new[]{"baseline","waitOnly","adapted"})foreach(var cancel in new[]{"clear","TaskCancel","root"}){
   using var root=new CancellationTokenSource();var scope=new NyTaskScope(root.Token);
   var host=new Probe {Gated=true,ThrowOnCancellation=cancel!="clear",Fault=cancel=="clear"?new OperationCanceledException("clear-token OCE",root.Token):null};
   var child=Start(policy,host,scope);Await(host.Entered,"primary entered");
   Check(!child.Running.IsCompleted&&!host.PrimaryToken.IsCancellationRequested,"worker gated before request");
   if(cancel=="TaskCancel"){Calls.cancelChild(child);Calls.cancelChild(child);Check(!root.IsCancellationRequested,"child cancellation leaves parent clear");}
   if(cancel=="root"){root.Cancel();root.Cancel();}
   Check(host.PrimaryToken.IsCancellationRequested==(cancel!="clear"),"linked child token request");
   host.Go.Set();var joined=Join(child);var original=host.Thrown!;
   Check(original is OperationCanceledException&&((OperationCanceledException)original).CancellationToken.IsCancellationRequested==(cancel!="clear"),"real OCE and token state");
   if(policy=="adapted"){
    SettledErr(child,joined);var value=Calls.joinOne(child,NyUnit.Value);Check(!value.IsOk&&value.ErrorValue.ToString()=="stopped-by-program","TaskWait reports Err");
    Check(Capture(()=>scope.Close(false)) is null,"adapted scope closes normally");
   }else{
    Identity(joined,original,"join");Check(child.Running.IsFaulted&&!child.Running.IsCanceled,"existing faulted child state");
    if(policy=="waitOnly"){
     var value=Calls.waitOnlyCatch(child,NyUnit.Value);Check(!value.IsOk&&value.ErrorValue.ToString()=="stopped-by-program","wait-only catch returns local Err");
     Check(child.Running.IsFaulted,"wait-only catch does not repair child");
    }else Identity(Capture(()=>Calls.joinOne(child,NyUnit.Value)),original,"TaskWait");
    Identity(Capture(()=>child.Snapshot()),original,"Snapshot");Identity(Capture(()=>scope.Close(false)),original,"scope Close");
   }
   Check(host.Active==0,"worker drained");Pass($"boundary-{policy}-{cancel}");
  }
 }
 static void Classification()
 {
  foreach(var ambientCancelled in new[]{false,true})foreach(var sample in new (string Name,Exception Fault,bool Cancellation)[]{
   ("message",new InvalidOperationException("operation cancelled"),false),
   ("wrapper",new InvalidOperationException("wrapper",new OperationCanceledException("inner")),false),
   ("clear-OCE",new OperationCanceledException("clear"),true),
   ("task-cancelled",new System.Threading.Tasks.TaskCanceledException("task cancelled"),true),
   ("derived",new DerivedCancellation(),true)}){
   using var root=new CancellationTokenSource();if(ambientCancelled)root.Cancel();var scope=new NyTaskScope(root.Token);
   var host=new Probe {Fault=sample.Fault};var child=Start("adapted",host,scope);var joined=Join(child);
   Check(host.PrimaryToken.IsCancellationRequested==ambientCancelled,"adapter preserves captured token state before scope Close");
   if(sample.Cancellation){SettledErr(child,joined);Check(Capture(()=>scope.Close(false)) is null,"cancellation normal close");}
   else{Identity(joined,sample.Fault,"ordinary join");Identity(Capture(()=>Calls.joinOne(child,NyUnit.Value)),sample.Fault,"ordinary TaskWait");Identity(Capture(()=>scope.Close(false)),sample.Fault,"ordinary Close");}
   Pass($"classification-{sample.Name}-{ambientCancelled}");
  }
  foreach(var typedError in new[]{false,true}){
   var scope=new NyTaskScope(CancellationToken.None);var host=new Probe {TypedError=typedError};var child=Start("adapted",host,scope);Check(Join(child) is null,"typed normal join");
   var result=Calls.joinOne(child,NyUnit.Value);Check(result.IsOk==!typedError&&(typedError?result.ErrorValue.ToString():result.OkValue.ToString())==(typedError?"existing-error":"success"),"existing Result passes unchanged");
   Check(host.Thrown is null&&child.Running.IsCompletedSuccessfully,"typed Result bypasses handler");scope.Close(false);Pass(typedError?"existing-typed-Err":"existing-Ok");
  }
 }
 static void Peer()
 {
  using var root=new CancellationTokenSource();var scope=new NyTaskScope(root.Token);var host=new Probe {Gated=true,ThrowOnCancellation=true};
  var child=Start("adapted",host,scope);var peer=Calls.start_rawWorker(host,scope,((NyStr)"peer"));Await(host.Entered,"primary");Await(host.PeerEntered,"peer");
  Calls.cancelChild(child);host.Go.Set();SettledErr(child,Join(child));
  Check(!peer.Running.IsCompleted&&!host.PeerToken.IsCancellationRequested&&!root.IsCancellationRequested&&host.Active==1,"held sibling survives adapted worker Err");
  host.PeerGo.Set();Check(Join(peer) is null,"explicit sibling join");var state=peer.Snapshot();Check(state.HasValue&&state.Value.IsOk&&state.Value.OkValue.ToString()=="peer-ok","sibling success after explicit release");scope.Close(false);Check(host.Active==0,"both drained");Pass("held-sibling-explicit-release-join");
 }
 static R Entry(string policy,Probe host)=>policy switch {
  "baseline"=>Calls.baseline(host,false),"waitOnly"=>Calls.waitOnly(host,false),"inverted"=>Calls.inverted(host,false),"cleanup"=>Calls.cleanup(host,false),_=>Calls.adapted(host,false)
 };
 static void Ownership()
 {
  foreach(var policy in new[]{"baseline","waitOnly","adapted"})foreach(var releaseFault in new[]{false,true}){
   var original=new OperationCanceledException("body cancellation");var release=new InvalidOperationException("release fault");var host=new Probe {Fault=original,ReleaseFault=releaseFault?release:null};R result=default!;
   var observed=Capture(()=>result=Entry(policy,host));
   if(policy=="adapted")Check(releaseFault?ReferenceEquals(observed,release):observed is null&&!result.IsOk&&result.ErrorValue.ToString()=="stopped-by-program","outer release visible after adapted Err");
   else Identity(observed,original,"unadapted entry");
   Check(host.Closed==1&&host.Active==0,"once-only release after owned scope drainage");Pass($"entry-release-{policy}-{releaseFault}");
  }
  foreach(var faultKind in new[]{"none","ordinary","OCE"})foreach(var releaseFault in new[]{false,true}){
   using var root=new CancellationTokenSource();var outer=new NyTaskScope(root.Token);
   Exception? cleanup=faultKind=="none"?null:faultKind=="OCE"?new OperationCanceledException("cleanup OCE"):new InvalidOperationException("cleanup ordinary");var release=new InvalidOperationException("release fault");
   var host=new Probe {Gated=true,ThrowOnCancellation=true,HoldCleanup=true,CleanupFault=cleanup,ReleaseFault=releaseFault?release:null};
   var owner=outer.Start(()=>Entry("cleanup",host));Await(host.Entered,"owned primary");root.Cancel();host.Go.Set();Await(host.CleanupEntered,"cleanup entered");
   Check(host.PrimaryToken.IsCancellationRequested&&!owner.Running.IsCompleted&&host.Cleaned==1&&host.Closed==0,"cleanup outside adapter holds release and terminal outcome");
   host.CleanupGo.Set();var observed=Join(owner);
   var expected=cleanup??(releaseFault?release:null);Check(ReferenceEquals(observed,expected),"cleanup exception escapes; body fault precedes release");
   if(expected is null){SettledErr(owner,observed);outer.Close(false);}else{Check(observed!.StackTrace!.Contains(cleanup is null?"Probe.close":"Probe.cleanup"),"cleanup/release stack preserved");Check(ReferenceEquals(Capture(()=>outer.Close(false)),expected),"outer Close preserves fault");}
   Check(host.Cleaned==1&&host.Closed==1&&host.Active==0,"once cleanup/release after join");Pass($"cleanup-release-{faultKind}-{releaseFault}");
  }
  var ordinary=new InvalidOperationException("first ordinary");var firstHost=new Probe {Fault=ordinary};var secondHost=new Probe {Fault=new OperationCanceledException("adapted second")};var scope=new NyTaskScope(CancellationToken.None);
  var first=Start("adapted",firstHost,scope);var second=Start("adapted",secondHost,scope);Identity(Join(first),ordinary,"first joined");SettledErr(second,Join(second));Identity(Capture(()=>scope.Close(false)),ordinary,"ordinary fault precedence");Pass("ordinary-fault-precedence-with-adapted-sibling");
  var early=new InvalidOperationException("started first, completed last");var late=new InvalidOperationException("started last, completed first");var orderedScope=new NyTaskScope(CancellationToken.None);
  var earlyHost=new Probe {Fault=early,Gated=true};var lateHost=new Probe {Fault=late};var earlyTask=Start("adapted",earlyHost,orderedScope);Await(earlyHost.Entered,"earliest child entered");var lateTask=Start("adapted",lateHost,orderedScope);
  Identity(Join(lateTask),late,"later child joined first");Check(!earlyTask.Running.IsCompleted,"earliest child still held");earlyHost.Go.Set();Identity(Join(earlyTask),early,"earliest child explicit join");Identity(Capture(()=>orderedScope.Close(false)),early,"earliest-started ordinary fault wins");Pass("ordinary-start-order-precedence-reverse-completion");
  foreach(var policy in new[]{"baseline","waitOnly","adapted"}){
   var host=new Probe {Fault=new InvalidOperationException("ordinary cancel message"),ReleaseFault=new InvalidOperationException("competing release")};
   Identity(Capture(()=>Entry(policy,host)),host.Fault,"body precedence over release");Check(host.Closed==1,"precedence release once");Pass($"ordinary-body-release-precedence-{policy}");
  }
 }
 // All variants use the identical acceptance assertions, after explicit joins.
 static void AcceptCancellation(T child,Exception? joined,Exception? close){SettledErr(child,joined);Check(close is null,"acceptance normal scope closure");}
 static void AcceptOrdinary(Exception? joined,Exception? close,Exception fault){Identity(joined,fault,"acceptance ordinary join");Identity(close,fault,"acceptance ordinary Close");}
 static void Mutants()
 {
  foreach(var policy in new[]{"adapted","waitOnly","inverted"}){
   var scope=new NyTaskScope(CancellationToken.None);var host=new Probe {Fault=new OperationCanceledException("mutant OCE")};var child=Start(policy,host,scope);var joined=Join(child);
   if(policy=="waitOnly")Check(!Calls.waitOnlyCatch(child,NyUnit.Value).IsOk,"wrong-boundary catch executed");
   var close=Capture(()=>scope.Close(false));var rejected=Capture(()=>AcceptCancellation(child,joined,close));Check((rejected is null)==(policy=="adapted"),"identical cancellation assertions reject compiled mutant");Pass($"compiled-cancellation-acceptance-{policy}",rejected?.Message);
  }
  foreach(var policy in new[]{"adapted","inverted"}){
   var scope=new NyTaskScope(CancellationToken.None);var host=new Probe {Fault=new InvalidOperationException("mutant ordinary")};var child=Start(policy,host,scope);var joined=Join(child);var close=Capture(()=>scope.Close(false));var rejected=Capture(()=>AcceptOrdinary(joined,close,host.Fault));Check((rejected is null)==(policy=="adapted"),"identical ordinary assertions reject inversion");Pass($"compiled-ordinary-acceptance-{policy}",rejected?.Message);
  }
 }
 static void Main(){Boundaries();Classification();Peer();Ownership();Mutants();}
}
