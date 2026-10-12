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
 readonly string name;
 readonly ConcurrentDictionary<long,ManualResetEventSlim> providerEntered=new();
 readonly ConcurrentDictionary<long,ManualResetEventSlim> snapshotEntered=new();
 readonly ConcurrentDictionary<long,ManualResetEventSlim> snapshotRelease=new();
 readonly ConcurrentDictionary<long,ManualResetEventSlim> workerRelease=new();
 readonly ConcurrentDictionary<long,CancellationToken> providerTokens=new();
 readonly ManualResetEventSlim checkEntered=new(false),checkRelease=new(false);
 readonly CountdownEvent bothProviders=new(2);
 readonly ManualResetEventSlim callerEntered=new(false);
 readonly ConcurrentQueue<object> calls=new();
 public int Active,MaxActive,Opened,Closed;
 public Effects(JsonElement scenario){this.scenario=scenario;name=scenario.GetProperty("id").GetString()!;}
 static ManualResetEventSlim Cell(ConcurrentDictionary<long,ManualResetEventSlim> map,long id)=>map.GetOrAdd(id,_=>new(false));
 public object[] Calls=>calls.ToArray();
 void Log(string op,params (string Name,object Value)[] args)=>calls.Enqueue(new {operation=op,arguments=args.Select(a=>new {parameter=a.Name,value=new {value=a.Value}}).ToArray()});
 static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
 static void Wait(ManualResetEventSlim cell,string label){Check(cell.Wait(TimeSpan.FromSeconds(5)),"barrier timeout: "+label);}
 string Kind(long id)=>scenario.GetProperty("providers").TryGetProperty(id.ToString(),out var v)?v.GetString()!:"hold";
 public NyResult<NyUnit,NyStr> Open(){Log("open");Opened++;return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyUnit Close(NyUnit handle){Log("close");Closed++;Check(Active==0,"provider active after scope join");return NyUnit.Value;}
 public NyResult<NyUnit,NyStr> WorkspaceCreate(NyStr workspace,NyStr basis){Log("workspaceCreate",("workspace",workspace.ToString()),("base",basis.ToString()));return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyResult<NyUnit,NyStr> WorkspaceDiscard(NyStr workspace){Log("workspaceDiscard",("workspace",workspace.ToString()));return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 sealed class ExpectedProviderError:Exception{}
 public NyResult<NyStr,NyStr> Provider(long id,NyStr workspace,long previous)
 {
  Log("provider",("id",id.ToString()),("workspace",workspace.ToString()),("previous",previous.ToString()));
  int active=Interlocked.Increment(ref Active),old;
  while(active>(old=Volatile.Read(ref MaxActive))&&Interlocked.CompareExchange(ref MaxActive,active,old)!=old){}
  providerTokens[id]=NyRuntime.Cancellation.Value;
  Cell(providerEntered,id).Set();
  bool overlap=name is "overlap-continuation" or "finish-active";
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
 public NyUnit MemberClose(long id){Log("memberClose",("id",id.ToString()));return NyUnit.Value;}
 public NyResult<NyStr,NyStr> Snapshot(long id,NyStr workspace)
 {
  Log("snapshot",("id",id.ToString()),("workspace",workspace.ToString()));
  Cell(snapshotEntered,id).Set();
  if((name=="overlap-continuation"&&id is 1 or 3)||((name is "stop-snapshot" or "finish-snapshot")&&id==1)){
   Wait(Cell(snapshotRelease,id),"snapshot release "+id);
   Check(!NyRuntime.CancellationRequested,"stop interrupted an already determined completion");
  }
  return name=="snapshot-error"&&id==1?NyResult<NyStr,NyStr>.Err("snapshot failed"):NyResult<NyStr,NyStr>.Ok("commit"+id);
 }
 public NyStr Checks(long id,NyStr commit){Log("checks",("id",id.ToString()),("commit",commit.ToString()));
  if(name=="checks-gated"&&id==1){checkEntered.Set();Wait(checkRelease,"checks release");Check(!NyRuntime.CancellationRequested,"stop cancelled determined completion during checks");}
  return name=="checks-failed"&&id==1?"failed":name=="checks-unavailable"&&id==1?"unavailable":"passed";}
 public NyUnit Gate(NyStr raw)
 {
  string label=raw.ToString();Log("gate",("label",label));
  if(label=="overlap")Check(bothProviders.Wait(TimeSpan.FromSeconds(5)),"overlap not reached");
  else if(label.StartsWith("provider"))Wait(Cell(providerEntered,long.Parse(label[8..])),label);
  else if(label.StartsWith("snapshot"))Wait(Cell(snapshotEntered,long.Parse(label[8..])),label);
  else if(label.StartsWith("release"))Cell(snapshotRelease,long.Parse(label[7..])).Set();
  else if(label=="checks1")Wait(checkEntered,label);
  else if(label.StartsWith("stop-join:")){
   long id=long.Parse(label[10..]);Cell(workerRelease,id).Set();
   if(id==1&&(name is "stop-snapshot" or "finish-snapshot"))Cell(snapshotRelease,1).Set();
   if(id==1&&name=="checks-gated")checkRelease.Set();
  }
  else if(label.StartsWith("worker-entry:")&&name=="stop-before-start")Wait(Cell(workerRelease,long.Parse(label[13..])),label);
  else if(label=="caller-wait-entered")callerEntered.Set();
  else if(label=="await-caller-wait"){Wait(callerEntered,label);Wait(Cell(providerEntered,1),"caller member started");}
  return NyUnit.Value;
 }
 public NyUnit Reply(NyStr label,NyStr record){
  if((name=="stop-during-run"&&label.ToString()=="timeout")||label.ToString()=="member-alive")
   Check(providerTokens.TryGetValue(1,out var token)&&!token.IsCancellationRequested,"caller wait cancelled its Member");
  Log("reply",("label",label.ToString()),("record",record.ToString()));return NyUnit.Value;
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
   var effects=new Effects(c);
   object response;
   try {
    NyResult<NyUnit,NyStr> result;
    if(c.GetProperty("id").GetString()=="caller-cancellation-gap")result=NyFunctions.@@CALLER_RUN@@(@@CALLER_HOST@@:effects);
    else {
     var requests=c.GetProperty("script").EnumerateArray().Select(r=>new @@REQUEST@@(@@REQUESTARGS@@)).ToArray();
     result=NyFunctions.@@RUN@@(@@RUN_HOST@@:effects,@@RUN_REQUESTS@@:NyList<@@REQUEST@@>.Create(requests));
    }
    if(!result.IsOk)throw new Exception("unexpected graph Err");
    response=new {ok=true,result=new {calls=effects.Calls}};
   }catch(Exception error){response=new {ok=false,error=new {code="RunRuntimeFailure",message=error.Message,calls=effects.Calls}};}
   Console.WriteLine(JsonSerializer.Serialize(new {id=c.GetProperty("id").GetString(),effects.MaxActive,effects.Opened,effects.Closed,response}));
  }
 }
}
