using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using Nyrwvy.Generated;

// Effects, narrowly located exception translation, and barriers only.
sealed class Effects : @@HOST@@, @@TAGGED@@
{
 readonly JsonElement scenario;
 readonly string name;
 readonly CancellationTokenSource root;
 readonly ManualResetEventSlim[] released={new(false),new(false)};
 readonly CountdownEvent entered=new(2);
 readonly ManualResetEventSlim monitorEntered=new(false),callerEntered=new(false),callerObserved=new(false),callerRelease=new(false),snapshotEntered=new(false),snapshotRelease=new(false);
 readonly ConcurrentQueue<object> calls=new();
 readonly ConcurrentDictionary<long,CancellationToken> tokens=new();
 readonly ConcurrentDictionary<long,int> leases=new();
 public int Active,MaxActive,Opened,Closed,MonitorPublished;
 public Effects(JsonElement scenario,CancellationTokenSource root){this.scenario=scenario;this.root=root;name=scenario.GetProperty("id").GetString()!;}
 public object[] Calls=>calls.ToArray();
 static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
 static void Wait(ManualResetEventSlim gate,string label){Check(gate.Wait(TimeSpan.FromSeconds(5)),"barrier timeout: "+label);}
 void Log(string op,params (string Name,object Value)[] args)=>calls.Enqueue(new {operation=op,arguments=args.Select(a=>new {parameter=a.Name,value=new {value=a.Value}}).ToArray()});
 public NyResult<NyUnit,NyStr> Open(){Log("open");Opened++;return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyUnit Close(NyUnit handle){Log("close");Closed++;Check(Active==0,"provider outlived scope");Check(leases.Count==2&&leases.Values.All(n=>n==1),"lease cleanup not exactly once");return NyUnit.Value;}
 public NyResult<NyUnit,NyStr> WorkspaceCreate(NyStr workspace,NyStr basis){Log("workspaceCreate",("workspace",workspace.ToString()),("base",basis.ToString()));return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyResult<NyUnit,NyStr> WorkspaceDiscard(NyStr workspace){throw new Exception("workspace discard is not authorized by this probe");}
 public NyResult<NyStr,NyStr> Provider(long id,NyStr workspace,long previous){throw new Exception("legacy provider must not run in follow-up");}
 public NyUnit MemberClose(long id){Log("memberClose",("id",id.ToString()));Check(leases.AddOrUpdate(id,1,(_,n)=>n+1)==1,"duplicate lease close");if(name=="cleanup-fault"&&id==1)throw new InvalidOperationException("cleanup fault");return NyUnit.Value;}
 public NyResult<NyStr,NyStr> Snapshot(long id,NyStr workspace){Log("snapshot",("id",id.ToString()),("workspace",workspace.ToString()));if(name=="snapshot-timeout"&&id==1){snapshotEntered.Set();Wait(snapshotRelease,"snapshot release");Check(!NyRuntime.CancellationRequested,"observer cancellation interrupted snapshot");}return NyResult<NyStr,NyStr>.Ok("commit"+id);}
 public NyStr Checks(long id,NyStr commit){Log("checks",("id",id.ToString()),("commit",commit.ToString()));return "passed";}
 public NyUnit Gate(NyStr raw){string label=raw.ToString();Log("gate",("label",label));
  switch(label){
   case "members-ready":Check(entered.Wait(TimeSpan.FromSeconds(5)),"members did not overlap");break;
   case "monitor-entered":monitorEntered.Set();break;
   case "caller-entered":callerEntered.Set();break;
   case "waiter-ready":Wait(monitorEntered,label);Wait(callerEntered,label);break;
   case "monitor-published":Interlocked.Increment(ref MonitorPublished);break;
   case "caller-observed":callerObserved.Set();if(name=="race-published-cancel")Wait(callerRelease,label);break;
   case "observation-ready":Wait(callerObserved,label);break;
   case "release-caller":callerRelease.Set();break;
   case "release-first":released[0].Set();break;
   case "release-peer":released[1].Set();break;
   case "release-all":released[0].Set();released[1].Set();break;
   case "snapshot1":Wait(snapshotEntered,label);break;
   case "release-snapshot":snapshotRelease.Set();break;
   case "global-arm":if(name=="global-external")root.Cancel();break;
  }return NyUnit.Value;}
 public NyUnit Reply(NyStr label,NyStr record){
  if(label.ToString()=="observed")Check(tokens.Count==2&&tokens.Values.All(t=>!t.IsCancellationRequested),"caller/timeout cancelled Member");
  Log("reply",("label",label.ToString()),("record",record.ToString()));return NyUnit.Value;}
 sealed class ExpectedProviderError:Exception{public ExpectedProviderError():base("deadline") {}}
 static @@OBSERVATION@@ Data(string kind,string cause="")=>new(@@OBS_KIND@@:kind,@@OBS_CAUSE@@:cause);
 public NyResult<@@OBSERVATION@@,NyStr> TaggedRun(long id,NyStr workspace)
 {
  Log("run",("id",id.ToString()),("workspace",workspace.ToString()));
  int active=Interlocked.Increment(ref Active),old;
  while(active>(old=Volatile.Read(ref MaxActive))&&Interlocked.CompareExchange(ref MaxActive,active,old)!=old){}
  var token=NyRuntime.Cancellation.Value;tokens[id]=token;
  try{
   entered.Signal();Check(entered.Wait(TimeSpan.FromSeconds(5)),"provider overlap barrier");
   // This fault is deliberately outside the translated provider-run boundary.
   if(name=="outside-provider-fault"&&id==1){released[0].Wait(token);throw new InvalidOperationException("outside provider run");}
   try{
    released[(int)id-1].Wait(token);
    if(name=="foreign-cancellation"&&id==1)throw new OperationCanceledException(new CancellationToken());
    string kind=scenario.GetProperty("providers")[(int)id-1].GetString()!;
    if(kind=="expected")throw new ExpectedProviderError();
    if(kind=="unexpected")throw new InvalidOperationException("unexpected host fault");
    return NyResult<@@OBSERVATION@@,NyStr>.Ok(Data("completed"));
   }catch(OperationCanceledException e)when(token.IsCancellationRequested&&e.CancellationToken==token){
    Log("provider-cancellation",("id",id.ToString()));return NyResult<@@OBSERVATION@@,NyStr>.Err("cancelled");
   }catch(OperationCanceledException){throw;}
    catch(ExpectedProviderError e){return NyResult<@@OBSERVATION@@,NyStr>.Ok(Data("expected-provider-error",e.Message));}
    catch(Exception e){return NyResult<@@OBSERVATION@@,NyStr>.Ok(Data("unexpected-provider-error",e.Message));}
  }finally{Interlocked.Decrement(ref Active);}
 }
 @@METHODS@@
}
static class Program
{
 public static void Main(string[] args){ThreadPool.SetMinThreads(16,16);using var data=JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));
  foreach(var c in data.RootElement.EnumerateArray()){
   using var root=new CancellationTokenSource();var effects=new Effects(c,root);object response;NyRuntime.Cancellation.Value=root.Token;
   try{var result=NyFunctions.@@RUN@@(@@RUN_HOST@@:effects,@@RUN_PROVIDER@@:effects,@@RUN_MODE@@:(NyStr)c.GetProperty("mode").GetString()!,@@RUN_LIMIT@@:c.GetProperty("limit").GetInt64());response=new {ok=true,result=new {actual=new {isOk=result.IsOk},calls=effects.Calls}};}
   catch(Exception error){response=new {ok=false,error=new {code="RunRuntimeFailure",message=error.Message,calls=effects.Calls}};}
   finally{NyRuntime.Cancellation.Value=default;}
   Console.WriteLine(JsonSerializer.Serialize(new {id=c.GetProperty("id").GetString(),effects.MaxActive,effects.Opened,effects.Closed,effects.MonitorPublished,response}));
  }
 }
}
