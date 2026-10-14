 // Example binding for one trusted writer, fenced Git mutation children and
 // intact storage. File flush is inherited; power-loss Git durability is not proved.
 sealed class GitState {
  public string Head {get;set;}=""; public string Symbolic {get;set;}="";
  public string Refs {get;set;}=""; public string Objects {get;set;}="";
  public string IndexBytes {get;set;}=""; public string IndexEntries {get;set;}="";
  public string Worktree {get;set;}="";
 }
 sealed class GitPlan {
  public string Version {get;set;}="3"; public string Identity {get;set;}="";
  public string Intent {get;set;}=""; public string Repository {get;set;}="";
  public string RepositoryId {get;set;}=""; public string Selected {get;set;}="";
  public string Tree {get;set;}=""; public string Patch {get;set;}="";
  public string Commit {get;set;}=""; public string CommitBytes {get;set;}="";
  public bool NoMutation {get;set;}
  public string ExpectedIndex {get;set;}=""; public string ExpectedWorktree {get;set;}="";
  public GitState Before {get;set;}=new();
 }
 // This is child observation, never an original request result. A retained active
 // launch may precede creation or outlive the parent. PID absence cannot clear it.
 sealed class GitChild {
  public string Version {get;set;}="1";public string State {get;set;}="active";
  public string Invocation {get;set;}="";public string Identity {get;set;}="";
  public string Repository {get;set;}="";public string RepositoryId {get;set;}="";
  public string[] Args {get;set;}=Array.Empty<string>();public string InputDigest {get;set;}="";
  public int? Pid {get;set;}public int? ExitCode {get;set;}
  public bool InputClosed {get;set;}public bool OutputDrained {get;set;}
 }
 void GitChildGuard(){
  string file=StoragePath("physical-child/v1");if(!System.IO.File.Exists(file))return;
  var child=JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(file))!;
  Check(child.Version=="1"&&child.Repository==GitRoot&&child.RepositoryId==Setting("fixtureId")&&child.Invocation!=""&&child.Identity!="","Git child ownership");
  Check(child.State=="quiescent"&&child.Pid.HasValue&&child.ExitCode.HasValue&&child.InputClosed&&child.OutputDrained,"Git child launch unresolved; explicit disposition required");
  var plan=JsonSerializer.Deserialize<GitPlan>(System.IO.File.ReadAllText(StoragePath(PlanKey(child.Identity))))!;
  Check(plan.Version=="3"&&plan.Identity==child.Identity&&plan.Repository==child.Repository&&plan.RepositoryId==child.RepositoryId,"Git child/plan correlation");
  string[] expected=child.Args.FirstOrDefault() switch{
   "commit-tree"=>new[]{"commit-tree",plan.Tree,"-p",plan.Before.Head},
   "update-ref"=>new[]{"update-ref","refs/heads/main",plan.Commit,plan.Before.Head},
   "read-tree"=>new[]{"read-tree","--reset","-u",plan.Commit},
   _=>Array.Empty<string>()};
  Check(expected.Length!=0&&child.Args.SequenceEqual(expected)&&child.InputDigest==DigestText(child.Args[0]=="commit-tree"?child.Identity+"\n":""),"Git child arguments/intent");
 }
 async System.Threading.Tasks.Task<string> GitOutput(System.IO.StreamReader reader){
  var output=new System.Text.StringBuilder();var buffer=new char[4096];int count;
  while((count=await reader.ReadAsync(buffer))!=0){Check(output.Length+count<=1048576,"bounded Git output exceeded");output.Append(buffer,0,count);}
  return output.ToString();
 }
 string GitRoot=>System.IO.Path.Combine(System.IO.Path.GetDirectoryName(StorageRoot)!,"git");
 void GitGuard(){
  Check(Setting("fixtureId")!=""&&System.IO.File.ReadAllText(System.IO.Path.Combine(GitRoot,".git","offline-fixture-id"))==Setting("fixtureId"),"disposable repository identity");
  for(var directory=new System.IO.DirectoryInfo(GitRoot);directory!=null;directory=directory.Parent)Check(directory.LinkTarget==null,"symlinked fixture directory");
  Check(new System.IO.DirectoryInfo(System.IO.Path.Combine(GitRoot,".git")).LinkTarget==null,"symlinked Git directory");
  Check(!System.IO.File.Exists(System.IO.Path.Combine(GitRoot,".git","objects","info","alternates")),"external object store");
 }
 string Git(string[] args,string? input=null,bool mutation=false,bool trim=true){
  GitGuard();GitChildGuard();
  int deadline=Setting("gitDeadlineMs")==""?10000:int.Parse(Setting("gitDeadlineMs"));Check(deadline>=250&&deadline<=10000,"bounded Git deadline");
  var start=new System.Diagnostics.ProcessStartInfo("git"){WorkingDirectory=GitRoot,UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var name in start.Environment.Keys.Where(x=>x.StartsWith("GIT_",StringComparison.Ordinal)).ToArray())start.Environment.Remove(name);
  foreach(var item in new[]{("GIT_CONFIG_NOSYSTEM","1"),("GIT_CONFIG_GLOBAL","/dev/null"),("GIT_TERMINAL_PROMPT","0"),("GIT_OPTIONAL_LOCKS","0"),
   ("GIT_AUTHOR_NAME","Offline fixture"),("GIT_AUTHOR_EMAIL","fixture@invalid"),("GIT_COMMITTER_NAME","Offline fixture"),("GIT_COMMITTER_EMAIL","fixture@invalid"),
   ("GIT_AUTHOR_DATE","2000-01-01T00:00:00Z"),("GIT_COMMITTER_DATE","2000-01-01T00:00:00Z")})start.Environment[item.Item1]=item.Item2;
  foreach(var arg in new[]{"-c","core.hooksPath="+System.IO.Path.Combine(GitRoot,"disabled-hooks"),"-c","commit.gpgSign=false","-c","core.fsmonitor=false","-c","core.untrackedCache=false"}.Concat(args))start.ArgumentList.Add(arg);
  GitChild? child=null;
  if(mutation){
   child=new(){Invocation=Guid.NewGuid().ToString("N"),Identity=effectIdentity.Value??"",Repository=GitRoot,RepositoryId=Setting("fixtureId"),Args=args,InputDigest=DigestText(input??"")};
   Check(child.Identity!="","Git mutation requires effect identity");AtomicWrite("physical-child/v1",JsonSerializer.Serialize(child));
   Observe(new {operation="physical-child-launch",child.Invocation,child.Identity,command=args[0]});Checkpoint("before-git-child-start");
  }
  using var process=new System.Diagnostics.Process{StartInfo=start};
  // If creation fails, leave the pre-launch fence unresolved rather than
  // asserting that no child exists from an unavailable start observation.
  Check(process.Start(),"Git child creation failed");
  if(child!=null){child.Pid=process.Id;AtomicWrite("physical-child/v1",JsonSerializer.Serialize(child));}
  Observe(new {operation="physical-git-start",command=args[0],mutation,pid=process.Id,invocation=child?.Invocation});
  var stdout=GitOutput(process.StandardOutput);var stderr=GitOutput(process.StandardError);
  async System.Threading.Tasks.Task Feed(){try{if(input!=null)await process.StandardInput.WriteAsync(input);}finally{process.StandardInput.Close();}}
  var feed=Feed();var exited=process.WaitForExitAsync();
  Exception? failure=null;
  try{System.Threading.Tasks.Task.WhenAll(feed,stdout,stderr,exited).WaitAsync(TimeSpan.FromMilliseconds(deadline)).GetAwaiter().GetResult();}
  catch(Exception error){
   failure=error;Observe(new {operation="physical-git-termination",command=args[0],pid=process.Id,reason=error.GetType().Name});
   // Failure to terminate/join/drain keeps the active fence. Never publish
   // quiescence merely because termination was requested or the parent exits.
   if(!process.HasExited)process.Kill(true);
   exited.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();process.StandardInput.Close();
   try{System.Threading.Tasks.Task.WhenAll(feed,stdout,stderr).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();}
   catch{if(!feed.IsCompleted||!stdout.IsCompletedSuccessfully||!stderr.IsCompletedSuccessfully)throw new InvalidOperationException("Git join/drain unresolved; explicit disposition required",error);}
  }
  Check(process.HasExited&&feed.IsCompleted&&stdout.IsCompletedSuccessfully&&stderr.IsCompletedSuccessfully,"Git quiescence unavailable");
  string output=stdout.GetAwaiter().GetResult(),errorText=stderr.GetAwaiter().GetResult();
  if(child!=null){child.State="quiescent";child.ExitCode=process.ExitCode;child.InputClosed=true;child.OutputDrained=true;AtomicWrite("physical-child/v1",JsonSerializer.Serialize(child));Observe(new {operation="physical-child-quiescent",child.Invocation,child.Identity,child.Pid,child.ExitCode});}
  Observe(new {operation="physical-git-exit",command=args[0],args,mutation,exitCode=process.ExitCode,childExited=true});
  if(failure!=null)throw new InvalidOperationException("Git command interrupted; physical effect must be inspected",failure);
  Check(process.ExitCode==0,"Git command failed: "+args[0]+": "+errorText);return trim?output.TrimEnd('\r','\n'):output;
 }
 // Git's regular-file mode records the owner's executable bit. Other Unix
 // permission bits, ownership and timestamps are not tree-mode facts.
 string FileFact(string name,string mode,string digest)=>name+":"+(Setting("worktreeModeControl")=="content-only"?"":mode+":")+digest;
 string WorktreeState(){
  var rows=new List<string>();
  void Walk(string dir){foreach(string entry in System.IO.Directory.EnumerateFileSystemEntries(dir).OrderBy(x=>x,StringComparer.Ordinal)){
   string name=System.IO.Path.GetRelativePath(GitRoot,entry);if(name==".git"||name=="disabled-hooks")continue;
   Check((System.IO.File.GetAttributes(entry)&System.IO.FileAttributes.ReparsePoint)==0,"symlinked worktree entry");
   if(System.IO.Directory.Exists(entry))Walk(entry);else{
    string mode=(System.IO.File.GetUnixFileMode(entry)&System.IO.UnixFileMode.UserExecute)!=0?"100755":"100644";
    rows.Add(FileFact(name,mode,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(entry))).ToLowerInvariant()));
   }
  }}Walk(GitRoot);return string.Join("\n",rows);
 }
 GitState PhysicalState(){
  Check(Git(new[]{"rev-parse","--show-toplevel"})==GitRoot,"repository/worktree path");
  Check(Git(new[]{"rev-parse","--absolute-git-dir"})==System.IO.Path.Combine(GitRoot,".git"),"standalone fixture Git directory");
  return new(){Head=Git(new[]{"rev-parse","HEAD"}),Symbolic=Git(new[]{"symbolic-ref","HEAD"}),
   Refs=Git(new[]{"show-ref"}),Objects=string.Join("\n",Git(new[]{"cat-file","--batch-all-objects","--batch-check=%(objectname) %(objecttype)"}).Split('\n').OrderBy(x=>x,StringComparer.Ordinal)),
   IndexBytes=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(System.IO.Path.Combine(GitRoot,".git","index")))).ToLowerInvariant(),
   IndexEntries=Git(new[]{"ls-files","--stage"}),Worktree=WorktreeState()};
 }
 string PlanKey(string identity)=>"physical-plan/v1/"+identity;
 GitPlan LoadPlan(string identity){
  var plan=JsonSerializer.Deserialize<GitPlan>(System.IO.File.ReadAllText(StoragePath(PlanKey(identity))))!;
  Check(plan.Version=="3"&&plan.Identity==identity&&plan.Repository==GitRoot&&plan.RepositoryId==Setting("fixtureId"),"physical intent ownership/version");
  Check(plan.Before.Symbolic=="refs/heads/main","bounded branch");
  Check(Git(new[]{"rev-parse",plan.Selected+"^{tree}"})==plan.Tree,"selected tree changed");
  Check(Git(new[]{"diff",plan.Before.Head,plan.Selected,"--","file.txt"})==plan.Patch,"selected patch changed");
  using var selectedIntent=JsonDocument.Parse(plan.Intent);Check(selectedIntent.RootElement.GetProperty("commit").GetString()==plan.Selected,"selected intent changed");
  if(plan.NoMutation)Check(selectedIntent.RootElement.GetProperty("integrated").GetString()==plan.Commit&&plan.Before.Head==plan.Commit,"retained no-mutation HEAD");
  else Check(Git(new[]{"hash-object","-t","commit","--stdin"},plan.CommitBytes)==plan.Commit,"predicted commit changed");
  return plan;
 }
 void PhysicalPrepare(string value){
  using var json=JsonDocument.Parse(value);var admission=json.RootElement;
  if(admission.GetProperty("phase").GetString()!="pending")return;
  string identity=admission.GetProperty("token").GetString()!,intent=admission.GetProperty("intent").GetString()!;
  string file=StoragePath(PlanKey(identity));
  if(System.IO.File.Exists(file)){Check(LoadPlan(identity).Intent==intent,"conflicting physical intent");return;}
  using var selectedIntent=JsonDocument.Parse(intent);string selected=selectedIntent.RootElement.GetProperty("commit").GetString()!;
  Check(System.Text.RegularExpressions.Regex.IsMatch(selected,"^[0-9a-f]{40}$"),"bounded SHA1 selected commit");
  var before=PhysicalState();Check(before.Symbolic=="refs/heads/main","bounded primary worktree");
  string tree=Git(new[]{"rev-parse",selected+"^{tree}"}),listing=Git(new[]{"ls-tree","-r",selected});
  Check(System.Text.RegularExpressions.Regex.IsMatch(listing,"^100644 blob [0-9a-f]{40}\\tfile.txt$"),"single plain-text file fixture");
  string blob=listing.Split(' ')[2].Split('\t')[0],expectedIndex="100644 "+blob+" 0\tfile.txt";
  string expectedWorktree=FileFact("file.txt",listing.Split(' ')[0],DigestText(Git(new[]{"show",selected+":file.txt"},trim:false)));
  string baseListing=Git(new[]{"ls-tree","-r",before.Head});
  Check(System.Text.RegularExpressions.Regex.IsMatch(baseListing,"^100644 blob [0-9a-f]{40}\\tfile.txt$"),"bounded initial tree");
  string baseBlob=baseListing.Split(' ')[2].Split('\t')[0];
  Check(before.IndexEntries=="100644 "+baseBlob+" 0\tfile.txt"&&before.Worktree==FileFact("file.txt",baseListing.Split(' ')[0],DigestText(Git(new[]{"show",before.Head+":file.txt"},trim:false))),"clean owned initial index/worktree");
  string integrated=selectedIntent.RootElement.GetProperty("integrated").GetString()!;bool noMutation=integrated!="";
  string patch=Git(new[]{"diff",before.Head,selected,"--","file.txt"});
  if(noMutation)Check(before.Head==integrated&&before.IndexEntries==expectedIndex&&before.Worktree==expectedWorktree&&patch=="","already integrated physical state");
  else Check(patch==Setting("patch"),"selected patch binding");
  string message=identity+"\n",bytes=$"tree {tree}\nparent {before.Head}\nauthor Offline fixture <fixture@invalid> 946684800 +0000\ncommitter Offline fixture <fixture@invalid> 946684800 +0000\n\n{message}";
  string commit=noMutation?integrated:Git(new[]{"hash-object","-t","commit","--stdin"},bytes);
  Check(noMutation||!before.Objects.Split('\n').Any(x=>x.StartsWith(commit+" ",StringComparison.Ordinal)),"preexisting predicted commit");
  var plan=new GitPlan{Identity=identity,Intent=intent,Repository=GitRoot,RepositoryId=Setting("fixtureId"),Selected=selected,Tree=tree,Patch=patch,Commit=commit,CommitBytes=noMutation?"":bytes,NoMutation=noMutation,ExpectedIndex=expectedIndex,ExpectedWorktree=expectedWorktree,Before=before};
  AtomicWrite(PlanKey(identity),JsonSerializer.Serialize(plan));Observe(new {operation="physical-intent",identity,selected,tree,commit,preHead=before.Head});Checkpoint("after-physical-intent");
 }
 void PhysicalPrepareJournal(string value){
  using var json=JsonDocument.Parse(value);var journal=json.RootElement;
  if(journal.GetProperty("state").GetString()!="pending")return;
  PhysicalPrepare(JsonSerializer.Serialize(new {phase="pending",token=journal.GetProperty("effectIdentity").GetString(),intent=journal.GetProperty("intent").GetString()}));
 }
 string Classify(GitPlan plan,GitState current){
  if(JsonSerializer.Serialize(current)==JsonSerializer.Serialize(plan.Before))return plan.NoMutation?"applied":"absent";
  string objects=string.Join("\n",(plan.Before.Objects+"\n"+plan.Commit+" commit").Split('\n').OrderBy(x=>x,StringComparer.Ordinal));
  string refs=plan.Before.Refs.Replace(plan.Before.Head+" refs/heads/main",plan.Commit+" refs/heads/main",StringComparison.Ordinal);
  if(current.Symbolic==plan.Before.Symbolic&&current.Objects==objects&&current.Refs==refs&&current.Head==plan.Commit&&current.IndexEntries==plan.ExpectedIndex&&current.Worktree==plan.ExpectedWorktree)return "applied";
  return "unknown"; // Includes partial/unowned/contradictory physical state.
 }
 string Corroborated(GitPlan plan,GitState current){
  string state=Classify(plan,current);
  foreach(string phase in new[]{"commit","head","save"}){
   string file=StoragePath("physical-receipt/"+plan.Identity+"/"+phase);if(!System.IO.File.Exists(file))continue;
   using var receipt=JsonDocument.Parse(System.IO.File.ReadAllText(file));var row=receipt.RootElement;
   if(state=="absent"||row.GetProperty("phase").GetString()!=phase||row.GetProperty("identity").GetString()!=plan.Identity||row.GetProperty("Commit").GetString()!=plan.Commit)return "unknown";
  }return state;
 }
 NyStr PhysicalEvidence(string identity){
  string state="unknown",head="",reason="inspection unavailable";
  try{if(Setting("evidence")!="unavailable"){
   if(Setting("physicalControl")=="receipt-absence"){
    // Deliberately faulty paired control: the reviewed synthetic absence rule.
    state=System.IO.File.Exists(StoragePath("physical-receipt/"+identity+"/commit"))?"unknown":"absent";reason="receipt-only control";
   }else{var plan=LoadPlan(identity);var current=PhysicalState();state=Corroborated(plan,current);head=state=="applied"?current.Head:"";reason="complete local state comparison";}
  }}catch(Exception error){reason=error.Message;}
  Observe(new {operation="effect-evidence",identity,state,head,reason});return JsonSerializer.Serialize(new {state,head});
 }
 string PhysicalRead(string key,string value){
  if(key!="registry/v1")return value;
  try{
   string launch=StoragePath("physical-child/v1");
   if(System.IO.File.Exists(launch))Check(JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(launch))!.State=="quiescent","Git child launch unresolved before admission");
   using var json=JsonDocument.Parse(value);var admission=json.RootElement;
   if(admission.GetProperty("phase").GetString()!="ready")return value;
   string identity=admission.GetProperty("token").GetString()!;
   var plan=LoadPlan(identity);Check(plan.Intent==admission.GetProperty("intent").GetString(),"ready physical intent mismatch");
   using var intent=JsonDocument.Parse(plan.Intent);string target=intent.RootElement.GetProperty("target").GetInt64().ToString();
   string integrated=admission.GetProperty("entries").EnumerateArray().Single(x=>x.GetProperty("id").GetString()==target).GetProperty("integrated").GetString()!;
   string state=Corroborated(plan,PhysicalState());
   Check(state=="applied"&&integrated==plan.Commit||state=="absent"&&integrated==""&&admission.GetProperty("outcome").GetString()!.StartsWith("err:",StringComparison.Ordinal),"ready physical effect/HEAD mismatch");
   return value;
  }catch(Exception error){Observe(new {operation="physical-ready-rejected",reason=error.Message});return "{physical-unresolved}";}
 }
 void PhysicalStep(string phase,string patch){
  string identity=effectIdentity.Value??"";var plan=LoadPlan(identity);Check(!plan.NoMutation,"no-mutation intent cannot dispatch Git");Check(patch==(phase=="save"?plan.Commit:""),"effect argument mismatch");
  if(phase=="commit"){
   Check(Corroborated(plan,PhysicalState())=="absent"||Setting("physicalControl")=="receipt-absence","effect absence required before dispatch");
   string commit=Git(new[]{"commit-tree",plan.Tree,"-p",plan.Before.Head},identity+"\n",mutation:true);Check(commit==plan.Commit,"physical commit identity");Checkpoint("after-git-object");
   Git(new[]{"update-ref","refs/heads/main",plan.Commit,plan.Before.Head},mutation:true);Checkpoint("after-git-ref");
  }else if(phase=="head"){Check(Git(new[]{"rev-parse","HEAD"})==plan.Commit,"HEAD observation mismatch");Observe(new {operation="physical-head-observed",head=plan.Commit});}
  else if(phase=="save"){Git(new[]{"read-tree","--reset","-u",plan.Commit},mutation:true);Checkpoint("after-git-worktree");Check(Classify(plan,PhysicalState())=="applied","physical worktree completion");}
  AtomicWrite("physical-receipt/"+identity+"/"+phase,JsonSerializer.Serialize(new {phase,identity,plan.Commit}));
  Observe(new {operation="effect-persisted",phase,identity,patch});
 }
 string PhysicalHead()=>Git(new[]{"rev-parse","HEAD"});
