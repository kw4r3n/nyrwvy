using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using Nyrwvy.Generated;

// Fake effects and deterministic barriers only. No registry, handles, lineage,
// capacity, settlement, snapshot/check decisions, or discard eligibility here.
sealed class Effects : @@HOST@@
{
 readonly JsonElement scenario;
 readonly CancellationTokenSource root; readonly string mode;
 readonly ManualResetEventSlim ownerParked=new(false),rootInjected=new(false);
 readonly ConcurrentDictionary<long,int> leases=new();
 readonly ConcurrentDictionary<string,ManualResetEventSlim> callerReplied=new();
 public System.Threading.Tasks.Task Driver {get;private set;}=System.Threading.Tasks.Task.CompletedTask;
 readonly AsyncLocal<string> requestLabel=new();
 readonly ConcurrentDictionary<string,ManualResetEventSlim> requestStarted=new();
 readonly ConcurrentDictionary<string,bool> acquired=new();
 readonly ManualResetEventSlim spawnHeld=new(false),spawnReleased=new(false),closeHeld=new(false),closeReleased=new(false);
 readonly string name;
 readonly ConcurrentDictionary<long,ManualResetEventSlim> providerEntered=new();
 readonly ConcurrentDictionary<long,ManualResetEventSlim> snapshotEntered=new();
 readonly ConcurrentDictionary<long,ManualResetEventSlim> snapshotRelease=new();
 readonly ConcurrentDictionary<long,ManualResetEventSlim> workerRelease=new();
 readonly ConcurrentDictionary<long,CancellationToken> providerTokens=new();
 readonly ManualResetEventSlim checkEntered=new(false),checkRelease=new(false);
 readonly CountdownEvent bothProviders=new(2);
 readonly ManualResetEventSlim callerEntered=new(false);
 readonly ManualResetEventSlim integrationEntered=new(false),integrationRelease=new(false),finishEntered=new(false),finishRelease=new(false);
 readonly ConcurrentDictionary<string,int> counts=new();
 bool Flag(string key)=>scenario.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
 string Setting(string key)=>scenario.TryGetProperty(key,out var v)?v.GetString()!:"";

