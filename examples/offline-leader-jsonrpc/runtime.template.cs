using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using Nyrwvy.Generated;

// Synthetic provider/Git effects and deterministic test gates. The host owns
// no Team registry, admission lock, task handle, ledger or session controller.
sealed class Effects : @@HOST@@
{
 readonly JsonElement scenario;
 readonly AsyncLocal<string> requestLabel=new();
 readonly ConcurrentQueue<object> calls=new();
 readonly ConcurrentDictionary<string,ManualResetEventSlim> started=new();
 readonly ConcurrentDictionary<string,bool> acquired=new();
 readonly ConcurrentDictionary<long,ManualResetEventSlim> workers=new(),providers=new();
 readonly ManualResetEventSlim snapshotEntered=new(false),snapshotReleased=new(false),commitEntered=new(false),commitReleased=new(false),closeEntered=new(false),closeReleased=new(false),spawnEntered=new(false),spawnReleased=new(false),faultReleased=new(false),cleanupEntered=new(false),cleanupReleased=new(false);
 readonly ConcurrentDictionary<string,int> counts=new();
 public int Active,MaxActive,Opened,Closed;
 public readonly Exception BodyFault=new InvalidOperationException("ordinary worker fault"),CleanupFault,ReleaseFault;
 public Exception? CancellationFault;
 string Setting(string key)=>scenario.TryGetProperty(key,out var v)?v.GetString()!:"";
 public Effects(JsonElement scenario){this.scenario=scenario;CleanupFault=Setting("cleanupFault")=="OCE"?new OperationCanceledException("cleanup OCE"):new InvalidOperationException("cleanup fault");ReleaseFault=Setting("releaseFault")=="OCE"?new OperationCanceledException("release OCE"):new InvalidOperationException("release fault");}
 public object[] Calls=>calls.ToArray();
 static ManualResetEventSlim Cell(ConcurrentDictionary<long,ManualResetEventSlim> map,long id)=>map.GetOrAdd(id,_=>new(false));
 void Log(string op,params (string Name,object Value)[] args)=>calls.Enqueue(new {operation=op,request=requestLabel.Value,arguments=args.Select(a=>new {parameter=a.Name,value=new {value=a.Value}}).ToArray()});
 public static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
 static void Wait(ManualResetEventSlim cell,string label)=>Check(cell.Wait(TimeSpan.FromSeconds(10)),"barrier timeout: "+label);
 static void Cancelled(string label){Check(NyRuntime.Cancellation.Value.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)),"barrier timeout: cancellation "+label);}
 public NyResult<NyUnit,NyStr> Open(){Log("open");Opened++;return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyUnit Close(NyUnit handle){Check(Active==0,"release requires joined provider workers");Log("close");Closed++;if(Setting("releaseFault")!="")throw ReleaseFault;return NyUnit.Value;}
 public NyResult<NyUnit,NyStr> WorkspaceCreate(NyStr workspace,NyStr basis){Log("workspaceCreate",("workspace",workspace.ToString()),("base",basis.ToString()));if(requestLabel.Value=="blocker"){spawnEntered.Set();Wait(spawnReleased,"spawn release");}return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyResult<NyUnit,NyStr> WorkspaceDiscard(NyStr workspace){Log("workspaceDiscard",("workspace",workspace.ToString()));return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyResult<NyStr,NyStr> Provider(long id,NyStr workspace,long previous)
 {
  Log("provider",("id",id.ToString()),("workspace",workspace.ToString()),("previous",previous.ToString()));
  int active=Interlocked.Increment(ref Active),old;
  while(active>(old=Volatile.Read(ref MaxActive))&&Interlocked.CompareExchange(ref MaxActive,active,old)!=old){}
  Cell(providers,id).Set();
  try{
   string kind=scenario.GetProperty("providers").TryGetProperty(id.ToString(),out var v)?v.GetString()!:"hold";
   if(kind=="fault")throw BodyFault;
   if(kind=="hold"){Cancelled("provider "+id);return NyResult<NyStr,NyStr>.Err("cancelled");}
   if(id==1&&Setting("kind")=="session")Wait(Cell(providers,2),"second turn provider overlap");
   return NyResult<NyStr,NyStr>.Ok("completed");
  }finally{Interlocked.Decrement(ref Active);}
 }
 public NyUnit MemberClose(long id){Log("memberClose",("id",id.ToString()));return NyUnit.Value;}
 public NyResult<NyStr,NyStr> Snapshot(long id,NyStr workspace){Log("snapshot",("id",id.ToString()),("workspace",workspace.ToString()));snapshotEntered.Set();Wait(snapshotReleased,"snapshot release");Check(!NyRuntime.CancellationRequested,"determined Member snapshot stays protected");return NyResult<NyStr,NyStr>.Ok("commit"+id);}
 public NyStr Checks(long id,NyStr commit){Log("checks",("id",id.ToString()),("commit",commit.ToString()));return "passed";}
 public NyResult<NyList<NyStr>,NyStr> Step(NyStr operation,NyStr patch)
 {
  string phase=operation.ToString();Log("step",("operation",phase),("patch",patch.ToString()));
  counts.AddOrUpdate(phase,1,(_,n)=>n+1);
  if(phase=="commit"){commitEntered.Set();Wait(commitReleased,"integration release");}
  Check(!NyRuntime.CancellationRequested,"protected synthetic Git tail sees a clear token");
  string[] values=phase switch {"paths-member"=>new[]{"a","b"},"paths-leader"=>new[]{"c"},"patch"=>new[]{Setting("patch")},"head"=>new[]{"integrated-head"},_=>Array.Empty<string>()};
  return NyResult<NyList<NyStr>,NyStr>.Ok(NyList<NyStr>.Create(values.Select(x=>(NyStr)x).ToArray()));
 }
 public NyUnit Reply(NyStr label,NyStr record){Log("reply",("label",label.ToString()),("record",record.ToString()));return NyUnit.Value;}
 public NyUnit Gate(NyStr raw)
 {
  string label=raw.ToString();
  if(label.StartsWith("request-start:"))requestLabel.Value=label[14..];
  if(label.StartsWith("enter:"))requestLabel.Value=label[6..];
  Log("gate",("label",label));
  if(label.StartsWith("request-start:")){
   string name=label[14..];started.GetOrAdd(name,_=>new(false)).Set();
   if(name=="cancelled"&&Setting("cancellation")=="throw"){Cancelled(name);try{NyRuntime.Cancellation.Value.ThrowIfCancellationRequested();}catch(OperationCanceledException error){CancellationFault=error;throw;}}
   if(name=="fault"){Wait(faultReleased,"fault release");throw BodyFault;}
  }
  else if(label.StartsWith("enter:"))acquired[label[6..]]=true;
  else if(label.StartsWith("queued:")){string name=label[7..];Wait(started.GetOrAdd(name,_=>new(false)),label);Check(!acquired.ContainsKey(name),"queued command acquired admission");}
  else if(label.StartsWith("command-cleanup:")){
   if(label=="command-cleanup:cancelled"&&Setting("cleanupFault")!=""){cleanupEntered.Set();Wait(cleanupReleased,"cleanup release");throw CleanupFault;}
  }
  else if(label.StartsWith("worker-entry:"))Check(WaitHandle.WaitAny(new[]{Cell(workers,long.Parse(label[13..])).WaitHandle,NyRuntime.Cancellation.Value.WaitHandle},TimeSpan.FromSeconds(10))!=WaitHandle.WaitTimeout,"barrier timeout: "+label);
  else if(label.StartsWith("provider")){long id=long.Parse(label[8..]);Cell(workers,id).Set();Wait(Cell(providers,id),label);}
  else if(label=="snapshot1")Wait(snapshotEntered,label);
  else if(label=="release1")snapshotReleased.Set();
  else if(label=="commit-held")Wait(commitEntered,label);
  else if(label=="commit-release")commitReleased.Set();
  else if(label=="close-held-worker"&&Setting("kind")=="session"){closeEntered.Set();Wait(closeReleased,"close release");}
  else if(label=="close-held")Wait(closeEntered,label);
  else if(label=="close-release")closeReleased.Set();
  else if(label=="spawn-held")Wait(spawnEntered,label);
  else if(label=="spawn-release")spawnReleased.Set();
  else if(label=="fault-release")faultReleased.Set();
  else if(label=="cleanup-held")Wait(cleanupEntered,label);
  else if(label=="cleanup-release")cleanupReleased.Set();
  else if(label.StartsWith("stop-join:"))Cell(workers,long.Parse(label[10..])).Set();
  return NyUnit.Value;
 }
 @@METHODS@@
}
static class Program
{
 static Exception Expected(Effects effects,string kind)=>kind switch {"boundary"=>effects.CancellationFault!,"cleanup-fault"=>effects.CleanupFault,"release-fault"=>effects.ReleaseFault,_=>effects.BodyFault};
 public static void Main(string[] args)
 {
  ThreadPool.SetMinThreads(16,16);
  using var data=JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));
  foreach(var c in data.RootElement.EnumerateArray()){
   foreach(bool wrongBoundary in c.GetProperty("kind").GetString() is "boundary" or "wire-control" or "parser"?new[]{false,true}:new[]{false}){
    var effects=new Effects(c);object response;bool identityPreserved=false,throwingStackPreserved=false;
    try{
     if(c.GetProperty("kind").GetString()=="parser"){
      var lines=NyList<NyStr>.Create(c.GetProperty("lines").EnumerateArray().Select(x=>(NyStr)x.GetString()!).ToArray());
      var result=NyFunctions.@@PREVIEW@@(@@PREVIEW_LINES@@:lines,@@PREVIEW_WRONG@@:wrongBoundary);
      response=new {ok=true,result=new {items=Enumerable.Range(0,(int)result.Length).Select(i=>result.ItemAt(i).Value.ToString()).ToArray(),calls=effects.Calls}};
     }else if(c.GetProperty("kind").GetString()=="boundary"){
      var result=NyFunctions.@@BOUNDARY@@(@@BOUNDARY_HOST@@:effects,@@BOUNDARY_WRONG@@:wrongBoundary);
      response=new {ok=true,result=new {actual=new {isOk=result.IsOk,value=result.IsOk?"unexpected state":result.ErrorValue.ToString()},calls=effects.Calls}};
     }else{
      var turns=c.GetProperty("turns").EnumerateArray().Select(t=>new @@TURN@@(@@TURNARGS@@)).ToArray();
      var result=NyFunctions.@@RUN@@(@@RUN_HOST@@:effects,@@RUN_TURNS@@:NyList<@@TURN@@>.Create(turns),@@RUN_WRONG@@:wrongBoundary);
      response=new {ok=true,result=new {actual=new {isOk=result.IsOk,value=result.IsOk?result.OkValue.ToString():result.ErrorValue.ToString()},calls=effects.Calls}};
     }
    }catch(Exception error){
     identityPreserved=ReferenceEquals(error,Expected(effects,c.GetProperty("kind").GetString()!));
     throwingStackPreserved=error.StackTrace?.Contains("Effects."+(c.GetProperty("kind").GetString()=="member-fault"?"Provider":c.GetProperty("kind").GetString()=="release-fault"?"Close":"Gate"))==true;
     response=new {ok=false,error=new {type=error.GetType().Name,message=error.Message,calls=effects.Calls}};
    }
    Console.WriteLine(JsonSerializer.Serialize(new {id=c.GetProperty("id").GetString(),wrongBoundary,identityPreserved,throwingStackPreserved,effects.MaxActive,effects.Opened,effects.Closed,response}));
   }
  }
 }
}
