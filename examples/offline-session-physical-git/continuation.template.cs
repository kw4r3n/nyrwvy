 // Experimental administrative example policy. The semantic graph/core stay
 // generic. This completes a new physical request, never the lost original.
 sealed class ContinuationIntent {
  public string Version {get;set;}="1";public string Id {get;set;}="";public string Request {get;set;}="";public string RequestDigest {get;set;}="";
  public string Identity {get;set;}="";public string Invocation {get;set;}="";public string Repository {get;set;}="";public string RepositoryId {get;set;}="";
  public string PlanDigest {get;set;}="";public string LaunchDigest {get;set;}="";public string DispositionDigest {get;set;}="";public string IntentDigest {get;set;}="";
  public string State {get;set;}="";public string StateDigest {get;set;}="";public string RegistryDigest {get;set;}="";
  public Dictionary<string,string> Originals {get;set;}=new();public string[][] Commands {get;set;}=Array.Empty<string[]>();
 }
 const string ContinuationKey="physical-continuation-intent/v1",AdmissionKey="physical-observation-intent/v1",ReleaseKey="physical-observation-release/v1";
 bool observationAdmission;
 static string Required(JsonElement row,string key)=>row.GetProperty(key).GetString()!;
 string AuthorityValue(JsonElement row,string key,bool allowEmpty=false){
  Check(row.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String,"authority must be a JSON string: "+key);
  string authority=value.GetString()??throw new InvalidOperationException("null authority: "+key);
  Check(allowEmpty||authority.Length!=0,"empty authority: "+key);return authority;
 }
 string ScenarioAuthority(string key,bool optional=false){
  if(optional&&!scenario.TryGetProperty(key,out _))return "";
  return AuthorityValue(scenario,key,optional);
 }
 void AdministrativeAuthority(JsonElement row,string setting,string action){
  string id=Required(row,"id"),authorization=AuthorityValue(row,"authorization");
  Check(System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-zA-Z0-9_-]{1,80}\z")&&authorization==ScenarioAuthority(setting)&&Required(row,"action")==action,"separate request identity/authority/action");
 }
 void RunContinuation(){
  try{
   Check(Setting("dispositionRequest")==""&&(Setting("continuationRequest")=="")!=(Setting("releaseRequest")==""),"one administrative request required");
   Console.WriteLine(Setting("continuationRequest")!=""?ContinueTail(System.IO.File.ReadAllText(Setting("continuationRequest"))):ReleaseObservation(System.IO.File.ReadAllText(Setting("releaseRequest"))));
  }catch(Exception error){Console.WriteLine("err:unresolved");Observe(new {operation="physical-continuation-rejected",reason=error.Message});}
  Environment.Exit(0);
 }
 string NewLaunchKey(string id,int index)=>"physical-continuation-child/v1/"+id+"/"+index;
 string ContinuationResultKey(string id)=>"physical-continuation-result/v1/"+id;
 Dictionary<string,string> OriginalInventory()=>System.IO.Directory.GetFiles(StorageRoot).OrderBy(x=>x,StringComparer.Ordinal).ToDictionary(x=>System.IO.Path.GetFileName(x)!,x=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(x))).ToLowerInvariant());
 void OriginalLineage(ContinuationIntent intent,bool interruptedDisposition=false){
  Check(intent.Originals.Count>0,"missing original lineage");
  foreach(var item in intent.Originals){
   Check(System.Text.RegularExpressions.Regex.IsMatch(item.Key,@"\A[0-9a-f]{64}\.json\z"),"invalid lineage filename");
   Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(System.IO.Path.Combine(StorageRoot,item.Key)))).ToLowerInvariant()==item.Value,"original evidence changed");
  }
  var allowed=intent.Originals.Keys.ToHashSet(StringComparer.Ordinal);
  if(interruptedDisposition)allowed.Add(System.IO.Path.GetFileName(StoragePath(InterruptedDispositionKey)));
  foreach(string key in new[]{ContinuationKey,ContinuationResultKey(intent.Id),AdmissionKey,ReleaseKey})allowed.Add(System.IO.Path.GetFileName(StoragePath(key)));
  for(int i=0;i<intent.Commands.Length;i++){
   string key=NewLaunchKey(intent.Id,i);allowed.Add(System.IO.Path.GetFileName(StoragePath(key)));
   if(System.IO.File.Exists(StoragePath(key))){var owned=JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(StoragePath(key)))!;allowed.Add(System.IO.Path.GetFileName(StoragePath("physical-join/v1/"+owned.Invocation)));}
  }
  if(System.IO.File.Exists(StoragePath(AdmissionKey))){using var decision=JsonDocument.Parse(System.IO.File.ReadAllText(StoragePath(AdmissionKey)));string releaseId=Required(decision.RootElement,"id");Check(System.Text.RegularExpressions.Regex.IsMatch(releaseId,@"\A[a-zA-Z0-9_-]{1,80}\z"),"invalid release lineage");allowed.Add(System.IO.Path.GetFileName(StoragePath("physical-observation-result/v1/"+releaseId)));}
  Check(System.IO.Directory.GetFiles(StorageRoot).All(x=>allowed.Contains(System.IO.Path.GetFileName(x))),"unbound storage evidence appeared");
  Check(Setting("evidence")!="unavailable"&&Setting("physicalControl")==""&&Setting("worktreeModeControl")=="","enabled inspection required");
  Check(intent.Version=="1"&&intent.Repository==GitRoot&&intent.RepositoryId==Setting("fixtureId"),"continuation domain mismatch");
  string launch=System.IO.File.ReadAllText(StoragePath("physical-child/v1"));var child=JsonSerializer.Deserialize<GitChild>(launch)!;
  Check(child.Invocation==intent.Invocation&&child.Identity==intent.Identity&&DigestText(launch)==intent.LaunchDigest,"original launch changed");
  if(child.State=="active")LoadJoin(child,launch);else GitChildGuard();
  string planRaw=System.IO.File.ReadAllText(StoragePath(PlanKey(intent.Identity)));var plan=LoadPlan(intent.Identity);
  Check(DigestText(planRaw)==intent.PlanDigest&&DigestText(plan.Intent)==intent.IntentDigest&&!plan.NoMutation,"original plan changed");
  using var request=JsonDocument.Parse(intent.Request);var r=request.RootElement;
  using var retentionRequest=JsonDocument.Parse(Required(r,"retentionRequest"));var q=retentionRequest.RootElement;
  Check(AuthorityValue(r,"authorization")!=AuthorityValue(q,"authorization"),"accepted continuation/retention authority separation");
  Check(intent.Id==Required(r,"id")&&intent.RequestDigest==DigestText(intent.Request)&&intent.State==Required(r,"expectedState")&&intent.StateDigest==Required(r,"observedStateDigest")&&intent.DispositionDigest==Required(r,"dispositionDigest"),"continuation intent/request mismatch");
  string disposition=System.IO.File.ReadAllText(StoragePath("physical-disposition/v1/"+Required(q,"id")));
  Check(DigestText(disposition)==intent.DispositionDigest,"prior disposition changed");
  using var proof=JsonDocument.Parse(disposition);var p=proof.RootElement;
  Check(Required(p,"requestDigest")==DigestText(Required(r,"retentionRequest"))&&Required(p,"identity")==intent.Identity&&Required(p,"invocation")==intent.Invocation&&Required(p,"launchDigest")==intent.LaunchDigest&&Required(p,"planDigest")==intent.PlanDigest&&Required(p,"state")==intent.State&&Required(p,"stateDigest")==intent.StateDigest&&Required(p,"originalOutcome")=="not-inferred","prior disposition lineage mismatch");
  Check(DigestText(System.IO.File.ReadAllText(StoragePath("registry/v1")))==intent.RegistryDigest,"original admission changed");
  string[][] commands=intent.State=="object-only"?new[]{new[]{"update-ref","refs/heads/main",plan.Commit,plan.Before.Head},new[]{"read-tree","--reset","-u",plan.Commit}}:new[]{new[]{"read-tree","--reset","-u",plan.Commit}};
  Check((intent.State is "object-only" or "ref-only")&&JsonSerializer.Serialize(intent.Commands)==JsonSerializer.Serialize(commands),"continuation commands mismatch");
 }
 string CompletionRecord(ContinuationIntent intent,string pending){
  OriginalLineage(intent);var plan=LoadPlan(intent.Identity);var current=PhysicalState();
  Check(OwnedTail(plan,current)=="applied","continuation completion state mismatch");
  var launches=new List<object>();
  for(int i=0;i<intent.Commands.Length;i++){
   string raw=System.IO.File.ReadAllText(StoragePath(NewLaunchKey(intent.Id,i)));var child=JsonSerializer.Deserialize<GitChild>(raw)!;
   Check(child.Version=="2"&&child.State=="quiescent"&&child.Identity==intent.Identity&&child.Repository==GitRoot&&child.RepositoryId==intent.RepositoryId&&child.Args.SequenceEqual(intent.Commands[i])&&child.InputDigest==DigestText("")&&child.Pid.HasValue&&child.ExitCode==0&&child.InputClosed&&child.OutputDrained,"new continuation custody mismatch");
   // The owner witnessed the active launch; reconstruct exactly that payload,
   // retaining both the active digest and final launch bytes in the new result.
   child.State="active";child.ExitCode=null;child.InputClosed=false;child.OutputDrained=false;
   LoadJoin(child,JsonSerializer.Serialize(child));
   launches.Add(new {key=NewLaunchKey(intent.Id,i),invocation=child.Invocation,launchDigest=DigestText(raw),witnessDigest=DigestText(System.IO.File.ReadAllText(StoragePath("physical-join/v1/"+child.Invocation)))});
  }
  return JsonSerializer.Serialize(new {version="1",id=intent.Id,requestDigest=intent.RequestDigest,pendingDigest=DigestText(pending),identity=intent.Identity,invocation=intent.Invocation,dispositionDigest=intent.DispositionDigest,planDigest=intent.PlanDigest,launchDigest=intent.LaunchDigest,intentDigest=intent.IntentDigest,registryDigest=intent.RegistryDigest,repository=GitRoot,repositoryId=intent.RepositoryId,state="applied",stateDigest=DigestText(JsonSerializer.Serialize(current)),launches,result="ok:continued-owned-tail:"+plan.Commit,originalOutcome="not-inferred"});
 }
 string ContinueTail(string request){
  using var document=JsonDocument.Parse(request);var r=document.RootElement;AdministrativeAuthority(r,"continuationAuthorization","continue-owned-tail");
  Check(AuthorityValue(r,"authorization")!=ScenarioAuthority("dispositionAuthorization",true)&&Setting("gitOwner")!="","separate continuation authority and owner required");
  dispositionInspection=true;
  try{
   string file=StoragePath(ContinuationKey),id=Required(r,"id");
   if(System.IO.File.Exists(file)){
    string pending=System.IO.File.ReadAllText(file);var retained=JsonSerializer.Deserialize<ContinuationIntent>(pending)!;
    Check(retained.Id==id&&retained.Request==request&&retained.RequestDigest==DigestText(request),"conflicting continuation decision");OriginalLineage(retained);
    string resultFile=StoragePath(ContinuationResultKey(id));Check(System.IO.File.Exists(resultFile),"pending continuation outcome unknown; no automatic retry");
    string result=CompletionRecord(retained,pending);Check(System.IO.File.ReadAllText(resultFile)==result,"conflicting continuation result");using var resultJson=JsonDocument.Parse(result);return Required(resultJson.RootElement,"result");
   }
   string retentionRaw=Required(r,"retentionRequest");using var qdoc=JsonDocument.Parse(retentionRaw);var q=qdoc.RootElement;
   Check(!System.IO.File.Exists(StoragePath(ContinuationResultKey(id)))&&!System.IO.File.Exists(StoragePath(NewLaunchKey(id,0)))&&!System.IO.File.Exists(StoragePath(NewLaunchKey(id,1)))&&!System.IO.File.Exists(StoragePath(AdmissionKey))&&!System.IO.File.Exists(StoragePath(ReleaseKey)),"orphan or conflicting continuation records");
   string disposition=System.IO.File.ReadAllText(StoragePath("physical-disposition/v1/"+Required(q,"id")));
   Check(id!=Required(q,"id")&&Required(r,"dispositionDigest")==DigestText(disposition),"separate decision/prior disposition binding");
   Check(RetainDisposition(retentionRaw)=="ok:retained-unresolved:"+Required(r,"expectedState"),"prior retention no longer matches");
   dispositionInspection=true;
   using var prior=JsonDocument.Parse(disposition);var p=prior.RootElement;
   string identity=Required(q,"identity");var plan=LoadPlan(identity);var current=PhysicalState();string state=OwnedTail(plan,current);
   Check((state is "object-only" or "ref-only")&&state==Required(r,"expectedState")&&DigestText(JsonSerializer.Serialize(current))==Required(r,"observedStateDigest")&&Required(p,"stateDigest")==Required(r,"observedStateDigest"),"explicit observed continuation state mismatch");
   string registry=System.IO.File.ReadAllText(StoragePath("registry/v1"));using var registryJson=JsonDocument.Parse(registry);var admission=registryJson.RootElement;
   Check(Required(admission,"phase")=="pending"&&Required(admission,"token")==identity&&Required(admission,"intent")==plan.Intent&&Required(admission,"outcome")==""&&Required(admission,"outcomeSource")=="","bounded unresolved original admission required");
   using var journalJson=JsonDocument.Parse(System.IO.File.ReadAllText(StoragePath(Required(admission,"id"))));var journal=journalJson.RootElement;
   Check(Required(journal,"version")=="1"&&Required(journal,"id")==Required(admission,"id")&&Required(journal,"fingerprint")==Required(admission,"fingerprint")&&Required(journal,"intent")==plan.Intent&&Required(journal,"effectIdentity")==identity&&Required(journal,"state")=="pending"&&Required(journal,"outcomeSource")==""&&Required(journal,"outcome")==""&&!System.IO.File.Exists(StoragePath("outcome/"+identity))&&!System.IO.File.Exists(StoragePath("outcome-receipt/"+identity)),"original pending outcome/correlation contradiction");
   var intent=new ContinuationIntent{Id=id,Request=request,RequestDigest=DigestText(request),Identity=identity,Invocation=Required(q,"invocation"),Repository=GitRoot,RepositoryId=Setting("fixtureId"),PlanDigest=Required(q,"planDigest"),LaunchDigest=Required(q,"launchDigest"),DispositionDigest=DigestText(disposition),IntentDigest=DigestText(plan.Intent),State=state,StateDigest=DigestText(JsonSerializer.Serialize(current)),RegistryDigest=DigestText(registry),Originals=OriginalInventory(),Commands=state=="object-only"?new[]{new[]{"update-ref","refs/heads/main",plan.Commit,plan.Before.Head},new[]{"read-tree","--reset","-u",plan.Commit}}:new[]{new[]{"read-tree","--reset","-u",plan.Commit}}};
   OriginalLineage(intent);Checkpoint("before-continuation-intent");string pendingRaw=JsonSerializer.Serialize(intent);AtomicWrite(ContinuationKey,pendingRaw);Checkpoint("after-continuation-intent");
   effectIdentity.Value=identity;
   for(int i=0;i<intent.Commands.Length;i++){
    OriginalLineage(intent);Check(OwnedTail(plan,PhysicalState())==(state=="object-only"&&i==0?"object-only":"ref-only"),"continuation mutation pre-state changed");
    Checkpoint("before-continuation-mutation-"+i);GitGuard();OwnedGit(intent.Commands[i],null,true,NewLaunchKey(id,i));Checkpoint("after-continuation-mutation-"+i);
   }
   string actual=CompletionRecord(intent,pendingRaw);Checkpoint("before-continuation-result");AtomicWrite(ContinuationResultKey(id),actual);Checkpoint("after-continuation-result");using var actualJson=JsonDocument.Parse(actual);return Required(actualJson.RootElement,"result");
  }finally{dispositionInspection=false;}
 }
 string ValidCompletion(string id){
  string pending=System.IO.File.ReadAllText(StoragePath(ContinuationKey));var intent=JsonSerializer.Deserialize<ContinuationIntent>(pending)!;Check(intent.Id==id,"release continuation identity mismatch");
  string actual=System.IO.File.ReadAllText(StoragePath(ContinuationResultKey(id)));Check(actual==CompletionRecord(intent,pending),"release requires actual new continuation outcome");return actual;
 }
 void ReleaseAuthorityLineage(JsonElement release,ContinuationIntent intent){
  using var accepted=JsonDocument.Parse(intent.Request);var continuation=accepted.RootElement;
  using var prior=JsonDocument.Parse(Required(continuation,"retentionRequest"));var retention=prior.RootElement;
  string authority=AuthorityValue(release,"authorization"),continued=AuthorityValue(continuation,"authorization"),retained=AuthorityValue(retention,"authorization");
  // Completion validation authenticates these exact accepted request bytes.
  // Optional current configuration can confirm them, never relabel their authority.
  Check(continued!=retained&&authority!=continued&&authority!=retained,"release authority must differ from accepted continuation and retention");
  string currentContinuation=ScenarioAuthority("continuationAuthorization",true),currentRetention=ScenarioAuthority("dispositionAuthorization",true);
  Check((currentContinuation==""||currentContinuation==continued)&&(currentRetention==""||currentRetention==retained),"current authority configuration contradicts accepted lineage");
 }
 string ReleaseObservation(string request){
  using var document=JsonDocument.Parse(request);var r=document.RootElement;AdministrativeAuthority(r,"releaseAuthorization","release-observation-admission");
  Check(Required(r,"id")!=Required(r,"continuationId"),"separate release decision required");
  dispositionInspection=true;
  try{
   string completion=ValidCompletion(Required(r,"continuationId"));using var cdoc=JsonDocument.Parse(completion);var c=cdoc.RootElement;
   var intent=JsonSerializer.Deserialize<ContinuationIntent>(System.IO.File.ReadAllText(StoragePath(ContinuationKey)))!;ReleaseAuthorityLineage(r,intent);using var originalRequest=JsonDocument.Parse(intent.Request);using var retentionRequest=JsonDocument.Parse(Required(originalRequest.RootElement,"retentionRequest"));Check(Required(r,"id")!=Required(retentionRequest.RootElement,"id"),"release identity must differ from retention");
   Check(Required(r,"continuationDigest")==DigestText(completion)&&Required(r,"observedStateDigest")==Required(c,"stateDigest")&&Required(r,"registryDigest")==Required(c,"registryDigest"),"release decision/state binding");
   string admission=JsonSerializer.Serialize(new {version="1",id=Required(r,"id"),request,requestDigest=DigestText(request),continuationId=Required(r,"continuationId"),continuationDigest=DigestText(completion),stateDigest=Required(c,"stateDigest"),registryDigest=Required(c,"registryDigest"),scope="observation-only",originalOutcome="not-inferred"});
   string pendingFile=StoragePath(AdmissionKey);if(System.IO.File.Exists(pendingFile))Check(System.IO.File.ReadAllText(pendingFile)==admission,"conflicting release intent");
   else{Checkpoint("before-release-intent");AtomicWrite(AdmissionKey,admission);Checkpoint("after-release-intent");}
   string resultKey="physical-observation-result/v1/"+Required(r,"id"),result=JsonSerializer.Serialize(new {version="1",intentDigest=DigestText(admission),result="ok:released-observation-admission",originalOutcome="not-inferred"});
   if(System.IO.File.Exists(StoragePath(resultKey)))Check(System.IO.File.ReadAllText(StoragePath(resultKey))==result,"conflicting release result");
   else{Checkpoint("before-release-result");AtomicWrite(resultKey,result);Checkpoint("after-release-result");}
   string pointer=JsonSerializer.Serialize(new {version="1",id=Required(r,"id"),intentDigest=DigestText(admission),resultDigest=DigestText(result)});
   if(System.IO.File.Exists(StoragePath(ReleaseKey)))Check(System.IO.File.ReadAllText(StoragePath(ReleaseKey))==pointer,"conflicting release activation");
   else{Checkpoint("before-release-activation");AtomicWrite(ReleaseKey,pointer);Checkpoint("after-release-activation");}
   return "ok:released-observation-admission";
  }finally{dispositionInspection=false;}
 }
 string ObservationAdmission(string registry){
  Check(Setting("evidence")!="unavailable","release inspection unavailable");
  using var pointer=JsonDocument.Parse(System.IO.File.ReadAllText(StoragePath(ReleaseKey)));var p=pointer.RootElement;
  string admission=System.IO.File.ReadAllText(StoragePath(AdmissionKey));using var document=JsonDocument.Parse(admission);var r=document.RootElement;
  string result=System.IO.File.ReadAllText(StoragePath("physical-observation-result/v1/"+Required(r,"id")));
  Check(Required(p,"version")=="1"&&Required(r,"version")=="1"&&Required(p,"id")==Required(r,"id")&&Required(p,"intentDigest")==DigestText(admission)&&Required(p,"resultDigest")==DigestText(result)&&Required(r,"scope")=="observation-only"&&Required(r,"originalOutcome")=="not-inferred","release activation mismatch");
  Check(result==JsonSerializer.Serialize(new {version="1",intentDigest=DigestText(admission),result="ok:released-observation-admission",originalOutcome="not-inferred"}),"release actual result mismatch");
  string request=Required(r,"request");using var requestJson=JsonDocument.Parse(request);var q=requestJson.RootElement;
  AuthorityValue(q,"authorization");
  Check(Required(r,"requestDigest")==DigestText(request)&&Required(q,"id")==Required(r,"id")&&Required(q,"action")=="release-observation-admission"&&Required(q,"continuationId")==Required(r,"continuationId")&&Required(q,"continuationDigest")==Required(r,"continuationDigest")&&Required(q,"observedStateDigest")==Required(r,"stateDigest")&&Required(q,"registryDigest")==Required(r,"registryDigest"),"release request lineage mismatch");
  dispositionInspection=true;
  try{string completion=ValidCompletion(Required(r,"continuationId"));using var cdoc=JsonDocument.Parse(completion);var c=cdoc.RootElement;
   ReleaseAuthorityLineage(q,JsonSerializer.Deserialize<ContinuationIntent>(System.IO.File.ReadAllText(StoragePath(ContinuationKey)))!);
   Check(DigestText(completion)==Required(r,"continuationDigest")&&Required(c,"stateDigest")==Required(r,"stateDigest")&&DigestText(registry)==Required(r,"registryDigest")&&Required(c,"registryDigest")==Required(r,"registryDigest"),"released admission facts changed");
  }finally{dispositionInspection=false;}
  observationAdmission=true;Observe(new {operation="physical-observation-admission",id=Required(r,"id"),scope="observation-only",originalOutcome="not-inferred"});return registry;
 }
