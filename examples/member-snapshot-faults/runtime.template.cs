using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using Nyrwvy.Generated;

// One synthetic Member. Barriers own effects only, never graph state.
sealed class Effects : @@HOST@@
{
 readonly JsonElement scenario; readonly CancellationTokenSource root; readonly string mode;
 readonly ManualResetEventSlim parked=new(false),injected=new(false),released=new(false),provider=new(false),providerCancelled=new(false),snapshot=new(false),checkedMember=new(false),admitted=new(false),published=new(false),closeRelease=new(false);
 readonly ConcurrentQueue<object> calls=new();
 readonly ConcurrentDictionary<long,int> closedMembers=new();
 public System.Threading.Tasks.Task Driver {get;private set;}=System.Threading.Tasks.Task.CompletedTask;
 public int Active,MaxActive,Opened,Closed,leases;
 string Boundary=>scenario.GetProperty("boundary").GetString()!;
 string Order=>scenario.GetProperty("order").GetString()!;
 bool Flag(string key)=>scenario.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
 string Setting(string key)=>scenario.TryGetProperty(key,out var v)?v.GetString()!:"";
 public Effects(JsonElement scenario,CancellationTokenSource root,string mode){this.scenario=scenario;this.root=root;this.mode=mode;if(mode=="external"&&!scenario.TryGetProperty("function",out _))Driver=System.Threading.Tasks.Task.Run(()=>{Wait(parked);root.Cancel();injected.Set();});}
 static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
 static void Wait(ManualResetEventSlim cell){Check(cell.Wait(TimeSpan.FromSeconds(5)),"barrier timeout");}
 void Log(string op,params (string Name,object Value)[] args)=>calls.Enqueue(new {operation=op,arguments=args.Select(a=>new {parameter=a.Name,value=new {value=a.Value}}).ToArray()});
 public object[] Calls=>calls.ToArray();
 public NyResult<NyUnit,NyStr> Open(){Log("open");Opened++;return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyUnit Close(NyUnit handle){Log("close");Closed++;Check(Active==0,"provider not drained");Check(leases==1,"cleanup once");if(Flag("releaseFault"))throw new InvalidOperationException("release fault");return NyUnit.Value;}
 public NyResult<NyUnit,NyStr> WorkspaceCreate(NyStr workspace,NyStr basis){Log("workspaceCreate",("workspace",workspace.ToString()),("base",basis.ToString()));return NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);}
 public NyResult<NyUnit,NyStr> WorkspaceDiscard(NyStr workspace)=>NyResult<NyUnit,NyStr>.Ok(NyUnit.Value);
 public NyResult<NyList<NyStr>,NyStr> Step(NyStr operation,NyStr patch)=>throw new Exception("unused mutation effect");
 public NyResult<NyStr,NyStr> Provider(long id,NyStr workspace,long previous){Log("provider",("id",id.ToString()));Interlocked.Increment(ref Active);MaxActive=1;provider.Set();try{if(Boundary=="provider"){Check(NyRuntime.Cancellation.Value.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)),"provider cancellation timeout");providerCancelled.Set();if(Flag("completedAfterCancel"))return NyResult<NyStr,NyStr>.Ok("completed");return NyResult<NyStr,NyStr>.Err("cancelled");}return Flag("providerFailure")?NyResult<NyStr,NyStr>.Err("provider failure"):NyResult<NyStr,NyStr>.Ok("completed");}finally{Interlocked.Decrement(ref Active);}}
 public NyUnit MemberClose(long id){Check(closedMembers.AddOrUpdate(id,1,(_,n)=>n+1)==1,"duplicate Member cleanup");Interlocked.Increment(ref leases);Log("memberClose",("id",id.ToString()));return NyUnit.Value;}
 public NyResult<NyStr,NyStr> Snapshot(long id,NyStr workspace){Log("snapshot",("id",id.ToString()));snapshot.Set();if(Boundary=="snapshot"&&id==1)Wait(released);if(Setting("snapshotFault")=="raw")throw new InvalidOperationException("raw snapshot fault");if(Setting("snapshotFault")=="oce")throw new OperationCanceledException("explicit snapshot cancellation");return Setting("snapshotFault")=="typed"?NyResult<NyStr,NyStr>.Err("snapshot failure"):NyResult<NyStr,NyStr>.Ok("commit"+id);}
 public NyStr Checks(long id,NyStr commit){Log("checks",("id",id.ToString()));checkedMember.Set();if(Boundary=="checks")Wait(released);if(Flag("checksFault"))throw new InvalidOperationException("raw checks fault");return "passed";}
 public NyUnit Reply(NyStr label,NyStr record){Log("reply",("label",label.ToString()),("record",record.ToString()));if(label.ToString()=="@close-publication")published.Set();return NyUnit.Value;}
 public NyUnit Gate(NyStr raw){string label=raw.ToString();Log("gate",("label",label));
  if(label=="provider1")Wait(provider);
  else if(label=="snapshot1")Wait(snapshot);
  else if(label=="checks1")Wait(checkedMember);
  else if(label=="enter:closing"){admitted.Set();Wait(closeRelease);}
  else if(label=="close-admitted")Wait(admitted);
  else if(label=="owner-park"){parked.Set();Check(NyRuntime.Cancellation.Value.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)),"owner cancellation timeout");}
  else if(label=="probe-ready"){Wait(parked);if(mode=="external")Wait(injected);}
  else if(label=="probe-cancelled"&&Boundary=="provider")Wait(providerCancelled);
  else if(label=="probe-release"||label=="release-effect"){closeRelease.Set();if(Order=="admitted")Wait(published);released.Set();}
  return NyUnit.Value;
 }
 @@METHODS@@
}
static class Program
{
 static object ObserveCancelledMember()
 {
  using var sample=JsonDocument.Parse("{\"boundary\":\"snapshot\",\"order\":\"none\",\"snapshotFault\":\"oce\",\"function\":\"concurrentRun\"}");
  using var root=new CancellationTokenSource();var effects=new Effects(sample.RootElement,root,"task");
  var scope=new NyTaskScope(root.Token);var phase=scope.CreateOneShot<NyStr>();var commit=scope.CreateOneShot<NyStr>();var closing=scope.CreateOneShot<NyUnit>();
  effects.Open();effects.Gate("release-effect");
  var member=scope.Start(()=>NyFunctions.@@SETTLEMENT@@(effects,1,"w1",phase,"completed",closing,commit,NyUnit.Value));
  bool finalReturned=false;Exception? error=null;try{member.Running.GetAwaiter().GetResult();finalReturned=true;}catch(Exception fault){error=fault;}
  var retained=phase.Wait(NyOption<long>.Some(0));var published=commit.Wait(NyOption<long>.Some(0));
  if(!member.Running.IsFaulted||member.Running.IsCanceled||error is not OperationCanceledException||!retained.HasValue||retained.Value.ToString()!="completed"||published.HasValue||finalReturned)throw new Exception("cancelled Member observation");
  Exception? scopeFault=null;try{scope.Close(false);}catch(Exception fault){scopeFault=fault;}finally{effects.Close(NyUnit.Value);}
  if(!ReferenceEquals(error,scopeFault))throw new Exception("scope retains original OCE");
  return new {taskCancelled=member.Running.IsCanceled,taskFaulted=member.Running.IsFaulted,cancellationException=error is OperationCanceledException,scopePreservesIdentity=ReferenceEquals(error,scopeFault),phase=retained.Value.ToString(),commitPublished=published.HasValue,finalReturned,drained=member.Running.IsCompleted&&effects.Active==0,released=effects.Closed};
 }
 public static void Main(string[] args){ThreadPool.SetMinThreads(16,16);using var data=JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));foreach(var c in data.RootElement.EnumerateArray())foreach(var mode in new[]{"task","external"}){
  using var root=new CancellationTokenSource();NyRuntime.Cancellation.Value=root.Token;var effects=new Effects(c,root,mode);object response;
  try{var requests=c.GetProperty("script").EnumerateArray().Select(r=>new @@REQUEST@@(@@REQUESTARGS@@)).ToArray();var result=c.TryGetProperty("function",out _)?NyFunctions.@@CALLER_RUN@@(@@CALLER_HOST@@:effects,@@CALLER_REQUESTS@@:NyList<@@REQUEST@@>.Create(requests)):NyFunctions.@@RUN@@(@@RUN_HOST@@:effects,@@RUN_REQUESTS@@:NyList<@@REQUEST@@>.Create(requests));response=new {ok=true,result=new {actual=new {isOk=result.IsOk,error=result.IsOk?"":result.ErrorValue.ToString()},calls=effects.Calls}};}
  catch(Exception error){response=new {ok=false,error=new {message=error.Message,exceptionType=error.GetType().FullName,calls=effects.Calls}};}
  effects.Driver.GetAwaiter().GetResult();NyRuntime.Cancellation.Value=default;Console.WriteLine(JsonSerializer.Serialize(new {id=c.GetProperty("id").GetString(),mode,effects.MaxActive,effects.Opened,effects.Closed,response,memberObservation=c.TryGetProperty("snapshotFault",out var fault)&&fault.GetString()=="oce"?ObserveCancelledMember():null}));
 }}
}
