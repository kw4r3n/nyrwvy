 // Separate administrative decision for a new request whose actual result is
 // missing. Custody and effects never become an application request outcome.
 const string InterruptedDispositionKey="physical-continuation-disposition/v1";
 const string InterruptedStorageContract="intact-flushed-single-writer-before-slot/v1";
 const string SupportedContinuationOwner="@@CONTINUATION_OWNER_DIGEST@@";
 void RunInterruptedDisposition(){
  try{
   Check(Setting("dispositionRequest")==""&&Setting("continuationRequest")==""&&Setting("releaseRequest")=="","one administrative request required");
   Console.WriteLine(RetainInterruptedContinuation(System.IO.File.ReadAllText(Setting("interruptedContinuationRequest"))));
  }catch(Exception error){Console.WriteLine("err:unresolved");Observe(new {operation="physical-interrupted-continuation-rejected",reason=error.Message});}
  Environment.Exit(0);
 }
 static bool AdministrativeId(string id)=>System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-zA-Z0-9_-]{1,80}\z");
 void InterruptedAuthorityLineage(JsonElement r,ContinuationIntent intent){
  using var accepted=JsonDocument.Parse(intent.Request);var c=accepted.RootElement;
  using var prior=JsonDocument.Parse(Required(c,"retentionRequest"));var q=prior.RootElement;
  Check(AdministrativeId(intent.Id)&&AdministrativeId(Required(q,"id"))&&intent.Id!=Required(q,"id")&&Required(c,"id")==intent.Id&&Required(c,"action")=="continue-owned-tail"&&Required(q,"action")=="retain-unresolved","accepted decision identities/actions");
  Check(Required(r,"id")!=intent.Id&&Required(r,"id")!=Required(q,"id")&&Required(r,"id")!=intent.Identity,"distinct interruption decision identity");
  // Shared accepted-authority guard permits optional absent/empty earlier
  // settings; present values confirm the accepted bytes, never relabel them.
  ReleaseAuthorityLineage(r,intent);
 }
 List<object> InterruptedCustody(ContinuationIntent intent,out GitChild? last){
  var rows=new List<object>();last=null;bool absent=false;
  var plan=JsonSerializer.Deserialize<GitPlan>(System.IO.File.ReadAllText(StoragePath(PlanKey(intent.Identity))))!;
  Check(plan.Version=="3"&&plan.Identity==intent.Identity&&plan.Repository==GitRoot&&plan.RepositoryId==intent.RepositoryId,"interrupted plan ownership");
  string[][] commands=intent.State=="object-only"?new[]{new[]{"update-ref","refs/heads/main",plan.Commit,plan.Before.Head},new[]{"read-tree","--reset","-u",plan.Commit}}:new[]{new[]{"read-tree","--reset","-u",plan.Commit}};
  Check((intent.State is "object-only" or "ref-only")&&JsonSerializer.Serialize(intent.Commands)==JsonSerializer.Serialize(commands),"interrupted command plan mismatch");
  var invocations=new HashSet<string>(StringComparer.Ordinal){intent.Invocation};
  for(int stage=0;stage<intent.Commands.Length;stage++){
   string key=NewLaunchKey(intent.Id,stage),file=StoragePath(key);
   if(!System.IO.File.Exists(file)){
    absent=true;rows.Add(new {key,stage,status="absent",invocation="",launchDigest="",witnessDigest=""});continue;
   }
   Check(!absent&&(last==null||last.State=="quiescent"&&last.ExitCode==0),"new launch sequence contradicts prior custody");
   string raw=System.IO.File.ReadAllText(file);var child=JsonSerializer.Deserialize<GitChild>(raw)!;
   Check(child.Version=="2"&&child.OwnerDigest==SupportedContinuationOwner&&child.Identity==intent.Identity&&child.Repository==GitRoot&&child.RepositoryId==intent.RepositoryId&&child.Args.SequenceEqual(intent.Commands[stage])&&child.InputDigest==DigestText("")&&child.Pid.HasValue&&child.Invocation!=""&&invocations.Add(child.Invocation),"unsupported or contradictory new launch");
   GitJoin witness;
   if(child.State=="active"){
    Check(!child.ExitCode.HasValue&&!child.InputClosed&&!child.OutputDrained,"active new launch flags contradict custody");
    witness=LoadJoin(child,raw);
   }else{
    Check(child.State=="quiescent"&&child.ExitCode.HasValue&&child.InputClosed&&child.OutputDrained,"new launch is not terminal");
    var active=JsonSerializer.Deserialize<GitChild>(raw)!;active.State="active";active.ExitCode=null;active.InputClosed=false;active.OutputDrained=false;
    witness=LoadJoin(active,JsonSerializer.Serialize(active));
    Check(witness.ExitCode==child.ExitCode&&witness.ExitSignal=="","quiescent new launch/owner exit mismatch");
   }
   // These bounded update-ref/read-tree fixtures are silent. A different
   // capture, even a well-formed digest, lacks this fixture's custody contract.
   Check(witness.OutputDigest==DigestText("")&&witness.ErrorDigest==DigestText(""),"unsupported new command capture");
   rows.Add(new {key,stage,status=child.State,invocation=child.Invocation,launchDigest=DigestText(raw),witnessDigest=DigestText(System.IO.File.ReadAllText(StoragePath("physical-join/v1/"+child.Invocation)))});last=child;
  }
  return rows;
 }
 string RetainInterruptedContinuation(string request){
  using var document=JsonDocument.Parse(request);var r=document.RootElement;
  AdministrativeAuthority(r,"interruptedContinuationAuthorization","retain-unresolved-continuation");
  Check(Required(r,"storageContract")==InterruptedStorageContract&&Setting("interruptedContinuationStorageContract")==InterruptedStorageContract,"explicit intact-storage/before-slot contract required");
  string pending=System.IO.File.ReadAllText(StoragePath(ContinuationKey));var intent=JsonSerializer.Deserialize<ContinuationIntent>(pending)!;
  InterruptedAuthorityLineage(r,intent);
  Check(Required(r,"continuationId")==intent.Id&&Required(r,"continuationRequest")==intent.Request&&Required(r,"continuationIntentDigest")==DigestText(pending),"exact accepted continuation binding required");
  Check(!System.IO.File.Exists(StoragePath(ContinuationResultKey(intent.Id)))&&!System.IO.File.Exists(StoragePath(AdmissionKey))&&!System.IO.File.Exists(StoragePath(ReleaseKey)),"actual completion or admission already present");
  // Establish every retained new child's terminal custody before any Git
  // inspection. A missing slot uses only the explicit intact-storage contract.
  // A retained prelaunch/no-PID slot, root exit or PID absence cannot join it.
  var custody=InterruptedCustody(intent,out var last);
  string custodyDigest=DigestText(JsonSerializer.Serialize(custody));Check(Required(r,"custodyDigest")==custodyDigest,"current stage custody differs from decision");
  string originalLaunch=System.IO.File.ReadAllText(StoragePath("physical-child/v1"));var original=JsonSerializer.Deserialize<GitChild>(originalLaunch)!;
  if(original.Version=="2")Check(original.OwnerDigest==SupportedContinuationOwner,"unsupported original owner");
  dispositionInspection=true;
  try{
   OriginalLineage(intent,true);var plan=LoadPlan(intent.Identity);var current=PhysicalState();string state=OwnedTail(plan,current);
   string before=last==null?intent.State:last.Args[0]=="update-ref"?"object-only":"ref-only",after=last==null?intent.State:last.Args[0]=="update-ref"?"ref-only":"applied";
   Check((state==before||state==after)&&(last==null||last.State!="quiescent"||last.ExitCode!=0||state==after),"physical state contradicts staged custody");
   string stateDigest=DigestText(JsonSerializer.Serialize(current));
   Check(state==Required(r,"expectedState")&&stateDigest==Required(r,"observedStateDigest"),"explicit current owned state mismatch");
   var evidence=OriginalInventory();evidence.Remove(System.IO.Path.GetFileName(StoragePath(InterruptedDispositionKey)));
   string evidenceDigest=DigestText(JsonSerializer.Serialize(evidence));Check(Required(r,"evidenceDigest")==evidenceDigest,"exact retained evidence differs from decision");
   string result="ok:retained-unresolved-continuation:"+state;
   string receipt=JsonSerializer.Serialize(new {version="1",id=Required(r,"id"),request,requestDigest=DigestText(request),continuationId=intent.Id,continuationRequestDigest=intent.RequestDigest,pendingDigest=DigestText(pending),identity=intent.Identity,invocation=intent.Invocation,repository=GitRoot,repositoryId=intent.RepositoryId,dispositionDigest=intent.DispositionDigest,planDigest=intent.PlanDigest,launchDigest=intent.LaunchDigest,intentDigest=intent.IntentDigest,registryDigest=intent.RegistryDigest,custody,custodyDigest,evidence,evidenceDigest,state,stateDigest,storageContract=InterruptedStorageContract,result,originalOutcome="not-inferred",continuationOutcome="not-inferred",admission="closed"});
   string file=StoragePath(InterruptedDispositionKey);
   if(System.IO.File.Exists(file)){Check(System.IO.File.ReadAllText(file)==receipt,"conflicting interrupted disposition");return result;}
   Checkpoint("before-interrupted-disposition-publication");AtomicWrite(InterruptedDispositionKey,receipt);
   Observe(new {operation="physical-interrupted-continuation-retained",id=Required(r,"id"),continuationId=intent.Id,state,result});Checkpoint("after-interrupted-disposition-publication");return result;
  }finally{dispositionInspection=false;}
 }
