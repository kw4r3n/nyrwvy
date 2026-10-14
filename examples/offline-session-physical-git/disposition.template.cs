 // Explicit bounded retain disposition. All original application records and
 // the launch fence remain byte-for-byte intact; this never supplies an outcome.
 sealed class GitJoin {
  public string Version {get;set;}="";public string Invocation {get;set;}="";public string Identity {get;set;}="";
  public string Repository {get;set;}="";public string RepositoryId {get;set;}="";public string LaunchDigest {get;set;}="";
  public string OwnerDigest {get;set;}="";public int OwnerPid {get;set;}public int ChildPid {get;set;}
  public string[] Args {get;set;}=Array.Empty<string>();public string InputDigest {get;set;}="";
  public int? ExitCode {get;set;}public string ExitSignal {get;set;}="";public bool InputClosed {get;set;}public bool OutputDrained {get;set;}
  public string OutputDigest {get;set;}="";public string ErrorDigest {get;set;}="";
 }
 GitJoin LoadJoin(GitChild child,string raw){
  var witness=JsonSerializer.Deserialize<GitJoin>(System.IO.File.ReadAllText(StoragePath("physical-join/v1/"+child.Invocation)))!;
  Check(child.Version=="2"&&child.Pid.HasValue&&child.OwnerDigest!=""&&witness.Version=="1"&&witness.Invocation==child.Invocation&&witness.Identity==child.Identity&&witness.Repository==child.Repository&&witness.RepositoryId==child.RepositoryId&&witness.LaunchDigest==DigestText(raw)&&witness.OwnerDigest==child.OwnerDigest&&witness.OwnerPid==child.Pid&&witness.ChildPid>0&&witness.Args.SequenceEqual(child.Args)&&witness.InputDigest==child.InputDigest&&witness.InputClosed&&witness.OutputDrained&&(witness.ExitCode.HasValue&&witness.ExitSignal==""||!witness.ExitCode.HasValue&&witness.ExitSignal=="SIGKILL"),"controlled child join witness mismatch");
  Check(System.Text.RegularExpressions.Regex.IsMatch(witness.OutputDigest,@"\A[0-9a-f]{64}\z")&&System.Text.RegularExpressions.Regex.IsMatch(witness.ErrorDigest,@"\A[0-9a-f]{64}\z"),"join output capture unavailable");return witness;
 }
 string OwnedGit(string[] args,string? input,bool trim,string launchKey="physical-child/v1"){
  string owner=System.IO.Path.GetFullPath(Setting("gitOwner"));
  Check(System.IO.File.Exists(owner)&&new System.IO.FileInfo(owner).LinkTarget==null,"controlled child owner unavailable");
  var child=new GitChild{Version="2",Invocation=Guid.NewGuid().ToString("N"),Identity=effectIdentity.Value??"",Repository=GitRoot,RepositoryId=Setting("fixtureId"),Args=args,InputDigest=DigestText(input??""),OwnerDigest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(owner))).ToLowerInvariant()};
  Check(child.Identity!="","owned mutation requires identity");
  Check(launchKey=="physical-child/v1"||System.Text.RegularExpressions.Regex.IsMatch(launchKey,@"\Aphysical-continuation-child/v1/[a-zA-Z0-9_-]{1,80}/[01]\z")&&!System.IO.File.Exists(StoragePath(launchKey)),"owned launch slot conflict");
  AtomicWrite(launchKey,JsonSerializer.Serialize(child));
  Observe(new {operation="physical-child-launch",child.Invocation,child.Identity,command=args[0],owner=true});Checkpoint("before-git-child-start");
  var start=new System.Diagnostics.ProcessStartInfo("node"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(owner);
  using var process=new System.Diagnostics.Process{StartInfo=start};Check(process.Start(),"controlled owner creation failed");child.Pid=process.Id;
  string raw=JsonSerializer.Serialize(child);AtomicWrite(launchKey,raw);
  int deadline=Setting("gitDeadlineMs")==""?10000:int.Parse(Setting("gitDeadlineMs"));Check(deadline>=250&&deadline<=10000,"bounded owned Git deadline");
  var output=GitOutput(process.StandardOutput);var error=GitOutput(process.StandardError);
  process.StandardInput.Write(JsonSerializer.Serialize(new {storage=StorageRoot,repository=GitRoot,invocation=child.Invocation,launchKey,input=input??"",deadlineMs=deadline}));process.StandardInput.Close();
  Observe(new {operation="physical-git-owner-start",child.Invocation,pid=process.Id,command=args[0]});
  // No kill of the owner on launcher loss: it owns root exit and both drains.
  System.Threading.Tasks.Task.WhenAll(output,error,process.WaitForExitAsync()).WaitAsync(TimeSpan.FromMilliseconds(deadline+4000)).GetAwaiter().GetResult();
  Check(process.ExitCode==0,"controlled owner join unavailable");var witness=LoadJoin(child,raw);
  Check(witness.OutputDigest==DigestText(output.Result)&&witness.ErrorDigest==DigestText(error.Result),"controlled owner capture mismatch");
  Check(witness.ExitCode.HasValue,"owned Git child interrupted; inspect separately");
  child.State="quiescent";child.ExitCode=witness.ExitCode;child.InputClosed=true;child.OutputDrained=true;AtomicWrite(launchKey,JsonSerializer.Serialize(child));
  Observe(new {operation="physical-child-quiescent",child.Invocation,child.Identity,child.Pid,child.ExitCode,owner=true});
  Observe(new {operation="physical-git-exit",command=args[0],args,mutation=true,exitCode=witness.ExitCode,childExited=true});
  Check(witness.ExitCode==0,"Git command failed: "+args[0]+": "+error.GetAwaiter().GetResult());return trim?output.Result.TrimEnd('\r','\n'):output.Result;
 }
 bool dispositionInspection;
 void RunDisposition(){
  try{Console.WriteLine(RetainDisposition(System.IO.File.ReadAllText(Setting("dispositionRequest"))));}
  catch(Exception error){Console.WriteLine("err:unresolved");Observe(new {operation="physical-disposition-rejected",reason=error.Message});}
  Environment.Exit(0);
 }
 string OwnedTail(GitPlan plan,GitState current){
  if(Classify(plan,current)=="applied")return "applied";
  if(JsonSerializer.Serialize(current)==JsonSerializer.Serialize(plan.Before))return "absent";
  string objects=string.Join("\n",(plan.Before.Objects+"\n"+plan.Commit+" commit").Split('\n').OrderBy(x=>x,StringComparer.Ordinal));
  bool rest=current.Symbolic==plan.Before.Symbolic&&current.Objects==objects&&current.IndexBytes==plan.Before.IndexBytes&&current.IndexEntries==plan.Before.IndexEntries&&current.Worktree==plan.Before.Worktree;
  if(rest&&current.Head==plan.Before.Head&&current.Refs==plan.Before.Refs)return "object-only";
  if(rest&&current.Head==plan.Commit&&current.Refs==plan.Before.Refs.Replace(plan.Before.Head+" refs/heads/main",plan.Commit+" refs/heads/main",StringComparison.Ordinal))return "ref-only";
  return "unknown";
 }
 public string RetainDisposition(string request){
  using var document=JsonDocument.Parse(request);var r=document.RootElement;
  string id=r.GetProperty("id").GetString()!,authorization=r.GetProperty("authorization").GetString()!,invocation=r.GetProperty("invocation").GetString()!,identity=r.GetProperty("identity").GetString()!;
  Check(System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-zA-Z0-9_-]{1,80}\z")&&id!=identity&&authorization!=""&&Setting("dispositionAuthorization")!=""&&authorization==Setting("dispositionAuthorization"),"disposition identity/authorization");
  Check(r.GetProperty("action").GetString()=="retain-unresolved"&&r.GetProperty("repository").GetString()==GitRoot&&r.GetProperty("repositoryId").GetString()==Setting("fixtureId")&&r.GetProperty("launchDigest").GetString()!=""&&r.GetProperty("planDigest").GetString()!=""&&r.GetProperty("expectedState").GetString()!="", "disposition payload");
  Check(Setting("worktreeModeControl")==""&&Setting("physicalControl")=="","disposition requires enabled physical evidence");
  string raw=System.IO.File.ReadAllText(StoragePath("physical-child/v1"));var child=JsonSerializer.Deserialize<GitChild>(raw)!;
  string planRaw=System.IO.File.ReadAllText(StoragePath(PlanKey(identity)));
  Check(child.Invocation==invocation&&child.Identity==identity&&r.GetProperty("launchDigest").GetString()==DigestText(raw)&&r.GetProperty("planDigest").GetString()==DigestText(planRaw),"disposition launch/plan correlation");
  string witnessDigest="";
  if(child.State=="active"){
   LoadJoin(child,raw);witnessDigest=DigestText(System.IO.File.ReadAllText(StoragePath("physical-join/v1/"+invocation)));
  }else{Check(child.State=="quiescent","disposition child state");GitChildGuard();}
  Check(child.Repository==GitRoot&&child.RepositoryId==Setting("fixtureId"),"disposition child repository");
  string requestDigest=DigestText(request),key="physical-disposition/v1/"+id,file=StoragePath(key);
  // Only this explicit, read-only scope may inspect an active witnessed launch.
  // Every inspected Git command still validates the original args and plan.
  dispositionInspection=true;
  try{
   Check(Setting("evidence")!="unavailable","disposition inspection unavailable");var plan=LoadPlan(identity);var current=PhysicalState();
   string state=OwnedTail(plan,current);Check(state!="unknown"&&state==r.GetProperty("expectedState").GetString(),"disposition physical state mismatch");
   foreach(string phase in new[]{"commit","head","save"}){
    string receipt=StoragePath("physical-receipt/"+identity+"/"+phase);if(!System.IO.File.Exists(receipt))continue;
    using var proof=JsonDocument.Parse(System.IO.File.ReadAllText(receipt));var row=proof.RootElement;
    bool allowed=phase=="save"?state=="applied"&&child.Args[0]=="read-tree":(state=="ref-only"||state=="applied")&&(child.Args[0]=="update-ref"||child.Args[0]=="read-tree");
    Check(allowed&&row.GetProperty("phase").GetString()==phase&&row.GetProperty("identity").GetString()==identity&&row.GetProperty("Commit").GetString()==plan.Commit,"disposition side receipt mismatch");
   }
   if(child.Args[0]=="commit-tree")Check(state is "absent" or "object-only","disposition command/tail mismatch");
   if(child.Args[0]=="update-ref")Check(state is "object-only" or "ref-only","disposition command/tail mismatch");
   if(child.Args[0]=="read-tree")Check(state is "ref-only" or "applied","disposition command/tail mismatch");
   string result="ok:retained-unresolved:"+state;
   string stateDigest=DigestText(JsonSerializer.Serialize(current));
   if(System.IO.File.Exists(file)){
    using var prior=JsonDocument.Parse(System.IO.File.ReadAllText(file));var p=prior.RootElement;
    Check(p.GetProperty("version").GetString()=="1"&&p.GetProperty("id").GetString()==id&&p.GetProperty("requestDigest").GetString()==requestDigest&&p.GetProperty("invocation").GetString()==invocation&&p.GetProperty("identity").GetString()==identity&&p.GetProperty("launchDigest").GetString()==DigestText(raw)&&p.GetProperty("planDigest").GetString()==DigestText(planRaw)&&p.GetProperty("repository").GetString()==GitRoot&&p.GetProperty("repositoryId").GetString()==Setting("fixtureId")&&p.GetProperty("action").GetString()=="retain-unresolved"&&p.GetProperty("state").GetString()==state&&p.GetProperty("witnessDigest").GetString()==witnessDigest&&p.GetProperty("stateDigest").GetString()==stateDigest&&p.GetProperty("result").GetString()==result&&p.GetProperty("originalOutcome").GetString()=="not-inferred","conflicting retained disposition");return result;
   }
   Checkpoint("before-disposition-publication");
   AtomicWrite(key,JsonSerializer.Serialize(new {version="1",id,requestDigest,invocation,identity,launchDigest=DigestText(raw),planDigest=DigestText(planRaw),repository=GitRoot,repositoryId=Setting("fixtureId"),action="retain-unresolved",state,stateDigest,witnessDigest,result,originalOutcome="not-inferred"}));
   Observe(new {operation="physical-disposition-retained",id,invocation,identity,state,result});Checkpoint("after-disposition-publication");return result;
  }finally{dispositionInspection=false;}
 }
