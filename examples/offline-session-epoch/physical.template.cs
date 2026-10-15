 // Mechanical inspection and ownership for the separate epoch namespace.
 // Request acceptance, authority separation, publication and blocked IDs are
 // authored in the graph. This fixture never produces an old request outcome.
 bool epochMode;
 public NyStr FixtureRead(NyStr key){
  string name=key.ToString();
  if(name=="epochRequest")return Setting(name)==""?"":System.IO.File.ReadAllText(Setting(name));
  if(!scenario.TryGetProperty(name,out var value))return "";
  // Required opaque input strings retain their JSON type; null and non-string
  // facts cannot impersonate empty/omitted or an accepted string.
  return value.GetRawText();
 }
 string EpochLaunchKey(string[] args)=>"physical-epoch-child/v1/"+DigestText(effectIdentity.Value??"")+"/"+(args[0]=="commit-tree"?0:args[0]=="update-ref"?1:2);
 const string SupportedEpochOwner="@@EPOCH_OWNER_DIGEST@@";
 const long SupportedEpochOwnerLength=@@EPOCH_OWNER_LENGTH@@;
 string EpochOwnerPath(){
  Check(scenario.TryGetProperty("gitOwner",out var supplied)&&supplied.ValueKind==JsonValueKind.String&&!string.IsNullOrEmpty(supplied.GetString()),"epoch requires a nonempty JSON-string owner");
  string owner=System.IO.Path.GetFullPath(supplied.GetString()!);var file=new System.IO.FileInfo(owner);
  Check(file.Exists&&file.LinkTarget==null&&file.Length==SupportedEpochOwnerLength,"epoch owner file unavailable or unsupported");
  for(var directory=file.Directory;directory!=null;directory=directory.Parent)Check(directory.LinkTarget==null,"symlinked epoch owner directory");
  Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(owner))).ToLowerInvariant()==SupportedEpochOwner,"epoch owner digest is not pinned");
  return owner;
 }
 void EpochChildGuard(){
  string file=StoragePath("epoch/v1/intent");if(!System.IO.File.Exists(file))return;
  using var document=JsonDocument.Parse(System.IO.File.ReadAllText(file));var q=document.RootElement;
  string token="journal:"+Required(q,"integrationIdentity")+":"+DigestText(Required(q,"integrationRequest"));
  bool gap=false;
  for(int stage=0;stage<3;stage++){
   string key="physical-epoch-child/v1/"+DigestText(token)+"/"+stage;
   if(!System.IO.File.Exists(StoragePath(key))){gap=true;continue;}
   Check(!gap,"epoch child sequence");string raw=System.IO.File.ReadAllText(StoragePath(key));var child=JsonSerializer.Deserialize<GitChild>(raw)!;
   Check(child.Version=="2"&&child.OwnerDigest==SupportedEpochOwner&&child.State=="quiescent"&&child.Pid.HasValue&&child.ExitCode==0&&child.InputClosed&&child.OutputDrained&&child.Identity==token&&child.Repository==GitRoot&&child.RepositoryId==Setting("fixtureId"),"epoch launch remains unresolved");
   var plan=JsonSerializer.Deserialize<GitPlan>(System.IO.File.ReadAllText(StoragePath(PlanKey(token))))!;
   string[] expected=stage==0?new[]{"commit-tree",plan.Tree,"-p",plan.Before.Head}:stage==1?new[]{"update-ref","refs/heads/main",plan.Commit,plan.Before.Head}:new[]{"read-tree","--reset","-u",plan.Commit};
   Check(child.Args.SequenceEqual(expected)&&child.InputDigest==DigestText(stage==0?token+"\n":""),"epoch child plan correlation");
   var active=JsonSerializer.Deserialize<GitChild>(raw)!;active.State="active";active.ExitCode=null;active.InputClosed=false;active.OutputDrained=false;
   var witness=LoadJoin(active,JsonSerializer.Serialize(active));
   Check(witness.ExitCode==0&&witness.ExitSignal==""&&witness.OutputDigest==DigestText(stage==0?plan.Commit+"\n":"")&&witness.ErrorDigest==DigestText(""),"epoch terminal custody mismatch");
  }
 }
 public NyStr PhysicalFacts(){
  try{return EpochFacts();}catch(Exception error){Observe(new {operation="epoch-inspection-rejected",reason=error.Message});return "{unavailable}";}
 }
 string EpochFacts(){
  Check(epochMode&&Setting("evidence")!="unavailable"&&Setting("physicalControl")==""&&Setting("worktreeModeControl")=="","enabled epoch inspection required");
  EpochOwnerPath();
  string receiptRaw=System.IO.File.ReadAllText(StoragePath(InterruptedDispositionKey));using var proof=JsonDocument.Parse(receiptRaw);var r=proof.RootElement;
  string pending=System.IO.File.ReadAllText(StoragePath(ContinuationKey));var continuation=JsonSerializer.Deserialize<ContinuationIntent>(pending)!;
  using var continuationRequest=JsonDocument.Parse(continuation.Request);var c=continuationRequest.RootElement;
  using var retentionRequest=JsonDocument.Parse(Required(c,"retentionRequest"));var retained=retentionRequest.RootElement;
  using var interruptionRequest=JsonDocument.Parse(Required(r,"request"));var interrupted=interruptionRequest.RootElement;
  Check(Required(r,"version")=="1"&&Required(r,"requestDigest")==DigestText(Required(r,"request"))&&Required(r,"pendingDigest")==DigestText(pending)&&Required(r,"continuationId")==continuation.Id&&Required(r,"identity")==continuation.Identity&&Required(r,"state")=="applied"&&Required(r,"admission")=="closed"&&Required(r,"originalOutcome")=="not-inferred"&&Required(r,"continuationOutcome")=="not-inferred","applied predecessor receipt correlation");
  Check(Required(interrupted,"continuationRequest")==continuation.Request&&Required(interrupted,"continuationIntentDigest")==DigestText(pending)&&Required(interrupted,"expectedState")=="applied"&&Required(interrupted,"observedStateDigest")==Required(r,"stateDigest"),"interruption accepted bytes correlation");
  foreach(var entry in r.GetProperty("evidence").EnumerateObject()){
   Check(System.Text.RegularExpressions.Regex.IsMatch(entry.Name,@"\A[0-9a-f]{64}\.json\z"),"frozen storage filename");
   Check(DigestText(System.IO.File.ReadAllText(System.IO.Path.Combine(StorageRoot,entry.Name)))==entry.Value.GetString(),"frozen predecessor evidence changed");
  }
  Check(DigestText(r.GetProperty("evidence").GetRawText())==Required(r,"evidenceDigest")&&Required(interrupted,"evidenceDigest")==Required(r,"evidenceDigest"),"frozen inventory digest");
  // All predecessor slots must retain their exact terminal ownership. Custody
  // precedes Git inspection. Never change an active original/continuation slot.
  var custody=InterruptedCustody(continuation,out var last);Check(DigestText(JsonSerializer.Serialize(custody))==Required(r,"custodyDigest")&&last!=null,"predecessor custody changed");
  string originalRaw=System.IO.File.ReadAllText(StoragePath("physical-child/v1"));var original=JsonSerializer.Deserialize<GitChild>(originalRaw)!;
  Check(original.Version=="2"&&original.OwnerDigest==SupportedContinuationOwner,"original controlled ownership required");
  if(original.State=="active")LoadJoin(original,originalRaw);
  else{Check(original.State=="quiescent"&&original.ExitCode==0&&original.InputClosed&&original.OutputDrained,"original terminal custody");var active=JsonSerializer.Deserialize<GitChild>(originalRaw)!;active.State="active";active.ExitCode=null;active.InputClosed=false;active.OutputDrained=false;var witness=LoadJoin(active,JsonSerializer.Serialize(active));Check(witness.ExitCode==0&&witness.ExitSignal=="","original actual join");}
  string rawRegistry=System.IO.File.ReadAllText(StoragePath("registry/v1"));using var registryDoc=JsonDocument.Parse(rawRegistry);var registry=registryDoc.RootElement;
  Check(Required(registry,"phase")=="pending"&&Required(registry,"token")==continuation.Identity&&Required(registry,"intent")!=""&&Required(registry,"outcome")==""&&Required(registry,"outcomeSource")==""&&DigestText(rawRegistry)==continuation.RegistryDigest,"original pending registry remains exact");
  Check(!System.IO.File.Exists(StoragePath(ContinuationResultKey(continuation.Id)))&&!System.IO.File.Exists(StoragePath("outcome/"+continuation.Identity))&&!System.IO.File.Exists(StoragePath("outcome-receipt/"+continuation.Identity))&&!System.IO.File.Exists(StoragePath(AdmissionKey))&&!System.IO.File.Exists(StoragePath(ReleaseKey)),"unsupported actual outcome/release ancestor");
  string retainedAuthority=AuthorityValue(retained,"authorization"),continuedAuthority=AuthorityValue(c,"authorization"),interruptedAuthority=AuthorityValue(interrupted,"authorization");
  foreach(var item in new[]{("dispositionAuthorization",retainedAuthority),("continuationAuthorization",continuedAuthority),("interruptedContinuationAuthorization",interruptedAuthority)}){string supplied=ScenarioAuthority(item.Item1,true);Check(supplied==""||supplied==item.Item2,"current authority contradicts accepted predecessor");}
  string request=Setting("epochRequest")!=""?System.IO.File.ReadAllText(Setting("epochRequest")):System.IO.File.ReadAllText(StoragePath("epoch/v1/intent"));using var proposalDoc=JsonDocument.Parse(request);var q=proposalDoc.RootElement;
  string memberIdentity=Required(q,"memberIdentity"),integrationIdentity=Required(q,"integrationIdentity"),member=Required(registry,"next"),token="journal:"+integrationIdentity+":"+DigestText(Required(q,"integrationRequest"));
  Check(System.Text.RegularExpressions.Regex.IsMatch(memberIdentity,@"\A[0-9a-f]{64}\z")&&System.Text.RegularExpressions.Regex.IsMatch(integrationIdentity,@"\A[0-9a-f]{64}\z"),"bounded proposed storage identities");
   var allowed=r.GetProperty("evidence").EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal);allowed.Add(System.IO.Path.GetFileName(StoragePath(InterruptedDispositionKey)));
  string[] admin={"epoch/v1/intent","epoch/v1/blocked","epoch/v1/result","epoch/v1/activation"};
  string[] application={"epoch-registry/v1","epoch-journal/v1/"+integrationIdentity,"registry-member/v1/"+member,"registry-admission/v1/"+memberIdentity,"outcome/"+token,"outcome-receipt/"+token};
  foreach(string key in admin.Concat(application).Concat(new[]{PlanKey(token),"physical-receipt/"+token+"/commit","physical-receipt/"+token+"/head","physical-receipt/"+token+"/save"}))allowed.Add(System.IO.Path.GetFileName(StoragePath(key)));
  foreach(string key in application){string lineage="epoch-lineage/v1/"+DigestText(key);allowed.Add(System.IO.Path.GetFileName(StoragePath(lineage)));
   if(System.IO.File.Exists(StoragePath("epoch/v1/activation"))&&System.IO.File.Exists(StoragePath(key)))Check(System.IO.File.ReadAllText(StoragePath(lineage))==System.IO.File.ReadAllText(StoragePath("epoch/v1/activation")),"application lineage differs from activation");
  }
  for(int stage=0;stage<3;stage++){string key="physical-epoch-child/v1/"+DigestText(token)+"/"+stage;allowed.Add(System.IO.Path.GetFileName(StoragePath(key)));if(System.IO.File.Exists(StoragePath(key))){var child=JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(StoragePath(key)))!;allowed.Add(System.IO.Path.GetFileName(StoragePath("physical-join/v1/"+child.Invocation)));}}
  Check(System.IO.Directory.GetFiles(StorageRoot).All(x=>allowed.Contains(System.IO.Path.GetFileName(x))),"unowned epoch storage appeared");
  if(!System.IO.File.Exists(StoragePath("epoch/v1/intent"))){
   Check(!System.IO.File.Exists(StoragePath(PlanKey(token))),"orphan epoch plan lacks accepted intent");
   for(int stage=0;stage<3;stage++)Check(!System.IO.File.Exists(StoragePath("physical-epoch-child/v1/"+DigestText(token)+"/"+stage)),"orphan epoch launch lacks accepted intent");
  }
  // Before epoch publication, reconstruct the predecessor receipt with its
  // original unchanged predicates. Later effects authenticate its frozen facts
  // separately; an old physical digest is never required to describe a new HEAD.
  if(!System.IO.File.Exists(StoragePath("epoch/v1/intent"))){
   // Reconstruct the old receipt under its exact accepted authority and storage
   // contract. Optional present current labels were already checked above;
   // omitted labels neither erase accepted bytes nor grant new authority.
   Check(Required(interrupted,"storageContract")==InterruptedStorageContract,"accepted predecessor storage contract");
   var acceptedSettings=scenario.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.Clone(),StringComparer.Ordinal);
   acceptedSettings["interruptedContinuationAuthorization"]=JsonSerializer.SerializeToElement(interruptedAuthority);
   acceptedSettings["interruptedContinuationStorageContract"]=JsonSerializer.SerializeToElement(InterruptedStorageContract);
   using var acceptedScenario=JsonDocument.Parse(JsonSerializer.Serialize(acceptedSettings));
   var replay=new Effects(acceptedScenario.RootElement,"");replay.epochMode=false;
   Check(replay.RetainInterruptedContinuation(Required(r,"request"))==Required(r,"result"),"predecessor receipt reconstruction");
  }
  var physical=PhysicalState();var priorPlan=LoadPlan(continuation.Identity);string selected=Required(q,"selectedCommit");
  Check(priorPlan.Before.Objects.Split('\n').Contains(selected+" commit"),"selected candidate lacks frozen owned object provenance");
  Check(Git(new[]{"rev-parse",selected+"^{tree}"})!="","selected candidate tree unavailable");
  string state="applied";
  if(System.IO.File.Exists(StoragePath(PlanKey(token)))){
   var currentPlan=LoadPlan(token);Check(!currentPlan.NoMutation&&currentPlan.Selected==selected&&DigestText(JsonSerializer.Serialize(currentPlan.Before))==Required(r,"stateDigest"),"new intent not based on epoch baseline/selection");
   string classified=Corroborated(currentPlan,physical);
   state=classified=="applied"||classified=="absent"?"applied":"unknown";
  }else Check(DigestText(JsonSerializer.Serialize(physical))==Required(r,"stateDigest"),"current state differs from frozen applied baseline");
  return JsonSerializer.Serialize(new {state,newPlan=System.IO.File.Exists(StoragePath(PlanKey(token)))?"true":"false",predecessorDigest=DigestText(receiptRaw),inventoryDigest=Required(r,"evidenceDigest"),baselineStateDigest=Required(r,"stateDigest"),baselineHead=priorPlan.Commit,repository=GitRoot,repositoryId=Setting("fixtureId"),retentionId=Required(retained,"id"),continuationId=continuation.Id,interruptionId=Required(interrupted,"id"),retentionAuthorization=retainedAuthority,continuationAuthorization=continuedAuthority,interruptionAuthorization=interruptedAuthority,originalIdentity=Required(registry,"id").Substring("journal/v1/".Length),originalFingerprint=Required(registry,"fingerprint"),originalIntent=Required(registry,"intent"),member=registry.GetProperty("entries")[0].GetProperty("id").GetString(),next=member,selectedCommit=selected,continuationRequest=continuation.Request,continuationRequestDigest=DigestText(continuation.Request),continuationIntentDigest=DigestText(pending),custodyDigest=Required(r,"custodyDigest")});
 }