 public NyResult<NyList<NyStr>,NyStr> Step(NyStr operation,NyStr patch)
 {
  string phase=operation.ToString();Log("step",("operation",phase),("patch",patch.ToString()));
  int count=counts.AddOrUpdate(phase,1,(_,n)=>n+1);
  if(phase==(Setting("patch")==""?"head":"commit")&&count==1){integrationEntered.Set();Wait(integrationRelease,"integration release");}
  if(Flag("cooperativeCheck")&&phase=="check"&&count==1){Check(NyRuntime.CancellationRequested,"unshielded check must see pending cancellation");throw new OperationCanceledException("cancelled",NyRuntime.Cancellation.Value);}
  if(phase==Setting("rawStepFault")&&count==1){Check(phase=="check"||!NyRuntime.CancellationRequested,"protected fault host observed cancellation");if(phase=="check"&&Setting("cancel")=="check")Check(NyRuntime.CancellationRequested,"unshielded throwing check must see pending cancellation");throw new InvalidOperationException("raw "+phase+" fault");}
  Check(!NyRuntime.CancellationRequested,"shielded effect observed cancellation: "+phase);
  if(Flag("rawFault")&&phase=="commit"&&count==1)throw new InvalidOperationException("raw commit fault");
  if(Flag("rawRollbackFault")&&phase==Setting("rollbackFail"))throw new InvalidOperationException(phase);
  if(Flag("rawSaveFault")&&phase=="save"&&count==1)throw new InvalidOperationException("raw save fault");
  if(Flag("rawHeadFault")&&phase=="head"&&count==1)throw new InvalidOperationException("raw head fault");
  if((phase==Setting("fail")&&count==1)||phase==Setting("rollbackFail"))return NyResult<NyList<NyStr>,NyStr>.Err(phase);
  string[] values=phase=="paths-member"?new[]{"a","b"}:phase=="paths-leader"?new[]{"c"}:phase=="patch"?new[]{Setting("patch")}:phase=="head"?new[]{Setting("patch")==""?"source-head":"integrated-head"}:Array.Empty<string>();
  return NyResult<NyList<NyStr>,NyStr>.Ok(NyList<NyStr>.Create(values.Select(x=>(NyStr)x).ToArray()));
 }
 readonly ConcurrentQueue<object> calls=new();
 public int Active,MaxActive,Opened,Closed;
 public Effects(JsonElement scenario,CancellationTokenSource root,string mode){this.scenario=scenario;this.root=root;this.mode=mode;name=scenario.GetProperty("id").GetString()!; if(mode=="external") Driver=System.Threading.Tasks.Task.Run(()=>{Wait(ownerParked,"external owner parked");root.Cancel();rootInjected.Set();});}
 static ManualResetEventSlim Cell(ConcurrentDictionary<long,ManualResetEventSlim> map,long id)=>map.GetOrAdd(id,_=>new(false));
 public object[] Calls=>calls.ToArray();
 void Log(string op,params (string Name,object Value)[] args)=>calls.Enqueue(new {operation=op,request=requestLabel.Value,arguments=args.Select(a=>new {parameter=a.Name,value=new {value=a.Value}}).ToArray()});
 static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
 static void Wait(ManualResetEventSlim cell,string label){Check(cell.Wait(TimeSpan.FromSeconds(5)),"barrier timeout: "+label);}
 string Kind(long id)=>scenario.GetProperty("providers").TryGetProperty(id.ToString(),out var v)?v.GetString()!:"hold";
 public NyResult<NyUnit,NyStr> Open(){Log("open");Opened++;return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyUnit Close(NyUnit handle){Log("close");Closed++;Check(Active==0,"provider active after scope join");Check(leases.Values.All(n=>n==1)&&providerTokens.Keys.All(id=>leases.TryGetValue(id,out var n)&&n==1),"every opened lease closes once");if(Flag("releaseFault"))throw new InvalidOperationException("release fault");return NyUnit.Value;}
 public NyResult<NyUnit,NyStr> WorkspaceCreate(NyStr workspace,NyStr basis){Log("workspaceCreate",("workspace",workspace.ToString()),("base",basis.ToString()));if(requestLabel.Value=="winner"){spawnHeld.Set();Wait(spawnReleased,"spawn release");}return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyResult<NyUnit,NyStr> WorkspaceDiscard(NyStr workspace){Log("workspaceDiscard",("workspace",workspace.ToString()));return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 sealed class ExpectedProviderError:Exception{}
 public NyResult<NyStr,NyStr> Provider(long id,NyStr workspace,long previous)
 {
  Log("provider",("id",id.ToString()),("workspace",workspace.ToString()),("previous",previous.ToString()));
  int active=Interlocked.Increment(ref Active),old;
  while(active>(old=Volatile.Read(ref MaxActive))&&Interlocked.CompareExchange(ref MaxActive,active,old)!=old){}
  providerTokens[id]=NyRuntime.Cancellation.Value;
  Cell(providerEntered,id).Set();
  bool overlap=false;
  try {
   if(overlap&&id<=2){bothProviders.Signal();Check(bothProviders.Wait(TimeSpan.FromSeconds(5)),"provider overlap barrier");}
   if(Kind(id)=="hold"){NyRuntime.Cancellation.Value.WaitHandle.WaitOne();return NyResult<NyStr,NyStr>.Err("cancelled");}
   try {
    if(Kind(id)=="expected-error")throw new ExpectedProviderError();
    if(Kind(id)=="inner-timeout")throw new TimeoutException("inner timeout");
    if(Kind(id)=="unexpected-fault")throw new InvalidOperationException("unexpected host fault");
    return NyResult<NyStr,NyStr>.Ok("completed");
   }catch(ExpectedProviderError){return NyResult<NyStr,NyStr>.Err("deadline");}
    catch(TimeoutException){return NyResult<NyStr,NyStr>.Err("inner-timeout");}
  }finally{Interlocked.Decrement(ref Active);}
 }
 public NyUnit MemberClose(long id){Check(leases.AddOrUpdate(id,1,(_,n)=>n+1)==1,"duplicate lease close");Log("memberClose",("id",id.ToString()));return NyUnit.Value;}
 public NyResult<NyStr,NyStr> Snapshot(long id,NyStr workspace)
 {
  Log("snapshot",("id",id.ToString()),("workspace",workspace.ToString()));
  Cell(snapshotEntered,id).Set();
  Wait(Cell(snapshotRelease,id),"snapshot release "+id);
  if(Setting("kind")=="settlement"&&NyRuntime.CancellationRequested)throw new InvalidOperationException("snapshot cancelled");
  Check(!NyRuntime.CancellationRequested,"stop interrupted determined completion");
  return name=="snapshot-error"&&id==1?NyResult<NyStr,NyStr>.Err("snapshot failed"):NyResult<NyStr,NyStr>.Ok("commit"+id);
 }
 public NyStr Checks(long id,NyStr commit){Log("checks",("id",id.ToString()),("commit",commit.ToString()));
  if(Flag("checksGate")&&id==1){checkEntered.Set();Wait(checkRelease,"checks release");Check(!NyRuntime.CancellationRequested,"stop cancelled determined completion during checks");}
  return name=="checks-failed"&&id==1?"failed":name=="checks-unavailable"&&id==1?"unavailable":"passed";}
 public NyUnit Gate(NyStr raw)
 {
  string label=raw.ToString();Log("gate",("label",label));
  if(label=="owner-park"){ownerParked.Set();NyRuntime.Cancellation.Value.WaitHandle.WaitOne();}
  else if(label=="probe-ready"){Wait(ownerParked,label);if(mode=="external")Wait(rootInjected,label);}
  else if(label=="probe-cancelled"){if(Setting("kind")=="queued"||Setting("kind")=="integration")Wait(callerReplied.GetOrAdd(Setting("kind")=="queued"?"loser":"second",_=>new(false)),"queued cancellation reply before held mutation release");}
  else if(label=="probe-release"){spawnReleased.Set();integrationRelease.Set();closeReleased.Set();if(Setting("kind")!="settlement")Cell(snapshotRelease,1).Set();Cell(workerRelease,1).Set();Cell(workerRelease,2).Set();}
  else if(label.StartsWith("request-start:"))requestStarted.GetOrAdd(label[14..],_=>new(false)).Set();
  else if(label.StartsWith("enter:")){requestLabel.Value=label[6..];acquired[label[6..]]=true;}
  else if(label.StartsWith("released:")){}
  else if(label.StartsWith("queued:")){Wait(requestStarted.GetOrAdd(label[7..],_=>new(false)),label);Check(!acquired.ContainsKey(label[7..]),"queued mutation acquired ownership");}
  else if(label=="spawn-held")Wait(spawnHeld,label);
  else if(label=="spawn-release")spawnReleased.Set();
  else if(label=="commit-held")Wait(integrationEntered,label);
  else if(label=="commit-release")integrationRelease.Set();
  else if(label=="close-held-worker"&&Setting("kind")=="shutdown"){closeHeld.Set();Wait(closeReleased,label); }
  else if(label=="close-held")Wait(closeHeld,label);
  else if(label=="close-release")closeReleased.Set();
  else if(label.StartsWith("worker-entry:"))Wait(Cell(workerRelease,long.Parse(label[13..])),label);
  else if(label.StartsWith("snapshot")){long id=long.Parse(label[8..]);Cell(workerRelease,id).Set();Cell(workerRelease,2).Set();Wait(Cell(providerEntered,2),"peer entered");Wait(Cell(snapshotEntered,id),label);}
  else if(label.StartsWith("provider")){long id=long.Parse(label[8..]);Cell(workerRelease,id).Set();Wait(Cell(providerEntered,id),label);}
  else if(label.StartsWith("release")&&label!="release-checks"){
   long id=long.Parse(label[7..]);Cell(snapshotRelease,id).Set();
   if(Flag("checksGate")&&id==1)Wait(checkEntered,"checks entered");
  }
  else if(label=="checks1")Wait(checkEntered,label);
  else if(label=="release-checks")checkRelease.Set();
  else if(label.StartsWith("stop-join:")){
   long id=long.Parse(label[10..]);Cell(workerRelease,id).Set();
   if(Flag("finishCancel")&&id==1){finishEntered.Set();Wait(finishRelease,"finish release");Check(!NyRuntime.CancellationRequested,"finish shield lost token mask");Cell(snapshotRelease,1).Set();}
  }
  else if(label=="integration-await")Wait(integrationEntered,label);
  else if(label=="integration-release")integrationRelease.Set();
  else if(label=="observation-await"){Check(providerTokens.TryGetValue(2,out var token)&&!token.IsCancellationRequested,"peer cancelled before observation");}
  else if(label=="finish-await")Wait(finishEntered,label);
  else if(label=="finish-release")finishRelease.Set();
  return NyUnit.Value;
 }
 public NyUnit Reply(NyStr label,NyStr record){
  if(label.ToString()=="observer")Check(providerTokens.TryGetValue(2,out var token)&&!token.IsCancellationRequested,"observer cancelled its Member");

  // The release audit waits for the next explicit settled request. No host policy.
  Log("reply",("label",label.ToString()),("record",record.ToString()));callerReplied.GetOrAdd(label.ToString(),_=>new(false)).Set();if(Setting("kind")=="settlement"&&label.ToString()=="retained")Cell(snapshotRelease,1).Set();return NyUnit.Value;
 }

 @@METHODS@@
}
static class Program
{
 public static void Main(string[] args)
 {
  ThreadPool.SetMinThreads(16,16);
  using var data=JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));
  foreach(var c in data.RootElement.EnumerateArray()){
   foreach(var mode in new[]{"task","external"}){
   using var root=new CancellationTokenSource();NyRuntime.Cancellation.Value=root.Token;var effects=new Effects(c,root,mode);
   object response;
   try {
    NyResult<NyUnit,NyStr> result;
    if(c.GetProperty("id").GetString()=="caller-cancellation-gap")result=NyFunctions.@@CALLER_RUN@@(@@CALLER_HOST@@:effects);
    else {
     var requests=c.GetProperty("script").EnumerateArray().Select(r=>new @@REQUEST@@(@@REQUESTARGS@@)).ToArray();
     result=NyFunctions.@@RUN@@(@@RUN_HOST@@:effects,@@RUN_REQUESTS@@:NyList<@@REQUEST@@>.Create(requests));
    }
    if(!result.IsOk&&result.ErrorValue.ToString()!="owner cancelled")throw new Exception("unexpected graph Err");
    response=new {ok=true,result=new {actual=new {isOk=result.IsOk,error=result.IsOk?"":result.ErrorValue.ToString()},calls=effects.Calls}};
   }catch(Exception error){response=new {ok=false,error=new {code="RunRuntimeFailure",message=error.Message,calls=effects.Calls}};}
   effects.Driver.GetAwaiter().GetResult();NyRuntime.Cancellation.Value=default;
   Console.WriteLine(JsonSerializer.Serialize(new {id=c.GetProperty("id").GetString(),mode,effects.MaxActive,effects.Opened,effects.Closed,response}));
   }
  }
 }
}
